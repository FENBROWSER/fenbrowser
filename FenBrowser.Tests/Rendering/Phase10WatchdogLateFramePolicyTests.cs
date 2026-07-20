using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    /// <summary>
    /// Phase 10 regression: the frame watchdog must not force an expensive full
    /// raster for an animation-only over-budget frame (that drives the recurring
    /// full-raster loop). It preserves the last presentable frame and drops the
    /// obsolete animation frame instead. Structural late frames still force a fresh
    /// raster to avoid presenting geometry inconsistent with the DOM.
    /// </summary>
    public class Phase10WatchdogLateFramePolicyTests
    {
        private static async Task<(Element html, System.Collections.Generic.Dictionary<Node, CssComputed> styles, Uri baseUri)> BuildDocAsync()
        {
            const string htmlSource = "<!doctype html><html><body style=\"margin:0\"><div style=\"width:128px;height:128px;background:#00ff00\"></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(htmlSource, baseUri).Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);
            return (html, styles, baseUri);
        }

        private static RendererSafetyPolicy OverBudgetPolicy() => new RendererSafetyPolicy
        {
            EnableWatchdog = true,
            MaxFrameBudgetMs = 0,
            MaxPaintStageMs = 0,
            MaxRasterStageMs = 0,
            SkipRasterWhenOverBudget = true
        };

        [Fact]
        public async Task AnimationOverBudgetFrame_PreservesPreviousFrame_DoesNotForceFullRaster()
        {
            var (html, styles, baseUri) = await BuildDocAsync();
            var renderer = new SkiaDomRenderer();

            using var firstBitmap = new SKBitmap(128, 128);
            using var firstCanvas = new SKCanvas(firstBitmap);
            renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = firstCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "seed"
            });
            firstCanvas.Flush();

            // Tighten the budget so the next frame is always over budget before raster.
            renderer.SafetyPolicy = OverBudgetPolicy();

            using var secondBitmap = new SKBitmap(128, 128);
            using var secondCanvas = new SKCanvas(secondBitmap);
            secondCanvas.DrawBitmap(firstBitmap, 0, 0);

            var animResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                HasBaseFrame = true,
                InvalidationReason = RenderFrameInvalidationReason.Animation,
                RequestedBy = "animation",
                EmitVerificationReport = false
            });

            Assert.True(animResult.WatchdogTriggered);
            Assert.Equal(RenderFrameRasterMode.PreservedBaseFrame, animResult.RasterMode);
            Assert.NotEqual(RenderFrameWatchdogAction.ForcedFreshRaster, animResult.WatchdogAction);
            Assert.Equal(RenderFrameWatchdogAction.DroppedObsoleteAnimationFrame, animResult.WatchdogAction);
            // The preserved base frame content is intact (no blank/stale lock).
            Assert.Equal(SKColors.Lime, secondBitmap.GetPixel(64, 64));
        }

        [Fact]
        public async Task StructuralOverBudgetFirstFrame_ForcesFreshRaster()
        {
            var (html, styles, baseUri) = await BuildDocAsync();
            var renderer = new SkiaDomRenderer { SafetyPolicy = OverBudgetPolicy() };

            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);

            var result = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                HasBaseFrame = false,
                InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom,
                RequestedBy = "structural",
                EmitVerificationReport = false
            });

            Assert.True(result.WatchdogTriggered);
            Assert.Equal(RenderFrameWatchdogAction.ForcedFreshRaster, result.WatchdogAction);
        }
    }
}
