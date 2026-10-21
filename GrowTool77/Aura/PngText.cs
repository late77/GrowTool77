using System.Buffers.Binary;
using System.Text;

namespace GrowTool77.Aura;

// Adds / reads a text chunk (tEXt) in a PNG file, so a diagnostic picture can
// carry its own data. Chunks are inserted before IEND with a proper CRC.
public static class PngText
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static byte[] Insert(byte[] png, string keyword, string text)
    {
        int iend = FindChunk(png, "IEND");
        if (iend < 0) throw new FormatException("Not a PNG file.");

        var body = Encoding.Latin1.GetBytes(keyword).Concat(new byte[] { 0 }).Concat(Encoding.Latin1.GetBytes(text)).ToArray();
        var chunk = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)body.Length);
        Encoding.ASCII.GetBytes("tEXt").CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + body.Length), Crc32(chunk.AsSpan(4, 4 + body.Length)));

        return png.Take(iend).Concat(chunk).Concat(png.Skip(iend)).ToArray();
    }

    public static string? Read(byte[] png, string keyword)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        for (int p = 8; p + 12 <= png.Length;)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            if (len < 0 || p + 12 + len > png.Length) return null;
            if (Encoding.ASCII.GetString(png, p + 4, 4) == "tEXt")
            {
                var body = png.AsSpan(p + 8, len);
                int zero = body.IndexOf((byte)0);
                if (zero > 0 && Encoding.Latin1.GetString(body[..zero]) == keyword)
                    return Encoding.Latin1.GetString(body[(zero + 1)..]);
            }
            p += 12 + len;
        }
        return null;
    }

    private static int FindChunk(byte[] png, string type)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return -1;
        for (int p = 8; p + 12 <= png.Length;)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            if (len < 0 || p + 12 + len > png.Length) return -1;
            if (Encoding.ASCII.GetString(png, p + 4, 4) == type) return p;
            p += 12 + len;
        }
        return -1;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
