using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

// World tab: dropped items and placed blocks of the current world, updated
// live. Type to narrow the list, pick the order, right-click a row to draw
// lines to it in Growtopia's window.
public class ScanTab : Panel
{
    private static readonly string[] SortNames = { "Most first", "Least first", "Rarest first", "Most common first", "Name A–Z" };

    private readonly Label    _itemsVal;
    private readonly Label    _gemsVal;
    private readonly Label    _statusVal;
    private readonly Label    _note;
    private readonly Label    _empty;
    private readonly GtButton _droppedBtn;
    private readonly GtButton _placedBtn;
    private readonly InputBox _filter;
    private readonly ComboBox _sort;
    private readonly ItemListView _list;
    private readonly ContextMenuStrip _menu = new();

    private ItemIcons? _icons;
    private AuraState  _state = AuraState.Off;
    private WorldView? _view;
    private bool       _showPlaced;
    private (bool Placed, bool Background, int ItemId)? _lines;
    private bool _publishing;                 // our lines are the ones on screen

    public event Action<IReadOnlyList<TilePos>, string?>? LinesChanged;

    private sealed record RowTag(bool Placed, bool Background, int ItemId, string Name) : IHasItem;

    public ScanTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        // ── Summary ─────────────────────────────────────────────────
        Controls.Add(Ui.Lbl("World scan", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var stats = new Card { Bounds = Theme.R(24, 36, 832, 80) };
        _itemsVal  = Ui.Lbl("—", Theme.R(22, 30, 180, 40), Theme.Big, Theme.Gold);
        _gemsVal   = Ui.Lbl("—", Theme.R(222, 30, 180, 40), Theme.Big, Theme.Gold);
        _statusVal = Ui.Lbl("[AURA] off", Theme.R(422, 34, 200, 30), Theme.BodyBold, Theme.Warn);
        _note      = Ui.Lbl("", Theme.R(622, 12, 190, 56), Theme.Small, Theme.TextDim, ContentAlignment.MiddleRight);
        _note.AutoEllipsis = false;

        stats.Controls.AddRange(new Control[]
        {
            Ui.Lbl("Dropped items", Theme.R(22, 10, 180, 20), Theme.Small, Theme.TextDim),
            _itemsVal,
            Ui.Divider(Theme.R(204, 14, 1, 52)),
            Ui.Lbl("Gems on the floor", Theme.R(222, 10, 180, 20), Theme.Small, Theme.TextDim),
            _gemsVal,
            Ui.Divider(Theme.R(404, 14, 1, 52)),
            Ui.Lbl("Status", Theme.R(422, 10, 200, 20), Theme.Small, Theme.TextDim),
            _statusVal,
            _note,
        });
        Controls.Add(stats);

        // ── View, filter, order ─────────────────────────────────────
        _droppedBtn = new GtButton("Dropped")                      { Bounds = Theme.R(24, 126, 104, 30) };
        _placedBtn  = new GtButton("Placed", ButtonKind.Secondary) { Bounds = Theme.R(134, 126, 104, 30) };
        _droppedBtn.Click += (_, _) => SwitchView(placed: false);
        _placedBtn.Click  += (_, _) => SwitchView(placed: true);

        _filter = new InputBox { Bounds = Theme.R(250, 124, 330, 34) };
        _filter.Box.PlaceholderText = "Type to filter…";
        _filter.Box.TextChanged += (_, _) => Render();

        _sort = new ComboBox
        {
            Bounds        = Theme.R(592, 126, 264, 30),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = Theme.Input,
            ForeColor     = Theme.Text,
            Font          = Theme.Body,
        };
        _sort.Items.AddRange(SortNames);
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => Render();

        Controls.AddRange(new Control[] { _droppedBtn, _placedBtn, _filter, _sort });

        // ── List ────────────────────────────────────────────────────
        var listCard = new Card { Bounds = Theme.R(24, 166, 832, 246) };
        _list = new ItemListView { Bounds = Theme.R(12, 10, 808, 226) };
        _list.Columns.Add("Item",    Theme.S(330));
        _list.Columns.Add("ID",      Theme.S(90));
        _list.Columns.Add("Amount",  Theme.S(110));
        _list.Columns.Add("Rarity",  Theme.S(90));
        _list.Columns.Add("Stacks",  Theme.S(120));
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

    public void SetIcons(ItemIcons icons)
    {
        _icons = icons;
        _list.Icons = icons;
        Render();
    }

    public void SetAuraState(AuraStatus status)
    {
        _state = status.State;
        Ui.SetText(_statusVal, status.State switch
        {
            AuraState.Connected  => _view != null ? "Scanning live" : "[AURA] connected",
            AuraState.Connecting => "[AURA] connecting",
            AuraState.Waiting    => "Waiting for Growtopia",
            _                    => "[AURA] off",
        });
        _statusVal.ForeColor = status.State == AuraState.Connected ? Theme.Good : Theme.Warn;
        Render();
    }

    public void UpdateScan(WorldView? view)
    {
        bool worldChanged = view?.Name != _view?.Name || view?.Entered != _view?.Entered;
        _view = view;
        if (worldChanged) _lines = null;                  // lines belong to the world they were made in
        if (_state == AuraState.Connected)
            Ui.SetText(_statusVal, view != null ? "Scanning live" : "[AURA] connected");
        Render();
        PublishLines();
    }

    private void SwitchView(bool placed)
    {
        _showPlaced = placed;
        _droppedBtn.Kind = placed ? ButtonKind.Secondary : ButtonKind.Primary;
        _placedBtn.Kind  = placed ? ButtonKind.Primary : ButtonKind.Secondary;
        _droppedBtn.Invalidate();
        _placedBtn.Invalidate();
        _list.Columns[0].Text = placed ? "Block" : "Item";
        _list.Columns[2].Text = placed ? "Count" : "Amount";
        _list.Columns[4].Text = placed ? "Layer" : "Stacks";
        Render();
    }

    private int Rarity(int itemId) => _icons?.Item(itemId)?.Rarity ?? 999;

    private IEnumerable<T> Sorted<T>(IEnumerable<T> rows, Func<T, int> amount, Func<T, int> itemId, Func<T, string> name) =>
        _sort.SelectedIndex switch
        {
            1 => rows.OrderBy(amount).ThenBy(name, StringComparer.OrdinalIgnoreCase),
            2 => rows.OrderBy(r => Rarity(itemId(r)) >= 999 ? 1 : 0).ThenByDescending(r => Rarity(itemId(r))).ThenBy(name, StringComparer.OrdinalIgnoreCase),
            3 => rows.OrderBy(r => Rarity(itemId(r)) >= 999 ? 1 : 0).ThenBy(r => Rarity(itemId(r))).ThenBy(name, StringComparer.OrdinalIgnoreCase),
            4 => rows.OrderBy(name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(amount).ThenBy(name, StringComparer.OrdinalIgnoreCase),
        };

    private void Render()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        if (_view == null)
        {
            _itemsVal.Text = "—";
            _gemsVal.Text  = "—";
            Ui.SetText(_note, "");
            _empty.Text = _state switch
            {
                AuraState.Off     => "[AURA] is off. Start it on the Main tab, then start Growtopia.",
                AuraState.Waiting => "Waiting for Growtopia to connect through [AURA].",
                _                 => "Enter a world in Growtopia to scan it.",
            };
            _empty.Visible = true;
            _list.EndUpdate();
            return;
        }

        _itemsVal.Text = _view.TotalItems.ToString("N0");
        _gemsVal.Text  = _view.Gems.ToString("N0");
        Ui.SetText(_note, $"{_view.Name}\nupdated {_view.Updated:HH:mm:ss}" + (_view.Exact ? "" : "\nlocated by fallback scan"));
        _note.ForeColor = _view.Exact ? Theme.TextDim : Theme.Warn;

        var filter = _filter.Box.Text;
        int shown = 0;
        if (_showPlaced)
        {
            var rows = _view.Placed.Where(p => ListFilter.Matches(p.Name, filter));
            foreach (var p in Sorted(rows, p => p.Count, p => p.ItemId, p => p.Name).Take(1500))
            {
                var tag = new RowTag(true, p.Background, p.ItemId, p.Name);
                var row = new ListViewItem(LineMark(tag) + p.Name) { ForeColor = Theme.Text, Tag = tag };
                row.SubItems.Add(p.ItemId.ToString());
                row.SubItems.Add(p.Count.ToString("N0"));
                row.SubItems.Add(ListFilter.RarityText(Rarity(p.ItemId)));
                row.SubItems.Add(p.Background ? "Background" : "Foreground");
                _list.Items.Add(row);
                shown++;
            }
            _empty.Text = _view.Placed.Count == 0 ? $"No blocks could be read in {_view.Name}." : $"No blocks match \"{filter.Trim()}\".";
        }
        else
        {
            var rows = _view.Items.Where(i => ListFilter.Matches(i.Name, filter));
            foreach (var item in Sorted(rows, i => i.Amount, i => i.ItemId, i => i.Name))
            {
                var tag = new RowTag(false, false, item.ItemId, item.Name);
                var row = new ListViewItem(LineMark(tag) + item.Name)
                {
                    ForeColor = item.ItemId == WorldTracker.GemsItemId ? Theme.Gold : Theme.Text,
                    Tag       = tag,
                };
                row.SubItems.Add(item.ItemId.ToString());
                row.SubItems.Add(item.Amount.ToString("N0"));
                row.SubItems.Add(ListFilter.RarityText(Rarity(item.ItemId)));
                row.SubItems.Add(item.Stacks.ToString());
                _list.Items.Add(row);
                shown++;
            }
            _empty.Text = _view.Items.Count == 0 ? $"No dropped items in {_view.Name}." : $"No dropped items match \"{filter.Trim()}\".";
        }
        _empty.Visible = shown == 0;
        _list.EndUpdate();
    }

    private string LineMark(RowTag tag) =>
        _lines is var (placed, bg, id) && placed == tag.Placed && bg == tag.Background && id == tag.ItemId ? "➜ " : "";

    // ── Lines ───────────────────────────────────────────────────────

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location).Item;
        if (hit?.Tag is not RowTag tag) return;
        hit.Selected = true;

        bool active = _lines is var (p, b, i) && p == tag.Placed && b == tag.Background && i == tag.ItemId;
        _menu.Items.Clear();
        if (active)
        {
            _menu.Items.Add("Hide lines", null, (_, _) => SetLines(null));
        }
        else
        {
            _menu.Items.Add($"Show lines to {tag.Name} in Growtopia", null,
                (_, _) => SetLines((tag.Placed, tag.Background, tag.ItemId)));
            if (_lines != null) _menu.Items.Add("Hide lines", null, (_, _) => SetLines(null));
        }
        _menu.Show(_list, e.Location);
    }

