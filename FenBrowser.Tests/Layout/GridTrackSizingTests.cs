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
            // minmax(100px, 200px) minmax(50px, 1fr) in an 800px container.
            // CSS Grid 12.4-12.7: base sizes 100 and 50; the fr track's growth limit
            // falls back to its base size (12.5). Maximize Tracks (12.6) then hands the
            // positive free space to every track up to its growth limit, so track 1
            // reaches 200px. Expand Flexible Tracks (12.7) gives the fr track the
            // 600px left over.
            var (container, items, styles) = CreateGrid("minmax(100px, 200px) minmax(50px, 1fr)", "100px", 2);
            var boxes = ArrangeGrid(container, styles);

            Assert.Equal(200, boxes[items[0]].ContentBox.Width);

            Assert.Equal(200, boxes[items[1]].ContentBox.Left);
            Assert.Equal(600, boxes[items[1]].ContentBox.Width);
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

        [Fact]
        public void AutoFill_MultiTrackPattern_AccountsForInternalAndInterTrackGaps()
        {
            // repeat(auto-fill, 100px 50px), gap:10, width:540
            // Per repeat:
            // track span = 150
            // tracks per repeat = 2
            // width formula: N*(150 + 2*10) <= 540 + 10 => N <= 3
            // So 3 repeats -> 6 tracks.
            var (container, items, styles) = CreateGrid("repeat(auto-fill, 100px 50px)", "100px", 6);
            styles[container].Width = 540;
            styles[container].ColumnGap = 10;

            var boxes = ArrangeGrid(container, styles);

            Assert.Equal(0, boxes[items[0]].ContentBox.Left);     // col 1 (100px)
            Assert.Equal(110, boxes[items[1]].ContentBox.Left);   // col 2 (50px)
            Assert.Equal(170, boxes[items[2]].ContentBox.Left);   // col 3 (100px)
            Assert.Equal(280, boxes[items[3]].ContentBox.Left);   // col 4 (50px)
            Assert.Equal(340, boxes[items[4]].ContentBox.Left);   // col 5 (100px)
            Assert.Equal(450, boxes[items[5]].ContentBox.Left);   // col 6 (50px)
        }

        [Fact]
        public void AutoFill_UnresolvedIntrinsicTrack_UsesSingleRepeatFallback()
        {
            // repeat(auto-fill, auto) has unresolved intrinsic minimum.
            // Fallback should be deterministic single-repeat (1 column), not explosive track expansion.
            var (container, items, styles) = CreateGrid("repeat(auto-fill, auto)", "100px", 3);
            styles[container].Width = 1000;

            var boxes = ArrangeGrid(container, styles);

            // Single auto column stretches to container width.
            Assert.Equal(1000, boxes[items[0]].ContentBox.Width);

            // Remaining items flow to next rows because only one explicit column exists.
            Assert.Equal(0, boxes[items[1]].ContentBox.Left);
            Assert.Equal(100, boxes[items[1]].ContentBox.Top);
        }

        [Fact]
        public void AutoFit_CollapsesUnusedTrailingTracks_BeforeJustifyContentDistribution()
        {
            var (container, items, styles) = CreateGrid("repeat(auto-fit, 100px)", "100px", 2);
            styles[container].Width = 500;
            styles[container].JustifyContent = "space-between";

            var boxes = ArrangeGrid(container, styles);

            Assert.Equal(0, boxes[items[0]].ContentBox.Left);
            Assert.Equal(400, boxes[items[1]].ContentBox.Left);
        }

        [Fact]
        public void AutoFill_MinMaxAutoDefiniteMax_UsesDefiniteMaxForRepeatCount()
        {
            // repeat(auto-fill, minmax(auto, 120px)) in 500px container
            // should resolve to 4 columns (480px total), not single-repeat fallback.
            var (container, items, styles) = CreateGrid("repeat(auto-fill, minmax(auto, 120px))", "100px", 5);
            styles[container].Width = 500;

            var boxes = ArrangeGrid(container, styles);

            Assert.Equal(0, boxes[items[0]].ContentBox.Left);
            Assert.Equal(120, boxes[items[1]].ContentBox.Left);
            Assert.Equal(240, boxes[items[2]].ContentBox.Left);
            Assert.Equal(360, boxes[items[3]].ContentBox.Left);
            Assert.Equal(0, boxes[items[4]].ContentBox.Left);
            Assert.Equal(100, boxes[items[4]].ContentBox.Top);
        }
    }
}
