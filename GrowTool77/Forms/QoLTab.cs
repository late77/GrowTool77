// lang: C#, file: QoLTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// Quality of Life tab — convenience automations that run transparently.
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class QoLTab : Panel
{
    private readonly CheckBox _skipGazette;
    private readonly CheckBox _ghostFinder;
    private readonly Label    _ghostCountLbl;

    public bool SkipGazette => _skipGazette.Checked;
    public bool GhostFinderEnabled => _ghostFinder.Checked;
    public event Action<bool>? SkipGazetteChanged;
    public event Action<bool>? GhostFinderChanged;

    public void ApplySettings(GrowTool77.Modules.AppSettings s)
    {
        _skipGazette.Checked = s.SkipGazette;
        _ghostFinder.Checked = s.GhostFinder;
        s.Save();
    }

    public QoLTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("Quality of Life", Theme.R(28, 12, 400, 22), Theme.Section, Theme.Gold));

        // ── News / Gazette ────────────────────────────────────────────
        var newsCard = new Card { Bounds = Theme.R(24, 36, 832, 100) };
        newsCard.Controls.Add(Ui.Lbl("Login & News", Theme.R(20, 12, 300, 20), Theme.BodyBold, Theme.Gold));

        _skipGazette = new CheckBox
        {
            Text      = "Auto-dismiss Gazette (skip login news dialog automatically)",
            Bounds    = Theme.R(20, 38, 700, 28),
            Font      = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            AutoSize  = false,
        };
        _skipGazette.CheckedChanged += (_, _) => SkipGazetteChanged?.Invoke(_skipGazette.Checked);
        newsCard.Controls.Add(_skipGazette);
        newsCard.Controls.Add(Ui.Lbl(
            "When checked, the Growtopia Gazette dialog that appears on login is dismissed instantly — you never see the news screen.",
            Theme.R(20, 66, 792, 22), Theme.Small, Theme.TextDim));
        Controls.Add(newsCard);

        // ── Ghost Finder ──────────────────────────────────────────────
        var ghostCard = new Card { Bounds = Theme.R(24, 148, 832, 110) };
        ghostCard.Controls.Add(Ui.Lbl("Ghost Finder", Theme.R(20, 10, 300, 22), Theme.BodyBold, Theme.Gold));
        ghostCard.Controls.Add(Ui.Lbl(
            "Automatically draws overlay lines to any ghost-type items dropped on the floor when you enter a world.",
            Theme.R(20, 32, 792, 20), Theme.Small, Theme.TextDim));

        _ghostFinder = new CheckBox
        {
            Text      = "Auto-highlight ghosts on the floor",
            Bounds    = Theme.R(20, 58, 400, 24),
            Font      = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            AutoSize  = false,
        };
        _ghostFinder.CheckedChanged += (_, _) =>
        {
            GhostFinderChanged?.Invoke(_ghostFinder.Checked);
            ApplySettings(new GrowTool77.Modules.AppSettings
            {
                SkipGazette = _skipGazette.Checked,
                GhostFinder = _ghostFinder.Checked
            });
        };

        _ghostCountLbl = Ui.Lbl("", Theme.R(440, 60, 350, 22), Theme.Small, Theme.TextDim);
        ghostCard.Controls.AddRange(new Control[] { _ghostFinder, _ghostCountLbl });
        Controls.Add(ghostCard);
    }

    public void UpdateGhostCount(int count)
    {
        if (IsDisposed) return;
        Action a = () =>
        {
            _ghostCountLbl.Text      = count > 0 ? $"{count} ghost{(count == 1 ? "" : "s")} found!" : "";
            _ghostCountLbl.ForeColor = count > 0 ? Color.FromArgb(160, 220, 255) : Theme.TextDim;
        };
        if (InvokeRequired) BeginInvoke(a); else a();
    }
}
