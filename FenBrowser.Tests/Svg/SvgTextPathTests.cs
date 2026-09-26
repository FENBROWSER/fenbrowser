using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgTextPathTests
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

        private static void AssertRendersDifferently(string firstSvg, string secondSvg)
        {
            using var first = new FenSvgRenderer().Render(firstSvg);
            using var second = new FenSvgRenderer().Render(secondSvg);
            Assert.True(first.Success, first.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.True(second.Success, second.ErrorMessage);
            bool identical = first.Bitmap.Width == second.Bitmap.Width &&
                             first.Bitmap.Height == second.Bitmap.Height;
            if (identical)
            {
                for (int y = 0; y < first.Bitmap.Height && identical; y++)
                for (int x = 0; x < first.Bitmap.Width; x++)
                {
                    if (first.Bitmap.GetPixel(x, y) == second.Bitmap.GetPixel(x, y)) continue;
                    identical = false;
                    break;
                }
            }
            Assert.False(identical, "expected the two documents to paint different pixels");
        }

        private static void AssertPaintsInk(string svg)
        {
            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            bool painted = false;
            for (int y = 0; y < result.Bitmap.Height && !painted; y++)
            for (int x = 0; x < result.Bitmap.Width; x++)
            {
                if (result.Bitmap.GetPixel(x, y).Alpha == 0) continue;
                painted = true;
                break;
            }
            Assert.True(painted, "expected the textPath run to paint visible ink");
        }

        private static bool HasForeground(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) return true;
            }
            return false;
        }

        private static bool TryGetColorBounds(SKBitmap bitmap, Func<SKColor, bool> predicate, out SKRectI bounds)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha == 0 || !predicate(color)) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
            bounds = right >= left ? new SKRectI(left, top, right + 1, bottom + 1) : SKRectI.Empty;
            return right >= left;
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void TextPath_PathLengthCalibratesNumericAndPercentageOffsets()
        {
            const string actual =
                "<svg width='300' height='120' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280' pathLength='2'/></defs>" +
                "<text><textPath href='#p' startOffset='1'><tspan>calibrated text</tspan></textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='50%'>" +
                "<tspan>calibrated text</tspan></textPath></text></g></svg>";
            const string reference =
                "<svg width='300' height='120' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280'/></defs>" +
                "<text><textPath href='#p' startOffset='130'><tspan>calibrated text</tspan></textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='130'>" +
                "<tspan>calibrated text</tspan></textPath></text></g></svg>";
            AssertIdenticalRender(actual, reference);
        }

        [Fact]
        public void TextPath_ZeroPathLengthKeepsZeroOffsetAndSuppressesNonZeroOffset()
        {
            const string actual =
                "<svg width='300' height='100' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280' pathLength='0'/></defs>" +
                "<text><textPath href='#p' startOffset='0'><tspan>visible</tspan></textPath></text>" +
                "<g transform='translate(0 50)'><text><textPath href='#p' startOffset='1'>" +
                "<tspan>hidden</tspan></textPath></text></g></svg>";
            const string reference =
                "<svg width='300' height='100' font-family='sans-serif' font-size='20'>" +
                "<defs><path id='p' d='M20 30 H280'/></defs>" +
                "<text><textPath href='#p'><tspan>visible</tspan></textPath></text></svg>";
            AssertIdenticalRender(actual, reference);
        }

        [Fact]
        public void TextPath_DisplayNoneSuppressesOnlyItsTextContent()
        {
            const string hidden =
                "<svg width='120' height='40'><defs><path id='p' d='M5 30H115'/></defs>" +
                "<text font-size='20'><textPath href='#p' display='none'><tspan>hidden</tspan></textPath></text></svg>";
            const string empty =
                "<svg width='120' height='40'><defs><path id='p' d='M5 30H115'/></defs>" +
                "<text font-size='20'></text></svg>";

            AssertIdenticalRender(hidden, empty);
        }

        [Fact]
        public void TextPath_ExternalReferenceFailsClosedAsRejectedResource()
        {
            const string svg =
                "<svg width='100' height='30'><text><textPath href='https://example.invalid/p.svg#p'>x</textPath></text></svg>";
            using var result = new FenSvgRenderer().Render(svg);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
            Assert.False(SvgRenderResult.IsAdmissible(result));
        }

        [Fact]
        public async Task TextPath_RenderingIsDeterministicAcrossConcurrentDocuments()
        {
            const string svg =
                "<svg width='200' height='50'><defs><path id='p' d='M5 35 H195'/></defs>" +
                "<text font-size='20'><textPath href='#p'><tspan>concurrent text</tspan></textPath></text></svg>";
            ulong[] checksums = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                using var result = new FenSvgRenderer().Render(svg);
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
                ulong hash = 1469598103934665603UL;
                for (int y = 0; y < result.Bitmap.Height; y++)
                for (int x = 0; x < result.Bitmap.Width; x++)
                {
                    SKColor color = result.Bitmap.GetPixel(x, y);
                    hash ^= (uint)(color.Alpha << 24 | color.Red << 16 | color.Green << 8 | color.Blue);
                    hash *= 1099511628211UL;
                }
                return hash;
            })));
            Assert.All(checksums, checksum => Assert.Equal(checksums[0], checksum));
        }

        [Fact]
        public void TextPath_RendersInkOnThePathBaseline()
        {
            AssertPaintsInk(
                "<svg width='200' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L180 60'/></defs>" +
                "<text fill='black'><textPath href='#p'><tspan>ABCD</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_DirectCharacterDataRendersTheSameGlyphsAsTheNestedTspan()
        {
            // WPT svg/text/reftests/textpath-path-attr.svg and its -ref.svg, and
            // the href form shared by textpath-side-001..005 and textpath-shape-001:
            // the reftests spell the run as direct character data, not a tspan.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>";
            const string defs = "<defs><path id='p' d='M20 60 L280 60'/></defs>";

            AssertIdenticalRender(
                head + defs + "<text fill='black'><textPath href='#p'>ABCD</textPath></text></svg>",
                head + defs + "<text fill='black'><textPath href='#p'><tspan>ABCD</tspan></textPath></text></svg>");
            AssertIdenticalRender(
                head + "<text fill='black'><textPath path='M20 60 L280 60'>Text on a path</textPath></text></svg>",
                head + "<text fill='black'><textPath path='M20 60 L280 60'><tspan>Text on a path</tspan></textPath></text></svg>");
            AssertIdenticalRender(
                head + "<text fill='black'><textPath path='M20 60 L280 60 4'>ABCD</textPath></text></svg>",
                head + "<text fill='black'><textPath path='M20 60 L280 60 4'><tspan>ABCD</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_DirectCharacterDataOnAShapeAndOnACalibratedPathMatchesTheTspanForm()
        {
            // WPT svg/text/reftests/textpath-shape-001.svg (a shape, not a path,
            // supplies the geometry) and path/distance/pathLength-*.svg (pathLength
            // rescales startOffset), both authored with direct character data.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>";
            const string circle = "<defs><circle id='c' cx='150' cy='60' r='50'/></defs>";
            const string calibrated =
                "<defs><path id='p' d='M20 60 L280 60' pathLength='2'/></defs>";

            AssertIdenticalRender(
                head + circle + "<text fill='black'><textPath href='#c'>ABCD</textPath></text></svg>",
                head + circle + "<text fill='black'><textPath href='#c'><tspan>ABCD</tspan></textPath></text></svg>");
            AssertIdenticalRender(
                head + calibrated + "<text fill='black'><textPath href='#p' startOffset='1'>ABCD</textPath></text></svg>",
                head + calibrated + "<text fill='black'><textPath href='#p' startOffset='1'><tspan>ABCD</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_RoundedRectGeometry_MatchesTheEquivalentSvgPathStart()
        {
            // WPT svg/text/reftests/textpath-shape-001.svg lays the run on a
            // rounded-rect shape. The shape subpath must start at (x + rx, y)
            // and run clockwise, which is where the equivalent path data below
            // starts, so both must place every glyph identically.
            const string head =
                "<svg width='300' height='300' font-family='monospace' font-size='18'>";
            const string shape =
                "<defs><rect id='r' x='90' y='150' width='100' height='100' rx='20'/></defs>";
            const string equivalentPath =
                "<defs><path id='r' d='M110 150 H170 A20 20 0 0 1 190 170 V230 " +
                "A20 20 0 0 1 170 250 H110 A20 20 0 0 1 90 230 V170 " +
                "A20 20 0 0 1 110 150 Z'/></defs>";

            AssertIdenticalRender(
                head + shape + "<text fill='black'><textPath href='#r'>" +
                "<tspan>ABCD</tspan></textPath></text></svg>",
                head + equivalentPath + "<text fill='black'><textPath href='#r'>" +
                "<tspan>ABCD</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_EntityReferencesInDirectContentExpandLikeTspanContent()
        {
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p'>A&amp;B&#65;</textPath></text></svg>",
                head + "<text fill='black'><textPath href='#p'><tspan>A&amp;B&#65;</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_TwoDirectRunsAdvanceIndependentlyAlongTheirOwnPaths()
        {
            // WPT svg/text/reftests/multiple-textpaths.svg and
            // textpath-path-attr-two-textpaths.svg.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='a' d='M10 30 L290 30'/><path id='b' d='M10 90 L290 90'/></defs>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#a'>First</textPath><textPath href='#b'>Second</textPath></text></svg>",
                head + "<text fill='black'><textPath href='#a'>First</textPath></text>" +
                       "<text fill='black'><textPath href='#b'>Second</textPath></text></svg>");
            AssertRendersDifferently(
                head + "<text fill='black'><textPath href='#a'>First</textPath><textPath href='#b'>Second</textPath></text></svg>",
                head + "<text fill='black'><textPath href='#a'>First</textPath><textPath href='#b'>First</textPath></text></svg>");
        }

        [Fact]
        public void TextPath_EmptyOrWhitespacePathAttributeFallsBackToTheHrefGeometry()
        {
            // WPT svg/text/reftests/textpath-path-attr-empty-fallback.svg and
            // textpath-path-attr-whitespace-fallback.svg.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";
            const string reference =
                head + "<text fill='black'><textPath href='#p'>ABCD</textPath></text></svg>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p' path=''>ABCD</textPath></text></svg>",
                reference);
            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p' path='   '>ABCD</textPath></text></svg>",
                reference);
        }

        [Fact]
        public void TextPath_UnusablePathAttributeFallsBackToTheHrefGeometry()
        {
            // WPT svg/text/reftests/textpath-path-attr-invalid-path-fallback.svg: a
            // path attribute that yields no geometry at all is an error, and the
            // textPath lays its run on the href target instead of painting nothing.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";
            const string reference =
                head + "<text fill='black'><textPath href='#p'>ABCD</textPath></text></svg>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p' path='Invalid path'>ABCD</textPath></text></svg>",
                reference);
            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p' path='Z'>ABCD</textPath></text></svg>",
                reference);
        }

        [Fact]
        public void TextPath_UnusablePathAttributeWithoutAnHrefPaintsNothingWithoutFallback()
        {
            const string svg =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<text fill='black'><textPath path='Invalid path'>ABCD</textPath></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(HasForeground(result.Bitmap));
        }

        [Fact]
        public void TextPath_PercentageStartOffsetIsAFractionOfTheDeclaredPathLength()
        {
            // path/distance/pathLength-positive-percentage.svg matches
            // pathLength-positive-ref.svg, whose 50% of an uncalibrated path is the
            // same point as 1 of a pathLength of 2: a percentage is a fraction of the
            // declared length, so the two forms must land on identical pixels.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>";
            const string calibrated =
                "<defs><path id='p' d='M20 60 L280 60' pathLength='2'/></defs>";
            const string plain = "<defs><path id='p' d='M20 60 L280 60'/></defs>";

            AssertIdenticalRender(
                head + calibrated + "<text fill='black'><textPath href='#p' startOffset='50%'>ABCD</textPath></text></svg>",
                head + plain + "<text fill='black'><textPath href='#p' startOffset='130'>ABCD</textPath></text></svg>");
            AssertRendersDifferently(
                head + calibrated + "<text fill='black'><textPath href='#p' startOffset='50%'>ABCD</textPath></text></svg>",
                head + calibrated + "<text fill='black'><textPath href='#p' startOffset='25%'>ABCD</textPath></text></svg>");
        }

        [Fact]
        public void TextPath_ZeroPathLengthCollapsesAPercentageStartOffsetToTheStart()
        {
            // path/distance/pathLength-zero-percentage.svg matches a reference that
            // carries no startOffset at all: a pathLength of zero is a scaling factor
            // of infinity, so 0%, 50% and -50% are each a fraction of zero and every
            // run starts where an uncalibrated path starts.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";
            const string reference =
                head + "<text fill='black'><textPath href='#p'>ABCD</textPath></text></svg>";
            string calibrated = head.Replace("<path id='p'", "<path pathLength='0' id='p'");

            foreach (string offset in new[] { "0%", "50%", "-50%" })
            {
                AssertIdenticalRender(
                    calibrated + "<text fill='black'><textPath href='#p' startOffset='" +
                    offset + "'>ABCD</textPath></text></svg>",
                    reference);
            }
        }

        [Fact]
        public void TextPath_LinkWrapperRendersTheSameGlyphsAsPlainContent()
        {
            // WPT svg/text/reftests/text-bidi-controls-anchors-1.svg / -2.svg wrap
            // the textPath run in an <a>; the link carries no geometry of its own.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p'><a href='#x'><tspan>Link</tspan></a></textPath></text></svg>",
                head + "<text fill='black'><textPath href='#p'><tspan>Link</tspan></textPath></text></svg>");
            AssertIdenticalRender(
                head + "<text fill='black'><a href='#x'><textPath href='#p'><tspan>Link</tspan></textPath></a></text></svg>",
                head + "<text fill='black'><textPath href='#p'><tspan>Link</tspan></textPath></text></svg>");
        }

        [Fact]
        public void TextPath_WhitespaceOnlyDirectContentPaintsNothing()
        {
            // A collapsed-to-empty run must not claim glyphs and must not be
            // reported as lost content.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p'>   </textPath></text></svg>",
                head + "<text fill='black'></text></svg>");
        }

        [Fact]
        public void TextPath_SelfClosingWithoutContentPaintsNothingWithoutFallback()
        {
            const string svg =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>" +
                "<text fill='black'><textPath href='#p'/></text></svg>";
            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            for (int y = 0; y < result.Bitmap.Height; y++)
            for (int x = 0; x < result.Bitmap.Width; x++)
                Assert.Equal(0, result.Bitmap.GetPixel(x, y).Alpha);
        }

        [Fact]
        public void TextPath_CdataDirectContentStillFailsClosed()
        {
            // The retained content path decodes XML entities, never CDATA
            // sections; a CDATA run is not silently dropped.
            const string svg =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>" +
                "<text fill='black'><textPath href='#p'><![CDATA[ABCD]]></textPath></text></svg>";
            using var result = new FenSvgRenderer().Render(svg);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("CDATA", StringComparison.Ordinal));
        }

        [Fact]
        public void TextPath_SideLeftAndUnusableSideValuesMatchTheDefault()
        {
            const string baseline =
                "<svg width='200' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L180 60'/></defs>" +
                "<text fill='black'><textPath href='#p'><tspan>ABCD</tspan></textPath></text></svg>";
            foreach (string side in new[] { "left", "invalid", "" })
            {
                AssertIdenticalRender(
                    baseline.Replace("href='#p'>", $"href='#p' side='{side}'>"),
                    baseline);
            }
        }

        [Fact]
        public void TextPath_SideRightMatchesTextOnTheReversedPath()
        {
            const string forward =
                "<svg width='200' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L180 60'/></defs>" +
                "<text fill='black'><textPath href='#p' side='right'><tspan>ABCD</tspan></textPath></text></svg>";
            const string reversed =
                "<svg width='200' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='r' d='M180 60 L20 60'/></defs>" +
                "<text fill='black'><textPath href='#r'><tspan>ABCD</tspan></textPath></text></svg>";
            AssertIdenticalRender(forward, reversed);
        }

        [Fact]
        public void TextPath_SideRightIsNotTheSameAsSideLeft()
        {
            const string head =
                "<svg width='200' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L180 60'/></defs>" +
                "<text fill='black'><textPath href='#p' side='";
            const string tail = "'><tspan>ABCD</tspan></textPath></text></svg>";
            AssertRendersDifferently(head + "left" + tail, head + "right" + tail);
        }

        [Fact]
        public void TextPath_InlinePathDataWinsOverHref()
        {
            const string both =
                "<svg width='400' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='line' d='M10 50 L100 100'/></defs>" +
                "<text fill='black'><textPath href='#line' path='M10 60 L380 60'>" +
                "<tspan>Text along a curve</tspan></textPath></text></svg>";
            const string inlineOnly =
                "<svg width='400' height='120' font-family='monospace' font-size='16'>" +
                "<text fill='black'><textPath path='M10 60 L380 60'>" +
                "<tspan>Text along a curve</tspan></textPath></text></svg>";
            AssertIdenticalRender(both, inlineOnly);
        }

        [Fact]
        public void TextPath_InlinePathDataMatchesTheEquivalentReferencedShape()
        {
            const string inline =
                "<svg width='300' height='120' font-family='monospace' font-size='16'>" +
                "<text fill='black'><textPath path='M20 40 L160 80'>" +
                "<tspan>Text on a path</tspan></textPath></text></svg>";
            const string referenced =
                "<svg width='300' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='p1' d='M20 40 L160 80'/></defs>" +
                "<text fill='black'><textPath href='#p1'>" +
                "<tspan>Text on a path</tspan></textPath></text></svg>";
            AssertIdenticalRender(inline, referenced);
        }

        [Fact]
        public void TextPath_TargetOwnTransformIsBakedIntoThePathGeometry()
        {
            const string transformed =
                "<svg width='300' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='p1' transform='scale(2)' d='M10 10 L80 40'/></defs>" +
                "<text fill='black'><textPath href='#p1'><tspan>ABCD</tspan></textPath></text></svg>";
            const string preScaled =
                "<svg width='300' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='p2' d='M20 20 L160 80'/></defs>" +
                "<text fill='black'><textPath href='#p2'><tspan>ABCD</tspan></textPath></text></svg>";
            const string unscaled =
                "<svg width='300' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='p3' d='M10 10 L80 40'/></defs>" +
                "<text fill='black'><textPath href='#p3'><tspan>ABCD</tspan></textPath></text></svg>";

            AssertIdenticalRender(transformed, preScaled);
            AssertRendersDifferently(transformed, unscaled);
        }

        [Fact]
        public void TextPath_MethodAndSpacingRemainExplicitlyUnsupported()
        {
            const string head =
                "<svg width='200' height='120' font-family='monospace' font-size='16'>" +
                "<defs><path id='p' d='M20 60 L180 60'/></defs>" +
                "<text fill='black'><textPath href='#p' ";
            foreach (string declaration in new[] { "method='align'", "spacing='auto'" })
            {
                using var result = new FenSvgRenderer().Render(
                    head + declaration + "><tspan>ABCD</tspan></textPath></text></svg>");
                Assert.False(result.Success, result.ErrorMessage);
                Assert.Null(result.Bitmap);
                Assert.True(result.RequiresFallback);
                Assert.Contains("advanced-text-layout", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void TextPath_LinkWrapperWithDirectContentRendersTheSameGlyphsAsPlainContent()
        {
            // WPT svg/import/linking-a-08-t-manual.svg spells the label straight into
            // the link ("Link inside text"), and struct-frag-05-t-manual.svg does the
            // same. The link contributes no geometry, so a wrapped run must land on
            // exactly the same glyphs, and must not be reported as lost content.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>";
            const string reference =
                head + "<text fill='black'><textPath href='#p'>Link inside text</textPath></text></svg>";

            AssertIdenticalRender(
                head + "<text fill='black'><textPath href='#p'>" +
                       "<a href='https://www.w3.org'>Link inside text</a></textPath></text></svg>",
                reference);
            AssertIdenticalRender(
                head + "<text fill='black'><a href='https://www.w3.org'>" +
                       "<textPath href='#p'>Link inside text</textPath></a></text></svg>",
                reference);
        }

        [Fact]
        public void TextPath_MixedDirectAndChildContentKeepsSourceOrder()
        {
            // WPT svg/text/reftests/multiple-textpaths.svg and
            // textpath-path-attr-two-textpaths.svg interleave character data with
            // element children, and multiple-textpaths.svg is itself written as
            // `<textPath>Some text.</textPath\n  ><textPath>More text.</textPath>`. A
            // run split between a textPath's own character data and a tspan child is
            // the same shape of problem inside one path.
            const string head =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>" +
                "<text fill='black'><textPath href='#p'>";
            const string tail = "</textPath></text></svg>";

            AssertIdenticalRender(
                head + "AB<tspan>CD</tspan>" + tail,
                head + "ABCD" + tail);
            AssertIdenticalRender(
                head + "A<tspan>B</tspan>C" + tail,
                head + "ABC" + tail);
            AssertIdenticalRender(
                head + "<a href='https://www.w3.org'>A</a><tspan>B</tspan>" + tail,
                head + "AB" + tail);
        }

        [Fact]
        public void TextPath_MixedContentPaintsDirectAndChildRunsInSourceOrder()
        {
            const string svg =
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>" +
                "<text fill='black'><textPath href='#p'>AB<tspan fill='red'>CD</tspan>" +
                "</textPath></text></svg>";
            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetColorBounds(result.Bitmap, _ => true, out var all));
            Assert.True(TryGetColorBounds(result.Bitmap,
                c => c.Red > 180 && c.Green < 80 && c.Blue < 80, out var red));
            Assert.True(TryGetColorBounds(result.Bitmap,
                c => c.Red < 100 && c.Green < 100 && c.Blue < 100, out var direct));
            Assert.Equal(all.Left, direct.Left);
            Assert.True(direct.Right <= red.Left, "the direct run must precede the tspan run");
        }

        [Fact]
        public void TextPath_DirectContentObeysTheSameCharacterBudgetAsTspan()
        {
            // The per-element render budget is a property of the text element, so a
            // textPath spelled with its own character data is bounded exactly like
            // the tspan it is equivalent to.
            const string head =
                "<svg width='120' height='60' font-family='monospace' font-size='8'>" +
                "<defs><path id='p' d='M10 30 L110 30'/></defs><text><textPath href='#p'>";
            const string tail = "</textPath></text></svg>";

            using var atLimit = new FenSvgRenderer().Render(head + new string('a', 4096) + tail);
            Assert.True(atLimit.Success, atLimit.ErrorMessage);
            Assert.False(atLimit.RequiresFallback, string.Join("; ", atLimit.Warnings));

            using var overLimit = new FenSvgRenderer().Render(head + new string('a', 4097) + tail);
            AssertFailsClosed(overLimit);
            Assert.True(overLimit.RequiresFallback);
            Assert.Contains(overLimit.Warnings, warning =>
                warning.Contains("render length budget", StringComparison.Ordinal));
        }

        [Fact]
        public void TextPath_LinkWrapperDirectContentObeysTheSameCharacterBudgetAsTspan()
        {
            const string head =
                "<svg width='120' height='60' font-family='monospace' font-size='8'>" +
                "<defs><path id='p' d='M10 30 L110 30'/></defs>" +
                "<text><textPath href='#p'><a href='https://www.w3.org'>";
            const string tail = "</a></textPath></text></svg>";

            using var atLimit = new FenSvgRenderer().Render(head + new string('a', 4096) + tail);
            Assert.True(atLimit.Success, atLimit.ErrorMessage);
            Assert.False(atLimit.RequiresFallback, string.Join("; ", atLimit.Warnings));

            using var overLimit = new FenSvgRenderer().Render(head + new string('a', 4097) + tail);
            AssertFailsClosed(overLimit);
            Assert.True(overLimit.RequiresFallback);
            Assert.Contains(overLimit.Warnings, warning =>
                warning.Contains("render length budget", StringComparison.Ordinal));
        }

        [Fact]
        public void TextPath_LocalUrlReferenceSurvivesADocumentBaseUrl()
        {
            // WPT svg/linking/reftests/url-reference-local-textpath.svg puts the
            // run on a textPath reached through a local url() reference while the
            // document also carries a self-closed <h:base href="..."/>. The base
            // URL must not redirect a same-document reference, so the run has to
            // land on the referenced path exactly where the same document without
            // the base element, and the equivalent inline `path` spelling, land it.
            const string head =
                "<svg width='200' height='100' xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:h='http://www.w3.org/1999/xhtml' font-family='monospace' font-size='16'>";
            const string withBase =
                "<h:base href='http://www.example.com/'/>" +
                "<path id='p' d='M10 60 H190'/><text fill='black'>" +
                "<textPath href='#p'>ABCD</textPath></text></svg>";
            const string withoutBase =
                "<path id='p' d='M10 60 H190'/><text fill='black'>" +
                "<textPath href='#p'>ABCD</textPath></text></svg>";
            const string inline =
                "<text fill='black'>" +
                "<textPath path='M10 60 H190'>ABCD</textPath></text></svg>";

            AssertIdenticalRender(head + withBase, head + withoutBase);
            AssertIdenticalRender(head + withBase, head + inline);
            AssertPaintsInk(head + withBase);
        }

        [Fact]
        public void TextPath_SideOnACurvedPathPaintsTheRunOnBothSides()
        {
            // WPT svg/text/reftests/textpath-side-005.svg lays the same run on a
            // clockwise circle twice, once with side="left" so the text falls on
            // the outside and once with side="right" so it falls on the inside.
            // The unsided run is the left-side run, the two sides are not the same
            // picture, and a curved path must neither drop the run nor leave the
            // side attribute inert.
            const string head =
                "<svg width='400' height='200' font-family='monospace' font-size='16'>" +
                "<path id='c' d='M 150,100 A 80,80 0 0,1 30,100 A 80,80 0 0,1 150,100'/>";
            const string unsided = head + "<text fill='black'>" +
                                   "<textPath href='#c'>ABCD</textPath></text></svg>";
            const string left = head + "<text fill='black'>" +
                                 "<textPath href='#c' side='left'>ABCD</textPath></text></svg>";
            const string right = head + "<text fill='black'>" +
                                  "<textPath href='#c' side='right'>ABCD</textPath></text></svg>";

            AssertIdenticalRender(unsided, left);
            AssertRendersDifferently(left, right);
            AssertPaintsInk(unsided);
            AssertPaintsInk(right);
        }

        [Fact]
        public void TextPath_EmptyTextPathIsNotReportedAsLostContent()
        {
            // The tree now retains a textPath's content, so an element with none is
            // genuinely empty rather than unrenderable.
            AssertIdenticalRender(
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<defs><path id='p' d='M20 60 L280 60'/></defs>" +
                "<text fill='black'><textPath href='#p'></textPath></text></svg>",
                "<svg width='300' height='120' font-family='monospace' font-size='20'>" +
                "<text fill='black'></text></svg>");
        }
    }
}
