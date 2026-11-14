using CSharpMath.Atom;
using CSharpMath.SkiaSharp;
using SkiaSharp;

namespace QuickTranslate.Services;

internal sealed record FormulaImage(byte[] Png, double Width, double Height);

internal static class FormulaRenderer
{
    private const int MaxCacheBytes = 4 * 1024 * 1024;
    private const int MaxCacheEntries = 96;
    private const float Scale = 2;
    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, FormulaImage?> Cache = [];
    private static readonly Queue<CacheKey> Order = [];
    private static int _cacheBytes;
    private sealed record CacheKey(MathFormula Formula, float FontSize, uint Color);

    public static Task<FormulaImage?> RenderAsync(MathFormula formula, double fontSize, uint color) =>
        Task.Run(() => Render(formula, (float)Math.Clamp(fontSize, 10, 32), color));

    private static FormulaImage? Render(MathFormula formula, float fontSize, uint color)
    {
        // Fonts are shared by the typesetter; serialize access and retain only a small PNG cache.
        lock (Gate)
        {
            var key = new CacheKey(formula, fontSize, color);
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            FormulaImage? result;
            try
            {
                result = Draw(formula, fontSize, color);
            }
            catch (Exception)
            {
                // Unsupported LaTeX stays available as readable source instead of breaking the answer.
                result = null;
            }
            while (Order.Count > 0 && (Cache.Count >= MaxCacheEntries ||
                   _cacheBytes + (result?.Png.Length ?? 0) > MaxCacheBytes))
            {
                var oldest = Order.Dequeue();
                _cacheBytes -= Cache[oldest]?.Png.Length ?? 0;
                Cache.Remove(oldest);
            }
            if ((result?.Png.Length ?? 0) <= MaxCacheBytes)
            {
                Cache.Add(key, result);
                Order.Enqueue(key);
                _cacheBytes += result?.Png.Length ?? 0;
            }
            return result;
        }
    }

    private static FormulaImage? Draw(MathFormula formula, float fontSize, uint color)
    {
        var painter = new MathPainter
        {
            FontSize = fontSize,
            TextColor = new SKColor(color),
            LineStyle = formula.IsDisplay ? LineStyle.Display : LineStyle.Text,
            DisplayErrorInline = false,
            LaTeX = formula.LaTeX
        };
        if (painter.ErrorMessage is not null)
        {
            return null;
        }
        var bounds = painter.Measure();
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 1800 || bounds.Height > 600)
        {
            return null;
        }
        const float padding = 3;
        var width = (int)Math.Ceiling((bounds.Width + padding * 2) * Scale);
        var height = (int)Math.Ceiling((bounds.Height + padding * 2) * Scale);
        if ((long)width * height > 2_000_000)
        {
            return null;
        }
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return null;
        }
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(Scale);
        painter.Draw(surface.Canvas, padding, -bounds.Y + padding);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(png.ToArray(), width / Scale, height / Scale);
    }
}
