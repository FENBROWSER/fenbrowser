using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;
using FenBrowser.Core;

namespace FenBrowser.Tests.Engine
{
    public class FlexLayoutTests
    {
        private CssComputed MockStyle(string display = "flex", string direction = "row", string wrap = "nowrap", double width = 100, double height = 100)
        {
            var s = new CssComputed();
            s.Display = display;
            s.FlexDirection = direction;
            s.FlexWrap = wrap;
            s.Width = width;
            s.Height = height;
            return s;
        }

        private Element MockElement(string tag = "DIV")
        {
            return new Element(tag);
        }

        [Fact]
        public void Measure_Row_NoWrap()
        {
            // Container: 500x100
            // Items: 2 items, 100x50 each
            // Expect: MaxChildWidth = 200, ContentHeight = 50
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);

            var measureChild = new Func<Node, SKSize, int, bool, LayoutMetrics>((n, size, d, shrink) => 
            {
                return new LayoutMetrics { MaxChildWidth = 100, ContentHeight = 50 };
            });

            var getStyle = new Func<Node, CssComputed>((n) => 
            {
                if (n == container) return MockStyle("flex", "row", "nowrap", 500, 100);
                return new CssComputed();
            });

            var result = CssFlexLayout.Measure(
                container, 
                new SKSize(500, float.MaxValue), 
                measureChild,
                getStyle,
                (n, s) => false, // ShouldHide
                0);

            Assert.Equal(200, result.MaxChildWidth);
            Assert.Equal(50, result.ContentHeight);
        }

        [Fact]
        public void Measure_Row_Wrap()
        {
            // Container: 150 width.
            // Items: 2 items, 100 width each.
            // Expect: Wrap to 2 lines. 
            // Width = 100 (max line width). Height = 50 + 50 = 100.
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);

            var measureChild = new Func<Node, SKSize, int, bool, LayoutMetrics>((n, size, d, shrink) => 
            {
                return new LayoutMetrics { MaxChildWidth = 100, ContentHeight = 50 };
            });

            var getStyle = new Func<Node, CssComputed>((n) => 
            {
                if (n == container) return MockStyle("flex", "row", "wrap", 150, 200);
                return new CssComputed();
            });

            var result = CssFlexLayout.Measure(
                container, 
                new SKSize(150, float.MaxValue), 
                measureChild,
                getStyle,
                (n, s) => false,
                0);

