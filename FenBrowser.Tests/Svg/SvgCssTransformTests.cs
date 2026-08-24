using System;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// CSS transform / transform-origin / transform-box support in the bounded
    /// SVG cascade. Pixel probes use asymmetric geometry so a silently dropped
    /// transform cannot hide behind background similarity; unsupported input
    /// must surface as RequiresFallback rather than wrong pixels.
    /// </summary>
    public class SvgCssTransformTests
    {
        private readonly FenSvgRenderer _r = new();

        private SKBitmap RenderRaw(string svg, out bool requiresFallback, out bool success)
        {
            var res = _r.Render(svg);
            success = res.Success;
            requiresFallback = res.RequiresFallback;
            Assert.True(res.Success, res.ErrorMessage ?? "(no error)");
            return res.Bitmap;
        }

        [Fact]
        public void StylesheetTranslate_MovesRect()
        {
            var bmp = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: translate(30px, 5px); }</style>" +
                "<rect width='20' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(40, 10));
            Assert.True(bmp.GetPixel(10, 5).Alpha == 0, "original position must be vacated");
        }

        [Fact]
        public void CssTransform_OverridesTransformAttribute()
        {
            // Attribute says move right/down; CSS wins and moves further.
            var bmp = RenderRaw(
                "<svg width='80' height='20'><style>rect { fill: red; transform: translate(50px, 5px); }</style>" +
                "<rect transform='translate(2,2)' width='20' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(60, 10));
            Assert.True(bmp.GetPixel(30, 8).Alpha == 0, "attribute-only position must be vacated");
        }

        [Fact]
        public void CssTransformNone_ResetsAttributeTransform()
        {
            var bmp = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: none; }</style>" +
                "<rect transform='translate(30,5)' width='20' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(10, 5));
            Assert.True(bmp.GetPixel(40, 10).Alpha == 0, "attribute translation must be suppressed");
        }

        [Fact]
        public void CalcInsideTranslate_Resolves()
        {
            var bmp = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: translate(calc(10px + 20px), 5px); }</style>" +
                "<rect width='20' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(40, 10));
        }

        [Fact]
        public void PercentageTranslate_ResolvesAgainstViewport()
        {
            var bmp = RenderRaw(
                "<svg width='40' height='12'><style>rect { fill: red; transform: translate(50%, 0px); }</style>" +
                "<rect width='10' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(25, 5));
            Assert.True(bmp.GetPixel(5, 5).Alpha == 0);
        }

        [Fact]
        public void TransformOrigin_ScalesAroundExplicitPoint()
        {
            // g translate(50,20) + rect(0,0,20,10), origin (10px,5px), scale 2:
            // local (-10,-5)-(30,15) -> canvas x 40..80, y 15..35.
            var bmp = RenderRaw(
                "<svg width='100' height='50'><style>rect { fill: red; transform-origin: 10px 5px; transform: scale(2); }</style>" +
                "<g transform='translate(50,20)'><rect width='20' height='10'/></g></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(75, 30));
            Assert.True(bmp.GetPixel(90, 12).Alpha == 0, "right of the scaled rect");
            Assert.True(bmp.GetPixel(38, 30).Alpha == 0, "left of the scaled rect");
            Assert.True(bmp.GetPixel(60, 44).Alpha == 0, "below the scaled rect");
        }

        [Fact]
        public void TransformBoxFillBox_PercentageOriginMatchesObjectBoundingBox()
        {
            // rect(0,0,40,10) inside g translate(20,10); fill-box origin
            // (75%,100%) = (30,10) local; scale 2 -> local (-30,-10)-(50,10)
            // -> canvas (-10,-0)-(70,20).
            var bmp = RenderRaw(
                "<svg width='100' height='60'><style>#r { fill: red; transform-box: fill-box;" +
                "transform-origin: 75% 100%; transform: scale(2,2); }</style>" +
                "<g transform='translate(20,10)'><rect id='r' width='40' height='10'/></g></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(65, 18));
            Assert.True(bmp.GetPixel(90, 55).Alpha == 0);
        }

        [Fact]
        public void RotateDegrees_RotatesAroundDefaultZeroOrigin()
        {
            // rotate(90deg) maps rect(0,-10,10,4) (offscreen strip) to
            // x 6..10, y 0..10; ignoring the transform would draw nothing.
            var bmp = RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate(90deg); }</style>" +
                "<rect y='-10' width='10' height='4'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(8, 5));
            Assert.True(bmp.GetPixel(2, 5).Alpha == 0, "outside the rotated strip");
        }

        [Fact]
        public void TransformOriginAttribute_UnitlessUserUnits()
        {
            // SVG2 presentation attribute form: transform-origin="30 10" with
            // attribute scale(2,3) on rect(0,0,40,10) in g translate(50,20):
            // local (-30,-20)-(50,10) -> canvas (20..100, 0..30).
            var bmp = RenderRaw(
                "<svg width='120' height='40'><style>rect { fill: red; }</style>" +
                "<g transform='translate(50,20)'>" +
                "<rect width='40' height='10' transform='scale(2, 3)' transform-origin='30 10'/></g></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(95, 25));
            Assert.True(bmp.GetPixel(15, 8).Alpha == 0, "left of scaled rect");
            Assert.True(bmp.GetPixel(60, 35).Alpha == 0, "below scaled rect");
        }

        [Fact]
        public void ThreeDFunction_RequiresCompatibilityFallback()
        {
            RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate3d(1,1,1,45deg); }</style>" +
                "<rect width='10' height='10'/></svg>",
                out bool fb, out _);
            Assert.True(fb);
        }

        [Fact]
        public void UnitlessNonZeroAngle_RequiresCompatibilityFallback()
        {
            RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate(45); }</style>" +
                "<rect width='10' height='10'/></svg>",
                out bool fb, out _);
            Assert.True(fb);
        }

        [Fact]
        public void StrokeBoxKeyword_RequiresCompatibilityFallback()
        {
            RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform-box: stroke-box; transform: scale(2); }</style>" +
                "<rect width='10' height='10'/></svg>",
                out bool fb, out _);
            Assert.True(fb);
        }

        [Fact]
        public void EmLengthInTranslate_ResolvesAgainstInheritedFontSize()
        {
            // Default font size is 16px: 1em shifts the rect by 16.
            var bmp = RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: translate(1em, 0px); }</style>" +
                "<rect width='10' height='10'/></svg>",
                out bool fb, out _);
            Assert.False(fb);
            Assert.Equal(SKColors.Red, bmp.GetPixel(20, 5));
            Assert.True(bmp.GetPixel(5, 5).Alpha == 0);
        }
    }
}

