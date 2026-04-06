namespace BPplus.Ble;

/// <summary>Scans for nearby BLE devices and reports discoveries.</summary>
public interface IBleScanner
{
    bool IsScanning { get; }
    event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
