// lang: C#, file: MainForm.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// v0.7: added "Auto" tab (index 1, after Main) and "aura" tab (index 5, last).
// Tab order: Main(0) | Auto(1) | World(2) | [AURA](3) | Vends(4) | aura(5)
using System.Drawing.Drawing2D;
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class MainForm : Form
{
    private readonly AuraService _aura = new();

    private readonly MainTab   _main;
    private readonly ProxyTab  _proxyTab;
    private readonly ScanTab   _scan;
    private readonly AuraTab   _auraTab;
    private readonly VendTab   _vendTab;
    private readonly QoLTab    _qolTab;
    private readonly LineOverlay  _overlay;

    private readonly TabButton _tabMain;
    private readonly TabButton _tabAuto;
    private readonly TabButton _tabScan;
    private readonly TabButton _tabAura;
    private readonly TabButton _tabVends;
    private readonly TabButton _tabAuraLower;

    private readonly StatusPill _pill;
    private readonly Label      _barAura;
    private readonly PictureBox _sprite1, _sprite2, _sprite3;
    private ItemIcons?          _icons;
    private readonly System.Windows.Forms.Timer _timer     = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _farmTimer = new() { Interval = 100 };
    private FarmFeature? _farmFeature;

    private bool       _busy;
    private bool       _leftover;
    private WorldView? _world;

    public MainForm()
    {
        Text            = "GrowTool77 — Team Aura";
        AutoScaleMode   = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Theme.Bg;
        Font            = Theme.Body;
        DoubleBuffered  = true;
        ClientSize      = new Size(Theme.S(Theme.DesignW), Theme.S(Theme.DesignH));

        // ── Top: title + tabs ───────────────────────────────────────
        // Tab row: Main(24) | Auto(158) | World(292) | [AURA](426) | Vends(560) | aura(694)
        var top = new Panel { Bounds = Theme.R(0, 0, 880, 108), BackColor = Theme.Header };
        top.Paint += (_, pe) =>
        {
            using var grad = new LinearGradientBrush(
                top.ClientRectangle, Theme.HeaderTop, Theme.Header, 90f);
            pe.Graphics.FillRectangle(grad, top.ClientRectangle);
        };

        _pill = new StatusPill { Bounds = Theme.R(640, 18, 216, 34) };

        // ── Item sprites loaded from game files via ItemIcons ────────
        // Lucky Clover: left, replaces ☘ emoji.
        // Green Beer:   immediately right of "GrowTool77" title text (title ends ~x=346).
        // Pot O' Gold:  in the status bar (bottom), added later after bar is created.
        _sprite1 = MakeSpritePic(Theme.R(16,  16, 40, 40)); // Lucky Clover (far left)
        _sprite2 = MakeSpritePic(Theme.R(16, 14, 36, 36)); // Green Beer — repositioned dynamically after title measurement
        _sprite3 = MakeSpritePic(new Rectangle(0, 0, 24, 24)); // Pot O' Gold — positioned in bar below

        _tabMain      = new TabButton("Main")    { Bounds = Theme.R(24,  70, 130, 38) };
        _tabAuto      = new TabButton("Proxy")   { Bounds = Theme.R(158, 70, 130, 38) };
        _tabScan      = new TabButton("World")   { Bounds = Theme.R(292, 70, 130, 38) };
        _tabAura      = new TabButton("[AURA]")  { Bounds = Theme.R(426, 70, 130, 38) };
        _tabVends     = new TabButton("Vends")   { Bounds = Theme.R(560, 70, 130, 38) };
        _tabAuraLower = new TabButton("QoL")     { Bounds = Theme.R(694, 70, 130, 38) };

        _tabMain.Click      += (_, _) => SelectTab(0);
        _tabAuto.Click      += (_, _) => SelectTab(1);
        _tabScan.Click      += (_, _) => SelectTab(2);
        _tabAura.Click      += (_, _) => SelectTab(3);
        _tabVends.Click     += (_, _) => SelectTab(4);
        _tabAuraLower.Click += (_, _) => SelectTab(5);

        top.Controls.AddRange(new Control[]
        {
            _sprite1,  // Lucky Clover — far left
            _sprite2,  // Green Beer  — right of GrowTool77 title
            Ui.Lbl("GrowTool77", Theme.R(66, 10, 280, 32), Theme.Title, Theme.Text),
            Ui.Lbl("by Team Aura", Theme.R(67, 42, 200, 18), Theme.SmallBold, Theme.Gold),
            _pill,
            _tabMain, _tabAuto, _tabScan, _tabAura, _tabVends, _tabAuraLower,
        });
        // Position Green Beer dynamically right after the measured title text width.
        // This is DPI-independent: we measure the rendered "GrowTool77" text at the
        // actual Theme.Title font and place the sprite 6px to its right.
        top.HandleCreated += (_, _) =>
        {
            var titleSize = TextRenderer.MeasureText("GrowTool77", Theme.Title);
            int beerX = Theme.S(66) + titleSize.Width + Theme.S(6);
            _sprite2.Left = beerX;
            _sprite2.Top  = Theme.S(14);
        };

        // sprite3 (Pot O' Gold) is added to the status bar below
        Controls.Add(top);

        // ── Pages ───────────────────────────────────────────────────
        _main     = new MainTab            { Bounds = Theme.R(0, 108, 880, 424) };
        _proxyTab = new ProxyTab           { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _scan     = new ScanTab            { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _auraTab  = new AuraTab(_aura.Log) { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _vendTab  = new VendTab            { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _qolTab   = new QoLTab             { Bounds = Theme.R(0, 108, 880, 424), Visible = false };

        Controls.Add(_main);
        Controls.Add(_proxyTab);
        Controls.Add(_scan);
        Controls.Add(_auraTab);
        Controls.Add(_vendTab);
        Controls.Add(_qolTab);

        // ── Status bar ──────────────────────────────────────────────
        var bar = new Panel { Bounds = Theme.R(0, 532, 880, 28), BackColor = Theme.HeaderDeep };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Gold, Math.Max(1, Theme.S(2)));
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };
        _barAura = Ui.Lbl("[AURA]: off", Theme.R(566, 2, 280, 26), Theme.Small, Theme.TextDim, ContentAlignment.MiddleRight);

        // Pot O' Gold sprite — bottom right of status bar
        _sprite3.Bounds    = Theme.R(852, 2, 24, 24);
        _sprite3.BackColor = Color.Transparent;

        bar.Controls.Add(Ui.Lbl("☘  GrowTool77 v2.1", Theme.R(14, 2, 400, 26), Theme.Small, Theme.TextDim));
        bar.Controls.Add(_barAura);
        bar.Controls.Add(_sprite3);
        Controls.Add(bar);

        // ── [AURA] wiring ───────────────────────────────────────────
        _overlay = new LineOverlay(
            () => _aura.Proxy.TryGetPlayer(out var x, out var y) ? (true, x, y) : (false, 0f, 0f),
            message => _aura.Log.Info(message));

        var _autoTab = _proxyTab.AutoFeatures; // proxy for all AutoTab references below

        _main.AuraToggleRequested += ToggleAura;
        _main.RestoreRequested    += RestoreHosts;

        _aura.StatusChanged += _ => OnUi(UpdateAuraUi);
        _aura.WorldChanged  += view => OnUi(() =>
        {
            _world = view;
            _overlay.SetWorld(view);
            _vendTab.SetCurrentWorld(view?.Name);
            UpdateAuraUi();
            // Notify providers so it rescans Fg tiles for ATMs
            _aura.Proxy.NotifyProvidersWorldChanged(view);
            _autoTab.NotifyProviderWorldChanged();
        });
        _aura.Log.LineAdded += line => OnUi(() => _auraTab.Append(line));
        _aura.LoginStep     += _ => OnUi(UpdateAuraUi);
        _aura.VendsChanged  += vends => OnUi(() => _vendTab.SetVends(vends));
        _auraTab.VerboseLogChanged  += on => _aura.Proxy.VerboseLog = on;
        _auraTab.DebugDropsChanged += on =>
        {
            _aura.Proxy.DebugDrops = on;
            _aura.Log.Info(on ? "Drop packet logging is on." : "Drop packet logging is off.");
        };

        _icons = new ItemIcons(_aura.EnsureItems(_main.GamePath), _main.GamePath);
        var icons = _icons;
        _scan.SetIcons(icons);
        _vendTab.SetIcons(icons);

        _scan.LinesChanged += (tiles, name) =>
        {
            _vendTab.ClearLines();
            _overlay.Style = PointerStyle.Line;
            ShowLines(tiles, name);
        };
        _vendTab.PointerStyleChanged += style => _overlay.Style = style;
        _vendTab.PlayerPosition = () => _aura.Proxy.TryGetPlayer(out var x, out var y) ? (true, x, y) : (false, 0f, 0f);
        _overlay.SetShiftyTexture(icons.Get(ShiftyColor.ItemId));
        _auraTab.SaveLinesSnapshot += () =>
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    $"GrowTool77_lines_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            try
            {
                var problem = _overlay.SaveSnapshot(file);
                _aura.Log.Info(problem ?? $"Lines snapshot saved: {file}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                _aura.Log.Warn($"Lines snapshot could not be saved: {ex.Message}");
            }
        };
        _vendTab.LinesChanged += (tiles, name) =>
        {
            _scan.ClearLines();
            _overlay.Style = _vendTab.PointerStyle;
            ShowLines(tiles, name);
        };

        // ── Skip Gazette (QoL) wiring ───────────────────────────────
        // ── Load persisted settings ─────────────────────────────────
        var settings = GrowTool77.Modules.AppSettings.Load();
        _qolTab.ApplySettings(settings);

        // Sync proxy flag and save on every change
        _aura.Proxy.SkipGazette = settings.SkipGazette;
        _qolTab.SkipGazetteChanged += on =>
        {
            _aura.Proxy.SkipGazette = on;
            settings.SkipGazette = on;
            settings.Save();
        };
        _aura.Proxy.GazetteArrived += () => OnUi(() =>
        {
            if (_qolTab.SkipGazette) _aura.Log.Info("[QoL] Gazette auto-dismissed.");
        });

        // ── Farm tile selection wiring ──────────────────────────────
        _autoTab.StartFarmSelection  += () =>
        {
            _overlay.SetFarmSelectTiles(_farmFeature?.Tiles ?? Array.Empty<TilePos>());
            _overlay.FarmSelectMode = true;
            _autoTab.EnterSelectMode();
        };
        _autoTab.FinishFarmSelection += () =>
        {
            _overlay.FarmSelectMode = false;
            _autoTab.ExitSelectMode(new List<TilePos>());
        };
        _overlay.FarmSelectionComplete += tiles =>
            OnUi(() => _autoTab.ExitSelectMode(tiles));

        // ── Door wrench wiring ──────────────────────────────────────
        _scan.DoorWrenchRequested += (tx, ty) => _aura.Proxy.WrenchTile(tx, ty);
        // (ToolSlotChanged wired below after surgFeature is declared)

        _aura.Proxy.DoorIdCaptured += (tx, ty, id) => OnUi(() =>
        {
            _scan.NotifyDoorIdReceived();
            _aura.Log.Info($"[Doors] Door at ({tx},{ty}) own ID: {id}");
        });

        // ── Fish feature wiring ─────────────────────────────────────
        var fishFeature = new FishingFeature(
            _aura.Proxy.EnqueueToServer, _aura.Log,
            () => _aura.Proxy.TryGetPlayer(out var ffx, out var ffy) ? (true, ffx, ffy) : (false, 0f, 0f));
        _aura.Proxy.SetFishingFeature(fishFeature);
        _autoTab.SetFishFeature(fishFeature);

        // ── Surgery feature wiring ──────────────────────────────────
        var surgFeature = new SurgeryFeature(_aura.Proxy.EnqueueToServer, _aura.Log);
        _aura.Proxy.SetSurgeryFeature(surgFeature);
        _autoTab.SetSurgFeature(surgFeature);
        surgFeature.ToolSlotChanged   += slot  => OnUi(() => _overlay.SurgeryHintSlot  = slot);
        surgFeature.ToolRecommended   += label => OnUi(() => _overlay.SurgeryHintLabel = label);

        // ── Load item sprites from game files via ItemDatabase lookup ──
        // Names looked up case-insensitively from the game's own item database.
        // No hardcoded IDs — always correct regardless of game version.
        // Load sprites now (might fail if item db not cached yet)
        LoadSpriteByName(_sprite1, "Lucky Clover");
        LoadSpriteByName(_sprite2, "Green Beer");
        LoadSpriteByName(_sprite3, "Pot O' Gold");

        // Re-try sprite load when items arrive via proxy
        _aura.Proxy.StatusChanged += _ => OnUi(() =>
        {
            if (!_sprite2.Visible) LoadSpriteByName(_sprite2, "Green Beer");
            if (!_sprite1.Visible) LoadSpriteByName(_sprite1, "Lucky Clover");
            if (!_sprite3.Visible) LoadSpriteByName(_sprite3, "Pot O' Gold");
        });

        _leftover = AuraService.HasLeftoverRedirect();
        if (_leftover)
            _aura.Log.Warn("Growtopia is still routed to [AURA] from an earlier session. Press \"Restore connection\" on the Main tab.");

        SelectTab(0);
        UpdateAuraUi();

        // ── Auto Buy feature wiring ─────────────────────────────────
        var autoBuyFeature = new AutoBuyFeature(
            _aura.Proxy.EnqueueToServer,
            _aura.Log);
        _aura.Proxy.SetAutoBuyFeature(autoBuyFeature);
        _autoTab.SetAutoBuyFeature(autoBuyFeature);

        // ── Providers feature wiring ────────────────────────────────
        var providersFeature = new ProvidersFeature(
            _aura.Proxy.EnqueueToServer,
            () => _aura.Proxy.TryGetPlayer(out var pfx, out var pfy) ? (true, pfx, pfy) : (false, 0f, 0f),
            _aura.Log);
        providersFeature.LoadProviderIds(_aura.EnsureItems(_main.GamePath));
        _aura.Proxy.SetProvidersFeature(providersFeature);
        _autoTab.SetProvidersFeature(providersFeature);

        // ── Farm feature wiring ─────────────────────────────────────
        _farmFeature = new FarmFeature(
            _aura.Proxy.EnqueueToServer,
            () => _aura.Proxy.TryGetPlayer(out var fx, out var fy) ? (true, fx, fy) : (false, 0f, 0f),
            _aura.Log);
        _aura.Proxy.SetFarmFeature(_farmFeature);
        _autoTab.SetFarmFeature(_farmFeature);

        _farmTimer.Tick += (_, _) =>
        {
            _farmFeature?.Tick();
            providersFeature.Tick();
            // autoBuyFeature has no Tick - runs event-driven via OnStorePurchaseResult

            // F key toggle — only when Growtopia has focus, no global steal
            if (GrowtopiaHasFocus())
            {
                bool fDown = (GetAsyncKeyState((int)Keys.F) & 0x8000) != 0;
                if (fDown && !_fWasDown) _autoTab.ToggleFarm();
                _fWasDown = fDown;
            }
            else
            {
                _fWasDown = false;
            }
        };
        _farmTimer.Start();

        // ── Proxy commands wiring ───────────────────────────────────
        var proxyCommands = new ProxyCommands();
        proxyCommands.SendToGame = _aura.Proxy.SendToGameClient;
        _aura.Proxy.SetProxyCommands(proxyCommands);

        // F key is polled in _farmTimer.Tick — no global hook needed.

        _timer.Tick += (_, _) => RefreshGameStatus();
        _timer.Start();
        RefreshGameStatus();
    }

    // ── F key detection — poll GetAsyncKeyState in farm timer ────────
    // No global hook, no key stealing. Fires only when Growtopia is
    // the foreground window. Any other app can use F normally.

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private bool _fWasDown;

    private bool GrowtopiaHasFocus()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(fg, sb, 256);
        return sb.ToString().Contains("Growtopia", StringComparison.OrdinalIgnoreCase);
    }

    private static PictureBox MakeSpritePic(Rectangle bounds) => new()
    {
        Bounds    = bounds,
        SizeMode  = PictureBoxSizeMode.Zoom,
        BackColor = Color.Transparent,
        Visible   = false,
    };

    private void LoadSpriteByName(PictureBox pb, string itemName)
    {
        if (_icons == null) return;
        var item = _aura.EnsureItems(_main.GamePath)?.FindByName(itemName);
        if (item == null) return;
        var bmp = _icons.Get(item.Id);
        if (bmp == null) return;
        // Scale to 40x40 for visibility (item icons are 32x32 in the game)
        var scaled = new Bitmap(bmp, new Size(40, 40));
        pb.Image   = scaled;
        pb.Visible = true;
    }

    private void ShowLines(IReadOnlyList<TilePos> tiles, string? name)
    {
        _overlay.SetTargets(tiles);
        if (name != null) _aura.Log.Info($"Lines to {name}: {tiles.Count} tile{(tiles.Count == 1 ? "" : "s")}.");
    }

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated) { action(); return; }
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private void SelectTab(int idx)
    {
        _main.Visible     = idx == 0;
        _proxyTab.Visible = idx == 1;
        _scan.Visible     = idx == 2;
        _auraTab.Visible  = idx == 3;
        _vendTab.Visible  = idx == 4;
        _qolTab.Visible   = idx == 5;

        _tabMain.Selected      = idx == 0;
        _tabAuto.Selected      = idx == 1;
        _tabScan.Selected      = idx == 2;
        _tabAura.Selected      = idx == 3;
        _tabVends.Selected     = idx == 4;
        _tabAuraLower.Selected = idx == 5;
    }

    private void RefreshGameStatus()
    {
        var st = GrowtopiaManager.GetStatus();
        _pill.SetState(st.Running, st.Running ? "Growtopia running" : "Growtopia not running");
        _main.SetGameStatus(st);
    }

    private void UpdateAuraUi()
    {
        var status = _aura.Proxy.Status;
        _main.SetAuraStatus(status, _busy, _leftover, _aura.LastLoginStep);
        _main.SetWorld(_world, status.State);
        _scan.SetAuraState(status);
        _scan.UpdateScan(_world);

        Ui.SetText(_barAura, status.State switch
        {
            AuraState.Connected  => _world != null ? $"[AURA]: {_world.Name}" : "[AURA]: connected",
            AuraState.Connecting => "[AURA]: connecting",
            AuraState.Waiting    => "[AURA]: on",
            _                    => _leftover ? "[AURA]: restore needed" : "[AURA]: off",
        });
        _barAura.ForeColor = status.State == AuraState.Connected ? Theme.Good
                           : _leftover && status.State == AuraState.Off ? Theme.Warn
                           : Theme.TextDim;
    }

    private async void ToggleAura()
    {
        if (_busy) return;
        _busy = true;
        UpdateAuraUi();
        try
        {
            if (_aura.IsRunning)
            {
                var error = await Task.Run(() => _aura.Stop());
                if (error != null) MessageBox.Show(this, error, "[AURA]", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                var result = await _aura.StartAsync(_main.GamePath);
                if (result.Error != null)
                    MessageBox.Show(this, result.Error, "[AURA] could not start", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (result.Warning != null)
                    MessageBox.Show(this, result.Warning, "[AURA] check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _aura.Log.Error(ex.Message);
            MessageBox.Show(this, ex.Message, "[AURA]", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy     = false;
            _leftover = !_aura.IsRunning && AuraService.HasLeftoverRedirect();
            if (!_aura.IsRunning) _world = null;
            UpdateAuraUi();
        }
    }

    private async void RestoreHosts()
    {
        if (_busy) return;
        _busy = true;
        UpdateAuraUi();
        try
        {
            var error = await Task.Run(() => _aura.Restore());
            if (error != null) MessageBox.Show(this, error, "[AURA]", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy     = false;
            _leftover = AuraService.HasLeftoverRedirect();
            UpdateAuraUi();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_aura.IsRunning || _aura.HostsActive)
        {
            var error = _aura.Stop();
            if (error != null)
            {
                MessageBox.Show(this,
                    "Growtopia is still routed to [AURA], so it can't connect without GrowTool77.\n\n" +
                    "Open GrowTool77 again and press \"Restore connection\" on the Main tab.",
                    "[AURA]", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // (no keyboard hook to clean up)
        _overlay.Close();
        _overlay.Dispose();
        _timer.Stop();     _timer.Dispose();
        _farmTimer.Stop(); _farmTimer.Dispose();
        base.OnFormClosed(e);
    }
}
