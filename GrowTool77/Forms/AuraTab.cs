using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

// Shows the in-memory [AURA] log. Nothing here is saved to disk.
public class AuraTab : Panel
{
    private const int MaxChars = 120_000;

    private readonly AuraLog _log;
    private readonly TextBox _text;

    public event Action<bool>? DebugDropsChanged;
    public event Action<bool>? VerboseLogChanged;
    public event Action?       SaveLinesSnapshot;

    public AuraTab(AuraLog log)
    {
        _log           = log;
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("[AURA] log", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var card = new Card { Bounds = Theme.R(24, 36, 832, 298) }; // shortened to fit verbose checkbox below
        _text = new TextBox
        {
            Bounds      = Theme.R(16, 14, 800, 270),
            Multiline   = true,
            ReadOnly    = true,
            WordWrap    = false,
            ScrollBars  = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor   = Theme.Card,
            ForeColor   = Theme.Text,
            Font        = new Font("Consolas", 12 * Theme.Scale, FontStyle.Regular, GraphicsUnit.Pixel),
        };
        card.Controls.Add(_text);
        Controls.Add(card);

        var copy = new GtButton("Copy log", ButtonKind.Secondary) { Bounds = Theme.R(24, 374, 130, 36) };
        copy.Click += (_, _) => CopyLog();

        var clear = new GtButton("Clear", ButtonKind.Secondary) { Bounds = Theme.R(164, 374, 100, 36) };
        clear.Click += (_, _) =>
        {
            _log.Clear();
            _text.Clear();
        };

        var note = Ui.Lbl(
            "Memory only. Never contains login, tokens or chat.",
            Theme.R(690, 374, 166, 36), Theme.Small, Theme.TextDim, ContentAlignment.MiddleLeft);
        note.AutoEllipsis = false;

        var debug = new CheckBox
        {
            Text      = "Log drop packets (debug)",
            Bounds    = Theme.R(280, 374, 230, 36),
            Font      = Theme.Small,
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            AutoSize  = false,
        };
        debug.CheckedChanged += (_, _) => DebugDropsChanged?.Invoke(debug.Checked);

        // Verbose mode: logs ALL packets in both directions — use to discover dialog formats
        var verbose = new CheckBox
        {
            Text      = "Log ALL packets (verbose — use to find dialog IDs)",
            Bounds    = Theme.R(280, 340, 430, 30),
            Font      = Theme.Small,
            ForeColor = Color.FromArgb(255, 220, 100),
            BackColor = Theme.Bg,
            AutoSize  = false,
        };
        verbose.CheckedChanged += (_, _) => VerboseLogChanged?.Invoke(verbose.Checked);

        var snapshot = new GtButton("Save lines snapshot", ButtonKind.Secondary) { Bounds = Theme.R(516, 374, 166, 36) };
        snapshot.Click += (_, _) => SaveLinesSnapshot?.Invoke();

        Controls.AddRange(new Control[] { copy, clear, debug, verbose, snapshot, note });

        foreach (var line in _log.Lines()) Append(line);
    }

    public void Append(string line)
    {
        if (_text.TextLength > MaxChars)
        {
            _text.Text = _text.Text[(_text.TextLength - MaxChars / 2)..];
        }
        _text.AppendText(line + Environment.NewLine);
    }

    private void CopyLog()
    {
        var all = string.Join(Environment.NewLine, _log.Lines());
        if (all.Length == 0) return;
        try
        {
            Clipboard.SetText(all);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy (another app holds it); the user can try again.
        }
    }
}
