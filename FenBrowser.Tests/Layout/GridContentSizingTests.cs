using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
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
            
            // Measurement callback that returns intrinsic sizes based on element styles
            LayoutMetrics MeasureChild(Node n, SKSize available, int d)
            {
                if (styles.TryGetValue(n, out var s))
                {
                    float w = (float)(s.Width ?? 0);
                    float h = (float)(s.Height ?? 0);
                    return new LayoutMetrics
                    {
                        MaxChildWidth = w,
                        ContentHeight = h,
                        MinContentWidth = w,
                        MaxContentWidth = w
                    };
                }
                return new LayoutMetrics();
            }
            
            GridLayoutComputer.Measure(container, new SKSize((float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, 0, MeasureChild);
            
            GridLayoutComputer.Arrange(container, new SKRect(0, 0, (float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, boxes, 0, (node, rect, depth) =>
            {
                if (node is Element el)
                {
                    if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                    boxes[el].ContentBox = rect;
                    boxes[el].BorderBox = rect; 
                }
            }, MeasureChild);
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

        [Fact]
        public void FitContent_Percent_ResolvesAgainstContainerWidth()
        {
            var (container, items, styles) = CreateGrid("fit-content(50%)", "auto", 1);
            styles[container].Width = 400;
            styles[container].Height = 300;

            // Keep item intrinsic width unconstrained so fit-content limit drives final width.
            styles[items[0]].Width = null;
            styles[items[0]].MinWidth = 0;
            styles[items[0]].MaxWidth = null;

            var boxes = ArrangeGrid(container, styles);
            Assert.Equal(200, boxes[items[0]].ContentBox.Width);
        }

        [Fact]
        public void AutoRows_ArrangePreservesContentContributionBeforeStretch()
        {
            var (container, items, styles) = CreateGrid("100px", "auto auto", 2);
            styles[container].Width = 100;
            styles[container].Height = 600; // Definite container height should not erase content-derived row minima.

            styles[items[0]].Height = 20;
            styles[items[1]].Height = 70;
            styles[items[0]].Width = 100;
            styles[items[1]].Width = 100;

            LayoutMetrics MeasureChild(Node n, SKSize available, int d)
            {
                if (styles.TryGetValue(n, out var s))
                {
                    float w = (float)(s.Width ?? 0);
                    float h = (float)(s.Height ?? 0);
                    return new LayoutMetrics
                    {
                        MaxChildWidth = w,
                        ContentHeight = h,
                        MinContentWidth = w,
                        MaxContentWidth = w
                    };
                }
                return new LayoutMetrics();
            }

            var measured = GridLayoutComputer.Measure(container, new SKSize(100, 600), styles, 0, MeasureChild);

            var boxes = new Dictionary<Node, BoxModel>();
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 100, measured.ContentHeight),
                styles,
                boxes,
                0,
                (node, rect, depth) =>
                {
                    if (node is Element el)
                    {
                        if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                        boxes[el].ContentBox = rect;
                        boxes[el].BorderBox = rect;
                    }
                },
                MeasureChild);

            Assert.Equal(0, boxes[items[0]].ContentBox.Top);
            // Row 1 contributes 20px intrinsic height, then remaining free space is distributed
            // across auto rows. This keeps content contribution visible before stretch.
            Assert.Equal(275, boxes[items[1]].ContentBox.Top);
        }

        [Fact]
        public void FlexibleRows_UseIntrinsicContentWhenContainerHeightIsAuto()
        {
            var (container, items, styles) = CreateGrid("100px", "min-content 1fr min-content", 3);
            styles[container].Width = 100;
            styles[container].Height = null;

            styles[items[0]].GridRowStart = "1";
            styles[items[0]].GridColumnStart = "1";
            styles[items[0]].Width = 100;
            styles[items[0]].Height = 10;

            styles[items[1]].GridRowStart = "2";
            styles[items[1]].GridColumnStart = "1";
            styles[items[1]].Width = 100;
            styles[items[1]].Height = 200;

            styles[items[2]].GridRowStart = "3";
            styles[items[2]].GridColumnStart = "1";
            styles[items[2]].Width = 100;
            styles[items[2]].Height = 20;

            LayoutMetrics MeasureChild(Node n, SKSize available, int d)
            {
                if (styles.TryGetValue(n, out var s))
                {
                    float w = (float)(s.Width ?? 0);
                    float h = (float)(s.Height ?? 0);
                    return new LayoutMetrics
                    {
                        MaxChildWidth = w,
                        ContentHeight = h,
                        MinContentWidth = w,
                        MaxContentWidth = w
                    };
                }
                return new LayoutMetrics();
            }

            var measured = GridLayoutComputer.Measure(container, new SKSize(100, 60), styles, 0, MeasureChild);
            Assert.Equal(230, measured.ContentHeight);

            var boxes = new Dictionary<Node, BoxModel>();
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 100, measured.ContentHeight),
                styles,
                boxes,
                0,
                (node, rect, depth) =>
                {
                    if (node is Element el)
                    {
                        if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                        boxes[el].ContentBox = rect;
                        boxes[el].BorderBox = rect;
                    }
                },
                MeasureChild);

            Assert.Equal(10, boxes[items[1]].ContentBox.Top);
            Assert.Equal(210, boxes[items[2]].ContentBox.Top);
        }
    }
}
