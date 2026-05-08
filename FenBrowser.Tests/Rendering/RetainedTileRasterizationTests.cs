using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class RetainedTileRasterizationTests
    {
        [Fact]
        public async Task RenderFrame_InitialFrame_UsesRetainedTileRasterizer()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div style='width:512px;height:512px;background:#22c55e'></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 512, viewportHeight: 512);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(512, 512);
            using var canvas = new SKCanvas(bitmap);

            var frame = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 512, 512),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "RetainedTileRasterizationTests.InitialFrame"
            });

            Assert.NotNull(frame);
            Assert.Equal(RenderFrameRasterMode.Full, frame.RasterMode);
            Assert.True(renderer.LastRetainedTileRasterization.Enabled);
            Assert.Equal(renderer.LastRetainedTileRasterization.VisibleTileCount, renderer.LastRetainedTileRasterization.RasterizedTileCount);
            Assert.True(renderer.LastRetainedTileRasterization.VisibleTileCount >= 4);
        }

        [Fact]
        public async Task RenderFrame_LocalStyleChange_ReusesCleanTiles()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='box-a' style='position:absolute;left:0;top:0;width:64px;height:64px;background:#22c55e'></div><div id='box-b' style='position:absolute;left:384px;top:384px;width:64px;height:64px;background:#0ea5e9'></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var boxA = doc.GetElementById("box-a");
            Assert.NotNull(boxA);

            var initialStyles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 512, viewportHeight: 512);
            var renderer = new SkiaDomRenderer();

            using var firstBitmap = new SKBitmap(512, 512);
            using var firstCanvas = new SKCanvas(firstBitmap);
            var firstFrame = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = firstCanvas,
                Styles = initialStyles,
                Viewport = new SKRect(0, 0, 512, 512),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "RetainedTileRasterizationTests.First"
            });
            Assert.NotNull(firstFrame);

            boxA.SetAttribute("style", "position:absolute;left:0;top:0;width:64px;height:64px;background:#ef4444");
            var updatedStyles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 512, viewportHeight: 512);

            using var secondBitmap = new SKBitmap(512, 512);
            using var secondCanvas = new SKCanvas(secondBitmap);
            secondCanvas.DrawBitmap(firstBitmap, 0, 0);

            var secondFrame = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = updatedStyles,
                Viewport = new SKRect(0, 0, 512, 512),
                BaseUrl = baseUri.AbsoluteUri,
                HasBaseFrame = true,
                InvalidationReason = RenderFrameInvalidationReason.Style,
                RequestedBy = "RetainedTileRasterizationTests.Second",
                EmitVerificationReport = false
            });

            Assert.NotNull(secondFrame);
            Assert.True(renderer.LastRetainedTileRasterization.Enabled);
            Assert.True(renderer.LastRetainedTileRasterization.RebuiltDisplayList);
            Assert.True(renderer.LastRetainedTileRasterization.RasterizedTileCount > 0);
            Assert.True(renderer.LastRetainedTileRasterization.ReusedTileCount > 0);
            Assert.True(renderer.LastRetainedTileRasterization.RasterizedTileCount < renderer.LastRetainedTileRasterization.VisibleTileCount);
        }
    }
}
