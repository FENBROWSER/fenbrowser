using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// bing.com's voice-search icon: an &lt;svg style="height:100%"&gt; inside `.b_icon`
    /// (height:100%), a flex item of `.mic_cont` (height:100%, max-height:23px). CSS 2.2
    /// §10.5 resolves the percentage against the containing block's height; it must not use
    /// the viewport, nor the ancestor's laid-out box, which already contains the icon and
    /// grew it 5.5px on every layout pass. The fixture is bing.com's search form with the
    /// CSS rules that reproduce it, rendered through SkiaDomRenderer as the browser does.
    /// </summary>
    public sealed class FlexItemReplacedPercentHeightTests
    {
        [Fact]
        public async Task BingMicIcon_StaysInsideItsCappedContainer()
        {
            const int width = 1280;
            const int height = 800;
            var baseUri = new Uri("https://test.local/");
            var html = await File.ReadAllTextAsync(Path.Combine(
                FindRepositoryRoot(), "FenBrowser.Tests", "Fixtures", "Layout", "bing_mic_icon.html"));
            var doc = new HtmlParser(html, baseUri).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });

            var mic = doc.Descendants().OfType<Element>().First(e => HasClass(e, "mic_cont"));
            var icon = mic.Descendants().OfType<Element>().First(e => HasClass(e, "b_icon"));
            var svg = icon.Descendants().OfType<Element>().First(e => string.Equals(e.LocalName, "svg", StringComparison.OrdinalIgnoreCase));

            Assert.True(renderer.LastLayout.TryGetElementRect(mic, out var micRect), "mic container has no layout rect");
            Assert.True(renderer.LastLayout.TryGetElementRect(icon, out var iconRect), "mic icon has no layout rect");
            Assert.True(renderer.LastLayout.TryGetElementRect(svg, out var svgRect), "mic svg has no layout rect");

            Assert.True(iconRect.Height <= micRect.Height + 0.5f, $".b_icon {iconRect.Height} is taller than .mic_cont {micRect.Height}");
            Assert.True(svgRect.Height <= micRect.Height + 0.5f, $"mic svg {svgRect.Height} is taller than .mic_cont {micRect.Height}");
        }

        private const string DefiniteHtml = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #definite { display: flex; width: 24px; height: 24px; }
  svg { height: 100%; width: 100%; }
</style></head>
<body>
  <div id='definite'><div style='height: 100%;'><svg id='definiteSvg' width='17' height='24' viewBox='0 0 17 24'></svg></div></div>
</body></html>";

        [Fact]
        public async Task SvgUnderDefiniteFlexContainer_ResolvesToItsHeight()
        {
            const int width = 800;
            const int height = 600;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(DefiniteHtml, baseUri).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });

            var svg = doc.Descendants().OfType<Element>().First(e => e.Id == "definiteSvg");
            Assert.True(renderer.LastLayout.TryGetElementRect(svg, out var svgRect), "svg has no layout rect");
            Assert.Equal(24f, svgRect.Height, 1);
        }

        private static bool HasClass(Element element, string className) =>
            (element.GetAttribute("class") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(className, StringComparer.Ordinal);

        private static string FindRepositoryRoot()
        {
            var current = AppContext.BaseDirectory;
            for (var depth = 0; depth < 10; depth++)
            {
                if (File.Exists(Path.Combine(current, "FenBrowser.sln")))
                {
                    return current;
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            throw new InvalidOperationException("Could not locate the FenBrowser repository root.");
        }
    }
}
