using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class Acid2LayoutTests
    {
        // Helper to run layout on a small tree
        private (MinimalLayoutComputer computer, BoxModel box) LayoutElement(Element root, CssComputed style)
        {
            var styles = new Dictionary<Node, CssComputed> { { root, style } };
            // Ensure implicit body/html like structure if needed, or just root
            // For absolute positioning, we often need a container.
            
            // Create a viewport-sized container (ICB)
            var doc = new Document();
            doc.AppendChild(root);
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            return (computer, computer.GetBox(root));
        }

        [Fact]
        public void AbsolutePositioning_Respects_MinHeight_Constraint()
        {
            // Scenario: Acid2 Scalp/Forehead
            // .top class in Acid2 often uses absolute positioning with constraints
            
            var doc = new Document();
            var container = new Element("DIV");
            doc.AppendChild(container);
            
            var absChild = new Element("DIV");
            container.AppendChild(absChild);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            // Container: Relative, 100x100
            var containerStyle = new CssComputed();
            containerStyle.Display = "block";
            containerStyle.Position = "relative";
            containerStyle.Width = 100;
            containerStyle.Height = 100;
            styles[container] = containerStyle;
            
            // AbsChild: Absolute, Top:0, Bottom:auto, Height:auto, MinHeight: 50
            // If content is empty, height should resolve to 0 normally, but min-height should force it to 50.
            var absStyle = new CssComputed();
            absStyle.Position = "absolute";
            absStyle.Top = 0;
            // Height is auto by default
            absStyle.MinHeight = 50;
            absStyle.BackgroundColor = SKColors.Red;
            styles[absChild] = absStyle;
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var childBox = computer.GetBox(absChild);
            Assert.NotNull(childBox);
            
            Assert.Equal(50, childBox.ContentBox.Height);
        }

        [Fact]
        public void AbsolutePositioning_NegativeMargins()
        {
            // Verify negative margins move the element outside the container
            var doc = new Document();
            var container = new Element("DIV");
            doc.AppendChild(container);
            var absChild = new Element("DIV");
            container.AppendChild(absChild);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var containerStyle = new CssComputed();
            containerStyle.Display = "block";
            containerStyle.Position = "relative";
            containerStyle.Width = 100;
            containerStyle.Height = 100;
            containerStyle.Margin = new FenBrowser.Core.Thickness(50); // Move container to 50,50
            styles[container] = containerStyle;
            
            var absStyle = new CssComputed();
            absStyle.Position = "absolute";
            absStyle.Top = 0;
            absStyle.Left = 0;
            absStyle.Width = 20;
            absStyle.Height = 20;
            absStyle.Margin = new FenBrowser.Core.Thickness(-10, -10, 0, 0); // Left -10, Top -10
            styles[absChild] = absStyle;
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var containerBox = computer.GetBox(container);
            var childBox = computer.GetBox(absChild);
            Assert.NotNull(containerBox);
            Assert.NotNull(childBox);
            
            var cbX = containerBox.PaddingBox.Left;
            var cbY = containerBox.PaddingBox.Top;
            
            // Child position relative to CB:
            // Top = 0, Left = 0.
            // Margin Top = -10, Margin Left = -10.
            // X = Left + MarginLeft = 0 - 10 = -10.
            // Y = Top + MarginTop = 0 - 10 = -10.
            // Absolute X/Y in document space should be Container.PaddingBox.X - 10
            
            var expectedX = cbX - 10;
            var actualX = childBox.BorderBox.Left;
            
            // Note: Assert.Equal had issues with precision/reporting, using explicit check
            Assert.True(Math.Abs(expectedX - actualX) < 1.0f, $"FAILURE: Expected {expectedX} (CB={cbX}), Actual {actualX}. Box={childBox.BorderBox}");
            
            Assert.True(Math.Abs((cbY - 10) - childBox.BorderBox.Top) < 1.0f, "Y Mismatch");
        }
    }
}
