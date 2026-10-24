namespace GrowTool77.Aura;

public sealed class DroppedObject
{
    public int   ItemId;
    public float X, Y;
    public int   Amount;
    public byte  Flags;
    public uint  Uid;
}

// A vending machine as stored in the world: price > 0 = World Locks per item,
// price < 0 = that many items per World Lock, 0 = not priced.
public sealed record VendInfo(int X, int Y, int ItemId, int Price)
{
    public bool   IsForSale  => ItemId > 0 && Price != 0;
    public double WlPerItem  => Price > 0 ? Price : Price < 0 ? 1.0 / -(double)Price : double.PositiveInfinity;
}

public sealed class WorldSnapshot
{
    public string Name    = "";
    public int    Version;
    public uint   Width, Height;
    public uint   LastObjectUid;
    public bool   TilesParsed;   // false = objects were located by the fallback search
    public string? Note;
    public List<DroppedObject> Objects = new();
    public List<VendInfo>      Vends   = new();
    public List<DoorInfo>      Doors   = new();
    public ushort[] Fg = Array.Empty<ushort>();   // foreground block per tile (0 = empty)
    public ushort[] Bg = Array.Empty<ushort>();   // background per tile
    public int TilesRead;                         // tiles read before an unknown type stopped the parser
}

// Reads SEND_MAP_DATA. Tile layout follows badewen's documented world format
// (Growtopia-Things/world_parser.ksy, 2026). If a tile type is unknown the
// dropped-object list is located by a validated search instead.
public static class WorldParser
{
    private static readonly HashSet<int> CborItemIds = new() { 15546, 14666, 14962, 14662 };
    private static readonly string[] CborExtraFiles = { "AutoSurgeonStation.xml", "ClothesRack.xml", "OperatingTable.xml" };

    public static WorldSnapshot Parse(byte[] data, int offset, int length, ItemDatabase? items)
    {
        var w = new WorldSnapshot();
        var r = new ByteReader(data, offset, length);

        w.Version = r.U16();
        r.Skip(4);
        w.Name   = r.Str16();
        w.Width  = r.U32();
        w.Height = r.U32();
        uint tileCount = r.U32();
        r.Skip(5);

        if (w.Name.Length == 0 || w.Name.Length > 32) throw new FormatException("Unexpected world name.");
        if (tileCount > 1_000_000) throw new FormatException("Unexpected tile count.");

        int tilesStart = r.Position;
        string? failure = null;
        try
        {
            w.Fg = new ushort[tileCount];
            w.Bg = new ushort[tileCount];
            for (uint i = 0; i < tileCount; i++)
            {
                if (!ReadTile(r, items, out int badType, w, i))
                {
                    failure = $"unknown tile data type {badType} at tile {i} ({i % Math.Max(1, w.Width)}, {i / Math.Max(1, w.Width)})";
                    break;
                }
            }
        }
        catch (FormatException)
        {
            failure = "tile data ended early";
        }

        if (failure == null && TryReadObjects(r, w, out var objects, out uint lastUid))
        {
            w.Objects       = objects;
            w.LastObjectUid = lastUid;
            w.TilesParsed   = true;
            return w;
        }

        // Fallback: find the object block by its structure.
        var found = FindObjectBlock(data, tilesStart, offset + length, w);
        w.TilesParsed = false;
        w.Note = failure ?? "object list did not validate";
        if (found != null)
        {
            w.Objects       = found.Value.Objects;
            w.LastObjectUid = found.Value.LastUid;
        }
        else
        {
            w.Note += "; dropped items could not be located";
        }
        return w;
    }

    private static bool ReadTile(ByteReader r, ItemDatabase? items, out int badType, WorldSnapshot w, uint index)
    {
        badType = 0;
        int fg    = r.U16();
        int bg    = r.U16();
        if (index < w.Fg.Length)
        {
            w.Fg[index] = (ushort)fg;
            w.Bg[index] = (ushort)bg;
            w.TilesRead = (int)index + 1;
        }
        r.Skip(2);              // parent block index
        int flags = r.U16();
        if ((flags & 0x02) != 0) r.Skip(2);   // parent lock

        int extraType = 0;
        if ((flags & 0x01) != 0)
        {
            extraType = r.U8();
            if (!SkipExtra(r, extraType, fg, w, index))
            {
                badType = extraType;
                return false;
            }
        }

        if (HasCbor(fg, flags, extraType, items))
            r.Skip(r.U32());
        return true;
    }

