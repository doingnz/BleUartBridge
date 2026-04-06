using BPplus.Ble;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;

namespace BleUartBridgeTester.Services;

public sealed class RawBleClient : IRawBleClient
{
    private IDevice?         _device;
    private ICharacteristic? _txChar;
    private ICharacteristic? _rxChar;
    private const int        MtuPayload = 128; // matches ESP32 BLE_CHUNK

    public bool IsConnected => _device?.State == Plugin.BLE.Abstractions.DeviceState.Connected;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler?         Disconnected;

    public async Task ConnectAsync(BleDeviceInfo info, BleGattProfile profile,
                                   CancellationToken ct = default)
    {
        var adapter = CrossBluetoothLE.Current.Adapter;

        adapter.DeviceDisconnected += OnDeviceDisconnected;

        _device = await adapter.ConnectToKnownDeviceAsync(info.Id, cancellationToken: ct);

        var service = await _device.GetServiceAsync(profile.ServiceUuid, ct)
            ?? throw new InvalidOperationException($"Service {profile.ServiceUuid} not found.");

        _txChar = await service.GetCharacteristicAsync(profile.TxCharUuid)
            ?? throw new InvalidOperationException("TX characteristic not found.");

        _rxChar = profile.Bidirectional
            ? _txChar
            : await service.GetCharacteristicAsync(profile.RxCharUuid)
              ?? throw new InvalidOperationException("RX characteristic not found.");

        _rxChar.ValueUpdated += OnValueUpdated;
        await _rxChar.StartUpdatesAsync(ct);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_txChar is null) throw new InvalidOperationException("Not connected.");

        for (int offset = 0; offset < data.Length; offset += MtuPayload)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(MtuPayload, data.Length - offset);
            byte[] chunk = data[offset..(offset + count)];
            await _txChar.WriteAsync(chunk, ct);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_device is null) return;

        if (_rxChar is not null)
        {
            _rxChar.ValueUpdated -= OnValueUpdated;
            try { await _rxChar.StopUpdatesAsync(); } catch { }
        }

        try
        {
            CrossBluetoothLE.Current.Adapter.DeviceDisconnected -= OnDeviceDisconnected;
            await CrossBluetoothLE.Current.Adapter.DisconnectDeviceAsync(_device);
        }
        catch { }

        _device  = null;
        _txChar  = null;
        _rxChar  = null;
    }

    private void OnValueUpdated(object? sender, Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs e)
        => DataReceived?.Invoke(this, e.Characteristic.Value ?? []);

    private void OnDeviceDisconnected(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceEventArgs e)
    {
        if (e.Device.Id == _device?.Id)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
