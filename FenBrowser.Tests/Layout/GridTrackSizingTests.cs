using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridTrackSizingTests
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
                    Width = 50, 
                    Height = 50,
                    GridColumnStart = "auto",
                    GridRowStart = "auto"
                };
                styles[item] = itemStyle;
            }

            return (container, items, styles);
        }

        private Dictionary<Node, BoxModel> ArrangeGrid(Element container, Dictionary<Node, CssComputed> styles)
        {
            var boxes = new Dictionary<Node, BoxModel>();
            GridLayoutComputer.Measure(container, new SKSize((float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, 0, (n, sz, d) => new LayoutMetrics());
            
            GridLayoutComputer.Arrange(container, new SKRect(0, 0, (float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, boxes, 0, (node, rect, depth) =>
            {
                if (node is Element el)
                {
                    if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                    boxes[el].ContentBox = rect;
                    boxes[el].BorderBox = rect; 
                }
            }, (n, sz, d) => new LayoutMetrics());
            return boxes;
        }

        [Fact]
        public void Repeat_ExpandingTracks_CreatesMultipleTracks()
        {
            // repeat(3, 100px) -> 100px 100px 100px
            var (container, items, styles) = CreateGrid("repeat(3, 100px)", "100px", 3);
            var boxes = ArrangeGrid(container, styles);

            // Item 1: 0-100
            Assert.Equal(0, boxes[items[0]].ContentBox.Left);
            Assert.Equal(100, boxes[items[0]].ContentBox.Width); // Stretches to track width

            // Item 2: 100-200
            Assert.Equal(100, boxes[items[1]].ContentBox.Left);
            Assert.Equal(100, boxes[items[1]].ContentBox.Width);

            // Item 3: 200-300
            Assert.Equal(200, boxes[items[2]].ContentBox.Left);
        }

        [Fact]
        public void MinMax_ConstrainsSize_UsesMinOrMax()
        {
            // minmax(100px, 200px) minmax(50px, 1fr)
            // Container 800px.
            // Track 1: min 100, max 200. Ideal? 
            // Track 2: min 50, max 1fr (takes remaining).
            // Logic: 
            // 1. Assign base sizes: 100px, 50px. Used: 150px. Free: 650px.
            // 2. Distribute free space.
            //    Track 1 is flexible? No, 100-200 is effectively fixed range but not 'fr'. 
            //    Track 2 is 1fr.
            //    Typically, 'fr' takes all free space. Track 1 stays at base size unless it's auto/fr?
            //    If minmax(100px, 200px), it consumes space if needed by content. Empty content?
            //    If empty, it should be 100px (min).
            //    So Track 1 = 100px. Track 2 = 700px.
            
            var (container, items, styles) = CreateGrid("minmax(100px, 200px) minmax(50px, 1fr)", "100px", 2);
            var boxes = ArrangeGrid(container, styles);

            // Item 1: 0-100 (Width 100)
            Assert.Equal(100, boxes[items[0]].ContentBox.Width);
            
            // Item 2: 100-800 (Width 700)
            Assert.Equal(100, boxes[items[1]].ContentBox.Left);
            Assert.Equal(700, boxes[items[1]].ContentBox.Width);
        }

        [Fact]
        public void AutoFill_CalculatesRepetitions_BasedOnContainerSize()
        {
            // repeat(auto-fill, 100px). Container 500px. -> 5 tracks.
            var (container, items, styles) = CreateGrid("repeat(auto-fill, 100px)", "100px", 5);
            styles[container].Width = 500;
            var boxes = ArrangeGrid(container, styles);

            // Item 1: 0-100
            Assert.Equal(0, boxes[items[0]].ContentBox.Left);
            // Item 5: 400-500
            Assert.Equal(400, boxes[items[4]].ContentBox.Left);
        }

        [Fact]
        public void AutoFill_WithGap_CalculatesRepetitions()
        {
            // repeat(auto-fill, 100px). Gap 10px. Container 540px.
            // Formula: N*100 + (N-1)*10 <= 540
            // 110N <= 550 -> N=5.
            var (container, items, styles) = CreateGrid("repeat(auto-fill, 100px)", "100px", 5);
            styles[container].Width = 540;
            styles[container].ColumnGap = 10;
            
            var boxes = ArrangeGrid(container, styles);

            // Item 5 should exist and be at...
            // Track 0: 0-100
            // Gap: 10
            // Track 1: 110-210
            // ...
            // Track 4: 440-540
            Assert.Equal(440, boxes[items[4]].ContentBox.Left);
            Assert.Equal(540, boxes[items[4]].ContentBox.Right);
        }

        [Fact]
        public void AutoFill_WithGap_ReducesCountIfNoFit()
        {
             // repeat(auto-fill, 100px). Gap 10px. Container 530px.
             // 110N <= 540 -> N=4.9 -> 4 tracks.
             // We create 5 items. Item 5 should auto-place to next row (if auto-flow default).
             // Wait, CreateGrid sets items to explicit? No, "auto" row/col.
             // So if only 4 cols exist, Item 5 goes to Row 2.
             
            var (container, items, styles) = CreateGrid("repeat(auto-fill, 100px)", "100px", 5);
            styles[container].Width = 530;
            styles[container].ColumnGap = 10;
            
            var boxes = ArrangeGrid(container, styles);
            
            // Check Item 4 (Index 3) is in Row 1, Col 4.
            Assert.Equal(330, boxes[items[3]].ContentBox.Left); // 0, 110, 220, 330.
            
            // Check Item 5 (Index 4) is in Row 2, Col 1.
            Assert.Equal(0, boxes[items[4]].ContentBox.Left);
            Assert.Equal(100, boxes[items[4]].ContentBox.Top); // Row height 100px? Row Gap 0?
            // Row Gap defaults to 0 in CreateGrid unless set.
        }
    }
}
