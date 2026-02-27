using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class RenderWatchdogTests
    {
        [Fact]
        public void Render_WatchdogCanAbortBeforeRaster_WhenBudgetIsExceeded()
        {
            var parser = new HtmlParser("<!doctype html><html><body><div>watchdog</div></body></html>");
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            var renderer = new SkiaDomRenderer
            {
                SafetyPolicy = new RendererSafetyPolicy
                {
                    EnableWatchdog = true,
                    MaxFrameBudgetMs = -1.0,
                    MaxPaintStageMs = -1.0,
                    MaxRasterStageMs = -1.0,
                    SkipRasterWhenOverBudget = true
                }
            };

            var styles = new Dictionary<Node, CssComputed>();
            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);

            var ex = Record.Exception(() =>
                renderer.Render(
                    html,
                    canvas,
                    styles,
                    new SKRect(0, 0, 128, 128),
                    "https://test.local",
                    (size, overlays) => { }));

            Assert.Null(ex);
            Assert.True(renderer.LastFrameWatchdogTriggered);
            Assert.False(string.IsNullOrWhiteSpace(renderer.LastFrameWatchdogReason));
        }
    }
}
