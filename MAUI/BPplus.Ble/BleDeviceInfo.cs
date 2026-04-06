namespace BPplus.Ble;

/// <summary>Information about a BLE device discovered during scanning.</summary>
public sealed record BleDeviceInfo(
    Guid   Id,
    string Name,
    int    Rssi,
    DateTimeOffset LastSeen)
{
    /// <summary>Display string suitable for picker binding.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? $"{Id}  {Rssi} dBm"
            : $"{Name}  {Rssi} dBm";
}
