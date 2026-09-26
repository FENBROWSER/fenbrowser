using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// PHASE 1 reproduction tests (written to FAIL before the fix):
    /// - A: element-level transforms on graphical elements were ignored.
    /// - B: image elements ignored transform and clip-path.
    /// - C: preserveAspectRatio alignment tokens never parsed (silent default).
    /// Every case uses asymmetric geometry with explicit foreground probes so a
    /// missing transform cannot hide behind background similarity.
    /// </summary>
    public class SvgTransformReproTests
    {
        private readonly FenSvgRenderer _r = new();

        private SKColor Px(string svg, int x, int y)
        {
            using var res = _r.Render(svg);
            Assert.True(res.Success, res.ErrorMessage ?? "(no error)");
            return res.Bitmap.GetPixel(x, y);
        }

        private static void AssertFilled(SKColor c, string because)
        {
            Assert.True(c.Alpha > 200, $"{because}: expected filled, got {c}");
        }

        private static void AssertEmpty(SKColor c, string because)
        {
            Assert.True(c.Alpha == 0, $"{because}: expected empty, got {c}");
        }

        // ------------------------------------------------------------ A: shapes

        [Fact]
        public void TranslatedRect_Moves()
        {
            var svg = "<svg width='40' height='40'>" +
                      "<rect x='2' y='2' width='6' height='6' fill='red' transform='translate(12 4)'/></svg>";
            AssertFilled(Px(svg, 16, 8), "translated position");
            AssertEmpty(Px(svg, 5, 5), "original position must be vacated");
        }

        [Fact]
        public void RotatedNonSquareRect_AroundCenter_Moves()
        {
            // 180deg about (30,18): rect(20,10,10,6) -> rect(30,20,10,6).
            var svg = "<svg width='50' height='50'>" +
                      "<rect x='20' y='10' width='10' height='6' fill='red' transform='rotate(180 30 18)'/></svg>";
            AssertFilled(Px(svg, 35, 23), "rotated position");
            AssertEmpty(Px(svg, 25, 13), "pre-rotation position");
        }

        [Fact]
        public void ScaledTriangle_Grows()
        {
            var svg = "<svg width='60' height='60'>" +
                      "<path d='M0 0 L10 0 L10 10 Z' fill='blue' transform='translate(20 0) scale(2)'/></svg>";
            // Triangle occupies x in [20,40], hypotenuse y=x-20: probe inside below line.
            AssertFilled(Px(svg, 30, 5), "scaled interior");
            AssertEmpty(Px(svg, 5, 5), "unscaled location");
        }

        [Fact]
        public void NestedGroupAndChild_Transforms_Compose()
        {
            var svg = "<svg width='40' height='40'>" +
                      "<g transform='translate(10 10)'>" +
                      "<rect x='0' y='0' width='5' height='5' fill='red' transform='translate(5 5)'/>" +
                      "</g></svg>";
            AssertFilled(Px(svg, 17, 17), "composed offset");
            AssertEmpty(Px(svg, 12, 12), "group-only offset must not hit");
            AssertEmpty(Px(svg, 2, 2), "untransformed origin");
        }

        [Fact]
        public void TransformList_Order_IsTranslateThenRotate()
        {
            // translate(8 0) rotate(90): rect(0,0,4,4) lands at x[4,8] y[0,4].
            var svg = "<svg width='20' height='20'>" +
                      "<rect x='0' y='0' width='4' height='4' fill='red' transform='translate(8 0) rotate(90)'/></svg>";
            AssertFilled(Px(svg, 6, 2), "list-order result");
            // Reversed order would push geometry off-canvas (negative y).
        }

        [Fact]
        public void TransformedShape_WithClipPath_IntersectsCorrectly()
        {
            var svg = "<svg width='40' height='40'>" +
                      "<clipPath id='c'><rect x='0' y='0' width='15' height='15'/></clipPath>" +
                      "<rect x='0' y='0' width='30' height='30' fill='red' transform='translate(10 10)' clip-path='url(#c)'/></svg>";
            AssertFilled(Px(svg, 20, 20), "intersection region");
            AssertEmpty(Px(svg, 28, 28), "outside clip");
        }

        [Fact]
        public void TransformedShape_ViaUse_IsPlaced()
        {
            var svg = "<svg width='40' height='40'>" +
                      "<defs><rect id='s' width='5' height='5' fill='red'/></defs>" +
                      "<use href='#s' transform='translate(11 7)'/></svg>";
            AssertFilled(Px(svg, 13, 9), "use-transformed placement");
            AssertEmpty(Px(svg, 2, 2), "origin must be empty");
        }

        // ------------------------------------------------------------- B: images

        private static string RedPng(int w, int h)
        {
            using var bmp = new SKBitmap(w, h);
            bmp.Erase(SKColors.Red);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        [Fact]
        public void TranslatedImage_Moves()
        {
            var svg = $"<svg width='40' height='40'><image href='{RedPng(4, 4)}' x='2' y='2' width='6' height='6' transform='translate(12 8)'/></svg>";
            AssertFilled(Px(svg, 16, 12), "translated image");
            AssertEmpty(Px(svg, 4, 4), "untransformed spot");
        }

        [Fact]
        public void RotatedImage_Applies()
        {
            var svg = $"<svg width='40' height='40'><image href='{RedPng(4, 4)}' x='25' y='5' width='10' height='10' transform='rotate(180 30 18)'/></svg>";
            // 180deg about (30,18): (25..35, 5..15) -> (25..35, 21..31).
            AssertFilled(Px(svg, 30, 26), "rotated image position");
            AssertEmpty(Px(svg, 30, 8), "pre-rotation position");
        }

        [Fact]
        public void ClippedImage_RespectsClip()
        {
            var svg = "<svg width='30' height='30'>" +
                      "<clipPath id='c'><rect x='0' y='0' width='15' height='30'/></clipPath>" +
                      $"<image href='{RedPng(4, 4)}' x='0' y='0' width='30' height='30' clip-path='url(#c)'/></svg>";
            AssertFilled(Px(svg, 8, 15), "clipped-in half");
            AssertEmpty(Px(svg, 22, 15), "clipped-out half");
        }

        [Fact]
        public void HiddenImage_DoesNotRender()
        {
            var svg = $"<svg width='20' height='20'><image href='{RedPng(4, 4)}' x='2' y='2' width='10' height='10' visibility='hidden'/></svg>";
            AssertEmpty(Px(svg, 7, 7), "visibility hidden");
        }

        [Fact]
        public void ImageOpacity_Composites()
        {
            var svg = $"<svg width='20' height='20'><image href='{RedPng(4, 4)}' x='0' y='0' width='20' height='20' opacity='0.5'/></svg>";
            var c = Px(svg, 10, 10);
            Assert.InRange(c.Alpha, 100, 155);
        }

        // --------------------------------------------------- C: preserveAspectRatio

        [Theory]
        [InlineData("xMinYMin", 5, 5)]
        [InlineData("xMaxYMin", 5, 5)]
        public void Par_WideViewBox_YPlacement(string par, int xProbe, int yTop)
        {
            // vb 80x20 into 40x40: scale .5 -> content 40x10; leftoverY=30.
            bool top = par.EndsWith("YMin", StringComparison.Ordinal);
            var svg = $"<svg width='40' height='40' viewBox='0 0 80 20' preserveAspectRatio='{par}'>" +
                      "<rect width='80' height='20' fill='red'/></svg>";
            var c = Px(svg, xProbe, top ? yTop : 35);
            AssertFilled(c, $"{par} y-placement");
            var other = Px(svg, xProbe, top ? 35 : yTop);
            AssertEmpty(other, $"{par} opposite side must be empty");
        }

        [Theory]
        [InlineData("xMinYMid", 3, 16)]
        [InlineData("xMaxYMid", 36, 16)]
        public void Par_TallViewBox_XPlacement(string par, int xFilled, int xEmpty)
        {
            // vb 20x80 into 40x40: scale .5 -> content 10x40; leftoverX=30.
            var svg = $"<svg width='40' height='40' viewBox='0 0 20 80' preserveAspectRatio='{par}'>" +
                      "<rect width='20' height='80' fill='lime'/></svg>";
            AssertFilled(Px(svg, xFilled, 20), $"{par} x-placement");
            AssertEmpty(Px(svg, xEmpty, 20), $"{par} opposite side");
        }

        [Fact]
        public void Par_Nested_Svg_AlignmentApplies()
        {
            var svg = "<svg width='40' height='40'>" +
                      "<svg width='40' height='40' viewBox='0 0 80 20' preserveAspectRatio='xMaxYMin'>" +
                      "<rect width='80' height='20' fill='red'/></svg></svg>";
            AssertFilled(Px(svg, 35, 5), "nested par top-right");
            AssertEmpty(Px(svg, 5, 35), "nested par elsewhere");
        }
    }
}
