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
    /// Conditional processing inside the draw walk, and the switch branch list
    /// that consumes it. Two wrong frames motivated this: a switch skipped a
    /// foreignObject branch and painted the next sibling where a browser paints
    /// the branch it selected, and a requiredExtensions branch was dropped
    /// without a reason so the fallback branch was painted for a branch every
    /// browser selects.
    ///
    /// The rule the walk now applies is that a conditional processing list is
    /// answered only when the answer holds for every user agent. A list of
    /// identifiers the specification does not define matches no user agent, so
    /// it is rejected and the walk continues. A list of identifiers from the
    /// attribute's own namespace asks about this user agent, and this renderer
    /// cannot answer it - it declares no extension, declines a feature, and has
    /// no user language - so it is refused with the identifier that made the
    /// question unanswerable, and no later branch stands in for it.
    /// </summary>
    public sealed class SvgSwitchConditionalProcessingTests
    {
        private const string XhtmlDeclarations =
            "xmlns='http://www.w3.org/2000/svg' xmlns:h='http://www.w3.org/1999/xhtml'";

        private const string ExtensionReason =
            "SVG conditional processing attribute 'requiredExtensions' requires compatibility fallback " +
            "for unimplemented extension 'http://www.w3.org/1999/xhtml'";

        private const string FeatureReason =
            "SVG conditional processing attribute 'requiredFeatures' requires compatibility fallback " +
            "for unimplemented feature 'http://www.w3.org/TR/SVG11/feature#SVGDOM'";

        private const string LanguageReason =
            "SVG conditional processing attribute 'systemLanguage' requires compatibility fallback " +
            "for language 'af' this renderer has no user language for";

        private const string XhtmlReftest =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:h=\"http://www.w3.org/1999/xhtml\">\n" +
            "  <title>requiredExtensions: support for HTML/XHTML (non-normative)</title>\n" +
            "  <h:link rel=\"match\" href=\"reference/green-100x100.svg\"/>\n" +
            "  <switch>\n" +
            "    <foreignObject width=\"100\" height=\"100\" " +
            "requiredExtensions=\"http://www.w3.org/1999/xhtml\">\n" +
            "      <body xmlns=\"http://www.w3.org/1999/xhtml\" style=\"margin: 0\">\n" +
            "        <div style=\"width: 100px; height: 100px; background-color: green\"></div>\n" +
            "      </body>\n" +
            "    </foreignObject>\n" +
            "    <rect width=\"100\" height=\"100\" fill=\"red\"/>\n" +
            "  </switch>\n" +
            "</svg>\n";

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
        public void Switch_ForeignObjectBranchWithNoConditionalAttributeIsNotSkippedForTheSibling()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<foreignObject width='100' height='100'>" +
                "<h:body><h:div style='width: 100px; height: 100px; background-color: green'/></h:body>" +
                "</foreignObject>" +
                "<rect width='100' height='100' fill='red'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 50).Alpha);
            Assert.False(IsRed(rendered.Bitmap.GetPixel(50, 50)));
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "SVG foreignObject content 'h:body' needs XHTML box layout requires compatibility fallback",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void Switch_RequiredExtensionsThisRendererCannotAnswerFailsClosedAndNamesTheExtension()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<foreignObject width='100' height='100' " +
                "requiredExtensions='http://www.w3.org/1999/xhtml'>" +
                "<h:body><h:div style='width: 100px; height: 100px; background-color: green'/></h:body>" +
                "</foreignObject>" +
                "<rect width='100' height='100' fill='red'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 50).Alpha);
            Assert.Contains(ExtensionReason, rendered.Warnings);
            Assert.Contains("unsupported-property", rendered.FallbackReasonCodes);
        }

        [Fact]
        public void Switch_RequiredExtensionsXhtmlReftestFailsClosedInsteadOfPaintingTheFallback()
        {
            using var rendered = Walk(XhtmlReftest);

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 50).Alpha);
            Assert.False(IsRed(rendered.Bitmap.GetPixel(50, 50)));
            Assert.Contains(ExtensionReason, rendered.Warnings);
        }

        [Fact]
        public void RequiredExtensions_AnIdentifierOutsideTheDefinedSetIsRejectedAndTheFallbackPaints()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' requiredExtensions='http://example.org/bogus'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Empty(rendered.Warnings);
            Assert.Empty(rendered.FallbackReasonCodes);
        }

        [Fact]
        public void RequiredExtensions_ARecognizedIdentifierAmongUnrecognizedOnesStillRejectsTheBranch()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' " +
                "requiredExtensions='http://example.org/bogus http://www.w3.org/1999/xhtml'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void RequiredExtensions_TheXlinkExtensionThisRendererDoesNotImplementIsAlsoUnanswerable()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='100' fill='red' " +
                "requiredExtensions='http://www.w3.org/1999/xlink'/>" +
                "</switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 50).Alpha);
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains(
                    "for unimplemented extension 'http://www.w3.org/1999/xlink'",
                    StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void RequiredExtensions_AnEmptyListIsFalseForEveryUserAgentSoTheElementIsSkipped(string value)
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<rect width='100' height='50' fill='red' requiredExtensions='" + value + "'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void RequiredFeatures_ASupportedFeatureStillSelectsTheBranch()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='green' " +
                "requiredFeatures='http://www.w3.org/TR/SVG11/feature#Shape'/>" +
                "<rect y='50' width='100' height='50' fill='red'/></switch></svg>");

            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 25)));
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 75).Alpha);
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void RequiredFeatures_ARealFeatureTheRendererDeclinesIsUnanswerable()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' " +
                "requiredFeatures='http://www.w3.org/TR/SVG11/feature#SVGDOM'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 75).Alpha);
            Assert.Contains(FeatureReason, rendered.Warnings);
        }

        [Fact]
        public void RequiredFeatures_ASupportedFeatureBesideAnUnrecognizedOneRejectsTheWholeList()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<rect width='100' height='50' fill='red' " +
                "requiredFeatures='http://www.w3.org/TR/SVG11/feature#Shape this.is.a.bogus.feature.string'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void SystemLanguage_AWellFormedTagIsUnanswerableBecauseTheRendererHasNoUserLanguage()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' systemLanguage='af'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 75).Alpha);
            Assert.Contains(LanguageReason, rendered.Warnings);
        }

        [Fact]
        public void SystemLanguage_AMalformedTagMatchesNoUserLanguageSoTheFallbackPaints()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' systemLanguage='INVALID_LANGUAGE_STRING'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void SystemLanguage_AnEmptyListIsFalseForEveryUserAgentSoTheBranchIsSkipped()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' systemLanguage=''/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.True(IsGreen(rendered.Bitmap.GetPixel(50, 75)));
            Assert.Empty(rendered.Warnings);
        }

        [Fact]
        public void SystemLanguage_AMalformedTagBesideAWellFormedOneIsStillUnanswerable()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<rect width='100' height='50' fill='red' systemLanguage='INVALID_LANGUAGE_STRING zh-CN'/>" +
                "<rect y='50' width='100' height='50' fill='green'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 75).Alpha);
            Assert.Contains(
                rendered.Warnings,
                warning => warning.Contains("for language 'zh-CN'", StringComparison.Ordinal));
        }

        [Fact]
        public void Switch_AnUnanswerableBranchIsNotPaintedAndNoSiblingIsPaintedInItsPlace()
        {
            using var rendered = Walk(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<title>skipped</title>" +
                "<rect width='100' height='50' fill='red' " +
                "requiredExtensions='http://www.w3.org/1999/xhtml'/>" +
                "<rect y='50' width='100' height='50' fill='lime'/>" +
                "<rect y='50' width='100' height='50' fill='blue'/></switch></svg>");

            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Equal((byte)0, rendered.Bitmap.GetPixel(50, 75).Alpha);
            Assert.Contains(ExtensionReason, rendered.Warnings);
        }

        [Fact]
        public void ConditionalProcessing_AnUnanswerableListOutsideASwitchIsRefusedToo()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<rect width='100' height='50' fill='red' " +
                "requiredExtensions='http://www.w3.org/1999/xhtml'/>" +
                "<rect y='50' width='100' height='50' fill='lime'/></svg>");

            Assert.False(result.Success);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains(ExtensionReason, result.Warnings);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Null(result.Bitmap);
            Assert.Equal(0f, result.Width);
        }

        [Fact]
        public void Switch_ForeignObjectBranchIsNeverReturnedAsAnAdmissibleFrame()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100' " + XhtmlDeclarations + ">" +
                "<switch>" +
                "<foreignObject width='100' height='100'>" +
                "<h:body><h:div style='width: 100px; height: 100px; background-color: green'/></h:body>" +
                "</foreignObject>" +
                "<rect width='100' height='100' fill='red'/></switch></svg>");

            Assert.False(result.Success);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Null(result.Bitmap);
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
