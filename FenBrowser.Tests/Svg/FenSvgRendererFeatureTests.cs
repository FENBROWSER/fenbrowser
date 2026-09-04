using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>Viewport math, gradients, use/symbol, and legacy parity cases.</summary>
    public class FenSvgRendererFeatureTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void NegativeViewBoxOrigin_RendersVisiblePixels_Parity()
        {
            // Mirrors the committed SvgSandboxingTests parity case.
            const string svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 -960 960 960\">" +
                "<path fill=\"#000000\" d=\"M440 -120v-123q-104-14-172-93t-68-184h80q0 83 58.5 141.5T480-320q83 0 141.5-58.5T680-520h80q0 105-68 184t-172 93v123h-80Z\"/>" +
                "</svg>";

            var result = _renderer.Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.True(HasVisiblePixels(result.Bitmap), "expected visible pixels");
            Assert.Equal(960f, result.Width);
            Assert.Equal(960f, result.Height);
        }

        [Fact]
        public void IntrinsicSize_DerivesFromViewBox()
        {
            // No width/height attributes: size comes from viewBox (legacy adapter parity).
            var result = _renderer.Render(
                "<svg viewBox=\"0 0 40 30\"><rect width=\"40\" height=\"30\" fill=\"red\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(40f, result.Width);
            Assert.Equal(30f, result.Height);
        }

        [Fact]
        public void LinearGradient_Orientation()
        {
            var result = _renderer.Render(
                "<svg width=\"20\" height=\"20\">" +
                "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" +
                "<stop offset=\"0\" stop-color=\"black\"/><stop offset=\"1\" stop-color=\"white\"/>" +
                "</linearGradient></defs>" +
                "<rect width=\"20\" height=\"20\" fill=\"url(#g)\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var left = result.Bitmap.GetPixel(1, 10);
            var right = result.Bitmap.GetPixel(18, 10);
            Assert.True(left.Red < 80, $"expected dark left, got {left}");
            Assert.True(right.Red > 170, $"expected light right, got {right}");
        }

        [Fact]
        public void MissingPaintServer_CurrentColorFallbackResolvesAtPaintedElement()
        {
            using var result = _renderer.Render(
                "<svg width='30' height='20'><g fill='url(#missing) currentColor' " +
                "stroke='url(#missing) currentColor' color='red'>" +
                "<rect width='10' height='20' color='lime' stroke='none'/>" +
                "<rect x='15' y='3' width='12' height='14' color='blue' fill='none' stroke-width='4'/>" +
                "</g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(16, 10));
        }

        [Fact]
        public void InheritedCurrentColorPaint_ResolvesAtPaintedElement()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><g fill='currentColor' color='red'>" +
                "<rect width='10' height='10' color='lime'/></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void Use_InstantiatesDefContent_WithOffset()
        {
            var result = _renderer.Render(
                "<svg width=\"20\" height=\"10\">" +
                "<defs><rect id=\"r\" width=\"4\" height=\"4\" fill=\"red\"/></defs>" +
                "<use href=\"#r\" x=\"2\" y=\"3\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(4, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(10, 5).Alpha);
        }

        [Fact]
        public void UseContextPaint_UsesInstanceFillAndStroke()
        {
            using var result = _renderer.Render(
                "<svg width='25' height='10'><defs><g id='s'>" +
                "<rect width='10' height='10' fill='context-fill'/>" +
                "<rect x='10' width='10' height='10' fill='context-stroke'/>" +
                "</g></defs><use href='#s' x='2' fill='red' stroke='blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void UseContextPaintServer_RemainsExplicitFallback()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><defs>" +
                "<linearGradient id='g'><stop stop-color='red'/></linearGradient>" +
                "<rect id='s' width='10' height='10' fill='context-fill'/>" +
                "</defs><use href='#s' fill='url(#g)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("context paint"));
        }

        [Fact]
        public void LocalUrlReferences_TrimSurroundingWhitespace()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs>" +
                "<linearGradient id='base'><stop stop-color='green'/></linearGradient>" +
                "<linearGradient id='derived' href=' #base '/>" +
                "<rect id='shape' width='10' height='10' fill='green'/>" +
                "</defs><rect width='10' height='10' fill=\"url(' #derived ') red\"/>" +
                "<use href=' #shape ' x='10'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(15, 5));
        }

        [Theory]
        [InlineData("svg")]
        [InlineData("symbol")]
        public void UseDimensions_OverrideReferencedViewport(string viewportElement)
        {
            using var result = _renderer.Render(
                $"<svg width='30' height='20'><defs><{viewportElement} id='s' width='5' height='5'>" +
                $"<rect width='100%' height='100%' fill='green'/></{viewportElement}></defs>" +
                "<use href='#s' width='20' height='10'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(15, 8));
            Assert.Equal(0, result.Bitmap.GetPixel(21, 8).Alpha);
        }

        [Fact]
        public void Use_SelfCycle_TerminatesWithoutHang()
        {
            var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\"><g id=\"a\"><use href=\"#a\"/></g></svg>",
                new SvgRenderLimits { MaxRenderTimeMs = 2000 });

            Assert.True(result.Success, result.ErrorMessage); // must terminate
        }

        [Fact]
        public void GradientReferenceCycle_TerminatesAndPaintsNothing()
        {
            var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\">" +
                "<linearGradient id=\"g1\"><linearGradient id=\"g2\" href=\"#g1\"/></linearGradient>" +
                "<rect width=\"10\" height=\"10\" fill=\"url(#g1)\"/></svg>",
                new SvgRenderLimits { MaxRenderTimeMs = 2000 });

            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void PreserveAspectRatio_Slice_FillsViewportClippingOverflow()
        {
            // viewBox aspect 2:1 into square viewport with slice: fills fully.
            var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\" viewBox=\"0 0 20 10\" preserveAspectRatio=\"xMidYMid slice\">" +
                "<rect width=\"20\" height=\"10\" fill=\"red\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            for (int x = 0; x <= 9; x += 3)
            {
                Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(x, 9));
            }
        }

        [Theory]
        [InlineData("0 0 0 10")]
        [InlineData("0 0 10 0")]
        public void ZeroAreaRootViewBox_RendersNothingButSucceeds(string viewBox)
        {
            using var result = _renderer.Render(
                $"<svg width='10' height='10' viewBox='{viewBox}'>" +
                "<rect width='10' height='10' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(10f, result.Width);
            Assert.Equal(10f, result.Height);
            Assert.False(HasVisiblePixels(result.Bitmap));
        }

        [Fact]
        public void ZeroAreaNestedViewBox_RendersNothing()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='20'><svg width='20' height='20' viewBox='0 0 0 10'>" +
                "<rect width='20' height='20' fill='red'/></svg></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(HasVisiblePixels(result.Bitmap));
        }

        [Fact]
        public void ZeroAreaSymbolViewBox_RendersNothing()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='20'><defs><symbol id='s' viewBox='0 0 0 10'>" +
                "<rect width='20' height='20' fill='red'/></symbol></defs>" +
                "<use href='#s' width='20' height='20'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(HasVisiblePixels(result.Bitmap));
        }

        private static bool HasVisiblePixels(FenBrowser.FenEngine.Adapters.SvgRenderResult r)
            => HasVisiblePixels(r.Bitmap);

        private static bool HasVisiblePixels(SkiaSharp.SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
