using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Object bounding boxes of use instances, which size the default filter and
    /// mask regions. SVG 2 §5.6: x/y on a use are a translation appended to its
    /// transform, so the instance's own effects see them already applied.
    /// </summary>
    public sealed class SvgUseEffectBoundsTests
    {
        private const string Defs =
            "<defs><filter id='f'><feOffset dx='0' dy='0'/></filter>" +
            "<mask id='m'><rect x='-1000' y='-1000' width='3000' height='3000' fill='white'/></mask>" +
            "<rect id='r' width='10' height='10' fill='lime'/></defs>";

        [Theory]
        [InlineData("filter='url(#f)'")]
        [InlineData("mask='url(#m)'")]
        public void TranslatedUse_KeepsItsOwnEffectRegionOnTheInstance(string effect)
        {
            // The region used to be offset by x twice, so the crop removed the instance.
            using var result = Render($"<use href='#r' x='50' {effect}/>");

            Assert.Equal(new SKRectI(50, 0, 60, 10), Painted(result));
        }

        [Fact]
        public void TranslatedUseInsideAFilteredGroup_CountsTowardTheGroupBounds()
        {
            using var result = Render("<g filter='url(#f)'><use href='#r' x='50'/></g>");

            Assert.Equal(new SKRectI(50, 0, 60, 10), Painted(result));
        }

        internal static SvgRenderResult Render(string body, string defs = Defs)
        {
            var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='40'>" + defs + body + "</svg>");
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }

        internal static SKRectI Painted(SvgRenderResult result)
        {
            int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
            for (int y = 0; y < result.Bitmap.Height; y++)
            for (int x = 0; x < result.Bitmap.Width; x++)
            {
                if (result.Bitmap.GetPixel(x, y).Alpha == 0) continue;
                left = System.Math.Min(left, x);
                top = System.Math.Min(top, y);
                right = System.Math.Max(right, x + 1);
                bottom = System.Math.Max(bottom, y + 1);
            }
            return left == int.MaxValue ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
        }
    }
}
