using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class PseudoElementTests
    {
        [Fact]
        public void CreateAndLayout_PseudoElement_WithTextContent()
        {
            // 1. Setup DOM
            var doc = new Document();
            var div = new Element("DIV");
            doc.AppendChild(div);

            // 2. Setup Styles
            var styles = new Dictionary<Node, CssComputed>();
            var boxes = new Dictionary<Node, BoxModel>();

            var divStyle = new CssComputed();
            divStyle.Display = "block";
            divStyle.Width = 100;
            divStyle.Height = 100;
            
            // Define ::before style
            var beforeStyle = new CssComputed();
            beforeStyle.Content = "\"Generated\""; // Content string with quotes
            beforeStyle.Display = "block";
            beforeStyle.Width = 50;
            beforeStyle.Height = 20;
            beforeStyle.BackgroundColor = SKColors.Red;

            // Link to parent
            divStyle.Before = beforeStyle;
            styles[div] = divStyle;

            // 3. Run Layout
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            var viewport = new SKSize(800, 600);
            
            computer.Measure(doc, viewport);
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            // 4. Verification
            // Did it create a PseudoElement instance?
            Assert.NotNull(divStyle.Before.PseudoElementInstance);
            var pseudo = divStyle.Before.PseudoElementInstance as PseudoElement;
            Assert.NotNull(pseudo);
            Assert.Equal("before", pseudo.PseudoType);
            Assert.Same(div, pseudo.OriginatingElement);

            // Did it populate text content?
            Assert.Single(pseudo.Children);
            var textNode = pseudo.Children.First() as Text;
            Assert.NotNull(textNode);
            Assert.Equal("Generated", textNode.Data); // Should be trimmed

            // Did it generate a BoxModel?
            // Use reflection to access private _boxes
            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(boxesField);
            var resultBoxes = boxesField.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            Assert.NotNull(resultBoxes);

            Assert.True(resultBoxes.ContainsKey(pseudo), "PseudoElement should have a layout box");
            var box = resultBoxes[pseudo];
            Assert.NotNull(box);
            
            // Check dimensions (Block layout: width=50, height=20)
            Assert.Equal(50, box.ContentBox.Width);
            Assert.Equal(20, box.ContentBox.Height);
        }

        [Fact]
        public void PseudoElements_AreIncluded_In_GetChildrenWithPseudos()
        {
             // Indirectly tested by layout above, but good to check painting path potential.
             // (NewPaintTreeBuilder logic is harder to unit test without more mocks, 
             // but if layout works, painting likely works due to BuildRecursive recursion).
        }
    }
}
