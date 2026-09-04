using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgDiagnosticReasonTests
    {
        [Fact]
        public void UnsupportedFeatures_ExposeStableReasonCodes()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><text textLength='10'>x</text>" +
                "<animate attributeName='opacity' dur='1s'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains("advanced-text-layout", result.FallbackReasonCodes);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void ResourceIsolation_ExposesStableReasonCode()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><style>@import url('https://example.test/a.css');</style>" +
                "<rect width='20' height='20'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void HybridFallback_PreservesFirstPartyReasonCodes()
        {
            var firstParty = new FenSvgRenderer();
            var legacy = new SvgSkiaRenderer();
            using var result = new HybridSvgRenderer(firstParty, legacy).Render(
                "<svg width='20' height='20'><animate attributeName='opacity' dur='1s'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.UsedLegacyFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void WarningMessages_AreBoundedBeforeLeavingRenderer()
        {
            string value = new string('x', 1024);
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20'><rect width='20' height='20' style='mix-blend-mode:{value}'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.All(result.Warnings, warning => Assert.True(warning.Length <= 256));
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
        public void LookalikePrefixedMetadata_StillRequiresFallback()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'>" +
                "<d:SVGTestCase xmlns:d='https://example.test/not-w3c'/>" +
                "<rect width='10' height='10' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }
    }
}
