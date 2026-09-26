using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // CSS Grid 1 §8.3 line-based placement and §8.3.1 conflict handling: a
    // negative line counts back from the explicit grid's last line, a span start
    // with a definite end ends there, a start after its end swaps with it, and a
    // start equal to its end spans one track. Google's results page places items
    // with `1/-1`, `2/-2` and `span 7/-2`; taken literally, a negative end line
    // indexed the line array below zero and the whole page failed to render.
    public class GridLinePlacementTests
    {
        // Four 100px columns and two 100px rows, one item placed by the given lines.
        private static SKRect Place(string columnStart, string columnEnd, string rowStart = null, string rowEnd = null,
            string templateRows = "100px 100px")
        {
            var container = new Element("div");
            var item = new Element("div");
            container.AppendChild(item);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "grid",
                    GridTemplateColumns = "100px 100px 100px 100px",
                    GridTemplateRows = templateRows,
                },
                [item] = new CssComputed
                {
                    GridColumnStart = columnStart,
                    GridColumnEnd = columnEnd,
                    GridRowStart = rowStart,
                    GridRowEnd = rowEnd,
                },
            };

            var placed = new Dictionary<Node, SKRect>();
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 1000, 1000),
                styles,
                new Dictionary<Node, BoxModel>(),
                0,
                (node, rect, depth) => placed[node] = rect,
                (node, size, depth) => new LayoutMetrics());
            return placed[item];
        }

        [Theory]
        // -1 is the last explicit line (line 5), so 1/-1 spans all four columns.
        [InlineData("1", "-1", 0, 400)]
        [InlineData("2", "-2", 100, 200)]
        [InlineData("-3", "-1", 200, 200)]
        public void NegativeLinesCountBackFromTheLastExplicitLine(string start, string end, float left, float width)
        {
            var rect = Place(start, end, "1", "2");
            Assert.Equal(left, rect.Left);
            Assert.Equal(width, rect.Width);
        }

        [Fact]
        public void ASpanStartEndsAtTheDefiniteEndLine()
        {
            // span 2 / -2: the end is line 4, so the item covers columns 2 and 3.
            var rect = Place("span 2", "-2", "1", "2");
            Assert.Equal(100, rect.Left);
            Assert.Equal(200, rect.Width);
        }

        [Fact]
        public void AStartAfterItsEndIsSwapped()
        {
            var rect = Place("4", "2", "1", "2");
            Assert.Equal(100, rect.Left);
            Assert.Equal(200, rect.Width);
        }

        [Fact]
        public void AStartEqualToItsEndSpansOneTrack()
        {
            var rect = Place("3", "3", "1", "2");
            Assert.Equal(200, rect.Left);
            Assert.Equal(100, rect.Width);
        }

        [Fact]
        public void ANegativeEndWithNoExplicitRowsIsLineOne()
        {
            // With no explicit rows the last explicit line is line 1, so 1/-1
            // resolves to 1/1 and the item spans one implicit row.
            var rect = Place("1", "2", "1", "-1", templateRows: null);
            Assert.Equal(0, rect.Top);
            Assert.Equal(100, rect.Width);
        }

        [Fact]
        public void LineZeroIsAuto()
        {
            var rect = Place("0", null, "1", "2");
            Assert.Equal(0, rect.Left);
            Assert.Equal(100, rect.Width);
        }
    }
}
