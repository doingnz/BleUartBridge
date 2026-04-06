using System.Globalization;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace BPplus.Serial;

/// <summary>
/// Concrete implementation of <see cref="IBpPlusSerialClient"/>.
/// One background task reads all incoming frames; a SemaphoreSlim(1,1) serialises
/// outgoing commands so only one is in-flight at a time.
/// </summary>
public sealed class BpPlusSerialClient : IBpPlusSerialClient
{
    // ── Serial port ───────────────────────────────────────────────────────────

    private SerialPort?                 _port;
    private CancellationTokenSource     _readLoopCts    = new();
    private Task                        _readLoopTask   = Task.CompletedTask;

    // ── Command serialisation ─────────────────────────────────────────────────

    private readonly SemaphoreSlim _commandLock = new(1, 1);

    // Pending response routing — guarded by _pendingSync
    private readonly object                              _pendingSync      = new();
    private TaskCompletionSource<BpPlusMessage>?         _pendingTcs;
    private Predicate<BpPlusMessage>?                    _pendingPredicate;

    // ── Pressure-test streaming channel ──────────────────────────────────────

    private Channel<int>? _pressureChannel;

    // ── Public properties / events ────────────────────────────────────────────

    public bool IsConnected => _port?.IsOpen == true;

    public event EventHandler<ModeChangedEventArgs>?       ModeChanged;
    public event EventHandler<PressureUpdatedEventArgs>?   PressureUpdated;
    public event EventHandler<UnsolicitedMessageEventArgs>? UnsolicitedMessage;
    public event EventHandler<RawDataReceivedEventArgs>?   RawDataReceived;

    // =========================================================================
    // Connection
    // =========================================================================

    public async Task ConnectAsync(string portName, int baudRate = 115200,
                                   bool hardwareFlowControl = true,
                                   CancellationToken ct = default)
    {
        if (_port?.IsOpen == true)
            await DisconnectAsync(ct);

        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            NewLine      = "\n",        // ReadLine() terminates on LF; we write "\r\n" explicitly
            ReadTimeout  = 500,         // ms — allows the read loop to check cancellation
            WriteTimeout = 2000,
            Encoding     = Encoding.ASCII,
            Handshake    = hardwareFlowControl
                               ? Handshake.RequestToSend
                               : Handshake.None,
        };

        _port.Open();

