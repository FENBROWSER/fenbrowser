using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Phase-timing harness: load a real-world HTML page (default: saved x.com snapshot at
    /// FenBrowser.Tests/Engine/sample_xcom.html) and report wall-clock per pipeline stage.
    /// Purpose is to identify the *actual* bottleneck on x.com-shaped pages before any more
    /// JS-engine work, which has only been validated against micro-benchmarks.
    /// </summary>
    public class PageLoadProfile
    {
        private readonly ITestOutputHelper _out;

        public PageLoadProfile(ITestOutputHelper output)
        {
            _out = output;
        }

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

        private void WriteLine(string s = "")
        {
            _out.WriteLine(s);
            try { File.AppendAllText(@"C:\Users\udayk\Videos\fenbrowser-test\page_profile.log", s + Environment.NewLine); } catch {}
        }

        [Fact(DisplayName = "x.com phase-timing profile")]
        public async Task Profile_RealPage_ReportsPhaseBreakdown()
        {
            try { File.WriteAllText(@"C:\Users\udayk\Videos\fenbrowser-test\page_profile.log", ""); } catch {}
            var fixturePath = ResolveFixture("sample_xcom.html");
            if (fixturePath == null)
            {
                WriteLine("No fixture; skipping. (Save x.com HTML to FenBrowser.Tests/Engine/sample_xcom.html)");
                return;
            }

            var html = File.ReadAllText(fixturePath);
            WriteLine($"Loaded fixture: {fixturePath} ({html.Length:N0} bytes)");
            WriteLine(new string('-', 60));

            // === 1. HTML parse (tokenize + tree-build combined) ===
            var swParse = Stopwatch.StartNew();
            var doc = HtmlParser.ParseDocument(html);
            swParse.Stop();
            int nodeCount = CountNodes(doc.DocumentElement);
            int elementCount = CountElements(doc.DocumentElement);
            int textNodeCount = CountTextNodes(doc.DocumentElement);
            long textBytes = SumTextBytes(doc.DocumentElement);
            int scriptNodes = CountByTag(doc.DocumentElement, "script");
            int styleNodes = CountByTag(doc.DocumentElement, "style");
            int divCount = CountByTag(doc.DocumentElement, "div");
            int linkCount = CountByTag(doc.DocumentElement, "link");
            int metaCount = CountByTag(doc.DocumentElement, "meta");
            WriteLine($"HTML parse       {swParse.Elapsed.TotalMilliseconds,10:F1} ms  ({nodeCount} nodes total)");
            WriteLine($"  elements={elementCount}, text-nodes={textNodeCount}, text-bytes={textBytes:N0}");
            WriteLine($"  by tag: script={scriptNodes}, style={styleNodes}, div={divCount}, link={linkCount}, meta={metaCount}");

            // Re-parse to time second-call (warm cache, allocations primed).
            var sw2 = Stopwatch.StartNew();
            var doc2 = HtmlParser.ParseDocument(html);
            sw2.Stop();
            WriteLine($"  re-parse       {sw2.Elapsed.TotalMilliseconds,10:F1} ms  (warm)");

            // === 2. JS execution (inline <script> bodies only, no external network) ===
            var swJs = Stopwatch.StartNew();
            long jsBytes = 0;
            int jsExecuted = 0;
            int jsErrored = 0;
            try
            {
                var runtime = new FenRuntime();
                foreach (var script in EnumerateInlineScripts(doc.DocumentElement))
                {
                    jsBytes += script.Length;
                    try
                    {
                        runtime.ExecuteSimple(script);
                        jsExecuted++;
                    }
                    catch (Exception)
                    {
                        jsErrored++;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLine($"  JS phase aborted: {ex.GetType().Name}: {ex.Message}");
            }
            swJs.Stop();
            WriteLine($"JS execute       {swJs.Elapsed.TotalMilliseconds,10:F1} ms  ({jsExecuted} ok, {jsErrored} err, {jsBytes:N0} bytes)");

            // === 3. CSS cascade (inline <style> bodies; external skipped to stay offline) ===
            var swStyle = Stopwatch.StartNew();
            Dictionary<Node, FenBrowser.Core.Css.CssComputed> styles = null;
            try
            {
                var cssEngine = new CustomCssEngine();
                styles = await cssEngine.ComputeStylesAsync(
                    doc.DocumentElement,
                    new Uri("https://x.com/"),
                    fetchExternalCssAsync: _ => Task.FromResult(string.Empty),
                    viewportWidth: 1280,
                    viewportHeight: 800);
            }
            catch (Exception ex)
            {
                WriteLine($"  CSS phase failed: {ex.GetType().Name}: {ex.Message}");
            }
            swStyle.Stop();
            int styledNodes = styles?.Count ?? 0;
            WriteLine($"CSS cascade      {swStyle.Elapsed.TotalMilliseconds,10:F1} ms  ({styledNodes} styled nodes)");

            // === 4. Layout ===
            var swLayout = Stopwatch.StartNew();
            int boxCount = 0;
            if (styles != null && styles.Count > 0)
            {
                try
                {
                    var engine = new LayoutEngine(styles, 1280, 800);
                    engine.ComputeLayout(doc.DocumentElement, 1280, 800);
                    boxCount = engine.AllBoxes?.Count ?? 0;
                }
                catch (Exception ex)
                {
                    WriteLine($"  Layout failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            swLayout.Stop();
            WriteLine($"Layout           {swLayout.Elapsed.TotalMilliseconds,10:F1} ms  ({boxCount} boxes)");

            WriteLine(new string('-', 60));
            double total = swParse.Elapsed.TotalMilliseconds + swJs.Elapsed.TotalMilliseconds
                + swStyle.Elapsed.TotalMilliseconds + swLayout.Elapsed.TotalMilliseconds;
            WriteLine($"TOTAL            {total,10:F1} ms");
            WriteLine();
            WriteLine("Share of total:");
            WriteLine($"  HTML parse  : {swParse.Elapsed.TotalMilliseconds / total * 100,5:F1}%");
            WriteLine($"  JS execute  : {swJs.Elapsed.TotalMilliseconds / total * 100,5:F1}%");
            WriteLine($"  CSS cascade : {swStyle.Elapsed.TotalMilliseconds / total * 100,5:F1}%");
            WriteLine($"  Layout      : {swLayout.Elapsed.TotalMilliseconds / total * 100,5:F1}%");
        }

        private static int CountNodes(Node n)
        {
            if (n == null) return 0;
            int c = 1;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += CountNodes(k);
            return c;
        }

        private static int CountByTag(Node n, string tag)
        {
            if (n == null) return 0;
            int c = (n is Element e && string.Equals(e.LocalName, tag, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += CountByTag(k, tag);
            return c;
        }

        private static int CountElements(Node n)
        {
            if (n == null) return 0;
            int c = (n is Element) ? 1 : 0;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += CountElements(k);
            return c;
        }

        private static int CountTextNodes(Node n)
        {
            if (n == null) return 0;
            int c = (n is Text) ? 1 : 0;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += CountTextNodes(k);
            return c;
        }

        private static long SumTextBytes(Node n)
        {
            if (n == null) return 0;
            long c = (n is Text t && t.Data != null) ? t.Data.Length : 0;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += SumTextBytes(k);
            return c;
        }

        private static IEnumerable<string> EnumerateInlineScripts(Node n)
        {
            if (n == null) yield break;
            if (n is Element e && string.Equals(e.LocalName, "script", StringComparison.OrdinalIgnoreCase))
            {
                if (!e.HasAttribute("src"))
                {
                    var sb = new System.Text.StringBuilder();
                    for (var k = e.FirstChild; k != null; k = k.NextSibling)
                    {
                        if (k is Text t) sb.Append(t.Data);
                    }
                    var body = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(body)) yield return body;
                }
            }
            for (var k = n.FirstChild; k != null; k = k.NextSibling)
            {
                foreach (var s in EnumerateInlineScripts(k)) yield return s;
            }
        }
    }
}
