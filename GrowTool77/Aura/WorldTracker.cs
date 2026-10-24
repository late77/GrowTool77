namespace GrowTool77.Aura;

public readonly record struct TilePos(int X, int Y);

// Dropped items of one kind: total amount, number of stacks, and the tiles they lie on.
public sealed record ScanEntry(int ItemId, string Name, int Amount, int Stacks, IReadOnlyList<TilePos> Tiles);

// Blocks of one kind placed in the world (foreground or background layer).
public sealed record PlacedEntry(int ItemId, string Name, int Count, bool Background, IReadOnlyList<TilePos> Tiles);

// A door tile with its linked destination world name and (optionally) its own ID.
// OwnId is blank until the server returns it via a wrench packet response.
public sealed record DoorInfo(int X, int Y, int FgItemId, string Destination, string OwnId = "");

// Vending machines seen in one world during this session (memory only).
public sealed record WorldVends(string World, DateTime Seen, IReadOnlyList<VendInfo> Vends, bool Partial);

public sealed record WorldView(
    string Name,
    IReadOnlyList<ScanEntry> Items,
    int Gems,
    int TotalItems,
    bool Exact,
    string? Note,
    DateTime Updated,
    int Width,
    int Height,
    IReadOnlyList<PlacedEntry> Placed,
    bool PlacedComplete,
    DateTime Entered,
    ushort[] Fg,
    ushort[] Bg,
    IReadOnlyList<DoorInfo> Doors);

// Keeps the current world and its dropped objects up to date.
// Lives on the [AURA] network thread; hands immutable WorldView copies to the UI.
internal sealed class WorldTracker
{
    public const int GemsItemId = 112;

    private readonly Dictionary<uint, DroppedObject> _objects = new();
    private readonly Dictionary<string, WorldVends> _vendsByWorld = new(StringComparer.OrdinalIgnoreCase);
    private string? _name;
    private uint    _lastUid;
    private uint    _entryLastUid;      // drops with a higher id were numbered by us after entering
    private int     _width, _height;
    private DateTime _entered;
    private bool    _placedComplete;
    private List<(int ItemId, bool Background, List<TilePos> Tiles)> _placed = new();
    private List<DoorInfo> _doors = new();
    private ushort[] _fg = Array.Empty<ushort>(), _bg = Array.Empty<ushort>();

    // Set by the proxy from the game's own position updates (world pixels).
    public float? PlayerX, PlayerY;
    public int    OwnNetId = int.MinValue;

    // How often our drop numbering was corrected to the server's (diagnostics).
    public int ResyncCount { get; private set; }

    // Pickups the game itself asked for (PACKET_ITEM_ACTIVATE_OBJECT_REQUEST),
    // waiting for the server's answer.
    private sealed class PendingPickup { public uint Uid; public DateTime At; public bool Resolved; }
    private readonly List<PendingPickup> _pending = new();
    private readonly Dictionary<uint, DateTime> _recentlyRemoved = new();
    private bool    _exact;
    private string? _note;

    public ItemDatabase? Items { get; set; }
    public string? CurrentWorld => _name;

    public uint LastUid => _lastUid;
    public bool HasObject(uint uid) => _objects.ContainsKey(uid);
    public int  ObjectCount => _objects.Count;

    // Every world entered this session; never written to disk.
    public IReadOnlyList<WorldVends> VendSnapshot() => _vendsByWorld.Values.ToList();

    // Called when the server returns a door's own ID from a wrench packet response.
    public void SetDoorId(int tileX, int tileY, string ownId)
    {
        for (int i = 0; i < _doors.Count; i++)
        {
            if (_doors[i].X == tileX && _doors[i].Y == tileY)
            {
                _doors[i] = _doors[i] with { OwnId = ownId };
                return;
            }
        }
    }

    public void Enter(WorldSnapshot snapshot)
    {
        _vendsByWorld[snapshot.Name] = new WorldVends(snapshot.Name, DateTime.Now, snapshot.Vends.ToList(), !snapshot.TilesParsed);
        _objects.Clear();
        foreach (var o in snapshot.Objects) _objects[o.Uid] = o;
        _name    = snapshot.Name;
        _lastUid = snapshot.LastObjectUid;
        _entryLastUid = snapshot.LastObjectUid;
        _exact   = snapshot.TilesParsed;
        _note    = snapshot.Note;
        _width   = (int)snapshot.Width;
        _height  = (int)snapshot.Height;
        _entered = DateTime.Now;
        _placed  = BuildPlaced(snapshot);
        _doors   = snapshot.Doors ?? new();
        _fg      = snapshot.Fg;
        _bg      = snapshot.Bg;
        _placedComplete = snapshot.Fg.Length > 0 && snapshot.TilesRead >= snapshot.Fg.Length;
        PlayerX = PlayerY = null;
    }

