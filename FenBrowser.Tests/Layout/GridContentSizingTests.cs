using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridContentSizingTests
    {
        private (Element Container, List<Element> Items, Dictionary<Node, CssComputed> Styles) CreateGrid(string templateCols, string templateRows, int itemCount)
        {
            var container = new Element("div");
            var styles = new Dictionary<Node, CssComputed>();

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = templateCols,
                GridTemplateRows = templateRows,
                Width = 800,
                Height = 600
            };
            styles[container] = containerStyle;

            var items = new List<Element>();
            for (int i = 0; i < itemCount; i++)
            {
                var item = new Element("div") { Id = $"item{i + 1}" };
                items.Add(item);
                container.AppendChild(item);
                
                var itemStyle = new CssComputed { 
                    GridColumnStart = "auto",
                    GridRowStart = "auto",
                    Width = 60,  // Default explicitly set for content simulation
                    Height = 20
                };
                styles[item] = itemStyle;
            }

            return (container, items, styles);
        }

        private Dictionary<Node, BoxModel> ArrangeGrid(Element container, Dictionary<Node, CssComputed> styles)
        {
            var boxes = new Dictionary<Node, BoxModel>();
            GridLayoutComputer.Measure(container, new SKSize((float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, 0);
            
            GridLayoutComputer.Arrange(container, new SKRect(0, 0, (float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, boxes, 0, (node, rect, depth) =>
            {
                if (node is Element el)
                {
                    if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                    boxes[el].ContentBox = rect;
                    boxes[el].BorderBox = rect; 
                }
            });
            return boxes;
        }

        [Fact]
        public void MinContent_ShrinksToSmallestItem()
        {
            // min-content should match the largest "minimum contribution" of items.
            // Items have explicit Width 60. So min-content = 60.
            // If track was 100px fixed, it would be 100.
            var (container, items, styles) = CreateGrid("min-content", "auto", 1);
            styles[items[0]].Width = 60; 
            
            var boxes = ArrangeGrid(container, styles);
            
            // Expected: Width 60.
            Assert.Equal(60, boxes[items[0]].ContentBox.Width);
        }

        [Fact]
        public void MaxContent_ExpandsToItemSize()
        {
            // max-content should match the largest "maximum contribution".
            // Items have Width 120.
            var (container, items, styles) = CreateGrid("max-content", "auto", 1);
            styles[items[0]].Width = 120;
            
            var boxes = ArrangeGrid(container, styles);
            
            Assert.Equal(120, boxes[items[0]].ContentBox.Width);
        }

        [Fact]
        public void FitContent_ClampsToLimit()
        {
            // fit-content(100px).
            // Item Width Auto. MaxWidth 150. MinWidth 0.
            // Logic: min(150, max(0, 100)) = 100.
            var (container, items, styles) = CreateGrid("fit-content(100px)", "auto", 1);
            // styles[items[0]].Width = 150; // Forces min-content=150
            styles[items[0]].MaxWidth = 150;
            styles[items[0]].MinWidth = 0;
            
            var boxes = ArrangeGrid(container, styles);
            
            Assert.Equal(100, boxes[items[0]].ContentBox.Width);
        }
    }
}
