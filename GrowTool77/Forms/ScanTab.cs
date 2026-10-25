// lang: C#, file: ScanTab.cs, target: .NET 8 Windows WinForms, proj: GrowTool77
// World tab — Dropped / Placed / Doors views.
// Doors view reads destinations from world binary (always available) and
// can fetch a door's own ID by sending a wrench packet via [AURA] proxy
// (right-click a row → Get door ID). The own ID is returned by the server
// in an OnDialogRequest and stored in WorldTracker.
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

public class ScanTab : Panel
{
    private static readonly string[] SortNames =
        { "Most first", "Least first", "Rarest first", "Most common first", "Name A–Z" };

    private readonly Label        _itemsVal;
    private readonly Label        _gemsVal;
    private readonly Label        _statusVal;
    private readonly Label        _note;
    private readonly Label        _empty;
    private readonly GtButton     _droppedBtn;
    private readonly GtButton     _placedBtn;
    private readonly GtButton     _doorsBtn;
    private readonly InputBox     _filter;
    private readonly ComboBox     _sort;
    private readonly ItemListView _list;
    private readonly ContextMenuStrip _menu = new();

    private ItemIcons? _icons;
    private AuraState  _state = AuraState.Off;
    private WorldView? _view;
    private bool       _showPlaced;
    private bool       _showDoors;
    private (bool Placed, bool Background, int ItemId)? _lines;
    private bool _publishing;

    public event Action<IReadOnlyList<TilePos>, string?>? LinesChanged;
    // Fires when user right-clicks a door row and picks "Get door ID".
    public event Action<int, int>? DoorWrenchRequested; // (tileX, tileY)

    private sealed record RowTag(bool Placed, bool Background, int ItemId, string Name) : IHasItem;
    private sealed record DoorRowTag(int X, int Y);

    public ScanTab()
    {
        BackColor      = Theme.Bg;
        DoubleBuffered = true;

        Controls.Add(Ui.Lbl("World scan", Theme.R(28, 12, 300, 22), Theme.Section, Theme.Gold));

        var stats = new Card { Bounds = Theme.R(24, 36, 832, 80) };
        _itemsVal  = Ui.Lbl("—", Theme.R(22, 30, 180, 40), Theme.Big, Theme.Gold);
        _gemsVal   = Ui.Lbl("—", Theme.R(222, 30, 180, 40), Theme.Big, Theme.Gold);
        _statusVal = Ui.Lbl("[AURA] off", Theme.R(422, 34, 200, 30), Theme.BodyBold, Theme.Warn);
        _note      = Ui.Lbl("", Theme.R(622, 12, 190, 56), Theme.Small, Theme.TextDim, ContentAlignment.MiddleRight);
        _note.AutoEllipsis = false;
        stats.Controls.AddRange(new Control[]
        {
            Ui.Lbl("Dropped items",     Theme.R(22,  10, 180, 20), Theme.Small, Theme.TextDim),
            _itemsVal,
            Ui.Divider(Theme.R(204, 14, 1, 52)),
            Ui.Lbl("Gems on the floor", Theme.R(222, 10, 180, 20), Theme.Small, Theme.TextDim),
            _gemsVal,
            Ui.Divider(Theme.R(404, 14, 1, 52)),
            Ui.Lbl("Status",            Theme.R(422, 10, 200, 20), Theme.Small, Theme.TextDim),
            _statusVal, _note,
        });
        Controls.Add(stats);

        _droppedBtn = new GtButton("Dropped")                      { Bounds = Theme.R(24,  126, 96, 30) };
        _placedBtn  = new GtButton("Placed",  ButtonKind.Secondary) { Bounds = Theme.R(126, 126, 96, 30) };
        _doorsBtn   = new GtButton("Doors",   ButtonKind.Secondary) { Bounds = Theme.R(228, 126, 96, 30) };
        _droppedBtn.Click += (_, _) => SwitchMode(0);
        _placedBtn.Click  += (_, _) => SwitchMode(1);
        _doorsBtn.Click   += (_, _) => SwitchMode(2);

        _filter = new InputBox { Bounds = Theme.R(334, 124, 240, 34) };
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

        Controls.AddRange(new Control[] { _droppedBtn, _placedBtn, _doorsBtn, _filter, _sort });

        var listCard = new Card { Bounds = Theme.R(24, 166, 832, 246) };
        _list = new ItemListView { Bounds = Theme.R(12, 10, 808, 226) };
        // Default headers (Dropped mode)
        _list.Columns.Add("Item",        Theme.S(300));
        _list.Columns.Add("ID",          Theme.S(80));
        _list.Columns.Add("Amount",      Theme.S(120));
        _list.Columns.Add("Rarity",      Theme.S(90));
        _list.Columns.Add("Stacks",      Theme.S(140));
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

    public void SetIcons(ItemIcons icons) { _icons = icons; _list.Icons = icons; Render(); }

    // Called externally when the server returns a door's own ID after a wrench.
    public void NotifyDoorIdReceived() => OnUi(Render);

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
        if (worldChanged) _lines = null;
        if (_state == AuraState.Connected)
            Ui.SetText(_statusVal, view != null ? "Scanning live" : "[AURA] connected");
        Render();
        PublishLines();
    }

    // ── Mode switching ────────────────────────────────────────────────

