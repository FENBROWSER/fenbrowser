using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class FlexLayoutTests
    {
        private MinimalLayoutComputer CreateComputer(Element root, Dictionary<Node, CssComputed> styles)
        {
            return new MinimalLayoutComputer(styles, 800, 600);
        }

        private Dictionary<Node, CssComputed> CreateStyles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle
            };
            
            foreach (var child in container.Descendants())
            {
                if (!styles.ContainsKey(child))
                {
                    styles[child] = new CssComputed();
                }
            }
            
            return styles;
        }

        private Element CreateFlexContainer(int childCount)
        {
            var container = new Element("div");
            for (int i = 0; i < childCount; i++)
            {
                var child = new Element("div");
                // Give children some size so they can be arranged
                child.SetAttribute("style", "width: 100px; height: 100px;"); 
                container.AppendChild(child);
            }
            return container;
        }

        [Fact]
        public void JustifyContent_FlexStart_Default()
        {
            // Container 500px wide, 3 items of 100px each
            // space remaining: 200px
            // flex-start: [100][100][100]...
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-start",
                Width = 500
            };

            // Set child styles (width/height needs to be in CssComputed for layout engine if not parsing style attr)
            // Note: MinimalLayoutComputer might look at style attributes via GetStyle, but better to provide CssComputed
            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            
            // Measure & Arrange
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var boxes = computer.GetAllBoxes();
            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);

            // Assert X positions
            Assert.Equal(0, child0.ContentBox.Left);
            Assert.Equal(100, child1.ContentBox.Left);
            Assert.Equal(200, child2.ContentBox.Left);
        }

        [Fact]
        public void JustifyContent_FlexEnd()
        {
            // Container 500px, 3 items (300px total)
            // flex-end: ...[100][100][100]
            // Start position = 500 - 300 = 200
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-end",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(200, child0.ContentBox.Left);
            Assert.Equal(300, computer.GetBox(container.Children[1]).ContentBox.Left);
            Assert.Equal(400, computer.GetBox(container.Children[2]).ContentBox.Left);
        }

        [Fact]
        public void JustifyContent_Center()
        {
            // Container 500px, 3 items (300px total)
            // center: ..[100][100][100]..
            // Margin = (500 - 300) / 2 = 100
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "center",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(100, child0.ContentBox.Left);
            Assert.Equal(200, computer.GetBox(container.Children[1]).ContentBox.Left);
            Assert.Equal(300, computer.GetBox(container.Children[2]).ContentBox.Left);
        }
        [Fact]
        public void JustifyContent_SpaceBetween()
        {
            // Container 500px, 3 items (300px total)
            // space-between: [100]...[100]...[100]
            // Remaining 200px. 2 gaps. Gap = 100px.
            // Pos: 0, 200, 400
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "space-between",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);
            
            Assert.Equal(0, child0.ContentBox.Left);
            Assert.Equal(200, child1.ContentBox.Left);
            Assert.Equal(400, child2.ContentBox.Left);
        }

        [Fact]
        public void JustifyContent_SpaceEvenly()
        {
            // Container 500px, 3 items (300px total)
            // space-evenly: .[100].[100].[100].
            // Remaining 200px. 4 gaps. Gap = 50px.
            // Pos: 50, 250 (50+100+50+100? No. 50(gap)+100(item)+50(gap) = 200 start of next), wait.
            // Items at: 
            // 1: 50
            // 2: 50 + 100 + 50 = 200
            // 3: 200 + 100 + 50 = 350
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "space-evenly",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);
            
            Assert.Equal(50, child0.ContentBox.Left);
            Assert.Equal(200, child1.ContentBox.Left);
            Assert.Equal(350, child2.ContentBox.Left);
        }

        [Fact]
        public void AlignItems_Center()
        {
            // Container height 600px. Item height 100px.
            // Center Y = (600 - 100) / 2 = 250.
            
            var container = CreateFlexContainer(1);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                Width = 500,
                Height = 600
            };

            var styles = CreateStyles(container, style);
            styles[container.Children[0]] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(250, child0.ContentBox.Top);
        }

        [Fact]
        public void AlignItems_Stretch()
        {
            // Container height 600px. Item height auto.
            // Should stretch to 600px.
            
            var container = CreateFlexContainer(1);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "stretch",
                Width = 500,
                Height = 600
            };

            var styles = CreateStyles(container, style);
            // No explicit height set
            styles[container.Children[0]] = new CssComputed { Width = 100 }; // Height defaults to Auto?

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(600, child0.ContentBox.Height);
        }

        [Fact]
        public void FlexWrap_Wrap()
        {
            // Container 300px. Items 150px. 3 items.
            // Row 1: Item 1, Item 2 (total 300px).
            // Row 2: Item 3.
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                FlexWrap = "wrap",
                Width = 300
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 150, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(300, 600));
            computer.Arrange(container, new SKRect(0, 0, 300, 600));

            var c0 = computer.GetBox(container.Children[0]);
            var c1 = computer.GetBox(container.Children[1]);
            var c2 = computer.GetBox(container.Children[2]);
            
            // Row 1
            Assert.Equal(0, c0.ContentBox.Top);
            Assert.Equal(0, c1.ContentBox.Top);
            
            // Row 2 (Top should be >= 100)
            Assert.True(c2.ContentBox.Top >= 100);
            Assert.Equal(0, c2.ContentBox.Left); // Should start new line
        }
        [Fact]
        public void FlexWrap_WrapReverse()
        {
            // Container 300px. Items 150px. 3 items.
            // wrap-reverse:
            // Row 1 (Bottom): Item 1, Item 2.
            // Row 2 (Top): Item 3.
            
            // Note: Since we don't have explicit line-height calculation easily exposed without digging deep,
            // we mainly check the Y order. Row 2 should be ABOVE Row 1.
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                FlexWrap = "wrap-reverse",
                Width = 300,
                Height = 200 // Explicit height to see bottom alignment clearly
            };

            var styles = CreateStyles(container, style);
            // Items height 100px.
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 150, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(300, 200));
            computer.Arrange(container, new SKRect(0, 0, 300, 200));

            var c0 = computer.GetBox(container.Children[0]); // Row 1 (Bottom, because it fills from Start in cross axis, but reverse means Start is Bottom?)
            // Wait: wrap-reverse means "cross-start" and "cross-end" are swapped?
            // "The cross-start line and cross-end line are swapped."
            // So lines are laid out from Bottom to Top (if column direction is down).
            
            // Line 1 contains Item 1, Item 2. It should be at the "Start" of the Block axis?
            // Cross axis direction: valid "row" means cross axis is vertical (top->bottom).
            // wrap-reverse means lines flow bottom->top.
            
            // So Line 1 (items 1, 2) is at Bottom. Y=100 (if height 200 and line height 100).
            // Line 2 (item 3) is at Top. Y=0.
            
            var c2 = computer.GetBox(container.Children[2]);
            
            // Assert Line 2 (Item 3) is ABOVE Line 1 (Item 0)
            Assert.True(c2.ContentBox.Top < c0.ContentBox.Top);
        }

        [Fact]
        public void NestedFlex_ParentAlignsChildFlexContainer()
        {
            // Parent: Flex, Center.
            // Child: Flex, 3 items.
            
            var parent = new Element("div");
            var childContainer = CreateFlexContainer(3); // 3 items of 100px = 300px width.
            parent.AppendChild(childContainer);
            
            var parentStyle = new CssComputed
            {
                Display = "flex",
                JustifyContent = "center",
                Width = 500,
                Height = 500
            };
            
            var childContainerStyle = new CssComputed
            {
                Display = "flex",
                Width = 300, // Explicit width matches content
                Height = 100
            };
            
            var styles = new Dictionary<Node, CssComputed>();
            styles[parent] = parentStyle;
            styles[childContainer] = childContainerStyle;
            foreach(var gc in childContainer.Children)
                 styles[gc] = new CssComputed { Width = 100, Height = 100 };
            
            var computer = CreateComputer(parent, styles);
            computer.Measure(parent, new SKSize(500, 500));
            computer.Arrange(parent, new SKRect(0, 0, 500, 500));
            
            var childBox = computer.GetBox(childContainer);
            
            // Parent centers childContainer. 
            // ChildContainer width 300. Parent 500. Center X = 100.
            Assert.Equal(100, childBox.ContentBox.Left);
            
            // Verify grandchild positions relative to childContainer?
            // Note: GetBox returns coordinates relative to *viewport* or *root* layout context?
            // MinimalLayoutComputer usually returns relative to parent? No, Arrange passes `finalRect` which is usually absolute in the layout context.
            // But internal recursion usually passes offset.
            // Let's check Grandchild.
            // Grandchild 0 should be at 0 relative to childContainer.
            // Grandchild 0 X in global = 100 + 0 = 100.
            
            var gc0 = computer.GetBox(childContainer.Children[0]);
            Assert.Equal(100, gc0.ContentBox.Left);
        }
    }
}
