// lang: C#, file: ProvidersFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// ATM farm auto-collect with optional Rage Mode.
//
// Normal mode: punches current tile + y±1, up to MaxPunches per tile.
// Rage mode:   sprays every provider tile in a 5×5 grid (player reach) with
//              10ms between each hit, then waits 1 second, repeats.
//              Does NOT track per-tile counts — sprays every cycle.
using System.Buffers.Binary;
using System.Text;

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

    private const int   MaxPunches  = 2;    // punches per tile (2 = lag tolerance without spam)
    private const float PunchMs     = 200f; // 200ms between ticks — matches portal farm fall speed

    private volatile bool _running;
    private bool          _rageMode;
    private readonly HashSet<int>          _providerItemIds = new();
    private          HashSet<long>         _providerTileSet = new();
    private          int                   _totalProviders;
    private readonly Dictionary<long, int> _punchCounts = new();

    // Normal-mode state
    private DateTime _lastNormalPunch = DateTime.MinValue;

    // Rage-mode async loop
    private CancellationTokenSource? _rageCts;

    private readonly Action<byte[]>             _sendToServer;
    private readonly Func<(bool, float, float)> _getPlayer;
    private readonly AuraLog                    _log;

    public bool IsRunning  => _running;
    public int  TotalTiles => _totalProviders;
    public int  DoneTiles  => _punchCounts.Count(kv => kv.Value >= MaxPunches);
    public bool RageMode   => _rageMode;

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
            ? $"Found {count} providers — Start to collect"
            : "No providers in this world");
    }

    // ── Control ──────────────────────────────────────────────────────

    public void Start(bool rageMode = false)
    {
        _punchCounts.Clear();
        _rageMode = rageMode;
        _running  = true;

        if (rageMode)
        {
            Fire($"RAGE MODE — 5×5 grid, 10ms/tile, 1s pause");
            _log.Info("[Providers] Rage mode started.");
            StartRageLoop();
        }
        else
        {
            Fire($"Running — {_totalProviders} providers");
            _log.Info($"[Providers] Normal mode started. {_totalProviders} total.");
        }
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        StopRageLoop();
        Fire($"{reason} — {DoneTiles}/{_totalProviders} done");
        _log.Info($"[Providers] Stopped. {DoneTiles}/{_totalProviders}.");
    }

    // ── Normal mode tick (called every 100ms from farm timer) ─────────

    public void Tick()
    {
        if (!_running || _rageMode) return;
        if ((DateTime.UtcNow - _lastNormalPunch).TotalMilliseconds < PunchMs) return;

        var (ok, px, py) = _getPlayer();
        if (!ok) return;

        int tileX = (int)MathF.Floor(px / 32f);
        int tileY = (int)MathF.Floor(py / 32f);

        // Punch current tile AND y-1 AND y+1 all in the same tick.
        // Portal farm fall speed ≈ 1 tile / 150-200ms. Single-tile-per-tick missed ~45%.
        // Punching 3 tiles per tick ensures full column coverage even during transition.
        bool anyPunched = false;
        for (int dy = 0; dy <= 2; dy++)
        {
            int checkY = tileY + (dy == 0 ? 0 : dy == 1 ? -1 : 1);
            if (checkY < 0) continue;

            long key = TileKey(tileX, checkY);
            if (!_providerTileSet.Contains(key)) continue;

            int count = _punchCounts.GetValueOrDefault(key, 0);
            if (count >= MaxPunches) continue;

            _punchCounts[key] = count + 1;
            SendPunch(tileX, checkY, px, py);
            anyPunched = true;
        }

        if (anyPunched)
        {
            _lastNormalPunch = DateTime.UtcNow;
            int done = DoneTiles;
            if (done >= _totalProviders && _totalProviders > 0)
            {
                Fire($"All {_totalProviders} collected!");
                _log.Info("[Providers] Session complete — resetting for next circuit.");
                _punchCounts.Clear();
            }
            else Fire($"({tileX},{tileY}) — {done}/{_totalProviders}");
        }
    }

    // ── Rage mode ─────────────────────────────────────────────────────

    private void StartRageLoop()
    {
        _rageCts?.Cancel();
        _rageCts = new CancellationTokenSource();
        _ = RageLoopAsync(_rageCts.Token);
    }

    private void StopRageLoop()
    {
        _rageCts?.Cancel();
        _rageCts = null;
    }

    private async Task RageLoopAsync(CancellationToken ct)
    {
        _log.Info("[Providers] Rage loop started.");
        int cycle = 0;

        while (_running && _rageMode && !ct.IsCancellationRequested)
        {
            cycle++;
            var (ok, px, py) = _getPlayer();
            if (!ok)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            int tileX = (int)MathF.Floor(px / 32f);
            int tileY = (int)MathF.Floor(py / 32f);

            int punched = 0;

            // 5×5 grid centred on player — punch every provider tile
            for (int dy = -2; dy <= 2 && !ct.IsCancellationRequested; dy++)
            {
                for (int dx = -2; dx <= 2 && !ct.IsCancellationRequested; dx++)
                {
                    int cx = tileX + dx;
                    int cy = tileY + dy;
                    if (cx < 0 || cy < 0) continue;

                    long key = TileKey(cx, cy);
                    if (!_providerTileSet.Contains(key)) continue;

                    SendPunch(cx, cy, px, py);
                    punched++;

                    // 200ms between hits — document says 150-200ms minimum safe rate
                    // (rage mode is still faster coverage than normal but within safe limits)
                    try { await Task.Delay(200, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            Fire($"Rage cycle {cycle}: {punched} hits in 5×5 — pausing 1s");
            _log.Info($"[Providers] Rage #{cycle}: {punched} hits.");

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        _log.Info("[Providers] Rage loop ended.");
    }

    // ── Packet ────────────────────────────────────────────────────────

    private void SendPunch(int tileX, int tileY, float px, float py)
    {
        // Binary TankPacket (outer type=4, inner type=3 PACKET_TILE_CHANGE_REQUEST)
        // with intData=18 (Fist) and flags=0x10.
        // This is identical to the fishing rod cast format which works reliably.
        // Text-based "action|punch\nx|Y\ny|Y" collected 0 — binary collected ~30k.
        var p = new byte[4 + 56];
        BinaryPrimitives.WriteInt32LittleEndian(p, NetMessage.GamePacket);
        p[4] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4 + 12), 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 20), 18);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 24), px);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 28), py);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 44), tileX);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 48), tileY);
        _sendToServer(p);
    }

    private static long TileKey(int x, int y) => ((long)x << 20) | (uint)y;
}