        _readLoopCts  = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoop(_readLoopCts.Token));
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _readLoopCts.Cancel();
        try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        lock (_pendingSync)
        {
            _pendingTcs?.TrySetException(new IOException("Serial port disconnected."));
            _pendingTcs       = null;
            _pendingPredicate = null;
        }

        _pressureChannel?.Writer.TryComplete();
        _pressureChannel = null;

        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;
    }

    // =========================================================================
    // Device info commands
    // =========================================================================

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var msg = await SendAndWaitAsync("?", m => m is VersionMessage, ct);
        return msg is VersionMessage v ? v.Version : msg.RawLine;
    }

    public async Task<FeatureInfo> GetFeaturesAsync(CancellationToken ct = default)
    {
        var msg = await SendAndWaitAsync("f", m => m is FeatureMessage, ct);
        return msg is FeatureMessage f ? BpPlusParser.ParseFeatureXml(f.FeatureXml)
            : new FeatureInfo("", "", null, null, null, null, null, null, null, null, null, null, msg.RawLine);
    }

    public async Task<DateTime> GetDeviceTimeAsync(CancellationToken ct = default)
    {
        var msg = await SendAndWaitAsync("y", m => m is DeviceTimeMessage, ct);
        return msg is DeviceTimeMessage t ? t.DeviceTime : DateTime.MinValue;
    }

    public async Task SetDeviceTimeAsync(DateTime time, CancellationToken ct = default)
    {
        // Command: y YYYYMMDDHHmmSS<space>  — trailing space is required by the spec
        string param = time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " ";
        await FireAndForgetAsync($"y {param}", ct);
    }

    public async Task<int> GetCurrentModeAsync(CancellationToken ct = default)
    {
        var msg = await SendAndWaitAsync("m", m => m is ModeMessage, ct);
        return msg is ModeMessage m ? m.Code : -1;
    }

    public async Task<bool> IsMeasurementInProgressAsync(CancellationToken ct = default)
    {
        var msg = await SendAndWaitAsync("!", m => m is MeasurementFailureMessage, ct);
        // F 22 = "No measurement in progress", F 17 = "Device is busy"
        return msg is MeasurementFailureMessage f && f.Code != 22;
    }

    public async Task SetDetailLevelAsync(int level, CancellationToken ct = default) =>
        await SendAndWaitAsync($"d {level}", m => m is AcknowledgeMessage, ct);

    // =========================================================================
    // Measurement commands
    // =========================================================================

    public async Task<MeasurementResult> StartMeasurementAsync(
        MeasurementRequest request,
        IProgress<int>?    cuffPressureProgress = null,
        CancellationToken  ct = default)
    {
        string cmd = BpPlusParser.BuildMeasurementCommand(request);
        return await RunMeasurementAsync(cmd, cuffPressureProgress, ct);
    }

    public Task CancelMeasurementAsync(CancellationToken ct = default)
    {
        // Bypass semaphore — a measurement may be holding it.
        if (_port?.IsOpen == true)
            WriteRaw("c");
        return Task.CompletedTask;
    }

    public async Task<MeasurementResult> RetrieveMeasurementAsync(int index,
                                                                   CancellationToken ct = default)
    {
        string cmd = index == 0 ? "r" : $"r {index}";
        return await RunMeasurementAsync(cmd, null, ct);
    }

    public async Task<IReadOnlyList<int>> GetStoredMeasurementIdsAsync(int startIndex = 0,
                                                                        CancellationToken ct = default)
    {
        string cmd = startIndex == 0 ? "i" : $"i {startIndex}";
        var msg = await SendAndWaitAsync(cmd, m => m is MeasurementIdListMessage, ct);
        return msg is MeasurementIdListMessage ids ? ids.Ids : Array.Empty<int>();
    }

    public async Task<MeasurementResult> StartSuprasystolicOnlyAsync(
        int sys, int map, int dia, int pr,
        CancellationToken ct = default)
    {
        string cmd = $"o {sys},{map},{dia},{pr}";
        return await RunMeasurementAsync(cmd, null, ct);
    }

    // =========================================================================
    // Streaming pressure-test mode
    // =========================================================================

    public async IAsyncEnumerable<int> StreamPressureTestAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        EventHandler<PressureUpdatedEventArgs> handler = (_, e) =>
            channel.Writer.TryWrite(e.PressureMmHg);

        PressureUpdated += handler;
        _pressureChannel = channel;

        try
        {
            // Send 't' — no response expected; device starts streaming pressure
            await FireAndForgetAsync("t", ct);

            await foreach (var p in channel.Reader.ReadAllAsync(ct))
                yield return p;
        }
        finally
        {
            PressureUpdated  -= handler;
            _pressureChannel  = null;
            channel.Writer.TryComplete();

            // Send cancel to return device to Ready state
            if (_port?.IsOpen == true)
            {
                try
                {
                    using var softCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await FireAndForgetAsync("c", softCts.Token);
                }
                catch { /* best effort */ }
            }
        }
    }

    // =========================================================================
    // Device control
    // =========================================================================

    public async Task SetBaudRateAsync(int baudRate, CancellationToken ct = default)
    {
        await FireAndForgetAsync($"b {baudRate}", ct);
        // Device changes baud immediately with no response — close and let caller reconnect.
        await Task.Delay(100, ct);
        await DisconnectAsync(ct);
    }

    public async Task RebootAsync(CancellationToken ct = default)
    {
        await FireAndForgetAsync("q", ct);
        // Device reboots silently; it will emit M 00 on restart.
    }

    // =========================================================================
    // Escape hatch
    // =========================================================================

    public async Task<string> SendRawCommandAsync(string command, TimeSpan timeout,
                                                   CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var msg = await SendAndWaitAsync(command, _ => true, linked.Token);
        return msg.RawLine;
    }

    // =========================================================================
    // IAsyncDisposable
    // =========================================================================

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _commandLock.Dispose();
        _readLoopCts.Dispose();
    }

    // =========================================================================
    // Internal: command helpers
    // =========================================================================

    /// <summary>Acquires the semaphore, writes command + CRLF, releases immediately (no response).</summary>
    private async Task FireAndForgetAsync(string command, CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);
        try { WriteRaw(command); }
        finally { _commandLock.Release(); }
    }

    /// <summary>
    /// Acquires the semaphore, writes the command, then waits for a response message
    /// that satisfies <paramref name="predicate"/> before releasing the semaphore.
    /// </summary>
    private async Task<BpPlusMessage> SendAndWaitAsync(string command,
                                                        Predicate<BpPlusMessage> predicate,
                                                        CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);
        try { return await SendAndWaitInternalAsync(command, predicate, ct); }
        finally { _commandLock.Release(); }
    }

    /// <summary>
    /// Writes the command and waits for a matching response. Caller must already hold
    /// <see cref="_commandLock"/> — use <see cref="SendAndWaitAsync"/> for the public path.
    /// </summary>
    private async Task<BpPlusMessage> SendAndWaitInternalAsync(string command,
                                                                Predicate<BpPlusMessage> predicate,
                                                                CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BpPlusMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingSync)
            {
                _pendingTcs       = tcs;
                _pendingPredicate = predicate;
            }
            WriteRaw(command);
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            lock (_pendingSync)
            {
                if (_pendingTcs == tcs)
                {
                    _pendingTcs       = null;
                    _pendingPredicate = null;
                }
            }
        }
    }

    /// <summary>
    /// Full measurement flow: sets detail level 4, sends <paramref name="command"/>,
    /// waits for S or F while streaming pressure updates, handles soft cancel.
    /// Semaphore is held for the duration to prevent interleaved commands.
    /// </summary>
    private async Task<MeasurementResult> RunMeasurementAsync(string command,
                                                               IProgress<int>? progress,
                                                               CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);

        var tcs = new TaskCompletionSource<BpPlusMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<PressureUpdatedEventArgs>? pressureHandler = null;
        if (progress != null)
        {
            pressureHandler = (_, e) => progress.Report(e.PressureMmHg);
            PressureUpdated += pressureHandler;
        }

        try
        {
            // d 4 immediately before the measurement command; wait for its ack so the
            // echo 'D 4' is consumed here and never reaches the unsolicited handler.
            // Wrapped in a short timeout + catch so older firmware that sends no ack
            // does not stall the measurement.
            using var d4Cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SendAndWaitInternalAsync("d 4", m => m is AcknowledgeMessage, d4Cts.Token); }
            catch { /* no ack from older firmware — proceed anyway */ }

            lock (_pendingSync)
            {
                _pendingTcs       = tcs;
                _pendingPredicate = m => m is MeasurementSuccessMessage or MeasurementFailureMessage;
            }

            WriteRaw(command);

            BpPlusMessage result;
            try
            {
                result = await tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Soft cancel: send 'c', wait up to 5 s for F nn
                WriteRaw("c");
                using var softCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try   { result = await tcs.Task.WaitAsync(softCts.Token); }
                catch { return MeasurementResult.Cancelled; }
            }

            return result switch
            {
                MeasurementSuccessMessage s => MeasurementResult.FromSuccess(s),
                MeasurementFailureMessage f => MeasurementResult.FromFailure(f),
                _                          => MeasurementResult.Cancelled,
            };
        }
        finally
        {
            if (pressureHandler != null)
                PressureUpdated -= pressureHandler;

            lock (_pendingSync)
            {
                if (_pendingTcs == tcs)
                {
                    _pendingTcs       = null;
                    _pendingPredicate = null;
                }
            }
            _commandLock.Release();
        }
    }

    private void WriteRaw(string command)
    {
        // Spec frame: <cmd>\r\n
        _port!.Write(command + "\r\n");
    }

    // =========================================================================
    // Internal: read loop
    // =========================================================================

    private void ReadLoop(CancellationToken ct)
    {
        string? pendingIdsHeader = null;

        while (!ct.IsCancellationRequested && _port?.IsOpen == true)
        {
            string line;
            try
            {
                line = _port.ReadLine().TrimEnd('\r');
            }
            catch (TimeoutException)  { continue; }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException)  { break; } // port closed
            catch (IOException ex)
            {
                HandlePortError(ex);
                break;
            }

            if (string.IsNullOrEmpty(line)) continue;

            // Emit raw bytes for every received line (reconstruct the \r\n the device sent)
            RawDataReceived?.Invoke(this,
                new RawDataReceivedEventArgs(Encoding.ASCII.GetBytes(line + "\r\n")));

            // ── Two-line IDs response ─────────────────────────────────────
            if (pendingIdsHeader != null)
            {
                RouteMessage(BpPlusParser.ParseIdsContent(pendingIdsHeader, line));
                pendingIdsHeader = null;
                continue;
            }

            // ── Multi-line XML response ───────────────────────────────────
            if (line.StartsWith("|_XML_Size", StringComparison.Ordinal))
            {
                var xmlMsg = ReadXmlMessage(line, ct);
                if (xmlMsg != null) RouteMessage(xmlMsg);
                continue;
            }

            // ── IDs header (second line follows) ─────────────────────────
            if (line.StartsWith("IDs_H ", StringComparison.Ordinal))
            {
                pendingIdsHeader = line;
                continue;
            }

            RouteMessage(BpPlusParser.ParseLine(line));
        }
    }

    private MeasurementSuccessMessage? ReadXmlMessage(string header, CancellationToken ct)
    {
        var match = Regex.Match(header, @"\|_XML_Size(\d+)\s+(\d+)_\|");
        if (!match.Success) return null;

        int xmlSize     = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int expectedCrc = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        var xmlBytes = new byte[xmlSize];
        int received = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (received < xmlSize && !ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                int n = _port!.Read(xmlBytes, received, xmlSize - received);
                if (n > 0) received += n;
            }
            catch (TimeoutException) { /* keep trying */ }
            catch { break; }
        }

        if (received < xmlSize) return null;

        // Consume the trailing \r\n after the XML
        try { ReadExactBytes(2); } catch { }

        string xml         = Encoding.ASCII.GetString(xmlBytes, 0, received);
        byte   computed    = Crc8.Compute(xmlBytes.AsSpan(0, received));
        bool   crcValid    = computed == (byte)expectedCrc;

        // Emit the xml payload bytes + trailing \r\n as raw data
        RawDataReceived?.Invoke(this, new RawDataReceivedEventArgs(xmlBytes[..received]));
        RawDataReceived?.Invoke(this, new RawDataReceivedEventArgs(new byte[] { 0x0D, 0x0A }));

        return new MeasurementSuccessMessage(xml, crcValid, header);
    }

    private void ReadExactBytes(int count)
    {
        var   buf      = new byte[count];
        int   received = 0;
        var   deadline = DateTime.UtcNow.AddSeconds(2);

        while (received < count && DateTime.UtcNow < deadline)
        {
            try
            {
                int n = _port!.Read(buf, received, count - received);
                if (n > 0) received += n;
            }
            catch (TimeoutException) { /* keep trying */ }
        }
    }

    // =========================================================================
    // Internal: message routing
    // =========================================================================

    private void RouteMessage(BpPlusMessage msg)
    {
        switch (msg)
        {
            case ModeMessage m:
                ModeChanged?.Invoke(this, new ModeChangedEventArgs(m.Code));
                // Also complete a pending 'm' command if one is waiting
                TryCompletePending(msg);
                break;

            case PressureMessage p:
                PressureUpdated?.Invoke(this, new PressureUpdatedEventArgs(p.CuffPressureMmHg));
                // Pressure updates never resolve a command TCS
                break;

            case ErrorMessage:
                // E messages are diagnostic only; the following F nn resolves the TCS
                UnsolicitedMessage?.Invoke(this, new UnsolicitedMessageEventArgs(msg));
                break;

            default:
                if (!TryCompletePending(msg))
                    UnsolicitedMessage?.Invoke(this, new UnsolicitedMessageEventArgs(msg));
                break;
        }
    }

    private bool TryCompletePending(BpPlusMessage msg)
    {
        TaskCompletionSource<BpPlusMessage>? tcs;
        Predicate<BpPlusMessage>?            predicate;

        lock (_pendingSync)
        {
            tcs       = _pendingTcs;
            predicate = _pendingPredicate;
        }

        if (tcs == null || predicate == null || !predicate(msg))
            return false;

        lock (_pendingSync)
        {
            if (_pendingTcs != tcs) return false; // race — already cleared
            _pendingTcs       = null;
            _pendingPredicate = null;
        }

        return tcs.TrySetResult(msg);
    }

    private void HandlePortError(Exception ex)
    {
        lock (_pendingSync)
        {
            _pendingTcs?.TrySetException(ex);
            _pendingTcs       = null;
            _pendingPredicate = null;
        }
        UnsolicitedMessage?.Invoke(this,
            new UnsolicitedMessageEventArgs(new UnknownMessage($"[Port error: {ex.Message}]")));
    }

}
