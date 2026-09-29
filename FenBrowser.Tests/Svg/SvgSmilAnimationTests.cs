using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgSmilAnimationTests
    {
        private static SvgRenderResult RenderAt(string svg, double seconds) =>
            new FenSvgRenderer().Render(new SvgRenderRequest(svg, SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = seconds
            });

        [Theory]
        [InlineData(2.9, "red")]
        [InlineData(3.0, "green")]
        [InlineData(3.25, "green")]
        [InlineData(3.5, "red")]
        public void Set_AppliesOnlyDuringItsActiveInterval(double seconds, string expected)
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='red'>" +
                "<set attributeName='fill' to='green' begin='3s' dur='.5s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, seconds);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(expected == "green" ? SKColors.Green : SKColors.Red,
                result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void Set_FreezeRetainsValueAfterActiveInterval()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='red'>" +
                "<set attributeName='fill' to='green' begin='1s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 10d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void Animate_InterpolatesNumericGeometryAtDocumentTime()
        {
            const string svg =
                "<svg width='30' height='10'><rect x='0' width='5' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' begin='1s' dur='2s' fill='freeze'/>" +
                "</rect></svg>";

            using var middle = RenderAt(svg, 2d);
            using var frozen = RenderAt(svg, 4d);

            Assert.True(middle.Success, middle.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(middle.RequiresFallback, string.Join("; ", middle.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));
            Assert.Equal(SKColors.Green, middle.Bitmap.GetPixel(12, 5));
            Assert.Equal(0, middle.Bitmap.GetPixel(2, 5).Alpha);
            Assert.Equal(SKColors.Green, frozen.Bitmap.GetPixel(22, 5));
        }

        [Fact]
        public void AnimateColor_InterpolatesPresentationColor()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='10' height='10'>" +
                "<animateColor attributeName='fill' from='red' to='blue' dur='2s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(5, 5);
            Assert.InRange(center.Red, (byte)127, (byte)128);
            Assert.Equal((byte)0, center.Green);
            Assert.InRange(center.Blue, (byte)127, (byte)128);
        }

        [Fact]
        public void Animate_RemoveRestoresUnderlyingValueAfterInterval()
        {
            const string svg =
                "<svg width='30' height='10'><rect x='0' width='5' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 2d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(2, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(22, 5).Alpha);
        }

        [Fact]
        public void Animate_OutOfRangeSplineFailsClosed()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='5' height='10'>" +
                "<animate attributeName='width' from='0' to='10' dur='1s' " +
                "calcMode='spline' keySplines='0 0 1 4'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning => warning.Contains("spline"));
        }

        [Fact]
        public void Animate_SplineWithoutKeySplinesFailsClosed()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='5' height='10'>" +
                "<animate attributeName='width' values='0;5;10' dur='1s' calcMode='spline'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void Animate_SplineKeyTimesWithoutKeySplinesFailsClosed()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='5' height='10'>" +
                "<animate attributeName='width' values='0;5;10' keyTimes='0;.25;1' " +
                "keySplines='0 0 1 1' dur='1s' calcMode='spline'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void Animate_ValuesAndKeyTimesSelectCurrentSegment()
        {
            const string svg =
                "<svg width='30' height='10'><rect width='5' height='10' fill='green'>" +
                "<animate attributeName='x' values='0;20;10' keyTimes='0;.5;1' dur='4s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(17, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(2, 5).Alpha);
        }

        [Fact]
        public void Animate_RepeatCountSamplesCurrentIterationAndFrozenEnd()
        {
            const string svg =
                "<svg width='20' height='10'><rect width='4' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='10' dur='1s' " +
                "repeatCount='3' fill='freeze'/>" +
                "</rect></svg>";

            using var repeated = RenderAt(svg, 2.5d);
            using var frozen = RenderAt(svg, 4d);

            Assert.True(repeated.Success, repeated.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(repeated.RequiresFallback, string.Join("; ", repeated.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));
            Assert.Equal(SKColors.Green, repeated.Bitmap.GetPixel(6, 5));
            Assert.Equal(SKColors.Green, frozen.Bitmap.GetPixel(11, 5));
        }

        [Fact]
        public void Animate_DiscreteValuesHoldCurrentValue()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='10' height='10' fill='red'>" +
                "<animate attributeName='fill' values='red;green;blue' " +
                "calcMode='discrete' dur='3s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1.5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void Animate_LocalHrefTargetsReferencedElement()
        {
            const string svg =
                "<svg width='20' height='10'><rect id='target' width='4' height='10' fill='green'/>" +
                "<animate href='#target' attributeName='x' from='0' to='10' dur='2s'/>" +
                "</svg>";

            using var result = RenderAt(svg, 1d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(6, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(1, 5).Alpha);
        }

        [Fact]
        public void Set_LocalHrefTargetReceivesTheAnimatedValue()
        {
            const string svg =
                "<svg width='20' height='10'><rect id='target' width='10' height='10' fill='green'/>" +
                "<set href='#target' attributeName='fill' to='red' begin='0s' dur='1s'/></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1d)]
        public void InvalidDocumentTime_IsRejected(double seconds)
        {
            using var result = RenderAt("<svg width='1' height='1'/>", seconds);
            Assert.False(result.Success);
        }

        [Fact]
        public void FirstPartyRenderer_UsesNonZeroDocumentTime()
        {
            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                "<svg width='1' height='1'/>", SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = 1d
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(SvgRenderResult.IsAdmissible(result));
        }

        [Theory]
        [InlineData("by='270'")]
        [InlineData("to='300'")]
        [InlineData("from='30' by='270'")]
        [InlineData("from='30' to='300'")]
        public void RelativeAndImplicitFrom_ResolveAgainstTheUnderlyingValue(string spec)
        {
            string svg = "<svg width='200' height='10'><rect width='30' height='10' fill='green'>" +
                "<animate attributeName='width' " + spec + " begin='2s' dur='3s' fill='freeze'/>" +
                "</rect></svg>";

            using var middle = RenderAt(svg, 3.5d);
            using var frozen = RenderAt(svg, 6d);

            Assert.True(middle.Success, middle.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(middle.RequiresFallback, string.Join("; ", middle.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));
            Assert.Equal(SKColors.Green, middle.Bitmap.GetPixel(160, 5));
            Assert.Equal(0, middle.Bitmap.GetPixel(180, 5).Alpha);
            Assert.Equal(SKColors.Green, frozen.Bitmap.GetPixel(280, 5));
        }

        [Fact]
        public void SplineKeySplines_EaseTheSampledProgress()
        {
            const string svg = "<svg width='120' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' from='0' to='100' dur='10s' " +
                "calcMode='spline' keySplines='0 0 0 1'/>" +
                "</rect></svg>";

            using var eased = RenderAt(svg, 2.5d);
            using var linear = RenderAt(svg, 2.5d);

            Assert.True(eased.Success, eased.ErrorMessage);
            Assert.False(eased.RequiresFallback, string.Join("; ", eased.Warnings));
            Assert.Equal(SKColors.Green, eased.Bitmap.GetPixel(74, 10));
            Assert.Equal(0, eased.Bitmap.GetPixel(30, 10).Alpha);
        }

        [Fact]
        public void PacedScalar_IgnoresKeyTimesAndHoldsAConstantRate()
        {
            const string svg = "<svg width='320' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='width' calcMode='paced' values='300;255;180;30' " +
                "keyTimes='0;.25;.5;1' dur='9s' fill='freeze'/>" +
                "</rect></svg>";

            using var paced = RenderAt(svg, 4.5d);

            Assert.True(paced.Success, paced.ErrorMessage);
            Assert.False(paced.RequiresFallback, string.Join("; ", paced.Warnings));
            Assert.Equal(SKColors.Green, paced.Bitmap.GetPixel(160, 10));
            Assert.Equal(0, paced.Bitmap.GetPixel(185, 10).Alpha);
        }

        [Fact]
        public void EnumeratedValues_SampleWithoutInterpolation()
        {
            const string svg = "<svg width='20' height='20'><rect width='20' height='20' fill='green'>" +
                "<animate attributeName='display' values='inline; none' calcMode='discrete' dur='2s'/>" +
                "</rect></svg>";

            using var shown = RenderAt(svg, .5d);
            using var hidden = RenderAt(svg, 1.5d);

            Assert.Equal(SKColors.Green, shown.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, hidden.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void VisibilityValues_SampleWithoutInterpolation()
        {
            const string svg = "<svg width='20' height='20'><rect width='20' height='20' fill='green'>" +
                "<animate attributeName='visibility' values='visible; hidden; visible' " +
                "keyTimes='0; .5; 1' dur='2s'/>" +
                "</rect></svg>";

            using var shown = RenderAt(svg, .4d);
            using var hidden = RenderAt(svg, 1d);

            Assert.Equal(SKColors.Green, shown.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, hidden.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Theory]
        [InlineData(0.1d, true)]
        [InlineData(3d, false)]
        [InlineData(4.1d, true)]
        public void BeginClockList_ReplaysTheSetAtEachListedInstant(double seconds, bool applied)
        {
            const string svg = "<svg width='20' height='20'><rect width='20' height='20' fill='red'>" +
                "<set attributeName='fill' to='green' begin='0s;4s' dur='.2s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, seconds);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(
                applied ? SKColors.Green : SKColors.Red,
                result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void CurrentColor_ResolvesAgainstTheTargetInheritedColor()
        {
            const string svg = "<svg width='10' height='10'>" +
                "<rect width='10' height='10' color='red' fill='currentColor'>" +
                "<animate attributeName='fill' from='currentColor' to='blue' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(5, 5);
            Assert.InRange(center.Red, (byte)127, (byte)128);
            Assert.Equal((byte)0, center.Green);
            Assert.InRange(center.Blue, (byte)127, (byte)128);
        }

        [Fact]
        public void AdditiveSum_ComposesTheSampleOntoTheUnderlyingColor()
        {
            const string svg = "<svg width='10' height='10'><rect width='10' height='10' fill='#404040'>" +
                "<animate attributeName='fill' from='#000000' to='#c0c0c0' additive='sum' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(5, 5);
            Assert.InRange(center.Red, (byte)159, (byte)161);
        }

        [Fact]
        public void PointsValues_InterpolateComponentwise()
        {
            const string svg = "<svg width='60' height='20'>" +
                "<polygon points='0,10 10,0 20,10 10,20' fill='green'>" +
                "<animate attributeName='points' from='0,10 10,0 20,10 10,20' " +
                "to='20,10 30,0 40,10 30,20' dur='1s'/>" +
                "</polygon></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(20, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(5, 10).Alpha);
        }

        [Fact]
        public void PointsValues_WithMismatchedCountsFallBackToADiscreteSample()
        {
            const string svg = "<svg width='60' height='20'>" +
                "<polygon points='0,10 10,0 20,10 10,20' fill='green'>" +
                "<animate attributeName='points' from='0,10 10,0 20,10 10,20' to='40,10' dur='1s'/>" +
                "</polygon></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(30, 10).Alpha);
        }

        [Fact]
        public void ViewBox_AnimatesAsANumberList()
        {
            const string svg = "<svg width='40' height='40' viewBox='0 0 40 40'>" +
                "<rect x='30' y='0' width='10' height='40' fill='green'/>" +
                "<animate attributeName='viewBox' from='0 0 40 40' to='0 0 20 20' dur='1s'/>" +
                "</svg>";

            using var start = RenderAt(svg, 0d);
            using var halfway = RenderAt(svg, .5d);

            Assert.Equal(SKColors.Green, start.Bitmap.GetPixel(35, 20));
            Assert.Equal(0, halfway.Bitmap.GetPixel(35, 20).Alpha);
        }

        [Fact]
        public void TrailingSemicolonInValuesIsIgnored()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' values='0;20;' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(15, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(5, 10).Alpha);
        }

        [Fact]
        public void AccumulateSum_IsAdmittedWhenOnlyOneIterationCanRun()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='width' from='0' to='20' repeatCount='1' accumulate='sum' " +
                "dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 10).Alpha);
        }

        [Fact]
        public void AccumulateSum_AcrossIterationsFailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='width' from='0' to='20' repeatCount='2' accumulate='sum' " +
                "dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1.5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void UnboundedTimingLists_FailClosed()
        {
            string begins = string.Join(";",
                System.Linq.Enumerable.Range(0, MaxSmilTimingListEntriesProbe + 1)
                    .Select(_ => "1s"));
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' begin='" + "" + "' dur='1s'/>" +
                "</rect></svg>";
            string document = svg.Replace("begin=''", "begin='" + begins + "'");

            using var result = RenderAt(document, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void FutureInterval_DoesNotDemandValueSupportItCannotUse()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' from='0' to='not-a-length' begin='10s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void LiveIntervalWithAnUnusableValue_FailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' from='0' to='not-a-length' begin='0s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("class")]
        [InlineData("href")]
        [InlineData("xlink:href")]
        [InlineData("style")]
        public void StructurallyUnsupportedAttributes_FailClosedWhenTheIntervalIsLive(
            string attributeName)
        {
            string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='" + attributeName + "' to='other' begin='0s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("class")]
        [InlineData("href")]
        [InlineData("xlink:href")]
        [InlineData("style")]
        public void StructurallyUnsupportedAttributes_AreIrrelevantOutsideTheirInterval(
            string attributeName)
        {
            string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='" + attributeName + "' to='other' begin='10s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void FutureSet_OnAClassTarget_DoesNotSelectTheRuleItWouldHaveSet()
        {
            const string svg =
                "<svg width='20' height='20' fill='blue'><style>.v { fill: green }</style>" +
                "<g><set attributeName='class' to='v' begin='1s'/>" +
                "<rect width='20' height='20'/></g></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureSet_FreezeOutsideItsIntervalDoesNotResurrectTheValue()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'>" +
                "<set attributeName='class' to='v' begin='1s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureClassAnimation_LeavesTheBaseFrameUntouched()
        {
            const string svg =
                "<svg width='20' height='20'><circle cx='10' cy='10' r='8' fill='blue' class='start'>" +
                "<set attributeName='class' attributeType='XML' to='midway' begin='2s' dur='2s' " +
                "fill='freeze'/>" +
                "<animate attributeName='class' attributeType='XML' from='midway' " +
                "to='final midway' begin='3s' dur='4s' fill='freeze'/>" +
                "</circle></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureUnclassifiableSet_LeavesTheFrameUntouched()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'>" +
                "<set attributeName='in' to='SourceGraphic' begin='3s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureSet_WithUnusableComposition_LeavesTheFrameUntouched()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'>" +
                "<set attributeName='class' to='v' attributeType='bogus' begin='1s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureAnimate_WithUnusableComposition_LeavesTheFrameUntouched()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'>" +
                "<animate attributeName='x' from='0' to='20' additive='multiply' " +
                "begin='10s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureAnimateTransform_OnANonTransformableTarget_LeavesTheFrameUntouched()
        {
            const string svg = "<svg width='20' height='20'><filter id='f'><feFlood flood-color='green'/>" +
                "<animateTransform attributeName='transform' type='translate' from='0' to='10' " +
                "begin='10s' dur='1s'/>" +
                "</filter></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void FutureImageHrefSet_LeavesTheFrameUntouched()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'/>" +
                "<image width='10' height='10'>" +
                "<set attributeName='xlink:href' to='other.png' begin='2s'/>" +
                "</image></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 15));
        }

        [Fact]
        public void LiveClassAnimation_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='blue'>" +
                "<set attributeName='class' to='v' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains(result.Warnings, warning => warning.Contains("set target or attribute"));
        }

        [Fact]
        public void LiveHrefSet_OnADataUrl_ResolvesToNoElement()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use>" +
                "<set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r'/>" +
                "</use></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            Assert.Contains(result.Warnings, warning => warning.Contains("data: URL"));
            Assert.DoesNotContain(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
            foreach (string warning in result.Warnings)
                Assert.DoesNotContain("base64", warning, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LiveHrefSet_OnARelativeValue_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use>" +
                "<set attributeName='href' to='other.svg#r'/>" +
                "</use></svg>";

            using var result = RenderAt(svg, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
        }

        [Fact]
        public void LiveHrefSet_OnACrossOriginValue_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use>" +
                "<set attributeName='href' to='https://other.example/x.svg#r'/>" +
                "</use></svg>";

            using var result = RenderAt(svg, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
        }

        [Fact]
        public void LiveHrefSet_OnADataUrl_TargetingANonUse_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'>" +
                "<set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference target"));
        }

        [Fact]
        public void LiveSet_OnAUseHrefToAFragment_RepointsTheInstance()
        {
            const string svg =
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'><defs>" +
                "<rect id='source' width='20' height='20' fill='blue'/>" +
                "<rect id='target' width='20' height='20' fill='green'/>" +
                "</defs><use xlink:href='#source'>" +
                "<set attributeName='xlink:href' to='#target' dur='1s'/></use></svg>";

            using var live = RenderAt(svg, .5d);
            using var after = RenderAt(svg, 1.5d);

            Assert.True(live.Success, live.ErrorMessage);
            Assert.True(after.Success, after.ErrorMessage);
            Assert.False(live.RequiresFallback, string.Join("; ", live.Warnings));
            Assert.False(after.RequiresFallback, string.Join("; ", after.Warnings));
            Assert.Equal(SKColors.Green, live.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Blue, after.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void LiveSet_OnAUseHref_OverridesAStaticHrefOfTheSameName()
        {
            const string svg =
                "<svg width='20' height='20'><defs>" +
                "<rect id='source' width='20' height='20' fill='blue'/>" +
                "<rect id='target' width='20' height='20' fill='green'/>" +
                "</defs><use href='#source'>" +
                "<set attributeName='href' to='#target' dur='1s'/></use></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void FutureSet_OnAUseHref_LeavesTheInstanceAlone()
        {
            const string svg =
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'><defs>" +
                "<rect id='source' width='20' height='20' fill='blue'/>" +
                "<rect id='target' width='20' height='20' fill='green'/>" +
                "</defs><use xlink:href='#source'>" +
                "<set attributeName='xlink:href' to='#target' begin='1s' dur='1s'/>" +
                "</use></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void LiveHrefSet_OnAnAnchor_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><a href='#x'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<set attributeName='href' to='#y' dur='1s'/></a></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference target"));
        }

        [Fact]
        public void LiveHrefSet_ShadowedByAStaticHref_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><use href='#a' xlink:href='#b'>" +
                "<set attributeName='xlink:href' to='#c' dur='1s'/></use>" +
                "<rect id='a' width='4' height='4' fill='blue'/>" +
                "<rect id='b' width='4' height='4' fill='blue'/>" +
                "<rect id='c' width='4' height='4' fill='blue'/></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference precedence"));
        }

        [Fact]
        public void AnimationEventHandler_FailsClosedAtParseTime()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='display' values='inline; none' calcMode='discrete' " +
                "dur='1s' onbegin='f()'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning => warning.Contains("event handler"));
        }

        [Fact]
        public void PacedOnAnEnumeratedAttribute_FailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='display' values='inline; none' calcMode='paced' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void SyncbaseBegin_FailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='other.end+1s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void SelfReferentialSyncbase_KeepsTheOtherBeginValue()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='0s; self.end+1s' dur='1s' id='self'/>" +
                "</rect></svg>";

            using var live = RenderAt(svg, .5d);
            using var after = RenderAt(svg, 1.5d);

            Assert.True(live.Success, live.ErrorMessage);
            Assert.True(after.Success, after.ErrorMessage);
            Assert.False(live.RequiresFallback, string.Join("; ", live.Warnings));
            Assert.False(after.RequiresFallback, string.Join("; ", after.Warnings));
            Assert.Equal(SKColors.Red, live.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Green, after.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void CrossElementSyncbaseEnd_ResolvesTheReferencedInterval()
        {
            const string svg = "<svg width='60' height='20'>" +
                "<rect id='base' width='10' height='20' fill='blue'>" +
                "<set id='baseSet' attributeName='fill' to='blue' begin='2s' dur='1s'/>" +
                "</rect>" +
                "<rect x='50' width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='baseSet.end+1s' dur='1s'/>" +
                "</rect></svg>";

            using var before = RenderAt(svg, 1d);
            using var inside = RenderAt(svg, 4.5d);
            using var after = RenderAt(svg, 5.5d);

            Assert.True(before.Success, before.ErrorMessage);
            Assert.True(inside.Success, inside.ErrorMessage);
            Assert.True(after.Success, after.ErrorMessage);
            Assert.False(before.RequiresFallback, string.Join("; ", before.Warnings));
            Assert.False(inside.RequiresFallback, string.Join("; ", inside.Warnings));
            Assert.False(after.RequiresFallback, string.Join("; ", after.Warnings));
            Assert.Equal(SKColors.Green, before.Bitmap.GetPixel(55, 10));
            Assert.Equal(SKColors.Red, inside.Bitmap.GetPixel(55, 10));
            Assert.Equal(SKColors.Green, after.Bitmap.GetPixel(55, 10));
        }

        [Fact]
        public void CrossElementSyncbaseWithANegativeOffset_FailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set id='baseSet' attributeName='x' to='0' begin='1s' dur='1s'/>" +
                "<set attributeName='fill' to='red' begin='baseSet.end-3s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void SyncbaseCycle_FailsClosed()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set id='a' attributeName='fill' to='red' begin='b.end+1s' dur='1s'/>" +
                "<set id='b' attributeName='fill' to='red' begin='a.end+1s' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 0d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void EventBasedBegin_LeavesThePreEventFrame()
        {
            const string svg = "<svg width='40' height='20'>" +
                "<rect id='trigger' width='10' height='20' fill='green'/>" +
                "<rect x='20' width='10' height='20' fill='blue'>" +
                "<set attributeName='fill' to='red' begin='trigger.mouseover' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(25, 10));
        }

        [Fact]
        public void EventBasedBegin_WithAClockSibling_StillAppliesTheClock()
        {
            const string svg = "<svg width='40' height='20'>" +
                "<rect width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='mouseover; 0s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void EventBasedEnd_RunsTheIntervalForTheWholeDuration()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='0s' end='mouseout' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void IndefiniteBegin_WaitsForBeginElementAndDoesNotStart()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set attributeName='fill' to='red' begin='indefinite' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
        }

        [Fact]
        public void SyncbaseToAnElementThatNeverBegins_LeavesTheDependencyPending()
        {
            const string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<set id='gate' attributeName='fill' to='blue' begin='indefinite' dur='1s'/>" +
                "<set attributeName='fill' to='red' begin='gate.end+1s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 10));
        }

        [Theory]
        [InlineData("load")]
        [InlineData("unload")]
        [InlineData("resize")]
        [InlineData("scroll")]
        [InlineData("zoom")]
        [InlineData("error")]
        [InlineData("notAnEventAtAll")]
        public void DocumentLevelAndUnrecognisedEvents_FailClosed(string eventName)
        {
            string svg = "<svg width='40' height='20'>" +
                "<rect id='target' width='10' height='20' fill='green'/>" +
                "<set xlink:href='#target' attributeName='fill' to='red' begin='target." +
                eventName + "' dur='1s'/></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void FontSize_IsAnAnimatableScalarLength()
        {
            const string svg = "<svg width='40' height='20'>" +
                "<g font-size='10'><text x='0' y='15'>W</text>" +
                "<animate attributeName='font-size' from='10' to='30' dur='1s' fill='freeze'/></g>" +
                "</svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void LinearPathDataAnimation_FailsClosedInsteadOfStepping()
        {
            const string svg = "<svg width='40' height='20'><path d='M0 0h10v10z' fill='green'>" +
                "<animate attributeName='d' from='M0 0h10v10z' to='M0 0h20v20z' dur='1s'/>" +
                "</path></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void DiscretePathDataAnimation_SamplesTheSelectedPath()
        {
            const string svg = "<svg width='40' height='20'><path fill='green' d='M0 0H10V10H0Z'>" +
                "<animate attributeName='d' values='M0 0H10V10H0Z;M20 0H30V10H20Z' " +
                "calcMode='discrete' dur='2s'/>" +
                "</path></svg>";

            using var first = RenderAt(svg, .5d);
            using var second = RenderAt(svg, 1.5d);

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.False(second.RequiresFallback, string.Join("; ", second.Warnings));
            Assert.Equal((byte)255, first.Bitmap.GetPixel(5, 5).Alpha);
            Assert.Equal(0, first.Bitmap.GetPixel(25, 5).Alpha);
            Assert.Equal((byte)255, second.Bitmap.GetPixel(25, 5).Alpha);
            Assert.Equal(0, second.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void UnboundedBeginEndCrossProduct_FailsClosed()
        {
            string begins = string.Join(";",
                System.Linq.Enumerable.Range(0, 128).Select(_ => "0s"));
            string ends = string.Join(";",
                System.Linq.Enumerable.Range(0, 128).Select(_ => "1s"));
            string svg = "<svg width='40' height='20'><rect width='10' height='20' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' begin='" + begins + "' end='" + ends +
                "' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        private const int MaxSmilTimingListEntriesProbe = 256;

        private static void AssertAnimatedTransformMatches(
            string animated,
            string expectedTransform,
            double seconds,
            string wrapper = null)
        {
            string open = wrapper ?? "<g>";
            string animatedDocument =
                "<svg width='120' height='120'>" + open +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'>" +
                animated +
                "</rect></g></svg>";
            string referenceDocument =
                "<svg width='120' height='120'>" + open +
                "<rect x='-15' y='-15' width='30' height='30' fill='green' transform='" +
                expectedTransform + "'/></g></svg>";

            using var actual = RenderAt(animatedDocument, seconds);
            using var expected = new FenSvgRenderer().Render(referenceDocument);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(expected.RequiresFallback, string.Join("; ", expected.Warnings));
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void AnimateTransform_RotateInterpolatesTheAngle()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='rotate' " +
                "from='45' to='90' dur='3s' additive='replace' fill='freeze'/>",
                "rotate(67.5)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_RotateInterpolatesTheRotationCenter()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='rotate' " +
                "from='45,0,0' to='90,-15,-15' dur='3s' additive='replace' fill='freeze'/>",
                "rotate(67.5,-7.5,-7.5)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_LinearTranslationUsesEvenlySpacedSegments()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='translate' " +
                "values='-40,40;-20,-20;40,-40' dur='3s' calcMode='linear' additive='replace' " +
                "fill='freeze'/>",
                "translate(-20,-20)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_PacedTranslationReachesTheMiddleValue()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='translate' " +
                "values='-40,40;-20,20;40,-40' dur='3s' calcMode='paced' additive='replace' " +
                "fill='freeze'/>",
                "translate(0,0)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_PacedScaleAdvancesOnlyTheVaryingComponent()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='scale' " +
                "values='1,1;3,1;1,1' dur='3s' calcMode='paced' fill='freeze'/>",
                "scale(3,1)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_PacedRotationRunsEveryComponentAtItsOwnRate()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='rotate' " +
                "values='0,0,0;45,-15,-20;180,30,50' dur='3s' calcMode='paced' additive='replace' " +
                "fill='freeze'/>",
                "rotate(90,0,5)",
                1.5d);

            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' attributeType='XML' type='rotate' " +
                "values='0,0,0;45,-15,-20;180,30,50' dur='3s' calcMode='linear' additive='replace' " +
                "fill='freeze'/>",
                "rotate(45,-15,-20)",
                1.5d);
        }

        [Fact]
        public void AnimateTransform_SkewInterpolatesTheAngle()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='skewX' values='0;45;-45;0' " +
                "dur='3s'/>",
                "skewX(22.5)",
                .5d);

            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='skewY' values='0;30;-30;0' " +
                "dur='3s'/>",
                "skewY(15)",
                .5d);
        }

        [Fact]
        public void AnimateTransform_MatrixInterpolatesComponentwise()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='matrix' " +
                "from='1 0 0 1 0 0' to='2 0 0 1 10 0' dur='2s'/>",
                "matrix(1.5 0 0 1 5 0)",
                1d);
        }

        [Fact]
        public void AnimateTransform_ReplaceDiscardsTheUnderlyingTransform()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='rotate' from='0' to='90' dur='2s'/>",
                "rotate(45)",
                1d,
                "<g transform='translate(30,10)'>");
        }

        [Fact]
        public void AnimateTransform_AdditiveSumKeepsTheUnderlyingTransform()
        {
            string animatedDocument =
                "<svg width='120' height='120'><g transform='translate(20,0)'>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'>" +
                "<animateTransform attributeName='transform' type='translate' from='0 0' to='140 0' " +
                "additive='sum' dur='3s' fill='freeze'/></rect></g></svg>";
            string referenceDocument =
                "<svg width='120' height='120'>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green' " +
                "transform='translate(90,0)'/></svg>";

            using var actual = RenderAt(animatedDocument, 1.5d);
            using var expected = new FenSvgRenderer().Render(referenceDocument);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(expected.RequiresFallback, string.Join("; ", expected.Warnings));
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void AnimateTransform_AdditiveAnimationsComposeInDocumentOrder()
        {
            string svg = "<svg width='120' height='120'>" +
                "<g transform='translate(10,0)'><g>" +
                "<animateTransform attributeName='transform' type='translate' from='0 0' to='100 0' dur='2s'/>" +
                "<animateTransform attributeName='transform' type='scale' from='1 1' to='0.5 1' " +
                "additive='sum' dur='2s'/>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'/>" +
                "</g></g></svg>";
            string reference = "<svg width='120' height='120'>" +
                "<g transform='translate(10,0)'><g>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green' " +
                "transform='translate(50,0) scale(0.75,1)'/>" +
                "</g></g></svg>";

            using var actual = RenderAt(svg, 1d);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void AnimateTransform_DiscreteTranslationHoldsEachValue()
        {
            const string animation =
                "<animateTransform attributeName='transform' type='translate' " +
                "values='0;30;60' calcMode='discrete' dur='3s' fill='freeze' additive='sum'/>";

            AssertAnimatedTransformMatches(animation, "translate(0,0)", .5d);
            AssertAnimatedTransformMatches(animation, "translate(30,0)", 1.5d);
            AssertAnimatedTransformMatches(animation, "translate(60,0)", 2.5d);
        }

        [Fact]
        public void AnimateTransform_KeyTimesSelectTheCurrentSegment()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='translate' " +
                "values='0;40;100' keyTimes='0;.25;1' dur='4s' fill='freeze'/>",
                "translate(80,0)",
                3d);
        }

        [Fact]
        public void AnimateTransform_RepeatCountSamplesTheCurrentIteration()
        {
            AssertAnimatedTransformMatches(
                "<animateTransform attributeName='transform' type='translate' from='0' to='10' " +
                "dur='1s' repeatCount='3' fill='freeze'/>",
                "translate(5,0)",
                2.5d);
        }

        [Fact]
        public void AnimateTransform_ByIntervalStartsFromTheIdentityOfItsType()
        {
            const string animation =
                "<animateTransform attributeName='transform' type='translate' by='20 0' begin='1s' " +
                "dur='3s' fill='freeze'/>";
            const string document =
                "<svg width='120' height='120'><g transform='translate(10,0)'>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'>" + animation +
                "</rect></g></svg>";

            using var atStart = RenderAt(document, 1d);
            using var frozen = RenderAt(document, 4d);

            Assert.True(atStart.Success, atStart.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(atStart.RequiresFallback, string.Join("; ", atStart.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));

            AssertAnimatedTransformMatches(animation, "translate(0,0)", 1d,
                "<g transform='translate(10,0)'>");
            AssertAnimatedTransformMatches(animation, "translate(20,0)", 4d,
                "<g transform='translate(10,0)'>");
        }

        [Fact]
        public void AnimateTransform_HrefTargetsAnotherElement()
        {
            string svg = "<svg width='120' height='120'><g id='target'>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'/></g>" +
                "<animateTransform xlink:href='#target' xmlns:xlink='http://www.w3.org/1999/xlink' " +
                "attributeName='transform' type='rotate' values='0;360;180;360' dur='3s'/>" +
                "</svg>";
            string reference = "<svg width='120' height='120'><g>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green' transform='rotate(270)'/>" +
                "</g></svg>";

            using var actual = RenderAt(svg, 2.5d);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void AnimateTransform_InsideAContainerIsNotAnUnsupportedElement()
        {
            const string svg = "<svg width='120' height='120'><g>" +
                "<rect x='-15' y='-15' width='30' height='30' fill='green'>" +
                "<animateTransform attributeName='transform' type='rotate' from='0' to='90' dur='1s'/>" +
                "</rect></g></svg>";

            using var result = RenderAt(svg, 0d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("attributeName='transform' type='flip' from='0' to='10' dur='1s'",
            "animateTransform type")]
        [InlineData("attributeName='transform' type='matrix' from='1 0 0 1 0' to='1 0 0 1 5 0' dur='1s'",
            "animateTransform values")]
        [InlineData("attributeName='transform' type='rotate' to='90' dur='1s'",
            "animateTransform values")]
        [InlineData("attributeName='transform' type='scale' from='1' to='2' by='1' dur='1s'",
            "animateTransform values")]
        [InlineData("attributeName='transform' type='translate' from='10px' to='20' dur='1s'",
            "animateTransform values")]
        [InlineData("attributeName='transform' type='skewX' from='0' to='0 0' dur='1s'",
            "animateTransform values")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' dur='indefinite'",
            "animate duration")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' repeatCount='2' " +
            "accumulate='sum' dur='1s'", "animateTransform accumulate mode")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' begin='absent.end' dur='1s'",
            "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' begin='t.end' dur='1s'",
            "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' begin='absent.repeat(1)' dur='1s'",
            "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' " +
            "begin='wallclock(2000-06-10T12:34:56Z)' dur='1s'", "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' begin='absent.load' dur='1s'",
            "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' begin='t.begin' dur='1s'",
            "animate begin timing")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' min='0s' dur='1s'",
            "advanced animate timing")]
        [InlineData("attributeName='transform' type='translate' values='0;10;20' " +
            "keyTimes='0;.2;.5' dur='1s'", "animateTransform key times")]
        [InlineData("attributeName='transform' type='translate' values='0;10;20' " +
            "keySplines='0 0 1 1' calcMode='spline' dur='1s'", "animateTransform key splines")]
        [InlineData("attributeName='transform' type='translate' values='0;10' calcMode='ease' dur='1s'",
            "animateTransform calculation mode")]
        [InlineData("attributeName='x' type='translate' from='0' to='10' dur='1s'",
            "animateTransform target or attribute")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' " +
            "attributeType='CSS' dur='1s'", "animateTransform target or attribute")]
        [InlineData("attributeName='transform' type='translate' from='0' to='10' " +
            "additive='multiply' dur='1s'", "animateTransform target or attribute")]
        [InlineData("href='https://example.invalid/x.svg#t' attributeName='transform' " +
            "type='translate' from='0' to='10' dur='1s'", "external target")]
        public void AnimateTransform_UnsupportedCompositionFailsClosed(string animation, string reason)
        {
            string svg = "<svg width='40' height='40'><rect id='t' width='10' height='10' fill='green'>" +
                "<animateTransform " + animation + "/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning => warning.Contains(reason));
            if (result.HadResourceRejection)
            {
                Assert.NotEmpty(result.ResourceRejectionReasonCodes);
                return;
            }
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        [Fact]
        public void AnimateTransform_SelfReferentialSyncbaseKeepsTheOtherBeginValue()

        {
            const string svg = "<svg width='40' height='40'><rect width='10' height='10' fill='green'>" +
                "<animateTransform attributeName='transform' id='firstSet' type='translate' " +
                "from='30,0' to='30,0' begin='0s; firstSet.end + 1s' dur='1s'/>" +
                "</rect></svg>";

            using var live = RenderAt(svg, .5d);
            using var after = RenderAt(svg, 1.5d);

            Assert.True(live.Success, live.ErrorMessage);
            Assert.True(after.Success, after.ErrorMessage);
            Assert.False(live.RequiresFallback, string.Join("; ", live.Warnings));
            Assert.False(after.RequiresFallback, string.Join("; ", after.Warnings));
            Assert.Equal(SKColors.Green, live.Bitmap.GetPixel(32, 2));
            Assert.Equal((byte)0, after.Bitmap.GetPixel(32, 2).Alpha);
        }

        [Fact]
        public void AnimateTransform_EventBasedBeginLeavesThePreEventFrame()
        {
            const string svg = "<svg width='40' height='40'><rect width='10' height='10' fill='green'>" +
                "<animateTransform attributeName='transform' type='translate' from='0,0' to='30,0' " +
                "begin='click' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)0, result.Bitmap.GetPixel(32, 2).Alpha);
        }

        [Fact]
        public void AnimateTransform_EventHandlerFailsClosed()
        {
            const string svg = "<svg width='40' height='40'><rect width='10' height='10' fill='green'>" +
                "<animateTransform attributeName='transform' type='translate' from='0' to='10' " +
                "dur='1s' onbegin='f()'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning => warning.Contains("event handler"));
        }

        [Fact]
        public void AnimateTransform_AdditiveSumPostMultipliesOntoACssTransform()
        {
            const string animation =
                "<animateTransform attributeName='transform' type='translate' from='0' to='40' " +
                "additive='sum' dur='1s'/>";
            string animatedDocument =
                "<svg width='120' height='120'><style>rect{transform:translate(10px 0)}</style>" +
                "<g><rect x='-15' y='-15' width='30' height='30' fill='green'>" + animation +
                "</rect></g></svg>";
            string referenceDocument =
                "<svg width='120' height='120'><style>rect{transform:translate(30px 0)}</style>" +
                "<g><rect x='-15' y='-15' width='30' height='30' fill='green'/></g></svg>";

            using var actual = RenderAt(animatedDocument, .5d);
            using var expected = new FenSvgRenderer().Render(referenceDocument);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(expected.RequiresFallback, string.Join("; ", expected.Warnings));
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void AnimateTransform_ReplaceOwnsTheListOverACssTransform()
        {
            const string svg = "<svg width='40' height='40'><style>rect{transform:translate(5px 0)}</style>" +
                "<rect width='10' height='10' fill='green'>" +
                "<animateTransform attributeName='transform' type='translate' from='0' to='10' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)0, result.Bitmap.GetPixel(2, 2).Alpha);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(12, 2));
        }

        [Fact]
        public void AnimateTransform_TargetingANonTransformableElementFailsClosed()
        {
            const string svg = "<svg width='40' height='40'><filter id='f'><feFlood flood-color='green'/>" +
                "<animateTransform attributeName='transform' type='translate' from='0' to='10' dur='1s'/>" +
                "</filter></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning => warning.Contains("target element"));
        }

        [Fact]
        public void AnimateTransform_UnboundedValueListFailsClosed()
        {
            string values = string.Join(";",
                System.Linq.Enumerable.Range(0, MaxSmilTransformValueListEntriesProbe + 1)
                    .Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            using var result = RenderAt(
                "<svg width='40' height='40'><rect width='10' height='10' fill='green'>" +
                "<animateTransform attributeName='transform' type='translate' values='" + values +
                "' dur='1s'/></rect></svg>",
                .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
        }

        private const int MaxSmilTransformValueListEntriesProbe = 1024;

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
