using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

// Vend locator: every vending machine seen this session, cheapest first per
// item. Type to narrow the list; right-click a vend in the world you're in to
// draw a line to it. Nothing is saved to disk.
public class VendTab : Panel
{
    private readonly InputBox _filter;
    private readonly Label    _status;
    private readonly Label    _selected;
    private readonly Label    _empty;
    private readonly ItemListView _list;
    private readonly ContextMenuStrip _menu = new();

    private ItemIcons? _icons;
    private IReadOnlyList<WorldVends> _vends = Array.Empty<WorldVends>();
    private string? _currentWorld;
    private (string World, int ItemId, int X, int Y, bool All)? _lines;
    private bool _publishing;

    public event Action<IReadOnlyList<TilePos>, string?>? LinesChanged;
    public event Action<PointerStyle>? PointerStyleChanged;

    public PointerStyle PointerStyle => _pointer.SelectedIndex == 1 ? PointerStyle.Arrow : PointerStyle.Line;

    // Where the player stands (world pixels), for "Nearest first".
    public Func<(bool Ok, float X, float Y)>? PlayerPosition { get; set; }

    private static readonly string[] SortNames =
    {
        "Cheapest first", "Most expensive first", "Nearest first (this world)",
        "Rarest first", "Most common first", "Name A–Z", "World A–Z",
    };
    private readonly ComboBox _sort;
    private readonly ComboBox _pointer;

    private sealed record RowTag(string World, int ItemId, int X, int Y, string Name, int Price) : IHasItem;

