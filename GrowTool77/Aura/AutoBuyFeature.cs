// lang: C#, file: AutoBuyFeature.cs, target: .NET 8 Windows, proj: GrowTool77
// Auto-buy from Growtopia's built-in SHOP with live shop browser.
//
// OnStoreRequest arrives whenever the player navigates the shop.
// We parse it and show all gem-purchasable items in a list.
// Player picks an item, sets quantity, presses Start.
// Proxy auto-sends buy packets and handles drop-when-full.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace GrowTool77.Aura;

public sealed record ShopItem(string Code, string Name, int Price, string Description);

public sealed class AutoBuyFeature
{
    // ── Shop catalog ──────────────────────────────────────────────────
    private readonly List<ShopItem> _items = new();
    public IReadOnlyList<ShopItem> ShopItems => _items;

    // ── Buy state ─────────────────────────────────────────────────────
    private string _buyCode  = "";
    private string _buyName  = "";
    private string _buyTab   = "bigitems";
    private int    _receivedItemId;
    private long   _gemsLeft = -1;
    private byte[]? _rawBuyPacket; // exact raw bytes of user's last buy click

    private volatile bool _running;
    private int  _targetCount;
    private int  _boughtCount;
    private int  _dropCount;
    private bool _waitingForDropDialog;
    private bool _waitingForShop;   // sent re-open, waiting for OnStoreRequest

    private readonly Action<byte[]> _sendToServer;
    private readonly AuraLog        _log;

    public bool   IsRunning   => _running;
    public int    BoughtCount => _boughtCount;
    public int    TargetCount => _targetCount;
    public string BuyName     => _buyName;
    public string BuyCode     => _buyCode;
    public long   GemsLeft    => _gemsLeft;

    public event Action<IReadOnlyList<ShopItem>>? ShopItemsUpdated;
    public event Action<string>?                  StatusChanged;
    private void Fire(string s) => StatusChanged?.Invoke(s);

    public AutoBuyFeature(Action<byte[]> sendToServer, AuraLog log)
    {
        _sendToServer = sendToServer;
        _log          = log;
    }

    // ── AuraProxy hooks ───────────────────────────────────────────────

    // Server → game: shop listing arrived (also signals shop is ready for next buy)
    public void OnStoreRequest(string body)
    {
        var parsed = ParseButtons(body);
        lock (_items) { _items.Clear(); _items.AddRange(parsed); }
        _log.Info($"[AutoBuy] Shop parsed: {parsed.Count} gem items.");
        ShopItemsUpdated?.Invoke(parsed);

        // Running and waiting for shop to open → shop is now open, send buy
        if (_running && _waitingForShop && !_waitingForDropDialog)
        {
            _waitingForShop = false;
            Task.Delay(120).ContinueWith(_ => SendBuy());
        }
    }

    // Called from AuraProxy with the raw ENet packet bytes when user clicks buy
    public void CaptureRawBuyPacket(byte[] rawData, string code)
    {
        if (IsNavCode(code)) return;
        _rawBuyPacket = (byte[])rawData.Clone(); // exact copy — byte-identical to game's packet
        _log.Info($"[AutoBuy] Raw buy packet captured: {rawData.Length} bytes for code={code}");
    }

    // Game → server: player clicked an item in the shop
    public void OnGameBuyAction(string text)
    {
        var m = Regex.Match(text, @"action\|buy\|item\|([^|\s\x00-\x1F]+)\|");
        if (!m.Success) return;
        string code = m.Groups[1].Value;
        if (IsNavCode(code)) return;
        _log.Info($"[AutoBuy] Game buy: {code}");

        // If running with same item → this click is #1, OnStorePurchaseResult handles #2+
        if (_running && code == _buyCode)
        {
            Fire($"Click detected — waiting for server confirmation (#1/{_targetCount})");
            return;
        }

        // Not running: notify UI of item selection
        if (!_running)
        {
            var item = _items.FirstOrDefault(i => i.Code == code);
            if (item != null) SelectItem(item);
        }
    }

