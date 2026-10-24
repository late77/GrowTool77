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
    private readonly Label          _buyItemLbl;
    private readonly Label          _buyPriceLbl;
    private readonly TextBox        _buyQtyBox;
    private readonly GtButton       _buyBtn;
    private readonly Dot            _buyDot;
    private readonly Label          _buyStatusLbl;
    private          AutoBuyFeature? _autoBuy;

    // ── Providers controls ────────────────────────────────────────────
    private readonly Panel           _providersPanel;
    private readonly GtButton        _provBtn;
    private readonly Dot             _provDot;
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
        _selector.Items.AddRange(new object[] { "Fish", "Surg", "Farm", "Providers", "Auto Buy" });
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

        // ── Auto Buy panel ────────────────────────────────────────────
        _buyPanel = new Panel { Bounds = Theme.R(24, 100, 832, 260), BackColor = Theme.Bg, Visible = false };

        var buyCard = new Card { Bounds = Theme.R(0, 0, 832, 190) };
        buyCard.Controls.Add(Ui.Lbl("Auto Buy from Vending Machine", Theme.R(20, 12, 500, 22), Theme.BodyBold, Theme.Gold));
        buyCard.Controls.Add(Ui.Lbl("Wrench any vending machine to detect the item for sale.", Theme.R(20, 36, 792, 20), Theme.Small, Theme.TextDim));

        buyCard.Controls.Add(Ui.Divider(Theme.R(20, 58, 792, 1)));

        _buyItemLbl  = Ui.Lbl("No vend detected — wrench a vending machine", Theme.R(20, 66, 500, 24), Theme.Body, Theme.Text);
        _buyPriceLbl = Ui.Lbl("", Theme.R(20, 92, 500, 22), Theme.Small, Theme.TextDim);
        buyCard.Controls.Add(_buyItemLbl);
        buyCard.Controls.Add(_buyPriceLbl);

        buyCard.Controls.Add(Ui.Lbl("Quantity:", Theme.R(20, 122, 80, 26), Theme.Body, Theme.TextDim));
        _buyQtyBox = new TextBox { Bounds = Theme.R(106, 120, 100, 26), Font = Theme.Body, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = "200" };
        var buyMaxBtn = new GtButton("Max", ButtonKind.Secondary) { Bounds = Theme.R(212, 120, 60, 26) };
        buyMaxBtn.Click += (_, _) =>
        {
            // Max = floor(gems / price) — gems not tracked yet → just leave as user input
            _buyQtyBox.Text = "10000";
        };
        buyCard.Controls.AddRange(new Control[] { _buyItemLbl, _buyPriceLbl, _buyQtyBox, buyMaxBtn });

        buyCard.Controls.Add(Ui.Divider(Theme.R(20, 152, 792, 1)));

        _buyBtn       = new GtButton("Start") { Bounds = Theme.R(20, 160, 120, 26) };
        _buyDot       = new Dot  { Bounds = Theme.R(152, 168, 14, 14) };
        _buyStatusLbl = Ui.Lbl("Wrench a vend to begin", Theme.R(174, 160, 500, 26), Theme.Body, Theme.TextDim);
        _buyBtn.Click += (_, _) => ToggleAutoBuy();
        buyCard.Controls.AddRange(new Control[] { _buyBtn, _buyDot, _buyStatusLbl });
        _buyPanel.Controls.Add(buyCard);

        var buyHint = new Card { Bounds = Theme.R(0, 198, 832, 58) };
        buyHint.Controls.Add(Ui.Lbl(
            "Wrench vend → item appears above → set quantity → Start.\n" +
            "When inventory fills, proxy auto-drops stack and continues. Stops at target count.",
            Theme.R(20, 6, 792, 46), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));
        _buyPanel.Controls.Add(buyHint);
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
    }

    // ── Auto Buy public methods ───────────────────────────────────────

    public void SetAutoBuyFeature(AutoBuyFeature f)
    {
        _autoBuy = f;
        _autoBuy.ShopItemDetected += (code, name, id) => OnUi(() =>
        {
            _buyItemLbl.Text  = $"Item: {name}  (code: {code})";
            _buyPriceLbl.Text = "Open the Shop, navigate to the item, click it — then Start here.";
            _selector.SelectedIndex = 4;
        });
        _autoBuy.StatusChanged += s => OnUi(() =>
        {
            _buyDot.DotColor = _autoBuy.IsRunning ? Theme.Good : Theme.Bad;
            _buyStatusLbl.Text = s;
        });
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
            _providers.Start();
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