    private static bool HasCbor(int fg, int flags, int extraType, ItemDatabase? items)
    {
        if (CborItemIds.Contains(fg)) return true;
        if ((flags & 0x01) != 0 && extraType == 36) return true;
        var info = items?.Get(fg);
        if (info == null) return false;
        if (info.Type == 77) return true;
        foreach (var f in CborExtraFiles)
            if (info.ExtraFile.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Skips one tile's extra data. Returns false for types we don't know.
    private static bool SkipExtra(ByteReader r, int type, int fg, WorldSnapshot w, uint index)
    {
        switch (type)
        {
            case 1:                                                        // door
            {
                string dest = r.Str16();
                r.Skip(1); // door flags
                uint dw = Math.Max(1u, w.Width);
                w.Doors.Add(new DoorInfo((int)(index % dw), (int)(index / dw), fg, dest));
                break;
            }
            case 2:  r.SkipStr16(); r.Skip(4); break;                      // sign
            case 3:                                                        // lock
                r.Skip(1 + 4);
                r.Skip(4L * r.U32());
                r.Skip(4 + 4);
                if (fg == 5814) r.Skip(4 + 2 + 2 + 4 + 4);
                break;
            case 4:  r.Skip(4 + 1); break;                                 // seed
            case 8:  r.Skip(1); break;                                     // dice
            case 9:  r.Skip(4); if (fg == 10656) r.Skip(4); break;         // provider
            case 10: r.Skip(4 + 1); break;                                 // achievement block
            case 11: r.Skip(4); r.SkipStr16(); break;                      // heart monitor
            case 14: r.SkipStr16(); r.Skip(1 + 2 + 2 + 2 * 9); break;      // mannequin
            case 15: r.Skip(4); break;                                     // magic egg
            case 16: r.Skip(1); break;                                     // game grave
            case 17: break;                                                // game generator
            case 18: r.Skip(5); break;                                     // xenonite
            case 19: r.Skip(2 * 9); break;                                 // phone booth
            case 20: r.Skip(r.U16()); break;                               // crystal
            case 21: r.SkipStr16(); r.Skip(4 + 1); break;                  // crime in progress
            case 22: break;                                                // spotlight
            case 23: r.Skip(4); break;                                     // display block
            case 24:                                                       // vending machine
            {
                int item  = r.I32();
                int price = r.I32();
                if (item > 0)
                {
                    uint width = Math.Max(1u, w.Width);
                    w.Vends.Add(new VendInfo((int)(index % width), (int)(index / width), item, price));
                }
                break;
            }
            case 25: r.Skip(1); r.Skip(8L * (r.U32() / 2)); break;         // fish tank port
            case 26: r.Skip(5); break;                                     // solar collector
            case 27: r.Skip(4); break;                                     // forge
            case 28: r.Skip(1 + 2 + 2 + 1); break;                         // giving tree
            case 30: r.Skip(1 + 4); break;                                 // steam organ
            case 31:                                                       // silk worm
                r.Skip(1); r.SkipStr16();
                r.Skip(4 + 4 + 4 + 1 + 4 + 4 + 4 + 4);
                break;
            case 32: r.Skip(4L * r.U32()); break;                          // sewing machine
            case 33: if (fg == 3394) r.SkipStr16(); break;                 // country flag
            case 34: break;                                                // lobster trap
            case 35: r.Skip(4); r.SkipStr16(); break;                      // painting easel
            case 36: r.SkipStr16(); r.Skip(4 * 3); break;                  // pet battle cage
            case 37: r.SkipStr16(); { uint n = r.U32(); r.Skip(4); r.Skip(4L * n); } break; // pet trainer
            case 38: r.Skip(4); break;                                     // steam engine
            case 39: r.Skip(4); break;                                     // lock bot
            case 40: r.Skip(4); break;                                     // weather machine
            case 41: r.Skip(4); break;                                     // spirit storage unit
            case 42: r.Skip(17 + 4); break;                                // data bedrock
            case 43: r.Skip(16); break;                                    // shelf
            case 44: r.Skip(1 + 4); r.Skip(4L * r.U32()); break;           // vip entrance
            case 45: break;                                                // challenge timer
            case 47: r.SkipStr16(); r.Skip(4 + 1); break;                  // fish wall mount
            case 48:                                                       // portrait
            {
                r.SkipStr16();
                r.Skip(4 + 4 + 5 + 1 + 2 + 2);
                int hat = r.U16();
                r.Skip(2 + 4);
                if (hat == 12958) r.SkipStr16();
                break;
            }
            case 49: r.Skip(4 + 4 + 1); break;                             // weather machine 2
            case 50: r.Skip(4); break;                                     // fossil prep station
            case 51: break;                                                // dna extractor
            case 52: break;                                                // howler
            case 53: r.Skip(8); break;                                     // chemsynth tank
            case 54: r.Skip(13L * (r.U16() / 13)); break;                  // storage box xtreme
            case 55:                                                       // cooking oven
            {
                r.Skip(4);
                uint n = r.U32();
                r.Skip(8L * (n / 2));
                r.Skip(12);
                break;
            }
            case 56: r.SkipStr16(); r.Skip(4); break;                      // audio rack
            case 57: r.Skip(4); break;                                     // geiger charger
            case 58: break;                                                // adventure begins
            case 59: break;                                                // tomb robber
            case 60: r.Skip(4 + 1); break;                                 // balloon-o-matic
            case 61: r.Skip(4 + 2 + 4 + 4 + 8 + 4 + 4 + 5); break;         // training port
            case 62: r.Skip(4 + 4 + 2 + 4); break;                         // item sucker
            case 63: r.Skip(15L * r.U32()); r.Skip(8); break;              // cybot
            case 65: r.Skip(1 + 4 + 2 + 2 + 4 + 4); break;                 // guild item
            case 66: r.Skip(1); break;                                     // growscan
            case 67: r.Skip(4); r.Skip(4L * r.U32()); break;               // containment field node
            case 68: r.Skip(4); r.SkipStr16(); r.SkipStr16(); r.Skip(4L * r.U32()); break; // spirit board
            case 69: r.Skip(16); break;                                    // tesseract manipulator
            case 72: r.Skip(12); break;                                    // stormy cloud
            case 73: r.Skip(4); break;                                     // temporary platform
            case 74: break;                                                // safe vault
            case 75: { uint state = r.U32(); r.Skip(2); if (state == 2) r.Skip(1); break; } // angelic counting cloud
            case 77: r.Skip(4); r.Skip(4L * r.U32()); break;               // infinity weather machine
            case 79: r.Skip(4); break;                                     // pineapple guzzler
            case 80: r.Skip(1 + 4 + 3); break;                             // kraken's galactic block
            case 81: r.Skip(4 + 2); r.Skip(4L * r.U16()); break;           // friends entrance
            default: return false;
        }
        return true;
    }

    private static bool TryReadObjects(ByteReader r, WorldSnapshot w, out List<DroppedObject> list, out uint lastUid)
    {
        list = new();
        lastUid = 0;
        try
        {
            r.Skip(12);
            uint count = r.U32();
            lastUid    = r.U32();
            if (count > 200_000 || !r.Has((int)Math.Min(int.MaxValue, count * 16L))) return false;
            for (uint i = 0; i < count; i++)
            {
                var o = ReadObject(r);
                if (!IsPlausible(o, w)) return false;
                list.Add(o);
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DroppedObject ReadObject(ByteReader r) => new()
    {
        ItemId = r.U16(),
        X      = r.F32(),
        Y      = r.F32(),
        Amount = r.U8(),
        Flags  = r.U8(),
        Uid    = r.U32(),
    };

    private static bool IsPlausible(DroppedObject o, WorldSnapshot w)
    {
        float maxX = Math.Max(1, w.Width)  * 32f + 64f;
        float maxY = Math.Max(1, w.Height) * 32f + 64f;
        return o.ItemId > 0 && o.Amount > 0
            && float.IsFinite(o.X) && float.IsFinite(o.Y)
            && o.X >= -64f && o.X <= maxX && o.Y >= -64f && o.Y <= maxY;
    }

    // Looks for [count][last uid][count * 16-byte objects] where every object
    // is plausible, uids are unique and none exceeds the last uid.
    private static (List<DroppedObject> Objects, uint LastUid)? FindObjectBlock(byte[] data, int from, int end, WorldSnapshot w)
    {
        (List<DroppedObject>, uint)? best = null;
        int bestCount = 0;

        for (int p = end - 8; p >= from; p--)
        {
            uint count = BitConverter.ToUInt32(data, p);
            if (count == 0 || count > 50_000) continue;
            long blockEnd = p + 8 + count * 16L;
            if (blockEnd > end || end - blockEnd > 4096) continue;
            if (count <= (uint)bestCount) continue;

            uint lastUid = BitConverter.ToUInt32(data, p + 4);
            var r    = new ByteReader(data, p + 8, (int)(count * 16));
            var list = new List<DroppedObject>((int)count);
            var uids = new HashSet<uint>();
            bool ok  = true;
            for (uint i = 0; i < count && ok; i++)
            {
                var o = ReadObject(r);
                ok = IsPlausible(o, w) && o.Uid <= lastUid && uids.Add(o.Uid);
                list.Add(o);
            }
            if (!ok) continue;

            best = (list, lastUid);
            bestCount = (int)count;
        }
        return best;
    }
}
