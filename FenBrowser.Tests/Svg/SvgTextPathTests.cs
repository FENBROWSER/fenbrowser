using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgTextPathTests
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
        public void TextPath_PathLengthCalibratesNumericAndPercentageOffsets()
        {
            const string actual =
                "<svg width='300' height='120' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280' pathLength='2'/></defs>" +
                "<text><textPath href='#p' startOffset='1'>calibrated text</textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='50%'>calibrated text</textPath></text></g></svg>";
            const string reference =
                "<svg width='300' height='120' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280'/></defs>" +
                "<text><textPath href='#p' startOffset='130'>calibrated text</textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='130'>calibrated text</textPath></text></g></svg>";
            AssertIdenticalRender(actual, reference);
        }

        [Fact]
        public void TextPath_ZeroPathLengthKeepsZeroOffsetAndSuppressesNonZeroOffset()
        {
            const string actual =
                "<svg width='300' height='100' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280' pathLength='0'/></defs>" +
                "<text><textPath href='#p' startOffset='0'>visible</textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='1'>hidden</textPath></text></g></svg>";
            const string reference =
                "<svg width='300' height='100' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280'/></defs>" +
                "<text><textPath href='#p'>visible</textPath></text></svg>";
            AssertIdenticalRender(actual, reference);
        }

        [Fact]
        public void TextPath_DisplayNoneSuppressesOnlyItsTextContent()
        {
            const string hidden =
                "<svg width='120' height='40'><defs><path id='p' d='M5 30H115'/></defs>" +
                "<text font-size='20'><textPath href='#p' display='none'>hidden</textPath></text></svg>";
            const string empty =
                "<svg width='120' height='40'><defs><path id='p' d='M5 30H115'/></defs>" +
                "<text font-size='20'></text></svg>";

            AssertIdenticalRender(hidden, empty);
        }

        [Fact]
        public void TextPath_ExternalReferenceFailsClosedAsRejectedResource()
        {
            const string svg =
                "<svg width='100' height='30'><text><textPath href='https://example.invalid/p.svg#p'>x</textPath></text></svg>";
            using var result = new FenSvgRenderer().Render(svg);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
            Assert.False(SvgRenderResult.IsAdmissible(result));
        }

        [Fact]
        public async Task TextPath_RenderingIsDeterministicAcrossConcurrentDocuments()
        {
            const string svg =
                "<svg width='200' height='50'><defs><path id='p' d='M5 35 H195'/></defs>" +
                "<text font-size='20'><textPath href='#p'>concurrent text</textPath></text></svg>";
            ulong[] checksums = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                using var result = new FenSvgRenderer().Render(svg);
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback);
                ulong hash = 1469598103934665603UL;
                for (int y = 0; y < result.Bitmap.Height; y++)
                for (int x = 0; x < result.Bitmap.Width; x++)
                {
                    SKColor color = result.Bitmap.GetPixel(x, y);
                    hash ^= (uint)(color.Alpha << 24 | color.Red << 16 | color.Green << 8 | color.Blue);
                    hash *= 1099511628211UL;
                }
                return hash;
            })));
            Assert.All(checksums, checksum => Assert.Equal(checksums[0], checksum));
        }
    }
}
