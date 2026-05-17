using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Cross-site phase profile: run the same pipeline against several saved snapshots
    /// (x.com, github, hacker news, react docs) so we can see whether the same hot spots
    /// dominate everywhere or specific page shapes have their own bottlenecks.
    /// </summary>
    public class MultiPageProfile
    {
        private readonly ITestOutputHelper _out;

        public MultiPageProfile(ITestOutputHelper output)
        {
            _out = output;
        }

        private void WriteLine(string s = "")
        {
            _out.WriteLine(s);
            try { File.AppendAllText(@"C:\Users\udayk\Videos\fenbrowser-test\multi_page_profile.log", s + Environment.NewLine); } catch { }
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

        [Fact(DisplayName = "Multi-site phase profile")]
        public async Task Profile_MultipleRealPages_ReportsPerSiteBreakdown()
        {
            try { File.WriteAllText(@"C:\Users\udayk\Videos\fenbrowser-test\multi_page_profile.log", ""); } catch { }

            var samples = new[]
            {
                "sample_xcom.html",
                "sample_github.html",
                "sample_hackernews.html",
                "sample_reactdocs.html"
            };

            WriteLine($"{"site",-25} {"bytes",10} {"parse",8} {"js",8} {"css",8} {"layout",8} {"total",10}   {"nodes",6} {"scripts",8} {"jsbytes",10}");
            WriteLine(new string('-', 120));

            foreach (var sample in samples)
            {
                var path = ResolveFixture(sample);
                if (path == null) { WriteLine($"{sample,-25} (missing)"); continue; }
                var html = File.ReadAllText(path);

                var swParse = Stopwatch.StartNew();
                var doc = HtmlParser.ParseDocument(html);
                swParse.Stop();

                int nodeCount = CountNodes(doc.DocumentElement);
                long jsBytes = 0;
                int jsCount = 0;
                var swJs = Stopwatch.StartNew();
                try
                {
                    var runtime = new FenRuntime();
                    foreach (var script in EnumerateInlineScripts(doc.DocumentElement))
                    {
                        jsBytes += script.Length;
                        try { runtime.ExecuteSimple(script); jsCount++; } catch { }
                    }
                }
                catch { }
                swJs.Stop();

                var swStyle = Stopwatch.StartNew();
                Dictionary<Node, FenBrowser.Core.Css.CssComputed> styles = null;
                try
                {
                    var cssEngine = new CustomCssEngine();
                    styles = await cssEngine.ComputeStylesAsync(doc.DocumentElement,
                        new Uri("https://example.com/"),
                        _ => Task.FromResult(string.Empty),
                        1280, 800);
                }
                catch { }
                swStyle.Stop();

                var swLayout = Stopwatch.StartNew();
                if (styles != null && styles.Count > 0)
                {
                    try
                    {
                        var engine = new LayoutEngine(styles, 1280, 800);
                        engine.ComputeLayout(doc.DocumentElement, 1280, 800);
                    }
                    catch { }
                }
                swLayout.Stop();

                double total = swParse.Elapsed.TotalMilliseconds + swJs.Elapsed.TotalMilliseconds
                             + swStyle.Elapsed.TotalMilliseconds + swLayout.Elapsed.TotalMilliseconds;

                string siteName = sample.Replace("sample_", "").Replace(".html", "");
                WriteLine($"{siteName,-25} {html.Length,10:N0} {swParse.Elapsed.TotalMilliseconds,8:F1} {swJs.Elapsed.TotalMilliseconds,8:F1} {swStyle.Elapsed.TotalMilliseconds,8:F1} {swLayout.Elapsed.TotalMilliseconds,8:F1} {total,10:F1}   {nodeCount,6} {jsCount,8} {jsBytes,10:N0}");
            }

            WriteLine(new string('-', 120));
            WriteLine("(all times in ms; viewport 1280x800; external CSS/JS not loaded)");
        }

        private static int CountNodes(Node n)
        {
            if (n == null) return 0;
            int c = 1;
            for (var k = n.FirstChild; k != null; k = k.NextSibling) c += CountNodes(k);
            return c;
        }

        private static IEnumerable<string> EnumerateInlineScripts(Node n)
        {
            if (n == null) yield break;
            if (n is Element e && string.Equals(e.LocalName, "script", StringComparison.OrdinalIgnoreCase) && !e.HasAttribute("src"))
            {
                var sb = new System.Text.StringBuilder();
                for (var k = e.FirstChild; k != null; k = k.NextSibling)
                    if (k is Text t) sb.Append(t.Data);
                var body = sb.ToString();
                if (!string.IsNullOrWhiteSpace(body)) yield return body;
            }
            for (var k = n.FirstChild; k != null; k = k.NextSibling)
                foreach (var s in EnumerateInlineScripts(k)) yield return s;
        }
    }
}
