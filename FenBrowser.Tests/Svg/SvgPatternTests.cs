using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgPatternTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void UserSpacePattern_RepeatsItsPictureTile()
        {
            using var result = _renderer.Render(
                "<svg width='40' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='10' height='10'><rect width='5' height='10' fill='red'/></pattern>" +
                "<rect width='40' height='20' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(2, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(7, 5).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(12, 5));
        }

        [Fact]
        public void UserSpacePattern_PaintsStrokeGeometry()
        {
            using var result = _renderer.Render(
                "<svg width='30' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='4' height='4'><rect width='4' height='4' fill='blue'/></pattern>" +
                "<rect x='3' y='3' width='24' height='14' fill='none' " +
                "stroke='url(#p)' stroke-width='4'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 3));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 10).Alpha);
        }

        [Fact]
        public void ObjectBoundingBoxContent_ResolvesAgainstPaintedGeometry()
        {
            using var result = _renderer.Render(
                "<svg width='30' height='20'><pattern id='p' width='1' height='1' " +
                "patternContentUnits='objectBoundingBox'><rect width='1' height='1' fill='lime'/>" +
                "</pattern><rect x='5' y='5' width='20' height='10' fill-opacity='.5' " +
                "fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(15, 10);
            Assert.True(center.Green > 200 && center.Alpha is >= 120 and <= 136, center.ToString());
        }

        [Fact]
        public void PatternTemplate_InheritsGeometryAndContent()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs>" +
                "<pattern id='base' patternUnits='userSpaceOnUse' width='5' height='5'>" +
                "<rect width='5' height='5' fill='green'/></pattern>" +
                "<pattern id='derived' href='#base'/></defs>" +
                "<rect width='20' height='10' fill='url(#derived)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(17, 7));
        }

        [Fact]
        public void PatternTemplate_MetadataOnlyDerivedInheritsRenderableContent()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs>" +
                "<pattern id='base' patternUnits='userSpaceOnUse' width='10' height='10'>" +
                "<rect width='10' height='10' fill='green'/></pattern>" +
                "<pattern id='derived' href='#base'><metadata>template metadata</metadata></pattern>" +
                "</defs><rect width='20' height='10' fill='url(#derived)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void PatternContent_InheritsCurrentColorThroughItsDefinitionTree()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs><pattern id='p' " +
                "patternUnits='userSpaceOnUse' width='10' height='10' " +
                "color='red' fill='currentColor'><circle color='lime' fill='inherit' " +
                "cx='5' cy='5' r='5'/></pattern></defs>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void PatternViewBox_MapsContentIntoEachTile()
        {
            using var result = _renderer.Render(
                "<svg width='40' height='20'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='20' height='20' viewBox='10 0 20 20' preserveAspectRatio='none'>" +
                "<rect x='10' width='10' height='20' fill='red'/>" +
                "<rect x='20' width='10' height='20' fill='blue'/></pattern>" +
                "<rect width='40' height='20' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 10));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(25, 10));
        }

        [Fact]
        public void WptPatternTransformEmptyAttribute_DoesNotBlockTemplateInheritance()
        {
            using var result = _renderer.Render(
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<pattern id='p1' patternTransform='scale(2)'/>" +
                "<pattern id='p' href='#p1' viewBox='0 0 50 50' patternTransform='' " +
                "width='100%' height='100%'>" +
                "<rect fill='red' width='50' height='50'/>" +
                "<rect fill='green' width='25' height='25'/></pattern>" +
                "<rect fill='url(#p)' width='100' height='100'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            for (int y = 0; y < 100; y += 7)
            {
                for (int x = 0; x < 100; x += 7)
                {
                    SKColor pixel = result.Bitmap.GetPixel(x, y);
                    Assert.True(
                        pixel == SKColors.Green,
                        $"an empty patternTransform must not hide the template transform at " +
                        $"{x},{y}: rendered {pixel}");
                }
            }
        }

        [Fact]
        public void WptPatternTemplateRemoved_HasNoInheritedContentToPaint()
        {
            using var inherited = _renderer.Render(
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<pattern id='pattern' width='1' height='1'>" +
                "<rect width='100' height='100' fill='orange'/></pattern>" +
                "<pattern id='inheritedPattern' href='#pattern'/>" +
                "<rect width='100' height='100' fill='url(#inheritedPattern) green'/></svg>");

            Assert.True(inherited.Success, inherited.ErrorMessage);
            Assert.Equal(SKColors.Orange, inherited.Bitmap.GetPixel(50, 50));

            using var noTemplate = _renderer.Render(
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<pattern id='inheritedPattern' href='#missing' width='1' height='1'/>" +
                "<rect width='100' height='100' fill='url(#inheritedPattern) green'/></svg>");

            Assert.True(noTemplate.Success, noTemplate.ErrorMessage);
            Assert.Equal(SKColors.Green, noTemplate.Bitmap.GetPixel(50, 50));
        }

        [Fact]
        public void NonInvertiblePatternTransform_UsesPaintFallback()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' width='1' height='1' " +
                "patternTransform='scale(0)'><rect width='1' height='1' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p) green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 5));
        }

        [Fact]
        public void ZeroAreaPatternViewBox_PaintsNothingWithoutCompatibilityFallback()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "width='20' height='10' viewBox='0 0 0 10'>" +
                "<rect width='20' height='10' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(10, 5).Alpha);
        }

        [Fact]
        public void RecursivePatternPaint_TerminatesAndSkipsNestedPaint()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><pattern id='p' width='1' height='1'>" +
                "<rect width='1' height='1' fill='url(#p)'/></pattern>" +
                "<rect width='10' height='10' fill='url(#p)'/></svg>",
                new SvgRenderLimits { MaxRenderTimeMs = 2000 });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains(result.Warnings, warning => warning.Contains("pattern paint-server cycle"));
        }

        [Fact]
        public void PatternContentOutsideRenderTree_DoesNotLeakIntoTemplate()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><pattern id='p' href='#invalid' width='1' height='1'/>" +
                "<rect width='10' height='10' fill='url(#p) green'/>" +
                "<text><pattern id='invalid'><rect width='10' height='10' fill='red'/>" +
                "</pattern></text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void ContextFillPattern_UsesReferencedObjectBoundingBox()
        {
            // WPT svg/painting/reftests/paint-context-004.svg: the pattern belongs
            // to the `use`, so its objectBoundingBox tile is sized from the
            // referenced 64x64 group box, not from either 32-wide rectangle.
            using var result = _renderer.Render(
                "<svg width='120' height='90' viewBox='0 0 120 90'><defs>" +
                "<pattern id='grid' x='0' y='0' width='0.125' height='0.125' stroke='blue' " +
                "stroke-width='0.03125' patternContentUnits='objectBoundingBox'>" +
                "<path d='M 0,0.0625 h 0.125'/><path d='M 0.0625,0 v 0.125'/></pattern>" +
                "<g id='rects'><rect width='32' height='64' fill='context-fill'/>" +
                "<rect x='32' y='6' width='32' height='58' fill='context-fill'/></g></defs>" +
                "<use x='18' y='18' fill='url(#grid)' href='#rects'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(22, 26));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(30, 26));
            Assert.Equal(0, result.Bitmap.GetPixel(26, 26).Alpha);
        }

        [Fact]
        public void ContextFillPattern_UsesNearestUseAncestorPaint()
        {
            // WPT svg/painting/reftests/paint-context-008.svg: the inner `use`
            // supplies the context paint, so the tile is sized from the content
            // box the inner `use` instantiates at its own offset.
            using var result = _renderer.Render(
                "<svg width='400' height='300' viewBox='0 0 400 300'><defs>" +
                "<pattern id='grid' x='0' y='0' width='0.125' height='0.25' stroke='blue' " +
                "stroke-width='0.03125' patternContentUnits='objectBoundingBox'>" +
                "<path fill='none' d='M 0.0625 0 l 0.0625 0.125 l -0.0625 0.125 l " +
                "-0.0625 -0.125 Z'/></pattern>" +
                "<g id='shapes'><rect x='50' y='90' width='256' height='128' fill='context-fill'/></g>" +
                "<g id='intermediate'><use x='19' y='23' fill='url(#grid)' href='#shapes'/></g>" +
                "</defs><use href='#intermediate'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(80, 120));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(85, 113));
            Assert.Equal(0, result.Bitmap.GetPixel(100, 140).Alpha);
        }

        [Fact]
        public void OversizedTileSeenThroughOneInstance_PaintsThatInstance()
        {
            // 100x100 objectBoundingBox units on a 100x100 rect: a 10000x10000 tile of
            // which only the corner under the rect can ever show.
            using var result = _renderer.Render(
                "<svg width='100' height='100'><pattern id='p' width='100' height='100'>" +
                "<rect width='100' height='100' fill='lime'/></pattern>" +
                "<rect width='100' height='100' fill='url(#p)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(99, 99));
        }

        [Fact]
        public void OversizedTileSpanningInstances_FailsClosedAsExplicitUnsupported()
        {
            // A 5000x5000 tile whose instance edge crosses the rect at x=50 would need
            // two instances rasterized; that stays refused.
            using var result = _renderer.Render(
                "<svg width='100' height='100'><pattern id='p' patternUnits='userSpaceOnUse' " +
                "x='-4950' width='5000' height='5000'>" +
                "<rect width='5000' height='5000' fill='red'/></pattern>" +
                "<rect width='100' height='100' fill='url(#p)'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("pattern tile exceeds"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("bogus")]
        [InlineData(null)]
        public void WptPatternTransformTemplateInheritance_OnlySpecifiedTransformStopsIt(
            string? declared)
        {
            string declaration = declared == null
                ? string.Empty
                : $" patternTransform='{declared}'";

            using var inherited = _renderer.Render(PatternTemplateInheritance(declaration));
            using var direct = _renderer.Render(
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<pattern id='p' width='20' height='20' patternUnits='userSpaceOnUse' " +
                "patternTransform='rotate(45)'><rect width='10' height='10' fill='green'/></pattern>" +
                "<rect x='10' y='10' width='80' height='80' fill='url(#p)'/></svg>");

            Assert.True(inherited.Success, inherited.ErrorMessage);
            Assert.False(inherited.RequiresFallback, string.Join("; ", inherited.Warnings));
            AssertEquivalent(
                inherited.Bitmap, direct.Bitmap,
                $"patternTransform='{declared}' must inherit rotate(45) from the template");
        }

        [Fact]
        public void WptPatternTransformTemplateInheritance_ScriptOnlyReasonFailsClosed()
        {
            using var result = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='400' height='100'><defs>" +
                "<pattern id='pattern-base' width='20' height='20' patternUnits='userSpaceOnUse' " +
                "patternTransform='rotate(45)'><rect width='10' height='10' fill='green'/></pattern>" +
                "<pattern id='pattern-empty' href='#pattern-base' patternTransform=''/>" +
                "<pattern id='pattern-invalid' href='#pattern-base' patternTransform='bogus'/>" +
                "<pattern id='pattern-absent' href='#pattern-base'/></defs>" +
                "<rect x='10' y='10' width='80' height='80' fill='url(#pattern-empty)'/>" +
                "<rect x='110' y='10' width='80' height='80' fill='url(#pattern-invalid)'/>" +
                "<rect x='210' y='10' width='80' height='80' fill='url(#pattern-absent)'/>" +
                "<script>var t = document.documentElement.createSVGTransform();" +
                "t.setTranslate(5, 0);" +
                "document.getElementById('pattern-absent')" +
                ".patternTransform.baseVal.appendItem(t);</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            Assert.DoesNotContain("paint-server", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("bogus")]
        [InlineData("not-a-transform")]
        public void InvalidPatternTransform_LeavesThePatternUntransformed(string declared)
        {
            using var invalid = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' width='1' height='1' " +
                $"patternTransform='{declared}'><rect width='1' height='1' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");
            using var untransformed = _renderer.Render(
                "<svg width='20' height='10'><pattern id='p' width='1' height='1'>" +
                "<rect width='1' height='1' fill='red'/></pattern>" +
                "<rect width='20' height='10' fill='url(#p)'/></svg>");

            Assert.True(invalid.Success, invalid.ErrorMessage);
            Assert.False(invalid.RequiresFallback, string.Join("; ", invalid.Warnings));
            AssertEquivalent(
                invalid.Bitmap, untransformed.Bitmap,
                $"patternTransform='{declared}' must be dropped like any invalid declaration");
        }

        [Fact]
        public void UnresolvablePatternTransform_FailsClosedInsteadOfInheriting()
        {
            using var result = _renderer.Render(
                "<svg width='20' height='10'><defs>" +
                "<pattern id='base' width='1' height='1' patternTransform='rotate(45)'>" +
                "<rect width='1' height='1' fill='red'/></pattern>" +
                "<pattern id='p' href='#base' patternTransform='translate(10px, 20px)'/>" +
                "</defs><rect width='20' height='10' fill='url(#p) green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("paint-server", result.FallbackReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("SVG pattern transform requires compatibility fallback"));
        }

        private static string PatternTemplateInheritance(string declaration) =>
            "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'><defs>" +
            "<pattern id='base' width='20' height='20' patternUnits='userSpaceOnUse' " +
            "patternTransform='rotate(45)'><rect width='10' height='10' fill='green'/></pattern>" +
            $"<pattern id='p' href='#base'{declaration}/></defs>" +
            "<rect x='10' y='10' width='80' height='80' fill='url(#p)'/></svg>";

        private static void AssertEquivalent(SKBitmap actual, SKBitmap expected, string label)
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            for (int y = 0; y < actual.Height; y++)
            {
                for (int x = 0; x < actual.Width; x++)
                {
                    Assert.True(
                        actual.GetPixel(x, y) == expected.GetPixel(x, y),
                        $"{label}: pixel {x},{y} is {actual.GetPixel(x, y)} but must be " +
                        $"{expected.GetPixel(x, y)}");
                }
            }
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
