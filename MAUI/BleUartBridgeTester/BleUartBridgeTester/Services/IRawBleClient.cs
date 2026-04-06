using BPplus.Ble;

namespace BleUartBridgeTester.Services;

public interface IRawBleClient
{
    bool IsConnected { get; }

    /// <summary>Fired on any thread when a BLE notification arrives.</summary>
    event EventHandler<byte[]> DataReceived;

    /// <summary>Fired when the remote device disconnects.</summary>
    event EventHandler Disconnected;

    Task ConnectAsync(BleDeviceInfo device, BleGattProfile profile,
                      CancellationToken ct = default);

    /// <summary>Chunk-writes data respecting negotiated MTU.</summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);

    Task DisconnectAsync();
}
