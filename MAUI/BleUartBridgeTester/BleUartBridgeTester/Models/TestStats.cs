using CommunityToolkit.Mvvm.ComponentModel;

namespace BleUartBridgeTester.Models;

public partial class TestStats : ObservableObject
{
    [ObservableProperty] private long   _packetsSent;
    [ObservableProperty] private long   _packetsReceived;
    [ObservableProperty] private long   _errors;
    [ObservableProperty] private double _bytesPerSec;
    [ObservableProperty] private double _latencyAvgMs;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LatencyMinDisplay))]
    private double _latencyMinMs = double.MaxValue;
    [ObservableProperty] private double _latencyMaxMs;
    [ObservableProperty] private string _elapsed = "00:00:00";

    private long   _latencySamples;
    private double _latencySum;

    public void UpdateLatency(double ms)
    {
        _latencySamples++;
        _latencySum += ms;
        LatencyAvgMs = _latencySum / _latencySamples;
        if (ms < LatencyMinMs) LatencyMinMs = ms;
        if (ms > LatencyMaxMs) LatencyMaxMs = ms;
    }

    public void Reset()
    {
        PacketsSent     = 0;
        PacketsReceived = 0;
        Errors          = 0;
        BytesPerSec     = 0;
        LatencyAvgMs    = 0;
        LatencyMinMs    = double.MaxValue;
        LatencyMaxMs    = 0;
        Elapsed         = "00:00:00";
        _latencySamples = 0;
        _latencySum     = 0;
    }

    public string LatencyMinDisplay =>
        LatencyMinMs == double.MaxValue ? "—" : $"{LatencyMinMs:F1} ms";
}
