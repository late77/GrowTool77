// lang: C#, file: FarmFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Auto-farm feature. Punches a list of configured tile positions, detects
// block breaks via the world-drop feed, then places new blocks in a
// humanlike random left→right or right→left sweep.
//
// Safety: stops immediately if the player's world-pixel position moves
// more than the configured threshold (default ≈ 1.5 tiles, 48 px).
// Also stops on world change. Re-starting requires the user to click Start
// or press F (registered globally by MainForm).
using System.Buffers.Binary;
using System.Text;

namespace GrowTool77.Aura;

public sealed class FarmFeature
{
    // ── Configuration (set before Start) ─────────────────────────────
    public int             ToolItemId  { get; set; } = 0;   // 0 = fist/current hand
    public int             BlockItemId { get; set; } = 0;   // block to place
    public float           SafetyPx    { get; set; } = 48f; // move threshold in world-pixels
    public IReadOnlyList<TilePos> Tiles => _tiles;

    // ── State ─────────────────────────────────────────────────────────
    private volatile bool _running;
    private readonly List<TilePos> _tiles = new();

    private float? _safeX, _safeY;
    private int    _currentIdx;
    private bool   _leftToRight = true;
    private bool   _waitingForBreak;
    private DateTime _lastPunch = DateTime.MinValue;

    private const float PunchInterval = 0.34f; // seconds between punches

    private readonly Action<byte[]> _sendToServer;
    private readonly Func<(bool Ok, float X, float Y)> _getPlayer;
    private readonly AuraLog _log;

    public bool IsRunning => _running;
    public event Action<string>? StatusChanged;
    public event Action?         SafetyTriggered; // UI resets start button

    public FarmFeature(Action<byte[]> sendToServer,
                       Func<(bool, float, float)> getPlayer,
                       AuraLog log)
    {
        _sendToServer = sendToServer;
        _getPlayer    = getPlayer;
        _log          = log;
    }

    // ── Tile list management ──────────────────────────────────────────

    public void AddTile(int x, int y)
    {
        if (!_tiles.Any(t => t.X == x && t.Y == y))
            _tiles.Add(new TilePos(x, y));
    }

    public void RemoveTile(int x, int y) =>
        _tiles.RemoveAll(t => t.X == x && t.Y == y);

    public void ClearTiles() => _tiles.Clear();

    // ── Lifecycle ─────────────────────────────────────────────────────

    public void Start()
    {
        if (_tiles.Count == 0) { Fire("No tiles configured"); return; }
        if (BlockItemId == 0)  { Fire("No block item selected"); return; }

        var (ok, px, py) = _getPlayer();
        if (!ok) { Fire("Player position unknown — enter a world first"); return; }

        _safeX        = px;
        _safeY        = py;
        _currentIdx   = 0;
        _leftToRight  = Random.Shared.Next(2) == 0;
        _waitingForBreak = false;
        _running      = true;
        Fire($"Farming {_tiles.Count} tiles");
        _log.Info($"[Farm] Started — {_tiles.Count} tiles, block={BlockItemId}, tool={ToolItemId}");
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire(reason);
        _log.Info($"[Farm] {reason}");
    }

    public void OnWorldChanged()
    {
        if (_running) Stop("World changed — stopped");
    }

    // ── Called periodically from a timer (every ~100 ms) ─────────────

    public void Tick()
    {
        if (!_running) return;

        // Safety check — stop if player moved
        var (ok, px, py) = _getPlayer();
        if (ok && _safeX.HasValue && _safeY.HasValue)
        {
            float dx = px - _safeX.Value;
            float dy = py - _safeY.Value;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > SafetyPx)
            {
                _running = false;
                _log.Info($"[Farm] Safety triggered — moved {dist:0}px from start position");
                SafetyTriggered?.Invoke();
                Fire("Safety triggered — moved from start position");
                return;
            }
        }

        // Rate limit punches
        if ((DateTime.UtcNow - _lastPunch).TotalSeconds < PunchInterval) return;
        _lastPunch = DateTime.UtcNow;

        TilePos tile = CurrentTile();

        // While waiting for break: keep punching current tile
        if (_waitingForBreak)
        {
            SendPunch(tile.X, tile.Y);
            return;
        }

        // Not waiting: punch current tile to start breaking
        SendPunch(tile.X, tile.Y);
        _waitingForBreak = true;
    }

    // Called when the world-drop feed detects a new item at a position.
    // Used to detect block breaks: if an item drops near our current tile → block broke.
    public void OnItemDropped(float dropX, float dropY)
    {
        if (!_running || !_waitingForBreak) return;

        TilePos tile = CurrentTile();
        float tilePixelX = tile.X * 32f + 16f;
        float tilePixelY = tile.Y * 32f + 16f;
        float dx = dropX - tilePixelX;
        float dy = dropY - tilePixelY;

        if (dx * dx + dy * dy > 64f * 64f) return; // too far

        // Block broke — place new block then advance
        _log.Info($"[Farm] Block broke at ({tile.X},{tile.Y}) — placing new");
        _waitingForBreak = false;
        PlaceBlock(tile.X, tile.Y);
        AdvanceTile();
    }

    // ── Packet builders ───────────────────────────────────────────────

    private void SendPunch(int tileX, int tileY)
    {
        GetPlayerPixels(out float px, out float py); // ok=false → use 0,0
        // flags=0x10 matches what GT uses for tile actions (confirmed from fishing cast log)
        var pkt = BuildTilePacket(3, ToolItemId, tileX, tileY, px, py, 0x10);
        _sendToServer(pkt);
        _log.Info($"[Farm] Punch ({tileX},{tileY}) tool={ToolItemId} player=({px:0},{py:0})");
    }

    private void PlaceBlock(int tileX, int tileY)
    {
        GetPlayerPixels(out float px, out float py);
        var pkt = BuildTilePacket(3, BlockItemId, tileX, tileY, px, py, 0x10);
        _sendToServer(pkt);
        _log.Info($"[Farm] Place ({tileX},{tileY}) block={BlockItemId}");
    }

    private static byte[] BuildTilePacket(byte type, int intData,
                                           int tileX, int tileY,
                                           float posX, float posY,
                                           uint flags)
    {
        var p = new byte[4 + 56];
        BinaryPrimitives.WriteInt32LittleEndian(p, NetMessage.GamePacket);
        p[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4 + 12), flags);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 20), intData);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 24), posX);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 28), posY);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 44), tileX);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 48), tileY);
        return p;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private TilePos CurrentTile()
    {
        var ordered = _leftToRight
            ? _tiles.OrderBy(t => t.X).ThenBy(t => t.Y)
            : _tiles.OrderByDescending(t => t.X).ThenBy(t => t.Y);
        var list = ordered.ToList();
        return list[_currentIdx % list.Count];
    }

    private void AdvanceTile()
    {
        _currentIdx++;
        if (_currentIdx >= _tiles.Count)
        {
            _currentIdx  = 0;
            _leftToRight = Random.Shared.Next(2) == 0; // randomize direction each round
            _log.Info("[Farm] Cycle complete — new sweep direction: " + (_leftToRight ? "L→R" : "R→L"));
        }
    }

    private bool GetPlayerPixels(out float px, out float py)
    {
        var (ok, x, y) = _getPlayer();
        px = ok ? x : 0f;
        py = ok ? y : 0f;
        return ok; // caller can check but we always set px/py
    }

    private void Fire(string s) => StatusChanged?.Invoke(s);
}
