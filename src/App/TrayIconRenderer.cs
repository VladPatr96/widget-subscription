using Avalonia.Controls;
using Avalonia.Media.Imaging;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>Bridges the SkiaSharp <see cref="DonutRenderer"/> to an Avalonia <see cref="WindowIcon"/>.</summary>
public static class TrayIconRenderer
{
    public static WindowIcon RenderIcon(IconView icon, int size = 32)
    {
        var png = DonutRenderer.RenderPng(icon, size);
        using var stream = new MemoryStream(png);
        return new WindowIcon(new Bitmap(stream));
    }
}
