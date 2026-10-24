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
    private int    _receivedItemId;
    private long   _gemsLeft = -1;

    private volatile bool _running;
    private int  _targetCount;
    private int  _boughtCount;
    private int  _dropCount;
    private bool _waitingForDropDialog;

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

    // Server → game: full shop listing
    public void OnStoreRequest(string body)
    {
        var parsed = ParseButtons(body);
        lock (_items) { _items.Clear(); _items.AddRange(parsed); }
        _log.Info($"[AutoBuy] Shop parsed: {parsed.Count} gem items.");
        ShopItemsUpdated?.Invoke(parsed);
    }

    // Game → server: player clicked an item
    public void OnGameBuyAction(string text)
    {
        var m = Regex.Match(text, @"action\|buy\|item\|([^|\s\x00-\x1F]+)\|");
        if (!m.Success) return;
        string code = m.Groups[1].Value;
        if (IsNavCode(code)) return;
        _log.Info($"[AutoBuy] Buy click: {code}");
        // If running and same code, continue; else notify UI
        if (!_running)
        {
            var item = _items.FirstOrDefault(i => i.Code == code);
            if (item != null) SelectItem(item);
        }
    }

    // Game → server: item received
    public void OnItemReceived(int itemId)
    {
        if (itemId > 0) _receivedItemId = itemId;
    }

    // Server → game: purchase result
    public void OnStorePurchaseResult(string body)
    {
        bool isFull = body.Contains("don't have enough space", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("not enough space", StringComparison.OrdinalIgnoreCase);
        if (isFull)
        {
            Fire("Inventory full — dropping, resuming…");
            _log.Info("[AutoBuy] Inventory full → drop");
            SendDropRequest();
            return;
        }
        if (!_running) return;

        var gm = Regex.Match(body, @"You have\s*`\$([0-9,]+)``\s*Gems? left");
        if (gm.Success) long.TryParse(gm.Groups[1].Value.Replace(",",""), out _gemsLeft);

        _boughtCount++;
        Fire($"Bought {_boughtCount}/{_targetCount} × {_buyName}  ({_gemsLeft:N0} gems left)");
        _log.Info($"[AutoBuy] #{_boughtCount} done. Gems: {_gemsLeft}");

        if (_boughtCount >= _targetCount) { Stop("Target reached!"); return; }
        Task.Delay(80).ContinueWith(_ => SendBuy());
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
        Task.Delay(250).ContinueWith(_ => SendBuy());
        Fire($"Dropped {count}× (#{_dropCount}) — resuming…");
    }

    // ── Public control ─────────────────────────────────────────────────

    public void SelectItem(ShopItem item)
    {
        _buyCode = item.Code;
        _buyName = item.Name;
        Fire($"Selected: {item.Name} — {item.Price:N0} gems — set qty and Start");
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
        Fire($"Buying {quantity}× {_buyName}…");
        _log.Info($"[AutoBuy] Start. {quantity}× code={_buyCode}");
        SendBuy();
    }

    public void Stop(string reason = "Stopped")
    {
        _running = false;
        Fire($"{reason} — {_boughtCount}/{_targetCount} bought");
        _log.Info($"[AutoBuy] {reason}. bought={_boughtCount} drops={_dropCount}");
    }

    // ── Internals ──────────────────────────────────────────────────────

    private void SendBuy()
    {
        if (!_running) return;
        SendGenericText($"action|buy|item|{_buyCode}|\n");
    }

    private void SendDropRequest()
    {
        if (_receivedItemId <= 0) return;
        _waitingForDropDialog = true;
        SendGenericText($"action|drop||itemID|{_receivedItemId}|\n");
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
