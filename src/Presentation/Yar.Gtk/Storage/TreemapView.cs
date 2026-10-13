using Yar.Core;

namespace Yar.App;

/// <summary>Draws storage items as blocks whose areas show their sizes. Clicking a block acts on it.</summary>
internal sealed class TreemapView
{
    private const double Gap = 1.5;

    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private IReadOnlyList<StorageItem> _items = [];
    private IReadOnlyList<TreemapRect> _rects = [];
    private Action<StorageItem>? _activated;

    public TreemapView(int height)
    {
        _area.SetSizeRequest(-1, height);
        _area.Hexpand = true;
        _area.HasTooltip = true;
        _area.SetDrawFunc((_, context, width, height) => Draw(context, width, height));

        _area.OnQueryTooltip += (_, args) =>
        {
            if (ItemAt(args.X, args.Y) is not { } hit)
                return false;
            args.Tooltip.SetText(hit.Item is { } item
                ? $"{item.Title}\n{(item.AtLeast ? T._("at least ") : "")}{Presentation.FormatSize(item.Bytes)}"
                : T.F("{0} smaller things", hit.Others));
            return true;
        };

        var click = Gtk.GestureClick.New();
        click.OnReleased += (_, args) =>
        {
            if (ItemAt(args.X, args.Y) is { Item: { } item })
                _activated?.Invoke(item);
        };
        _area.AddController(click);
    }

    public Gtk.Widget Widget => _area;

    public void Show(IReadOnlyList<StorageItem> items, Action<StorageItem> activated)
    {
        _items = items;
        _activated = activated;
        _area.QueueDraw();
    }

    private (StorageItem? Item, int Others)? ItemAt(double x, double y)
    {
        foreach (var rect in _rects)
        {
            if (rect.Contains(x, y))
                return rect.Index >= 0 ? (_items[rect.Index], 0) : (null, _items.Count - _rects.Count + 1);
        }
        return null;
    }

    private void Draw(Cairo.Context context, int width, int height)
    {
        _rects = Treemap.Layout(_items.Select(item => (double)item.Bytes).ToList(), width, height);
        foreach (var rect in _rects)
        {
            var item = rect.Index >= 0 ? _items[rect.Index] : null;
            var (red, green, blue) = Colour(item);
            context.SetSourceRgb(red, green, blue);
            context.Rectangle(rect.X + Gap, rect.Y + Gap, Math.Max(0, rect.Width - 2 * Gap), Math.Max(0, rect.Height - 2 * Gap));
            context.Fill();

            // A name only where there is room to read it.
            if (rect.Width < 70 || rect.Height < 34)
                continue;
            context.Save();
            context.Rectangle(rect.X + Gap, rect.Y + Gap, rect.Width - 2 * Gap - 6, rect.Height - 2 * Gap);
            context.Clip();
            context.SetSourceRgb(1, 1, 1);
            context.SetFontSize(12);
            context.MoveTo(rect.X + 8, rect.Y + 19);
            context.ShowText(item?.Title ?? T._("Smaller things"));
            if (item is not null && rect.Height >= 52)
            {
                context.SetFontSize(10.5);
                context.MoveTo(rect.X + 8, rect.Y + 35);
                context.ShowText(Presentation.FormatSize(item.Bytes));
            }
            context.Restore();
        }
    }

    // One colour per kind of thing, dark enough for white text in both light and dark mode.
    private static (double, double, double) Colour(StorageItem? item) => item switch
    {
        null => (0.37, 0.36, 0.39),
        { Kind: StorageKind.SharedParts } => (0.57, 0.25, 0.67),
        { Kind: StorageKind.Leftover } => (0.75, 0.11, 0.16),
        { Kind: StorageKind.Cache } => (0.78, 0.47, 0.0),
        { Kind: StorageKind.Other } => (0.37, 0.36, 0.39),
        { ProviderId: "flatpak" } => (0.11, 0.44, 0.85),
        { ProviderId: "packagekit" } => (0.15, 0.64, 0.41),
        _ => (0.13, 0.56, 0.64),
    };

    public static string Legend(StorageKind kind, string? providerId = null) => kind switch
    {
        StorageKind.SharedParts => T._("Shared parts"),
        StorageKind.Leftover => T._("Left behind"),
        StorageKind.Cache => T._("Temporary files"),
        StorageKind.Other => T._("Everything else"),
        _ => providerId switch
        {
            "flatpak" => T._("Flatpak app"),
            "packagekit" => T._("System package"),
            _ => T._("Other app"),
        },
    };
}
