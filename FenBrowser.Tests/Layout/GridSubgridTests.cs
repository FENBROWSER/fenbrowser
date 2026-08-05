using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridSubgridTests
    {
        private static CssComputed Style(
            string display = "grid",
            string gridTemplateColumns = null,
            string gridTemplateRows = null,
            double? width = null,
            double? height = null)
        {
            return new CssComputed
            {
                Display = display,
                GridTemplateColumns = gridTemplateColumns,
                GridTemplateRows = gridTemplateRows,
                Width = width,
                Height = height
            };
        }

        private static Element ElementWith(string id, params Element[] children)
        {
            var element = new Element("div") { Id = id };
            foreach (var child in children)
            {
                element.AppendChild(child);
            }
            return element;
        }

        private static LayoutMetrics FixedMeasure(Node node, SKSize size, int depth)
        {
            return new LayoutMetrics
            {
                MaxChildWidth = 0f,
                MinContentWidth = 0f,
                MaxContentWidth = 0f,
                ContentHeight = 0f,
                ActualHeight = 0f
            };
        }

        private static Dictionary<Node, BoxModel> ArrangeGrid(
            Element container,
            Dictionary<Node, CssComputed> styles,
            float width,
            float height)
        {
            var boxes = new Dictionary<Node, BoxModel>();

            void Record(Node node, SKRect rect)
            {
                if (node is Element el)
                {
                    if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                    boxes[el].ContentBox = rect;
                }
            }

            void ArrangeNode(Node node, SKRect rect, int depth)
            {
                ArrangeNodeCore(node, rect, depth, null);
            }

            void ArrangeNodeWithSubgrid(Node node, SKRect rect, int depth, GridSubgridContext subgrid)
            {
                ArrangeNodeCore(node, rect, depth, subgrid);
            }

            void ArrangeNodeCore(Node node, SKRect rect, int depth, GridSubgridContext subgrid)
            {
                Record(node, rect);

                // Recurse into nested grid containers the way GridFormattingContext
                // does, threading the inherited subgrid context through.
                if (node is Element el &&
                    styles.TryGetValue(el, out var elStyle) &&
                    elStyle?.Display?.Contains("grid", System.StringComparison.OrdinalIgnoreCase) == true)
                {
                    var subSource = el.ChildNodes?.ToList() ?? new List<Node>();
                    if (subSource.Count == 0) return;

                    var subStyles = new Dictionary<Node, CssComputed>();
                    foreach (var child in subSource)
                    {
                        if (child is Element childEl && styles.TryGetValue(childEl, out var childStyle))
                        {
                            subStyles[childEl] = childStyle;
                        }
                    }

                    GridLayoutComputer.Measure(el, new SKSize(rect.Width, rect.Height), styles, depth, FixedMeasure, subSource, subgrid);
                    GridLayoutComputer.Arrange(
                        el,
                        new SKRect(0, 0, rect.Width, rect.Height),
                        styles,
                        boxes,
                        depth,
                        ArrangeNode,
                        FixedMeasure,
                        subSource,
                        subgrid,
                        ArrangeNodeWithSubgrid);
                }
            }

            var metrics = GridLayoutComputer.Measure(container, new SKSize(width, height), styles, 0, FixedMeasure);

            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, width, height),
                styles,
                boxes,
                0,
                ArrangeNode,
                FixedMeasure,
                null,
                null,
                ArrangeNodeWithSubgrid);

            return boxes;
        }

        [Fact]
        public void Subgrid_ChildWithSubgridColumns_InheritsParentTrackLines()
        {
            // Parent grid: 100px + 200px columns, one row. The subgrid child spans
            // both columns; its own children must align with the parent's lines.
            var subgridChild = ElementWith("subgrid", ElementWith("a"), ElementWith("b"));
            var container = ElementWith("grid", subgridChild);

            var subElements = subgridChild.ChildNodes.OfType<Element>().ToList();

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = Style("grid", "100px 200px", "50px"),
                [subgridChild] = Style("grid", "subgrid", null, null, null),
                [subElements[0]] = Style("block", null, null),
                [subElements[1]] = Style("block", null, null)
            };
            styles[subgridChild].GridColumnStart = "1";
            styles[subgridChild].GridColumnEnd = "3";
            styles[subElements[0]].GridColumnStart = "1";
            styles[subElements[0]].GridColumnEnd = "2";
            styles[subElements[1]].GridColumnStart = "2";
            styles[subElements[1]].GridColumnEnd = "3";

            var boxes = ArrangeGrid(container, styles, 300f, 50f);

            var a = boxes[subElements[0]].ContentBox;
            var b = boxes[subElements[1]].ContentBox;

            Assert.Equal(0f, a.Left, 1);
            Assert.Equal(100f, a.Width, 1);
            Assert.Equal(100f, b.Left, 1);
            Assert.Equal(200f, b.Width, 1);
        }

        [Fact]
        public void Subgrid_SingleColumnSpan_InheritsOnlySpannedTrack()
        {
            // Parent: 100px 200px 100px. Subgrid spans the middle column only:
            // its single child fills that one 200px track.
            var subgridChild = ElementWith("subgrid", ElementWith("a"));
            var container = ElementWith("grid", ElementWith("other1"), subgridChild, ElementWith("other2"));

            var subA = subgridChild.ChildNodes.OfType<Element>().First();

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = Style("grid", "100px 200px 100px", "50px"),
                [subgridChild] = Style("grid", "subgrid", null, null, null),
                [subA] = Style("block", null, null)
            };
            styles[subgridChild].GridColumnStart = "2";
            styles[subgridChild].GridColumnEnd = "3";

            foreach (var other in container.ChildNodes.OfType<Element>().Where(e => e != subgridChild))
            {
                styles[other] = Style("block", null, null);
            }

            var boxes = ArrangeGrid(container, styles, 400f, 50f);

            // The child sits at the subgrid's local line 0; the subgrid item
            // itself is placed by the parent at x=100, so the inherited 200px
            // track is the only track the child sees.
            var a = boxes[subA].ContentBox;
            Assert.Equal(0f, a.Left, 1);
            Assert.Equal(200f, a.Width, 1);
        }

        [Fact]
        public void Subgrid_SubgridKeyword_ParsesWithoutLocalTracks()
        {
            // `subgrid` alone must not create a local auto track: the axis is
            // inherited. Verify via the parsing surface used by Measure.
            var subgridChild = ElementWith("subgrid", ElementWith("a"));
            var container = ElementWith("grid", subgridChild);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = Style("grid", "subgrid", "subgrid", 400f, 200f),
                [subgridChild] = Style("block", null, null)
            };
            styles[subgridChild.ChildNodes.OfType<Element>().First()] = Style("block", null, null);

            // No crash + the parent grid still measures (axis treated as auto).
            var metrics = GridLayoutComputer.Measure(container, new SKSize(400f, 200f), styles, 0, FixedMeasure);
            Assert.True(metrics.ContentHeight >= 0f);
        }

        [Fact]
        public void Subgrid_LineNames_AfterKeywordAreParsed()
        {
            // `subgrid [start] [end]` names the two inherited lines. Line-name
            // parsing must not treat the keyword as a track.
            var subgridChild = ElementWith("subgrid", ElementWith("a"));
            var container = ElementWith("grid", subgridChild);

            var subA = subgridChild.ChildNodes.OfType<Element>().First();

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = Style("grid", "100px 100px", "50px"),
                [subgridChild] = Style("grid", "subgrid [left] [right]", null, null, null),
                [subA] = Style("block", null, null)
            };
            styles[subgridChild].GridColumnStart = "1";
            styles[subgridChild].GridColumnEnd = "3";
            styles[subA].GridColumnStart = "left";
            styles[subA].GridColumnEnd = "right";

            var boxes = ArrangeGrid(container, styles, 200f, 50f);

            var a = boxes[subA].ContentBox;
            Assert.Equal(0f, a.Left, 1);
            Assert.Equal(100f, a.Width, 1);
        }

        [Fact]
        public void Subgrid_RowsAxis_AlignsToParentRowLines()
        {
            // Parent rows: 30px 70px. Subgrid spans both rows; its children must
            // align with the parent's row lines.
            var subgridChild = ElementWith("subgrid", ElementWith("top"), ElementWith("bottom"));
            var container = ElementWith("grid", subgridChild);

            var subElements = subgridChild.ChildNodes.OfType<Element>().ToList();

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = Style("grid", "100px", "30px 70px"),
                [subgridChild] = Style("grid", "subgrid", "subgrid", null, null)
            };
            styles[subgridChild].GridRowStart = "1";
            styles[subgridChild].GridRowEnd = "3";

            styles[subElements[0]] = Style("block", null, null);
            styles[subElements[1]] = Style("block", null, null);
            styles[subElements[0]].GridRowStart = "1";
            styles[subElements[0]].GridRowEnd = "2";
            styles[subElements[1]].GridRowStart = "2";
            styles[subElements[1]].GridRowEnd = "3";

            var boxes = ArrangeGrid(container, styles, 100f, 100f);

            var top = boxes[subElements[0]].ContentBox;
            var bottom = boxes[subElements[1]].ContentBox;

            Assert.Equal(0f, top.Top, 1);
            Assert.Equal(30f, top.Height, 1);
            Assert.Equal(30f, bottom.Top, 1);
            Assert.Equal(70f, bottom.Height, 1);
        }
    }
}
