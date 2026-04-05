using System;
using System.Collections.Generic;
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
    public class RenderFrameTelemetryTests
    {
        [Fact]
        public async Task RenderFrame_ReportsInvalidationAndFrameTelemetry()
        {
            const string htmlSource = "<!doctype html><html><body style=\"margin:0\"><div style=\"width:96px;height:48px;background:#00ff00\">frame</div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);

            var result = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Style,
                RequestedBy = "RenderFrameTelemetryTests.InitialCommit"
            });

            Assert.NotNull(result);
            Assert.Equal(RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Style, result.InvalidationReason);
            Assert.Equal("RenderFrameTelemetryTests.InitialCommit", result.RequestedBy);
            Assert.NotNull(result.Telemetry);
            Assert.True(result.Telemetry.FrameSequence > 0);
            Assert.True(result.Telemetry.LayoutUpdated);
            Assert.True(result.Telemetry.PaintTreeRebuilt);
            Assert.True(result.Telemetry.DomNodeCount >= 3);
            Assert.True(result.Telemetry.BoxCount > 0);
            Assert.True(result.Telemetry.PaintNodeCount > 0);
            Assert.Equal(RenderFrameRasterMode.Full, result.RasterMode);
        }

        [Fact]
        public async Task RenderFrame_PreservesSeededBaseFrame_WhenSteadyStateHasNoDamage()
        {
            const string htmlSource = "<!doctype html><html><body style=\"margin:0\"><div style=\"width:128px;height:128px;background:#00ff00\"></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);

            var renderer = new SkiaDomRenderer();
            using var firstBitmap = new SKBitmap(128, 128);
            using var firstCanvas = new SKCanvas(firstBitmap);

            var firstResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = firstCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "RenderFrameTelemetryTests.First"
            });
            firstCanvas.Flush();

            using var secondBitmap = new SKBitmap(128, 128);
            using var secondCanvas = new SKCanvas(secondBitmap);
            secondCanvas.DrawBitmap(firstBitmap, 0, 0);

            var secondResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                HasBaseFrame = true,
                InvalidationReason = RenderFrameInvalidationReason.Timer,
                RequestedBy = "RenderFrameTelemetryTests.SteadyState",
                EmitVerificationReport = false
            });
            secondCanvas.Flush();

            Assert.NotNull(firstResult);
            Assert.NotNull(secondResult);
            Assert.Equal(RenderFrameRasterMode.PreservedBaseFrame, secondResult.RasterMode);
            Assert.False(secondResult.UsedDamageRasterization);
            Assert.Equal(SKColors.Lime, secondBitmap.GetPixel(64, 64));
        }

        [Fact]
        public async Task RenderFrame_PaintOnlyInvalidation_DoesNotForceLayout()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='box' style='width:96px;height:48px;background:#00ff00'>frame</div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);

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
                RequestedBy = "RenderFrameTelemetryTests.PaintOnly.First"
            });
            firstCanvas.Flush();

            html.MarkDirty(InvalidationKind.Paint);

            using var secondBitmap = new SKBitmap(128, 128);
            using var secondCanvas = new SKCanvas(secondBitmap);
            var animationResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Animation,
                RequestedBy = "RenderFrameTelemetryTests.PaintOnly.Second",
                EmitVerificationReport = false
            });
            secondCanvas.Flush();

            Assert.NotNull(animationResult);
            Assert.NotNull(animationResult.Telemetry);
            Assert.False(animationResult.Telemetry.LayoutUpdated);
            Assert.True(animationResult.Telemetry.PaintTreeRebuilt);
        }

        [Fact]
        public async Task RenderFrame_StyleOnlyVisualChange_DoesNotPreserveStaleBaseFrame()
        {
            const string htmlSource = "<!doctype html><html><body style='margin:0'><div id='box' style='width:128px;height:128px;background:#00ff00'></div></body></html>";
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var box = html.QuerySelector("#box");
            var styles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);

            var renderer = new SkiaDomRenderer();
            using var firstBitmap = new SKBitmap(128, 128);
            using var firstCanvas = new SKCanvas(firstBitmap);
            var firstResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = firstCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = "RenderFrameTelemetryTests.StyleChange.First",
                EmitVerificationReport = false
            });
            firstCanvas.Flush();

            Assert.NotNull(firstResult);
            Assert.Equal(SKColors.Lime, firstBitmap.GetPixel(64, 64));

            box.SetAttribute("style", "width:128px;height:128px;background:#0f172a");
            var updatedStyles = await CssLoader.ComputeAsync(html, baseUri, null, viewportWidth: 128, viewportHeight: 128);

            using var secondBitmap = new SKBitmap(128, 128);
            using var secondCanvas = new SKCanvas(secondBitmap);
            secondCanvas.DrawBitmap(firstBitmap, 0, 0);

            var secondResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = html,
                Canvas = secondCanvas,
                Styles = updatedStyles,
                Viewport = new SKRect(0, 0, 128, 128),
                BaseUrl = baseUri.AbsoluteUri,
                HasBaseFrame = true,
                InvalidationReason = RenderFrameInvalidationReason.Style,
                RequestedBy = "RenderFrameTelemetryTests.StyleChange.Second",
                EmitVerificationReport = false
            });
            secondCanvas.Flush();

            Assert.NotNull(secondResult);
            Assert.NotEqual(RenderFrameRasterMode.PreservedBaseFrame, secondResult.RasterMode);
            Assert.Equal(new SKColor(15, 23, 42), secondBitmap.GetPixel(64, 64));
        }

        [Fact]
        public void CssAnimationEngine_ClassifiesOpacityAsPaintOnlyInvalidation()
        {
            Assert.Equal(InvalidationKind.Paint, CssAnimationEngine.ClassifyPropertyInvalidation("opacity"));
            Assert.Equal(InvalidationKind.Paint, CssAnimationEngine.DetermineInvalidationKind(new[] { "opacity", "transform" }));
            Assert.Equal(
                InvalidationKind.Layout | InvalidationKind.Paint,
                CssAnimationEngine.DetermineInvalidationKind(new[] { "opacity", "width" }));
        }
    }
}