    // Auto-start: called when raw packet captured and user has qty set
    // Returns true if auto-started
    public bool TryAutoStart(string code)
    {
        if (_running) return false;
        if (code != _buyCode || _rawBuyPacket == null) return false;
        if (_targetCount <= 0) return false;
        // Raw packet captured and item matches — auto-start from #2
        // #1 is the user's own click, handled by OnStorePurchaseResult
        _boughtCount = 0;
        _dropCount   = 0;
        _waitingForDropDialog = false;
        _waitingForShop = false;
        _running = true;
        Fire($"Armed! Click confirmed buy #1 — proxy takes over from #2/{_targetCount}");
        _log.Info($"[AutoBuy] Auto-armed. Raw packet ready. {_targetCount}× {_buyName}");
        return true;
    }

    // Game → server: item received
    public void OnItemReceived(int itemId)
    {
        if (itemId > 0) _receivedItemId = itemId;
    }

    // Server → game: purchase result
    public void OnStorePurchaseResult(string body)
    {
        // MUST check _running first — otherwise proxy drops user's manually-bought items!
        if (!_running) return;

        bool isFull = body.Contains("don't have enough space", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("not enough space", StringComparison.OrdinalIgnoreCase);
        if (isFull)
        {
            Fire("Inventory full — dropping, resuming…");
            _log.Info("[AutoBuy] Inventory full → drop");
            SendDropRequest();
            return;
        }

        var gm = Regex.Match(body, @"You have\s*`\$([0-9,]+)``\s*Gems? left");
        if (gm.Success) long.TryParse(gm.Groups[1].Value.Replace(",",""), out _gemsLeft);

        _boughtCount++;
        Fire($"Bought {_boughtCount}/{_targetCount} × {_buyName}  ({_gemsLeft:N0} gems left)");
        _log.Info($"[AutoBuy] #{_boughtCount} done. Gems: {_gemsLeft}");

        if (_boughtCount >= _targetCount) { Stop("Target reached!"); return; }
        // Server confirmed purchase — shop stays open server-side, just buy again
        Task.Delay(300).ContinueWith(_ => SendBuy());
    }

    // Server → game: drop dialog (server asks how many to drop)
    public void OnDropDialog(string dialog)
    {
        if (!_waitingForDropDialog) return;
        _waitingForDropDialog = false;

        var idM    = Regex.Match(dialog, @"embed_data\|itemID\|(\d+)");
        var countM = Regex.Match(dialog, @"add_text_input\|count\|\|(\d+)\|");
        if (!idM.Success) return;

        int itemId = int.Parse(idM.Groups[1].Value);
        int count  = countM.Success ? int.Parse(countM.Groups[1].Value) : 200;
        _dropCount++;
        _log.Info($"[AutoBuy] Drop dialog: item={itemId} count={count} drop#{_dropCount}");
        SendGenericText($"action|dialog_return\ndialog_name|drop_item\nitemID|{itemId}|\ncount|{count}|\n");

        if (!_running) return;
        Task.Delay(500).ContinueWith(_ =>
        {
            if (!_running) return;
            SendGenericText($"action|store|location|gem|");
            SendGenericText($"action|buy|item|bigitems|");
            Task.Delay(300).ContinueWith(_ => SendBuy());
        });
        Fire($"Dropped {count}× (#{_dropCount}) — re-opening shop…");
    }

    // ── Public control ─────────────────────────────────────────────────

    public void SelectItem(ShopItem item)
    {
        _buyCode = item.Code;
        _buyName = item.Name;
        Fire($"Selected: {item.Name} — {item.Price:N0} gems — set qty and Start");
    }

    // Called from AuraProxy when OnSetPearl arrives (login gem count)
    public void SetGemsLeft(long gems) { if (gems > 0) _gemsLeft = gems; }

    // Called from AuraProxy when game navigates to a shop tab (for re-open tracking)
    public void OnShopTabNavigated(string tabCode)
    {
        if (!IsNavCode(tabCode)) return;
        // Map nav code back to location string used by action|store|location|X|
        _buyTab = tabCode switch
        {
            "bigitems" or "bigitems_menu" => "gem",     // Awesome Items is on gem tab
            "itempack" or "itempack_menu" => "gem",
            "locks"    or "locks_menu"    => "gem",
            _ => "gem"
        };
        _log.Info($"[AutoBuy] Tab navigated: {tabCode} → re-open via location={_buyTab}");
    }

    public void SelectByCode(string code)
    {
        _buyCode = code;
        _buyName = _items.FirstOrDefault(i => i.Code == code)?.Name ?? code;
    }

    public void Start(int quantity)
    {
        if (string.IsNullOrEmpty(_buyCode)) { Fire("Select an item from the list first"); return; }
        _targetCount = quantity;
        _boughtCount = 0;
        _dropCount   = 0;
        _waitingForDropDialog = false;
        _running     = true;
        Fire($"Armed — open Shop, navigate to {_buyName}, click once to begin");
        _log.Info($"[AutoBuy] Armed. {quantity}× {_buyCode}. Waiting for first confirmed buy.");
        // No navigation packets sent — user opens shop manually to avoid OnStoreRequest spam
        // that could trigger rate limiting. Proxy takes over from #2 after first user click.
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire($"{reason} — {_boughtCount}/{_targetCount} bought");
        _log.Info($"[AutoBuy] {reason}. bought={_boughtCount} drops={_dropCount}");
    }

    // ── Internals ──────────────────────────────────────────────────────

    private void ReopenShop()
    {
        if (!_running) return;
        _waitingForShop = true;
        // Open shop so server knows we have it open — no trailing \n (matches game packet format)
        SendGenericText($"action|store|location|gem|");
        _log.Info("[AutoBuy] Re-opened shop → waiting for OnStoreRequest");
    }

    private void SendBuy()
    {
        if (!_running) return;
        _waitingForShop = false;
        if (_rawBuyPacket != null)
        {
            // Use exact copy of user's original packet — byte-identical, server can't reject it
            _sendToServer((byte[])_rawBuyPacket.Clone());
            _log.Info($"[AutoBuy] → buy {_buyCode} (#{_boughtCount + 1}/{_targetCount}) [raw replay]");
        }
        else
        {
            // Fallback: constructed packet (may not work on all servers)
            SendGenericText($"action|buy|item|{_buyCode}|");
            _log.Info($"[AutoBuy] → buy {_buyCode} (#{_boughtCount + 1}/{_targetCount}) [constructed]");
        }
    }

    private void SendDropRequest()
    {
        if (_receivedItemId <= 0) return;
        _waitingForDropDialog = true;
        SendGenericText($"action|drop||itemID|{_receivedItemId}|");
    }

    private void SendGenericText(string text)
    {
        var body   = Encoding.Latin1.GetBytes(text + "\0");
        var packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, NetMessage.GenericText);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        _sendToServer(packet);
    }

