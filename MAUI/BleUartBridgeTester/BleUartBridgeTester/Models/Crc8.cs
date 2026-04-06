namespace BleUartBridgeTester.Models;

/// <summary>CRC-8/SMBUS — poly 0x07, init 0x00, no reflection.</summary>
public static class Crc8
{
    private static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte c = (byte)i;
            for (int j = 0; j < 8; j++)
                c = (c & 0x80) != 0 ? (byte)((c << 1) ^ 0x07) : (byte)(c << 1);
            t[i] = c;
        }
        return t;
    }

    public static byte Compute(ReadOnlySpan<byte> data, byte init = 0)
    {
        byte crc = init;
        foreach (byte b in data)
            crc = Table[crc ^ b];
        return crc;
    }
}