    private static List<(int, bool, List<TilePos>)> BuildPlaced(WorldSnapshot snap)
    {
        var map = new Dictionary<(int, bool), List<TilePos>>();
        int width = (int)Math.Max(1u, snap.Width);
        int count = Math.Min(snap.TilesRead, snap.Fg.Length);
        for (int i = 0; i < count; i++)
        {
            var pos = new TilePos(i % width, i / width);
            if (snap.Fg[i] != 0) Add(map, (snap.Fg[i], false), pos);
            if (snap.Bg[i] != 0) Add(map, (snap.Bg[i], true), pos);
        }
        return map.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

        static void Add(Dictionary<(int, bool), List<TilePos>> m, (int, bool) key, TilePos pos)
        {
            if (!m.TryGetValue(key, out var list)) m[key] = list = new List<TilePos>();
            list.Add(pos);
        }
    }

    public void Leave()
    {
        _objects.Clear();
        _placed = new();
        _pending.Clear();
        _recentlyRemoved.Clear();
        _name = null;
        _note = null;
        PlayerX = PlayerY = null;
    }

    // PACKET_ITEM_CHANGE_OBJECT: net id -1 = new drop, -3 = drop changed,
    // anything else = the drop with uid ItemNetId was picked up.
    public bool Apply(in GameUpdate gu)
    {
        if (_name == null) return false;

        if (gu.NetId == -1)
        {
            uint uid = ++_lastUid;
            _objects[uid] = new DroppedObject
            {
                Uid    = uid,
                ItemId = gu.IntData,
                X      = gu.PosX,
                Y      = gu.PosY,
                Amount = DecodeAmount(gu),
            };
            return true;
        }

        uint target = unchecked((uint)gu.ItemNetId);

        // The server refers to drops by an id that both the game and [AURA]
        // count up from the world's "last drop id". If that starting value was
        // read differently, every drop we added is off by the same amount and
        // the pickup misses. Find the drop being picked up by position instead,
        // then shift our numbering so later pickups match directly.
        if (!_objects.ContainsKey(target))
        {
            if (WasJustRemoved(target)) return false;          // already handled (e.g. via "Collected")
            if (gu.PosX == 0 && gu.PosY == 0) return false;
            var near = Nearest(gu.PosX, gu.PosY, 24f, gu.NetId == -3 ? gu.IntData : 0);
            if (near == null) return false;
            Resync(near, target);
        }

        foreach (var p in _pending) if (p.Uid == target) p.Resolved = true;

        if (gu.NetId == -3)
        {
            // A change to an existing drop (partial pickup, merge, etc.).
            if (!_objects.TryGetValue(target, out var obj)) return false;
            int amount = DecodeAmount(gu);
            // Growtopia uses a change-to-zero (or negative) as "it's gone".
            if (amount <= 0 || gu.RawAt16 == 0)
            {
                Remove(target);
                return true;
            }
            obj.Amount = amount;
            if (gu.IntData > 0) obj.ItemId = gu.IntData;
            return true;
        }

        // Any other change type (net id is a player id): the drop was collected.
        return Remove(target);
    }

    // PACKET_ITEM_ACTIVATE_OBJECT_REQUEST from the game: the drop's id (the
    // server's own numbering) and its exact position. If our id for the drop
    // at that position differs, re-key it now — before the server answers.
    public string NotePickupRequest(int uid, float x, float y)
    {
        if (_name == null || uid <= 0) return "ignored";
        Expire();
        uint id = (uint)uid;
        string result;
        if (_objects.ContainsKey(id))
        {
            result = "id matches";
        }
        else
        {
            var near = Nearest(x, y, 24f, 0);
            if (near == null) result = "no known drop at that position";
            else
            {
                uint before = near.Uid;
                Resync(near, id);
                result = $"renumbered {before} -> {id}";
            }
        }
        _pending.Add(new PendingPickup { Uid = id, At = DateTime.UtcNow });
        return result;
    }

    // The server's "Collected N <item>" message after our own pickup. If the
    // server's removal for that drop wasn't seen, apply the pickup from this.
    public string NoteCollected(int amount, int itemId)
    {
        if (_name == null) return "ignored";
        Expire();
        var p = _pending.FirstOrDefault(x => !x.Resolved
                                             && _objects.TryGetValue(x.Uid, out var o) && o.ItemId == itemId);
        if (p == null) return "already applied";
        p.Resolved = true;
        var obj = _objects[p.Uid];
        obj.Amount -= amount;
        if (obj.Amount > 0) return $"reduced to {obj.Amount}";
        Remove(p.Uid);
        return "removed";
    }

    private bool Remove(uint uid)
    {
        if (!_objects.Remove(uid)) return false;
        _recentlyRemoved[uid] = DateTime.UtcNow;
        return true;
    }

