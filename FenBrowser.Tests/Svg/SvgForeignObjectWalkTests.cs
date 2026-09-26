using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// The foreignObject decision in the draw walk. A foreignObject establishes a
    /// viewport from its x/y/width/height geometry; the walk certifies the one case
    /// it can prove paints nothing - a viewport with no area - and refuses every
    /// viewport with area, because a foreignObject's content is laid out as a
    /// foreign namespace and this engine has no box layout for it.
    ///
    /// These tests also pin the coupling to the parse-time gate. The walk only
    /// governs the foreignObjects it is reached for, and several skip paths can pass
    /// a paintable one by, so a non-rendering foreignObject is still condemned at
    /// parse time. The gate must not be removed on the strength of the walk alone.
    /// </summary>
    public sealed class SvgForeignObjectWalkTests
    {
        private const string XhtmlDeclarations =
            "xmlns='http://www.w3.org/2000/svg' xmlns:h='http://www.w3.org/1999/xhtml'";

        private const string ParseGateReason =
            "SVG feature 'foreignObject' requires compatibility fallback";
        private const string UnmodelledReason =
            "SVG foreignObject content requires XHTML box layout";

        private static WalkRender Walk(string source)
        {
            bool rendered = SvgRenderEngine.TryRender(
                source, SvgRenderLimits.Default, null, null, 0,
                out var picture, out float width, out float height, out string error,
                out IReadOnlyList<string> warnings, out IReadOnlyList<string> fallbackReasonCodes,
                out _, out _, out _);

            Assert.True(rendered, error);
            Assert.NotNull(picture);
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(width)),
                Math.Max(1, (int)Math.Round(height)));
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawPicture(picture);
            picture.Dispose();
            return new WalkRender(
                surface.Snapshot(), warnings.ToList(), fallbackReasonCodes.ToList());
        }

        [Fact]
        public void ForeignObject_EmptyViewportIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject></foreignObject></svg>");

            AssertInvisible(rendered);
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_OmittedExtentIsZeroSoTheViewportIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='200' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject><rect width='400' height='400' fill='green'/></foreignObject></svg>");

            AssertInvisible(rendered);
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_AutoExtentIsZeroSoTheViewportIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='200' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='auto' height='200'>" +
                "<rect width='400' height='400' fill='green'/></foreignObject>" +
                "<foreignObject style='height: auto' width='150'>" +
                "<rect width='400' height='400' fill='green'/></foreignObject>" +
                "</svg>");

            AssertInvisible(rendered);
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_NegativeExtentIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='-30' height='40'>" +
                "<rect width='30' height='40' fill='green'/></foreignObject>" +
                "<foreignObject width='40' height='-30'>" +
                "<rect width='40' height='30' fill='green'/></foreignObject></svg>");

            AssertInvisible(rendered);
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_ZeroPercentageExtentIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='200' height='200' viewBox='0 0 200 200' " + XhtmlDeclarations + ">" +
                "<foreignObject x='25%' y='10%' width='0%' height='50%'>" +
                "<rect width='100' height='50' fill='green'/></foreignObject>" +
                "<foreignObject x='25%' y='60%' width='50%' height='0%'>" +
                "<rect width='100' height='50' fill='green'/></foreignObject></svg>");

            AssertInvisible(rendered);
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_EmptySubtreeWithAVisibleViewportIsRefused()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject x='50' y='60' width='70' height='80'></foreignObject></svg>");

            AssertInvisible(rendered);
            Assert.Contains(ParseGateReason, rendered.Warnings);
            Assert.Contains(UnmodelledReason, rendered.Warnings);
        }

        [Fact]
        public void ForeignObject_SelfClosedElementWithAVisibleViewportIsRefused()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject id='target' width='30' height='40'/></svg>");

            AssertInvisible(rendered);
            Assert.Contains(UnmodelledReason, rendered.Warnings);
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithShapesIsRefusedAndPaintsNothing()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject x='10' y='20' width='30' height='40'>" +
                "<rect width='30' height='40' fill='green'/></foreignObject>" +
                "<rect x='60' y='60' width='20' height='20' fill='red'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 30).Alpha);
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(12, 22).Alpha);
            Assert.True(IsRed(rendered.Bitmap.GetPixel(70, 70)), "siblings still paint");
            Assert.Contains(UnmodelledReason, rendered.Warnings);
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithCssGeometryIsRefused()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<style>.c { x: 10px; y: 20px; width: 30px; height: 40px; }</style>" +
                "<foreignObject class='c'><rect width='30' height='40' fill='green'/></foreignObject></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 30).Alpha);
            Assert.Contains(UnmodelledReason, rendered.Warnings);
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithANestedSvgIsRefused()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='100px' height='100px'>" +
                "<svg width='100px' height='100px'>" +
                "<circle cx='50' cy='0' r='44' fill='blue'/></svg></foreignObject></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 40).Alpha);
            Assert.Contains(UnmodelledReason, rendered.Warnings);
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithXhtmlContentNamesTheBoxLayoutGap()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='40' height='40'>" +
                "<h:div style='width: 40px; height: 40px; background-color: green'/>" +
                "</foreignObject>" +
                "<rect x='60' y='60' width='20' height='20' fill='red'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 20).Alpha);
            Assert.True(IsRed(rendered.Bitmap.GetPixel(70, 70)));
            Assert.Contains(ParseGateReason, rendered.Warnings);
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject content 'h:div' needs XHTML box layout requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithUnknownSvgContentNamesTheElement()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='40' height='40'><marquee/></foreignObject></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 20).Alpha);
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject content 'marquee' is not renderable here requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ForeignObject_VisibleViewportWithTheAnimationElementIsRefused()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='40' height='40'><animation/></foreignObject></svg>");

            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject content 'animation' is not renderable here requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ForeignObject_UnresolvableExtentIsRefusedRatherThanTreatedAsZero()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='min-content' height='40'>" +
                "<rect width='40' height='40' fill='green'/></foreignObject></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 20).Alpha);
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject geometry property 'width: min-content' requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ForeignObject_DisplayNoneIsSkippedBeforeTheViewportDecision()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='20' height='20' style='display: none'>" +
                "<rect width='20' height='20' fill='green'/></foreignObject>" +
                "<rect x='50' y='50' width='20' height='20' fill='red'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(IsRed(rendered.Bitmap.GetPixel(60, 60)));
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_RequiredExtensionsFalseBranchNeverReachesTheViewportDecision()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<foreignObject width='100' height='100' " +
                "requiredExtensions='http://www.w3.org/1999/xhtml'>" +
                "<h:body><h:div/></h:body></foreignObject>" +
                "<rect width='100' height='100' fill='red'/></switch></svg>");

            Assert.True(IsRed(rendered.Bitmap.GetPixel(50, 50)));
            Assert.Contains(ParseGateReason, rendered.Warnings);
            Assert.DoesNotContain(UnmodelledReason, rendered.Warnings);
            Assert.Contains("unsupported-element", rendered.FallbackReasonCodes);
        }

        [Fact]
        public void ForeignObject_ConditionalProcessingSkipsTheWholeSubtree()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='40' height='40' requiredExtensions='http://www.w3.org/1999/xhtml'>" +
                "<rect width='40' height='40' fill='green'/></foreignObject>" +
                "<rect x='60' y='60' width='20' height='20' fill='red'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(20, 20).Alpha);
            Assert.True(IsRed(rendered.Bitmap.GetPixel(70, 70)));
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_InsideNonRenderingContainersIsStillCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><foreignObject><svg><text id='hello'>Hello</text></svg></foreignObject></defs>" +
                "<symbol><foreignObject width='40' height='40'>x</foreignObject></symbol>" +
                "<rect width='100' height='100' fill='green'/></svg>");

            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 50)));
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_BoundedSubtreeInspectionStaysWithinTheRenderBudget()
        {
            string body = string.Concat(Enumerable.Repeat("<g/>", 5000));
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject width='40' height='40'>" + body + "</foreignObject></svg>");

            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject content exceeding the first-party render budget requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void Use_TargetingATextContentElementPaintsNothingAndStaysAdmissible()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><text><tspan id='t' x='20' y='60' fill='red' font-size='36'>no</tspan>" +
                "</text></defs>" +
                "<rect width='100' height='100' fill='green'/>" +
                "<use href='#t'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(IsGreen(result.Bitmap.GetPixel(50, 50)));
            Assert.False(IsRed(result.Bitmap.GetPixel(20, 30)), "the tspan was not instantiated");
            Assert.False(IsRed(result.Bitmap.GetPixel(30, 50)));
        }

        [Fact]
        public void Use_TargetingATextPathPaintsNothingAndStaysAdmissible()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><path id='line' d='M4 60 H96'/></defs>" +
                "<text><textPath id='p' href='#line'>x</textPath></text>" +
                "<rect width='100' height='100' fill='green'/>" +
                "<use href='#p'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.True(IsGreen(result.Bitmap.GetPixel(50, 50)));
        }

        [Fact]
        public void Use_TargetingAShapeStillDrawsIt()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><rect id='r' width='20' height='20' fill='red'/></defs>" +
                "<use href='#r' x='30' y='40'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.True(IsRed(result.Bitmap.GetPixel(40, 50)));
        }

        [Fact]
        public void Use_TargetingANestedTspanPaintsNothing()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><text x='0' y='0'><tspan><tspan id='inner' x='20' y='60'>no</tspan></tspan>" +
                "</text></defs>" +
                "<rect width='100' height='100' fill='green'/>" +
                "<use href='#inner'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.True(IsGreen(result.Bitmap.GetPixel(50, 50)));
        }

        [Fact]
        public void ForeignObject_TextContentWithNoAreaOnEitherAxisIsCertifiedInvisible()
        {
            using var rendered = Walk(
                "<svg width='200' height='100' " + XhtmlDeclarations + ">" +
                "<foreignObject>Some content</foreignObject>" +
                "<foreignObject height='200'>Some content</foreignObject>" +
                "<foreignObject style='height: 75px'>Some content</foreignObject>" +
                "<foreignObject style='height: 50%'>Some content</foreignObject>" +
                "<foreignObject style='height: auto; display: none'>Some content</foreignObject>" +
                "<rect width='100' height='100' fill='red'/></svg>");

            Assert.True(IsRed(rendered.Bitmap.GetPixel(50, 50)), "siblings still paint");
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void ForeignObject_AProvablyNonRenderingSubtreeIsStillCondemnedByTheParseGate()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<defs><foreignObject><svg><text id='hello'>Hello</text></svg></foreignObject></defs>" +
                "<rect width='100' height='100' fill='green'/></svg>");

            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 50)));
            AssertParseGateOnly(rendered);
        }

        [Fact]
        public void Switch_DoesNotOfferForeignObjectAsASelectableBranch()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<foreignObject width='100' height='100'>" +
                "<h:body><h:div style='width: 100px; height: 100px; background-color: green'/></h:body>" +
                "</foreignObject>" +
                "<rect width='100' height='100' fill='red'/></switch></svg>");

            Assert.True(IsRed(rendered.Bitmap.GetPixel(50, 50)), "the false branch is selected");
            AssertParseGateOnly(rendered);
        }

        private static void AssertInvisible(WalkRender rendered)
        {
            using var bitmap = rendered.Bitmap;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                Assert.Equal((byte)0, bitmap.GetPixel(x, y).Alpha);
        }

        private static void AssertParseGateOnly(WalkRender rendered)
        {
            Assert.Equal(new[] { "unsupported-element" }, rendered.FallbackReasonCodes);
            Assert.Contains(ParseGateReason, rendered.Warnings);
            Assert.DoesNotContain(UnmodelledReason, rendered.Warnings);
            Assert.DoesNotContain(
                rendered.Warnings,
                warning => warning.Contains("SVG foreignObject content", StringComparison.Ordinal) ||
                           warning.Contains("SVG foreignObject geometry", StringComparison.Ordinal));
        }

        private static bool IsGreen(SKColor pixel) =>
            pixel.Alpha > 200 && pixel.Green > 100 && pixel.Red < 60 && pixel.Blue < 60;

        private static bool IsRed(SKColor pixel) =>
            pixel.Alpha > 200 && pixel.Red > 100 && pixel.Green < 60 && pixel.Blue < 60;

        private sealed class WalkRender : IDisposable
        {
            private readonly SKImage _image;

            public WalkRender(SKImage image, List<string> warnings, List<string> fallbackReasonCodes)
            {
                _image = image;
                Warnings = warnings;
                FallbackReasonCodes = fallbackReasonCodes;
            }

            public IReadOnlyList<string> Warnings { get; }

            public IReadOnlyList<string> FallbackReasonCodes { get; }

            public SKBitmap Bitmap =>
                SKBitmap.FromImage(_image) ??
                throw new InvalidOperationException("rasterization produced no bitmap");

            public void Dispose() => _image.Dispose();
        }
    }
}