    public VendTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        // ── Filter ──────────────────────────────────────────────────
        Controls.Add(Ui.Lbl("Vend locator", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var card = new Card { Bounds = Theme.R(24, 36, 832, 92) };

        _filter = new InputBox { Bounds = Theme.R(20, 14, 420, 34) };
        _filter.Box.PlaceholderText = "Type an item name to narrow the list, e.g. high";
        _filter.Box.TextChanged += (_, _) => Render();

        _status = Ui.Lbl("No worlds scanned yet.", Theme.R(456, 14, 356, 34), Theme.Small, Theme.TextDim);

        var hint = Ui.Lbl(
            "Every world you enter while [AURA] is on is scanned automatically. Right-click a vend in the world you're in to point to it. Nothing is saved.",
            Theme.R(20, 54, 792, 30), Theme.Small, Theme.TextDim, ContentAlignment.MiddleLeft);
        hint.AutoEllipsis = false;

        card.Controls.AddRange(new Control[] { _filter, _status, hint });
        Controls.Add(card);

        // ── Order and pointer ───────────────────────────────────────
        Controls.Add(Ui.Lbl("Sort", Theme.R(28, 134, 46, 30), Theme.Section, Theme.Gold, ContentAlignment.MiddleLeft));
        _sort = Combo(Theme.R(78, 134, 250, 30), SortNames);
        _sort.SelectedIndexChanged += (_, _) => Render();

        Controls.Add(Ui.Lbl("Pointer", Theme.R(344, 134, 74, 30), Theme.Section, Theme.Gold, ContentAlignment.MiddleLeft));
        _pointer = Combo(Theme.R(420, 134, 150, 30), new[] { "Line", "Arrow" });
        _pointer.SelectedIndexChanged += (_, _) => PointerStyleChanged?.Invoke(PointerStyle);

        _selected = Ui.Lbl("", Theme.R(580, 134, 276, 30), Theme.Small, Theme.TextDim, ContentAlignment.MiddleRight);
        Controls.AddRange(new Control[] { _sort, _pointer, _selected });

        // ── List ────────────────────────────────────────────────────

        var listCard = new Card { Bounds = Theme.R(24, 164, 832, 248) };
        _list = new ItemListView { Bounds = Theme.R(12, 10, 808, 228) };
        _list.Columns.Add("Item",     Theme.S(250));
        _list.Columns.Add("World",    Theme.S(160));
        _list.Columns.Add("Position", Theme.S(90));
        _list.Columns.Add("Price",    Theme.S(150));
        _list.Columns.Add("Per item", Theme.S(120));
        _list.MouseUp += OnListMouseUp;

        _empty = Ui.Lbl("", Theme.R(12, 90, 808, 60), Theme.Body, Theme.TextDim, ContentAlignment.MiddleCenter);
        _empty.BackColor    = Theme.Card;
        _empty.AutoEllipsis = false;

        listCard.Controls.Add(_list);
        listCard.Controls.Add(_empty);
        _empty.BringToFront();
        Controls.Add(listCard);

        Render();
    }

    private static ComboBox Combo(Rectangle bounds, string[] items)
    {
        var box = new ComboBox
        {
            Bounds        = bounds,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = Theme.Input,
            ForeColor     = Theme.Text,
            Font          = Theme.Body,
        };
        box.Items.AddRange(items);
        box.SelectedIndex = 0;
        return box;
    }

    public void SetIcons(ItemIcons icons)
    {
        _icons = icons;
        _list.Icons = icons;
        UpdateStatus();
        Render();
    }

    public void SetVends(IReadOnlyList<WorldVends> vends)
    {
        _vends = vends;
        UpdateStatus();
        Render();
        PublishLines();
    }

    public void SetCurrentWorld(string? world)
    {
        if (string.Equals(world, _currentWorld, StringComparison.OrdinalIgnoreCase)) return;
        _currentWorld = world;
        _lines = null;                                     // lines belong to the world they were made in
        Render();
        PublishLines();
    }

    public void ClearLines()
    {
        _publishing = false;
        if (_lines == null) return;
        _lines = null;
        Render();
    }

    private void UpdateStatus()
    {
        int worlds   = _vends.Count;
        int machines = _vends.Sum(w => w.Vends.Count(v => v.IsForSale));
        Ui.SetText(_status, worlds == 0
            ? "No worlds scanned yet. Start [AURA] and enter worlds."
            : $"{worlds} world{(worlds == 1 ? "" : "s")} scanned · {machines} vend{(machines == 1 ? "" : "s")} for sale");
    }

    private string NameOf(int itemId) => _icons?.Item(itemId)?.Name is { Length: > 0 } n ? n : $"Item #{itemId}";

    private void Render()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        var filter = _filter.Box.Text;
        var all = _vends
            .SelectMany(w => w.Vends.Where(v => v.IsForSale).Select(v => (World: w.World, Vend: v, Partial: w.Partial)))
            .Select(r => (r.World, r.Vend, r.Partial, Name: NameOf(r.Vend.ItemId)))
            .Where(r => ListFilter.Matches(r.Name, filter))
            .ToList();

        // The cheapest vend of each item is gold, whatever the order.
        var cheapest = all.GroupBy(r => r.Vend.ItemId).ToDictionary(g => g.Key, g => g.Min(r => r.Vend.WlPerItem));

        var (havePlayer, ppx, ppy) = PlayerPosition?.Invoke() ?? (false, 0f, 0f);
        double Distance((string World, VendInfo Vend, bool Partial, string Name) r)
        {
            if (!havePlayer || !string.Equals(r.World, _currentWorld, StringComparison.OrdinalIgnoreCase)) return double.MaxValue;
            double dx = r.Vend.X * 32 + 16 - (ppx + 10), dy = r.Vend.Y * 32 + 16 - (ppy + 15);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        int Rarity(int id) => _icons?.Item(id)?.Rarity ?? 999;
        var byName = StringComparer.OrdinalIgnoreCase;

        var rows = (_sort.SelectedIndex switch
        {
            1 => all.OrderByDescending(r => r.Vend.WlPerItem).ThenBy(r => r.Name, byName),
            2 => all.OrderBy(Distance).ThenBy(r => r.Vend.WlPerItem),
            3 => all.OrderBy(r => Rarity(r.Vend.ItemId) >= 999 ? 1 : 0).ThenByDescending(r => Rarity(r.Vend.ItemId)).ThenBy(r => r.Vend.WlPerItem),
            4 => all.OrderBy(r => Rarity(r.Vend.ItemId) >= 999 ? 1 : 0).ThenBy(r => Rarity(r.Vend.ItemId)).ThenBy(r => r.Vend.WlPerItem),
            5 => all.OrderBy(r => r.Name, byName).ThenBy(r => r.Vend.WlPerItem),
            6 => all.OrderBy(r => r.World, byName).ThenBy(r => r.Name, byName).ThenBy(r => r.Vend.WlPerItem),
            _ => all.OrderBy(r => r.Vend.WlPerItem).ThenBy(r => r.Name, byName),
        }).Take(2000).ToList();

        foreach (var r in rows)
        {
            bool here = string.Equals(r.World, _currentWorld, StringComparison.OrdinalIgnoreCase);
            var tag = new RowTag(r.World, r.Vend.ItemId, r.Vend.X, r.Vend.Y, r.Name, r.Vend.Price);
            bool lined = _lines is var (lw, li, lx, ly, every) && string.Equals(lw, r.World, StringComparison.OrdinalIgnoreCase)
                         && li == r.Vend.ItemId && (every || (lx == r.Vend.X && ly == r.Vend.Y));
            var row = new ListViewItem((lined ? "➜ " : "") + r.Name)
            {
                ForeColor = r.Vend.WlPerItem <= cheapest[r.Vend.ItemId] ? Theme.Gold : Theme.Text,
                Tag       = tag,
            };
            row.SubItems.Add(r.World + (here ? " (here)" : "") + (r.Partial ? " *" : ""));
            row.SubItems.Add($"{r.Vend.X}, {r.Vend.Y}");
            row.SubItems.Add(VendSearch.PriceText(r.Vend.Price));
            row.SubItems.Add(VendSearch.PerItemText(r.Vend.WlPerItem));
            _list.Items.Add(row);
        }
        _list.EndUpdate();

        int items = rows.Select(r => r.Vend.ItemId).Distinct().Count();
        Ui.SetText(_selected, rows.Count == 0 ? "" :
            $"{rows.Count} vend{(rows.Count == 1 ? "" : "s")} · {items} item{(items == 1 ? "" : "s")}" +
            (rows.Any(r => r.Partial) ? " · * world only partly read" : ""));

        _empty.Text = _vends.Count == 0
            ? "No worlds scanned yet. Start [AURA] and enter worlds with vending machines."
            : $"No vend sells anything matching \"{filter.Trim()}\" in the {_vends.Count} world{(_vends.Count == 1 ? "" : "s")} scanned.";
        _empty.Visible = rows.Count == 0;
    }

    // ── Lines ───────────────────────────────────────────────────────

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location).Item;
        if (hit?.Tag is not RowTag tag) return;
        hit.Selected = true;

