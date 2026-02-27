using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class RendererViewportHardeningTests
    {
        [Fact]
        public void Render_InvalidViewportDimensions_AreSanitizedAndDoNotCrash()
        {
            var parser = new HtmlParser("<!doctype html><html><body><div>hello</div></body></html>");
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);

            var ex = Record.Exception(() =>
                renderer.Render(
                    html,
                    canvas,
                    styles,
                    new SKRect(0, 0, float.PositiveInfinity, float.NegativeInfinity),
                    "https://test.local",
                    (size, overlays) => { }));

            Assert.Null(ex);
            Assert.NotNull(renderer.LastLayout);
            Assert.True(renderer.LastLayout.ViewportWidth > 0f && renderer.LastLayout.ViewportWidth <= 16384f);
            Assert.True(renderer.LastLayout.ViewportHeight > 0f && renderer.LastLayout.ViewportHeight <= 16384f);
        }
    }
}
