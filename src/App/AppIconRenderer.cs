using SkiaSharp;

namespace WidgetSubscription.App;

/// <summary>
/// Renders the static application icon — direction V5 "Spark-in-ring" (#15): the brand ring
/// (#D97757) encircling Claude's sunburst — straight from the vetted SVG geometry using SkiaSharp,
/// so no external rasterizer or extra dependency is needed. This is the app's fixed identity, kept
/// separate from the dynamic donut tray icon (which encodes state). Below 24px the sunburst turns
/// to mush, so it collapses to a solid centre dot (the #15 caveat). <see cref="BuildIco"/> packs
/// several sizes into a Windows .ico (PNG-compressed frames).
/// </summary>
public static class AppIconRenderer
{
    private static readonly SKColor Brand = SKColor.Parse("#D97757");

    public static byte[] RenderPng(int size)
    {
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var s = size / 256f; // the source viewBox is 256×256

            using (var ring = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 30 * s,
                Color = Brand,
                IsAntialias = true,
            })
            {
                canvas.DrawCircle(128 * s, 128 * s, 86 * s, ring);
            }

            if (size >= 24)
            {
                using var spark = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 12 * s,
                    Color = Brand,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawLine(128 * s, 92 * s, 128 * s, 164 * s, spark);
                canvas.DrawLine(97.2f * s, 110 * s, 158.8f * s, 146 * s, spark);
                canvas.DrawLine(97.2f * s, 146 * s, 158.8f * s, 110 * s, spark);
                canvas.DrawLine(92 * s, 128 * s, 164 * s, 128 * s, spark);
            }
            else
            {
                using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = Brand, IsAntialias = true };
                canvas.DrawCircle(128 * s, 128 * s, 26 * s, dot);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] BuildIco(params int[] sizes)
    {
        var frames = sizes.Select(RenderPng).ToArray();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((short)0);              // reserved
        writer.Write((short)1);              // type: icon
        writer.Write((short)sizes.Length);   // image count

        var offset = 6 + 16 * sizes.Length;  // header + directory entries
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var png = frames[i];
            writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 ⇒ 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);            // palette
            writer.Write((byte)0);            // reserved
            writer.Write((short)1);           // colour planes
            writer.Write((short)32);          // bits per pixel
            writer.Write(png.Length);         // bytes in resource
            writer.Write(offset);             // offset of image data
            offset += png.Length;
        }

        foreach (var png in frames)
            writer.Write(png);

        writer.Flush();
        return ms.ToArray();
    }
}