            Assert.Equal(100, result.MaxChildWidth); // Line width
            Assert.Equal(100, result.ContentHeight); // 2 lines * 50
        }
        
        [Fact]
        public void Measure_Column()
        {
            // Container: Column
            // Items: 2 items, 100x50 each
            // Expect: Width = 100, Height = 100
            
            var container = MockElement();
            container.AppendChild(MockElement());
            container.AppendChild(MockElement());

            var measureChild = new Func<Node, SKSize, int, bool, LayoutMetrics>((n, size, d, shrink) => 
            {
                return new LayoutMetrics { MaxChildWidth = 100, ContentHeight = 50 };
            });

            var getStyle = new Func<Node, CssComputed>((n) => 
            {
                if (n == container) return MockStyle("flex", "column", "nowrap");
                return new CssComputed();
            });

            var result = CssFlexLayout.Measure(
                container, 
                new SKSize(500, float.MaxValue), 
                measureChild,
                getStyle,
                (n, s) => false,
                0);

            Assert.Equal(100, result.MaxChildWidth);
            Assert.Equal(100, result.ContentHeight);
        }

        // ============== ARRANGE TESTS ==============
        
        [Fact]
        public void Arrange_JustifyContent_Center()
        {
            // Container: 500px width, 3 items of 100px each = 300px total
            // justify-content: center should leave 100px margin on each side (200px free / 2)
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            var child3 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            container.AppendChild(child3);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var style = MockStyle("flex", "row", "nowrap", 500, 100);
            style.JustifyContent = "center";
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? style : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            // With center justification, first item should start at (500 - 300) / 2 = 100
            Assert.True(childPositions.ContainsKey(child1));
            Assert.Equal(100, childPositions[child1].Left, 1);
        }
        
        [Fact]
        public void Arrange_JustifyContent_SpaceBetween()
        {
            // Container: 500px, 2 items of 100px = 200px total, 300px free
            // space-between: first at 0, last at 400 (500-100)
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var style = MockStyle("flex", "row", "nowrap", 500, 100);
            style.JustifyContent = "space-between";
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? style : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            Assert.Equal(0, childPositions[child1].Left, 1);
            Assert.Equal(400, childPositions[child2].Left, 1);
        }
        
        [Fact]
        public void Arrange_JustifyContent_SpaceEvenly()
        {
            // Container: 400px, 2 items of 100px = 200px total, 200px free
            // space-evenly: gaps = 200 / (2+1) = 66.67px
            // Positions: 66.67, 166.67+100 = 266.67
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var style = MockStyle("flex", "row", "nowrap", 400, 100);
            style.JustifyContent = "space-evenly";
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 400, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? style : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            // First item starts at evenly distributed position
            float expectedGap = 200f / 3f; // ~66.67
            Assert.InRange(childPositions[child1].Left, expectedGap - 1, expectedGap + 1);
        }
        
        [Fact]
        public void Arrange_AlignItems_Center()
        {
            // Container: 500x100, item is 50px tall
            // align-items: center should center vertically: (100-50)/2 = 25
            
            var container = MockElement();
            var child1 = MockElement();
            container.AppendChild(child1);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var style = MockStyle("flex", "row", "nowrap", 500, 100);
            style.AlignItems = "center";
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? style : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            Assert.Equal(25, childPositions[child1].Top, 1);
        }
        
        [Fact]
        public void Arrange_AlignItems_FlexEnd()
        {
            // Container: 500x100, item is 50px tall
            // align-items: flex-end should position at bottom: 100-50 = 50
            
            var container = MockElement();
            var child1 = MockElement();
            container.AppendChild(child1);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var style = MockStyle("flex", "row", "nowrap", 500, 100);
            style.AlignItems = "flex-end";
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? style : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            Assert.Equal(50, childPositions[child1].Top, 1);
        }
        
        [Fact]
        public void Arrange_FlexGrow_DistributesEvenly()
        {
            // Container: 500px, 2 items with flex-grow: 1, base size 100px each
            // Free space: 500 - 200 = 300px, split evenly = 150px each
            // Final sizes: 100 + 150 = 250px each
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var containerStyle = MockStyle("flex", "row", "nowrap", 500, 100);
            var childStyle = new CssComputed { Width = 100, Height = 50, FlexGrow = 1 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? containerStyle : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            // Each item should be 250px wide
            Assert.Equal(250, childPositions[child1].Width, 1);
            Assert.Equal(250, childPositions[child2].Width, 1);
        }
        
        [Fact]
        public void Arrange_FlexGrow_WeightedDistribution()
        {
            // Container: 400px, item1 flex-grow:1, item2 flex-grow:3
            // Base size: 100px each = 200px total, 200px free
            // item1 gets 200 * 1/4 = 50px extra -> 150px
            // item2 gets 200 * 3/4 = 150px extra -> 250px
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var containerStyle = MockStyle("flex", "row", "nowrap", 400, 100);
            var child1Style = new CssComputed { Width = 100, Height = 50, FlexGrow = 1 };
            var child2Style = new CssComputed { Width = 100, Height = 50, FlexGrow = 3 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 400, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => {
                    if (n == container) return containerStyle;
                    if (n == child1) return child1Style;
                    return child2Style;
                },
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            Assert.Equal(150, childPositions[child1].Width, 1);
            Assert.Equal(250, childPositions[child2].Width, 1);
        }
        
        [Fact]
        public void Arrange_Gap_AddsSpacing()
        {
            // Container: 500px, 3 items of 100px, gap: 20px
            // Total with gaps: 300 + 40 = 340px
            // Positions: 0, 120, 240
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            var child3 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            container.AppendChild(child3);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var containerStyle = MockStyle("flex", "row", "nowrap", 500, 100);
            containerStyle.Gap = 20;
            containerStyle.ColumnGap = 20;
            
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? containerStyle : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            Assert.Equal(0, childPositions[child1].Left, 1);
            Assert.Equal(120, childPositions[child2].Left, 1);
            Assert.Equal(240, childPositions[child3].Left, 1);
        }
        
        [Fact]
        public void Measure_Gap_IncludesSpacing()
        {
            // Container: row, 2 items 100px each, gap: 20px
            // Total width should be 100 + 20 + 100 = 220px
            
            var container = MockElement();
            container.AppendChild(MockElement());
            container.AppendChild(MockElement());
            
            var containerStyle = MockStyle("flex", "row", "nowrap", 500, 100);
            containerStyle.Gap = 20;
            containerStyle.ColumnGap = 20;
            
            var result = CssFlexLayout.Measure(
                container,
                new SKSize(500, float.MaxValue),
                (n, size, d, shrink) => new LayoutMetrics { MaxChildWidth = 100, ContentHeight = 50 },
                (n) => n == container ? containerStyle : new CssComputed(),
                (n, s) => false,
                0);
            
            Assert.Equal(220, result.MaxChildWidth);
        }
        
        [Fact]
        public void Arrange_Column_VerticalLayout()
        {
            // Column flex: items stacked vertically
            
            var container = MockElement();
            var child1 = MockElement();
            var child2 = MockElement();
            container.AppendChild(child1);
            container.AppendChild(child2);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var containerStyle = MockStyle("flex", "column", "nowrap", 200, 200);
            var childStyle = new CssComputed { Width = 100, Height = 50 };
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 200, 200),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? containerStyle : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            // Items should be stacked vertically
            Assert.Equal(0, childPositions[child1].Top, 1);
            Assert.Equal(50, childPositions[child2].Top, 1);
        }
        
        [Fact]
        public void Arrange_AlignItems_Stretch()
        {
            // Container: 500x100, item has no explicit height
            // align-items: stretch should make item fill cross-axis
            
            var container = MockElement();
            var child1 = MockElement();
            container.AppendChild(child1);
            
            var childPositions = new Dictionary<Node, SKRect>();
            
            var containerStyle = MockStyle("flex", "row", "nowrap", 500, 100);
            containerStyle.AlignItems = "stretch";
            
            var childStyle = new CssComputed { Width = 100 }; // No height specified
            
            CssFlexLayout.Arrange(
                container,
                new SKRect(0, 0, 500, 100),
                (node, rect, depth) => childPositions[node] = rect,
                (n) => n == container ? containerStyle : childStyle,
                (n) => new SKSize(100, 50),
                (n, s) => false,
                0);
            
            // Item should stretch to fill container height
            Assert.Equal(100, childPositions[child1].Height, 1);
        }
    }
}