    private bool WasJustRemoved(uint uid) =>
        _recentlyRemoved.TryGetValue(uid, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(10);

    private void Expire()
    {
        var now = DateTime.UtcNow;
        _pending.RemoveAll(p => now - p.At > TimeSpan.FromSeconds(5));
        foreach (var key in _recentlyRemoved.Where(kv => now - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList())
            _recentlyRemoved.Remove(key);
    }

    // Nearest drop to a drop position (both in world pixels), optionally of one item.
    private DroppedObject? Nearest(float x, float y, float maxDistance, int itemId)
    {
        DroppedObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (var o in _objects.Values)
        {
            if (itemId > 0 && o.ItemId != itemId) continue;
            float dx = o.X - x, dy = o.Y - y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestDistance) { bestDistance = d; best = o; }
        }
        return bestDistance <= maxDistance ? best : null;
    }

    private void Resync(DroppedObject match, uint serverUid)
    {
        long delta = (long)serverUid - match.Uid;
        if (delta == 0) return;

        if (match.Uid > _entryLastUid)
        {
            // One of ours: the whole series we numbered is shifted by delta.
            var own = _objects.Values.Where(o => o.Uid > _entryLastUid).ToList();
            foreach (var o in own) _objects.Remove(o.Uid);
            foreach (var o in own)
            {
                long shifted = o.Uid + delta;
                if (shifted <= 0 || _objects.ContainsKey((uint)shifted)) continue;
                o.Uid = (uint)shifted;
                _objects[o.Uid] = o;
            }
            _lastUid      = (uint)Math.Max(0, _lastUid + delta);
            _entryLastUid = (uint)Math.Max(0, _entryLastUid + delta);
        }
        else
        {
            _objects.Remove(match.Uid);
            match.Uid = serverUid;
            _objects[serverUid] = match;
        }
        ResyncCount++;
    }

    // Sources disagree on whether the amount is a float or an integer at
    // offset 16, so accept whichever reading gives a sane whole number.
    internal static int DecodeAmount(in GameUpdate gu)
    {
        float f = gu.FloatVar;
        if (float.IsFinite(f) && f >= 1f && f <= 65535f && f == MathF.Floor(f)) return (int)f;
        if (gu.RawAt16 is >= 1 and <= 65535) return gu.RawAt16;
        return 1;
    }

    public WorldView? Snapshot()
    {
        if (_name == null) return null;

        var groups = _objects.Values
            .GroupBy(o => o.ItemId)
            .Select(g => new ScanEntry(
                g.Key,
                g.Key == GemsItemId ? "Gems" : Items?.NameOf(g.Key) ?? $"Item #{g.Key}",
                g.Sum(o => o.Amount),
                g.Count(),
                g.Select(o => new TilePos((int)MathF.Floor((o.X + 8) / 32f), (int)MathF.Floor((o.Y + 8) / 32f)))
                 .Distinct().ToList()))
            .OrderByDescending(e => e.Amount)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int gems  = groups.Where(e => e.ItemId == GemsItemId).Sum(e => e.Amount);
        int total = groups.Where(e => e.ItemId != GemsItemId).Sum(e => e.Amount);
        string? note = _note;
        if (Items == null) note = (note == null ? "" : note + "; ") + "item names unavailable until items.dat is loaded";

        var placed = _placed
            .Select(p => new PlacedEntry(p.ItemId, Items?.NameOf(p.ItemId) ?? $"Item #{p.ItemId}",
                                         p.Tiles.Count, p.Background, p.Tiles))
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorldView(_name, groups, gems, total, _exact, note, DateTime.Now,
                             _width, _height, placed, _placedComplete, _entered, _fg, _bg,
                             _doors.ToList());
    }
}

public sealed record VendHit(string World, int X, int Y, int ItemId, int Price, double WlPerItem, bool PartialWorld);

// Finds every vending machine selling an item in the worlds seen this session,
// cheapest (in World Locks per item) first.
public static class VendSearch
{
    public static List<VendHit> Find(IReadOnlyList<WorldVends> worlds, int itemId) =>
        worlds.SelectMany(w => w.Vends
                  .Where(v => v.ItemId == itemId && v.IsForSale)
                  .Select(v => new VendHit(w.World, v.X, v.Y, v.ItemId, v.Price, v.WlPerItem, w.Partial)))
              .OrderBy(h => h.WlPerItem)
              .ThenBy(h => h.World, StringComparer.OrdinalIgnoreCase)
              .ToList();

    public static string PriceText(int price) =>
        price > 0 ? $"{price:N0} WL each" :
        price < 0 ? $"{-price:N0} for 1 WL" : "not priced";

    public static string PerItemText(double wlPerItem) =>
        double.IsInfinity(wlPerItem) ? "—" :
        wlPerItem >= 1 ? $"{wlPerItem:0.##} WL" : $"{wlPerItem:0.####} WL";
}
