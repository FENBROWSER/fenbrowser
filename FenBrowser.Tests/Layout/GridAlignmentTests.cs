using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridAlignmentTests
    {
        private (Element Container, List<Element> Items, Dictionary<Node, CssComputed> Styles) CreateGrid(string templateCols, string templateRows, int itemCount, string justifyItems = null, string alignItems = null, string justifyContent = null, string alignContent = null)
        {
            var container = new Element("div");
            var styles = new Dictionary<Node, CssComputed>();

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = templateCols,
                GridTemplateRows = templateRows,
                JustifyItems = justifyItems,
                AlignItems = alignItems,
                JustifyContent = justifyContent,
                AlignContent = alignContent,
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
            var metrics = GridLayoutComputer.Measure(container, new SKSize((float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, 0);
            
            GridLayoutComputer.Arrange(container, new SKRect(0, 0, (float)styles[container].Width.Value, (float)styles[container].Height.Value), styles, boxes, 0, (node, rect, depth) =>
            {
                if (node is Element el)
                {
                    // Update box model for verification
                    if (!boxes.ContainsKey(el)) boxes[el] = new BoxModel();
                    boxes[el].ContentBox = rect;
                    // For alignment tests, we assume content box == border box for simplicity unless we test padding
                    boxes[el].BorderBox = rect; 
                }
            });
            return boxes;
        }

        private SKRect GetRect(Element item, Dictionary<Node, BoxModel> boxes)
        {
            if (boxes != null && boxes.TryGetValue(item, out var box))
            {
                return box.ContentBox;
            }
            return SKRect.Empty;
        }

        [Fact]
        public void JustifyItems_Center_CentersItemsHorizontallyInCell()
        {
            // Grid: 200x200 cell. Item: 50x50.
            // justify-items: center -> Item X should be (200 - 50)/2 = 75.
            var (container, items, styles) = CreateGrid("200px", "200px", 1, justifyItems: "center");

            var boxes = ArrangeGrid(container, styles);
            var result = GetRect(items[0], boxes);

            Assert.Equal(75, result.Left);
            Assert.Equal(50, result.Width);
        }

        [Fact]
        public void AlignItems_End_AlignsItemsVerticallyEndInCell()
        {
            // Grid: 200x200 cell. Item: 50x50.
            // align-items: end -> Item Y should be 200 - 50 = 150.
            var (container, items, styles) = CreateGrid("200px", "200px", 1, alignItems: "end");

            var boxes = ArrangeGrid(container, styles);
            var result = GetRect(items[0], boxes);

            Assert.Equal(150, result.Top);
            Assert.Equal(50, result.Height);
        }

        [Fact]
        public void JustifySelf_Override_OverridesContainerCenter()
        {
            // Grid: 200x200. Container: justify-items: center. Item: justify-self: start.
            // Item X should be 0.
            var (container, items, styles) = CreateGrid("200px", "200px", 1, justifyItems: "center");
            
            // Override item style
            styles[items[0]].JustifySelf = "start"; 

            var boxes = ArrangeGrid(container, styles);
            var result = GetRect(items[0], boxes);

            Assert.Equal(0, result.Left);
        }

        [Fact]
        public void AlignContent_Center_CentersTracksVertically()
        {
            // Container 600px height. Rows: 100px. Content Height: 100px.
            // align-content: center.
            // Start Y should be (600 - 100)/2 = 250.
            var (container, items, styles) = CreateGrid("100px", "100px", 1, alignContent: "center");

            var boxes = ArrangeGrid(container, styles);
            var result = GetRect(items[0], boxes);

            Assert.Equal(250, result.Top); // Item follows track
        }

        [Fact]
        public void TemplateAreas_PlacesItemsByName()
        {
            // Grid 200x300. Cols: 100px 100px. Rows: 50px 200px 50px.
            // "head head"
            // "nav  main"
            // "foot foot"
            var container = new Element("div");
            var styles = new Dictionary<Node, CssComputed>();

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                GridTemplateRows = "50px 200px 50px",
                GridTemplateAreas = "'head head' 'nav main' 'foot foot'",
                Width = 200,
                Height = 300
            };
            styles[container] = containerStyle;

            var head = new Element("div") { Id = "head" };
            var main = new Element("div") { Id = "main" };
            var foot = new Element("div") { Id = "foot" };
            
            container.AppendChild(head);
            container.AppendChild(main);
            container.AppendChild(foot);

            styles[head] = new CssComputed { GridArea = "head" };
            styles[main] = new CssComputed { GridArea = "main" };
            styles[foot] = new CssComputed { GridArea = "foot" };

            var boxes = ArrangeGrid(container, styles);

            // Head: Row 1, Col 1-span-2. Rect: 0,0 200x50
            Assert.Equal(new SKRect(0, 0, 200, 50), boxes[head].ContentBox);
            
            // Main: Row 2, Col 2. Rect: 100,50 100x200
            Assert.Equal(new SKRect(100, 50, 200, 250), boxes[main].ContentBox);
            
            // Foot: Row 3, Col 1-span-2. Rect: 0,250 200x50
            Assert.Equal(new SKRect(0, 250, 200, 300), boxes[foot].ContentBox);
        }
    }
}
