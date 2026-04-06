namespace BPplus.Serial;

// ── Measurement request / result ──────────────────────────────────────────────

/// <summary>Parameters for an adult-mode BP measurement.</summary>
public sealed record MeasurementRequest(
    int         TargetPressureMmHg = 0,     // 0 = Auto
    string?     PatientId          = null,
    MeasureMode Mode               = MeasureMode.Auto);

/// <summary>Deflation / inflation mode passed as the third parameter of 's'.</summary>
public enum MeasureMode
{
    Auto,           // 0 or omitted
    Inflate,        // 'i'
    Deflate,        // 'd' — 6 mmHg/s
    SlowDeflate,    // '4' — 4 mmHg/s
}

/// <summary>Outcome of a measurement or retrieve command.</summary>
public sealed record MeasurementResult(
    bool    IsSuccess,
    int     FailureCode,
    string  FailureDescription,
    string? Xml,            // non-null when IsSuccess && detail level 4
    bool    XmlCrcValid)
{
    public bool IsCancelled => FailureCode == 2;

    public static MeasurementResult Cancelled =>
        new(false, 2, FailureDescriptions.Get(2), null, false);

    public static MeasurementResult FromFailure(MeasurementFailureMessage f) =>
        new(false, f.Code, f.Description, null, false);

    public static MeasurementResult FromSuccess(MeasurementSuccessMessage s) =>
        new(true, 0, string.Empty, s.Xml, s.XmlCrcValid);
}

// ── Device info ───────────────────────────────────────────────────────────────

/// <summary>Max/min operating range for one BP parameter (parsed from "max,min" strings in bpRange).</summary>
public sealed record BpRangeInfo(int Max, int Min);

/// <summary>Full set of device BP operating ranges from the bpRange element.</summary>
public sealed record BpRanges(
    BpRangeInfo? Sys,
    BpRangeInfo? Dia,
    BpRangeInfo? Map,
    BpRangeInfo? Hr);

/// <summary>Parsed content of the 'f' (features) response.</summary>
public sealed record FeatureInfo(
    string    Version,
    string    ProtocolXmlVersion,
    string?   FirmwareVersion,
    string?   SoftwareVersion,
    string?   HardwareId,
    string?   DeviceId,
    string?   NibpType,
    string?   NibpVersion,
    string?   NibpId,       // nibp_id element
    string?   PcbId,        // pcb_id element
    string?   ThemeId,      // theme_id element
    BpRanges? BpRanges,     // bpRange element
    string    RawXml);

// ── Event args ────────────────────────────────────────────────────────────────

public sealed class ModeChangedEventArgs(int code) : EventArgs
{
    public int Code { get; } = code;

    public DeviceMode Mode => Enum.IsDefined(typeof(DeviceMode), Code)
        ? (DeviceMode)Code
        : (DeviceMode)(-1);

    public string Description => Code switch
    {
        0  => "Boot/self-test",
        1  => "Offline",
        2  => "Ready",
        3  => "Measuring BP",
        4  => "Deflating cuff",
        5  => "Inflating to suprasystolic",
        6  => "Acquiring data",
        7  => "Processing data",
        8  => "Select storage",
        9  => "Store/recall",
        10 => "Extra info",
        11 => "Safety lock",
        12 => "Service menu",
        13 => "Service menu — manometer",
        14 => "Service menu — characterising Sys",
        15 => "Set target",
        16 => "Download app",
        17 => "Settings",
        18 => "Select language",
        19 => "Set date/time",
        20 => "Central pressure",
        21 => "Pressure test mode",
        22 => "Countdown AOBP",
        23 => "Select AOBP mode",
        _  => $"Mode {Code}",
    };
}

public sealed class PressureUpdatedEventArgs(int pressureMmHg) : EventArgs
{
    public int PressureMmHg { get; } = pressureMmHg;
}

public sealed class UnsolicitedMessageEventArgs(BpPlusMessage message) : EventArgs
{
    public BpPlusMessage Message { get; } = message;
}

public sealed class RawDataReceivedEventArgs(byte[] data) : EventArgs
{
    /// <summary>Raw bytes received from the serial port in this chunk.</summary>
    public byte[] Data { get; } = data;
}

// ── Failure code descriptions ─────────────────────────────────────────────────

internal static class FailureDescriptions
{
    private static readonly Dictionary<int, string> Map = new()
    {
        {  0, "Failed to start measurement" },
        {  1, "Timeout" },
        {  2, "Measurement was cancelled" },
        {  3, "Pneumatic error" },
        {  4, "Data processing error" },
        {  5, "Failed to process suprasystolic data" },
        {  6, "Error finding feature points" },
        {  7, "Failed to calculate central BP parameters" },
        {  8, "Failed to finish measurement" },
        {  9, "No measurement found" },
        { 10, "Measurement did not complete in permitted time" },
        { 11, "NIBP device error (and retry limit reached)" },
        { 12, "Measurement data invalid" },
        { 13, "BP out of range" },
        { 14, "Measurement serial error" },
        { 15, "Failed self-test" },
        { 17, "Device is busy" },
        { 18, "Timeout or connection error" },
        { 19, "Data receiving error" },
        { 20, "Data receiving timeout" },
        { 21, "Invalid port name" },
        { 22, "No measurement in progress" },
        { 23, "Failed safety test" },
        { 24, "Invalid date/time" },
        { 25, "Invalid patient mode" },
        { 26, "Invalid baud rate" },
    };

    public static string Get(int code) =>
        Map.TryGetValue(code, out var desc) ? desc : $"Unknown failure code {code}";
}