    // Lines from the vend tab replace ours, and the other way round.
    public void ClearLines()
    {
        _publishing = false;
        if (_lines == null) return;
        _lines = null;
        Render();
    }

    private void SetLines((bool, bool, int)? lines)
    {
        _lines = lines;
        Render();
        PublishLines();
    }

    // One line per tile: several stacks on the same tile share one line.
    private void PublishLines()
    {
        if (_lines is not var (placed, bg, id) || _view == null)
        {
            if (_publishing) LinesChanged?.Invoke(Array.Empty<TilePos>(), null);
            _publishing = false;
            return;
        }
        _publishing = true;

        IReadOnlyList<TilePos> tiles;
        string name;
        if (placed)
        {
            var entry = _view.Placed.FirstOrDefault(p => p.ItemId == id && p.Background == bg);
            tiles = entry?.Tiles ?? (IReadOnlyList<TilePos>)Array.Empty<TilePos>();
            name  = entry?.Name ?? $"#{id}";
        }
        else
        {
            var entry = _view.Items.FirstOrDefault(i => i.ItemId == id);
            tiles = entry?.Tiles ?? (IReadOnlyList<TilePos>)Array.Empty<TilePos>();
            name  = entry?.Name ?? $"#{id}";
        }
        LinesChanged?.Invoke(tiles.Distinct().ToList(), name);
    }
}
