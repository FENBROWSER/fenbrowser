using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class InputOverlayColorTests
    {
        [Fact]
        public async Task TransparentInputText_UsesVisibleOverlayFallbackColor()
        {
            const string html = @"<!doctype html>
<html>
<head>
  <style>
    body { color: rgb(17, 34, 51); }
    input { width: 320px; height: 40px; color: transparent; }
  </style>
</head>
<body>
  <input id='q' value='fenbrowser' />
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://example.test/"));
            var document = parser.Parse();
            var root = document.Children.OfType<Element>()
                .First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));

            var styles = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null, viewportWidth: 1024, viewportHeight: 768);
            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(1024, 768);
            using var canvas = new SKCanvas(bitmap);

            renderer.Render(
                root,
                canvas,
                styles,
                new SKRect(0, 0, 1024, 768),
                "https://example.test/");

            Element input = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "q", StringComparison.Ordinal));
            var overlay = renderer.CurrentOverlays.FirstOrDefault(o => ReferenceEquals(o.Node, input));

            Assert.NotNull(overlay);
            Assert.True(overlay.TextColor.HasValue);
            Assert.Equal((byte)255, overlay.TextColor.Value.Alpha);
            Assert.Equal((byte)17, overlay.TextColor.Value.Red);
            Assert.Equal((byte)34, overlay.TextColor.Value.Green);
            Assert.Equal((byte)51, overlay.TextColor.Value.Blue);
        }
    }
}
