using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BPplus.Serial;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace BPplus.Ble;

/// <summary>
/// Implements <see cref="IBpPlusSerialClient"/> over BLE GATT using Plugin.BLE.
/// The protocol engine (PipeReader read loop, command serialization, message
/// routing) is identical to the WinRT implementation in BPplusSerialTester.
/// </summary>
public sealed class BpPlusBleClient : IBpPlusSerialClient
{
    // ── BLE objects (Plugin.BLE) ─────────────────────────────────────────────

    private IDevice?          _device;
    private IService?         _gattService;
    private ICharacteristic?  _txChar;
    private ICharacteristic?  _rxChar;

    // ── Byte accumulation pipe (notify → read loop) ──────────────────────────

    private Pipe _rxPipe = new();

    // ── Read loop ────────────────────────────────────────────────────────────

    private CancellationTokenSource _readLoopCts  = new();
    private Task                    _readLoopTask = Task.CompletedTask;

    // ── Command serialisation ────────────────────────────────────────────────

    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly object                              _pendingSync      = new();
    private TaskCompletionSource<BpPlusMessage>?         _pendingTcs;
    private Predicate<BpPlusMessage>?                    _pendingPredicate;

    // ── Pressure-test streaming ──────────────────────────────────────────────

    private Channel<int>? _pressureChannel;

    // ── MTU ──────────────────────────────────────────────────────────────────

    private int _mtuPayload = 20;

    // ── Public state ─────────────────────────────────────────────────────────

    public bool IsConnected { get; private set; }

    public event EventHandler<ModeChangedEventArgs>?       ModeChanged;
    public event EventHandler<PressureUpdatedEventArgs>?   PressureUpdated;
    public event EventHandler<UnsolicitedMessageEventArgs>? UnsolicitedMessage;
    public event EventHandler<RawDataReceivedEventArgs>?   RawDataReceived;

    /// <summary>
    /// Fires during XML payload reception with (received, total) byte counts.
    /// Used for UI progress indication.
    /// </summary>
    public event EventHandler<XmlProgressEventArgs>?       XmlProgress;

    // =========================================================================
    // BLE-specific connect (Plugin.BLE)
    // =========================================================================

