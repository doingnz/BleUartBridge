using System.IO.Ports;

namespace BleUartBridgeTester.Services;

public sealed class SerialPortService : ISerialPortService, IDisposable
{
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen ?? false;

    public event EventHandler<byte[]>? DataReceived;

    public IReadOnlyList<string> GetAvailablePorts()
        => SerialPort.GetPortNames().OrderBy(p => p).ToList();

    public void Open(string portName, int baudRate, int dataBits,
                     string parity, string stopBits, bool flowControl)
    {
        Close();

        _port = new SerialPort(portName, baudRate,
            Enum.Parse<Parity>(parity),
            dataBits,
            Enum.Parse<StopBits>(stopBits switch
            {
                "1"   => nameof(StopBits.One),
                "1.5" => nameof(StopBits.OnePointFive),
                "2"   => nameof(StopBits.Two),
                _     => nameof(StopBits.One),
            }))
        {
            Handshake  = flowControl ? Handshake.RequestToSend : Handshake.None,
            ReadTimeout  = 500,
            WriteTimeout = 500,
        };

        _port.DataReceived += OnDataReceived;
        _port.Open();
    }

    public void Send(byte[] data)
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("Port is not open.");
        _port.Write(data, 0, data.Length);
    }

    public void Close()
    {
        if (_port is null) return;
        _port.DataReceived -= OnDataReceived;
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _port = null;
    }

    public void Dispose() => Close();

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            int available = _port!.BytesToRead;
            if (available <= 0) return;
            byte[] buf = new byte[available];
            _port.Read(buf, 0, available);
            DataReceived?.Invoke(this, buf);
        }
        catch { /* port may have closed */ }
    }
}
