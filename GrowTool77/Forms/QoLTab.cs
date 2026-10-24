// lang: C#, file: QoLTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// Quality of Life tab — convenience automations that run transparently.
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class QoLTab : Panel
{
    private readonly CheckBox _skipGazette;

    public bool SkipGazette => _skipGazette.Checked;
    public event Action<bool>? SkipGazetteChanged;

    public void ApplySettings(GrowTool77.Modules.AppSettings s)
    {
        _skipGazette.Checked = s.SkipGazette;
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

        // ── More QoL features placeholder ────────────────────────────
        var futureCard = new Card { Bounds = Theme.R(24, 148, 832, 60) };
        futureCard.Controls.Add(Ui.Lbl("More Quality of Life features coming soon.", Theme.R(20, 18, 600, 24), Theme.Body, Theme.TextDim));
        Controls.Add(futureCard);
    }
}
