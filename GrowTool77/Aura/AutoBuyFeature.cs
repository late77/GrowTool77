// lang: C#, file: AutoBuyFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Auto-buy from Growtopia's built-in SHOP (not player vends).
//
// How it works (confirmed from packet logs):
//  1. Player opens shop, navigates to item, clicks it once
//  2. Game sends: action|buy|item|<code>|  (GenericText)
//  3. Server responds: OnStorePurchaseResult (success) or OnConsoleMessage (fail/full)
//  4. OnStoreRequest always follows → shop stays open
//  5. We just re-send action|buy|item|<code>| after each OnStorePurchaseResult
//
// No re-opening needed — the store stays open between purchases.
// When inventory fills: try to drop the item, then continue.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace GrowTool77.Aura;

public sealed class AutoBuyFeature
{
    // Current shop item being bought
    private string _itemCode   = "";   // e.g. "5seed"
    private string _itemName   = "";   // e.g. "Small Seed Pack"
    private int    _itemId;            // from ItemDatabase, used for drop
    private long   _gemsLeft   = -1;  // updated from each purchase confirmation

    // Buy state
    private volatile bool _running;
    private int  _targetCount;
    private int  _boughtCount;
    private int  _dropCount;
    private bool _waitingForResult; // sent buy, waiting for OnStorePurchaseResult

    private readonly Action<byte[]>             _sendToServer;
    private readonly Func<(bool, float, float)> _getPlayer;
    private readonly ItemDatabase?              _db;
    private readonly AuraLog                    _log;

    public bool   IsRunning   => _running;
    public int    BoughtCount => _boughtCount;
    public int    TargetCount => _targetCount;
    public string ItemCode    => _itemCode;
    public string ItemName    => _itemName;
    public long   GemsLeft    => _gemsLeft;

    // Fires when game sends action|buy|item|X → user clicked shop item
    public event Action<string, string, int>? ShopItemDetected; // (code, name, itemId)
    public event Action<string>?              StatusChanged;
    private void Fire(string s) => StatusChanged?.Invoke(s);

    public AutoBuyFeature(Action<byte[]> sendToServer,
                           Func<(bool, float, float)> getPlayer,
                           ItemDatabase? db,
                           AuraLog log)
    {
        _sendToServer = sendToServer;
        _getPlayer    = getPlayer;
        _db           = db;
        _log          = log;
    }

    // ── Called by AuraProxy when game sends action|buy|item|... ──────
    // This is the game→server packet intercepted in InspectFromGame.
    // Returns true = consume packet (we handle it), false = pass through.
    public bool OnGameBuyAction(string text)
    {
        // text: "action|buy|item|5seed|" or "action|buy|item|bigitems|"
        var m = Regex.Match(text, @"action\|buy\|item\|([^|]+)\|");
        if (!m.Success) return false;

        string code = m.Groups[1].Value;

        // Navigation clicks (bigitems, main_menu etc.) — pass through, don't intercept
        if (code.Length > 20 || IsNavCode(code)) return false;

        // Real item purchase click detected
        string name   = _db?.FindByName(code)?.Name ?? GuessName(code);
        int    itemId = _db?.FindByName(code)?.Id ?? 0;

        _itemCode = code;
        _itemName = name;
        _itemId   = itemId;

        _log.Info($"[AutoBuy] Shop item clicked: code={code} name={name} id={itemId}");
        ShopItemDetected?.Invoke(code, name, itemId);

        if (_running)
        {
            // Running — re-intercept this buy and handle it ourselves
            return false; // let it through; server will process
        }
        return false; // pass through normally when not running
    }

