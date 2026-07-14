using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FenBrowser.FenEngine.Rendering.Performance
{
    public static class PerformancePageRenderer
    {
        public static string Render(Uri uri)
        {
            ApplyAction(uri);
            var history = PerformanceDiagnosticsStore.GetNavigationHistory();
            var latest = history.LastOrDefault();
            string reportJson = JsonSerializer.Serialize(
                new { Recording = PerformanceDiagnosticsStore.IsRecording, Navigations = history },
                new JsonSerializerOptions { WriteIndented = true });

            var html = new StringBuilder(16 * 1024);
            html.Append("<!doctype html><html><head><meta charset='utf-8'><title>FenBrowser Performance</title>");
            html.Append("<style>body{font:14px Segoe UI,Arial;margin:0;background:#0b1020;color:#e5e7eb}main{max-width:1180px;margin:auto;padding:28px}h1{margin:0 0 8px}h2{font-size:17px;margin:0 0 14px}section{background:#131b2f;border:1px solid #293550;border-radius:12px;padding:18px;margin:16px 0}.toolbar a,button{display:inline-block;background:#2563eb;color:white;text-decoration:none;border:0;border-radius:7px;padding:8px 12px;margin:4px}.toolbar .secondary{background:#334155}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:7px;border-bottom:1px solid #293550}th{color:#93c5fd}.muted{color:#94a3b8}.ok{color:#86efac}.stopped{color:#fbbf24}pre{white-space:pre-wrap;max-height:360px;overflow:auto;background:#090d18;padding:12px;border-radius:8px}</style></head><body><main>");
            html.Append("<h1>FenBrowser Performance</h1><p class='")
                .Append(PerformanceDiagnosticsStore.IsRecording ? "ok'>Recording" : "stopped'>Recording stopped")
                .Append(" &middot; bounded to the latest ").Append(PerformanceDiagnosticsStore.MaximumNavigationHistory).Append(" navigations</p>");
            html.Append("<div class='toolbar'><a href='fen://performance?action=start'>Start recording</a><a class='secondary' href='fen://performance?action=stop'>Stop recording</a><a class='secondary' href='fen://performance?action=reset'>Reset counters</a><button onclick='copyReport()'>Copy report</button><button onclick='exportReport()'>Export results</button></div>");

            AppendNavigationSection(html, latest);
            AppendMemorySection(html, latest);
            AppendDocumentSection(html, latest);
            AppendInvalidationSection(html, latest);
            AppendUnavailableSection(html, "FenJS statistics", new[] { "Scripts parsed", "Bytecode instructions generated", "Instructions executed", "Function calls", "Property reads and writes", "Slow property paths", "Inline-cache hits and misses", "Objects and arrays created", "Promise jobs and microtasks", "Exceptions" });
            AppendRendererSection(html, latest);
            AppendComparisonSection(html, history);
            AppendHistorySection(html, history);

            html.Append("<section><h2>Structured report</h2><pre id='report'>")
                .Append(WebUtility.HtmlEncode(reportJson)).Append("</pre></section>");
            string scriptJson = JsonSerializer.Serialize(reportJson);
            html.Append("<script>const report=").Append(scriptJson).Append(";async function copyReport(){try{await navigator.clipboard.writeText(report)}catch(e){const t=document.createElement('textarea');t.value=report;document.body.appendChild(t);t.select();document.execCommand('copy');t.remove()}}function exportReport(){const b=new Blob([report],{type:'application/json'});const a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='fen-performance.json';a.click();URL.revokeObjectURL(a.href)}</script>");
            html.Append("</main></body></html>");
            return html.ToString();
        }

        private static void ApplyAction(Uri uri)
        {
            string query = uri?.Query ?? string.Empty;
            if (query.Contains("action=reset", StringComparison.OrdinalIgnoreCase)) PerformanceDiagnosticsStore.Reset();
            else if (query.Contains("action=stop", StringComparison.OrdinalIgnoreCase)) PerformanceDiagnosticsStore.StopRecording();
            else if (query.Contains("action=start", StringComparison.OrdinalIgnoreCase)) PerformanceDiagnosticsStore.StartRecording();
        }

        private static void AppendNavigationSection(StringBuilder html, NavigationPerformanceSnapshot? latest)
        {
            html.Append("<section><h2>Navigation timings</h2><table>");
            Row(html, "URL", latest?.Url);
            Row(html, "Navigation start", latest?.NavigationStartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            Row(html, "Response start", null);
            RowMs(html, "HTML parse", latest?.HtmlParseMs);
            RowMs(html, "CSS parse and style", latest?.CssAndStyleMs);
            RowMs(html, "Script parse", null);
            RowMs(html, "Script execution", latest?.ScriptExecutionMs);
            RowMs(html, "Layout", latest?.LayoutMs);
            RowMs(html, "Paint generation", latest?.PaintGenerationMs);
            RowMs(html, "Rasterization", latest?.RasterMs);
            RowMs(html, "First paint", latest?.InitialVisualTreeMs);
            RowMs(html, "Load completion", latest?.TotalLoadMs);
            html.Append("</table></section>");
        }

        private static void AppendMemorySection(StringBuilder html, NavigationPerformanceSnapshot? latest)
        {
            html.Append("<section><h2>Memory and GC</h2><table>");
            RowBytes(html, "Managed allocated bytes", latest?.ManagedAllocatedBytes);
            RowBytes(html, "Layout allocated bytes (latest frame)", latest?.LayoutAllocatedBytes);
            RowBytes(html, "Paint allocated bytes (latest frame)", latest?.PaintAllocatedBytes);
            RowBytes(html, "Raster allocated bytes (latest frame)", latest?.RasterAllocatedBytes);
            RowBytes(html, "Current managed heap", latest?.ManagedHeapBytes);
            RowBytes(html, "Working set", latest?.WorkingSetBytes);
            Row(html, "Gen 0 collections", latest?.Gen0Collections.ToString(CultureInfo.InvariantCulture));
            Row(html, "Gen 1 collections", latest?.Gen1Collections.ToString(CultureInfo.InvariantCulture));
            Row(html, "Gen 2 collections", latest?.Gen2Collections.ToString(CultureInfo.InvariantCulture));
            Row(html, "Recent GC pause data", null);
            Row(html, "Large Object Heap data", null);
            html.Append("</table></section>");
        }

        private static void AppendDocumentSection(StringBuilder html, NavigationPerformanceSnapshot? latest)
        {
            html.Append("<section><h2>Document statistics</h2><table>");
            Row(html, "DOM nodes", latest?.DomNodeCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Element nodes", latest?.ElementNodeCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Text nodes", latest?.TextNodeCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Attributes", latest?.AttributeCount.ToString(CultureInfo.InvariantCulture));
            foreach (string name in new[] { "Stylesheets", "CSS rules", "Matched selectors" }) Row(html, name, null);
            Row(html, "Inline style cache hits (process)", latest?.InlineStyleCacheHits.ToString(CultureInfo.InvariantCulture));
            Row(html, "Inline style cache misses (process)", latest?.InlineStyleCacheMisses.ToString(CultureInfo.InvariantCulture));
            Row(html, "Inline style cache evictions (process)", latest?.InlineStyleCacheEvictions.ToString(CultureInfo.InvariantCulture));
            Row(html, "Inline style cache entries (latest cascade)", latest?.InlineStyleCacheEntries.ToString(CultureInfo.InvariantCulture));
            Row(html, "Layout objects", latest?.LayoutObjectCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Paint commands", latest?.PaintCommandCount.ToString(CultureInfo.InvariantCulture));
            foreach (string name in new[] { "Images", "Fonts", "Script count", "Event listeners" }) Row(html, name, null);
            html.Append("</table></section>");
        }

        private static void AppendInvalidationSection(StringBuilder html, NavigationPerformanceSnapshot? latest)
        {
            html.Append("<section><h2>Invalidation statistics</h2><table>");
            foreach (string name in new[] { "Full style recalculations", "Partial style recalculations", "Nodes restyled" }) Row(html, name, null);
            Row(html, "Incremental layout used", latest == null ? null : latest.UsedIncrementalLayout.ToString());
            Row(html, "Incremental layout roots", latest?.IncrementalLayoutRootCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Damage rectangles", latest?.DamageRegionCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Damaged area ratio", latest == null ? null : latest.DamageAreaRatio.ToString("P2", CultureInfo.InvariantCulture));
            html.Append("</table></section>");
        }

        private static void AppendRendererSection(StringBuilder html, NavigationPerformanceSnapshot? latest)
        {
            html.Append("<section><h2>Renderer statistics</h2><table>");
            Row(html, "Display-list command count", latest?.PaintCommandCount.ToString(CultureInfo.InvariantCulture));
            Row(html, "Raster mode", latest?.RasterMode.ToString());
            Row(html, "Text measurement calls (process)", latest?.TextMeasurementCalls.ToString(CultureInfo.InvariantCulture));
            Row(html, "Text measurement cache hits (process)", latest?.TextMeasurementCacheHits.ToString(CultureInfo.InvariantCulture));
            Row(html, "Text measurement cache misses (process)", latest?.TextMeasurementCacheMisses.ToString(CultureInfo.InvariantCulture));
            Row(html, "Text measurement cache evictions (process)", latest?.TextMeasurementCacheEvictions.ToString(CultureInfo.InvariantCulture));
            Row(html, "Cached images (process)", latest?.CachedImageCount.ToString(CultureInfo.InvariantCulture));
            RowBytes(html, "Image cache bytes (process)", latest?.ImageCacheBytes);
            Row(html, "Image cache hits (process)", latest?.ImageCacheHits.ToString(CultureInfo.InvariantCulture));
            Row(html, "Image cache misses (process)", latest?.ImageCacheMisses.ToString(CultureInfo.InvariantCulture));
            Row(html, "Image cache evictions (process)", latest?.ImageCacheEvictions.ToString(CultureInfo.InvariantCulture));
            Row(html, "Cached typefaces (process)", latest?.CachedTypefaceCount.ToString(CultureInfo.InvariantCulture));
            RowBytes(html, "Font cache bytes (process)", latest?.FontCacheBytes);
            Row(html, "Font cache hits (process)", latest?.FontCacheHits.ToString(CultureInfo.InvariantCulture));
            Row(html, "Font cache misses (process)", latest?.FontCacheMisses.ToString(CultureInfo.InvariantCulture));
            Row(html, "Font cache evictions (process)", latest?.FontCacheEvictions.ToString(CultureInfo.InvariantCulture));
            foreach (string name in new[] { "Skia draw calls", "Paint objects created", "Paths created", "Image decodes" }) Row(html, name, null);
            html.Append("</table></section>");
        }

        private static void AppendComparisonSection(StringBuilder html, IReadOnlyList<NavigationPerformanceSnapshot> history)
        {
            html.Append("<section><h2>Compare latest two navigations</h2>");
            if (history.Count < 2) { html.Append("<p class='muted'>Two recorded navigations are required.</p></section>"); return; }
            var previous = history[^2]; var latest = history[^1];
            html.Append("<table><tr><th>Metric</th><th>Previous</th><th>Latest</th><th>Difference</th></tr>");
            CompareRow(html, "Load completion", previous.TotalLoadMs, latest.TotalLoadMs, "ms");
            CompareRow(html, "Managed allocations", previous.ManagedAllocatedBytes, latest.ManagedAllocatedBytes, " B");
            CompareRow(html, "DOM nodes", previous.DomNodeCount, latest.DomNodeCount, string.Empty);
            CompareRow(html, "Paint commands", previous.PaintCommandCount, latest.PaintCommandCount, string.Empty);
            html.Append("</table></section>");
        }

        private static void AppendHistorySection(StringBuilder html, IReadOnlyList<NavigationPerformanceSnapshot> history)
        {
            html.Append("<section><h2>Recent navigations</h2><table><tr><th>URL</th><th>Load</th><th>Allocated</th><th>DOM</th><th>Raster mode</th></tr>");
            foreach (var item in history.Reverse())
            {
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(item.Url)).Append("</td><td>").Append(item.TotalLoadMs).Append(" ms</td><td>").Append(item.ManagedAllocatedBytes).Append(" B</td><td>").Append(item.DomNodeCount).Append("</td><td>").Append(item.RasterMode).Append("</td></tr>");
            }
            html.Append("</table></section>");
        }

        private static void AppendUnavailableSection(StringBuilder html, string title, IEnumerable<string> rows)
        {
            html.Append("<section><h2>").Append(title).Append("</h2><table>"); foreach (var row in rows) Row(html, row, null); html.Append("</table></section>");
        }

        private static void Row(StringBuilder html, string name, string? value) => html.Append("<tr><th>").Append(name).Append("</th><td>").Append(value == null ? "<span class='muted'>Not instrumented</span>" : WebUtility.HtmlEncode(value)).Append("</td></tr>");
        private static void RowMs(StringBuilder html, string name, double? value) => Row(html, name, value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : null);
        private static void RowBytes(StringBuilder html, string name, long? value) => Row(html, name, value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + " B" : null);
        private static void CompareRow(StringBuilder html, string name, long previous, long latest, string suffix) => html.Append("<tr><th>").Append(name).Append("</th><td>").Append(previous).Append(suffix).Append("</td><td>").Append(latest).Append(suffix).Append("</td><td>").Append(latest - previous).Append(suffix).Append("</td></tr>");
    }
}