    // ── Shop parser ────────────────────────────────────────────────────

    private static List<ShopItem> ParseButtons(string body)
    {
        var result = new List<ShopItem>();
        // Split on add_button| entries
        var parts = body.Split("||");
        // Full reconstruction: find every add_button| entry
        var btnRx = new Regex(@"add_button\|([^|]+)\|([^|]*)\|[^|]*\|([^|]*)\|[^|]*\|[^|]*\|(-?\d+)\|");
        foreach (Match m in btnRx.Matches(body))
        {
            string code  = m.Groups[1].Value;
            string label = m.Groups[2].Value;
            string desc  = m.Groups[3].Value;
            string priceStr = m.Groups[4].Value;

            // Skip nav codes
            if (IsNavCode(code)) continue;
            // Skip real-money (description starts with https://) or OPENDIALOG/warp buttons
            if (desc.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (desc.StartsWith("OPENDIALOG", StringComparison.OrdinalIgnoreCase)) continue;
            // Must have positive price (gem items only)
            if (!int.TryParse(priceStr, out int price) || price <= 0) continue;

            string name = StripColors(label);
            if (string.IsNullOrWhiteSpace(name)) name = code;

            result.Add(new ShopItem(code, name, price, StripColors(desc.Replace("<CR>", " "))));
        }
        return result;
    }

    private static string StripColors(string s) =>
        Regex.Replace(s.Replace("``", "\x01"), @"`[^\x01`]", "")
             .Replace("\x01", "").Trim();

    private static bool IsNavCode(string c) =>
        c is "bigitems" or "bigitems_menu" or "main_menu" or "locks" or "locks_menu"
          or "itempack" or "itempack_menu" or "weather" or "weather_menu"
          or "token" or "token_menu" or "vouchers" or "vouchers_menu"
          or "activities_menu" or "gems_menu" or "player_menu" or "worldbuilding_menu"
          or "fashionshow_dummy" or "halloween_dummy" or "xsollawall"
          or "CLOSE_show_glory_claim_menu";
}
