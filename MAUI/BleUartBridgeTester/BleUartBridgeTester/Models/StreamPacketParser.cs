using System.Buffers.Binary;

namespace BleUartBridgeTester.Models;

/// <summary>
/// Reassembles packets from a raw byte stream.
/// Packet format: [seq:4 LE][len:2 LE][payload:len][crc:2]
///   crc[0] = CRC-8/SMBUS over seq+len+payload; crc[1] = 0x00
/// </summary>
public sealed class StreamPacketParser
{
    private readonly List<byte> _buf = [];
    private const int HeaderSize  = 6;   // seq(4) + len(2)
    private const int TrailerSize = 2;   // crc(2)
    private const int MaxPayload  = 240;

    public event EventHandler<ParsedPacket>? PacketReceived;
    public event EventHandler<byte[]>?       SyncError;   // arg = buffer snapshot at error point (≤128 B)

    public int BufferSize => _buf.Count;

    public void Feed(byte[] data)
    {
        _buf.AddRange(data);
        Parse();
    }

    public void Reset() => _buf.Clear();

    private void Parse()
    {
        while (_buf.Count >= HeaderSize)
        {
            // Peek at len field without allocating
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(
                _buf.ToArray().AsSpan(4, 2));

            if (len > MaxPayload)
            {
                byte[] snap = [.. _buf.Take(Math.Min(_buf.Count, 128))];
                _buf.RemoveAt(0);
                SyncError?.Invoke(this, snap);
                continue;
            }

            int total = HeaderSize + len + TrailerSize;
            if (_buf.Count < total) break;

            byte[] raw = [.. _buf.Take(total)];

            byte expected = Crc8.Compute(raw.AsSpan(0, HeaderSize + len));
            byte received = raw[HeaderSize + len];  // crc[0]

            if (expected == received)
            {
                uint   seq     = BinaryPrimitives.ReadUInt32LittleEndian(raw);
                byte[] payload = raw[HeaderSize..(HeaderSize + len)];
                _buf.RemoveRange(0, total);
                PacketReceived?.Invoke(this, new ParsedPacket(seq, payload));
            }
            else
            {
                // Pass a snapshot of the buffer before discarding the bad byte so
                // the caller can see the raw received bytes and attempt to decode them.
                byte[] snap = [.. _buf.Take(Math.Min(_buf.Count, 128))];
                _buf.RemoveAt(0);
                SyncError?.Invoke(this, snap);
            }
        }
    }
}

public sealed record ParsedPacket(uint Seq, byte[] Payload);
