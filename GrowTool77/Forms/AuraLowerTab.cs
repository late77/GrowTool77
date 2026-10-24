// lang: C#, file: AuraLowerTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// "aura" tab — in-game proxy command reference.
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class AuraLowerTab : Panel
{
    public AuraLowerTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("In-game commands", Theme.R(28, 12, 400, 22), Theme.Section, Theme.Gold));

        // ── Destination / ID commands ────────────────────────────────
        var destCard = new Card { Bounds = Theme.R(24, 36, 832, 196) };
        destCard.Controls.Add(Ui.Lbl("Destination & ID fill", Theme.R(20, 12, 400, 22), Theme.BodyBold, Theme.Gold));

        AddRow(destCard, "/pdest <N> [lock]",
               "Prepares N random 7-char destinations (:XXXXXXX). " +
               "Then wrench doors/portals in order — each gets the next pre-filled destination. " +
               "Add 'lock' to also lock each door. Packet never reaches the server.",
               Theme.R(20, 38, 792, 44));

        AddRow(destCard, "/pid",
               "Assigns IDs matching the /pdest session (same values, no colon, same order). " +
               "Run after /pdest. Wrench the same doors/portals in order to assign each door's own ID.",
               Theme.R(20, 96, 792, 44));

        AddRow(destCard, "/cldd",
               "Clears all stored destinations and IDs. Shows 'done!' in-game. " +
               "Use to reset a session and start fresh.",
               Theme.R(20, 152, 792, 32));

        Controls.Add(destCard);

        // ── Farm commands ────────────────────────────────────────────
        var farmCard = new Card { Bounds = Theme.R(24, 246, 832, 80) };
        farmCard.Controls.Add(Ui.Lbl("Auto farm", Theme.R(20, 12, 400, 22), Theme.BodyBold, Theme.Gold));
        AddRow(farmCard, "F  (global hotkey)",
               "Start / Stop the configured auto-farm. Farm must be set up in the Auto tab first.",
               Theme.R(20, 36, 792, 32));
        Controls.Add(farmCard);
    }

    private static void AddRow(Card parent, string command, string description, Rectangle bounds)
    {
        var lbl = new Label
        {
            Bounds    = bounds,
            Font      = Theme.Small,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Text      = $"`{command}`   {description}",
        };
        // Highlight the command part
        var cmd = new Label
        {
            Bounds    = new Rectangle(bounds.X, bounds.Y, 230, 22),
            Font      = Theme.SmallBold,
            ForeColor = Theme.Gold,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Text      = command,
        };
        var desc = new Label
        {
            Bounds    = new Rectangle(bounds.X + 238, bounds.Y, bounds.Width - 238, bounds.Height),
            Font      = Theme.Small,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Text      = description,
        };
        parent.Controls.Add(cmd);
        parent.Controls.Add(desc);
    }
}