    /// <summary>
    /// Connects to an already-discovered BLE device using Plugin.BLE.
    /// If <paramref name="profile"/> is null, the profile is auto-detected.
    /// </summary>
    public async Task ConnectToDeviceAsync(
        IDevice device,
        BleGattProfile? profile,
        CancellationToken ct = default)
    {
        if (IsConnected)
            await DisconnectAsync(ct);

        _rxPipe = new Pipe();

        var adapter = CrossBluetoothLE.Current.Adapter;
        await adapter.ConnectToDeviceAsync(device, cancellationToken: ct);
        _device = device;

        profile ??= await BleProfileDetector.DetectAsync(device, ct)
            ?? throw new InvalidOperationException(
                "No known BLE UART GATT profile found on this device.");

        await SetupGattAsync(profile, ct);

        IsConnected   = true;
        _readLoopCts  = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    /// <summary>
    /// Reconnects to a previously-known device by its Plugin.BLE device ID.
    /// </summary>
    public async Task ConnectToKnownDeviceAsync(
        Guid deviceId,
        BleGattProfile? profile,
        CancellationToken ct = default)
    {
        if (IsConnected)
            await DisconnectAsync(ct);

        _rxPipe = new Pipe();

        var adapter = CrossBluetoothLE.Current.Adapter;
        _device = await adapter.ConnectToKnownDeviceAsync(deviceId, cancellationToken: ct);

        profile ??= await BleProfileDetector.DetectAsync(_device, ct)
            ?? throw new InvalidOperationException(
                "No known BLE UART GATT profile found on this device.");

        await SetupGattAsync(profile, ct);

        IsConnected   = true;
        _readLoopCts  = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    private async Task SetupGattAsync(BleGattProfile profile, CancellationToken ct)
    {
        // ── MTU negotiation (critical for Android) ───────────────────────────
        // Android's default BLE MTU is 23 bytes (20 usable). Large XML payloads
        // require hundreds of notifications at this size, and Android's BLE stack
        // processes them slower than Windows. The adapter's internal buffer
        // overflows and data is silently lost.
        //
        // Requesting a 512-byte MTU reduces the notification count ~25x and
        // gives the adapter breathing room. The actual negotiated MTU may be
        // lower (depends on adapter + phone), but any increase helps.
        try
        {
            int mtu = 128+3;
            int negotiated = await _device!.RequestMtuAsync(mtu);
            Debug.WriteLine($"[BLE MTU] Requested {mtu}, negotiated {negotiated}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BLE MTU] Negotiation failed (will use default): {ex.Message}");
        }

        var services = await _device!.GetServicesAsync();
        _gattService = services.FirstOrDefault(s => s.Id == profile.ServiceUuid)
            ?? throw new IOException($"GATT service {profile.ServiceUuid} not found.");

        var chars = await _gattService.GetCharacteristicsAsync();

        _txChar = chars.FirstOrDefault(c => c.Id == profile.TxCharUuid)
            ?? throw new IOException($"TX characteristic {profile.TxCharUuid} not found.");

        _rxChar = chars.FirstOrDefault(c => c.Id == profile.RxCharUuid)
            ?? throw new IOException($"RX characteristic {profile.RxCharUuid} not found.");

        // Subscribe to notifications
        _rxChar.ValueUpdated += OnValueUpdated;
        await _rxChar.StartUpdatesAsync(ct);

        // Write chunk size — use profile max or negotiated MTU - 3 (ATT overhead)
        if (profile.MaxPayloadBytes > 0)
            _mtuPayload = profile.MaxPayloadBytes;
        else if (_txChar.Properties.HasFlag(CharacteristicPropertyType.WriteWithoutResponse))
            _mtuPayload = 128;
        else
            _mtuPayload = 20;

        Debug.WriteLine($"[BLE] Setup complete — TX write chunk={_mtuPayload} bytes");
    }

    // =========================================================================
    // IBpPlusSerialClient — ConnectAsync / DisconnectAsync
    // =========================================================================

    public Task ConnectAsync(string portName, int baudRate = 115200,
                             bool hardwareFlowControl = true,
                             CancellationToken ct = default)
        => throw new NotSupportedException("BLE transport uses ConnectToDeviceAsync.");

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _readLoopCts.Cancel();
        try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        lock (_pendingSync)
        {
            _pendingTcs?.TrySetException(new IOException("BLE device disconnected."));
            _pendingTcs       = null;
            _pendingPredicate = null;
        }

        _pressureChannel?.Writer.TryComplete();
        _pressureChannel = null;

        if (_rxChar != null)
        {
            _rxChar.ValueUpdated -= OnValueUpdated;
            try { await _rxChar.StopUpdatesAsync(); } catch { }
            _rxChar = null;
        }
        _txChar = null;

        _gattService?.Dispose();
        _gattService = null;

        if (_device != null)
        {
            try
            {
                var adapter = CrossBluetoothLE.Current.Adapter;
                await adapter.DisconnectDeviceAsync(_device);
            }
            catch { }
            _device = null;
        }

        await _rxPipe.Writer.CompleteAsync();
        IsConnected = false;
    }

    // =========================================================================
    // IBpPlusSerialClient — Device info commands
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
        return msg is MeasurementFailureMessage f && f.Code != 22;
    }

    public async Task SetDetailLevelAsync(int level, CancellationToken ct = default) =>
        await SendAndWaitAsync($"d {level}", m => m is AcknowledgeMessage, ct);