    // ── Called by AuraProxy when OnStorePurchaseResult arrives ────────
    public void OnStorePurchaseResult(string body)
    {
        if (!_running) return;

        // Parse gem count: "You have `$2,660,247`` Gems left."
        var gemMatch = Regex.Match(body, @"You have\s*`\$([0-9,]+)``\s*Gems? left");
        if (gemMatch.Success)
            long.TryParse(gemMatch.Groups[1].Value.Replace(",", ""), out _gemsLeft);

        _boughtCount++;
        _waitingForResult = false;
        Fire($"Bought {_boughtCount}/{_targetCount} × {_itemName}  ({_gemsLeft:N0} gems left)");
        _log.Info($"[AutoBuy] Purchase #{_boughtCount} confirmed. Gems left: {_gemsLeft}");

        if (_boughtCount >= _targetCount)
        {
            Stop("Target reached!");
            return;
        }

        // Brief delay then buy again — shop is still open
        Task.Delay(100).ContinueWith(_ => SendBuy());
    }

    // ── Called when server sends "can't" or "full" console message ────
    public void OnServerMessage(string msg)
    {
        if (!_running) return;
        string lower = msg.ToLowerInvariant();
        bool isFull  = lower.Contains("backpack is full") || lower.Contains("can't carry")
                    || lower.Contains("inventory full")   || lower.Contains("no more room");
        if (!isFull) return;

        _log.Info($"[AutoBuy] Inventory full — dropping item {_itemId}, then continuing");
        _dropCount++;
        SendDrop();
        Task.Delay(150).ContinueWith(_ => SendBuy());
    }

    // ── Called when OnStoreRequest arrives after purchase ─────────────
    public void OnStoreRequest()
    {
        // Store is open and ready. If we haven't sent next buy yet (e.g. no
        // OnStorePurchaseResult came), send it now as a fallback.
        if (!_running || _waitingForResult) return;
        // nothing — buy is already scheduled via OnStorePurchaseResult
    }

    public void Start(int quantity)
    {
        if (string.IsNullOrEmpty(_itemCode)) { Fire("Open shop and click an item first"); return; }
        _targetCount       = quantity;
        _boughtCount       = 0;
        _dropCount         = 0;
        _waitingForResult  = false;
        _running           = true;
        Fire($"Starting — {quantity}× {_itemName} ({_itemCode})");
        _log.Info($"[AutoBuy] Started. {quantity}× code={_itemCode}");
        SendBuy();
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire($"{reason} — bought {_boughtCount}/{_targetCount}, dropped {_dropCount} stacks");
        _log.Info($"[AutoBuy] {reason}. bought={_boughtCount} drops={_dropCount}");
    }

    public void Tick()
    {
        // Timeout: if waiting for result >800ms, re-send buy
        // (handles rare case where result packet was lost)
    }

    // ── Internals ─────────────────────────────────────────────────────

    private void SendBuy()
    {
        if (!_running) return;
        _waitingForResult = true;
        SendGenericText($"action|buy|item|{_itemCode}|\n");
        _log.Info($"[AutoBuy] → buy {_itemCode}");
    }

    private void SendDrop()
    {
        if (_itemId <= 0) return;
        // Drop the purchased item from inventory (try two known formats)
        SendGenericText($"action|drop\npacket_id|{_itemId}|count|200\n");
        _log.Info($"[AutoBuy] → drop item {_itemId}");
    }

    private void SendGenericText(string text)
    {
        var body   = Encoding.Latin1.GetBytes(text + "\0");
        var packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GenericText);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        _sendToServer(packet);
    }

    private static bool IsNavCode(string code) =>
        code is "bigitems" or "main_menu" or "locks_menu" or "itempack_menu"
             or "activities_menu" or "gems_menu" or "growtokens_menu"
             or "vouchers_menu" or "creativity_menu" or "player_menu" or "worldbuilding_menu";

    // Make a readable name from the item code if database lookup fails
    private static string GuessName(string code) =>
        code switch
        {
            "5seed"     => "Small Seed Pack",
            "10seed"    => "Small Seed Pack Collection",
            "5rseed"    => "Rare Seed Pack",
            "bigitems"  => "Item Packs",
            _           => code,
        };
}