    // 0=Dropped, 1=Placed, 2=Doors
    private void SwitchMode(int mode)
    {
        _showPlaced = mode == 1;
        _showDoors  = mode == 2;
        _droppedBtn.Kind = mode == 0 ? ButtonKind.Primary : ButtonKind.Secondary;
        _placedBtn.Kind  = mode == 1 ? ButtonKind.Primary : ButtonKind.Secondary;
        _doorsBtn.Kind   = mode == 2 ? ButtonKind.Primary : ButtonKind.Secondary;
        _droppedBtn.Invalidate(); _placedBtn.Invalidate(); _doorsBtn.Invalidate();

        // Column headers per mode
        if (mode == 2)
        {
            _list.Columns[0].Text = "Door";         // door type name
            _list.Columns[1].Text = "Position";     // (X, Y)
            _list.Columns[2].Text = "Destination";  // target world (WORLD:ID split left)
            _list.Columns[3].Text = "Target ID";    // right side of WORLD:ID (target door's ID)
            _list.Columns[4].Text = "Own ID";       // this door's own ID (from wrench)
        }
        else
        {
            _list.Columns[0].Text = mode == 1 ? "Block"  : "Item";
            _list.Columns[1].Text = "ID";
            _list.Columns[2].Text = mode == 1 ? "Count"  : "Amount";
            _list.Columns[3].Text = mode == 1 ? "Layer"  : "Rarity";
            _list.Columns[4].Text = mode == 1 ? ""       : "Stacks";
        }
        Render();
    }

    // ── Rendering ─────────────────────────────────────────────────────

    private int Rarity(int id) => _icons?.Item(id)?.Rarity ?? 999;

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
        Ui.SetText(_note, $"{_view.Name}\nupdated {_view.Updated:HH:mm:ss}" +
                          (_view.Exact ? "" : "\nlocated by fallback scan"));
        _note.ForeColor = _view.Exact ? Theme.TextDim : Theme.Warn;

        var filter = _filter.Box.Text;
        int shown  = 0;

        if (_showDoors)
        {
            var doors = _view.Doors
                .Where(d => string.IsNullOrEmpty(filter) ||
                            ListFilter.Matches(d.Destination, filter) ||
                            ListFilter.Matches(d.OwnId, filter) ||
                            ListFilter.Matches(_icons?.Item(d.FgItemId)?.Name ?? "", filter))
                .OrderBy(d => string.IsNullOrEmpty(d.Destination))
                .ThenBy(d => d.Destination, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var door in doors)
            {
                string doorName  = _icons?.Item(door.FgItemId)?.Name ?? $"Door #{door.FgItemId}";
                string rawDest   = door.Destination ?? "";

                // Split WORLDNAME:DOORID format
                string targetWorld = rawDest;
                string targetId    = "";
                int colon = rawDest.IndexOf(':');
                if (colon >= 0)
                {
                    targetWorld = rawDest[..colon].Trim();
                    targetId    = rawDest[(colon + 1)..].Trim();
                }
                if (string.IsNullOrEmpty(targetWorld) && !string.IsNullOrEmpty(targetId))
                    targetWorld = "(this world)";

                string destDisplay = string.IsNullOrEmpty(rawDest) ? "(unset)" : targetWorld.ToUpperInvariant();

                bool hasOwnId = !string.IsNullOrEmpty(door.OwnId);
                var  row      = new ListViewItem(doorName)
                {
                    ForeColor = string.IsNullOrEmpty(rawDest) ? Theme.TextDim : Theme.Gold,
                    Tag       = new DoorRowTag(door.X, door.Y),
                };
                row.SubItems.Add($"({door.X}, {door.Y})");
                row.SubItems.Add(destDisplay);
                row.SubItems.Add(string.IsNullOrEmpty(targetId) ? "-" : targetId.ToUpperInvariant());
                row.SubItems.Add(hasOwnId ? door.OwnId.ToUpperInvariant() : "—  right-click to fetch");
                _list.Items.Add(row);
                shown++;
            }
            _empty.Text = _view.Doors.Count == 0
                ? $"No doors found in {_view.Name}."
                : $"No doors match \"{filter.Trim()}\".";
            _empty.Visible = shown == 0;
            _list.EndUpdate();
            return;
        }

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
            _empty.Text = _view.Placed.Count == 0
                ? $"No blocks could be read in {_view.Name}."
                : $"No blocks match \"{filter.Trim()}\".";
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
            _empty.Text = _view.Items.Count == 0
                ? $"No dropped items in {_view.Name}."
                : $"No dropped items match \"{filter.Trim()}\".";
        }
        _empty.Visible = shown == 0;
        _list.EndUpdate();
    }

    private string LineMark(RowTag tag) =>
        _lines is var (p, b, id) && p == tag.Placed && b == tag.Background && id == tag.ItemId ? "➜ " : "";

    // ── Context menu ──────────────────────────────────────────────────

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location).Item;
        if (hit == null) return;
        hit.Selected = true;
        _menu.Items.Clear();

        if (_showDoors && hit.Tag is DoorRowTag dtag)
        {
            _menu.Items.Add("Get door ID (sends wrench via [AURA])", null,
                (_, _) =>
                {
                    DoorWrenchRequested?.Invoke(dtag.X, dtag.Y);
                    // Update the row to show "Fetching..."
                    if (hit.SubItems.Count > 4) hit.SubItems[4].Text = "fetching…";
                });
            _menu.Show(_list, e.Location);
            return;
        }

        if (hit.Tag is not RowTag tag) return;

        bool active = _lines is var (pl, bg, i) && pl == tag.Placed && bg == tag.Background && i == tag.ItemId;
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

    // ── Lines ─────────────────────────────────────────────────────────

    // Called externally (e.g. Ghost Finder) to draw overlay lines
    public void FireExternalLines(IReadOnlyList<TilePos> tiles, string label)
    {
        LinesChanged?.Invoke(tiles, label);
    }

    public void ClearLines()
    {
        _publishing = false;
        if (_lines == null) return;
        _lines = null;
        Render();
    }

    private void SetLines((bool, bool, int)? lines) { _lines = lines; Render(); PublishLines(); }

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

    private void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }
}
