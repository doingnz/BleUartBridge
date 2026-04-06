namespace BPplus.Serial;

/// <summary>Base type for every parsed frame received from the BP+ device.</summary>
public abstract record BpPlusMessage(string RawLine);

// ── Unsolicited ───────────────────────────────────────────────────────────────

/// <summary>Device mode notification ('M nn'). Sent whenever the device changes state.</summary>
public record ModeMessage(int Code, string RawLine) : BpPlusMessage(RawLine)
{
    public DeviceMode Mode => Enum.IsDefined(typeof(DeviceMode), Code)
        ? (DeviceMode)Code
        : (DeviceMode)(-1);
}

/// <summary>Cuff pressure update ('P nnn' or 'p nnn'). Sent during measurements and pressure-test mode.</summary>
public record PressureMessage(int CuffPressureMmHg, string RawLine) : BpPlusMessage(RawLine);

// ── Command responses ─────────────────────────────────────────────────────────

/// <summary>Response to '?' — terminal API version string.</summary>
public record VersionMessage(string Version, string RawLine) : BpPlusMessage(RawLine);

/// <summary>Response to 'f' — feature XML (single-line, no XML-size wrapper).</summary>
public record FeatureMessage(string FeatureXml, string RawLine) : BpPlusMessage(RawLine);

/// <summary>Response to 'y' (get time).</summary>
public record DeviceTimeMessage(DateTime DeviceTime, string RawLine) : BpPlusMessage(RawLine);

/// <summary>
/// Measurement success ('S ...'). At detail level 4 the Xml property contains the full
/// measurement XML; at level 0 the raw space-delimited fields are in RawLine.
/// </summary>
public record MeasurementSuccessMessage(string Xml, bool XmlCrcValid, string RawLine)
    : BpPlusMessage(RawLine);

/// <summary>Measurement failure or command error ('F nn').</summary>
public record MeasurementFailureMessage(int Code, string Description, string RawLine)
    : BpPlusMessage(RawLine);

/// <summary>Textual error ('E msg'). Always followed by a MeasurementFailureMessage.</summary>
public record ErrorMessage(string Text, string RawLine) : BpPlusMessage(RawLine);

/// <summary>Response to 'i' — list of stored measurement IDs.</summary>
public record MeasurementIdListMessage(IReadOnlyList<int> Ids, bool CrcValid, string RawLine)
    : BpPlusMessage(RawLine);

/// <summary>Generic acknowledgement or echo with no structured fields.</summary>
public record AcknowledgeMessage(string RawLine) : BpPlusMessage(RawLine);

/// <summary>Unrecognised or malformed frame — logged but otherwise ignored.</summary>
public record UnknownMessage(string RawLine) : BpPlusMessage(RawLine);
