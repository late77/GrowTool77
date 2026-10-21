using System.Buffers.Binary;
using System.IO.Compression;

namespace GrowTool77.Aura;

// A decoded texture: top-down rows, 4 bytes per pixel (R, G, B, A).
public sealed class RtTexImage
{
    public int    Width, Height;
    public byte[] Rgba = Array.Empty<byte>();
}

// Proton SDK textures (.rttex) as used by Growtopia's item sheets:
// an optional "RTPACK" zlib wrapper around an "RTTXTR" image whose pixel rows
// are stored bottom-up. Only uncompressed 8-bit RGBA/RGB is supported; other
// formats return null (the item then simply has no icon).
public static class RtTex
{
    private const int RtPackHeader = 32;
    private const int RtTxtrHeader = 124;     // 100-byte header + 24-byte first mip header
    private const int GlUnsignedByte = 0x1401;

    public static RtTexImage? Decode(byte[] file)
    {
        try
        {
            byte[] data = file;
            if (file.Length >= RtPackHeader && file.AsSpan(0, 6).SequenceEqual("RTPACK"u8))
            {
                uint compressed   = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
                uint decompressed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
                byte kind         = file[16];
                if (compressed > file.Length - RtPackHeader || decompressed > 256 * 1024 * 1024) return null;

                if (kind == 1)
                {
                    using var z = new ZLibStream(new MemoryStream(file, RtPackHeader, (int)compressed), CompressionMode.Decompress);
                    data = new byte[decompressed];
                    z.ReadExactly(data);
                }
                else
                {
                    data = file.AsSpan(RtPackHeader, (int)compressed).ToArray();
                }
            }

            if (data.Length < RtTxtrHeader || !data.AsSpan(0, 6).SequenceEqual("RTTXTR"u8)) return null;
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
            int width  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            int format = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
            bool alpha        = data[28] == 1;
            bool preCompressed = data[29] == 1;
            if (format != GlUnsignedByte || preCompressed || width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;

            int bpp = alpha ? 4 : 3;
            if (data.Length < RtTxtrHeader + (long)width * height * bpp) return null;

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int src = RtTxtrHeader + (height - 1 - y) * width * bpp;   // bottom-up on disk
                int dst = y * width * 4;
                for (int x = 0; x < width; x++, src += bpp, dst += 4)
                {
                    rgba[dst]     = data[src];
                    rgba[dst + 1] = data[src + 1];
                    rgba[dst + 2] = data[src + 2];
                    rgba[dst + 3] = alpha ? data[src + 3] : (byte)255;
                }
            }
            return new RtTexImage { Width = width, Height = height, Rgba = rgba };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }

    // The 32x32 cell holding an item's icon. Blocks drawn from several cells
    // (smart edges, rows, columns…) use their "standing alone" variant, the
    // same offsets the open-source world renderers use.
    public static (int X, int Y) IconCell(ItemInfo item)
    {
        var (dx, dy) = item.SpreadType switch
        {
            2 or 5     => (4, 1),     // 8- and 4-direction smart edges: no neighbours
            3 or 7 or 9 => (3, 0),    // horizontal / vertical / wall-attached rows
            4          => (4, 0),     // attaches to a wall
            _          => (0, 0),
        };
        return (item.TexX + dx, item.TexY + dy);
    }

    // Copies one 32x32 cell out of a sheet, or null if it lies outside.
    public static byte[]? Cell(RtTexImage sheet, int cellX, int cellY, int size = 32)
    {
        int x0 = cellX * size, y0 = cellY * size;
        if (cellX < 0 || cellY < 0 || x0 + size > sheet.Width || y0 + size > sheet.Height) return null;
        var cell = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            Buffer.BlockCopy(sheet.Rgba, ((y0 + y) * sheet.Width + x0) * 4, cell, y * size * 4, size * 4);
        return cell;
    }
}
