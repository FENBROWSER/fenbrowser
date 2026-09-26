using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgDiagnosticReasonTests
    {
        [Fact]
        public void UnsupportedFeatures_ExposeStableReasonCodesAndFailClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><text textLength='10' lengthAdjust='squeeze'>x</text>" +
                "<animate attributeName='opacity' dur='1s'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("advanced-text-layout", result.FallbackReasonCodes);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void ResourceIsolation_ExposesStableReasonCodeAndFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><style>@import url('https://example.test/a.css');</style>" +
                "<rect width='20' height='20'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void FallbackRequirement_IsTerminalAndCarriesNoSecondOpinion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><animate attributeName='opacity' dur='1s'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.False(string.IsNullOrWhiteSpace(
                SvgRenderResult.DescribeRejection(result)));
        }

        [Fact]
        public void WarningMessages_AreBoundedBeforeLeavingRenderer()
        {
            string value = new string('x', 1024);
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20'><rect width='20' height='20' style='mix-blend-mode:{value}'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.All(result.Warnings, warning => Assert.True(warning.Length <= 256));
            Assert.InRange(result.ErrorMessage.Length, 1, 200);
        }

        [Fact]
        public void W3cSvgTestSuiteMetadata_DoesNotRequireFallback()
        {
            const string metadata =
                "<d:SVGTestCase xmlns:d='http://www.w3.org/2000/02/svg/testsuite/description/'>" +
                "<d:testDescription><p>not rendered</p></d:testDescription>" +
                "</d:SVGTestCase>";

            using var result = new FenSvgRenderer().Render(
                $"<svg width='10' height='10'>{metadata}<rect width='10' height='10' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void LookalikePrefixedMetadata_StillRequiresFallbackAndFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'>" +
                "<d:SVGTestCase xmlns:d='https://example.test/not-w3c'/>" +
                "<rect width='10' height='10' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("script")]
        [InlineData("SCRIPT")]
        [InlineData("h:script")]
        [InlineData("html:script")]
        public void SvgImageScripts_FailClosedAsDynamicContent(string elementName)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='10' height='10' xmlns:h='http://www.w3.org/1999/xhtml' " +
                $"xmlns:html='http://www.w3.org/1999/xhtml'>" +
                $"<{elementName}>if (1 &lt; 2) window.test = '&lt;rect/&gt;';</{elementName}>" +
                "<rect width='10' height='10' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            Assert.Null(result.Bitmap);
        }

        private const string XhtmlDeclarations =
            "xmlns='http://www.w3.org/2000/svg' xmlns:h='http://www.w3.org/1999/xhtml'";

        [Fact]
        public void WptViewElement_IsANonRenderingStructuralContainer()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' viewBox='0 0 20 20'>" +
                "<view id='transform' viewBox='-20 -20 40 40'/>" +
                "<view id='invalid' viewBox='0 0 -20 40'/>" +
                "<view id='populated'><rect width='20' height='20' fill='green'/></view>" +
                "</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void WptSvgFontDescriptionElements_AreReferencedOnlyContent()
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20' {XhtmlDeclarations}>" +
                "<font horiz-adv-x='1000'><font-face font-family='TestFont'/>" +
                "<glyph unicode='a' horiz-adv-x='100' d='M0,0 h80 v1000 h-80 z'/>" +
                "<missing-glyph horiz-adv-x='1' d='M0,0 h10 v10 h-10 z'/>" +
                "<hkern g1='a' g2='b' k='0'/><vkern g1='a' g2='b' k='0'/>" +
                "<altGlyphDef id='agd'><altGlyphItem><glyphRef href='#missing'/>" +
                "</altGlyphItem></altGlyphDef></font>" +
                "<font-face font-family='External' units-per-em='2048'>" +
                "<font-face-src><font-face-name='External'/>" +
                "<font-face-format name='svg'/></font-face-src></font-face>" +
                "<cursor id='c' xlink:href='#c'/>" +
                "<color-profile id='p' name='sRGB' xlink:href='#p'/>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void WptMeshGradientElements_AreReferencedOnlyContent()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'>" +
                "<mesh><meshgradient id='g'><meshrow><meshpatch>" +
                "<stop path='c 10,10 0,0 1,0' stop-color='green'/>" +
                "</meshpatch></meshrow></meshgradient></mesh>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void WptXhtmlInteractivityElementsOutsideForeignObject_AreInert()
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20' {XhtmlDeclarations}>" +
                "<h:base href='http://www.example.com/'/>" +
                "<h:iframe src='resources/blank.htm'></h:iframe>" +
                "<h:audio controls='controls'></h:audio>" +
                "<h:video controls='controls'></h:video>" +
                "<h:canvas tabindex='0'></h:canvas>" +
                "<h:div><rect width='20' height='20' fill='blue'/></h:div>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void WptXhtmlDisplayContents_DoesNotReenterTheSvgWalk()
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='400' height='400' viewBox='0 0 400 400' {XhtmlDeclarations}>" +
                "<rect x='0' y='0' width='100' height='100' fill='green'/>" +
                "<h:div style='display: contents'>" +
                "<svg width='300' height='300'>" +
                "<rect x='5' y='5' width='100' height='100' fill='red'/></svg>" +
                "</h:div></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(result.Bitmap.GetPixel(50, 50).Green > 100);
            Assert.True(result.Bitmap.GetPixel(50, 50).Red < 60);
            Assert.Equal((byte)0, result.Bitmap.GetPixel(200, 200).Alpha);
        }

        [Fact]
        public void WptForeignObjectContent_StillRequiresFallbackAndFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20' {XhtmlDeclarations}>" +
                "<foreignObject width='20' height='20'>" +
                "<h:div style='display: contents'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "</h:div></foreignObject>" +
                "<rect width='20' height='20' fill='red'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        [Fact]
        public void WptUnrecognizedForeignNamespace_StillRequiresFallbackAndFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:bd='http://example.org/ExampleBusinessData'>" +
                "<g><bd:Results id='results'><bd:RegionName>East</bd:RegionName>" +
                "</bd:Results></g>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        [Fact]
        public void WptReboundDefaultNamespaceElement_StillRequiresFallbackAndFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:s='http://www.w3.org/2000/svg'>" +
                "<s:g xmlns='http://www.example.org/notsvg'>" +
                "<circle cx='10' cy='10' r='5' fill='blue'/></s:g>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
