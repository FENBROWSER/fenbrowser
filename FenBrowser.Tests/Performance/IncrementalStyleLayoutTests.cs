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

namespace FenBrowser.Tests.Performance
{
    [Collection("Performance diagnostics")]
    public class IncrementalStyleLayoutTests
    {
        private static Task<string> EmptyCssFetch(Uri _) => Task.FromResult(string.Empty);

        [Fact]
        public async Task RenderFrame_StyleInvalidationReusesLayoutOutsideDirtyFlowRoot()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='isolation' style='display:flow-root;width:240px'><div id='child' style='width:120px;height:30px'>child</div></div><div style='width:200px;height:80px'>clean sibling</div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var document = new HtmlParser(htmlSource, baseUri).Parse();
            var html = document.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var child = Assert.IsType<Element>(document.GetElementById("child"));
            var styles = await CssLoader.ComputeAsync(
                html, baseUri, EmptyCssFetch, viewportWidth: 360, viewportHeight: 220);
            var renderer = new SkiaDomRenderer
            {
                SafetyPolicy = new RendererSafetyPolicy
                {
                    EnableWatchdog = false,
                    SkipRasterWhenOverBudget = false
                }
            };

            using (var firstBitmap = new SKBitmap(360, 220))
            using (var firstCanvas = new SKCanvas(firstBitmap))
            {
                renderer.RenderFrame(new RenderFrameRequest
                {
                    Root = html,
                    Canvas = firstCanvas,
                    Styles = styles,
                    Viewport = new SKRect(0, 0, 360, 220),
                    BaseUrl = baseUri.AbsoluteUri,
                    InvalidationReason = RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "IncrementalStyleLayoutTests.First",
                    EmitVerificationReport = false
                });
            }

            child.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);

            using var secondBitmap = new SKBitmap(360, 220);
            using var secondCanvas = new SKCanvas(secondBitmap);
            var second = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 360, 220),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Style,
                RequestedBy = "IncrementalStyleLayoutTests.Second",
                EmitVerificationReport = false
            });

            Assert.NotNull(second.Telemetry);
            Assert.True(second.Telemetry.UsedIncrementalLayout);
            Assert.Equal(1, second.Telemetry.IncrementalLayoutRootCount);
        }
    }
}
