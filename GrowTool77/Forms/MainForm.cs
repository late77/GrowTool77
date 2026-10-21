// lang: C#, file: MainForm.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// v0.7: added "Auto" tab (index 1, after Main) and "aura" tab (index 5, last).
// Tab order: Main(0) | Auto(1) | World(2) | [AURA](3) | Vends(4) | aura(5)
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class MainForm : Form
{
    private readonly AuraService _aura = new();

    private readonly MainTab      _main;
    private readonly AutoTab      _autoTab;
    private readonly ScanTab      _scan;
    private readonly AuraTab      _auraTab;
    private readonly VendTab      _vendTab;
    private readonly AuraLowerTab _auraLowerTab;
    private readonly LineOverlay  _overlay;

    private readonly TabButton _tabMain;
    private readonly TabButton _tabAuto;
    private readonly TabButton _tabScan;
    private readonly TabButton _tabAura;
    private readonly TabButton _tabVends;
    private readonly TabButton _tabAuraLower;

    private readonly StatusPill _pill;
    private readonly Label      _barAura;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };

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

        _pill         = new StatusPill { Bounds = Theme.R(640, 18, 216, 34) };

        _tabMain      = new TabButton("Main")    { Bounds = Theme.R(24,  70, 130, 38) };
        _tabAuto      = new TabButton("Auto")    { Bounds = Theme.R(158, 70, 130, 38) };
        _tabScan      = new TabButton("World")   { Bounds = Theme.R(292, 70, 130, 38) };
        _tabAura      = new TabButton("[AURA]")  { Bounds = Theme.R(426, 70, 130, 38) };
        _tabVends     = new TabButton("Vends")   { Bounds = Theme.R(560, 70, 130, 38) };
        _tabAuraLower = new TabButton("aura")    { Bounds = Theme.R(694, 70, 130, 38) };

        _tabMain.Click      += (_, _) => SelectTab(0);
        _tabAuto.Click      += (_, _) => SelectTab(1);
        _tabScan.Click      += (_, _) => SelectTab(2);
        _tabAura.Click      += (_, _) => SelectTab(3);
        _tabVends.Click     += (_, _) => SelectTab(4);
        _tabAuraLower.Click += (_, _) => SelectTab(5);

        top.Controls.AddRange(new Control[]
        {
            Ui.Lbl("☘", Theme.R(16, 6, 48, 56), Theme.Symbol, Theme.Gold, ContentAlignment.MiddleCenter),
            Ui.Lbl("GrowTool77", Theme.R(68, 10, 300, 32), Theme.Title, Theme.Text),
            Ui.Lbl("by Team Aura", Theme.R(69, 42, 300, 18), Theme.SmallBold, Theme.Gold),
            _pill, _tabMain, _tabAuto, _tabScan, _tabAura, _tabVends, _tabAuraLower,
        });
        Controls.Add(top);

        // ── Pages ───────────────────────────────────────────────────
        _main         = new MainTab                { Bounds = Theme.R(0, 108, 880, 424) };
        _autoTab      = new AutoTab                { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _scan         = new ScanTab                { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _auraTab      = new AuraTab(_aura.Log)     { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _vendTab      = new VendTab                { Bounds = Theme.R(0, 108, 880, 424), Visible = false };
        _auraLowerTab = new AuraLowerTab           { Bounds = Theme.R(0, 108, 880, 424), Visible = false };

        Controls.Add(_main);
        Controls.Add(_autoTab);
        Controls.Add(_scan);
        Controls.Add(_auraTab);
        Controls.Add(_vendTab);
        Controls.Add(_auraLowerTab);

        // ── Status bar ──────────────────────────────────────────────
        var bar = new Panel { Bounds = Theme.R(0, 532, 880, 28), BackColor = Theme.HeaderDeep };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Gold, Math.Max(1, Theme.S(2)));
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };
        _barAura = Ui.Lbl("[AURA]: off", Theme.R(566, 2, 300, 26), Theme.Small, Theme.TextDim, ContentAlignment.MiddleRight);
        bar.Controls.Add(Ui.Lbl("☘  GrowTool77 v2.1", Theme.R(14, 2, 400, 26), Theme.Small, Theme.TextDim));
        bar.Controls.Add(_barAura);
        Controls.Add(bar);

        // ── [AURA] wiring ───────────────────────────────────────────
        _overlay = new LineOverlay(
            () => _aura.Proxy.TryGetPlayer(out var x, out var y) ? (true, x, y) : (false, 0f, 0f),
            message => _aura.Log.Info(message));

        _main.AuraToggleRequested += ToggleAura;
        _main.RestoreRequested    += RestoreHosts;

        _aura.StatusChanged += _ => OnUi(UpdateAuraUi);
        _aura.WorldChanged  += view => OnUi(() =>
        {
            _world = view;
            _overlay.SetWorld(view);
            _vendTab.SetCurrentWorld(view?.Name);
            UpdateAuraUi();
        });
        _aura.Log.LineAdded += line => OnUi(() => _auraTab.Append(line));
        _aura.LoginStep     += _ => OnUi(UpdateAuraUi);
        _aura.VendsChanged  += vends => OnUi(() => _vendTab.SetVends(vends));
        _auraTab.DebugDropsChanged += on =>
        {
            _aura.Proxy.DebugDrops = on;
            _aura.Log.Info(on ? "Drop packet logging is on." : "Drop packet logging is off.");
        };

        var icons = new ItemIcons(_aura.EnsureItems(_main.GamePath), _main.GamePath);
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

        // ── Fish feature wiring ─────────────────────────────────────
        var fishFeature = new FishingFeature(_aura.Proxy.EnqueueToServer, _aura.Log);
        _aura.Proxy.SetFishingFeature(fishFeature);
        _autoTab.SetFishFeature(fishFeature);

        _leftover = AuraService.HasLeftoverRedirect();
        if (_leftover)
            _aura.Log.Warn("Growtopia is still routed to [AURA] from an earlier session. Press \"Restore connection\" on the Main tab.");

        SelectTab(0);
        UpdateAuraUi();

        _timer.Tick += (_, _) => RefreshGameStatus();
        _timer.Start();
        RefreshGameStatus();
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
        _main.Visible         = idx == 0;
        _autoTab.Visible      = idx == 1;
        _scan.Visible         = idx == 2;
        _auraTab.Visible      = idx == 3;
        _vendTab.Visible      = idx == 4;
        _auraLowerTab.Visible = idx == 5;

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
        _overlay.Close();
        _overlay.Dispose();
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
