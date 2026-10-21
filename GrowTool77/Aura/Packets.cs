using System.Buffers.Binary;
using System.Text;

namespace GrowTool77.Aura;

// Message types in the first 4 bytes of every ENet packet.
internal static class NetMessage
{
    public const int ServerHello = 1;
    public const int GenericText = 2;
    public const int GameMessage = 3;
    public const int GamePacket  = 4;
}

// Game packet types (first byte of the 56-byte game update header).
internal static class GamePacketType
{
    public const byte CallFunction         = 1;
    public const byte SendMapData          = 4;
    public const byte ItemChangeObject     = 14;
    public const byte SendItemDatabaseData = 16;
}

// Read-only view over a NET_MESSAGE_GAME_PACKET. Offsets match the header
// used by GTProxy, growbrew and the Growtopia Noobs sources.
internal readonly struct GameUpdate
{
    public const int HeaderStart = 4;   // after the 4-byte message type
    public const int HeaderSize  = 56;
    public const int ExtraStart  = HeaderStart + HeaderSize;

    public readonly byte  Type;
    public readonly int   NetId;        // +4
    public readonly int   ItemNetId;    // +8
    public readonly uint  Flags;        // +12
    public readonly float FloatVar;     // +16
    public readonly int   RawAt16;      // +16 as an integer
    public readonly int   IntData;      // +20
    public readonly float PosX;         // +24
    public readonly float PosY;         // +28
    public readonly int   IntX;         // +44
    public readonly int   IntY;         // +48
    public readonly uint  DataSize;     // +52
    public readonly int   ExtraLength;

    private GameUpdate(byte[] p)
    {
        var s = p.AsSpan(HeaderStart);
        Type      = s[0];
        NetId     = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        ItemNetId = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        Flags     = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        FloatVar  = BinaryPrimitives.ReadSingleLittleEndian(s[16..]);
        RawAt16   = BinaryPrimitives.ReadInt32LittleEndian(s[16..]);
        IntData   = BinaryPrimitives.ReadInt32LittleEndian(s[20..]);
        PosX      = BinaryPrimitives.ReadSingleLittleEndian(s[24..]);
        PosY      = BinaryPrimitives.ReadSingleLittleEndian(s[28..]);
        IntX      = BinaryPrimitives.ReadInt32LittleEndian(s[44..]);
        IntY      = BinaryPrimitives.ReadInt32LittleEndian(s[48..]);
        DataSize  = BinaryPrimitives.ReadUInt32LittleEndian(s[52..]);

        int available = p.Length - ExtraStart;
        ExtraLength = DataSize > 0 && DataSize <= (uint)available ? (int)DataSize : available;
    }

    public static bool TryRead(byte[] payload, out GameUpdate update)
    {
        update = default;
        if (payload.Length < ExtraStart) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(payload) != NetMessage.GamePacket) return false;
        update = new GameUpdate(payload);
        return true;
    }
}

internal static class TextMessage
{
    public static bool TryRead(byte[] payload, out string text)
    {
        text = "";
        if (payload.Length < 4) return false;
        int type = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (type != NetMessage.GenericText && type != NetMessage.GameMessage) return false;
        int len = payload.Length - 4;
        while (len > 0 && payload[4 + len - 1] == 0) len--;
        text = Encoding.Latin1.GetString(payload, 4, len);
        return true;
    }

    // Returns the value of "key|value" on its own line, or null.
    public static string? Value(string text, string key)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(key + "|", StringComparison.Ordinal))
                return line[(key.Length + 1)..];
        }
        return null;
    }
}

// Growtopia's variant list (used by CALL_FUNCTION packets). Values are kept
// with their original type tags so a rewritten packet is byte-identical
// except for the fields that are deliberately changed.
internal sealed class VariantList
{
    public const byte TFloat = 1, TString = 2, TVec2 = 3, TVec3 = 4, TUInt = 5, TInt = 9;

    public sealed class Entry
    {
        public byte   Index;
        public byte   Type;
        public float  F1, F2, F3;
        public string S = "";
        public uint   U;
        public int    I;
    }

    public List<Entry> Items { get; } = new();

    public string? Function => Items.Count > 0 && Items[0].Type == TString ? Items[0].S : null;

    public static bool TryParse(byte[] data, int offset, int length, out VariantList list)
    {
        list = new VariantList();
        try
        {
            var r = new ByteReader(data, offset, length);
            int count = r.U8();
            for (int i = 0; i < count; i++)
            {
                var e = new Entry { Index = r.U8(), Type = r.U8() };
                switch (e.Type)
                {
                    case TFloat:  e.F1 = r.F32(); break;
                    case TString: e.S  = r.Str32(); break;
                    case TVec2:   e.F1 = r.F32(); e.F2 = r.F32(); break;
                    case TVec3:   e.F1 = r.F32(); e.F2 = r.F32(); e.F3 = r.F32(); break;
                    case TUInt:   e.U  = r.U32(); break;
                    case TInt:    e.I  = r.I32(); break;
                    default: return false;
                }
                list.Items.Add(e);
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);
        w.Write((byte)Items.Count);
        foreach (var e in Items)
        {
            w.Write(e.Index);
            w.Write(e.Type);
            switch (e.Type)
            {
                case TFloat:  w.Write(e.F1); break;
                case TString:
                    var bytes = Encoding.Latin1.GetBytes(e.S);
                    w.Write((uint)bytes.Length);
                    w.Write(bytes);
                    break;
                case TVec2:   w.Write(e.F1); w.Write(e.F2); break;
                case TVec3:   w.Write(e.F1); w.Write(e.F2); w.Write(e.F3); break;
                case TUInt:   w.Write(e.U); break;
                case TInt:    w.Write(e.I); break;
            }
        }
        return ms.ToArray();
    }

    public int? GetInt(int i)
    {
        if (i >= Items.Count) return null;
        var e = Items[i];
        return e.Type switch { TInt => e.I, TUInt => (int)e.U, TFloat => (int)e.F1, _ => null };
    }

    public void SetInt(int i, int value)
    {
        var e = Items[i];
        switch (e.Type)
        {
            case TInt:   e.I  = value; break;
            case TUInt:  e.U  = (uint)value; break;
            case TFloat: e.F1 = value; break;
        }
    }

    // Rebuilds a CALL_FUNCTION packet: same message type and 56-byte header,
    // new variant data and a corrected data size.
    public static byte[] RebuildCallFunction(byte[] original, in GameUpdate gu, byte[] newExtra)
    {
        int tailStart = GameUpdate.ExtraStart + gu.ExtraLength;
        int tailLen   = Math.Max(0, original.Length - tailStart);
        var result    = new byte[GameUpdate.ExtraStart + newExtra.Length + tailLen];
        Buffer.BlockCopy(original, 0, result, 0, GameUpdate.ExtraStart);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(GameUpdate.HeaderStart + 52), (uint)newExtra.Length);
        Buffer.BlockCopy(newExtra, 0, result, GameUpdate.ExtraStart, newExtra.Length);
        if (tailLen > 0) Buffer.BlockCopy(original, tailStart, result, GameUpdate.ExtraStart + newExtra.Length, tailLen);
        return result;
    }
}
