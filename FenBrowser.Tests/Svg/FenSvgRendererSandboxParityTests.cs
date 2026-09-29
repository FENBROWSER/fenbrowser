using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// First-party sandbox budget semantics plus an adversarial corpus that must
    /// never throw, hang, or allocate unbounded memory.
    /// </summary>
    public class FenSvgRendererSandboxParityTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void EmptyContent_FailsWithParityMessage()
        {
            using var result = _renderer.Render("");
            Assert.False(result.Success);
            Assert.Equal("Empty SVG content", result.ErrorMessage);
        }

        [Fact]
        public void SourceLengthLimit_ParityMessage()
        {
            var limits = new SvgRenderLimits { MaxSourceChars = 10 };
            using var result = _renderer.Render("<svg></svg>", limits);
            Assert.False(result.Success);
            Assert.Contains("exceeds limit (10)", result.ErrorMessage);
            Assert.Contains("source length", result.ErrorMessage);
        }

        [Fact]
        public void ElementCountLimit_ParityMessage()
        {
            using var result = _renderer.Render(
                "<svg width=\"1\" height=\"1\"><g/><g/><g/></svg>",
                new SvgRenderLimits { MaxElementCount = 3 });
            Assert.False(result.Success);
            Assert.Contains("element count", result.ErrorMessage);
        }

        [Fact]
        public void FilterCountLimit_ParityMessage()
        {
            using var result = _renderer.Render(
                "<svg width=\"1\" height=\"1\">" +
                "<filter id=\"a\"/><filter id=\"b\"/><filter id=\"c\"/></svg>",
                new SvgRenderLimits { MaxFilterCount = 2 });
            Assert.False(result.Success);
            Assert.Contains("filter count", result.ErrorMessage);
        }

        [Fact]
        public void DepthLimit_ParityMessage()
        {
            var sb = new StringBuilder("<svg width=\"1\" height=\"1\">");
            for (int i = 0; i < 40; i++) sb.Append("<g>");
            using var result = _renderer.Render(sb.ToString(), new SvgRenderLimits { MaxRecursionDepth = 8 });
            Assert.False(result.Success);
            Assert.Contains("nesting depth", result.ErrorMessage);
        }

        [Fact]
        public void RasterBomb_IsRejected_BeforeAllocation()
        {
            using var result = _renderer.Render(
                "<svg width=\"20000\" height=\"20000\"><rect width=\"20000\" height=\"20000\"/></svg>",
                new SvgRenderLimits { MaxRasterWidth = 512, MaxRasterHeight = 512, MaxRasterPixels = 262144 });
            Assert.False(result.Success);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
            Assert.Null(result.Bitmap);
        }

        [Fact]
        public void HugeDeclaredViewport_WithTinyViewBox_UsesViewBoxDerivedSize()
        {
            // A classic raster-bomb trick: huge width/height, small content.
            // Our intrinsic sizing prefers explicit attrs, so this must hit the
            // raster guard rather than attempt a giant allocation.
            using var result = _renderer.Render(
                "<svg width=\"1000000\" height=\"1000000\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>",
                new SvgRenderLimits { MaxRasterWidth = 8192, MaxRasterHeight = 8192, MaxRasterPixels = 16L * 1024 * 1024 });
            Assert.False(result.Success);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
        }

        [Fact]
        public void PixelBudgetOverrun_AtTheClampAllowance_RendersAtTheClampedRaster()
        {
            var limits = ClampBudget(160_000);

            using var result = _renderer.Render(SolidDocument(800, 800), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.Null(result.Picture);
            Assert.False(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Empty(result.ResourceRejectionReasonCodes);

            Assert.NotNull(result.Bitmap);
            Assert.Equal(400, result.Bitmap.Width);
            Assert.Equal(400, result.Bitmap.Height);
            Assert.Equal(800f, result.Width);
            Assert.Equal(800f, result.Height);
            AssertClampWarning(result, 400, 400, 800, 800);
            AssertWithinBudget(result.Bitmap, limits);
            Assert.True(ForegroundRatio(result.Bitmap) > 0.99d, "clamped raster lost its content");
        }

        [Fact]
        public void PixelBudgetOverrun_JustPastTheClampAllowance_FailsClosed()
        {
            var limits = ClampBudget(160_000);

            using var result = _renderer.Render(SolidDocument(802, 802), limits);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.Equal(
                "SVG raster bounds 802x802 are inside the raster size caps (8192x8192) but " +
                "cannot fit the raster pixel budget (160000 pixels) within the 0.5x " +
                "minimum reduction",
                result.ErrorMessage);
            Assert.DoesNotContain("exceed browser limits", result.ErrorMessage);
            Assert.False(SvgRenderResult.IsAdmissible(result));
        }

        [Fact]
        public void PixelBudgetOverrun_StaysAdmissibleAtTheLargestClampableRaster()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 30_000;

            using var result = _renderer.Render(SolidDocument(8192, 8192), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(4096, result.Bitmap.Width);
            Assert.Equal(4096, result.Bitmap.Height);
            Assert.Equal(8192f, result.Width);
            Assert.Equal(8192f, result.Height);
            AssertClampWarning(result, 4096, 4096, 8192, 8192);
            AssertWithinBudget(result.Bitmap, limits);
        }

        [Fact]
        public void RasterWidthAndHeightCaps_AreNeverRescaledByTheClamp()
        {
            var limits = ClampBudget(1_000_000, maxWidth: 100, maxHeight: 100);

            using var result = _renderer.Render(SolidDocument(200, 20), limits);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
        }

        [Fact]
        public void PixelBudgetOverrun_ClampedRasterIsDeterministicAndBounded()
        {
            var limits = ClampBudget(6_000);
            string source = SolidDocument(100, 100);

            using var first = _renderer.Render(source, limits);
            using var second = _renderer.Render(source, limits);

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.Equal(77, first.Bitmap.Width);
            Assert.Equal(77, first.Bitmap.Height);
            Assert.Equal((int)first.Width, (int)second.Width);
            Assert.Equal((int)first.Height, (int)second.Height);
            Assert.Equal(Fingerprint(first.Bitmap), Fingerprint(second.Bitmap));
            AssertClampWarning(first, 77, 77, 100, 100);
            AssertWithinBudget(first.Bitmap, limits);
            AssertWithinBudget(second.Bitmap, limits);
        }

        [Fact]
        public void PixelBudgetOverrun_ClampedRasterKeepsTheCullRectOrigin()
        {
            var limits = ClampBudget(160_000);

            using var result = _renderer.Render(
                "<svg viewBox='100 200 800 800'>" +
                "<rect x='100' y='200' width='800' height='800' fill='#3a7'/></svg>", limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(400, result.Bitmap.Width);
            Assert.Equal(400, result.Bitmap.Height);
            Assert.Equal(800f, result.Width);
            Assert.Equal(800f, result.Height);
            AssertClampWarning(result, 400, 400, 800, 800);
            Assert.Equal(255, result.Bitmap.GetPixel(0, 0).Alpha);
            Assert.Equal(255, result.Bitmap.GetPixel(399, 0).Alpha);
            Assert.Equal(255, result.Bitmap.GetPixel(0, 399).Alpha);
            Assert.Equal(255, result.Bitmap.GetPixel(399, 399).Alpha);
            Assert.True(ForegroundRatio(result.Bitmap) > 0.99d, "clamped raster lost its origin");
        }

        [Fact]
        public void PixelBudgetOverrun_ReportsTheReductionSoCallersCompositeCorrectly()
        {
            var limits = ClampBudget(2_500);

            using var result = _renderer.Render(SolidDocument(100, 100), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.NotNull(result.Bitmap);
            Assert.Equal(50, result.Bitmap.Width);
            Assert.Equal(50, result.Bitmap.Height);

            Assert.Equal(100f, result.Width);
            Assert.Equal(100f, result.Height);
            Assert.Equal(0.5f, result.RasterScaleX);
            Assert.Equal(0.5f, result.RasterScaleY);
            Assert.True(result.IsDownscaled);
            Assert.Equal((float)result.Bitmap.Width, result.Width * result.RasterScaleX, 3);
            Assert.Equal((float)result.Bitmap.Height, result.Height * result.RasterScaleY, 3);
            AssertClampWarning(result, 50, 50, 100, 100);
        }

        [Fact]
        public void WithinPixelBudget_ReportsUnitScaleAndNoDownscale()
        {
            var limits = ClampBudget(160_000);

            using var result = _renderer.Render(SolidDocument(100, 100), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(100, result.Bitmap.Width);
            Assert.Equal(100, result.Bitmap.Height);
            Assert.Equal(1f, result.RasterScaleX);
            Assert.Equal(1f, result.RasterScaleY);
            Assert.False(result.IsDownscaled);
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("raster clamped"));
        }

        [Fact]
        public void RejectedRaster_ReportsNoReduction()
        {
            var limits = ClampBudget(160_000);

            using var result = _renderer.Render(SolidDocument(802, 802), limits);

            Assert.False(result.Success);
            Assert.Equal(1f, result.RasterScaleX);
            Assert.Equal(1f, result.RasterScaleY);
            Assert.False(result.IsDownscaled);
        }

        [Fact]
        public void ClampedRasterDraw_PlacesAnOffOriginCullRectAtTheBitmapOrigin()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(160_000));
            var cullRect = new SKRect(1000f, 2000f, 1800f, 2800f);
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(
                    800d, 800d, limits, out var plan, out _),
                "800x800 must stay clampable at a 160000 pixel budget");
            Assert.True(plan.IsClamped);
            Assert.Equal(400, plan.Width);
            Assert.Equal(400, plan.Height);

            using var picture = RecordSplitPicture(cullRect);
            using var bitmap = DrawThroughAdapter(picture, plan, cullRect);

            Assert.Equal(400, bitmap.Width);
            Assert.Equal(400, bitmap.Height);
            Assert.Equal(255, bitmap.GetPixel(0, 0).Alpha);
            Assert.Equal(255, bitmap.GetPixel(399, 399).Alpha);
            Assert.True(ForegroundRatio(bitmap) > 0.99d, "clamped draw lost the cull rect");
            Assert.True(
                Near(bitmap.GetPixel(50, 200), SKColors.Red),
                "the cull-rect left half must land on the bitmap left half");
            Assert.True(
                Near(bitmap.GetPixel(350, 200), SKColors.Lime),
                "the cull-rect right half must land on the bitmap right half");
        }

        [Fact]
        public void UnclampedRasterDraw_PlaysTheOffOriginCullRectBackUnchanged()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(160_000));
            var cullRect = new SKRect(1000f, 2000f, 1800f, 2800f);
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(
                    800d, 800d, limits, out var clamped, out _));
            Assert.Equal(0.5d, clamped.ScaleX);
            Assert.Equal(400, clamped.Width);

            var roomy = SvgRenderLimits.Normalize(ClampBudget(1_000_000));
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(
                    800d, 800d, roomy, out var plan, out _),
                "800x800 is inside the 1000000 pixel budget and must not clamp");
            Assert.False(plan.IsClamped);
            Assert.Equal(1d, plan.ScaleX);
            Assert.Equal(1d, plan.ScaleY);
            Assert.Equal(800, plan.Width);
            Assert.Equal(800, plan.Height);

            using var picture = RecordSplitPicture(cullRect);
            using var bitmap = new SKBitmap(plan.Width, plan.Height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(-cullRect.Left, -cullRect.Top);
                FenSvgRenderer.DrawRasterSurface(canvas, picture, plan, cullRect);
            }

            Assert.Equal(255, bitmap.GetPixel(0, 0).Alpha);
            Assert.True(Near(bitmap.GetPixel(50, 400), SKColors.Red));
            Assert.True(Near(bitmap.GetPixel(700, 400), SKColors.Lime));
        }

        [Fact]
        public void RasterPlan_AtTheClampAllowance_AdmitsExactlyHalfScale()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(160_000));

            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(800d, 800d, limits, out var plan, out _));
            Assert.True(plan.IsClamped);
            Assert.Equal(400, plan.Width);
            Assert.Equal(400, plan.Height);
            Assert.Equal(0.5d, plan.ScaleX);
            Assert.Equal(0.5d, plan.ScaleY);
            Assert.Equal(160_000L, (long)plan.Width * plan.Height);
        }

        [Fact]
        public void RasterPlan_JustPastTheClampAllowance_RefusesInsteadOfAllocating()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(160_000));

            Assert.False(
                FenSvgRenderer.RasterPlan.TryCreate(
                    802d, 802d, limits, out var plan, out var refusal));
            Assert.Equal(FenSvgRenderer.RasterRefusal.ReductionTooLarge, refusal);
            Assert.Equal(0, plan.Width);
            Assert.Equal(0, plan.Height);
            Assert.False(plan.IsClamped);
        }

        [Fact]
        public void RasterPlan_AtTheLargestClampableRaster_HalvesEachAxisExactly()
        {
            var limits = SvgRenderLimits.Normalize(SvgRenderLimits.Default);

            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(8192d, 8192d, limits, out var plan, out _));
            Assert.Equal(8192, limits.MaxRasterWidth);
            Assert.Equal(8192, limits.MaxRasterHeight);
            Assert.True(plan.IsClamped);
            Assert.Equal(4096, plan.Width);
            Assert.Equal(4096, plan.Height);
            Assert.Equal(0.5d, plan.ScaleX);
            Assert.Equal(16L * 1024 * 1024, (long)plan.Width * plan.Height);
        }

        [Fact]
        public void RasterPlan_PerAxisCapsRefuseEvenWithAnUnboundedPixelBudget()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(64L * 1024 * 1024, 100, 100));

            Assert.False(
                FenSvgRenderer.RasterPlan.TryCreate(
                    101d, 100d, limits, out _, out var wide));
            Assert.Equal(FenSvgRenderer.RasterRefusal.PerAxisCap, wide);
            Assert.False(
                FenSvgRenderer.RasterPlan.TryCreate(
                    100d, 101d, limits, out _, out var tall));
            Assert.Equal(FenSvgRenderer.RasterRefusal.PerAxisCap, tall);

            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(
                    100d, 100d, limits, out var admitted, out _));
            Assert.False(admitted.IsClamped);
            Assert.Equal(100, admitted.Width);
            Assert.Equal(100, admitted.Height);
        }

        [Fact]
        public void RasterPlan_NeverExceedsAnyCapAfterIntegerRounding()
        {
            int[] sizes =
            {
                1, 2, 3, 7, 10, 63, 64, 100, 101, 255, 400, 800, 801, 802, 1000, 1023,
                1024, 4095, 4096, 5669, 8191, 8192
            };
            long[] budgets = { 1, 4, 2500, 6000, 100_000, 160_000, 1_000_000, 16L * 1024 * 1024 };

            foreach (long budget in budgets)
            {
                var limits = SvgRenderLimits.Normalize(ClampBudget(budget));
                foreach (int width in sizes)
                {
                    foreach (int height in sizes)
                    {
                        bool admitted = FenSvgRenderer.RasterPlan.TryCreate(
                            width, height, limits, out var plan, out var refusal);
                        string context = $"{width}x{height} at {budget} pixels";

                        if (!admitted)
                        {
                            Assert.True(
                                refusal != FenSvgRenderer.RasterRefusal.None,
                                $"{context} was refused without a reason");
                            continue;
                        }

                        Assert.True(
                            plan.Width >= 1 && plan.Width <= limits.MaxRasterWidth,
                            $"{context} admitted width {plan.Width}");
                        Assert.True(
                            plan.Height >= 1 && plan.Height <= limits.MaxRasterHeight,
                            $"{context} admitted height {plan.Height}");
                        Assert.True(
                            (long)plan.Width * plan.Height <= limits.MaxRasterPixels,
                            $"{context} admitted {(long)plan.Width * plan.Height} pixels");
                        Assert.True(
                            plan.ScaleX >= FenSvgRenderer.MinRasterClampScale && plan.ScaleX <= 1d,
                            $"{context} scaleX {plan.ScaleX}");
                        Assert.True(
                            plan.ScaleY >= FenSvgRenderer.MinRasterClampScale && plan.ScaleY <= 1d,
                            $"{context} scaleY {plan.ScaleY}");
                        Assert.True(
                            (long)width * height <= budget == !plan.IsClamped,
                            $"{context} clamp flag {plan.IsClamped}");

                        FenSvgRenderer.RasterPlan.TryCreate(
                            width, height, limits, out var repeat, out _);
                        Assert.True(
                            plan.Width == repeat.Width &&
                            plan.Height == repeat.Height &&
                            plan.ScaleX == repeat.ScaleX &&
                            plan.ScaleY == repeat.ScaleY,
                            $"{context} is not deterministic");
                    }
                }
            }
        }

        [Fact]
        public void ClampedDrawMatrix_MapsTheOffOriginCullRectOntoTheBitmapRect()
        {
            var limits = SvgRenderLimits.Normalize(ClampBudget(160_000));
            var cullRect = new SKRect(1000f, 2000f, 1800f, 2800f);
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(800d, 800d, limits, out var plan, out _));

            SKMatrix matrix = FenSvgRenderer.CreateClampedDrawMatrix(plan, cullRect);

            SKPoint origin = matrix.MapPoint(new SKPoint(cullRect.Left, cullRect.Top));
            Assert.Equal(0f, origin.X, 3);
            Assert.Equal(0f, origin.Y, 3);

            SKPoint far = matrix.MapPoint(new SKPoint(cullRect.Right, cullRect.Bottom));
            Assert.Equal(plan.Width, far.X, 3);
            Assert.Equal(plan.Height, far.Y, 3);

            var unscaled = SKMatrix.CreateScaleTranslation(
                (float)plan.ScaleX, (float)plan.ScaleY, -cullRect.Left, -cullRect.Top);
            SKPoint wrong = unscaled.MapPoint(new SKPoint(cullRect.Left, cullRect.Top));
            Assert.True(
                Math.Abs(wrong.X) > 0.001f || Math.Abs(wrong.Y) > 0.001f,
                "CreateScaleTranslation composes as T*S, so an unscaled -left/-top " +
                "translation cannot map the cull-rect origin onto the bitmap origin");
            Assert.True(
                wrong.X < 0f || wrong.Y < 0f,
                "the unscaled translation pushes the cull rect off the reduced surface");
        }

        [Fact]
        public void RasterPlan_IntegerFlooringCannotUndercutTheClampBound()
        {
            var snappy = SvgRenderLimits.Normalize(ClampBudget(16L * 1024 * 1024));
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(8191d, 8192d, snappy, out var odd, out _));
            Assert.True(odd.IsClamped);
            Assert.Equal(4096, odd.Width);
            Assert.Equal(4096, odd.Height);
            Assert.Equal(16L * 1024 * 1024, (long)odd.Width * odd.Height);
            Assert.InRange(odd.ScaleX, FenSvgRenderer.MinRasterClampScale, 1d);
            Assert.InRange(odd.ScaleY, FenSvgRenderer.MinRasterClampScale, 1d);

            var tight = SvgRenderLimits.Normalize(ClampBudget(1));
            Assert.False(
                FenSvgRenderer.RasterPlan.TryCreate(1d, 3d, tight, out var refused, out var why));
            Assert.Equal(FenSvgRenderer.RasterRefusal.ReductionTooLarge, why);
            Assert.Equal(0, refused.Width);
            Assert.Equal(0, refused.Height);

            var roomy = SvgRenderLimits.Normalize(ClampBudget(6000));
            Assert.True(
                FenSvgRenderer.RasterPlan.TryCreate(3d, 5669d, roomy, out var tall, out _));
            Assert.True(tall.IsClamped);
            Assert.Equal(2, tall.Width);
            Assert.Equal(3000, tall.Height);
            Assert.Equal(6000L, (long)tall.Width * tall.Height);
            Assert.InRange(tall.ScaleX, FenSvgRenderer.MinRasterClampScale, 1d);
            Assert.InRange(tall.ScaleY, FenSvgRenderer.MinRasterClampScale, 1d);
        }

        [Fact]
        public void WptPatternViewboxDocument_RendersAtTheClampedRaster()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 30_000;

            using var result = _renderer.Render(PatternViewboxDocument(), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.Null(result.Picture);
            Assert.Equal(4096, result.Bitmap.Width);
            Assert.Equal(4096, result.Bitmap.Height);
            Assert.Equal(5669f, result.Width);
            Assert.Equal(5669f, result.Height);
            Assert.True(result.IsDownscaled);
            Assert.Equal(4096f / 5669f, result.RasterScaleX, 5);
            Assert.Equal(4096f / 5669f, result.RasterScaleY, 5);
            AssertClampWarning(result, 4096, 4096, 5669, 5669);
            AssertWithinBudget(result.Bitmap, limits);
            double coverage = ForegroundRatio(result.Bitmap);
            Assert.True(coverage > 0.01d && coverage < 0.5d, $"unexpected pattern coverage {coverage:F4}");
        }

        [Fact]
        public void WptPatternViewboxDocument_UnderStrictLimits_FailsClosed()
        {
            using var result = _renderer.Render(PatternViewboxDocument(), SvgRenderLimits.Strict);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
        }

        [Fact]
        public void NonSvgRoot_FailsGracefully()
        {
            using var result = _renderer.Render("<html><body>x</body></html>");
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
        }

        [Fact]
        public void AdversarialCorpus_NeverThrows_NeverHangs()
        {
            string[] corpus =
            {
                null,
                "   ",
                "not svg at all",
                "<<<<>>>>",
                "<svg",
                "<svg>",
                "</svg>",
                "<svg width=\"NaN\" height=\"NaN\"><rect/></svg>",
                "<svg width=\"-5\" height=\"-5\"><rect/></svg>",
                "<svg viewBox=\"garbage\"><rect d=\"M0 0\"/></svg>",
                "<svg width=\"10\" height=\"10\"><path d=\"M0 0A\"/></svg>",
                "<svg width=\"10\" height=\"10\"><path d=\"M1e30 1e30 L-1e30 -1e30\"/></svg>",
                "<svg width=\"10\" height=\"10\"><use href=\"#self\" id=\"self\"/><use href=\"#self\"/></svg>",
                "<svg width=\"10\" height=\"10\"><text>hello</text></svg>",
                "<svg width=\"10\" height=\"10\"><image href=\"http://evil/x.png\"/></svg>",
                "<svg width=\"10\" height=\"10\"><script>alert(1)</script><rect width=\"9\" height=\"9\"/></svg>",
                "<svg width=\"10\" height=\"10\"><style>rect{fill:url(http://x)}</style><rect width=\"9\" height=\"9\"/></svg>",
                "<?php exit(); ?><svg width=\"1\" height=\"1\"/>",
                "<svg width=\"10&#xZZ;\" height='unterminated><rect/></svg>",
                "<svg width=\"1e999\" height=\"10\"/>"
            };

            foreach (var input in corpus)
            {
                using var result = _renderer.Render(input);
                if (!result.Success)
                {
                    Assert.NotNull(result.ErrorMessage);
                }
            }
        }

        [Fact]
        public void StrictLimits_MoreRestrictiveThanDefault()
        {
            Assert.True(SvgRenderLimits.Strict.MaxRecursionDepth <= SvgRenderLimits.Default.MaxRecursionDepth);
            Assert.True(SvgRenderLimits.Strict.MaxFilterCount <= SvgRenderLimits.Default.MaxFilterCount);
            Assert.True(SvgRenderLimits.Strict.MaxRenderTimeMs <= SvgRenderLimits.Default.MaxRenderTimeMs);
            Assert.False(SvgRenderLimits.Default.AllowExternalReferences);
            Assert.False(SvgRenderLimits.Strict.AllowExternalReferences);
        }

        [Fact]
        public void StrictLimits_BoundCumulativeDecodedRasterPixelsAtOrBelowDefault()
        {
            Assert.True(
                SvgRenderLimits.Strict.MaxCumulativeDecodedImagePixels <=
                SvgRenderLimits.Default.MaxCumulativeDecodedImagePixels);
            Assert.True(SvgRenderLimits.Default.MaxCumulativeDecodedImagePixels > 0);
            Assert.Equal(
                64L * 1024 * 1024,
                SvgRenderLimits.Normalize(
                    new SvgRenderLimits { MaxCumulativeDecodedImagePixels = long.MaxValue })
                    .MaxCumulativeDecodedImagePixels);
        }

        [Fact]
        public void RepeatedIdenticalEmbeddedRasters_ShareOneDecodedSurface()
        {
            string uri = PngUri(SKColors.Red, seed: 1);
            var limits = SvgRenderLimits.Default;
            limits.MaxCumulativeDecodedImagePixels = 4;

            using var result = _renderer.Render(EightIdenticalImages(uri), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(15, 1));
        }

        [Fact]
        public void DistinctEmbeddedRasters_ObeyCumulativeDecodedPixelBudget()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxCumulativeDecodedImagePixels = 8;

            using var result = _renderer.Render(ThreeDistinctImages(out _), limits);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("resource-budget", result.ResourceRejectionReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("cumulative decoded raster budget"));
        }

        [Fact]
        public void DistinctEmbeddedRasters_AdmittedUnderTheSameBudgetRenderEveryImage()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxCumulativeDecodedImagePixels = 12;

            using var result = _renderer.Render(ThreeDistinctImages(out SKColor lastColor), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.Equal(lastColor, result.Bitmap.GetPixel(5, 1));
        }

        [Fact]
        public void RasterizationOverrun_FailsClosedWithoutPixels()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 1;

            using var result = _renderer.Render(ExpensiveEffectDocument(), limits);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.Contains("time limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void ExpensiveEffectDocument_StaysAdmissibleUnderAnUnboundedDeadline()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 30_000;

            using var result = _renderer.Render(ExpensiveEffectDocument(), limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(256, result.Bitmap.Width);
            Assert.True(result.Bitmap.GetPixel(128, 128).Alpha > 0);
        }

        private static string ExpensiveEffectDocument() =>
            "<svg width='256' height='256'>" +
            "<filter id='b' x='0' y='0' width='100%' height='100%'>" +
            "<feTurbulence type='fractalNoise' baseFrequency='0.03' numOctaves='6' seed='3'/>" +
            "<feGaussianBlur stdDeviation='12'/>" +
            "<feColorMatrix type='saturate' values='0.4'/>" +
            "</filter>" +
            "<rect width='256' height='256' fill='#3a7' filter='url(#b)'/></svg>";

        private static SvgRenderLimits ClampBudget(
            long maxRasterPixels, int maxWidth = 8192, int maxHeight = 8192)
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRasterPixels = maxRasterPixels;
            limits.MaxRasterWidth = maxWidth;
            limits.MaxRasterHeight = maxHeight;
            limits.MaxRenderTimeMs = 30_000;
            return limits;
        }

        private static string SolidDocument(int width, int height) =>
            $"<svg width='{width}' height='{height}'>" +
            $"<rect width='{width}' height='{height}' fill='#3a7'/></svg>";

        private static SKPicture RecordSplitPicture(SKRect cullRect)
        {
            using var recorder = new SKPictureRecorder();
            SKCanvas canvas = recorder.BeginRecording(cullRect);
            canvas.Clear(SKColors.Transparent);
            using (var paint = new SKPaint { Color = SKColors.Red, IsAntialias = false })
            {
                canvas.DrawRect(
                    new SKRect(cullRect.Left, cullRect.Top, cullRect.MidX, cullRect.Bottom),
                    paint);
            }
            using (var paint = new SKPaint { Color = SKColors.Lime, IsAntialias = false })
            {
                canvas.DrawRect(
                    new SKRect(cullRect.MidX, cullRect.Top, cullRect.Right, cullRect.Bottom),
                    paint);
            }
            return recorder.EndRecording();
        }

        private static SKBitmap DrawThroughAdapter(
            SKPicture picture, FenSvgRenderer.RasterPlan plan, SKRect cullRect)
        {
            var bitmap = new SKBitmap(plan.Width, plan.Height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                FenSvgRenderer.DrawRasterSurface(canvas, picture, plan, cullRect);
            }
            return bitmap;
        }

        private static bool Near(SKColor actual, SKColor expected) =>
            actual.Alpha == expected.Alpha &&
            actual.Red == expected.Red &&
            actual.Green == expected.Green &&
            actual.Blue == expected.Blue;

        private static string PatternViewboxDocument() =>
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5669 5669'>" +
            "<pattern width='2258.997' height='1289.823' patternUnits='userSpaceOnUse' id='pattern' " +
            "viewBox='752.999 0 2258.997 1289.823' patternTransform='scale(0.5)'>" +
            "<g transform='translate(0,1934.735)' fill='none' stroke='black' stroke-width='10'>" +
            "<rect x='752.999' y='-1934.735' width='2258.997' height='1289.823'/>" +
            "<circle cx='1882.4975' cy='-1289.8235' r='644.9115'/>" +
            "</g></pattern>" +
            "<rect width='100%' height='100%' fill='url(#pattern)'/></svg>";

        private static void AssertClampWarning(
            SvgRenderResult result, int width, int height, double naturalWidth, double naturalHeight)
        {
            string expected =
                $"SVG raster clamped to {width}x{height} pixels; natural bounds " +
                $"{naturalWidth:0}x{naturalHeight:0} exceed the raster pixel budget";
            Assert.Contains(result.Warnings, warning => warning == expected);
            Assert.True(result.Warnings.Count <= FenSvgRenderer.MaxResultDiagnosticEntries);
            foreach (string warning in result.Warnings)
            {
                Assert.True(warning.Length <= FenSvgRenderer.MaxResultDiagnosticChars);
            }
        }

        private static void AssertWithinBudget(SKBitmap bitmap, SvgRenderLimits limits)
        {
            Assert.InRange(bitmap.Width, 1, limits.MaxRasterWidth);
            Assert.InRange(bitmap.Height, 1, limits.MaxRasterHeight);
            Assert.InRange((long)bitmap.Width * bitmap.Height, 1L, limits.MaxRasterPixels);
        }

        private static double ForegroundRatio(SKBitmap bitmap)
        {
            long visible = 0;
            long sampled = 0;
            for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                sampled++;
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    visible++;
                }
            }

            return sampled == 0 ? 0d : (double)visible / sampled;
        }

        private static ulong Fingerprint(SKBitmap bitmap)
        {
            ulong checksum = 1469598103934665603UL;
            for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor color = bitmap.GetPixel(x, y);
                foreach (int channel in new[] { color.Alpha, color.Red, color.Green, color.Blue })
                {
                    checksum = (checksum ^ (byte)channel) * 1099511628211UL;
                }
            }

            return checksum;
        }

        private static string EightIdenticalImages(string uri) =>
            TiledDocument(Enumerable.Repeat(uri, 8).ToArray());

        private static string ThreeDistinctImages(out SKColor lastColor)
        {
            var uris = new[]
            {
                PngUri(SKColors.Red, seed: 1),
                PngUri(SKColors.Lime, seed: 2),
                PngUri(SKColors.Blue, seed: 3)
            };
            lastColor = SKColors.Blue;
            return TiledDocument(uris);
        }

        private static string TiledDocument(IReadOnlyList<string> uris) =>
            "<svg width='" + uris.Count * 2 + "' height='2'>" +
            string.Concat(uris.Select((uri, index) =>
                $"<image x='{index * 2}' y='0' width='2' height='2' href='{uri}'/>")) +
            "</svg>";

        private static string PngUri(SKColor color, byte seed) =>
            "data:image/png;base64," + Convert.ToBase64String(PngBytes(color, seed));

        private static byte[] PngBytes(SKColor color, byte seed)
        {
            using var bitmap = new SKBitmap(2, 2);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(color);
                canvas.DrawPoint(0, 0, new SKPaint { Color = new SKColor(seed, seed, seed) });
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
