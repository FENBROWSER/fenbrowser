using System;
using FenBrowser.Core.Dom;
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

            var measureChild = new Func<Node, SKSize, int, LayoutMetrics>((n, size, d) => 
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

            var measureChild = new Func<Node, SKSize, int, LayoutMetrics>((n, size, d) => 
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

            var measureChild = new Func<Node, SKSize, int, LayoutMetrics>((n, size, d) => 
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
    }
}
