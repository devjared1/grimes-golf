namespace GrimesGolf.Core.Garmin;

/// <summary>
/// Consistent Overhead Byte Stuffing (Cheshire and Baker, 1999): rewrites a frame so it holds no 0x00, letting 0x00
/// mark where frames start and end on the R10's link.
/// </summary>
public static class Cobs
{
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + data.Length / 254 + 2);
        int codeAt = 0;
        output.Add(0);   // the block's code, filled in when the block ends
        byte code = 1;
        bool open = true;   // false after a full block (254 bytes): the next one starts only if more data comes
        foreach (byte b in data)
        {
            if (!open)
            {
                codeAt = output.Count;
                output.Add(0);
                code = 1;
                open = true;
            }
            if (b == 0)
            {
                output[codeAt] = code;
                codeAt = output.Count;
                output.Add(0);
                code = 1;
                continue;
            }
            output.Add(b);
            if (++code == 0xFF)
            {
                output[codeAt] = code;
                open = false;
            }
        }
        if (open) output[codeAt] = code;
        return output.ToArray();
    }

    /// <exception cref="FormatException">Not valid COBS (a 0x00, or a block running past the end).</exception>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        int i = 0;
        while (i < data.Length)
        {
            byte code = data[i];
            if (code == 0) throw new FormatException($"COBS data holds a zero at {i}.");
            if (i + code > data.Length) throw new FormatException($"COBS block at {i} runs past the end.");
            for (int k = 1; k < code; k++)
            {
                if (data[i + k] == 0) throw new FormatException($"COBS data holds a zero at {i + k}.");
                output.Add(data[i + k]);
            }
            i += code;
            if (code < 0xFF && i < data.Length) output.Add(0);
        }
        return output.ToArray();
    }
}

/// <summary>CRC-16/ARC (poly 0x8005 reflected, init 0, no final XOR; check value 0xBB3D), the GFDI frame checksum.</summary>
public static class Crc16Arc
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data) crc = (ushort)(crc >> 8 ^ Table[(byte)(crc ^ b)]);
        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)i;
            for (int bit = 0; bit < 8; bit++) v = (v & 1) != 0 ? (ushort)(v >> 1 ^ 0xA001) : (ushort)(v >> 1);
            table[i] = v;
        }
        return table;
    }
}
