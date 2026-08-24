using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgMotionAnimationTests
    {
        private static void AssertIdenticalRender(string actualSvg, string referenceSvg)
        {
            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);
            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void ConstantKeyPointMPath_RendersTimeZeroSnapshotWithoutFallback()
        {
            const string svg = "<svg width='200' height='100'>" +
                "<path id='p' d='M20 50h100'/><circle r='5'>" +
                "<animateMotion dur='10s' repeatCount='indefinite' keyPoints='0.5;0.5' keyTimes='0;1'>" +
                "<mpath href='#p'/></animateMotion></circle></svg>";

            const string reference = "<svg width='200' height='100'>" +
                "<path d='M20 50h100'/><circle r='5' transform='translate(70 50)'/></svg>";

            AssertIdenticalRender(svg, reference);
        }

        [Fact]
        public void ExternalMPath_IsRejected()
        {
            const string svg = "<svg width='20' height='20'><circle r='2'>" +
                "<animateMotion dur='1s'><mpath href='https://example.invalid/p.svg#p'/>" +
                "</animateMotion></circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.HadResourceRejection);
            Assert.False(result.UsedLegacyFallback);
        }

        [Fact]
        public void EventBasedBegin_RemainsExplicitFallback()
        {
            const string svg = "<svg width='20' height='20'><circle r='2'>" +
                "<animateMotion dur='1s' begin='click' path='M0 0L10 10'/>" +
                "</circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("keyPoints='.5;bad' keyTimes='0;1'")]
        [InlineData("keyPoints='.5;.5' keyTimes='0;.5;1'")]
        [InlineData("keyPoints='.5;.5'")]
        [InlineData("keyPoints='1.1;1.1' keyTimes='0;1'")]
        public void MalformedOrAmbiguousTimingLists_RemainExplicitFallback(string timing)
        {
            string svg = "<svg width='20' height='20'><circle r='2'>" +
                $"<animateMotion dur='1s' {timing} path='M0 0L10 10'/>" +
                "</circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void UnsupportedTarget_RemainsExplicitFallback()
        {
            const string svg = "<svg width='20' height='20'><g>" +
                "<animateMotion dur='1s' path='M0 0L10 10'/><circle r='2'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }
    }
}
