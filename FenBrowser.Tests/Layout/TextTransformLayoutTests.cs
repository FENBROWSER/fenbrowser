using System;
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
    /// CSS Text 3 §2.1: `text-transform` changes the text that is measured and painted.
    /// bing.com's scope bar sets `text-transform:uppercase` on its tabs; the layout read the
    /// raw text, so the tabs showed "All", "Images" instead of "ALL", "IMAGES" and were
    /// sized for the lowercase glyphs.
    /// </summary>
    public sealed class TextTransformLayoutTests
    {
        private const string Html = @"<!doctype html><html><head><style>
body { margin: 0; font: 16px Arial, sans-serif; }
span { display: inline-block; white-space: nowrap; }
li { text-transform: uppercase; }
</style></head><body>
<div><span id='upper' style='text-transform:uppercase'>scope tabs</span></div>
<div><span id='upperRef'>SCOPE TABS</span></div>
<div><span id='plain'>scope tabs</span></div>
<div><span id='cap' style='text-transform:capitalize'>hello wORLD</span></div>
<div><span id='capRef'>Hello WORLD</span></div>
<div><span id='lower' style='text-transform:lowercase'>MIXED Case</span></div>
<div><span id='lowerRef'>mixed case</span></div>
<ul><li><span id='inherited'>images</span></li></ul>
<div><span id='inheritedRef'>IMAGES</span></div>
</body></html>";

        [Fact]
        public async Task TransformedText_IsMeasuredAsTheTransformedString()
        {
            var (renderer, doc) = await RenderAsync();

            AssertSameWidth(renderer, doc, "upper", "upperRef");
            AssertSameWidth(renderer, doc, "cap", "capRef");
            AssertSameWidth(renderer, doc, "lower", "lowerRef");
            AssertSameWidth(renderer, doc, "inherited", "inheritedRef");
            Assert.True(Width(renderer, doc, "upper") > Width(renderer, doc, "plain") + 5f,
                "uppercase text should be wider than the same text in lowercase");
        }

        private static void AssertSameWidth(SkiaDomRenderer renderer, Document doc, string id, string referenceId)
        {
            float width = Width(renderer, doc, id);
            float reference = Width(renderer, doc, referenceId);
            Assert.True(Math.Abs(width - reference) < 0.5f, $"#{id} is {width} wide, #{referenceId} is {reference}");
        }

        private static float Width(SkiaDomRenderer renderer, Document doc, string id)
        {
            var element = doc.GetElementById(id);
            Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"#{id} has no layout rect");
            return rect.Width;
        }

        private static async Task<(SkiaDomRenderer Renderer, Document Doc)> RenderAsync()
        {
            const int width = 1024;
            const int height = 600;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(Html, baseUri).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });
            return (renderer, doc);
        }
    }
}
