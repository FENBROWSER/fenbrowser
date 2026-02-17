using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class Acid2PropertiesTests
    {
        private (MinimalLayoutComputer computer, Dictionary<Node, BoxModel> boxes, ImmutablePaintTree tree) RunPipeline(Element root, Dictionary<Node, CssComputed> styles)
        {
            var doc = new Document();
            doc.AppendChild(root);
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            
            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var boxes = boxesField.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            
            var boxDict = new Dictionary<Node, BoxModel>(boxes);
            
            var tree = NewPaintTreeBuilder.Build(doc, boxDict, styles, 800, 600, null);
            return (computer, boxDict, tree);
        }

        [Fact]
        public void Visibility_Hidden_With_Visible_Child()
        {
            // Scenario: Acid2 Eyes/Pupils often rely on this.
            // .parent { visibility: hidden }
            // .child { visibility: visible }
            // Parent should NOT paint background/border, but Child SHOULD paint.
            
            var parent = new Element("DIV");
            var child = new Element("DIV");
            parent.AppendChild(child);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var pStyle = new CssComputed();
            pStyle.Display = "block";
            pStyle.Width = 100;
            pStyle.Height = 100;
            pStyle.BackgroundColor = SKColors.Red; // Should NOT match
            pStyle.Visibility = "hidden";
            styles[parent] = pStyle;
            
            var cStyle = new CssComputed();
            cStyle.Display = "block";
            cStyle.Width = 50;
            cStyle.Height = 50;
            cStyle.BackgroundColor = SKColors.Green; // Should match
            cStyle.Visibility = "visible";
            styles[child] = cStyle;
            
            var (computer, boxes, tree) = RunPipeline(parent, styles);
            
            // Check Layout: Parent should still exist and take space
            Assert.True(boxes.ContainsKey(parent));
            var pBox = boxes[parent];
            Assert.Equal(100, pBox.BorderBox.Width);
            Assert.Equal(100, pBox.BorderBox.Height);
            
            // Perform Flatten
            var paintNodes = FlattenTree(tree.Roots);
            
            // Verify Parent (Red) is NOT present
            var redNode = paintNodes.OfType<BackgroundPaintNode>().FirstOrDefault(n => n.Color.Equals(SKColors.Red));
            Assert.Null(redNode);
            
            // Verify Child (Green) IS present
            var greenNode = paintNodes.OfType<BackgroundPaintNode>().FirstOrDefault(n => n.Color.Equals(SKColors.Green));
            Assert.NotNull(greenNode);
        }

        [Fact]
        public void Overflow_Hidden_Clips_Content()
        {
            // Scenario: Acid2 overlapping elements often use overflow hidden to clip shapes
            var container = new Element("DIV");
            var child = new Element("DIV");
            container.AppendChild(child);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var cStyle = new CssComputed();
            cStyle.Display = "block";
            cStyle.Width = 50;
            cStyle.Height = 50;
            cStyle.Overflow = "hidden";
            cStyle.BackgroundColor = SKColors.Blue;
            styles[container] = cStyle;
            
            var childStyle = new CssComputed();
            childStyle.Display = "block";
            childStyle.Width = 100;
            childStyle.Height = 100;
            childStyle.BackgroundColor = SKColors.Red;
            styles[child] = childStyle;
            
            var (computer, boxes, tree) = RunPipeline(container, styles);
            
            // Verify Child Layout (should still be 100x100)
            var childBox = boxes[child];
            Assert.Equal(100, childBox.BorderBox.Width);
            
            // Verify Paint Tree contains ClipPaintNode
            var paintNodes = FlattenTree(tree.Roots);
            
            // We expect a ClipPaintNode wrapping the child content
            // The container generates a BackgroundPaintNode AND a ClipPaintNode (for children).
            var clipNode = paintNodes.OfType<ClipPaintNode>().FirstOrDefault();
            Assert.NotNull(clipNode);
            
            // Clip rect should be approx 50x50 (container padding box)
            // Note: ClipRect property on PaintNodeBase vs ClipPaintNode specific logic
            // In PaintNodeBase.cs, ClipPaintNode has ClipPath. But wait, PaintNodeBase has ClipRect property too.
            // Let's check ClipPaintNode definition again. It has ClipPath.
            // If it uses ClipRect from base, distinct.
            // Assuming ClipRect is what we want or the Bounds of the ClipPaintNode.
            Assert.True(clipNode.Bounds.Width <= 50); 
        }
        
        [Fact]
        public void ZIndex_Stacking_Order()
        {
            // Scenario: Acid2 layering
            // Red: z-index 10, Blue: z-index 5. 
            // Both absolute/relative.
            // Order should be Blue then Red (Red on top).
            
            var container = new Element("DIV");
            var blue = new Element("DIV");
            var red = new Element("DIV");
            container.AppendChild(red); // Red first in DOM
            container.AppendChild(blue);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var conStyle = new CssComputed();
            conStyle.Position = "relative";
            conStyle.Width = 100;
            conStyle.Height = 100;
            conStyle.ZIndex = 0; // Stacking Context Root
            styles[container] = conStyle;
            
            var redStyle = new CssComputed();
            redStyle.Position = "absolute";
            redStyle.Width = 50;
            redStyle.Height = 50;
            redStyle.BackgroundColor = SKColors.Red;
            redStyle.ZIndex = 10;
            styles[red] = redStyle;
            
            var blueStyle = new CssComputed();
            blueStyle.Position = "absolute";
            blueStyle.Width = 50;
            blueStyle.Height = 50;
            blueStyle.BackgroundColor = SKColors.Blue;
            blueStyle.ZIndex = 5;
            styles[blue] = blueStyle;
            
            var (computer, boxes, tree) = RunPipeline(container, styles);
            
            var paintNodes = FlattenTree(tree.Roots).OfType<BackgroundPaintNode>().ToList();
            
            // Should find Blue and Red
            var blueNode = paintNodes.FirstOrDefault(n => n.Color.Equals(SKColors.Blue));
            var redNode = paintNodes.FirstOrDefault(n => n.Color.Equals(SKColors.Red));
             Assert.NotNull(blueNode);
            Assert.NotNull(redNode);
            
            // Index of Blue should be less than Red
            var blueIdx = paintNodes.IndexOf(blueNode);
            var redIdx = paintNodes.IndexOf(redNode);
            
            Assert.True(blueIdx < redIdx, $"Blue (Index {blueIdx}) should be painted BEFORE Red (Index {redIdx})");
        }
        
        // Helper to recursively flatten paint nodes
        private List<PaintNodeBase> FlattenTree(IEnumerable<PaintNodeBase> nodes)
        {
            var list = new List<PaintNodeBase>();
            if (nodes == null) return list;
            
            foreach (var node in nodes)
            {
                list.Add(node);
                // Recursively add children
                if (node.Children != null)
                {
                    list.AddRange(FlattenTree(node.Children));
                }
            }
            return list;
        }
    }
}
