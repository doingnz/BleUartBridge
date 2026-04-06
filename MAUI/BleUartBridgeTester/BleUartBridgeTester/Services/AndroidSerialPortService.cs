namespace BleUartBridgeTester.Services;

/// <summary>No-op stub used on Android where System.IO.Ports is unavailable.</summary>
public sealed class AndroidSerialPortService : ISerialPortService
{
    public bool IsOpen => false;

#pragma warning disable CS0067
    public event EventHandler<byte[]>? DataReceived;
#pragma warning restore CS0067

    public IReadOnlyList<string> GetAvailablePorts() => [];

    public void Open(string portName, int baudRate, int dataBits,
                     string parity, string stopBits, bool flowControl)
        => throw new PlatformNotSupportedException("COM ports are not available on Android.");

    public void Send(byte[] data)
        => throw new PlatformNotSupportedException("COM ports are not available on Android.");

    public void Close() { }
}