namespace FenBrowser.Tests.Svg
{
    public class SvgCssTransformResolverTests
    {
        [Fact]
        public void TranslatePx_Parses()
        {
            var s = SvgCssTransform.TryResolve("translate(29px, 11px)", null, null,
                300, 200, 16, 16, null, out var m, out var o);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            Assert.Equal(29f, m.TransX);
            Assert.Equal(11f, m.TransY);
        }

        [Fact]
        public void OriginPx_Parses()
        {
            var s = SvgCssTransform.TryResolve("scale(2)", "10px 5px", null,
                100, 50, 16, 16, null, out var m, out _);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            // T(10,5) S(2) T(-10,-5): x' = 2x - 10
            Assert.Equal(-10f, m.TransX);
            Assert.Equal(-5f, m.TransY);
            Assert.Equal(2f, m.ScaleX);
        }

        [Fact]
        public void OriginPercentFillBox_UsesBoxDims()
        {
            var box = SKRect.Create(20, 10, 40, 10); // object bounding box
            var s = SvgCssTransform.TryResolve("scale(2)", "75% 100%", "fill-box",
                300, 200, 16, 16, box, out var m, out _);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            // Origin (boxLeft+30, boxTop+10) = (50,20); T(o) S(2) T(-o):
            Assert.Equal(-50f, m.TransX, 2f);
            Assert.Equal(-20f, m.TransY, 2f);
            Assert.Equal(2f, m.ScaleX);
        }
    }
}

