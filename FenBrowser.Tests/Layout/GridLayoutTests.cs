using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridLayoutTests
    {
        private Element CreateGridContainer(int childCount)
        {
            var container = new Element("div");
            for (int i = 0; i < childCount; i++)
            {
                container.AppendChild(new Element("div"));
            }
            return container;
        }

        private Dictionary<Node, CssComputed> CreateStyles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle
            };
            
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed();
            }
            
            return styles;
        }

        // ============== TRACK PARSING TESTS ==============

        [Fact]
        public void Measure_FixedColumns_CalculatesCorrectWidth()
        {
            // Container with "100px 200px 100px" columns
            var container = CreateGridContainer(3);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 200px 100px"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(500, 500), styles, 0);
            
            // Total width: 100 + 200 + 100 = 400px
            Assert.Equal(400, result.MaxChildWidth);
        }

        [Fact]
        public void Measure_FrColumns_DistributesSpace()
        {
            // Container 600px with "1fr 2fr" columns
            var container = CreateGridContainer(2);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "1fr 2fr"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(600, 500), styles, 0);
            
            // Total FR = 3, available = 600
            // 1fr = 200, 2fr = 400
            Assert.Equal(600, result.MaxChildWidth);
        }

        [Fact]
        public void Measure_MixedColumns_CombinesFixedAndFr()
        {
            // Container 500px with "100px 1fr 100px" columns
            var container = CreateGridContainer(3);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 1fr 100px"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(500, 500), styles, 0);
            
            // Fixed: 200px, remaining for 1fr: 300px
            // Total: 500px
            Assert.Equal(500, result.MaxChildWidth);
        }

        [Fact]
        public void Measure_WithGap_IncludesGapInSize()
        {
            // Container with 2 columns of 100px each, gap: 20px
            var container = CreateGridContainer(2);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                ColumnGap = 20
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(500, 500), styles, 0);
            
            // Total: 100 + 20 (gap) + 100 = 220px
            Assert.Equal(220, result.MaxChildWidth);
        }

        [Fact]
        public void Measure_PercentColumns_CalculatesFromContainer()
        {
            // Container 400px with "50% 50%" columns
            var container = CreateGridContainer(2);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "50% 50%"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(400, 500), styles, 0);
            
            // 50% of 400 = 200 each, total = 400
            Assert.Equal(400, result.MaxChildWidth);
        }

        // ============== ARRANGE TESTS ==============

        [Fact]
        public void Arrange_TwoByTwoGrid_PositionsCorrectly()
        {
            // 2 columns x 2 rows grid
            var container = CreateGridContainer(4);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                GridTemplateRows = "50px 50px"
            };
            
            var styles = CreateStyles(container, style);
            var boxes = new Dictionary<Node, BoxModel>();
            var positions = new Dictionary<Node, SKRect>();
            
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 200, 100),
                styles,
                boxes,
                0,
                (node, rect, depth) => positions[node] = rect);
            
            var children = new List<Node>(container.Children);
            
            // Item 0: column 1, row 1 -> (0, 0)
            Assert.Equal(0, positions[children[0]].Left, 1);
            Assert.Equal(0, positions[children[0]].Top, 1);
            
            // Item 1: column 2, row 1 -> (100, 0)
            Assert.Equal(100, positions[children[1]].Left, 1);
            Assert.Equal(0, positions[children[1]].Top, 1);
            
            // Item 2: column 1, row 2 -> (0, 50)
            Assert.Equal(0, positions[children[2]].Left, 1);
            Assert.Equal(50, positions[children[2]].Top, 1);
            
            // Item 3: column 2, row 2 -> (100, 50)
            Assert.Equal(100, positions[children[3]].Left, 1);
            Assert.Equal(50, positions[children[3]].Top, 1);
        }

        [Fact]
        public void Arrange_WithGap_AppliesGapBetweenCells()
        {
            // 2 columns with 10px gap
            var container = CreateGridContainer(2);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                GridTemplateRows = "50px",
                ColumnGap = 10
            };
            
            var styles = CreateStyles(container, style);
            var boxes = new Dictionary<Node, BoxModel>();
            var positions = new Dictionary<Node, SKRect>();
            
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 210, 50),
                styles,
                boxes,
                0,
                (node, rect, depth) => positions[node] = rect);
            
            var children = new List<Node>(container.Children);
            
            // Item 0 at x=0
            Assert.Equal(0, positions[children[0]].Left, 1);
            
            // Item 1 at x=110 (100px + 10px gap)
            Assert.Equal(110, positions[children[1]].Left, 1);
        }

        [Fact]
        public void Arrange_ExplicitPlacement_PositionsAtSpecifiedCell()
        {
            // Grid with explicit placement for child
            var container = new Element("div");
            var child1 = new Element("div");
            var child2 = new Element("div");
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px 100px",
                GridTemplateRows = "50px 50px"
            };
            
            // Place child2 at column 3
            var child2Style = new CssComputed
            {
                GridColumnStart = "3"
            };
            
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle,
                [child1] = new CssComputed(),
                [child2] = child2Style
            };
            
            var boxes = new Dictionary<Node, BoxModel>();
            var positions = new Dictionary<Node, SKRect>();
            
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 300, 100),
                styles,
                boxes,
                0,
                (node, rect, depth) => positions[node] = rect);
            
            // child2 should be at column 3 -> x=200
            Assert.Equal(200, positions[child2].Left, 1);
        }

        [Fact]
        public void Arrange_SpanColumns_ItemSpansMultipleCells()
        {
            // Grid with item spanning 2 columns
            var container = new Element("div");
            var spanItem = new Element("div");
            var normalItem = new Element("div");
            container.AppendChild(spanItem);
            container.AppendChild(normalItem);
            
            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px 100px",
                GridTemplateRows = "50px 50px"
            };
            
            // Make first item span 2 columns
            var spanStyle = new CssComputed
            {
                GridColumnStart = "1",
                GridColumnEnd = "span 2"
            };
            
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle,
                [spanItem] = spanStyle,
                [normalItem] = new CssComputed()
            };
            
            var boxes = new Dictionary<Node, BoxModel>();
            var positions = new Dictionary<Node, SKRect>();
            
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 300, 100),
                styles,
                boxes,
                0,
                (node, rect, depth) => positions[node] = rect);
            
            // Span item should be 200px wide (spanning 2 columns)
            Assert.Equal(200, positions[spanItem].Width, 1);
        }

        // ============== AUTO ROW TESTS ==============

        [Fact]
        public void Measure_AutoRows_CreatesImplicitRows()
        {
            // 2 columns but 4 items -> needs 2 rows
            var container = CreateGridContainer(4);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px"
                // No explicit rows
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(200, 500), styles, 0);
            
            // Should have height for 2 implicit rows
            Assert.True(result.ContentHeight > 0);
        }

        [Fact]
        public void Measure_ExplicitRows_UsesSpecifiedHeights()
        {
            var container = CreateGridContainer(2);
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                GridTemplateRows = "80px"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(200, 500), styles, 0);
            
            // Should be 80px tall (explicit row height)
            Assert.Equal(80, result.ContentHeight);
        }

        // ============== EDGE CASES ==============

        [Fact]
        public void Measure_SingleItemGrid_ReturnsValidSize()
        {
            var container = CreateGridContainer(1); // One child
            var style = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px"
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(200, 200), styles, 0);
            
            // Single item grid should have valid dimensions
            Assert.True(result.ContentHeight > 0);
            Assert.Equal(200, result.MaxChildWidth);
        }

        [Fact]
        public void Measure_NoTemplateColumns_CreatesAutoColumns()
        {
            // Grid with no template but 3 items
            var container = CreateGridContainer(3);
            var style = new CssComputed
            {
                Display = "grid"
                // No grid-template-columns
            };
            
            var styles = CreateStyles(container, style);
            
            var result = GridLayoutComputer.Measure(container, new SKSize(600, 500), styles, 0);
            
            // Should create auto columns for each item
            Assert.True(result.MaxChildWidth > 0);
        }
    }
}
