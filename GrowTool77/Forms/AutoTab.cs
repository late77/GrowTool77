// lang: C#, file: AutoTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// "Auto" tab — dropdown feature selector with start/stop and packet logger toggle.
// Currently ships one feature: Fish.
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class AutoTab : Panel
{
    private readonly ComboBox _selector;
    private readonly GtButton _toggleBtn;
    private readonly Dot      _statusDot;
    private readonly Label    _statusLbl;
    private readonly CheckBox _logCheck;

    private FishingFeature? _fish;

    public AutoTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("Auto features", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        // ── Feature card ────────────────────────────────────────────
        var card = new Card { Bounds = Theme.R(24, 36, 832, 148) };

        card.Controls.Add(Ui.Lbl("Feature", Theme.R(20, 18, 72, 30), Theme.Body, Theme.TextDim, ContentAlignment.MiddleLeft));

        _selector = new ComboBox
        {
            Bounds        = Theme.R(98, 18, 180, 30),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = Theme.Input,
            ForeColor     = Theme.Text,
            Font          = Theme.Body,
        };
        _selector.Items.Add("Fish");
        _selector.SelectedIndex = 0;

        _toggleBtn = new GtButton("Start") { Bounds = Theme.R(288, 18, 120, 30) };
        _toggleBtn.Click += (_, _) => ToggleFeature();

        card.Controls.Add(Ui.Divider(Theme.R(20, 56, 792, 1)));

        _statusDot = new Dot { Bounds = Theme.R(20, 72, 14, 14) };
        _statusLbl = Ui.Lbl("Stopped", Theme.R(42, 64, 560, 28), Theme.Body, Theme.TextDim);

        card.Controls.Add(Ui.Divider(Theme.R(20, 100, 792, 1)));

        _logCheck = new CheckBox
        {
            Text      = "Log all fishing packets to [AURA] tab (use this to find the real packet names)",
            Bounds    = Theme.R(20, 110, 700, 26),
            Font      = Theme.Small,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Card,
            AutoSize  = false,
        };
        _logCheck.CheckedChanged += (_, _) =>
        {
            if (_fish != null) _fish.LoggingEnabled = _logCheck.Checked;
        };

        card.Controls.AddRange(new Control[] { _selector, _toggleBtn, _statusDot, _statusLbl, _logCheck });
        Controls.Add(card);

        // ── Hint card ───────────────────────────────────────────────
        var hint = new Card { Bounds = Theme.R(24, 196, 832, 76) };
        var hintLbl = Ui.Lbl(
            "First run: tick the logger → cast once in-game → check [AURA] tab for [Fish] lines.\n" +
            "Copy the real packet names into FishingFeature.cs (FnFishBite + PullAction), rebuild, untick logger.",
            Theme.R(20, 12, 792, 52), Theme.Small, Theme.TextDim, ContentAlignment.TopLeft);
        hintLbl.AutoEllipsis = false;
        hint.Controls.Add(hintLbl);
        Controls.Add(hint);
    }

    public void SetFishFeature(FishingFeature fish)
    {
        if (_fish != null) _fish.StatusChanged -= OnFishStatus;
        _fish                = fish;
        _fish.LoggingEnabled = _logCheck.Checked;
        _fish.StatusChanged += OnFishStatus;
    }

    private void ToggleFeature()
    {
        if (_fish == null) return;
        if (_fish.IsRunning)
        {
            _fish.Stop();
            _toggleBtn.Kind = ButtonKind.Primary;
            Ui.SetText(_toggleBtn, "Start");
        }
        else
        {
            _fish.Start();
            _toggleBtn.Kind = ButtonKind.Danger;
            Ui.SetText(_toggleBtn, "Stop");
        }
        _toggleBtn.Invalidate();
    }

    private void OnFishStatus(string status)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFishStatus(status)); return; }
        bool on = _fish?.IsRunning ?? false;
        _statusDot.DotColor = on ? Theme.Good : Theme.Bad;
        Ui.SetText(_statusLbl, status);
    }
}
