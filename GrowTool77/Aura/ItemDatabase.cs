using System.IO.Compression;
using System.Text;

namespace GrowTool77.Aura;

public sealed class ItemInfo
{
    public int    Id;
    public string Name      = "";
    public byte   Type;
    public string ExtraFile = "";
    public string Texture   = "";   // e.g. "tiles_page1.rttex" in the game's folder
    public byte   TexX, TexY;       // 32x32 cell of the icon inside that texture
    public byte   SpreadType;       // how multi-tile blocks are drawn (icon cell is still TexX/TexY)
    public int    Rarity;           // 999 = not rarity-ranked in the game
    public ushort Flags;            // item flags (untradeable, etc.)
    public byte   VisualEffect;     // how the game renders it (glow, colour shift, ...)
}

// items.dat reader, versions up to 27. Field order follows the published
// format (StileDevs/grow-items). Only id, name, type and extra file are kept.
public sealed class ItemDatabase
{
    private const string NameKey = "PBG892FXX982ABC*";

    private readonly Dictionary<int, ItemInfo> _items = new();

    public int Version { get; private set; }
    internal int ParsedLength { get; private set; }
    public int Count   => _items.Count;

    public ItemInfo? Get(int id) => _items.TryGetValue(id, out var item) ? item : null;

    public IEnumerable<ItemInfo> All => _items.Values;

    private Dictionary<string, ItemInfo>? _byName;

    // Exact name → item (case-insensitive). For seeds and blocks sharing a name, the lower id wins.
    public ItemInfo? FindByName(string name)
    {
        if (_byName == null)
        {
            var map = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items.Values.OrderBy(i => i.Id))
                if (item.Name.Length > 0) map.TryAdd(item.Name, item);
            _byName = map;
        }
        return _byName.TryGetValue(name.Trim(), out var found) ? found : null;
    }

    // Items whose name starts with the text (then items with a word starting
    // with it), shortest names first. "high" → "High Five", "Highway Sign"…
    public List<ItemInfo> SearchPrefix(string text, int limit = 40)
    {
        text = text.Trim();
        if (text.Length == 0) return new List<ItemInfo>();

        var starts = _items.Values
            .Where(i => i.Name.Length > 0 && i.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name.Length).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        if (starts.Count >= limit) return starts;

        var words = _items.Values
            .Where(i => i.Name.Length > 0 && !i.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                        && i.Name.Split(' ').Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name.Length).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit - starts.Count);
        starts.AddRange(words);
        return starts;
    }

    public string NameOf(int id)
    {
        var item = Get(id);
        return item != null && item.Name.Length > 0 ? item.Name : $"Item #{id}";
    }

    public static ItemDatabase Parse(byte[] data)
    {
        var db = new ItemDatabase();
        var r  = new ByteReader(data);

        db.Version = r.U16();
        int count  = r.I32();
        if (count < 0 || count > 200_000) throw new FormatException("items.dat: bad item count.");
        int v = db.Version;

        for (int n = 0; n < count; n++)
        {
            var item = new ItemInfo { Id = r.I32() };
            item.Flags = r.U16();
            item.Type = r.U8();
            r.Skip(1);                               // material
            item.Name = ReadName(r, item.Id, v);
            item.Texture = r.Str16();                // texture file
            r.Skip(4);                               // texture hash
            item.VisualEffect = r.U8();
            r.Skip(4);                               // cooking time
            item.TexX       = r.U8();
            item.TexY       = r.U8();
            item.SpreadType = r.U8();
            r.Skip(1 + 1 + 1);                       // stripey wallpaper, collision, break hits
            r.Skip(4 + 1);                           // reset state, body part
            item.Rarity = r.U16();
            r.Skip(1);                               // max amount
            item.ExtraFile = r.Str16();
            r.Skip(4 + 4);                           // extra file hash, audio volume
            for (int i = 0; i < 4; i++) r.SkipStr16(); // pet name/prefix/suffix/ability
            r.Skip(4);                               // seed/tree base & overlay
            r.Skip(4 * 5);                           // seed colors, ingredient, grow time, fx flags
            for (int i = 0; i < 3; i++) r.SkipStr16(); // extra options, texture2, extra options2
            r.Skip(4 * 3);                           // unknown ints, flags2
            r.Skip(60);                              // extra bytes
            r.Skip(4 + 4);                           // tile range, vault capacity

            if (v >= 11)
            {
                r.SkipStr16();                       // punch options
                if (v >= 12) r.Skip(4 + 9);          // flags3, body part
                if (v >= 13) r.Skip(4);              // light range
                if (v >= 14) r.Skip(4);
                if (v >= 15) { r.Skip(1 + 4 * 6); r.SkipStr16(); } // chair data + texture
                if (v >= 16) r.SkipStr16();          // item renderer
                if (v >= 17) r.Skip(4);
                if (v >= 18) r.Skip(4);
                if (v >= 19) r.Skip(9);
                if (v >= 21) r.Skip(2);
                if (v >= 22) r.SkipStr16();          // info
                if (v >= 23) r.Skip(4);              // recipe
                if (v >= 24) r.Skip(1);
                if (v >= 25) { r.SkipStr16(); r.Skip(4); } // hit sound + hash
                if (v >= 26) r.Skip(1);
                if (v >= 27) r.SkipStr16();
            }

            db._items[item.Id] = item;
        }
        db.ParsedLength = r.Position;
        return db;
    }

    private static string ReadName(ByteReader r, int id, int version)
    {
        int len = r.U16();
        var raw = r.Bytes(len);
        if (version < 3) return Encoding.Latin1.GetString(raw);

        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)(raw[i] ^ NameKey[(id + i) % NameKey.Length]);
        return new string(chars);
    }

    // The server sends items.dat zlib-compressed inside SEND_ITEM_DATABASE_DATA.
    public static ItemDatabase ParseCompressed(byte[] payload, int offset, int length)
    {
        using var input  = new MemoryStream(payload, offset, length, writable: false);
        using var zlib   = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Parse(output.ToArray());
    }

    // The game keeps its own copy in its cache folder; read it, never write it.
    public static ItemDatabase? TryLoadFromGame(string? gameExePath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameExePath))
        {
            var dir = Path.GetDirectoryName(gameExePath);
            if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "cache", "items.dat"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Growtopia", "cache", "items.dat"));

        foreach (var path in candidates.Distinct())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                return Parse(bytes);
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                // try the next location
            }
        }
        return null;
    }
}
