using Yar.Core;

namespace Yar.App;

/// <summary>Pictures of an app, shown one at a time. They are downloaded when the page opens.</summary>
internal static class ScreenshotCarousel
{
    private const int Height = 320;

    /// <summary>Returns null when there is nothing to show: no pictures, or online data switched off.</summary>
    public static Gtk.Widget? Create(Ui ui, StoreApp app)
    {
        if (!ui.OnlineAllowed || app.Screenshots.Count == 0)
            return null;

        var carousel = Adw.Carousel.New();
        carousel.Spacing = 12;
        carousel.AllowScrollWheel = false;
        carousel.SetSizeRequest(-1, Height);

        var dots = Adw.CarouselIndicatorDots.New();
        dots.Carousel = carousel;

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.Append(carousel);
        box.Append(StoreHome.PagerRow(carousel, dots));

        foreach (var url in app.Screenshots)
        {
            var spinner = Gtk.Spinner.New();
            spinner.Spinning = true;
            spinner.Halign = Gtk.Align.Center;
            spinner.Valign = Gtk.Align.Center;
            spinner.SetSizeRequest(32, 32);
            var frame = Adw.Bin.New();
            frame.Hexpand = true;
            frame.Child = spinner;
            carousel.Append(frame);
            _ = Load(ui, url, carousel, frame);
        }
        return box;
    }

    private static async Task Load(Ui ui, string url, Adw.Carousel carousel, Adw.Bin frame)
    {
        Gdk.Texture? texture = null;
        try
        {
            if (await ui.Host.Online.GetImageAsync(url, CancellationToken.None) is { } bytes)
                texture = Gdk.Texture.NewFromBytes(GLib.Bytes.New(bytes));
        }
        catch (Exception e) when (e is GLib.GException or HttpRequestException or TaskCanceledException or IOException)
        {
            // A picture that cannot be fetched or is not really a picture is left out.
        }

        if (texture is null)
        {
            carousel.Remove(frame);
            return;
        }

        var picture = Gtk.Picture.NewForPaintable(texture);
        picture.ContentFit = Gtk.ContentFit.Contain;
        picture.CanShrink = true;
        picture.AddCssClass("yar-shot");
        picture.SetSizeRequest(-1, Height);
        frame.Child = picture;
    }
}
