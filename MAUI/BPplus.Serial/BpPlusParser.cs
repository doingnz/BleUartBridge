using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BPplus.Serial;

/// <summary>
/// Static protocol-level parsing and formatting helpers.
/// Shared between <see cref="BpPlusSerialClient"/> and any alternative transport
/// implementations (e.g. BLE adapters) that use the same ASCII line protocol.
/// </summary>
public static class BpPlusParser
{
    // =========================================================================
    // Line parser
    // =========================================================================

    /// <summary>Parses a single trimmed ASCII line into a typed <see cref="BpPlusMessage"/>.</summary>
    public static BpPlusMessage ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new UnknownMessage(line);

        // Version response — device replies to '?' with bare "verN.M" (no leading type char)
        if (line.StartsWith("ver", StringComparison.OrdinalIgnoreCase))
            return new VersionMessage(line.Trim(), line);

        // Detail-level acknowledgement — device echoes 'D <level>' (upper-case D)
        if (line.Length >= 2 && line[0] == 'D' && line[1] == ' '
            && int.TryParse(line[2..].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out _))
            return new AcknowledgeMessage(line);

        // Feature XML response (starts with '<Feature')
        if (line.TrimStart().StartsWith("<Feature", StringComparison.OrdinalIgnoreCase))
            return new FeatureMessage(line.Trim(), line);

        if (line.Length < 1)
            return new UnknownMessage(line);

        char   typ  = line[0];
        string rest = line.Length > 2 ? line[2..].Trim() : string.Empty;

        return typ switch
        {
            'V' => new VersionMessage(rest, line),

            'M' => int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)
                       ? new ModeMessage(mode, line)
                       : new UnknownMessage(line),

            'P' or 'p' => int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
                              ? new PressureMessage(p, line)
                              : new UnknownMessage(line),

            'S' => new MeasurementSuccessMessage(string.Empty, false, line), // detail level 0

            'F' => int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                       ? new MeasurementFailureMessage(code, FailureDescriptions.Get(code), line)
                       : new MeasurementFailureMessage(-1, rest, line),

            'E' => new ErrorMessage(rest.Trim('"', ' '), line),

            'T' => DateTime.TryParseExact(rest.Trim(), "yyyyMMddHHmmss",
                       CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                       ? new DeviceTimeMessage(dt, line)
                       : new DeviceTimeMessage(DateTime.MinValue, line),

            _ => new UnknownMessage(line),
        };
    }

    // =========================================================================
    // Two-line IDs response parser
    // =========================================================================

    /// <summary>
    /// Parses the two-line IDs response: an "IDs_H …" header followed by
    /// "IDs_Content …" content.
    /// </summary>
    public static MeasurementIdListMessage ParseIdsContent(string header, string content)
    {
        // Header: "IDs_H <len> <crc8>"
        // Content: "IDs_Content <id0> <id1> …"
        var hParts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool crcValid = false;

        if (hParts.Length >= 3
            && int.TryParse(hParts[2], out int expectedCrc))
        {
            byte[] contentBytes = Encoding.ASCII.GetBytes(content + "\r\n");
            byte   computed     = Crc8.Compute(contentBytes);
            crcValid = computed == (byte)expectedCrc;
        }

        var ids = new List<int>();
        if (content.StartsWith("IDs_Content ", StringComparison.Ordinal))
        {
            foreach (var token in content["IDs_Content ".Length..].Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out int id))
                    ids.Add(id);
            }
        }

        return new MeasurementIdListMessage(ids, crcValid, header + "\n" + content);
    }

    // =========================================================================
    // Feature XML parser
    // =========================================================================

    /// <summary>Parses the XML payload returned by the 'f' (features) command.</summary>
    public static FeatureInfo ParseFeatureXml(string xml)
    {
        // The device is known to emit malformed closing tags, e.g.:
        //   <nibp_id>5B2800234   <nibp_id>   (missing '/' in closing tag)
        // Fix before parsing: <tag>value<tag> → <tag>value</tag>
        string fixedXml = Regex.Replace(xml, @"<(\w+)>([^<]*)<\1>", "<$1>$2</$1>");

        XElement? root = null;
        try { root = XDocument.Parse(fixedXml).Root; }
        catch { /* fall through to return with RawXml only */ }

        if (root == null)
            return new FeatureInfo("", "", null, null, null, null, null, null, null, null, null, null, xml);

        var version = root.Attribute("version")?.Value ?? string.Empty;

        BpRanges? bpRanges = null;
        var bpRangeElem = root.Element("bpRange");
        if (bpRangeElem != null)
        {
            bpRanges = new BpRanges(
                ParseRange(bpRangeElem.Element("sys")?.Value),
                ParseRange(bpRangeElem.Element("dia")?.Value),
                ParseRange(bpRangeElem.Element("map")?.Value),
                ParseRange(bpRangeElem.Element("hr")?.Value));
        }

        return new FeatureInfo(
            Version:            version,
            ProtocolXmlVersion: root.Element("xml")?.Value          ?? string.Empty,
            FirmwareVersion:    root.Element("fw")?.Value,
            SoftwareVersion:    root.Element("sw")?.Value,
            HardwareId:         root.Element("hw")?.Value,
            DeviceId:           root.Element("id")?.Value,
            NibpType:           root.Element("nibpType")?.Value,
            NibpVersion:        root.Element("nibpVersion")?.Value?.Trim(),
            NibpId:             root.Element("nibp_id")?.Value?.Trim(),
            PcbId:              root.Element("pcb_id")?.Value,
            ThemeId:            root.Element("theme_id")?.Value,
            BpRanges:           bpRanges,
            RawXml:             xml);
    }

    // =========================================================================
    // Measurement command builder
    // =========================================================================

    /// <summary>Formats the 's' command string from a <see cref="MeasurementRequest"/>.</summary>
    public static string BuildMeasurementCommand(MeasurementRequest r)
    {
        if (r.TargetPressureMmHg == 0 && r.PatientId == null && r.Mode == MeasureMode.Auto)
            return "s";

        string target    = r.TargetPressureMmHg.ToString(CultureInfo.InvariantCulture);
        string patientId = r.PatientId ?? string.Empty;
        string mode      = r.Mode switch
        {
            MeasureMode.Inflate     => "i",
            MeasureMode.Deflate     => "d",
            MeasureMode.SlowDeflate => "4",
            _                       => string.Empty,
        };

        return string.IsNullOrEmpty(mode)
            ? $"s {target},\"{patientId}\""
            : $"s {target},\"{patientId}\",\"{mode}\"";
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private static BpRangeInfo? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0].Trim(), out int max)) return null;
        if (!int.TryParse(parts[1].Trim(), out int min)) return null;
        return new BpRangeInfo(max, min);
    }
}
