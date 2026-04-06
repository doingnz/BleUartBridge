namespace BleUartBridgeTester.Services;

public interface ISerialPortService
{
    bool IsOpen { get; }

    /// <summary>Fired on a thread-pool thread when bytes arrive.</summary>
    event EventHandler<byte[]> DataReceived;

    IReadOnlyList<string> GetAvailablePorts();

    void Open(string portName, int baudRate, int dataBits,
              string parity, string stopBits, bool flowControl);

    void Send(byte[] data);

    void Close();
}
