using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        [Fact]
        public async Task Render_WatchdogForcesFullRaster_WhenNoBaseFrameExists()
        {
            const string htmlSource = "<!doctype html><html><body style=\"margin:0\"><div style=\"width:128px;height:128px;background:#00ff00\"></div></body></html>";
            var parser = new HtmlParser(htmlSource, new Uri("https://test.local/"));
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, new Uri("https://test.local/"), null, viewportWidth: 128, viewportHeight: 128);

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

            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);

            renderer.Render(
                html,
                canvas,
                styles,
                new SKRect(0, 0, 128, 128),
                "https://test.local/",
                (size, overlays) => { },
                hasBaseFrame: false);
            canvas.Flush();

            Assert.True(renderer.LastFrameWatchdogTriggered);
            Assert.False(string.IsNullOrWhiteSpace(renderer.LastFrameWatchdogReason));
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(64, 64));
        }

        [Fact]
        public async Task Render_WatchdogPreservesSeededBaseFrame_WhenReusableFrameExists()
        {
            const string htmlSource = "<!doctype html><html><body style=\"margin:0\"><div style=\"width:128px;height:128px;background:#00ff00\"></div></body></html>";
            var parser = new HtmlParser(htmlSource, new Uri("https://test.local/"));
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, new Uri("https://test.local/"), null, viewportWidth: 128, viewportHeight: 128);

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

            using var bitmap = new SKBitmap(128, 128);
            bitmap.Erase(SKColors.Blue);
            using var canvas = new SKCanvas(bitmap);

            renderer.Render(
                html,
                canvas,
                styles,
                new SKRect(0, 0, 128, 128),
                "https://test.local/",
                (size, overlays) => { },
                hasBaseFrame: true);
            canvas.Flush();

            Assert.True(renderer.LastFrameWatchdogTriggered);
            Assert.False(string.IsNullOrWhiteSpace(renderer.LastFrameWatchdogReason));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(64, 64));
        }
    }
}
