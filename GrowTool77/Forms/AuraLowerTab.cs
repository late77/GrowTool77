// lang: C#, file: AuraLowerTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// "aura" tab — placeholder; fill in content as needed.
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class AuraLowerTab : Panel
{
    public AuraLowerTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("aura", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var card = new Card { Bounds = Theme.R(24, 36, 832, 72) };
        card.Controls.Add(Ui.Lbl("Coming soon.", Theme.R(20, 20, 400, 32), Theme.Body, Theme.TextDim));
        Controls.Add(card);
    }
}
