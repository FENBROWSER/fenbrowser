using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public class SvgPathParserTests
    {
        [Fact]
        public void BasicCommands_ProduceExpectedBounds()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M0 0 L10 0 L10 10 Z".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                var b = path.Bounds;
                Assert.Equal(0f, b.Left);
                Assert.Equal(10f, b.Right);
                Assert.Equal(10f, b.Bottom);
            }
        }

        [Fact]
        public void ImplicitRepeat_AfterMoveTo_IsLineTo()
        {
            // "M0 0 5 5" == "M0 0 L5 5"
            Assert.True(SvgPathParser.TryBuildPath("M0 0 5 5".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                Assert.Equal(5f, path.Bounds.Right);
            }
        }

        [Fact]
        public void RelativeHorizontalAndVerticalCommands_UseCurrentPoint()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M10 10 h5 v7".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                Assert.Equal(10f, path.Bounds.Left);
                Assert.Equal(15f, path.Bounds.Right);
                Assert.Equal(10f, path.Bounds.Top);
                Assert.Equal(17f, path.Bounds.Bottom);
            }
        }

        [Fact]
        public void NumbersAfterClosePath_StopWithoutBurningSegmentBudget()
        {
            var report = new SvgParseReport();
            Assert.True(SvgPathParser.TryBuildPath(
                "M0 0 L5 5 Z 1 1".AsSpan(), out var path, report));
            path.Dispose();
            Assert.False(report.TruncatedPathData);
        }

        [Fact]
        public void LongPathInvokesCooperativeBudgetCheck()
        {
            var data = new System.Text.StringBuilder("M0 0");
            for (int i = 0; i < 300; i++) data.Append(" l1 0");
            int checks = 0;

            Assert.True(SvgPathParser.TryBuildPath(
                data.ToString().AsSpan(),
                out var path,
                new SvgParseReport(),
                () => checks++));
            path.Dispose();
            Assert.True(checks >= 1);
        }

        [Theory]
        [InlineData("M0 0 A10 10 0 0 1 20 0")]      // half circle sweep
        [InlineData("M0 0 A10 10 0 1 0 20 0")]      // large arc
        [InlineData("M0 0 A0 0 0 0 1 20 0")]        // zero radii -> line
        [InlineData("M5 5 A10 10 0 0 1 5 5")]       // coincident endpoints -> skip
        [InlineData("M0 0 A1 2 45 1 1 -30.5 12.75 e4")] // trailing junk stops parse
        public void Arcs_DegenerateCases_TerminateGracefully(string d)
        {
            var report = new SvgParseReport();
            Assert.True(SvgPathParser.TryBuildPath(d.AsSpan(), out var path, report));
            path.Dispose();
        }

        [Fact]
        public void Arc_RadiusScaling_WhenTooSmallForChord()
        {
            // Chord length 20 with radius 1: spec requires scaling radii up.
            var report = new SvgParseReport();
            Assert.True(SvgPathParser.TryBuildPath("M0 0 A1 1 0 0 1 20 0".AsSpan(), out var path, report));
            using (path)
            {
                Assert.True(path.Bounds.Width > 15f); // reached endpoint
            }
        }

        [Fact]
        public void SegmentBudget_TruncatesAndReports()
        {
            var sb = new System.Text.StringBuilder("M0 0");
            for (int i = 0; i < SvgPathParser.MaxSegments + 16; i++)
            {
                sb.Append(" L1 1");
            }

            var report = new SvgParseReport();
            Assert.True(SvgPathParser.TryBuildPath(sb.ToString().AsSpan(), out var path, report));
            path.Dispose();
            Assert.True(report.TruncatedPathData);
        }

        [Fact]
        public void HugeCoordinates_AreClamped()
        {
            var report = new SvgParseReport();
            Assert.True(SvgPathParser.TryBuildPath("M0 0 L1e30 1e30".AsSpan(), out var path, report));
            using (path)
            {
                Assert.True(path.Bounds.Right <= SvgValues.CoordClamp + 1f);
            }
        }

        [Fact]
        public void RelativeCommands_Compose()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M10 10 m5 5 l-3 0".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                // Points: (10,10) -> m -> (15,15) -> l -> (12,15).
                // SKPath tight bounds cover drawn segments only.
                var b = path.Bounds;
                Assert.Equal(12f, b.Left, 3);
                Assert.Equal(15f, b.Top, 3);
                Assert.Equal(15f, b.Right, 3);
                Assert.Equal(15f, b.Bottom, 3);
            }
        }

        [Theory]
        [InlineData("M 20 150 B -90 h 120 B 0 h 140 B 90 h 120 z")]
        [InlineData("M 20 150 b -90 h 120 b 90 h 140 b 90 h 120 z")]
        public void BearingCommands_RotateRelativePathCoordinates(string data)
        {
            Assert.True(SvgPathParser.TryBuildPath(
                data.AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                Assert.Equal(20f, path.Bounds.Left, 3);
                Assert.Equal(30f, path.Bounds.Top, 3);
                Assert.Equal(160f, path.Bounds.Right, 3);
                Assert.Equal(150f, path.Bounds.Bottom, 3);
            }
        }

        [Fact]
        public void Bearing_AppliesToRelativeCoordinatePairs()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M10 10 B90 l5 2".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                Assert.Equal(8f, path.Bounds.Left, 3);
                Assert.Equal(10f, path.Bounds.Top, 3);
                Assert.Equal(10f, path.Bounds.Right, 3);
                Assert.Equal(15f, path.Bounds.Bottom, 3);
            }
        }

        [Fact]
        public void ScientificNotation_AndSeparators_Parse()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M1e1 , 1E1L2e+1,-3.5e-1Z".AsSpan(), out var path, new SvgParseReport()));
            path.Dispose();
        }

        [Fact]
        public void RepeatedSmoothCubicGroups_ReflectThePreviousGroupControl()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M0 0 C10 0 10 0 20 0 S30 0 40 0 50 100 60 100".AsSpan(),
                out var path,
                new SvgParseReport()));
            using (path)
            {
                SKPoint[] points = path.GetPoints(path.PointCount);
                Assert.Equal(path.PointCount, points.Length);
                Assert.Equal(new SKPoint(50, 0), points[7]);
            }
        }

        [Fact]
        public void InvalidTokenAfterImplicitCommand_StopsAtParsedPrefix()
        {
            Assert.True(SvgPathParser.TryBuildPath(
                "M20 100 H40#90".AsSpan(), out var path, new SvgParseReport()));
            using (path)
            {
                SKPoint[] points = path.GetPoints(path.PointCount);
                Assert.Equal(new SKPoint(40, 100), points[^1]);
            }
        }
    }
}
