using System.Globalization;
using System.Text;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.Tooling;

/// <summary>
/// Times frames the way the brokered renderer child produces them (Program.cs
/// SendFrameReadyCore): a fresh full raster of the visible viewport plus the
/// scroll overdraw band, canvas translated by -scrollY, one retained renderer.
/// Two sequences: a scroll sweep, and repeated video-frame repaints at rest.
/// Enabled from debug-site with FEN_DEBUG_SITE_FRAME_BENCH=1; the viewport size
/// comes from FEN_DEBUG_SITE_FRAME_BENCH_SIZE (default 1920x930).
/// </summary>
internal static class FrameCostBench
{
    public static string Run(
        Node root,
        Dictionary<Node, CssComputed> styles,
        string baseUrl,
        Func<IDisposable> enterResourceContext)
    {
        var (width, visibleHeight) = ParseSize(Environment.GetEnvironmentVariable("FEN_DEBUG_SITE_FRAME_BENCH_SIZE"));
        // Program.ComputeBrokeredFrameRasterHeight: visible band plus half of it again, 128..512px.
        int rasterHeight = visibleHeight + (int)Math.Clamp(visibleHeight * 0.5f, 128f, 512f);

        using var resourceContext = enterResourceContext?.Invoke();
        var renderer = new SkiaDomRenderer();
        using var bitmap = new SKBitmap(new SKImageInfo(width, rasterHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        var report = new StringBuilder();
        report.AppendLine($"frame-bench viewport={width}x{visibleHeight} raster={width}x{rasterHeight}");

        Frame(renderer, canvas, root, styles, baseUrl, width, visibleHeight, rasterHeight, 0f,
            RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom);
        Frame(renderer, canvas, root, styles, baseUrl, width, visibleHeight, rasterHeight, 0f,
            RenderFrameInvalidationReason.ProcessIsolation);

        var scroll = new List<RenderFrameTelemetry>();
        for (float y = 200f; y <= 2400f; y += 200f)
        {
            scroll.Add(Frame(renderer, canvas, root, styles, baseUrl, width, visibleHeight, rasterHeight, y,
                RenderFrameInvalidationReason.ProcessIsolation));
        }
        Summarize(report, "scroll", scroll);

        var video = FindFirst(root, e => string.Equals(e.TagName, "VIDEO", StringComparison.OrdinalIgnoreCase));
        if (video != null)
        {
            Frame(renderer, canvas, root, styles, baseUrl, width, visibleHeight, rasterHeight, 0f,
                RenderFrameInvalidationReason.ProcessIsolation);
            var repaint = new List<RenderFrameTelemetry>();
            for (int i = 0; i < 8; i++)
            {
                // What OnFrameAvailable does for each decoded picture.
                video.MarkDirty(InvalidationKind.Paint);
                repaint.Add(Frame(renderer, canvas, root, styles, baseUrl, width, visibleHeight, rasterHeight, 0f,
                    RenderFrameInvalidationReason.ProcessIsolation));
            }
            Summarize(report, "video-repaint", repaint);
        }
        else
        {
            report.AppendLine("video-repaint: no <video> element");
        }

        return report.ToString();
    }

    private static RenderFrameTelemetry Frame(
        SkiaDomRenderer renderer,
        SKCanvas canvas,
        Node root,
        Dictionary<Node, CssComputed> styles,
        string baseUrl,
        int width,
        int visibleHeight,
        int rasterHeight,
        float scrollY,
        RenderFrameInvalidationReason reason)
    {
        canvas.Clear(SKColors.White);
        renderer.ScrollManager.SetScrollBounds(null, width, Math.Max(renderer.LastLayout?.ContentHeight ?? 0f, scrollY + rasterHeight), width, visibleHeight);
        renderer.ScrollManager.SetScrollPosition(null, 0, scrollY);
        canvas.Save();
        canvas.Translate(0, -scrollY);
        var result = renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, scrollY, width, scrollY + rasterHeight),
            BaseUrl = baseUrl,
            SeparateLayoutViewport = new SKSize(width, visibleHeight),
            HasBaseFrame = false,
            InvalidationReason = reason,
            RequestedBy = "frame-bench",
            EmitVerificationReport = false
        });
        canvas.Restore();
        canvas.Flush();
        var t = result.Telemetry;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[frame-bench] scrollY={scrollY,6:F0} rebuilt={t?.PaintTreeRebuilt} layout={t?.LayoutDurationMs,7:F1} paint={t?.PaintDurationMs,7:F1} raster={t?.RasterDurationMs,8:F1} total={t?.TotalDurationMs,8:F1} paintNodes={t?.PaintNodeCount} boxes={t?.BoxCount} layers={t?.CompositedLayerCount}/{t?.PromotedLayerCount} saveCount={canvas.SaveCount}"));
        return t;
    }

    private static void Summarize(StringBuilder report, string label, List<RenderFrameTelemetry> frames)
    {
        var valid = frames.Where(f => f != null).ToList();
        if (valid.Count == 0)
        {
            report.AppendLine($"{label}: no telemetry");
            return;
        }

        static string Stats(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            double median = sorted[sorted.Length / 2];
            return string.Create(CultureInfo.InvariantCulture, $"median {median,7:F1}  max {sorted[^1],7:F1}");
        }

        report.AppendLine($"{label}: {valid.Count} frames, paint tree rebuilt {valid.Count(f => f.PaintTreeRebuilt)}, layout updated {valid.Count(f => f.LayoutUpdated)}");
        report.AppendLine($"  layout ms  {Stats(valid.Select(f => f.LayoutDurationMs))}");
        report.AppendLine($"  paint ms   {Stats(valid.Select(f => f.PaintDurationMs))}");
        report.AppendLine($"  raster ms  {Stats(valid.Select(f => f.RasterDurationMs))}");
        report.AppendLine($"  total ms   {Stats(valid.Select(f => f.TotalDurationMs))}");
    }

    private static (int Width, int Height) ParseSize(string value)
    {
        var parts = (value ?? string.Empty).Split('x', 'X');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
            w > 0 && h > 0)
        {
            return (w, h);
        }

        return (1920, 930);
    }

    private static Element FindFirst(Node node, Func<Element, bool> predicate)
    {
        if (node is Element element && predicate(element)) return element;
        foreach (var child in node.ChildNodes)
        {
            var found = FindFirst(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
