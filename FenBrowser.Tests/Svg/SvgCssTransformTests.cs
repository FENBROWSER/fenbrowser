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

        private SvgRenderResult RenderRaw(string svg)
        {
            var res = _r.Render(svg);
            Assert.True(res.Success, res.ErrorMessage ?? "(no error)");
            return res;
        }

        [Fact]
        public void StylesheetTranslate_MovesRect()
        {
            using var result = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: translate(30px, 5px); }</style>" +
                "<rect width='20' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(40, 10));
            Assert.True(result.Bitmap.GetPixel(10, 5).Alpha == 0, "original position must be vacated");
        }

        [Fact]
        public void CssTransform_OverridesTransformAttribute()
        {
            // Attribute says move right/down; CSS wins and moves further.
            using var result = RenderRaw(
                "<svg width='80' height='20'><style>rect { fill: red; transform: translate(50px, 5px); }</style>" +
                "<rect transform='translate(2,2)' width='20' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(60, 10));
            Assert.True(result.Bitmap.GetPixel(30, 8).Alpha == 0, "attribute-only position must be vacated");
        }

        [Fact]
        public void CssTransformNone_ResetsAttributeTransform()
        {
            using var result = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: none; }</style>" +
                "<rect transform='translate(30,5)' width='20' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 5));
            Assert.True(result.Bitmap.GetPixel(40, 10).Alpha == 0, "attribute translation must be suppressed");
        }

        [Fact]
        public void CalcInsideTranslate_Resolves()
        {
            using var result = RenderRaw(
                "<svg width='60' height='20'><style>rect { fill: red; transform: translate(calc(10px + 20px), 5px); }</style>" +
                "<rect width='20' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(40, 10));
        }

        [Fact]
        public void PercentageTranslate_ResolvesAgainstViewport()
        {
            using var result = RenderRaw(
                "<svg width='40' height='12'><style>rect { fill: red; transform: translate(50%, 0px); }</style>" +
                "<rect width='10' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(25, 5));
            Assert.True(result.Bitmap.GetPixel(5, 5).Alpha == 0);
        }

        [Fact]
        public void TransformOrigin_ScalesAroundExplicitPoint()
        {
            // g translate(50,20) + rect(0,0,20,10), origin (10px,5px), scale 2:
            // local (-10,-5)-(30,15) -> canvas x 40..80, y 15..35.
            using var result = RenderRaw(
                "<svg width='100' height='50'><style>rect { fill: red; transform-origin: 10px 5px; transform: scale(2); }</style>" +
                "<g transform='translate(50,20)'><rect width='20' height='10'/></g></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(75, 30));
            Assert.True(result.Bitmap.GetPixel(90, 12).Alpha == 0, "right of the scaled rect");
            Assert.True(result.Bitmap.GetPixel(38, 30).Alpha == 0, "left of the scaled rect");
            Assert.True(result.Bitmap.GetPixel(60, 44).Alpha == 0, "below the scaled rect");
        }

        [Fact]
        public void TransformBoxFillBox_PercentageOriginMatchesObjectBoundingBox()
        {
            // rect(0,0,40,10) inside g translate(20,10); fill-box origin
            // (75%,100%) = (30,10) local; scale 2 -> local (-30,-10)-(50,10)
            // -> canvas (-10,-0)-(70,20).
            using var result = RenderRaw(
                "<svg width='100' height='60'><style>#r { fill: red; transform-box: fill-box;" +
                "transform-origin: 75% 100%; transform: scale(2,2); }</style>" +
                "<g transform='translate(20,10)'><rect id='r' width='40' height='10'/></g></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(65, 18));
            Assert.True(result.Bitmap.GetPixel(90, 55).Alpha == 0);
        }

        [Fact]
        public void RotateDegrees_RotatesAroundDefaultZeroOrigin()
        {
            // rotate(90deg) maps rect(0,-10,10,4) (offscreen strip) to
            // x 6..10, y 0..10; ignoring the transform would draw nothing.
            using var result = RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate(90deg); }</style>" +
                "<rect y='-10' width='10' height='4'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(8, 5));
            Assert.True(result.Bitmap.GetPixel(2, 5).Alpha == 0, "outside the rotated strip");
        }

        [Fact]
        public void TransformOriginAttribute_UnitlessUserUnits()
        {
            // SVG2 presentation attribute form: transform-origin="30 10" with
            // attribute scale(2,3) on rect(0,0,40,10) in g translate(50,20):
            // local (-30,-20)-(50,10) -> canvas (20..100, 0..30).
            using var result = RenderRaw(
                "<svg width='120' height='40'><style>rect { fill: red; }</style>" +
                "<g transform='translate(50,20)'>" +
                "<rect width='40' height='10' transform='scale(2, 3)' transform-origin='30 10'/></g></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(95, 25));
            Assert.True(result.Bitmap.GetPixel(15, 8).Alpha == 0, "left of scaled rect");
            Assert.True(result.Bitmap.GetPixel(60, 35).Alpha == 0, "below scaled rect");
        }

        [Fact]
        public void ThreeDFunction_FailsClosedAsUnsupported()
        {
            AssertFailsClosed(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate3d(1,1,1,45deg); }</style>" +
                "<rect width='10' height='10'/></svg>");
        }

        [Fact]
        public void UnitlessNonZeroAngle_FailsClosedAsUnsupported()
        {
            AssertFailsClosed(
                "<svg width='40' height='20'><style>rect { fill: red; transform: rotate(45); }</style>" +
                "<rect width='10' height='10'/></svg>");
        }

        [Fact]
        public void StrokeBoxKeyword_FailsClosedAsUnsupported()
        {
            AssertFailsClosed(
                "<svg width='40' height='20'><style>rect { fill: red; transform-box: stroke-box; transform: scale(2); }</style>" +
                "<rect width='10' height='10'/></svg>");
        }

        [Fact]
        public void TransformNone_DoesNotResolveInertUnsupportedBox()
        {
            using var result = RenderRaw(
                "<svg width='40' height='20'><style>rect { fill:red; transform:none; transform-box:stroke-box; }</style>" +
                "<rect width='10' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void EmLengthInTranslate_ResolvesAgainstInheritedFontSize()
        {
            // Default font size is 16px: 1em shifts the rect by 16.
            using var result = RenderRaw(
                "<svg width='40' height='20'><style>rect { fill: red; transform: translate(1em, 0px); }</style>" +
                "<rect width='10' height='10'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 5));
            Assert.True(result.Bitmap.GetPixel(5, 5).Alpha == 0);
        }

        [Fact]
        public void CssOriginAndFillBox_ApplyToAttributeTransform()
        {
            using var result = RenderRaw(
                "<svg width='100' height='40'><style>#r { fill:red; transform-box:fill-box;" +
                "transform-origin:right center; }</style>" +
                "<rect id='r' x='20' y='10' width='20' height='10' transform='scale(2)'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
            Assert.True(result.Bitmap.GetPixel(50, 15).Alpha == 0);
        }

        [Fact]
        public void PresentationOriginUserUnits_ApplyToCssTransform()
        {
            using var result = RenderRaw(
                "<svg width='100' height='40'><style>#r { fill:red; transform:scale(2); }</style>" +
                "<rect id='r' x='20' y='10' width='20' height='10' transform-origin='40 15'/></svg>");
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
            Assert.True(result.Bitmap.GetPixel(50, 15).Alpha == 0);
        }

        [Fact]
        public void QuarterTurnRotation_RendersIdenticallyToTheEquivalentMatrix()
        {
            const string head = "<svg width='400' height='300'>" +
                "<path d='M 170 -30 l -120 240 l 240 0 Z' fill='black' ";
            using var rotated = RenderRaw(head +
                "transform-origin='170 150' style='transform:rotate(90deg)'/></svg>");
            using var matrix = RenderRaw(head +
                "style='transform:matrix(0 1 -1 0 320 -20)'/></svg>");
            Assert.False(rotated.RequiresFallback);
            Assert.False(matrix.RequiresFallback);
            AssertIdenticalPixels(matrix, rotated);
        }

        private void AssertFailsClosed(string svg)
        {
            using var result = _r.Render(svg);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.NotEmpty(result.FallbackReasonCodes);
        }

        private static void AssertIdenticalPixels(SvgRenderResult expected, SvgRenderResult actual)
        {
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < expected.Bitmap.Height; y++)
            for (int x = 0; x < expected.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
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

        [Fact]
        public void MatrixLengthComponents_AreUnsupported()
        {
            var s = SvgCssTransform.TryResolve("matrix(1,0,0,1,50px,0)", null, null,
                100, 50, 16, 16, null, out _, out _);
            Assert.Equal(SvgCssTransformStatus.Unsupported, s);
        }

        [Fact]
        public void HorizontalThenCenterOrigin_Parses()
        {
            var box = SKRect.Create(20, 10, 40, 20);
            var s = SvgCssTransform.TryResolve("scale(2)", "left center", "fill-box",
                100, 50, 16, 16, box, out _, out var origin);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            Assert.Equal(new SKPoint(20, 20), origin);
        }

        [Fact]
        public void CenterThenVerticalOrigin_Parses()
        {
            var box = SKRect.Create(20, 10, 40, 20);
            var s = SvgCssTransform.TryResolve("scale(2)", "center top", "fill-box",
                100, 50, 16, 16, box, out _, out var origin);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            Assert.Equal(new SKPoint(40, 10), origin);
        }

        [Fact]
        public void OverflowingAngle_IsUnsupportedBeforeNativeConcat()
        {
            var s = SvgCssTransform.TryResolve("rotate(1e308deg)", null, null,
                100, 50, 16, 16, null, out var matrix, out _);
            Assert.Equal(SvgCssTransformStatus.Unsupported, s);
            Assert.True(matrix.IsIdentity);
        }

        [Fact]
        public void OverflowingNumber_IsUnsupportedBeforeNativeConcat()
        {
            var s = SvgCssTransform.TryResolve("scale(1e308)", null, null,
                100, 50, 16, 16, null, out var matrix, out _);
            Assert.Equal(SvgCssTransformStatus.Unsupported, s);
            Assert.True(matrix.IsIdentity);
        }

        [Fact]
        public void PercentageTransformOriginDepth_IsUnsupported()
        {
            var s = SvgCssTransform.TryResolve("scale(2)", "left top 0%", null,
                100, 50, 16, 16, null, out _, out _);
            Assert.Equal(SvgCssTransformStatus.Unsupported, s);
        }

        [Theory]
        [InlineData("rotate(0)", 1f, 0f, 0f, 1f)]
        [InlineData("rotate(90deg)", 0f, -1f, 1f, 0f)]
        [InlineData("rotate(180deg)", -1f, 0f, 0f, -1f)]
        [InlineData("rotate(270deg)", 0f, 1f, -1f, 0f)]
        [InlineData("rotate(-90deg)", 0f, 1f, -1f, 0f)]
        [InlineData("rotate(0.25turn)", 0f, -1f, 1f, 0f)]
        [InlineData("rotate(100grad)", 0f, -1f, 1f, 0f)]
        [InlineData("rotate(200grad)", -1f, 0f, 0f, -1f)]
        public void QuarterTurnRotation_ResolvesToExactTrigonometry(
            string transform, float scaleX, float skewX, float skewY, float scaleY)
        {
            var s = SvgCssTransform.TryResolve(transform, null, null,
                400, 300, 16, 16, null, out var m, out _);
            Assert.True(s is SvgCssTransformStatus.Matrix or SvgCssTransformStatus.Identity,
                $"quarter turn must resolve, got {s}");
            Assert.Equal(scaleX, m.ScaleX);
            Assert.Equal(skewX, m.SkewX);
            Assert.Equal(skewY, m.SkewY);
            Assert.Equal(scaleY, m.ScaleY);
            Assert.Equal(0f, m.TransX);
            Assert.Equal(0f, m.TransY);
        }

        [Fact]
        public void QuarterTurnRotationAboutOrigin_KeepsTranslationExact()
        {
            var s = SvgCssTransform.TryResolve("rotate(90deg)", "170 150", null,
                400, 300, 16, 16, null, allowOriginUserUnits: true, out var m, out _);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            Assert.Equal(0f, m.ScaleX);
            Assert.Equal(-1f, m.SkewX);
            Assert.Equal(1f, m.SkewY);
            Assert.Equal(0f, m.ScaleY);
            Assert.Equal(320f, m.TransX);
            Assert.Equal(-20f, m.TransY);
        }

        [Fact]
        public void NonQuarterTurnRotation_KeepsFullPrecisionTrigonometry()
        {
            var s = SvgCssTransform.TryResolve("rotate(45deg)", null, null,
                400, 300, 16, 16, null, out var m, out _);
            Assert.Equal(SvgCssTransformStatus.Matrix, s);
            Assert.Equal(0.70710677f, m.ScaleX);
            Assert.Equal(-0.70710677f, m.SkewX);
            Assert.Equal(0.70710677f, m.SkewY);
            Assert.Equal(0.70710677f, m.ScaleY);
        }
    }
}