        _menu.Items.Clear();
        bool here = string.Equals(tag.World, _currentWorld, StringComparison.OrdinalIgnoreCase);
        if (!here)
        {
            var info = _menu.Items.Add($"This vend is in {tag.World}. Enter that world to draw lines.");
            info.Enabled = false;
        }
        else
        {
            _menu.Items.Add("Point to this vend", null,
                (_, _) => SetLines((tag.World, tag.ItemId, tag.X, tag.Y, false)));
            _menu.Items.Add($"Point to every {tag.Name} vend in this world", null,
                (_, _) => SetLines((tag.World, tag.ItemId, tag.X, tag.Y, true)));
        }
        if (_lines != null) _menu.Items.Add("Hide pointers", null, (_, _) => SetLines(null));
        _menu.Show(_list, e.Location);
    }

    private void SetLines((string, int, int, int, bool)? lines)
    {
        _lines = lines;
        Render();
        PublishLines();
    }

    private void PublishLines()
    {
        if (_lines is not var (world, itemId, x, y, all))
        {
            if (_publishing) LinesChanged?.Invoke(Array.Empty<TilePos>(), null);
            _publishing = false;
            return;
        }
        _publishing = true;

        var vends = _vends.FirstOrDefault(w => string.Equals(w.World, world, StringComparison.OrdinalIgnoreCase))?.Vends
                    ?? (IReadOnlyList<VendInfo>)Array.Empty<VendInfo>();
        var tiles = vends.Where(v => v.ItemId == itemId && v.IsForSale && (all || (v.X == x && v.Y == y)))
                         .Select(v => new TilePos(v.X, v.Y)).Distinct().ToList();
        LinesChanged?.Invoke(tiles, all ? $"{NameOf(itemId)} vends" : $"{NameOf(itemId)} vend");
    }
}
