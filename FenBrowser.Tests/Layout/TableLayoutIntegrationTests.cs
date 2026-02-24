using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using System;

namespace FenBrowser.Tests.Layout
{
    public class TableLayoutIntegrationTests
    {
        private static bool TagEquals(Element element, string tagName) =>
            string.Equals(element?.TagName, tagName, StringComparison.OrdinalIgnoreCase);

        private static Element FindElementById(Node root, string id)
        {
            if (root == null || string.IsNullOrEmpty(id)) return null;
            var stack = new Stack<Node>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Element element &&
                    string.Equals(element.GetAttribute("id"), id, StringComparison.Ordinal))
                {
                    return element;
                }

                if (node.ChildNodes == null)
                {
                    continue;
                }

                var children = node.ChildNodes.ToList();
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        }

        private static Element FindFirstElementByTag(Node root, string tagName)
        {
            if (root == null) return null;
            var stack = new Stack<Node>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Element element && TagEquals(element, tagName))
                {
                    return element;
                }

                if (node.ChildNodes == null)
                {
                    continue;
                }

                var children = node.ChildNodes.ToList();
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        }

        private static List<Element> FindElementsByTag(Node root, string tagName)
        {
            var result = new List<Element>();
            if (root == null) return result;

            var stack = new Stack<Node>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Element element && TagEquals(element, tagName))
                {
                    result.Add(element);
                }

                if (node.ChildNodes == null)
                {
                    continue;
                }

                var children = node.ChildNodes.ToList();
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            return result;
        }

        [Fact]
        public void TableLayout_Basic2x2_CalculatesDimensions()
        {
             // 1. Setup HTML
             var html = "<table><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>";
             var parser = new HtmlParser(html);
             var doc = parser.Parse();
             
             // Ensure Structure Doc -> Body -> Table
             var body = new Element("BODY");
             var nodes = doc.Children.ToList();
             doc.RemoveAllChildren();
             doc.AppendChild(body);
             foreach(var node in nodes) body.AppendChild(node);

             var table = FindFirstElementByTag(body, "TABLE");
             Assert.NotNull(table);
             
             // 2. Mock Styles
             var styles = new Dictionary<Node, CssComputed>();
             
             // Recursively set display types based on tags (mimic ua.css)
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (TagEquals(e, "BODY")) style.Display = "block";
                     else if (TagEquals(e, "TABLE")) style.Display = "table";
                     else if (TagEquals(e, "TR")) style.Display = "table-row";
                     else if (TagEquals(e, "TD")) style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 
                 if (node.ChildNodes != null)
                 {
                     foreach (var child in node.ChildNodes) ApplyStyles(child);
                 }
             }
             ApplyStyles(body);

             // 3. Layout Measure
             var layout = new MinimalLayoutComputer(styles, 800, 600);
             layout.Measure(body, new SKSize(800, 600));

             // 4. Arrange
             layout.Arrange(body, new SKRect(0, 0, 800, 600));
             
             // 5. Assertions
             var boxes = layout.GetAllBoxes().ToDictionary(k => k.Key, v => v.Value);
             
             Assert.True(boxes.ContainsKey(table), "Table should have a box");
             var tableBox = boxes[table];
             Assert.True(tableBox.BorderBox.Width > 0, $"Table width should be > 0, got {tableBox.BorderBox.Width}");
             Assert.True(tableBox.BorderBox.Height > 0, $"Table height should be > 0, got {tableBox.BorderBox.Height}");

             // Check cells
             var tds = FindElementsByTag(table, "TD").Cast<Node>().ToList();
             Assert.NotEmpty(tds);
             foreach(var td in tds)
             {
                 Assert.True(boxes.ContainsKey(td), $"TD {((Element)td).TagName} should have a box");
                 var tdBox = boxes[td];
                 Assert.True(tdBox.BorderBox.Width > 0, $"TD width should be > 0");
                 Assert.True(tdBox.BorderBox.Height > 0, $"TD height should be > 0");
             }
             
             // Check TRs
             var trs = FindElementsByTag(table, "TR");
             foreach(var tr in trs)
             {
                 Assert.True(boxes.ContainsKey(tr), "TR should have a box (required for renderer traversal)");
                 var trBox = boxes[tr];
                 Assert.True(trBox.BorderBox.Height > 0, "TR height should be > 0");
             }
        }
        
        [Fact]
        public void TableLayout_Colspan_SpansColumns()
        {
             // 1. Setup HTML with Colspan
             var html = "<table><tr><td colspan='2'>Header</td></tr><tr><td>A</td><td>B</td></tr></table>";
             var parser = new HtmlParser(html);
             var doc = parser.Parse();
             
             var body = new Element("BODY");
             var nodes = doc.Children.ToList();
             doc.RemoveAllChildren();
             doc.AppendChild(body);
             foreach(var node in nodes) body.AppendChild(node);

             var table = FindFirstElementByTag(body, "TABLE");
             Assert.NotNull(table);

             // 2. Styles
             var styles = new Dictionary<Node, CssComputed>();
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (TagEquals(e, "BODY")) style.Display = "block";
                     else if (TagEquals(e, "TABLE")) style.Display = "table";
                     else if (TagEquals(e, "TR")) style.Display = "table-row";
                     else if (TagEquals(e, "TD")) style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 if (node.ChildNodes != null) foreach (var child in node.ChildNodes) ApplyStyles(child);
             }
             ApplyStyles(body);

             // 3. Layout
             var layout = new MinimalLayoutComputer(styles, 800, 600);
             layout.Measure(body, new SKSize(800, 600));
             layout.Arrange(body, new SKRect(0, 0, 800, 600));

             // 4. Assert
             var boxes = layout.GetAllBoxes().ToDictionary(k => k.Key, v => v.Value);
             
             // Get the colspan cell
             var rows = FindElementsByTag(table, "TR");
             Assert.True(rows.Count >= 2, "Expected at least two table rows for colspan scenario.");
             var row1 = rows[0];
             var row2 = rows[1];
             var row1Cells = FindElementsByTag(row1, "TD");
             var row2Cells = FindElementsByTag(row2, "TD");
             Assert.NotEmpty(row1Cells);
             Assert.True(row2Cells.Count >= 2, "Expected second row to contain at least two cells.");

             var colSpanCell = row1Cells[0];
             var cellA = row2Cells[0];
             var cellB = row2Cells[1];
             
             Assert.True(boxes.ContainsKey(colSpanCell));
             Assert.True(boxes.ContainsKey(cellA));
             Assert.True(boxes.ContainsKey(cellB));
             
             var boxSpan = boxes[colSpanCell];
             var boxA = boxes[cellA];
             var boxB = boxes[cellB];
             
             float expectedWidth = boxA.BorderBox.Width + boxB.BorderBox.Width;
             Assert.True(Math.Abs(boxSpan.BorderBox.Width - expectedWidth) < 5, 
                $"Colspan width {boxSpan.BorderBox.Width} should be approx {expectedWidth}");
        }

        [Fact]
        public void TableLayout_Rowspan_LongSecondColumn_DoesNotPolluteFirstColumnWidth()
        {
             // The long content belongs to column 2 while column 1 is occupied by a rowspan cell.
             // Column indexing must respect slot.ColumnIndex (not row-local slot order).
             var html = "<table><tr><td id='left' rowspan='2'>L</td><td id='r1'>x</td></tr><tr><td id='r2'>VeryVeryVeryVeryVeryVeryLongContent</td></tr></table>";
             var parser = new HtmlParser(html);
             var doc = parser.Parse();

             var body = new Element("BODY");
             var nodes = doc.Children.ToList();
             doc.RemoveAllChildren();
             doc.AppendChild(body);
             foreach (var node in nodes) body.AppendChild(node);

             var table = FindFirstElementByTag(body, "TABLE");
             Assert.NotNull(table);

             var left = FindElementById(table, "left");
             var rightTop = FindElementById(table, "r1");
             var rightBottom = FindElementById(table, "r2");
             Assert.NotNull(left);
             Assert.NotNull(rightTop);
             Assert.NotNull(rightBottom);

             var styles = new Dictionary<Node, CssComputed>();
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (TagEquals(e, "BODY")) style.Display = "block";
                     else if (TagEquals(e, "TABLE")) style.Display = "table";
                     else if (TagEquals(e, "TR")) style.Display = "table-row";
                     else if (TagEquals(e, "TD")) style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 if (node.ChildNodes != null)
                 {
                     foreach (var child in node.ChildNodes) ApplyStyles(child);
                 }
             }
             ApplyStyles(body);

             var layout = new MinimalLayoutComputer(styles, 800, 600);
             layout.Measure(body, new SKSize(800, 600));
             layout.Arrange(body, new SKRect(0, 0, 800, 600));

             var boxes = layout.GetAllBoxes().ToDictionary(k => k.Key, v => v.Value);
             Assert.True(boxes.ContainsKey(left));
             Assert.True(boxes.ContainsKey(rightTop));
             Assert.True(boxes.ContainsKey(rightBottom));

             var leftWidth = boxes[left].BorderBox.Width;
             var rightTopWidth = boxes[rightTop].BorderBox.Width;
             var rightBottomWidth = boxes[rightBottom].BorderBox.Width;

             Assert.True(Math.Abs(rightTopWidth - rightBottomWidth) < 0.5f,
                 $"Right-column cells should share width. top={rightTopWidth}, bottom={rightBottomWidth}");
             Assert.True(rightBottomWidth > leftWidth,
                 $"Long right-column content should widen column 2, not column 1. left={leftWidth}, right={rightBottomWidth}");
        }

        [Fact]
        public void TableLayout_Rowspan_CellHeight_DistributesAcrossSpannedRows()
        {
             var html = "<table><tr><td id='span' rowspan='2'>S</td><td>A</td></tr><tr><td>B</td></tr></table>";
             var parser = new HtmlParser(html);
             var doc = parser.Parse();

             var body = new Element("BODY");
             var nodes = doc.Children.ToList();
             doc.RemoveAllChildren();
             doc.AppendChild(body);
             foreach (var node in nodes) body.AppendChild(node);

             var table = FindFirstElementByTag(body, "TABLE");
             Assert.NotNull(table);
             var spanCell = FindElementById(table, "span");
             Assert.NotNull(spanCell);

             var styles = new Dictionary<Node, CssComputed>();
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (TagEquals(e, "BODY")) style.Display = "block";
                     else if (TagEquals(e, "TABLE")) style.Display = "table";
                     else if (TagEquals(e, "TR")) style.Display = "table-row";
                     else if (TagEquals(e, "TD")) style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 if (node.ChildNodes != null)
                 {
                     foreach (var child in node.ChildNodes) ApplyStyles(child);
                 }
             }
             ApplyStyles(body);

             // Explicit cell height on a rowspan cell must force enough row-height budget across its span.
             styles[spanCell].Height = 120;

             var layout = new MinimalLayoutComputer(styles, 800, 600);
             layout.Measure(body, new SKSize(800, 600));
             layout.Arrange(body, new SKRect(0, 0, 800, 600));

             var boxes = layout.GetAllBoxes().ToDictionary(k => k.Key, v => v.Value);
             Assert.True(boxes.ContainsKey(spanCell));
             Assert.True(boxes[spanCell].BorderBox.Height >= 120f,
                 $"Rowspan cell should honor explicit height budget. actual={boxes[spanCell].BorderBox.Height}");
        }
    }
}
