using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Render a saved real-world page to a PNG and emit a diagnostic report so
    /// we can see what FenBrowser actually paints versus what the real site
    /// looks like. Outputs to repo root:
    ///   real_site_render.png        — full-viewport snapshot
    ///   real_site_render.diag.txt   — issue report
    /// </summary>
    public class RealSiteRenderDiagnostics
    {
        private readonly ITestOutputHelper _out;

        public RealSiteRenderDiagnostics(ITestOutputHelper output) { _out = output; }

        private static string ResolveFixture(string fileName)
        {
            var probe = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                var candidate = Path.Combine(probe, "FenBrowser.Tests", "Engine", fileName);
                if (File.Exists(candidate)) return candidate;
                probe = Path.GetDirectoryName(probe);
            }
            return null;
        }

        [Theory]
        [InlineData("sample_github.html", "https://github.com/")]
        [InlineData("sample_xcom.html", "https://x.com/")]
        [InlineData("sample_hackernews.html", "https://news.ycombinator.com/")]
        [InlineData("sample_reactdocs.html", "https://react.dev/")]
        public async Task Render_SavedRealSite_ToPng(string sample, string baseUrl)
        {
            var path = ResolveFixture(sample);
            if (path == null) return;

            const int W = 1280;
            const int H = 800;

            string html = File.ReadAllText(path);
            var baseUri = new Uri(baseUrl);
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var rootEl = doc.Children.OfType<Element>().FirstOrDefault(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            if (rootEl == null) return;

            var computed = await CssLoader.ComputeAsync(rootEl, baseUri, FetchCssAsync, viewportWidth: W, viewportHeight: H);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(W, H);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            renderer.Render(rootEl, canvas, computed, new SKRect(0, 0, W, H), baseUri.AbsoluteUri, (s, o) => { });
            canvas.Flush();

            string siteName = sample.Replace("sample_", "").Replace(".html", "");
            string pngPath = Path.Combine(RepoRoot(), $"real_site_render_{siteName}.png");
            using (var img = SKImage.FromBitmap(bitmap))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
            using (var fs = File.Create(pngPath))
            {
                data.SaveTo(fs);
            }

            string diagPath = Path.Combine(RepoRoot(), $"real_site_render_{siteName}.diag.txt");
            File.WriteAllText(diagPath, BuildDiagnosticReport(rootEl, computed, renderer.LastLayout, W, H));

            _out.WriteLine($"[{siteName}] PNG: {pngPath}");
            _out.WriteLine($"[{siteName}] Diagnostics: {diagPath}");
        }

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Fetch external CSS with on-disk cache so reruns are fast and offline-safe.
        private static async Task<string> FetchCssAsync(Uri uri)
        {
            string url = uri?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            try
            {
                string cacheDir = Path.Combine(RepoRoot(), ".css_cache");
                Directory.CreateDirectory(cacheDir);
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
                string cached = Path.Combine(cacheDir, key + ".css");
                if (File.Exists(cached))
                {
                    return await File.ReadAllTextAsync(cached);
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (FenBrowser test harness)");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { return string.Empty; }
                string body = await resp.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(cached, body);
                return body;
            }
            catch { return string.Empty; }
        }

        private static string RepoRoot()
        {
            var probe = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                if (File.Exists(Path.Combine(probe, "FenBrowser.sln"))) return probe;
                probe = Path.GetDirectoryName(probe);
            }
            return AppContext.BaseDirectory;
        }

        private static string BuildDiagnosticReport(Element root, Dictionary<Node, CssComputed> computed, LayoutResult layout, int W, int H)
        {
            var sb = new StringBuilder();
            int totalElements = 0;
            int elementsWithRect = 0;
            int elementsWithZeroArea = 0;
            int elementsOffscreen = 0;
            int elementsWithTextButZeroWidth = 0;
            int elementsWithBackgroundButHidden = 0;
            int elementsMissingFromLayout = 0;
            int displayNoneCount = 0;
            float maxRight = 0, maxBottom = 0;

            var issues = new List<string>();

            foreach (var node in root.Descendants())
            {
                if (node is not Element el) continue;
                totalElements++;

                if (!computed.TryGetValue(el, out var style) || style == null)
                {
                    issues.Add($"NO_STYLE  <{el.TagName.ToLowerInvariant()}{Selector(el)}>");
                    continue;
                }

                string display = style.Display ?? string.Empty;
                if (string.Equals(display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    displayNoneCount++;
                    continue;
                }

                ElementGeometry rect = default;
                bool hasRect = layout?.TryGetElementRect(el, out rect) == true;
                if (!hasRect)
                {
                    elementsMissingFromLayout++;
                    if (issues.Count < 200 && HasMeaningfulContent(el))
                        issues.Add($"NO_RECT   <{el.TagName.ToLowerInvariant()}{Selector(el)}>  display={display}");
                    continue;
                }

                elementsWithRect++;
                maxRight = Math.Max(maxRight, rect.X + rect.Width);
                maxBottom = Math.Max(maxBottom, rect.Y + rect.Height);

                bool zeroArea = rect.Width <= 0.5f || rect.Height <= 0.5f;
                if (zeroArea) elementsWithZeroArea++;
                bool offscreen = rect.X + rect.Width <= 0 || rect.Y + rect.Height <= 0 || rect.X >= W || rect.Y >= H;
                if (offscreen) elementsOffscreen++;

                if (zeroArea && HasMeaningfulContent(el))
                {
                    elementsWithTextButZeroWidth++;
                    if (issues.Count < 200)
                        issues.Add($"ZERO_AREA <{el.TagName.ToLowerInvariant()}{Selector(el)}>  rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0})  display={display}");
                }

                if (style.BackgroundColor != null && zeroArea)
                {
                    elementsWithBackgroundButHidden++;
                }
            }

            sb.AppendLine($"viewport         : {W}x{H}");
            sb.AppendLine($"total elements   : {totalElements}");
            sb.AppendLine($"display:none     : {displayNoneCount}");
            sb.AppendLine($"with layout rect : {elementsWithRect}");
            sb.AppendLine($"missing rect     : {elementsMissingFromLayout}");
            sb.AppendLine($"zero-area boxes  : {elementsWithZeroArea}");
            sb.AppendLine($"  with content   : {elementsWithTextButZeroWidth}  <-- likely broken");
            sb.AppendLine($"  with bg paint  : {elementsWithBackgroundButHidden}");
            sb.AppendLine($"offscreen boxes  : {elementsOffscreen}");
            sb.AppendLine($"content extent   : right={maxRight:F0}  bottom={maxBottom:F0}");
            sb.AppendLine();
            sb.AppendLine("--- top issues (first 200) ---");
            foreach (var i in issues) sb.AppendLine(i);
            return sb.ToString();
        }

        private static string Selector(Element el)
        {
            var s = new StringBuilder();
            var id = el.GetAttribute("id");
            if (!string.IsNullOrEmpty(id)) s.Append("#").Append(id);
            var cls = el.GetAttribute("class");
            if (!string.IsNullOrEmpty(cls))
            {
                var firstClass = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstClass)) s.Append(".").Append(firstClass);
            }
            return s.ToString();
        }

        private static bool HasMeaningfulContent(Element el)
        {
            for (var child = el.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Text t && !string.IsNullOrWhiteSpace(t.Data)) return true;
                if (child is Element ce)
                {
                    string tag = ce.TagName?.ToLowerInvariant();
                    if (tag == "img" || tag == "svg" || tag == "input" || tag == "button" || tag == "a")
                        return true;
                }
            }
            return false;
        }
    }
}
