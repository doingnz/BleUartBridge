namespace BPplus.Serial;

/// <summary>
/// Async serial interface to a Uscom BP+ device.
/// All public methods are thread-safe. Only one command may be in-flight at a time;
/// concurrent callers queue via an internal semaphore.
/// Events are raised on a background thread — consumers must marshal to the UI thread.
/// </summary>
public interface IBpPlusSerialClient : IAsyncDisposable
{
    // ── Connection ────────────────────────────────────────────────────────────

    /// <param name="hardwareFlowControl">
    /// When <see langword="true"/> (default) RTS/CTS hardware flow control is enabled
    /// (<see cref="System.IO.Ports.Handshake.RequestToSend"/>).
    /// Set to <see langword="false"/> for USB-to-serial adapters that do not wire RTS/CTS.
    /// </param>
    Task ConnectAsync(string portName, int baudRate = 115200,
                      bool hardwareFlowControl = true, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }

    // ── Unsolicited notifications ─────────────────────────────────────────────

    /// <summary>Fires whenever the device sends M nn (mode change, boot, menu navigation).</summary>
    event EventHandler<ModeChangedEventArgs> ModeChanged;

    /// <summary>
    /// Fires whenever the device sends a cuff pressure update (P nnn / p nnn).
    /// Raised during active measurements AND in pressure-test mode.
    /// </summary>
    event EventHandler<PressureUpdatedEventArgs> PressureUpdated;

    /// <summary>
    /// Fires for messages not consumed as a command response — E (error strings),
    /// unexpected F codes, and unknown frames.
    /// </summary>
    event EventHandler<UnsolicitedMessageEventArgs> UnsolicitedMessage;

    /// <summary>
    /// Fires with every chunk of raw bytes received from the serial port, before parsing.
    /// Raised on the background read thread — consumers must marshal to the UI thread.
    /// </summary>
    event EventHandler<RawDataReceivedEventArgs> RawDataReceived;

    // ── Device info commands ──────────────────────────────────────────────────

    Task<string>      GetVersionAsync              (CancellationToken ct = default);
    Task<FeatureInfo> GetFeaturesAsync             (CancellationToken ct = default);
    Task<DateTime>    GetDeviceTimeAsync           (CancellationToken ct = default);
    Task              SetDeviceTimeAsync           (DateTime time, CancellationToken ct = default);
    Task<int>         GetCurrentModeAsync          (CancellationToken ct = default);
    Task<bool>        IsMeasurementInProgressAsync (CancellationToken ct = default);

    // ── Measurement configuration ─────────────────────────────────────────────

    /// <summary>
    /// Sets the reporting detail level. Use level 4 to receive full XML results.
    /// Automatically called inside StartMeasurementAsync; available separately for
    /// explicit control.
    /// </summary>
    Task SetDetailLevelAsync(int level, CancellationToken ct = default);

    // ── Measurement commands ──────────────────────────────────────────────────

    /// <summary>
    /// Starts an adult-mode BP measurement. Automatically sets detail level 4 first.
    /// <para>
    /// Cancellation sends 'c' to the device and waits up to 5 s for the F 02 response
    /// before returning <see cref="MeasurementResult.Cancelled"/>.
    /// </para>
    /// </summary>
    /// <param name="cuffPressureProgress">
    /// Optional; receives cuff pressure (mmHg) updates while the measurement runs.
    /// </param>
    Task<MeasurementResult> StartMeasurementAsync(
        MeasurementRequest  request,
        IProgress<int>?     cuffPressureProgress = null,
        CancellationToken   ct = default);

    /// <summary>Sends 'c' immediately without waiting for a response.</summary>
    Task CancelMeasurementAsync(CancellationToken ct = default);

    Task<MeasurementResult>  RetrieveMeasurementAsync      (int index, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetStoredMeasurementIdsAsync  (int startIndex = 0, CancellationToken ct = default);

    /// <summary>Suprasystolic-only measurement using caller-supplied NIBP values.</summary>
    Task<MeasurementResult> StartSuprasystolicOnlyAsync(
        int sys, int map, int dia, int pr,
        CancellationToken ct = default);

    // ── Streaming modes ───────────────────────────────────────────────────────

    /// <summary>
    /// Enters pressure-test mode and streams live cuff pressure values (mmHg).
    /// Cancel the token to send 'c' and exit pressure-test mode.
    /// </summary>
    IAsyncEnumerable<int> StreamPressureTestAsync(CancellationToken ct = default);

    // ── Device control ────────────────────────────────────────────────────────

    /// <summary>
    /// Sets baud rate. The existing connection is closed immediately after sending the
    /// command — caller must reconnect at the new baud.
    /// </summary>
    Task SetBaudRateAsync(int baudRate, CancellationToken ct = default);

    /// <summary>Instructs the device to reboot. No response is sent by the device.</summary>
    Task RebootAsync(CancellationToken ct = default);

    // ── Escape hatch ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a raw ASCII command and returns the first response line received within
    /// <paramref name="timeout"/>. For exploratory / test use only.
    /// </summary>
    Task<string> SendRawCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default);
}