    // =========================================================================
    // IBpPlusSerialClient — Measurement commands
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
        if (IsConnected)
            _ = WriteRawAsync("c");
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
        int sys, int map, int dia, int pr, CancellationToken ct = default)
    {
        string cmd = $"o {sys},{map},{dia},{pr}";
        return await RunMeasurementAsync(cmd, null, ct);
    }

    // =========================================================================
    // IBpPlusSerialClient — Streaming pressure-test mode
    // =========================================================================

    public async IAsyncEnumerable<int> StreamPressureTestAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(200)
            { FullMode = BoundedChannelFullMode.DropOldest });

        EventHandler<PressureUpdatedEventArgs> handler = (_, e) =>
            channel.Writer.TryWrite(e.PressureMmHg);

        PressureUpdated += handler;
        _pressureChannel = channel;

        try
        {
            await FireAndForgetAsync("t", ct);
            await foreach (var p in channel.Reader.ReadAllAsync(ct))
                yield return p;
        }
        finally
        {
            PressureUpdated  -= handler;
            _pressureChannel  = null;
            channel.Writer.TryComplete();

            if (IsConnected)
            {
                try
                {
                    using var softCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await FireAndForgetAsync("c", softCts.Token);
                }
                catch { }
            }
        }
    }

    // =========================================================================
    // IBpPlusSerialClient — Device control
    // =========================================================================

    public async Task SetBaudRateAsync(int baudRate, CancellationToken ct = default)
    {
        await FireAndForgetAsync($"b {baudRate}", ct);
        await Task.Delay(100, ct);
        await DisconnectAsync(ct);
    }

    public async Task RebootAsync(CancellationToken ct = default) =>
        await FireAndForgetAsync("q", ct);

    // =========================================================================
    // IBpPlusSerialClient — Escape hatch
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

    private async Task FireAndForgetAsync(string command, CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);
        try { await WriteRawAsync(command); }
        finally { _commandLock.Release(); }
    }

    private async Task<BpPlusMessage> SendAndWaitAsync(string command,
                                                        Predicate<BpPlusMessage> predicate,
                                                        CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);
        try { return await SendAndWaitInternalAsync(command, predicate, ct); }
        finally { _commandLock.Release(); }
    }

    private async Task<BpPlusMessage> SendAndWaitInternalAsync(string command,
                                                                Predicate<BpPlusMessage> predicate,
                                                                CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BpPlusMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_pendingSync) { _pendingTcs = tcs; _pendingPredicate = predicate; }
            await WriteRawAsync(command);
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            lock (_pendingSync)
            {
                if (_pendingTcs == tcs) { _pendingTcs = null; _pendingPredicate = null; }
            }
        }
    }

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
            using var d4Cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SendAndWaitInternalAsync("d 4", m => m is AcknowledgeMessage, d4Cts.Token); }
            catch { }

            lock (_pendingSync)
            {
                _pendingTcs       = tcs;
                _pendingPredicate = m => m is MeasurementSuccessMessage or MeasurementFailureMessage;
            }

            await WriteRawAsync(command);

            BpPlusMessage result;
            try
            {
                result = await tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ = WriteRawAsync("c");
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
            if (pressureHandler != null) PressureUpdated -= pressureHandler;
            lock (_pendingSync)
            {
                if (_pendingTcs == tcs) { _pendingTcs = null; _pendingPredicate = null; }
            }
            _commandLock.Release();
        }
    }

    private async Task WriteRawAsync(string command)
    {
        if (_txChar == null) return;
        byte[] data = Encoding.ASCII.GetBytes(command + "\r\n");

        for (int i = 0; i < data.Length; i += _mtuPayload)
        {
            int size = Math.Min(_mtuPayload, data.Length - i);
            await _txChar.WriteAsync(data[i..(i + size)]);
        }
    }

    // =========================================================================
    // Internal: BLE notify handler → pipe
    // =========================================================================

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var bytes = e.Characteristic.Value;
        if (bytes == null || bytes.Length == 0) return;

        Debug.WriteLine($"[BLE RX] {bytes.Length} bytes: {Encoding.ASCII.GetString(bytes).Replace("\r", "\\r").Replace("\n", "\\n")}");

        RawDataReceived?.Invoke(this, new RawDataReceivedEventArgs(bytes));

        _rxPipe.Writer.Write(bytes);
        _ = _rxPipe.Writer.FlushAsync();
    }

    // =========================================================================
    // Internal: read loop (consumes pipe, routes messages)
    // =========================================================================

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var    reader            = _rxPipe.Reader;
        string? pendingIdsHeader = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult readResult = await reader.ReadAsync(ct);
                var        buffer     = readResult.Buffer;

                bool   needXml   = false;
                string xmlHeader = string.Empty;

                SequencePosition consumed = buffer.Start;

                try
                {
                    SequencePosition? lineEnd;
                    while ((lineEnd = buffer.PositionOf((byte)'\n')) != null)
                    {
                        var lineSlice = buffer.Slice(0, lineEnd.Value);
                        string line   = Encoding.ASCII.GetString(lineSlice).TrimEnd('\r');

                        consumed = buffer.GetPosition(1, lineEnd.Value);
                        buffer   = buffer.Slice(consumed);

                        if (string.IsNullOrEmpty(line)) continue;

                        Debug.WriteLine($"[BLE LINE] {line}");

                        if (pendingIdsHeader != null)
                        {
                            RouteMessage(BpPlusParser.ParseIdsContent(pendingIdsHeader, line));
                            pendingIdsHeader = null;
                            continue;
                        }

                        if (line.StartsWith("|_XML_Size", StringComparison.Ordinal))
                        {
                            Debug.WriteLine($"[BLE XML] Header detected: {line}");
                            needXml   = true;
                            xmlHeader = line;
                            break;
                        }

                        if (line.StartsWith("IDs_H ", StringComparison.Ordinal))
                        {
                            pendingIdsHeader = line;
                            continue;
                        }

                        RouteMessage(BpPlusParser.ParseLine(line));
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed, buffer.End);
                }

                if (needXml)
                {
                    var xmlMsg = await ReadXmlFromPipeAsync(reader, xmlHeader, ct);
                    if (xmlMsg != null) RouteMessage(xmlMsg);
                }

                if (readResult.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { HandleConnectionError(ex); }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private async Task<MeasurementSuccessMessage?> ReadXmlFromPipeAsync(
        PipeReader reader, string header, CancellationToken ct)
    {
        var match = Regex.Match(header, @"\|_XML_Size(\d+)\s+(\d+)_\|");
        if (!match.Success)
        {
            Debug.WriteLine($"[BLE XML] Header parse FAILED: {header}");
            return null;
        }

        int xmlSize     = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int expectedCrc = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        Debug.WriteLine($"[BLE XML] Expecting {xmlSize} bytes, CRC={expectedCrc}");
        XmlProgress?.Invoke(this, new XmlProgressEventArgs(0, xmlSize));

        byte[] xmlBytes = await ReadExactBytesWithProgressAsync(reader, xmlSize, ct);

        Debug.WriteLine($"[BLE XML] Received all {xmlSize} bytes. Consuming trailing CRLF…");
        try { await ReadExactBytesAsync(reader, 2, ct); } catch { }

        string xml      = Encoding.ASCII.GetString(xmlBytes);
        byte   computed = Crc8.Compute(xmlBytes);
        bool   crcValid = computed == (byte)expectedCrc;

        Debug.WriteLine($"[BLE XML] CRC check: expected={expectedCrc}, computed={computed}, valid={crcValid}");
        Debug.WriteLine($"[BLE XML] XML starts with: {xml[..Math.Min(120, xml.Length)]}");

        return new MeasurementSuccessMessage(xml, crcValid, header);
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from the pipe, firing
    /// <see cref="XmlProgress"/> events as chunks arrive.
    /// </summary>
    private async Task<byte[]> ReadExactBytesWithProgressAsync(
        PipeReader reader, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        var result   = new byte[count];
        int received = 0;
        int lastPct  = -1;

        while (received < count && !ct.IsCancellationRequested)
        {
            var readResult = await reader.ReadAsync(ct);
            var buffer     = readResult.Buffer;
            int toRead     = (int)Math.Min(buffer.Length, count - received);
            buffer.Slice(0, toRead).CopyTo(result.AsSpan(received));
            received += toRead;
            reader.AdvanceTo(buffer.GetPosition(toRead), buffer.End);

            // Progress reporting
            int pct = (int)((long)received * 100 / count);
            if (pct != lastPct)
            {
                lastPct = pct;
                Debug.WriteLine($"[BLE XML] {received}/{count} bytes ({pct}%)");
                XmlProgress?.Invoke(this, new XmlProgressEventArgs(received, count));
            }

            if (readResult.IsCompleted && received < count)
            {
                Debug.WriteLine($"[BLE XML] PIPE CLOSED at {received}/{count} bytes — DATA LOST");
                throw new IOException($"BLE pipe closed at {received}/{count} bytes while reading XML payload.");
            }
        }
        return result;
    }

    private static async Task<byte[]> ReadExactBytesAsync(
        PipeReader reader, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        var result   = new byte[count];
        int received = 0;

        while (received < count && !ct.IsCancellationRequested)
        {
            var readResult = await reader.ReadAsync(ct);
            var buffer     = readResult.Buffer;
            int toRead     = (int)Math.Min(buffer.Length, count - received);
            buffer.Slice(0, toRead).CopyTo(result.AsSpan(received));
            received += toRead;
            reader.AdvanceTo(buffer.GetPosition(toRead), buffer.End);
            if (readResult.IsCompleted && received < count)
                throw new IOException("BLE pipe closed while reading payload.");
        }
        return result;
    }

    // =========================================================================
    // Internal: message routing
    // =========================================================================

    private void RouteMessage(BpPlusMessage msg)
    {
        switch (msg)
        {
            case ModeMessage m:
                Debug.WriteLine($"[BLE MODE] {m.Code} — {new ModeChangedEventArgs(m.Code).Description}");
                ModeChanged?.Invoke(this, new ModeChangedEventArgs(m.Code));
                TryCompletePending(msg);
                break;
            case PressureMessage p:
                PressureUpdated?.Invoke(this, new PressureUpdatedEventArgs(p.CuffPressureMmHg));
                break;
            case MeasurementSuccessMessage s:
                Debug.WriteLine($"[BLE RESULT] Success — XML length={s.Xml?.Length ?? 0}, CRC valid={s.XmlCrcValid}");
                if (!TryCompletePending(msg))
                    UnsolicitedMessage?.Invoke(this, new UnsolicitedMessageEventArgs(msg));
                break;
            case MeasurementFailureMessage f:
                Debug.WriteLine($"[BLE RESULT] Failure — code={f.Code}, desc={f.Description}");
                if (!TryCompletePending(msg))
                    UnsolicitedMessage?.Invoke(this, new UnsolicitedMessageEventArgs(msg));
                break;
            case ErrorMessage:
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
        lock (_pendingSync) { tcs = _pendingTcs; predicate = _pendingPredicate; }
        if (tcs == null || predicate == null || !predicate(msg)) return false;
        lock (_pendingSync)
        {
            if (_pendingTcs != tcs) return false;
            _pendingTcs = null; _pendingPredicate = null;
        }
        return tcs.TrySetResult(msg);
    }

    private void HandleConnectionError(Exception ex)
    {
        lock (_pendingSync)
        {
            _pendingTcs?.TrySetException(ex);
            _pendingTcs = null; _pendingPredicate = null;
        }
        UnsolicitedMessage?.Invoke(this,
            new UnsolicitedMessageEventArgs(new UnknownMessage($"[BLE error: {ex.Message}]")));
        IsConnected = false;
    }
}

/// <summary>Progress event args for XML payload reception.</summary>
public sealed class XmlProgressEventArgs(int received, int total) : EventArgs
{
    public int Received { get; } = received;
    public int Total    { get; } = total;
    public int Percent  => Total > 0 ? (int)((long)Received * 100 / Total) : 0;
}
