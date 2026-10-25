using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class MainTab : Panel
{
    private readonly Dot      _dot;
    private readonly Label    _statusLbl;
    private readonly Label    _statusSub;
    private readonly GtButton _launchBtn;
    private readonly InputBox _path;

    private readonly Dot      _auraDot;
    private readonly Label    _auraLbl;
    private readonly Label    _auraSub;
    private readonly GtButton _auraBtn;
    private readonly GtButton _restoreBtn;
    private readonly Label    _auraHint;

    private readonly Label    _worldLbl;
    private readonly Label    _worldSub;

    public event Action? AuraToggleRequested;
    public event Action? RestoreRequested;

    public string GamePath => _path.Box.Text.Trim();

    public MainTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        // ── Growtopia ───────────────────────────────────────────────
        Controls.Add(Ui.Lbl("Growtopia", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var game = new Card { Bounds = Theme.R(24, 36, 832, 136) };

        _dot       = new Dot { Bounds = Theme.R(22, 25, 14, 14) };
        _statusLbl = Ui.Lbl("Checking…", Theme.R(46, 12, 440, 38), Theme.Heading, Theme.Text);
        _statusSub = Ui.Lbl("", Theme.R(47, 48, 440, 20), Theme.Small, Theme.TextDim);

        _launchBtn = new GtButton("Launch Growtopia") { Bounds = Theme.R(612, 20, 200, 42) };
        _launchBtn.Click += (_, _) => Launch();

        _path = new InputBox { Bounds = Theme.R(112, 92, 580, 34) };
        _path.Box.Text            = GrowtopiaManager.FindExePath() ?? "";
        _path.Box.PlaceholderText = "Select Growtopia.exe";

        var browse = new GtButton("Browse", ButtonKind.Secondary) { Bounds = Theme.R(702, 92, 110, 34) };
        browse.Click += (_, _) => Browse();

        game.Controls.AddRange(new Control[]
        {
            _dot, _statusLbl, _statusSub, _launchBtn,
            Ui.Divider(Theme.R(20, 80, 792, 1)),
            Ui.Lbl("Game path", Theme.R(20, 92, 88, 34), Theme.Body, Theme.TextDim),
            _path, browse,
        });
        Controls.Add(game);

        // ── [AURA]->proxy ───────────────────────────────────────────
        Controls.Add(Ui.Lbl("[AURA]->proxy", Theme.R(28, 188, 300, 22), Theme.Section, Theme.Gold));

        var aura = new Card { Bounds = Theme.R(24, 212, 410, 124) };

        _auraDot = new Dot { Bounds = Theme.R(20, 24, 14, 14), DotColor = Theme.Bad };
        _auraLbl = Ui.Lbl("[AURA] off", Theme.R(44, 12, 346, 38), Theme.Heading, Theme.Text);
        _auraSub = Ui.Lbl("Start [AURA] before you start Growtopia.", Theme.R(45, 48, 346, 20), Theme.Small, Theme.TextDim);

        _auraBtn = new GtButton("Start [AURA]") { Bounds = Theme.R(20, 76, 170, 36) };
        _auraBtn.Click += (_, _) => AuraToggleRequested?.Invoke();

        _restoreBtn = new GtButton("Restore connection", ButtonKind.Secondary) { Bounds = Theme.R(200, 76, 190, 36), Visible = false };
        _restoreBtn.Click += (_, _) => RestoreRequested?.Invoke();

        _auraHint = Ui.Lbl("Windows asks for permission when you start and stop.",
            Theme.R(200, 74, 190, 40), Theme.Small, Theme.TextDim, ContentAlignment.MiddleLeft);
        _auraHint.AutoEllipsis = false;

        aura.Controls.AddRange(new Control[] { _auraDot, _auraLbl, _auraSub, _auraBtn, _restoreBtn, _auraHint });
        Controls.Add(aura);

        // ── Current world ───────────────────────────────────────────
        Controls.Add(Ui.Lbl("Current world", Theme.R(450, 188, 300, 22), Theme.Section, Theme.Gold));

        var world = new Card { Bounds = Theme.R(446, 212, 410, 124) };

        _worldLbl = Ui.Lbl("—", Theme.R(20, 14, 370, 54), Theme.Big, Theme.TextDim);
        _worldSub = Ui.Lbl("Start [AURA] to see the world you are in.", Theme.R(22, 70, 370, 40), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft);
        _worldSub.AutoEllipsis = false;

        world.Controls.AddRange(new Control[] { _worldLbl, _worldSub });
        Controls.Add(world);

        // ── Shadow ban reset ──────────────────────────────────────────
        Controls.Add(Ui.Lbl("Shadow ban bypass", Theme.R(28, 352, 300, 22), Theme.Section, Theme.Gold));

        var resetCard = new Card { Bounds = Theme.R(24, 376, 832, 96) };
        resetCard.Controls.Add(Ui.Lbl(
            "Resets Growtopia device fingerprint (save.dat + registry). Combine with airplane mode on mobile for full IP+device reset.",
            Theme.R(20, 8, 600, 36), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft));

        var resetBtn  = new GtButton("Reset GT Fingerprint", ButtonKind.Danger) { Bounds = Theme.R(20, 50, 220, 36) };
        var resetLbl  = Ui.Lbl("Stops Growtopia, clears device ID, requires restart", Theme.R(254, 56, 560, 26), Theme.Small, Theme.TextDim);
        resetBtn.Click += (_, _) => ResetFingerprint(resetLbl);
        resetCard.Controls.AddRange(new Control[] { resetBtn, resetLbl });
        Controls.Add(resetCard);
    }

    public void SetGameStatus(GameStatus st)
    {
        _dot.DotColor = st.Running ? Theme.Good : Theme.Bad;
        Ui.SetText(_statusLbl, st.Running ? "Growtopia is running" : "Growtopia is not running");
        Ui.SetText(_statusSub, st.Running ? $"Process ID {st.Pid}" : "Launch it here or from the Start menu.");
        _launchBtn.Enabled = !st.Running;
        Ui.SetText(_launchBtn, st.Running ? "Running" : "Launch Growtopia");
    }

    public void SetAuraStatus(AuraStatus status, bool busy, bool leftover, string? loginStep = null)
    {
        bool on = status.State != AuraState.Off;

        _auraDot.DotColor = status.State switch
        {
            AuraState.Connected  => Theme.Good,
            AuraState.Connecting => Theme.Warn,
            AuraState.Waiting    => Theme.Warn,
            _                    => Theme.Bad,
        };

        Ui.SetText(_auraLbl, status.State switch
        {
            AuraState.Connected  => "[AURA] connected",
            AuraState.Connecting => "[AURA] connecting",
            AuraState.Waiting    => "[AURA] on",
            _                    => "[AURA] off",
        });

        string sub = status.State switch
        {
            AuraState.Off     => leftover ? "Growtopia is still routed to [AURA] from last time." : "Start [AURA] before you start Growtopia.",
            AuraState.Waiting => "Waiting for Growtopia. Start it now, or restart it if it's open.",
            _                 => status.Detail,
        };
        Ui.SetText(_auraSub, busy ? "Working… (Windows may ask for permission)" : sub);

        _auraBtn.Kind    = on ? ButtonKind.Danger : ButtonKind.Primary;
        _auraBtn.Enabled = !busy;
        Ui.SetText(_auraBtn, busy ? "Please wait…" : on ? "Stop [AURA]" : "Start [AURA]");
        _auraBtn.Invalidate();

        _restoreBtn.Visible = leftover && !on;
        _restoreBtn.Enabled = !busy;
        _auraHint.Visible   = !_restoreBtn.Visible;

        bool failed = loginStep != null && loginStep.Contains("failed", StringComparison.Ordinal);
        Ui.SetText(_auraHint, !on || loginStep == null
            ? "Windows asks for permission when you start and stop."
            : failed
                ? $"{StepName(loginStep)} failed. Details on the [AURA] tab."
                : "Login ok: Growtopia was sent to [AURA].");
        _auraHint.ForeColor = on && failed ? Theme.Warn : Theme.TextDim;
    }

    // "Login step 1 failed: …" → "Login step 1"
    private static string StepName(string step)
    {
        int colon = step.IndexOf(':');
        var head = colon > 0 ? step[..colon] : step;
        return head.Replace(" failed", "", StringComparison.Ordinal).Trim();
    }

    public void SetWorld(WorldView? view, AuraState state)
    {
        if (view == null)
        {
            Ui.SetText(_worldLbl, "—");
            _worldLbl.ForeColor = Theme.TextDim;
            Ui.SetText(_worldSub, state == AuraState.Off
                ? "Start [AURA] to see the world you are in."
                : "Not in a world.");
            return;
        }

        Ui.SetText(_worldLbl, view.Name);
        _worldLbl.ForeColor = Theme.Gold;
        int stacks = view.Items.Sum(i => i.Stacks);
        Ui.SetText(_worldSub, $"{stacks} dropped stack{(stacks == 1 ? "" : "s")} · {view.Gems:N0} gems on the floor. Details on the World tab.");
    }

    private void Launch()
    {
        var result = GrowtopiaManager.Launch(_path.Box.Text);
        if (!result.Success)
            MessageBox.Show(result.Message, "Could not launch Growtopia",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ResetFingerprint(Label statusLbl)
    {
        // Kill Growtopia process
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Growtopia"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { /* ignore */ }
        }

        var errors = new System.Text.StringBuilder();

        // Delete save.dat (contains rid = Random ID, wk = Windows Key)
        string saveDat = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Growtopia", "save.dat");
        if (File.Exists(saveDat))
        {
            try { File.Delete(saveDat); }
            catch (Exception ex) { errors.AppendLine($"save.dat: {ex.Message}"); }
        }

        // Clear registry key (login history, tokens)
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Growtopia", writable: true);
            if (key != null)
                foreach (var name in key.GetValueNames())
                    try { key.DeleteValue(name); } catch { /* ignore */ }
        }
        catch (Exception ex) { errors.AppendLine($"Registry: {ex.Message}"); }

        if (errors.Length > 0)
        {
            Ui.SetText(statusLbl, $"Partial reset — {errors}");
        }
        else
        {
            Ui.SetText(statusLbl, "Done — new device ID on next GT launch. Now toggle airplane mode on mobile for new IP.");
            statusLbl.ForeColor = Theme.Good;
        }
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Select Growtopia.exe",
            Filter = "Growtopia|Growtopia.exe|Programs|*.exe",
        };
        var dir = Path.GetDirectoryName(_path.Box.Text);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Box.Text = dlg.FileName;
    }
}
