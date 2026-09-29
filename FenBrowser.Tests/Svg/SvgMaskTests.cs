using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgMaskTests
    {
        [Fact]
        public void ObjectBoundingBoxLuminanceMask_ClipsTargetByWhiteContent()
        {
            const string svg =
                "<svg width='80' height='40'><defs><mask id='m' maskContentUnits='objectBoundingBox'>" +
                "<rect x='0' y='0' width='.5' height='1' fill='white'/></mask></defs>" +
                "<rect x='10' y='5' width='60' height='30' fill='red' mask='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(20, 20).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(55, 20).Alpha);
        }

        [Fact]
        public void AlphaMask_UsesAlphaInsteadOfBlackLuminance()
        {
            const string svg =
                "<svg width='50' height='30'><defs><mask id='m' mask-type='alpha' " +
                "maskContentUnits='objectBoundingBox'><rect width='1' height='1' fill='black' opacity='.5'/></mask></defs>" +
                "<rect x='5' y='5' width='40' height='20' fill='blue' mask='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.InRange(result.Bitmap.GetPixel(20, 15).Alpha, 110, 140);
        }

        [Fact]
        public void UserSpaceMaskRegion_IsBoundedAndApplied()
        {
            const string svg =
                "<svg width='60' height='30'><defs><mask id='m' maskUnits='userSpaceOnUse' " +
                "x='0' y='0' width='30' height='30'><rect width='60' height='30' fill='white'/></mask></defs>" +
                "<rect width='60' height='30' mask='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 15).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(45, 15).Alpha);
        }

        [Fact]
        public void UserSpaceMaskOnGroup_DoesNotRequireObjectBounds()
        {
            const string svg =
                "<svg width='98' height='36'><defs><mask id='m' maskUnits='userSpaceOnUse' " +
                "x='0' y='0' width='98' height='36'><rect width='98' height='36' fill='white'/></mask></defs>" +
                "<g mask='url(#m)'><path d='M0 0h98v36H0z' fill='red'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(50, 18).Alpha > 0);
        }

        [Theory]
        [InlineData("url(#missing)", "")]
        [InlineData("url(#notamask)", "<rect id='notamask' width='20' height='20' fill='black'/>")]
        [InlineData("url(#notagroup)", "<g id='notagroup'><rect width='20' height='20' fill='black'/></g>")]
        public void UnresolvedLocalMaskReference_PaintsTheTargetUnmasked(string mask, string defs)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='20' height='20'><defs>{defs}</defs>" +
                $"<rect width='20' height='20' fill='red' mask='{mask}'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection);
            Assert.Equal(255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void ExternalMaskReference_NeverSilentlyCountsAsSupported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'>" +
                "<rect width='20' height='20' mask='url(https://example.test/mask.svg#m)'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback || result.HadResourceRejection);
        }

        [Fact]
        public void MaskOnUnsupportedObjectBounds_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='30'><defs><mask id='m'><rect width='1' height='1' fill='white'/></mask></defs>" +
                "<text x='2' y='18' font-size='12' mask='url(#m)'>masked</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("object bounds"));
        }

        [Fact]
        public void MaskReferenceCycle_FailsClosed()
        {
            const string svg =
                "<svg width='30' height='30'><defs><mask id='m'>" +
                "<rect width='20' height='20' mask='url(#m)'/></mask></defs>" +
                "<rect width='30' height='30' mask='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("reference-depth budget"));
        }

        [Fact]
        public void MaskLayers_ObeyNativeLayerDepthBudget()
        {
            const string svg =
                "<svg width='30' height='30'><defs><mask id='m' mask-type='alpha' " +
                "maskContentUnits='objectBoundingBox'><rect width='1' height='1'/></mask></defs>" +
                "<rect width='20' height='20' mask='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg,
                new SvgRenderLimits { MaxActiveLayers = 1 });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("layer budget"));
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
