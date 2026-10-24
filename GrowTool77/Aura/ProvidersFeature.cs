// lang: C#, file: ProvidersFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Auto-collect provider blocks in portal ATM farm worlds.
// Punches current tile + y-1 + y+1 to catch tiles when player transitions between rows.
using System.Buffers.Binary;

namespace GrowTool77.Aura;

public sealed class ProvidersFeature
{
    private static readonly string[] KnownProviderNames =
    {
        "ATM Machine", "BenBarrage's Awesome Item-O-Matic", "OldGertie's Awesome Item-O-Matic",
        "Keenan GT's Awesome Item-O-Matic", "MrSongo GT's Awesome Item-O-Matic",
        "evets_gt's Awesome Item-O-Matic", "VenomST's Awesome Item-O-Matic",
        "macproof_gt's Awesome Item-O-Matic", "LinkTrader GT's Awesome Item-O-Matic",
        "Tery's Awesome Item-O-Matic",
    };

    private const int   MaxPunches = 4;   // punches per tile for lag tolerance
    private const float PunchMs    = 70f; // ms between punch packets

    private volatile bool          _running;
    private readonly HashSet<int>  _providerItemIds = new();
    private HashSet<long>          _providerTileSet = new();
    private int                    _totalProviders;
    private readonly Dictionary<long, int> _punchCounts = new();

    private DateTime _lastPunch = DateTime.MinValue;

    private readonly Action<byte[]>             _sendToServer;
    private readonly Func<(bool, float, float)> _getPlayer;
    private readonly AuraLog                    _log;

    public bool IsRunning  => _running;
    public int  TotalTiles => _totalProviders;
    public int  DoneTiles  => _punchCounts.Count(kv => kv.Value >= MaxPunches);

    public event Action<string>? StatusChanged;
    private void Fire(string s) => StatusChanged?.Invoke(s);

    public ProvidersFeature(Action<byte[]> sendToServer,
                             Func<(bool, float, float)> getPlayer,
                             AuraLog log)
    {
        _sendToServer = sendToServer;
        _getPlayer    = getPlayer;
        _log          = log;
    }

    public void LoadProviderIds(ItemDatabase? db)
    {
        _providerItemIds.Clear();
        if (db == null) return;
        foreach (var name in KnownProviderNames)
        {
            var item = db.FindByName(name);
            if (item != null) _providerItemIds.Add(item.Id);
        }
        _log.Info($"[Providers] {_providerItemIds.Count} provider IDs loaded.");
    }

    public void OnWorldChanged(WorldView? world)
    {
        if (_running) Stop("World changed");
        _punchCounts.Clear();
        _providerTileSet = new HashSet<long>();
        _totalProviders  = 0;

        if (world == null || _providerItemIds.Count == 0) return;
        int w = world.Width, h = world.Height;
        if (w <= 0 || h <= 0 || world.Fg?.Length < w * h) return;

        int count = 0;
        for (int ty = 0; ty < h; ty++)
        for (int tx = 0; tx < w; tx++)
        {
            if (_providerItemIds.Contains(world.Fg![ty * w + tx]))
            {
                _providerTileSet.Add(TileKey(tx, ty));
                count++;
            }
        }

        _totalProviders = count;
        _log.Info($"[Providers] {count} provider tiles in {world.Name}.");
        Fire(count > 0
            ? $"Found {count} providers — Start, portals move you"
            : "No providers in this world");
    }

    public void Start()
    {
        _punchCounts.Clear();
        _running = true;
        Fire($"Running — {_totalProviders} providers, {MaxPunches} punches each");
        _log.Info($"[Providers] Started. {_totalProviders} total.");
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire($"{reason} — {DoneTiles}/{_totalProviders} fully punched");
        _log.Info($"[Providers] Stopped. {DoneTiles}/{_totalProviders} done.");
    }

    // Called every ~100ms from farm timer.
    public void Tick()
    {
        if (!_running) return;
        if ((DateTime.UtcNow - _lastPunch).TotalMilliseconds < PunchMs) return;

        var (ok, px, py) = _getPlayer();
        if (!ok) return;

        int tileX = (int)MathF.Floor(px / 32f);
        int tileY = (int)MathF.Floor(py / 32f);

        // Check current tile AND one row above/below — player may be mid-transition.
        // The first tile that is a provider and still needs punching gets hit this tick.
        for (int dy = 0; dy <= 2; dy++)
        {
            int checkY = tileY + (dy == 0 ? 0 : dy == 1 ? -1 : 1); // 0, -1, +1
            if (checkY < 0) continue;

            long key = TileKey(tileX, checkY);
            if (!_providerTileSet.Contains(key)) continue;

            int count = _punchCounts.GetValueOrDefault(key, 0);
            if (count >= MaxPunches) continue;

            _punchCounts[key] = count + 1;
            _lastPunch        = DateTime.UtcNow;

            float punchY = checkY * 32f + 16f; // punch at tile center Y
            SendPunch(tileX, checkY, px, punchY);

            int done = DoneTiles;
            Fire($"({tileX},{checkY}) ×{count + 1}/{MaxPunches} — {done}/{_totalProviders}");
            return; // one punch per tick
        }

        Fire($"{DoneTiles}/{_totalProviders} done — walk to next area");
    }

    private void SendPunch(int tileX, int tileY, float px, float py)
    {
        var p = new byte[4 + 56];
        BinaryPrimitives.WriteInt32LittleEndian(p, NetMessage.GamePacket);
        p[4] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4 + 12), 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 20), 18);        // Fist
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 24), px);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 28), py);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 44), tileX);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 48), tileY);
        _sendToServer(p);
    }

    private static long TileKey(int x, int y) => ((long)x << 20) | (uint)y;
}
