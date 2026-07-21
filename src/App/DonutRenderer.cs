using SkiaSharp;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>
/// Draws the tray donut (spec #3/#4) from an <see cref="IconView"/> using SkiaSharp only —
/// no Avalonia, so it stays testable and cheap. The arc length is the worst headroom and the
/// color already encodes the threshold state (grey when degraded).
/// </summary>
public static class DonutRenderer
{
    private const string TrackColor = "#30363d";

    /// <summary>Renders the donut to PNG bytes at <paramref name="size"/> px square.</summary>
    public static byte[] RenderPng(IconView icon, int size = 32)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var center = size / 2f;
        var stroke = size * 0.18f;
        var radius = center - stroke / 2f - size * 0.06f;
        var box = new SKRect(center - radius, center - radius, center + radius, center + radius);

        using (var track = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            Color = SKColor.Parse(TrackColor),
            IsAntialias = true,
        })
        {
            canvas.DrawCircle(center, center, radius, track);
        }

        var sweep = (float)(360.0 * Math.Clamp(icon.ArcFraction, 0d, 1d));
        if (sweep > 0f)
        {
            using var arc = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                Color = SKColor.Parse(icon.Color),
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
            };
            using var path = new SKPath();
            path.AddArc(box, -90f, sweep);   // start at 12 o'clock, sweep clockwise
            canvas.DrawPath(path, arc);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
