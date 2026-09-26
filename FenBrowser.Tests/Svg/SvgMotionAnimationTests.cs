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
        public void ExternalMPath_FailsClosedAsRejectedResource()
        {
            const string svg = "<svg width='20' height='20'><circle r='2'>" +
                "<animateMotion dur='1s'><mpath href='https://example.invalid/p.svg#p'/>" +
                "</animateMotion></circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void EventBasedBegin_FailsClosedAsExplicitUnsupported()
        {
            const string svg = "<svg width='20' height='20'><circle r='2'>" +
                "<animateMotion dur='1s' begin='click' path='M0 0L10 10'/>" +
                "</circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void AnimateMotion_UsesDocumentTimeForTheBoundedPathSnapshot()
        {
            const string animated =
                "<svg width='120' height='40'><path id='p' d='M0 20H100'/>" +
                "<circle r='5'><animateMotion dur='10s'><mpath href='#p'/></animateMotion></circle></svg>";
            const string expected =
                "<svg width='120' height='40'><circle cx='50' cy='20' r='5'/></svg>";

            using var actual = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = 5d });
            using var reference = new FenSvgRenderer().Render(expected);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(reference.Success, reference.ErrorMessage);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(reference.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void PacedMotion_OnAPlainPath_IsUniformArcLengthProgress()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion dur='10s' calcMode='paced' path='M0 20H100'/>" +
                "</rect></svg>";

            using var paced = new FenSvgRenderer().Render(new SvgRenderRequest(
                svg, SvgRenderLimits.Default) { DocumentTimeSeconds = 2.5d });
            const string reference =
                "<svg width='120' height='40'><rect x='21' y='16' width='8' height='8' fill='black'/></svg>";
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(paced.Success, paced.ErrorMessage);
            Assert.False(paced.RequiresFallback, string.Join("; ", paced.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            AssertIdenticalPixels(paced.Bitmap, expected.Bitmap);
        }

        [Fact]
        public void PacedMotion_WithKeyPoints_FailsClosedBecauseTheRateIsKeyTimeDependent()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='10s' calcMode='paced' " +
                "keyTimes='0;.5;1' keyPoints='0;1;0' path='M0 20H100'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                svg, SvgRenderLimits.Default) { DocumentTimeSeconds = 2.5d });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning => warning.Contains("paced"));
        }

        [Fact]
        public void MotionPath_FromToDefinesThePath()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion from='10,20' to='110,20' begin='0s' dur='3s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1.5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            AssertBlockAt(result.Bitmap, 60d, 20d);
        }

        [Fact]
        public void MotionPath_ValuesDefineThePath()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion values='10,20;60,20;110,20' begin='0s' dur='6s' " +
                "calcMode='linear' fill='freeze'/></rect></svg>";

            using var early = RenderAt(svg, 1.5d);
            using var late = RenderAt(svg, 4.5d);

            Assert.True(early.Success, early.ErrorMessage);
            Assert.True(late.Success, late.ErrorMessage);
            Assert.False(early.RequiresFallback, string.Join("; ", early.Warnings));
            Assert.False(late.RequiresFallback, string.Join("; ", late.Warnings));
            AssertBlockAt(early.Bitmap, 35d, 20d);
            AssertBlockAt(late.Bitmap, 85d, 20d);
        }

        [Fact]
        public void DiscreteValueList_StepsBetweenTheDeclaredPositions()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion values='10,20;60,20;110,20' begin='0s' dur='8s' " +
                "calcMode='discrete' fill='freeze'/></rect></svg>";

            using var firstHold = RenderAt(svg, 2.5d);
            using var secondHold = RenderAt(svg, 3.5d);

            AssertBlockAt(firstHold.Bitmap, 10d, 20d);
            AssertBlockAt(secondHold.Bitmap, 60d, 20d);
        }

        [Fact]
        public void PacedValueList_HoldsAConstantRateAcrossTheWholePath()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion values='10,20;60,20;110,20' begin='0s' dur='9s' " +
                "calcMode='paced' fill='freeze'/></rect></svg>";

            using var middle = RenderAt(svg, 4.5d);

            AssertBlockAt(middle.Bitmap, 60d, 20d);
        }

        [Fact]
        public void SplineMotion_EasesThePathProgressWithinItsSegment()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion from='10,20' to='110,20' begin='0s' dur='10s' " +
                "calcMode='spline' keySplines='0 0 0 1' fill='freeze'/></rect></svg>";

            using var eased = RenderAt(svg, 2.5d);

            AssertBlockAt(eased.Bitmap, 79d, 20d);
            Assert.Equal(0, eased.Bitmap.GetPixel(35, 20).Alpha);
        }

        [Fact]
        public void SplineMotion_LinearControlPointsMatchLinearProgress()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion from='10,20' to='110,20' begin='0s' dur='10s' " +
                "calcMode='spline' keySplines='0 0 1 1' fill='freeze'/></rect></svg>";

            using var eased = RenderAt(svg, 2.5d);

            AssertBlockAt(eased.Bitmap, 35d, 20d);
        }

        [Fact]
        public void KeyPoints_InterpolateOnAValueDerivedMotionPath()
        {
            const string svg = "<svg width='320' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion dur='4' from='20,22' to='220,22' calcMode='linear' " +
                "fill='freeze' keyPoints='0.8; 1; 0; 0.2' keyTimes='0; 0.25; 0.75; 1'/>" +
                "</rect></svg>";

            using var firstKeyPoint = RenderAt(svg, 0d);
            using var secondKeyTime = RenderAt(svg, 1.5d);
            using var thirdKeyTime = RenderAt(svg, 3d);

            AssertBlockAt(firstKeyPoint.Bitmap, 180d, 22d);
            AssertBlockAt(secondKeyTime.Bitmap, 170d, 22d);
            AssertBlockAt(thirdKeyTime.Bitmap, 20d, 22d);
        }

        [Fact]
        public void MotionBeginClockList_RepeatsThePathSnapshot()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<rect x='-4' y='-4' width='8' height='8' fill='black'>" +
                "<animateMotion from='10,20' to='110,20' begin='0s;4s' dur='3s' " +
                "fill='freeze'/></rect></svg>";

            using var firstRun = RenderAt(svg, 1.5d);
            using var betweenRuns = RenderAt(svg, 3.5d);
            using var secondRun = RenderAt(svg, 5.5d);

            AssertBlockAt(firstRun.Bitmap, 60d, 20d);
            AssertBlockAt(betweenRuns.Bitmap, 110d, 20d);
            AssertBlockAt(secondRun.Bitmap, 60d, 20d);
        }

        [Fact]
        public void MotionValuePath_WithoutValuesOrFromTo_FailsClosed()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='1s'/></circle></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void MotionValuePath_RejectsAValueListThatIsNotAPositionPair()
        {
            const string svg = "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='1s' values='0,0;10;20,0'/></circle></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        private static SvgRenderResult RenderAt(string svg, double seconds) =>
            new FenSvgRenderer().Render(new SvgRenderRequest(svg, SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = seconds
            });

        private static void AssertBlockAt(SkiaSharp.SKBitmap bitmap, double centerX, double centerY)
        {
            int x = (int)Math.Round(centerX);
            int y = (int)Math.Round(centerY);
            Assert.InRange(x, 0, bitmap.Width - 1);
            Assert.InRange(y, 0, bitmap.Height - 1);
            Assert.Equal((byte)255, bitmap.GetPixel(x, y).Alpha);
        }

        private static void AssertIdenticalPixels(SkiaSharp.SKBitmap actual, SkiaSharp.SKBitmap expected)
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        }

        [Theory]
        [InlineData("dur='1e308s' repeatCount='1000000'")]
        [InlineData("dur='1e-320s' repeatCount='1e-320'")]
        public void AnimateMotion_UnrepresentableTotalDurationFailsClosed(string timing)
        {
            string svg = "<svg width='20' height='20'><circle r='2'>" +
                $"<animateMotion {timing} path='M0 0H10'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                svg, SvgRenderLimits.Default) { DocumentTimeSeconds = 1d });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("keyPoints='.5;bad' keyTimes='0;1'")]
        [InlineData("keyPoints='.5;.5' keyTimes='0;.5;1'")]
        [InlineData("keyPoints='.5;.5'")]
        [InlineData("keyPoints='1.1;1.1' keyTimes='0;1'")]
        public void MalformedOrAmbiguousTimingLists_FailClosedAsExplicitUnsupported(string timing)
        {
            string svg = "<svg width='20' height='20'><circle r='2'>" +
                $"<animateMotion dur='1s' {timing} path='M0 0L10 10'/>" +
                "</circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("<mpath/>")]
        [InlineData("<mpath href=''/>")]
        [InlineData("<mpath href='#missing'/>")]
        public void MissingOrUnresolvedMPath_FailsClosedInsteadOfDisappearing(string mpath)
        {
            string svg = "<svg width='20' height='20'><path id='p' d='M0 0H10'/><circle r='2'>" +
                $"<animateMotion dur='1s'>{mpath}</animateMotion></circle></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void UnsupportedTarget_FailsClosedAsExplicitUnsupported()
        {
            const string svg = "<svg width='20' height='20'><g>" +
                "<animateMotion dur='1s' path='M0 0L10 10'/><circle r='2'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData(1.25d, 6.25d)]
        [InlineData(3.75d, 18.75d)]
        [InlineData(7.5d, 62.5d)]
        public void KeyPoints_InterpolateInsideTheSelectedKeyTimeSegment(
            double documentTime,
            double expectedCenterX)
        {
            const string animated =
                "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='10s' keyTimes='0;.5;1' keyPoints='0;.25;1' " +
                "path='M0 20H100'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = documentTime });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            AssertCircleCenteredAt(result.Bitmap, expectedCenterX, 20d, 5d);
        }

        [Theory]
        [InlineData(2.5d, 0d)]
        [InlineData(6d, 100d)]
        [InlineData(9d, 100d)]
        public void DiscreteKeyPoints_SelectTheActiveSegmentWithoutInterpolation(
            double documentTime,
            double expectedCenterX)
        {
            const string animated =
                "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='10s' calcMode='discrete' " +
                "keyTimes='0;.5;1' keyPoints='0;1;0' path='M0 20H100'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = documentTime });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            AssertCircleCenteredAt(result.Bitmap, expectedCenterX, 20d, 5d);
        }

        [Theory]
        [InlineData("fill='freeze'", 12d, 100d)]
        [InlineData("fill='freeze'", 45d, 100d)]
        [InlineData("fill='freeze' repeatCount='3'", 45d, 100d)]
        [InlineData("fill='freeze' keyTimes='0;.5;1' keyPoints='0;.5;1'", 12d, 100d)]
        [InlineData("fill='freeze' calcMode='discrete' keyTimes='0;.5;1' keyPoints='0;1;.5'", 12d, 50d)]
        public void FillFreeze_HoldsTheFinalKeyPointAfterDur(
            string timing,
            double documentTime,
            double expectedCenterX)
        {
            string animated =
                "<svg width='120' height='40'><circle r='5'>" +
                $"<animateMotion dur='10s' {timing} path='M0 20H100'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = documentTime });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            AssertCircleCenteredAt(result.Bitmap, expectedCenterX, 20d, 5d);
        }

        [Fact]
        public void FinishedWithoutFreeze_LeavesTheTargetUntransformed()
        {
            const string animated =
                "<svg width='120' height='40'><circle r='5'>" +
                "<animateMotion dur='10s' path='M0 20H100'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = 12d });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            AssertCircleCenteredAt(result.Bitmap, 0d, 0d, 5d);
        }

        private static void AssertCircleCenteredAt(
            SkiaSharp.SKBitmap bitmap,
            double centerX,
            double centerY,
            double radius)
        {
            int x = (int)Math.Round(centerX);
            int y = (int)Math.Round(centerY);
            int reach = (int)Math.Round(radius) + 2;
            Assert.Equal((byte)255, bitmap.GetPixel(x, y).Alpha);
            AssertTransparentOutside(bitmap, x + reach, y);
            AssertTransparentOutside(bitmap, x - reach, y);
            AssertTransparentOutside(bitmap, x, y + reach);
            AssertTransparentOutside(bitmap, x, y - reach);
        }

        private static void AssertTransparentOutside(SkiaSharp.SKBitmap bitmap, int x, int y)
        {
            if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
            {
                return;
            }
            Assert.Equal(0, bitmap.GetPixel(x, y).Alpha);
        }

        [Fact]
        public void RepeatCount_WrapsTheBoundedPathSnapshot()
        {
            const string animated =
                "<svg width='120' height='40'>" +
                "<circle r='5'><animateMotion dur='10s' repeatCount='3' " +
                "path='M0 20H100'/></circle></svg>";
            const string expected =
                "<svg width='120' height='40'><circle cx='50' cy='20' r='5'/></svg>";

            using var actual = new FenSvgRenderer().Render(new SvgRenderRequest(
                animated, SvgRenderLimits.Default) { DocumentTimeSeconds = 15d });
            using var reference = new FenSvgRenderer().Render(expected);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(reference.Success, reference.ErrorMessage);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(reference.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Theory]
        [InlineData("min='1s'")]
        [InlineData("max='2s'")]
        [InlineData("repeatDur='2s'")]
        [InlineData("calcMode='spline'")]
        [InlineData("begin='-1s'")]
        [InlineData("fill='hold'")]
        [InlineData("repeatCount='0'")]
        [InlineData("repeatCount='-2'")]
        [InlineData("repeatCount='1000001'")]
        public void UnsupportedAnimationComposition_FailsClosedAsExplicitUnsupported(string attribute)
        {
            string svg = "<svg width='20' height='20'><circle r='2'>" +
                $"<animateMotion dur='1s' {attribute} path='M0 0H10'/></circle></svg>";

            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                svg, SvgRenderLimits.Default) { DocumentTimeSeconds = .5d });

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
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
