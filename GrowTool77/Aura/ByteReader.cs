using System.Buffers.Binary;
using System.Text;

namespace GrowTool77.Aura;

// Bounds-checked little-endian reader. Every read throws FormatException
// instead of running past the end, so a malformed packet can't crash [AURA].
internal sealed class ByteReader
{
    private readonly byte[] _data;
    private readonly int    _end;

    public int Position { get; set; }

    public ByteReader(byte[] data, int offset = 0, int length = -1)
    {
        _data    = data;
        Position = offset;
        _end     = length < 0 ? data.Length : Math.Min(data.Length, offset + length);
    }

    public int  End       => _end;
    public int  Remaining => _end - Position;
    public bool Has(int n) => n >= 0 && Position <= _end - n;

    private int Take(int n)
    {
        if (!Has(n)) throw new FormatException("Unexpected end of data.");
        int p = Position;
        Position += n;
        return p;
    }

    public byte   U8()  => _data[Take(1)];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Take(2)));
    public short  I16() => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Take(2)));
    public uint   U32() => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Take(4)));
    public int    I32() => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Take(4)));
    public float  F32() => BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Take(4)));

    public void Skip(long n)
    {
        if (n < 0 || n > Remaining) throw new FormatException("Unexpected end of data.");
        Position += (int)n;
    }

    public ReadOnlySpan<byte> Bytes(int n) => _data.AsSpan(Take(n), n);

    // Growtopia strings: u16 length + bytes
    public string Str16()
    {
        int len = U16();
        return Encoding.Latin1.GetString(_data, Take(len), len);
    }

    // Variant strings: u32 length + bytes
    public string Str32()
    {
        uint len = U32();
        if (len > int.MaxValue) throw new FormatException("String too long.");
        return Encoding.Latin1.GetString(_data, Take((int)len), (int)len);
    }

    public void SkipStr16() => Skip(U16());
}
