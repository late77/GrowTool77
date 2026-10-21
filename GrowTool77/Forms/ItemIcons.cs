using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GrowTool77.Aura;
using GrowTool77.Modules;

namespace GrowTool77.Forms;

// Item icons cut from Growtopia's own texture sheets in the game folder
// (read-only). Sheets are decoded on first use; icons are cached in memory.
public sealed class ItemIcons
{
    private const int MaxSheets = 6;

    private readonly ItemDatabase? _items;
    private readonly string? _gameFolder;
    private readonly Dictionary<string, RtTexImage?> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _sheetOrder = new();
    private readonly Dictionary<int, Bitmap?> _icons = new();

    public ItemIcons(ItemDatabase? items, string? gameExePath)
    {
        _items = items;
        var dir = string.IsNullOrWhiteSpace(gameExePath) ? null : Path.GetDirectoryName(gameExePath);
        _gameFolder = dir == null ? null : Path.Combine(dir, "game");
    }

    public ItemInfo? Item(int itemId) => _items?.Get(itemId);

    public Bitmap? Get(int itemId)
    {
        if (_icons.TryGetValue(itemId, out var cached)) return cached;

        Bitmap? icon = null;
        var item = _items?.Get(itemId);
        if (item != null && item.Texture.Length > 0 && _gameFolder != null)
        {
            var sheet = Sheet(item.Texture);
            if (sheet != null)
            {
                var (cx, cy) = RtTex.IconCell(item);
                var rgba = RtTex.Cell(sheet, cx, cy);
                if (rgba != null) icon = ToBitmap(rgba, 32, 32);
            }
        }
        _icons[itemId] = icon;
        return icon;
    }

    private RtTexImage? Sheet(string name)
    {
        if (_sheets.TryGetValue(name, out var sheet)) return sheet;
        try
        {
            var path = Path.Combine(_gameFolder!, name);
            sheet = File.Exists(path) ? RtTex.Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sheet = null;
        }
        _sheets[name] = sheet;
        _sheetOrder.Enqueue(name);
        while (_sheetOrder.Count > MaxSheets) _sheets.Remove(_sheetOrder.Dequeue());
        return sheet;
    }

    private static Bitmap ToBitmap(byte[] rgba, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4]     = rgba[i * 4 + 2];
            bgra[i * 4 + 1] = rgba[i * 4 + 1];
            bgra[i * 4 + 2] = rgba[i * 4];
            bgra[i * 4 + 3] = rgba[i * 4 + 3];
        }
        var bmp  = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        return bmp;
    }
}

// Owner-drawn list in the GrowTool77 style. The first column shows the
// item icon of the row (Tag must implement IHasItem).
public interface IHasItem { int ItemId { get; } }

public sealed class ItemListView : ListView
{
    public ItemIcons? Icons { get; set; }

    public ItemListView()
    {
        View          = View.Details;
        FullRowSelect = true;
        MultiSelect   = false;
        BorderStyle   = BorderStyle.None;
        HeaderStyle   = ColumnHeaderStyle.Nonclickable;
        OwnerDraw     = true;
        BackColor     = Theme.Card;
        ForeColor     = Theme.Text;
        Font          = Theme.Body;
        // Rows tall enough for an icon.
        SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(28)) };

        DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Theme.HeaderDeep)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Theme.SmallBold,
                Rectangle.Inflate(e.Bounds, -Theme.S(8), 0), Theme.Gold,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        DrawItem += (_, _) => { };
        DrawSubItem += (_, e) =>
        {
            if (e.Item == null) return;
            var bg = e.Item.Selected ? Theme.Selection : (e.ItemIndex % 2 == 0 ? Theme.Card : Theme.CardAlt);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            var text = e.Bounds;
            if (e.ColumnIndex == 0 && e.Item.Tag is IHasItem row)
            {
                int size = Theme.S(22);
                var icon = Icons?.Get(row.ItemId);
                var box = new Rectangle(e.Bounds.X + Theme.S(6), e.Bounds.Y + (e.Bounds.Height - size) / 2, size, size);
                if (icon != null)
                {
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    e.Graphics.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    e.Graphics.DrawImage(icon, box);
                }
                text = new Rectangle(box.Right + Theme.S(6), e.Bounds.Y, Math.Max(0, e.Bounds.Right - box.Right - Theme.S(6)), e.Bounds.Height);
            }
            else
            {
                text = Rectangle.Inflate(e.Bounds, -Theme.S(8), 0);
            }
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Font, text, e.Item.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }
}
