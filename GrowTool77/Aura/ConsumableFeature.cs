// lang: C#, file: ConsumableFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Opens consumable packs (SSP etc.) by sending item-use packet on self.
// Auto-drops seeds when a stack hits 200.
using System.Buffers.Binary;

namespace GrowTool77.Aura;

public sealed class ConsumableFeature
{
    // Known consumable items
    public static readonly (string Label, int ItemId)[] KnownItems =
    {
        ("Small Seed Pack (SSP)",       5706),
        ("Small Seed Pack Collection",  5707),
        ("Rare Seed Pack",              5708),
    };

    private volatile bool _running;
    private int    _itemId;
    private int    _targetCount;
    private int    _openedCount;
    private int    _dropCount;

    // Seed tracking: itemId → count received this session
    private readonly Dictionary<int, int> _seedCounts = new();

    // Pending drop
    private int  _pendingDropItem;
    private bool _waitingForDropDialog;

    // Pause when inventory has no SSPs — resume when type=13 delivers SSP
    private bool _paused;
    private DateTime _lastSspReceived = DateTime.MinValue;

    private const int  StackMax  = 200;
    private const int  IntervalMs = 500; // ms between each pack open
    private DateTime   _lastOpen  = DateTime.MinValue;

    private readonly Action<byte[]>             _sendToServer;
    private readonly Func<(bool, float, float)> _getPlayer;
    private readonly Func<int>                  _getPlayerNetId;
    private readonly AuraLog                    _log;

    public bool IsRunning   => _running;
    public int  OpenedCount => _openedCount;
    public int  DropCount   => _dropCount;

    public event Action<string>? StatusChanged;
    private void Fire(string s) => StatusChanged?.Invoke(s);

    public ConsumableFeature(Action<byte[]> sendToServer,
                              Func<(bool, float, float)> getPlayer,
                              Func<int> getPlayerNetId,
                              AuraLog log)
    {
        _sendToServer   = sendToServer;
        _getPlayer      = getPlayer;
        _getPlayerNetId = getPlayerNetId;
        _log            = log;
    }

    // ── Control ───────────────────────────────────────────────────────

    public void Start(int itemId, int count)
    {
        _itemId       = itemId;
        _targetCount  = count;
        _openedCount  = 0;
        _dropCount    = 0;
        _waitingForDropDialog = false;
        _seedCounts.Clear();
        _paused          = false;
        _lastSspReceived = DateTime.UtcNow; // assume we have some SSPs at start
        _running         = true;
        var name = KnownItems.FirstOrDefault(k => k.ItemId == itemId).Label ?? $"item {itemId}";
        Fire($"Opening {count}× {name}…");
        _log.Info($"[Consume] Start. {count}× itemId={itemId}");
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire($"{reason} — {_openedCount} opened, {_dropCount} drops");
        _log.Info($"[Consume] {reason}. opened={_openedCount} drops={_dropCount}");
    }

    // ── Tick (called ~every 100ms from farm timer) ─────────────────

    public void Tick()
    {
        if (!_running || _waitingForDropDialog) return;
        if (_openedCount >= _targetCount) { Stop("Done!"); return; }
        if ((DateTime.UtcNow - _lastOpen).TotalMilliseconds < IntervalMs) return;

        // Pause if no SSP received recently (inventory empty — user picking up from ground)
        if (_lastSspReceived != DateTime.MinValue
            && (DateTime.UtcNow - _lastSspReceived).TotalSeconds > 3
            && !_paused)
        {
            _paused = true;
            Fire($"Paused — waiting for SSPs (#{_openedCount}/{_targetCount}, drops: {_dropCount})");
            _log.Info("[Consume] Paused — inventory empty, waiting for SSP pickup");
            return;
        }
        if (_paused) return;

        var (ok, px, py) = _getPlayer();
        if (!ok) return;

        _lastOpen = DateTime.UtcNow;
        SendItemOnSelf(px, py);
        _openedCount++;
        Fire($"Opened #{_openedCount}/{_targetCount} — drops: {_dropCount}");
        _log.Info($"[Consume] → open #{_openedCount}/{_targetCount}");
    }

    // ── Server responses ──────────────────────────────────────────

    // Called when server sends type=13 (item received)
    public void OnItemReceived(int itemId)
    {
        if (!_running) return;

        // SSP picked up from ground → unpause
        // SSP picked up from ground → unpause
        if (itemId == _itemId)
        {
            _lastSspReceived = DateTime.UtcNow;
            if (_paused)
            {
                _paused = false;
                Fire($"Resumed — SSPs picked up (#{_openedCount}/{_targetCount})");
                _log.Info("[Consume] Resumed after SSP pickup");
            }
            return;
        }

        // Track seed counts — any item received while opening packs is a seed
        if (!_seedCounts.TryGetValue(itemId, out int cnt)) cnt = 0;
        cnt++;
        _seedCounts[itemId] = cnt;
        _log.Info($"[Consume] item {itemId}: {cnt}/200");

        if (cnt >= StackMax && !_waitingForDropDialog)
        {
            _waitingForDropDialog = true;
            _pendingDropItem = itemId;
            _log.Info($"[Consume] item {itemId} full → dropping {StackMax}");
            Task.Delay(100).ContinueWith(_ => SendDropRequest(itemId));
        }
    }

    // Called when server sends OnDialogRequest with drop_item dialog
    public bool OnDropDialog(string body)
    {
        if (!_running || !_waitingForDropDialog) return false;
        var m = System.Text.RegularExpressions.Regex.Match(body, @"itemID\|(\d+)");
        if (!m.Success) return false;
        int id = int.Parse(m.Groups[1].Value);
        if (id != _pendingDropItem) return false;

        // Auto-confirm drop
        SendGenericText($"action|dialog_return\ndialog_name|drop_item\nitemID|{id}|\ncount|{StackMax}|\n");
        _seedCounts[id] = 0; // reset counter for this seed
        _dropCount++;
        _waitingForDropDialog = false;
        _log.Info($"[Consume] Dropped {StackMax}× item {id}. total drops={_dropCount}");
        Fire($"Opened #{_openedCount}/{_targetCount} — dropped {_dropCount} stacks");
        return true;
    }

    // ── Packet builders ───────────────────────────────────────────

    private void SendItemOnSelf(float px, float py)
    {
        // Same format as fishing rod cast (type=3 PACKET_TILE_CHANGE_REQUEST):
        // intData = item being used, intX/intY = target tile (player's own tile)
        int tileX = (int)(px / 32f);
        int tileY = (int)(py / 32f);

        var p = new byte[4 + 56];
        BinaryPrimitives.WriteInt32LittleEndian(p, NetMessage.GamePacket);
        p[4] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4 + 12), 0x10); // item-use flag
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 20), _itemId);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 24), px);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4 + 28), py);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 44), tileX);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + 48), tileY);
        _sendToServer(p);
    }

    private void SendDropRequest(int itemId)
    {
        if (!_running) return;
        SendGenericText($"action|drop||itemID|{itemId}|");
    }

    private void SendGenericText(string text)
    {
        var body   = System.Text.Encoding.Latin1.GetBytes(text + "\0");
        var packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GameMessage);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        _sendToServer(packet);
    }
}
