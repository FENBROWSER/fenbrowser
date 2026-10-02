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

    /// <summary>
    /// Times full document layouts of the live page through its own renderer, the cost
    /// a script pays for each forced layout (getBoundingClientRect, getComputedStyle
    /// width). Going through the page's renderer serializes with the page's own layouts:
    /// a second renderer laid out the same style objects concurrently. Enabled from
    /// debug-site with FEN_DEBUG_SITE_LAYOUT_BENCH=&lt;passes&gt;; prints one line before
    /// the passes start, so a profiler can be attached for exactly that window.
    /// </summary>
    public static async Task<string> RunLayoutAsync(Node root, Func<Task> flushLayout, int passes)
    {
        var milliseconds = new List<double>(passes);
        long calls = 0;
        long cacheHits = 0;
        long snapshots = 0, snapshotBoxes = 0, restores = 0;
        Console.WriteLine($"[layout-bench] start passes={passes}");
        for (int i = 0; i < passes; i++)
        {
            root.MarkDirty(InvalidationKind.Layout);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await flushLayout().ConfigureAwait(false);
            milliseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var counters = FenBrowser.FenEngine.Layout.Contexts.FormattingContext.PassCounters;
            calls += counters.Calls;
            cacheHits += counters.CacheHits;
            var snapshotCounters = FenBrowser.FenEngine.Layout.Tree.LayoutBoxStore.SnapshotCounters;
            snapshots += snapshotCounters.Taken;
            snapshotBoxes += snapshotCounters.BoxesCloned;
            restores += snapshotCounters.Restores;
        }

        milliseconds.Sort();
        return string.Create(CultureInfo.InvariantCulture,
            $"layout-bench passes={passes} median={milliseconds[passes / 2]:F1}ms min={milliseconds[0]:F1}ms max={milliseconds[^1]:F1}ms " +
            $"fcCalls/pass={calls / Math.Max(1, passes)} cacheHits/pass={cacheHits / Math.Max(1, passes)} " +
            $"snapshots/pass={snapshots / Math.Max(1, passes)} clonedBoxes/pass={snapshotBoxes / Math.Max(1, passes)} restores/pass={restores / Math.Max(1, passes)}");
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
            $"[frame-bench] scrollY={scrollY,6:F0} rebuilt={t?.PaintTreeRebuilt} layout={t?.LayoutDurationMs,7:F1} paint={t?.PaintDurationMs,7:F1} raster={t?.RasterDurationMs,8:F1} total={t?.TotalDurationMs,8:F1} paintNodes={t?.PaintNodeCount} boxes={t?.BoxCount} layers={t?.CompositedLayerCount}/{t?.PromotedLayerCount} tiles={renderer.LastRetainedTileRasterization.RasterizedTileCount}/{renderer.LastRetainedTileRasterization.VisibleTileCount}"));
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
