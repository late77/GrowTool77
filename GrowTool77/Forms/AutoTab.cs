// lang: C#, file: AutoTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// "Auto" tab — dropdown: Fish | Surg. Each feature has its own sub-panel.
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class AutoTab : Panel
{
    private readonly ComboBox _selector;
    private readonly Panel    _fishPanel;
    private readonly Panel    _surgPanel;
    private readonly Panel    _farmPanel;

    // ── Fish controls ────────────────────────────────────────────────
    private readonly GtButton _fishBtn;
    private readonly Dot      _fishDot;
    private readonly Label    _fishLbl;
    private readonly CheckBox _fishLog;
    private FishingFeature?   _fish;

    // ── Auto Buy controls ─────────────────────────────────────────────
    private readonly Panel          _buyPanel;
    private readonly Panel          _consumePanel;
    private ConsumableFeature?      _consume;
    private readonly GtButton       _consumeBtn;
    private readonly Dot            _consumeDot;
    private readonly Label          _consumeStatusLbl;
    private readonly ComboBox       _consumeItemSel;
    private readonly NumericUpDown  _consumeQty;
    private readonly ItemListView   _buyList;
    private readonly TextBox        _buyFilter;
    private readonly Label          _buyItemLbl;
    private readonly Label          _buyPriceLbl;
    private readonly TextBox        _buyQtyBox;
    private readonly GtButton       _buyBtn;
    private readonly Dot            _buyDot;
    private readonly Label          _buyStatusLbl;
    private          AutoBuyFeature? _autoBuy;
    private          ShopItem?       _selectedBuyItem;
    private          IReadOnlyList<ShopItem> _allShopItems = Array.Empty<ShopItem>();

    // ── Providers controls ────────────────────────────────────────────
    private readonly Panel           _providersPanel;
    private readonly GtButton        _provBtn;
    private readonly Dot             _provDot;
    private readonly CheckBox        _rageCheck;
    private readonly Label           _provLbl;
    private readonly Label           _provCountLbl;
    private          ProvidersFeature? _providers;

    // ── Farm controls ────────────────────────────────────────────────
    private GtButton?           _farmSelectBtn;
    private GtButton?           _farmDoneBtn;
    public event Action?        StartFarmSelection;
    public event Action?        FinishFarmSelection;
    private readonly GtButton   _farmBtn;
    private readonly Dot        _farmDot;
    private readonly Label      _farmLbl;
    private readonly TextBox    _farmBlockId;
    private readonly TextBox    _farmToolId;
    private readonly ListBox    _farmTileList;
    private readonly Label      _farmSafetyLbl;
    private          FarmFeature? _farm;
    private readonly TrackBar   _safetySlider;

    // ── Surg controls ────────────────────────────────────────────────
    private readonly RadioButton _surgInsta;
    private readonly RadioButton _surgManual;
    private readonly GtButton    _surgBtn;
    private readonly Dot         _surgDot;
    private readonly Label       _surgLbl;
    private readonly CheckBox    _surgLog;
    private SurgeryFeature?      _surg;

    public AutoTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("Auto features", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        // ── Feature selector ─────────────────────────────────────────
        var selectorCard = new Card { Bounds = Theme.R(24, 36, 832, 52) };
        selectorCard.Controls.Add(Ui.Lbl("Feature", Theme.R(20, 12, 72, 28), Theme.Body, Theme.TextDim, ContentAlignment.MiddleLeft));

        _selector = new ComboBox
        {
            Bounds        = Theme.R(98, 12, 180, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = Theme.Input,
            ForeColor     = Theme.Text,
            Font          = Theme.Body,
        };
        _selector.Items.AddRange(new object[] { "Fish", "Surg", "Farm", "Providers", "Auto Buy", "Consumables" });
        _selector.SelectedIndex    = 0;
        _selector.SelectedIndexChanged += (_, _) => SwitchPanel();
        selectorCard.Controls.Add(_selector);
        Controls.Add(selectorCard);

        // ── Fish panel ───────────────────────────────────────────────
        _fishPanel = new Panel { Bounds = Theme.R(24, 100, 832, 225), BackColor = Theme.Bg };

        var fishCard = new Card { Bounds = Theme.R(0, 0, 832, 140) };
        _fishBtn = new GtButton("Start") { Bounds = Theme.R(20, 18, 120, 30) };
        _fishBtn.Click += (_, _) => ToggleFish();

        fishCard.Controls.Add(Ui.Divider(Theme.R(20, 56, 792, 1)));
        _fishDot = new Dot { Bounds = Theme.R(20, 72, 14, 14) };
        _fishLbl = Ui.Lbl("Stopped", Theme.R(42, 64, 560, 28), Theme.Body, Theme.TextDim);

        fishCard.Controls.Add(Ui.Divider(Theme.R(20, 100, 792, 1)));
        _fishLog = new CheckBox
        {
            Text      = "Log all fishing packets to [AURA] tab",
            Bounds    = Theme.R(20, 110, 600, 26),
            Font      = Theme.Small,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Card,
            AutoSize  = false,
        };
        _fishLog.CheckedChanged += (_, _) => { if (_fish != null) _fish.LoggingEnabled = _fishLog.Checked; };

        fishCard.Controls.AddRange(new Control[] { _fishBtn, _fishDot, _fishLbl, _fishLog });
        _fishPanel.Controls.Add(fishCard);

        var fishHint = new Card { Bounds = Theme.R(0, 150, 832, 52) };
        fishHint.Controls.Add(Ui.Lbl(
            "Cast once manually to register the tile, then auto-fish takes over (117–217 ms pull delay, 400–700 ms recast).",
            Theme.R(20, 10, 792, 32), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));
        _fishPanel.Controls.Add(fishHint);
        Controls.Add(_fishPanel);

        // ── Surg panel ───────────────────────────────────────────────
        _surgPanel = new Panel { Bounds = Theme.R(24, 100, 832, 260), BackColor = Theme.Bg, Visible = false };

        var surgCard = new Card { Bounds = Theme.R(0, 0, 832, 180) };

        // Mode radio buttons
        surgCard.Controls.Add(Ui.Lbl("Mode", Theme.R(20, 14, 60, 28), Theme.Body, Theme.TextDim, ContentAlignment.MiddleLeft));

        _surgInsta = new RadioButton
        {
            Text      = "Full Auto",
            Bounds    = Theme.R(88, 14, 80, 28),
            Font      = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Checked   = true,
            AutoSize  = false,
        };
        surgCard.Controls.Add(_surgInsta);

        // Risk badge label
        var riskLbl = new Label
        {
            Text      = "[RISK_lvl{value_high(1)}]",
            Bounds    = Theme.R(174, 18, 200, 22),
            Font      = Theme.Small,
            ForeColor = Color.FromArgb(255, 160, 30),
            BackColor = Color.FromArgb(60, 30, 0),
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        surgCard.Controls.Add(riskLbl);

        _surgManual = new RadioButton
        {
            Text      = "Manual  (shows next tool in Growtopia overlay)",
            Bounds    = Theme.R(88, 46, 420, 28),
            Font      = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            AutoSize  = false,
        };
        surgCard.Controls.Add(_surgManual);

        surgCard.Controls.Add(Ui.Divider(Theme.R(20, 80, 792, 1)));

        _surgBtn = new GtButton("Start") { Bounds = Theme.R(20, 92, 120, 30) };
        _surgBtn.Click += (_, _) => ToggleSurg();
        surgCard.Controls.Add(_surgBtn);

        _surgDot = new Dot { Bounds = Theme.R(160, 100, 14, 14) };
        _surgLbl = Ui.Lbl("Stopped", Theme.R(182, 92, 500, 28), Theme.Body, Theme.TextDim);
        surgCard.Controls.AddRange(new Control[] { _surgDot, _surgLbl });

        surgCard.Controls.Add(Ui.Divider(Theme.R(20, 130, 792, 1)));

        _surgLog = new CheckBox
        {
            Text      = "Log surgery packets to [AURA] tab (required for first run — reveals dialog_name + button IDs)",
            Bounds    = Theme.R(20, 142, 780, 26),
            Font      = Theme.Small,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Card,
            Checked   = true,
            AutoSize  = false,
        };
        _surgLog.CheckedChanged += (_, _) => { if (_surg != null) _surg.LoggingEnabled = _surgLog.Checked; };
        surgCard.Controls.Add(_surgLog);

        _surgPanel.Controls.Add(surgCard);

        var surgHint = new Card { Bounds = Theme.R(0, 192, 832, 64) };
        surgHint.Controls.Add(Ui.Lbl(
            "Start, enable logger, wrench a Surg-E — check [AURA] tab for [Surg] lines.\n" +
            "Copy dialog_name + buttonClicked values here so Insta-mode can be wired up.",
            Theme.R(20, 8, 792, 48), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));
        _surgPanel.Controls.Add(surgHint);
        Controls.Add(_surgPanel);

        // ── Auto Buy panel — Shop Browser ─────────────────────────────
        // Compact single-card layout — fits without scrolling
        _buyPanel = new Panel { Bounds = Theme.R(24, 100, 832, 248), BackColor = Theme.Bg, Visible = false };
        var buyCard = new Card { Bounds = Theme.R(0, 0, 832, 248) };

        // Title + search on same row
        buyCard.Controls.Add(Ui.Lbl("Shop Browser", Theme.R(20, 10, 160, 20), Theme.BodyBold, Theme.Gold));
        _buyFilter = new TextBox { Bounds = Theme.R(186, 8, 200, 24), Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        _buyFilter.PlaceholderText = "Search…";
        _buyFilter.TextChanged += (_, _) => RefreshBuyList();
        buyCard.Controls.Add(_buyFilter);
        buyCard.Controls.Add(Ui.Lbl("1. Select item below  2. Set qty  3. Click it once in GT Shop", Theme.R(396, 10, 420, 20), Theme.Small, Theme.TextDim));

        // List (shows 3-4 rows)
        _buyList = new ItemListView { Bounds = Theme.R(20, 38, 792, 96) };
        _buyList.Columns.Add("Item Name",    Theme.S(420));
        _buyList.Columns.Add("Price (gems)", Theme.S(130));
        _buyList.Columns.Add("Code",         Theme.S(160));
        _buyList.MultiSelect   = false;
        _buyList.FullRowSelect = true;
        _buyList.Click += (_, _) => OnBuyListClick();
        buyCard.Controls.Add(_buyList);

        buyCard.Controls.Add(Ui.Divider(Theme.R(20, 140, 792, 1)));

        // Selected item info
        _buyItemLbl  = Ui.Lbl("Click an item to select it", Theme.R(20, 147, 700, 20), Theme.Body, Theme.Text);
        _buyPriceLbl = Ui.Lbl("", Theme.R(20, 168, 600, 18), Theme.Small, Theme.TextDim);
        buyCard.Controls.AddRange(new Control[] { _buyItemLbl, _buyPriceLbl });

        // Qty + Max + Start + Status on one row
        buyCard.Controls.Add(Ui.Lbl("Qty:", Theme.R(20, 198, 36, 26), Theme.Body, Theme.TextDim));
        _buyQtyBox = new TextBox { Bounds = Theme.R(58, 196, 80, 26), Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = "200" };
        var buyMaxBtn = new GtButton("Max", ButtonKind.Secondary) { Bounds = Theme.R(144, 196, 60, 26) };
        buyMaxBtn.Click += (_, _) =>
        {
            if (_autoBuy == null || _selectedBuyItem == null) { _buyStatusLbl.Text = "Select item first"; return; }
            long gems = _autoBuy.GemsLeft;
            if (gems <= 0) { _buyStatusLbl.Text = "Buy one first — gem count needed"; return; }
            _buyQtyBox.Text = Math.Max(1, gems / _selectedBuyItem.Price).ToString();
        };
        _buyBtn       = new GtButton("Start") { Bounds = Theme.R(212, 196, 100, 26) };
        _buyDot       = new Dot  { Bounds = Theme.R(322, 204, 14, 14) };
        _buyStatusLbl = Ui.Lbl("Select item → set qty → click it once in Growtopia Shop", Theme.R(344, 196, 468, 26), Theme.Small, Theme.TextDim);
        _buyBtn.Click += (_, _) => ToggleAutoBuy();
        buyCard.Controls.AddRange(new Control[] { _buyQtyBox, buyMaxBtn, _buyBtn, _buyDot, _buyStatusLbl });
        _buyPanel.Controls.Add(buyCard);
        Controls.Add(_buyPanel);

        // ── Providers panel ──────────────────────────────────────────
        _providersPanel = new Panel { Bounds = Theme.R(24, 100, 832, 260), BackColor = Theme.Bg, Visible = false };

        var provCard = new Card { Bounds = Theme.R(0, 0, 832, 180) };
        provCard.Controls.Add(Ui.Lbl("Auto-collect Providers", Theme.R(20, 12, 400, 22), Theme.BodyBold, Theme.Gold));
        provCard.Controls.Add(Ui.Lbl("ATM Machines, Item-O-Matics and other provider blocks in the current world.",
            Theme.R(20, 36, 792, 22), Theme.Small, Theme.TextDim));

        provCard.Controls.Add(Ui.Divider(Theme.R(20, 62, 792, 1)));

        _provCountLbl = Ui.Lbl("— providers found", Theme.R(20, 72, 500, 26), Theme.Body, Theme.TextDim);
        provCard.Controls.Add(_provCountLbl);

        provCard.Controls.Add(Ui.Divider(Theme.R(20, 106, 792, 1)));

        _rageCheck = new CheckBox
        {
            Text      = "Rage mode — 5×5 grid, 10ms/tile, 1s pause",
            Bounds    = Theme.R(20, 92, 420, 22),
            Font      = Theme.Small,
            ForeColor = Color.FromArgb(255, 80, 80),
            BackColor = Theme.Bg,
            AutoSize  = false,
        };
        provCard.Controls.Add(_rageCheck);

        _provBtn = new GtButton("Start") { Bounds = Theme.R(20, 116, 120, 30) };
        _provBtn.Click += (_, _) => ToggleProviders();
        _provDot = new Dot { Bounds = Theme.R(152, 124, 14, 14) };
        _provLbl = Ui.Lbl("Stopped", Theme.R(174, 116, 500, 28), Theme.Body, Theme.TextDim);
        provCard.Controls.AddRange(new Control[] { _provBtn, _provDot, _provLbl, _provCountLbl });
        _providersPanel.Controls.Add(provCard);

        var provHint = new Card { Bounds = Theme.R(0, 192, 832, 64) };
        provHint.Controls.Add(Ui.Lbl(
            "Walk to your farm world first. Providers are found automatically when you enter a world.\n" +
            "One punch per provider — cooldown is server-enforced (22h for ATMs).",
            Theme.R(20, 8, 792, 48), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));
        _providersPanel.Controls.Add(provHint);
        Controls.Add(_providersPanel);

        // ── Farm panel ───────────────────────────────────────────────
        _farmPanel = new Panel { Bounds = Theme.R(24, 100, 832, 316), BackColor = Theme.Bg, Visible = false };

        var farmCard = new Card { Bounds = Theme.R(0, 0, 832, 224) };

        // Block + tool row
        farmCard.Controls.Add(Ui.Lbl("Block ID", Theme.R(20, 16, 80, 24), Theme.Small, Theme.TextDim));
        _farmBlockId = new TextBox { Bounds = Theme.R(104, 14, 100, 24), Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = "0" };
        farmCard.Controls.Add(_farmBlockId);

        farmCard.Controls.Add(Ui.Lbl("Tool ID", Theme.R(216, 16, 72, 24), Theme.Small, Theme.TextDim));
        _farmToolId = new TextBox { Bounds = Theme.R(292, 14, 100, 24), Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = "0" };
        farmCard.Controls.Add(_farmToolId);

        farmCard.Controls.Add(Ui.Lbl("(0 = equipped item)", Theme.R(400, 18, 200, 20), Theme.Small, Theme.TextDim));

        // Safety slider
        farmCard.Controls.Add(Ui.Lbl("Safety sensitivity", Theme.R(20, 52, 160, 20), Theme.Small, Theme.TextDim));
        _safetySlider = new TrackBar { Bounds = Theme.R(184, 44, 200, 32), Minimum = 1, Maximum = 10, Value = 5, TickFrequency = 1, SmallChange = 1, BackColor = Theme.Card };
        farmCard.Controls.Add(_safetySlider);
        _farmSafetyLbl = Ui.Lbl("Medium", Theme.R(392, 52, 80, 20), Theme.Small, Theme.Gold);
        _safetySlider.ValueChanged += (_, _) =>
        {
            _farmSafetyLbl.Text = _safetySlider.Value switch { <= 2 => "Low", >= 8 => "High", _ => "Medium" };
            if (_farm != null) _farm.SafetyPx = (11 - _safetySlider.Value) * 16f;
        };
        farmCard.Controls.Add(_farmSafetyLbl);
        farmCard.Controls.Add(Ui.Lbl("(recommended)", Theme.R(480, 52, 140, 20), Theme.Small, Theme.TextDim));

        farmCard.Controls.Add(Ui.Divider(Theme.R(20, 78, 792, 1)));

        // Tile list
        farmCard.Controls.Add(Ui.Lbl("Farm tiles  (tileX,tileY)", Theme.R(20, 88, 250, 20), Theme.Small, Theme.TextDim));
        _farmTileList = new ListBox { Bounds = Theme.R(20, 110, 480, 96), Font = Theme.Small, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        farmCard.Controls.Add(_farmTileList);

        var selectBtn = new GtButton("Select in game", ButtonKind.Secondary) { Bounds = Theme.R(512, 100, 140, 34) };
        var clrTileBtn = new GtButton("Clear all",     ButtonKind.Secondary) { Bounds = Theme.R(512, 142, 140, 30) };
        var doneBtn    = new GtButton("Done (Esc)",    ButtonKind.Primary)   { Bounds = Theme.R(512, 180, 140, 30), Visible = false };
        selectBtn.Click  += (_, _) => StartFarmSelection?.Invoke();
        doneBtn.Click    += (_, _) => FinishFarmSelection?.Invoke();
        clrTileBtn.Click += (_, _) => { _farm?.ClearTiles(); RefreshTileList(); };
        _farmSelectBtn = selectBtn;
        _farmDoneBtn   = doneBtn;
        farmCard.Controls.AddRange(new Control[] { selectBtn, clrTileBtn, doneBtn });

        farmCard.Controls.Add(Ui.Divider(Theme.R(20, 140, 792, 1)));

        // Start/stop + status
        _farmBtn = new GtButton("Start (F)") { Bounds = Theme.R(20, 185, 130, 30) };
        _farmBtn.Click += (_, _) => ToggleFarm();
        _farmDot = new Dot { Bounds = Theme.R(162, 193, 14, 14) };
        _farmLbl = Ui.Lbl("Stopped", Theme.R(184, 185, 500, 28), Theme.Body, Theme.TextDim);
        farmCard.Controls.AddRange(new Control[] { _farmBtn, _farmDot, _farmLbl });
        _farmPanel.Controls.Add(farmCard);

        var farmHint = new Card { Bounds = Theme.R(0, 232, 832, 52) };
        farmHint.Controls.Add(Ui.Lbl(
            "Press F anywhere to toggle farm. Stand at your farm spot before starting. " +
            "Safety stops farm if you move — adjust slider for sensitivity.",
            Theme.R(20, 10, 792, 32), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));
        _farmPanel.Controls.Add(farmHint);
        Controls.Add(_farmPanel);

        // ── Consumables panel ─────────────────────────────────────────
        _consumePanel = new Panel { Bounds = Theme.R(24, 100, 832, 200), BackColor = Theme.Bg, Visible = false };
        var conCard   = new Card  { Bounds = Theme.R(0, 0, 832, 200) };

        conCard.Controls.Add(Ui.Lbl("Consumables", Theme.R(20, 10, 200, 22), Theme.BodyBold, Theme.Gold));
        conCard.Controls.Add(Ui.Lbl("Opens packs automatically. Auto-drops seeds when stack hits 200.",
            Theme.R(20, 32, 700, 20), Theme.Small, Theme.TextDim));

        _consumeItemSel = new ComboBox
        {
            Bounds        = Theme.R(20, 62, 300, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font          = Theme.Body,
            BackColor     = Theme.Input,
            ForeColor     = Theme.Text,
        };
        foreach (var (label, _) in ConsumableFeature.KnownItems)
            _consumeItemSel.Items.Add(label);
        _consumeItemSel.SelectedIndex = 0;

        _consumeQty = new NumericUpDown
        {
            Bounds   = Theme.R(336, 62, 100, 26),
            Minimum  = 0,
            Maximum  = 100000,
            Value    = 0,
            Font     = Theme.Body,
            BackColor= Theme.Input,
            ForeColor= Theme.Text,
        };

        _consumeBtn = new GtButton("Start") { Bounds = Theme.R(20, 104, 120, 30) };
        _consumeBtn.Click += (_, _) => ToggleConsume();

        _consumeDot       = new Dot   { Bounds = Theme.R(152, 112, 14, 14) };
        _consumeStatusLbl = Ui.Lbl("Select item and press Start", Theme.R(176, 108, 560, 26), Theme.Small, Theme.TextDim);

        conCard.Controls.AddRange(new Control[]
        {
            _consumeItemSel, Ui.Lbl("Qty", Theme.R(440, 66, 30, 20), Theme.Small, Theme.TextDim),
            _consumeQty, _consumeBtn, _consumeDot, _consumeStatusLbl
        });
        _consumePanel.Controls.Add(conCard);
        Controls.Add(_consumePanel);
    }

    // ── Public wiring ────────────────────────────────────────────────

    public void SetFishFeature(FishingFeature fish)
    {
        if (_fish != null) _fish.StatusChanged -= OnFishStatus;
        _fish                = fish;
        _fish.LoggingEnabled = _fishLog.Checked;
        _fish.StatusChanged += OnFishStatus;
    }

    public void SetSurgFeature(SurgeryFeature surg)
    {
        if (_surg != null)
        {
            _surg.StatusChanged    -= OnSurgStatus;
            _surg.ToolRecommended  -= OnToolRecommended;
        }
        _surg                = surg;
        _surg.LoggingEnabled = _surgLog.Checked;
        _surg.Mode           = _surgInsta.Checked ? SurgMode.Insta : SurgMode.Manual;
        _surg.StatusChanged   += OnSurgStatus;
        _surg.ToolRecommended += OnToolRecommended;

        _surgInsta.CheckedChanged  += (_, _) => { if (_surg != null && _surgInsta.Checked)  _surg.Mode = SurgMode.Insta;  };
        _surgManual.CheckedChanged += (_, _) => { if (_surg != null && _surgManual.Checked) _surg.Mode = SurgMode.Manual; };
    }

    // ── Internal ─────────────────────────────────────────────────────

    private void SwitchPanel()
    {
        int idx = _selector.SelectedIndex;
        _fishPanel.Visible      = idx == 0;
        _surgPanel.Visible      = idx == 1;
        _farmPanel.Visible      = idx == 2;
        _providersPanel.Visible = idx == 3;
        _buyPanel.Visible       = idx == 4;
        _consumePanel.Visible   = idx == 5;
    }

    // ── Auto Buy public methods ───────────────────────────────────────

    public void SetAutoBuyFeature(AutoBuyFeature f)
    {
        _autoBuy = f;
        _autoBuy.ShopItemsUpdated += items => OnUi(() =>
        {
            _allShopItems = items;
            RefreshBuyList();
            _selector.SelectedIndex = 4; // switch to Auto Buy tab
        });
        _autoBuy.StatusChanged += s => OnUi(() =>
        {
            _buyDot.DotColor = _autoBuy.IsRunning ? Theme.Good : Theme.Bad;
            _buyStatusLbl.Text = s;
        });
    }

    private void RefreshBuyList()
    {
        if (IsDisposed) return;
        string filter = _buyFilter?.Text ?? "";
        _buyList.BeginUpdate();
        _buyList.Items.Clear();
        foreach (var item in _allShopItems)
        {
            if (!string.IsNullOrEmpty(filter) &&
                !item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !item.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var row = new ListViewItem(item.Name) { Tag = item, ForeColor = Theme.Text };
            row.SubItems.Add($"{item.Price:N0}");
            row.SubItems.Add(item.Code);
            _buyList.Items.Add(row);
        }
        _buyList.EndUpdate();
        if (_autoBuy?.IsRunning == true) return; // don't overwrite buy status
        if (_allShopItems.Count == 0)
            _buyStatusLbl.Text = "Browse Shop in Growtopia to load items";
        else if (_buyList.Items.Count == 0)
            _buyStatusLbl.Text = "No items match filter";
    }

    private void OnBuyListClick()
    {
        if (_autoBuy == null) return;
        var sel = _buyList.SelectedItems.Count > 0 ? _buyList.SelectedItems[0] : null;
        if (sel?.Tag is not ShopItem item) return;
        _selectedBuyItem   = item;
        _buyItemLbl.Text   = $"{item.Name}";
        _buyPriceLbl.Text  = $"{item.Price:N0} gems each — code: {item.Code}";
        _buyDot.DotColor   = Theme.Warn;
        _buyStatusLbl.Text = "Ready — set quantity and click Start";
        _autoBuy.SelectItem(item);
    }

    private void ToggleAutoBuy()
    {
        if (_autoBuy == null) return;
        if (_autoBuy.IsRunning)
        {
            _autoBuy.Stop();
            _buyBtn.Kind = ButtonKind.Primary;
            Ui.SetText(_buyBtn, "Start");
        }
        else
        {
            if (!int.TryParse(_buyQtyBox.Text, out int qty) || qty <= 0) { _buyStatusLbl.Text = "Enter a valid quantity"; return; }
            _autoBuy.Start(qty);
            if (_autoBuy.IsRunning) { _buyBtn.Kind = ButtonKind.Danger; Ui.SetText(_buyBtn, "Stop"); }
        }
        _buyBtn.Invalidate();
    }

    private void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    // ── Providers public methods ──────────────────────────────────────

    public void SetConsumableFeature(ConsumableFeature f)
    {
        _consume = f;
        _consume.StatusChanged += s => OnUi(() =>
        {
            _consumeDot.DotColor  = _consume.IsRunning ? Theme.Good : Theme.Bad;
            _consumeStatusLbl.Text = s;
            _consumeBtn.Kind = _consume.IsRunning ? ButtonKind.Danger : ButtonKind.Primary;
            Ui.SetText(_consumeBtn, _consume.IsRunning ? "Stop" : "Start");
            _consumeBtn.Invalidate();
        });
    }

    private void ToggleConsume()
    {
        if (_consume == null) return;
        if (_consume.IsRunning)
        {
            _consume.Stop();
            _consumeBtn.Kind = ButtonKind.Primary;
            Ui.SetText(_consumeBtn, "Start");
        }
        else
        {
            int idx    = Math.Max(0, _consumeItemSel.SelectedIndex);
            int itemId = ConsumableFeature.KnownItems[idx].ItemId;
            int qty    = (int)_consumeQty.Value;
            _consume.Start(itemId, qty);
            if (_consume.IsRunning) { _consumeBtn.Kind = ButtonKind.Danger; Ui.SetText(_consumeBtn, "Stop"); }
        }
        _consumeBtn.Invalidate();
    }

    public void SetProvidersFeature(ProvidersFeature p)
    {
        _providers                = p;
        _providers.StatusChanged += OnProvStatus;
    }

    public void NotifyProviderWorldChanged()
    {
        if (_providers == null) return;
        Ui.SetText(_provCountLbl, $"{_providers.TotalTiles} providers found — ready to collect");
    }

    private void ToggleProviders()
    {
        if (_providers == null) return;
        if (_providers.IsRunning)
        {
            _providers.Stop();
            _provBtn.Kind = ButtonKind.Primary;
            Ui.SetText(_provBtn, "Start");
        }
        else
        {
            _providers.Start(_rageCheck.Checked);
            if (_providers.IsRunning) { _provBtn.Kind = ButtonKind.Danger; Ui.SetText(_provBtn, "Stop"); }
        }
        _provBtn.Invalidate();
    }

    private void OnProvStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProvStatus(s)); return; }
        _provDot.DotColor = _providers?.IsRunning ?? false ? Theme.Good : Theme.Bad;
        Ui.SetText(_provLbl, s);
    }

    // ── Farm public methods ───────────────────────────────────────────

    public void SetFarmFeature(FarmFeature farm)
    {
        _farm                = farm;
        _farm.StatusChanged  += OnFarmStatus;
        _farm.SafetyTriggered += OnFarmSafety;
        _farm.SafetyPx = (11 - _safetySlider.Value) * 16f;
    }

    public void EnterSelectMode()
    {
        if (_farmSelectBtn != null) _farmSelectBtn.Visible = false;
        if (_farmDoneBtn   != null) _farmDoneBtn.Visible   = true;
    }

    public void ExitSelectMode(List<GrowTool77.Aura.TilePos> tiles)
    {
        if (_farmSelectBtn != null) _farmSelectBtn.Visible = true;
        if (_farmDoneBtn   != null) _farmDoneBtn.Visible   = false;
        if (_farm != null)
        {
            _farm.ClearTiles();
            foreach (var t in tiles) _farm.AddTile(t.X, t.Y);
            RefreshTileList();
        }
    }

    // Called by F key (MainForm global hotkey)
    public void ToggleFarm()
    {
        if (_farm == null) return;
        if (_farm.IsRunning)
        {
            _farm.Stop();
            _farmBtn.Kind = ButtonKind.Primary;
            Ui.SetText(_farmBtn, "Start (F)");
        }
        else
        {
            // Apply config before starting
            if (int.TryParse(_farmBlockId.Text, out int blockId)) _farm.BlockItemId = blockId;
            if (int.TryParse(_farmToolId.Text,  out int toolId))  _farm.ToolItemId  = toolId;
            _farm.Start();
            if (_farm.IsRunning) { _farmBtn.Kind = ButtonKind.Danger; Ui.SetText(_farmBtn, "Stop (F)"); }
        }
        _farmBtn.Invalidate();
    }

    private void OnFarmStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFarmStatus(s)); return; }
        _farmDot.DotColor = _farm?.IsRunning ?? false ? Theme.Good : Theme.Bad;
        Ui.SetText(_farmLbl, s);
    }

    private void OnFarmSafety()
    {
        if (InvokeRequired) { BeginInvoke(OnFarmSafety); return; }
        _farmBtn.Kind = ButtonKind.Primary;
        Ui.SetText(_farmBtn, "Start (F)");
        _farmBtn.Invalidate();
    }

    // Tile list: right-click to remove a specific tile
    private void RemoveTileFromList()
    {
        if (_farmTileList.SelectedItem is string s && s.Length > 2)
        {
            var parts = s.Trim('(', ')').Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int y))
            {
                _farm?.RemoveTile(x, y);
                RefreshTileList();
            }
        }
    }

    private void RefreshTileList()
    {
        if (InvokeRequired) { BeginInvoke(RefreshTileList); return; }
        _farmTileList.Items.Clear();
        if (_farm == null) return;
        foreach (var t in _farm.Tiles) _farmTileList.Items.Add($"({t.X}, {t.Y})");
    }

    private void ToggleFish()
    {
        if (_fish == null) return;
        if (_fish.IsRunning) { _fish.Stop();  _fishBtn.Kind = ButtonKind.Primary; Ui.SetText(_fishBtn, "Start"); }
        else                 { _fish.Start(); _fishBtn.Kind = ButtonKind.Danger;  Ui.SetText(_fishBtn, "Stop");  }
        _fishBtn.Invalidate();
    }

    private void ToggleSurg()
    {
        if (_surg == null) return;
        if (_surg.IsRunning) { _surg.Stop();  _surgBtn.Kind = ButtonKind.Primary; Ui.SetText(_surgBtn, "Start"); }
        else                 { _surg.Start(); _surgBtn.Kind = ButtonKind.Danger;  Ui.SetText(_surgBtn, "Stop");  }
        _surgBtn.Invalidate();
    }

    private void OnFishStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFishStatus(s)); return; }
        _fishDot.DotColor = _fish?.IsRunning ?? false ? Theme.Good : Theme.Bad;
        Ui.SetText(_fishLbl, s);
    }

    private void OnSurgStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnSurgStatus(s)); return; }
        _surgDot.DotColor = _surg?.IsRunning ?? false ? Theme.Good : Theme.Bad;
        Ui.SetText(_surgLbl, s);
    }

    private void OnToolRecommended(string toolName)
    {
        if (InvokeRequired) { BeginInvoke(() => OnToolRecommended(toolName)); return; }
        Ui.SetText(_surgLbl, $"Next: {toolName}");
    }
}
