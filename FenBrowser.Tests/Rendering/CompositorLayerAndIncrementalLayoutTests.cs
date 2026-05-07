using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CompositorLayerAndIncrementalLayoutTests
    {
        private static Task<string> EmptyCssFetch(Uri _) => Task.FromResult(string.Empty);

        [Fact]
        public async Task RenderFrame_ReportsPromotedCompositedLayers_ForTransformOpacityAndWillChange()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='hero' style='width:120px;height:40px;opacity:0.85;transform:translateX(10px);will-change:transform,opacity;background:#0f172a'>hero</div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var document = parser.Parse();
            var html = document.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, EmptyCssFetch, viewportWidth: 256, viewportHeight: 128);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(256, 128);
            using var canvas = new SKCanvas(bitmap);
            var result = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 256, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Style,
                RequestedBy = "CompositorLayerAndIncrementalLayoutTests.LayerPromotion",
                EmitVerificationReport = false
            });

            Assert.NotNull(result);
            Assert.NotNull(result.Telemetry);
            Assert.True(result.Telemetry.CompositedLayerCount > 0);
            Assert.True(result.Telemetry.PromotedLayerCount > 0);
        }

        [Fact]
        public async Task RenderFrame_UsesIncrementalLayout_ForOutOfFlowDirtySubtree()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='anchor' style='position:relative;width:220px;height:120px'><div id='abs' style='position:absolute;left:10px;top:10px;width:64px;height:24px;background:#22c55e'>abs</div></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var document = parser.Parse();
            var html = document.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var abs = document.GetElementById("abs");
            Assert.NotNull(abs);

            var styles = await CssLoader.ComputeAsync(html, baseUri, EmptyCssFetch, viewportWidth: 320, viewportHeight: 180);
            var renderer = new SkiaDomRenderer();

            using (var firstBitmap = new SKBitmap(320, 180))
            using (var firstCanvas = new SKCanvas(firstBitmap))
            {
                var first = renderer.RenderFrame(new RenderFrameRequest
                {
                    Root = html,
                    Canvas = firstCanvas,
                    Styles = styles,
                    Viewport = new SKRect(0, 0, 320, 180),
                    BaseUrl = baseUri.AbsoluteUri,
                    InvalidationReason = RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "CompositorLayerAndIncrementalLayoutTests.First",
                    EmitVerificationReport = false
                });
                Assert.NotNull(first);
            }

            abs.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);

            using var secondBitmap = new SKBitmap(320, 180);
            using var secondCanvas = new SKCanvas(secondBitmap);
            var second = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 320, 180),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Layout,
                RequestedBy = "CompositorLayerAndIncrementalLayoutTests.Second",
                EmitVerificationReport = false
            });

            Assert.NotNull(second);
            Assert.NotNull(second.Telemetry);
            Assert.True(second.Telemetry.LayoutUpdated);
            Assert.True(second.Telemetry.UsedIncrementalLayout);
            Assert.True(second.Telemetry.IncrementalLayoutRootCount >= 1);
        }
    }
}
