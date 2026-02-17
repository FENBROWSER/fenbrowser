using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core; // For Thickness
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class BlockLayoutTests
    {
        [Fact]
        public void VerticalStacking_BlocksStackVertically()
        {
            // divs are block-level, should stack.
            var container = LayoutTestHelper.CreateBlockContainer(3, 100, 50); // 3 items 100x50
            var style = new CssComputed { Display = "block", Width = 500, Height = 500 };
            
            var styles = LayoutTestHelper.CreateStyles(container, style);
            foreach(var child in container.Children)
                styles[child] = new CssComputed { Display = "block", Width = 100, Height = 50 };

            var computer = LayoutTestHelper.CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 500));
            computer.Arrange(container, new SKRect(0, 0, 500, 500));

            var c0 = computer.GetBox(container.Children[0]);
            var c1 = computer.GetBox(container.Children[1]);
            var c2 = computer.GetBox(container.Children[2]);

            // X = 0 (default alignment)
            Assert.Equal(0, c0.ContentBox.Left);
            Assert.Equal(0, c1.ContentBox.Left);
            Assert.Equal(0, c2.ContentBox.Left);

            // Y = Stacking
            Assert.Equal(0, c0.ContentBox.Top);
            Assert.Equal(50, c1.ContentBox.Top);
            Assert.Equal(100, c2.ContentBox.Top);
        }

        [Fact]
        public void MarginCollapsing_Siblings_LargerWins()
        {
            // Div A: Margin-Bottom 20
            // Div B: Margin-Top 30
            // Gap should be 30.
            
            var container = new Element("div");
            var item1 = new Element("div");
            var item2 = new Element("div");
            container.AppendChild(item1);
            container.AppendChild(item2);

            var style = new CssComputed { Display = "block", Width = 500 };
            var styles = LayoutTestHelper.CreateStyles(container, style);
            
            styles[item1] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0,0,0,20) };
            styles[item2] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0,30,0,0) };

            var computer = LayoutTestHelper.CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var c0 = computer.GetBox(item1);
            var c1 = computer.GetBox(item2);

            Assert.Equal(0, c0.ContentBox.Top);
            // Item 1 ends at 50.
            // Gap is Max(20, 30) = 30.
            // Item 2 starts at 50 + 30 = 80.
            Assert.Equal(80, c1.ContentBox.Top);
        }

        [Fact]
        public void AutoWidth_BlockTakesAvailableWidth()
        {
            var container = new Element("div");
            var child = new Element("div");
            container.AppendChild(child);

            // Container fixed width
            var styles = LayoutTestHelper.CreateStyles(container, new CssComputed { Display = "block", Width = 400 });
            // Child auto width (default)
            styles[child] = new CssComputed { Display = "block", Height = 50 }; 

            var computer = LayoutTestHelper.CreateComputer(container, styles);
            computer.Measure(container, new SKSize(400, 600));
            computer.Arrange(container, new SKRect(0, 0, 400, 600));

            var cBox = computer.GetBox(child);
            
            // Should fill 400px
            Assert.Equal(400, cBox.ContentBox.Width);
        }

        [Fact]
        public void AutoWidth_WithMargins_RespectsConstraint()
        {
            // Parent 400px.
            // Child Margin-Left 10, Margin-Right 20.
            // Child Width should be 400 - 10 - 20 = 370.
            
            var container = new Element("div");
            var child = new Element("div");
            container.AppendChild(child);

            var styles = LayoutTestHelper.CreateStyles(container, new CssComputed { Display = "block", Width = 400 });
            styles[child] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(10, 0, 20, 0) };

            var computer = LayoutTestHelper.CreateComputer(container, styles);
            computer.Measure(container, new SKSize(400, 600));
            computer.Arrange(container, new SKRect(0, 0, 400, 600));

            var cBox = computer.GetBox(child);

            Assert.Equal(370, cBox.ContentBox.Width);
            Assert.Equal(10, cBox.ContentBox.Left); // Relative to parent 0
        }
    }
}
