using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// CSS Images 3 linear gradients as the paint tree builds them, sampled from the
    /// shader. github.com's hero is `linear-gradient(to bottom, #000240, #000 117%)`: it
    /// painted upside down (black on top) because `to bottom` lost its "to" from inside
    /// "bottom", and the 117% stop was clamped and then dropped.
    /// </summary>
    public class CssLinearGradientPaintTests
    {
        private static SKBitmap Paint(string backgroundImage, int width, int height, SKColor? backdrop = null)
        {
            var element = new Element("div");
            var styles = new Dictionary<Node, CssComputed>
            {
                [element] = new CssComputed { Display = "block", BackgroundImage = backgroundImage },
            };
            var boxes = new Dictionary<Node, BoxModel> { [element] = BoxModel.FromContentBox(0, 0, width, height) };

            var tree = NewPaintTreeBuilder.Build(element, boxes, styles, width, height, null);
            var nodes = Flatten(tree.Roots).Where(n => ReferenceEquals(n.SourceNode, element)).ToList();

            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(backdrop ?? SKColors.White);
            var gradient = nodes.OfType<BackgroundPaintNode>().FirstOrDefault(n => n.Gradient != null);
            if (gradient != null)
            {
                using var paint = new SKPaint { Shader = gradient.Gradient };
                canvas.DrawRect(new SKRect(0, 0, width, height), paint);
                return bitmap;
            }

            // Background images - gradients included - are rasterised into an image node.
            var image = Assert.Single(nodes.OfType<ImagePaintNode>(), n => n.IsBackgroundImage && n.Bitmap != null);
            canvas.DrawBitmap(image.Bitmap, new SKRect(0, 0, width, height));
            return bitmap;
        }

        private static IEnumerable<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
        {
            foreach (var node in nodes ?? Enumerable.Empty<PaintNodeBase>())
            {
                yield return node;
                foreach (var child in Flatten(node.Children)) yield return child;
            }
        }

        [Fact]
        public void ToBottom_WithAStopPast100Percent_RunsTopToBottomAlongTheExtendedLine()
        {
            // The line runs 117px for a 100px box, so the bottom edge is 99.5/117 of the
            // way to black. Chrome's pixels at x=50: y=0 blue 63, y=50 blue 36, y=99 blue 10.
            using var bitmap = Paint("linear-gradient(to bottom, #000240, #000 117%)", 100, 100);
            Assert.InRange((int)bitmap.GetPixel(50, 0).Blue, 61, 64);
            Assert.InRange((int)bitmap.GetPixel(50, 50).Blue, 34, 38);
            Assert.InRange((int)bitmap.GetPixel(50, 99).Blue, 8, 12);
        }

        [Theory]
        [InlineData("to top", 0, 99, 0, 0)]      // red at the bottom, blue at the top
        [InlineData("to left", 99, 0, 0, 0)]     // red at the right edge
        [InlineData("to right", 0, 0, 99, 0)]
        public void SideKeywords_PointTheLineThatWay(string direction, int redX, int redY, int blueX, int blueY)
        {
            using var bitmap = Paint($"linear-gradient({direction}, red, blue)", 100, 100);
            var red = bitmap.GetPixel(redX, redY);
            var blue = bitmap.GetPixel(blueX, blueY);
            Assert.True(red.Red > 240 && red.Blue < 15, $"expected red at ({redX},{redY}), got {red}");
            Assert.True(blue.Blue > 240 && blue.Red < 15, $"expected blue at ({blueX},{blueY}), got {blue}");
        }

        [Fact]
        public void CornerKeyword_JoinsTheNeighbouringCornersAtTheMidpoint()
        {
            // §3.4.1: for `to top right` on a 2:1 box the 50% line runs from the top-left
            // corner to the bottom-right one, so those two corners share one colour.
            using var bitmap = Paint("linear-gradient(to top right, red, blue)", 200, 100);
            var topLeft = bitmap.GetPixel(0, 0);
            var bottomRight = bitmap.GetPixel(199, 99);
            Assert.InRange(Math.Abs(topLeft.Red - bottomRight.Red), 0, 6);
            Assert.InRange(Math.Abs(topLeft.Blue - bottomRight.Blue), 0, 6);
            Assert.True(bitmap.GetPixel(199, 0).Blue > 240);   // the named corner
            Assert.True(bitmap.GetPixel(0, 99).Red > 240);     // the opposite corner
        }

        [Fact]
        public void TransparentStop_FadesTheColourOut_InsteadOfPassingThroughGrey()
        {
            // §3.4.3 interpolates premultiplied: halfway from navy to transparent is navy
            // at half alpha. Unpremultiplied, `transparent` (transparent white) dragged
            // the midpoint to a light grey-blue over a white page.
            using var bitmap = Paint("linear-gradient(navy, transparent)", 10, 101, SKColors.White);
            var mid = bitmap.GetPixel(5, 50);
            Assert.InRange((int)mid.Red, 120, 136);
            Assert.InRange((int)mid.Blue, 185, 197);
        }

        [Fact]
        public void RepeatingGradient_RepeatsItsStopsAlongTheLine()
        {
            using var bitmap = Paint("repeating-linear-gradient(90deg, red 0px, red 10px, blue 10px, blue 20px)", 60, 4);
            Assert.True(bitmap.GetPixel(5, 1).Red > 240);
            Assert.True(bitmap.GetPixel(15, 1).Blue > 240);
            Assert.True(bitmap.GetPixel(25, 1).Red > 240);
            Assert.True(bitmap.GetPixel(35, 1).Blue > 240);
        }

        [Fact]
        public void UnpositionedStops_AreSpacedBetweenTheirPositionedNeighbours()
        {
            // §3.5.3: `red, lime, blue 50%` puts lime at 25%; past 50% it is solid blue.
            using var bitmap = Paint("linear-gradient(90deg, red, lime, blue 50%)", 100, 2);
            var quarter = bitmap.GetPixel(25, 0);
            Assert.True(quarter.Green > 240 && quarter.Red < 20, $"lime expected at 25%, got {quarter}");
            Assert.True(bitmap.GetPixel(75, 0).Blue > 250);
        }
    }
}
