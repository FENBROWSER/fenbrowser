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
                "<svg width='20' height='20'><text textLength='10'>x</text>" +
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
        [InlineData("h:script")]
        [InlineData("html:script")]
        public void SvgImageScripts_AreInertNonRenderingContent(string elementName)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='10' height='10' xmlns:h='http://www.w3.org/1999/xhtml' " +
                $"xmlns:html='http://www.w3.org/1999/xhtml'>" +
                $"<{elementName}>if (1 &lt; 2) window.test = '&lt;rect/&gt;';</{elementName}>" +
                "<rect width='10' height='10' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 5).Alpha);
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
