using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>Core paint/geometry semantics of the first-party renderer.</summary>
    public class FenSvgRendererPixelTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void RedRect_FillsExpectedPixels()
        {
            using var result = _renderer.Render(
                "<svg width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"red\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(16, 16));
        }

        [Fact]
        public void DefaultFill_IsBlack_PerSpec()
        {
            // No fill attribute anywhere: rect must render black. No source
            // rewrite is needed; the default fill comes from the cascade.
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\"><rect width=\"8\" height=\"8\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(4, 4);
            Assert.Equal((byte)255, px.Alpha);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void FillNone_RendersNothing()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"none\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void RootFillAttribute_InheritsToChildren()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\" fill=\"blue\"><rect width=\"6\" height=\"6\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(3, 3);
            Assert.Equal((byte)255, px.Blue);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void Circle_CenterFilled_EdgeOutside()
        {
            using var result = _renderer.Render(
                "<svg width=\"20\" height=\"20\"><circle cx=\"10\" cy=\"10\" r=\"6\" fill=\"green\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void GroupOpacity_CompositesChildren()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\">" +
                "<g opacity=\"0.5\"><rect width=\"10\" height=\"10\" fill=\"red\"/></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(5, 5);
            // GetPixel returns UN-premultiplied color: red channel stays 255,
            // the alpha channel carries the composite.
            Assert.InRange(px.Alpha, 100, 155);
            Assert.Equal((byte)255, px.Red);
        }

        private static bool BitmapHasVisiblePixels(SKBitmap bitmap)
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

        private const string DashDocOpen =
            "<svg width=\"200\" height=\"40\" xmlns=\"http://www.w3.org/2000/svg\">";

        private static bool[] RowPaintedMask(SKBitmap bitmap, int y, int count)
        {
            var mask = new bool[count];
            for (int x = 0; x < count; x++)
            {
                mask[x] = bitmap.GetPixel(x, y).Alpha >= 128;
            }
            return mask;
        }

        private static bool[] ColumnPaintedMask(SKBitmap bitmap, int x, int yStart, int count)
        {
            var mask = new bool[count];
            for (int i = 0; i < count; i++)
            {
                mask[i] = bitmap.GetPixel(x, yStart + i).Alpha >= 128;
            }
            return mask;
        }

        [Fact]
        public void WptPaintingStroke06_OddDashList_MatchesRepeatedEvenList()
        {
            using var odd = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"5,2,5\"/></svg>");
            using var even = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"5,2,5,5,2,5\"/></svg>");

            Assert.True(odd.Success, odd.ErrorMessage);
            Assert.True(even.Success, even.ErrorMessage);

            bool[] oddMask = RowPaintedMask(odd.Bitmap, 20, 200);
            Assert.Equal(RowPaintedMask(even.Bitmap, 20, 200), oddMask);

            foreach (int painted in new[] { 0, 4, 7, 11, 17, 18, 24, 31, 41, 42 })
            {
                Assert.True(oddMask[painted], $"x={painted} is inside a dash of 5,2,5,5,2,5");
            }
            foreach (int gap in new[] { 5, 6, 12, 13, 14, 15, 16, 19, 20, 21, 22, 23 })
            {
                Assert.False(oddMask[gap], $"x={gap} is inside a gap of 5,2,5,5,2,5");
            }
        }

        [Fact]
        public void WptPaintingStroke06_DashNoneAndZeroSumLists_RenderSolidStroke()
        {
            foreach (string dashArray in new[] { "none", "0", "0 0", "0 0 0", "0,0,0,0,0" })
            {
                using var result = _renderer.Render(
                    DashDocOpen +
                    "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                    "stroke-width=\"20\" stroke-dasharray=\"" + dashArray + "\"/></svg>");

                Assert.True(result.Success, dashArray + ": " + result.ErrorMessage);
                bool[] mask = RowPaintedMask(result.Bitmap, 20, 200);
                for (int x = 0; x < 200; x++)
                {
                    Assert.True(mask[x], $"dasharray='{dashArray}' left a gap at x={x}");
                }
            }
        }

        [Fact]
        public void WptPaintingStroke06_OddSingleValueList_DashOffsetShiftsOneInterval()
        {
            using var plain = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"2\"/></svg>");
            using var shifted = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"2\" stroke-dashoffset=\"2\"/></svg>");

            Assert.True(plain.Success, plain.ErrorMessage);
            Assert.True(shifted.Success, shifted.ErrorMessage);

            bool[] plainMask = RowPaintedMask(plain.Bitmap, 20, 200);
            bool[] shiftedMask = RowPaintedMask(shifted.Bitmap, 20, 200);
            for (int x = 0; x < 200; x++)
            {
                Assert.True(
                    plainMask[x] != shiftedMask[x],
                    $"x={x}: dashoffset equal to one repeated interval must swap dash and gap");
            }
        }

        [Fact]
        public void WptPaintingControl02_OddLengthUnitList_StartsAtEachPathStart()
        {
            const string Open = "<svg width=\"40\" height=\"130\" xmlns=\"http://www.w3.org/2000/svg\">";
            const string Dash = " stroke-dasharray=\"10px 20px 20px\"";

            using var forward = _renderer.Render(
                Open + "<path d=\"M 20 20 L 20 110\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"10\"" + Dash + "/></svg>");
            using var reverse = _renderer.Render(
                Open + "<path d=\"M 20 110 L 20 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"10\"" + Dash + "/></svg>");

            Assert.True(forward.Success, forward.ErrorMessage);
            Assert.True(reverse.Success, reverse.ErrorMessage);

            bool[] down = ColumnPaintedMask(forward.Bitmap, 20, 20, 90);
            bool[] up = ColumnPaintedMask(reverse.Bitmap, 20, 20, 90);

            foreach (int y in new[] { 25, 55 })
            {
                Assert.True(down[y - 20], $"y={y} is inside a dash of the downward path");
                Assert.False(up[y - 20], $"y={y} is inside a gap of the upward path");
            }
            foreach (int y in new[] { 75, 105 })
            {
                Assert.False(down[y - 20], $"y={y} is inside a gap of the downward path");
                Assert.True(up[y - 20], $"y={y} is inside a dash of the upward path");
            }
        }

        [Fact]
        public void WptPaintingControl02_OddLengthUnitList_DashOffsetShiftsPhase()
        {
            const string Open = "<svg width=\"40\" height=\"230\" xmlns=\"http://www.w3.org/2000/svg\">";

            using var plain = _renderer.Render(
                Open + "<path d=\"M 20 20 L 20 210\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"10\" stroke-dasharray=\"10px 10px 10px\"/></svg>");
            using var shifted = _renderer.Render(
                Open + "<path d=\"M 20 20 L 20 210\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"10\" stroke-dasharray=\"10px 10px 10px\" " +
                "stroke-dashoffset=\"5px\"/></svg>");

            Assert.True(plain.Success, plain.ErrorMessage);
            Assert.True(shifted.Success, shifted.ErrorMessage);

            bool[] plainMask = ColumnPaintedMask(plain.Bitmap, 20, 20, 190);
            bool[] shiftedMask = ColumnPaintedMask(shifted.Bitmap, 20, 20, 190);

            Assert.True(plainMask[0], "the unshifted pattern starts with a full 10px dash");
            Assert.True(shiftedMask[0], "a 5px offset starts 5px into the first dash, so it still paints");
            for (int y = 0; y < 5; y++)
            {
                Assert.True(plainMask[y], $"y={y + 20} is inside the first 10px dash");
                Assert.True(shiftedMask[y], $"y={y + 20} is inside the truncated first dash");
            }
            for (int y = 5; y < 10; y++)
            {
                Assert.True(plainMask[y], $"y={y + 20} is inside the first 10px dash");
                Assert.False(shiftedMask[y], $"y={y + 20} is inside the 5px offset gap");
            }
            for (int y = 10; y < 15; y++)
            {
                Assert.False(plainMask[y], $"y={y + 20} is inside the first 10px gap");
                Assert.False(shiftedMask[y], $"y={y + 20} is inside the 5px offset gap");
            }
            for (int y = 15; y < 20; y++)
            {
                Assert.False(plainMask[y], $"y={y + 20} is inside the first 10px gap");
                Assert.True(shiftedMask[y], $"y={y + 20} is inside the shifted dash");
            }
            for (int y = 0; y < 190 - 5; y++)
            {
                if (plainMask[y + 5] != shiftedMask[y])
                {
                    Assert.Fail(
                        $"y={y + 20}: a 5px dashoffset must advance the pattern phase by exactly 5px");
                }
            }
        }

        [Fact]
        public void WptAnimateElem_OddDashListOnGroup_RendersAndDashesChildren()
        {
            const string Open = "<svg width=\"60\" height=\"20\" xmlns=\"http://www.w3.org/2000/svg\">";

            using var grouped = _renderer.Render(
                Open + "<g stroke=\"black\" stroke-width=\"10\" stroke-dasharray=\"3,4,5\">" +
                "<line x1=\"5\" y1=\"10\" x2=\"55\" y2=\"10\"/></g></svg>");
            using var explicitEven = _renderer.Render(
                Open + "<g stroke=\"black\" stroke-width=\"10\" stroke-dasharray=\"3,4,5,3,4,5\">" +
                "<line x1=\"5\" y1=\"10\" x2=\"55\" y2=\"10\"/></g></svg>");

            Assert.True(grouped.Success, grouped.ErrorMessage);
            Assert.True(explicitEven.Success, explicitEven.ErrorMessage);

            bool[] mask = RowPaintedMask(grouped.Bitmap, 10, 60);
            Assert.Equal(RowPaintedMask(explicitEven.Bitmap, 10, 60), mask);

            foreach (int painted in new[] { 5, 6, 7, 12, 13, 14, 15, 16, 20, 21 })
            {
                Assert.True(mask[painted], $"x={painted} is inside a dash of 3,4,5,3,4,5");
            }
            foreach (int gap in new[] { 8, 9, 10, 11, 17, 18, 19, 25, 26, 27 })
            {
                Assert.False(mask[gap], $"x={gap} is inside a gap of 3,4,5,3,4,5");
            }
        }

        [Theory]
        [InlineData("4 -2")]
        [InlineData("4 bogus")]
        [InlineData("4 2 3 5 -1")]
        [InlineData("3 4 5 bogus")]
        [InlineData("4 2 3 5 1e40")]
        public void InvalidDashListEntry_LeavesStrokeSolid(string dashArray)
        {
            using var result = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"" + dashArray + "\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            bool[] mask = RowPaintedMask(result.Bitmap, 20, 200);
            for (int x = 0; x < 200; x++)
            {
                Assert.True(mask[x], $"dasharray='{dashArray}' left a gap at x={x}");
            }
        }

        [Fact]
        public void DashListCount_BudgetRejectsExcessiveListsAndKeepsOddRepetition()
        {
            string[] atBudget = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Repeat("1", 64));
            string[] overBudget = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Repeat("1", 65));
            string[] oddAtBudget = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Repeat("1", 63));

            using var atLimit = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"" +
                string.Join(" ", atBudget) + "\"/></svg>");
            using var pastLimit = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"" +
                string.Join(" ", overBudget) + "\"/></svg>");
            using var oddAtLimit = _renderer.Render(
                DashDocOpen +
                "<path d=\"M 0 20 L 200 20\" fill=\"none\" stroke=\"black\" " +
                "stroke-width=\"20\" stroke-dasharray=\"" +
                string.Join(" ", oddAtBudget) + "\"/></svg>");

            Assert.True(atLimit.Success, atLimit.ErrorMessage);
            Assert.True(pastLimit.Success, pastLimit.ErrorMessage);
            Assert.True(oddAtLimit.Success, oddAtLimit.ErrorMessage);

            bool[] atLimitMask = RowPaintedMask(atLimit.Bitmap, 20, 200);
            for (int x = 0; x < 200; x++)
            {
                Assert.Equal(x % 2 == 0, atLimitMask[x]);
            }

            bool[] pastLimitMask = RowPaintedMask(pastLimit.Bitmap, 20, 200);
            for (int x = 0; x < 200; x++)
            {
                Assert.True(pastLimitMask[x], $"the dash budget must fall back to a solid stroke at x={x}");
            }

            bool[] oddMask = RowPaintedMask(oddAtLimit.Bitmap, 20, 200);
            for (int x = 0; x < 200; x++)
            {
                Assert.Equal(x % 2 == 0, oddMask[x]);
            }
        }

        private const string Square100 =
            "<svg width=\"100\" height=\"100\" xmlns=\"http://www.w3.org/2000/svg\">";

        private static void AssertPixel(
            SKBitmap bitmap,
            int x,
            int y,
            SKColor expected,
            string label)
        {
            SKColor actual = bitmap.GetPixel(x, y);
            Assert.True(
                actual == expected,
                $"{label} at {x},{y}: expected {expected} but rendered {actual}");
        }

        [Theory]
        [InlineData("linearGradient")]
        [InlineData("radialGradient")]
        public void NonInvertibleGradientTransform_UsesPaintFallback(string server)
        {
            foreach (string transform in new[] { "scale(0)", "matrix(0, 0, 0, 0, 0, 0)" })
            {
                using var result = _renderer.Render(
                    Square100 + "<" + server + " id='g' gradientTransform='" + transform + "'>" +
                    "<stop offset='0' stop-color='yellow'/>" +
                    "<stop offset='1' stop-color='red'/></" + server + ">" +
                    "<rect width='50' height='100' fill='url(#g) green'/></svg>");

                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
                AssertPixel(
                    result.Bitmap, 25, 50, SKColors.Green,
                    server + " gradientTransform='" + transform + "' must fall back to the paint colour");
            }
        }

        [Fact]
        public void WptPserversGradientTransformTemplate_CssTransformBeatsAttribute()
        {
            const string Stops =
                "<stop offset='0' stop-color='green'/><stop offset='0.5' stop-color='green'/>" +
                "<stop offset='1' stop-color='red'/>";
            const string Body =
                "<rect width='100' height='100' fill='url(#g)'/></svg>";

            using var scaled = _renderer.Render(
                Square100 + "<linearGradient id='g' style='transform: scale(2)'>" + Stops +
                "</linearGradient>" + Body);
            using var attributeOnly = _renderer.Render(
                Square100 + "<linearGradient id='g' gradientTransform='scale(2)'>" + Stops +
                "</linearGradient>" + Body);
            using var overrides = _renderer.Render(
                Square100 + "<linearGradient id='g' gradientTransform='scale(0)' " +
                "style='transform: scale(2)'>" + Stops +
                "</linearGradient>" + Body);
            using var reset = _renderer.Render(
                Square100 + "<linearGradient id='g' style='transform: none'>" + Stops +
                "</linearGradient>" + Body);
            using var untransformed = _renderer.Render(
                Square100 + "<linearGradient id='g'>" + Stops +
                "</linearGradient>" + Body);

            Assert.True(scaled.Success, scaled.ErrorMessage);
            Assert.True(attributeOnly.Success, attributeOnly.ErrorMessage);
            Assert.True(overrides.Success, overrides.ErrorMessage);
            Assert.True(reset.Success, reset.ErrorMessage);
            Assert.True(untransformed.Success, untransformed.ErrorMessage);

            for (int x = 0; x < 100; x++)
            {
                AssertPixel(
                    scaled.Bitmap, x, 50, SKColors.Green,
                    "a CSS transform on the gradient must act as its gradientTransform");
                AssertPixel(
                    attributeOnly.Bitmap, x, 50, SKColors.Green,
                    "gradientTransform must stretch the gradient over twice the box");
                AssertPixel(
                    overrides.Bitmap, x, 50, SKColors.Green,
                    "the CSS transform must outrank gradientTransform");
            }

            Assert.True(
                untransformed.Bitmap.GetPixel(99, 50) != SKColors.Green,
                "an untransformed gradient must end on its last stop at the right edge");

            AssertEquivalent(reset.Bitmap, untransformed.Bitmap, "transform: none");
        }

        [Fact]
        public void WptPserversEmptyGradientTransformAttribute_DoesNotBlockTemplateInheritance()
        {
            using var linear = _renderer.Render(
                Square100 + "<linearGradient id='lg1' gradientTransform='translate(1,0)'>" +
                "<stop offset='0' stop-color='green'/><stop offset='1' stop-color='red'/>" +
                "</linearGradient><linearGradient id='lg' href='#lg1' gradientTransform=''>" +
                "<stop offset='0' stop-color='green'/><stop offset='1' stop-color='red'/>" +
                "</linearGradient><rect width='100' height='100' fill='url(#lg)'/></svg>");

            using var radial = _renderer.Render(
                Square100 + "<radialGradient id='rg1' gradientTransform='translate(0,-2)'>" +
                "<stop offset='0' stop-color='red'/><stop offset='1' stop-color='green'/>" +
                "</radialGradient><radialGradient id='rg' href='#rg1' gradientTransform=''>" +
                "<stop offset='0' stop-color='red'/><stop offset='1' stop-color='green'/>" +
                "</radialGradient><rect width='100' height='100' fill='url(#rg)'/></svg>");

            Assert.True(linear.Success, linear.ErrorMessage);
            Assert.True(radial.Success, radial.ErrorMessage);

            for (int y = 0; y < 100; y += 7)
            {
                for (int x = 0; x < 100; x += 7)
                {
                    AssertPixel(
                        linear.Bitmap, x, y, SKColors.Green,
                        "an empty gradientTransform must not hide the template transform");
                    AssertPixel(
                        radial.Bitmap, x, y, SKColors.Green,
                        "an empty gradientTransform must not hide the template transform");
                }
            }
        }

        [Fact]
        public void WptPserversRadialGradientFocalRadius_MatchesShiftedStopsWithoutFr()
        {
            const string FocalStops =
                "<stop offset='0' stop-color='#0000ff'/><stop offset='0.5' stop-color='#00ffff'/>" +
                "<stop offset='1' stop-color='#ffff00'/>";
            const string ShiftedStops =
                "<stop offset='0.5' stop-color='#0000ff'/><stop offset='0.75' stop-color='#00ffff'/>" +
                "<stop offset='1' stop-color='#ffff00'/>";
            const string Open = "<svg width='240' height='240' xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><radialGradient id='g' ";

            using var userSpace = _renderer.Render(
                Open + "gradientUnits='userSpaceOnUse' cx='120' cy='120' fx='120' fy='120' " +
                "r='100' fr='50'>" + FocalStops + "</radialGradient></defs>" +
                "<rect width='240' height='240' fill='url(#g)'/></svg>");
            using var userSpaceReference = _renderer.Render(
                Open + "gradientUnits='userSpaceOnUse' cx='120' cy='120' r='100'>" +
                ShiftedStops + "</radialGradient></defs>" +
                "<rect width='240' height='240' fill='url(#g)'/></svg>");

            using var boundingBox = _renderer.Render(
                Open + "cx='0.5' cy='0.5' r='0.5' fr='0.25'>" + FocalStops +
                "</radialGradient></defs>" +
                "<rect width='240' height='240' fill='url(#g)'/></svg>");
            using var boundingBoxReference = _renderer.Render(
                Open + "cx='0.5' cy='0.5' r='0.5'>" + ShiftedStops +
                "</radialGradient></defs>" +
                "<rect width='240' height='240' fill='url(#g)'/></svg>");

            Assert.True(userSpace.Success, userSpace.ErrorMessage);
            Assert.True(userSpaceReference.Success, userSpaceReference.ErrorMessage);
            Assert.True(boundingBox.Success, boundingBox.ErrorMessage);
            Assert.True(boundingBoxReference.Success, boundingBoxReference.ErrorMessage);

            AssertPixel(userSpace.Bitmap, 120, 120, SKColors.Blue, "the focal centre holds the first stop");
            AssertPixel(userSpace.Bitmap, 145, 120, SKColors.Blue, "the focal disc holds the first stop");
            AssertPixel(userSpace.Bitmap, 95, 120, SKColors.Blue, "the focal disc holds the first stop");
            AssertPixel(boundingBox.Bitmap, 145, 120, SKColors.Blue, "the focal disc holds the first stop");

            AssertEquivalent(userSpace.Bitmap, userSpaceReference.Bitmap, "userSpaceOnUse fr=50");
            AssertEquivalent(boundingBox.Bitmap, boundingBoxReference.Bitmap, "objectBoundingBox fr=0.25");
        }

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

        [Fact]
        public void WptPserversStopColorCurrentColor_ResolvesAgainstInheritedColor()
        {
            using var result = _renderer.Render(
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg' " +
                "style='color: green'><defs><linearGradient id='g'>" +
                "<stop stop-color='currentcolor'/></linearGradient></defs>" +
                "<rect width='50' height='100' fill='url(#g)'/>" +
                "<rect width='50' height='100' x='50' fill='currentcolor'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            for (int y = 0; y < 100; y += 11)
            {
                for (int x = 0; x < 100; x += 11)
                {
                AssertPixel(
                    result.Bitmap, x, y, SKColors.Green,
                    "stop-color:currentcolor must follow the inherited color");
                }
            }
        }

        [Fact]
        public void WptStructOuterSvgTransform_AppliesRootTransformToChildren()
        {
            using var result = _renderer.Render(
                "<svg width='400' height='400' transform='translate(100,0)' " +
                "xmlns='http://www.w3.org/2000/svg'>" +
                "<svg width='50' height='50'><rect width='50' height='50' fill='green'/></svg>" +
                "</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            for (int y = 5; y < 45; y += 13)
            {
                for (int x = 105; x < 145; x += 13)
                {
                    AssertPixel(
                        result.Bitmap, x, y, SKColors.Green,
                        "a transform on the outer svg must move the nested viewport");
                }
            }
            for (int y = 5; y < 45; y += 13)
            {
                for (int x = 5; x < 45; x += 13)
                {
                    Assert.Equal(
                        0, result.Bitmap.GetPixel(x, y).Alpha);
                }
            }
        }

        [Fact]
        public void WptStructOuterSvgTransform_ComposesWithViewBoxAndZoom()
        {
            using var withViewBox = _renderer.Render(
                "<svg width='200' height='200' viewBox='0 0 100 100' " +
                "transform='translate(50,0)' xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            Assert.True(withViewBox.Success, withViewBox.ErrorMessage);
            for (int y = 10; y < 90; y += 17)
            {
                for (int x = 110; x < 190; x += 17)
                {
                    AssertPixel(
                        withViewBox.Bitmap, x, y, SKColors.Green,
                        "the root transform must compose with the viewBox mapping");
                }
            }
            for (int y = 10; y < 90; y += 17)
            {
                for (int x = 10; x < 90; x += 17)
                {
                    Assert.Equal(
                        0, withViewBox.Bitmap.GetPixel(x, y).Alpha);
                }
            }
        }

        [Fact]
        public void WptStructUseEncoding_PercentDecodedFragmentResolvesTarget()
        {
            using var result = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><rect id='a b' width='100' height='100' fill='green'/></defs>" +
                "<rect width='100' height='100' fill='red'/>" +
                "<use href='#a%20b'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            for (int y = 0; y < 100; y += 11)
            {
                for (int x = 0; x < 100; x += 11)
                {
                    AssertPixel(
                        result.Bitmap, x, y, SKColors.Green,
                        "a percent-encoded use fragment must resolve the same-document id");
                }
            }
        }

        [Fact]
        public void WptStructUseEncoding_MalformedEscapeStaysLiteralAndExternalHrefStaysRejected()
        {
            using var malformed = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><rect id='a%zz' width='100' height='100' fill='green'/></defs>" +
                "<rect width='100' height='100' fill='red'/><use href='#a%zz'/></svg>");
            using var truncated = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><rect id='a%2' width='100' height='100' fill='green'/></defs>" +
                "<rect width='100' height='100' fill='red'/><use href='#a%2'/></svg>");
            using var overDecoded = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><rect id='a b' width='100' height='100' fill='green'/></defs>" +
                "<rect width='100' height='100' fill='red'/><use href='#a%2'/></svg>");
            using var external = _renderer.Render(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='100' height='100' fill='red'/>" +
                "<use href='https://example.invalid/a%20b.svg#g'/></svg>");

            Assert.True(malformed.Success, malformed.ErrorMessage);
            Assert.True(truncated.Success, truncated.ErrorMessage);
            Assert.True(overDecoded.Success, overDecoded.ErrorMessage);
            AssertPixel(
                malformed.Bitmap, 50, 50, SKColors.Green,
                "a malformed percent escape is not an escape and must stay literal");
            AssertPixel(
                truncated.Bitmap, 50, 50, SKColors.Green,
                "a truncated percent escape must stay literal");
            AssertPixel(
                overDecoded.Bitmap, 50, 50, SKColors.Red,
                "a truncated escape must not decode into a different id");
            Assert.False(external.Success, "a non-local use href must fail closed");
            Assert.True(
                external.HadResourceRejection,
                "a non-local use href must stay a resource rejection");
            Assert.Contains("external-resource", external.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void WptStructUseSvgInlineCss_UseAttributesWinOverCssDeclarations()
        {
            using var referencingSvg = _renderer.Render(
                "<svg width='500' height='500' xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><svg id='svg' width='200' height='200'>" +
                "<rect width='400' height='400' fill='green'/></svg></defs>" +
                "<use href='#svg' width='100' height='100' " +
                "style='width:200px; height:200px'/></svg>");
            using var referencingSymbol = _renderer.Render(
                "<svg width='500' height='500' xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><symbol id='sym' width='200' height='200'>" +
                "<rect width='400' height='400' fill='green'/></symbol></defs>" +
                "<use href='#sym' width='100' height='100' " +
                "style='width:200px; height:200px'/></svg>");
            using var unsizedSymbol = _renderer.Render(
                "<svg width='500' height='500' xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><symbol id='sym'><rect width='400' height='400' fill='green'/></symbol></defs>" +
                "<use href='#sym' width='100' height='100' " +
                "style='width:200px; height:200px'/></svg>");

            foreach (var result in new[] { referencingSvg, referencingSymbol, unsizedSymbol })
            {
                Assert.True(result.Success, result.ErrorMessage);
                for (int y = 5; y < 95; y += 15)
                {
                    for (int x = 5; x < 95; x += 15)
                    {
                        AssertPixel(
                            result.Bitmap, x, y, SKColors.Green,
                            "the use width/height attributes must size the instance");
                    }
                }
                for (int y = 105; y < 195; y += 15)
                {
                    for (int x = 5; x < 195; x += 15)
                    {
                        Assert.Equal(
                            0, result.Bitmap.GetPixel(x, y).Alpha);
                    }
                }
            }
        }

        [Fact]
        public void WptStructNestedSvgThroughDisplayContents_ForeignSubtreeIsNotRendered()
        {
            using var result = _renderer.Render(
                "<svg viewBox='0 0 400 400' width='400' height='400' stroke='none' " +
                "xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:h='http://www.w3.org/1999/xhtml'>" +
                "<rect x='0' y='0' width='100' height='100' fill='green'/>" +
                "<h:div style='display: contents'><svg width='300' height='300'>" +
                "<rect x='5' y='5' width='100' height='100' fill='red'/></svg></h:div></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            for (int y = 0; y < 100; y += 11)
            {
                for (int x = 0; x < 100; x += 11)
                {
                    AssertPixel(
                        result.Bitmap, x, y, SKColors.Green,
                        "the SVG child of the document must still render");
                }
            }
            for (int y = 120; y < 400; y += 17)
            {
                for (int x = 0; x < 400; x += 17)
                {
                    Assert.Equal(
                        0, result.Bitmap.GetPixel(x, y).Alpha);
                }
            }
        }
    }
}
