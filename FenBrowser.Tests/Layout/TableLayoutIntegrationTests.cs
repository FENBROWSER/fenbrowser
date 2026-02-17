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
             
             var table = body.Children.OfType<Element>().First(e => e.TagName == "TABLE");
             
             // 2. Mock Styles
             var styles = new Dictionary<Node, CssComputed>();
             
             // Recursively set display types based on tags (mimic ua.css)
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (e.TagName == "BODY") style.Display = "block";
                     else if (e.TagName == "TABLE") style.Display = "table";
                     else if (e.TagName == "TR") style.Display = "table-row";
                     else if (e.TagName == "TD") style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 
                 if (node.Children != null)
                 {
                     foreach (var child in node.Children) ApplyStyles(child);
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
             var tds = table.Children.SelectMany(tr => tr.Children).Where(c => c is Element).ToList();
             Assert.NotEmpty(tds);
             foreach(var td in tds)
             {
                 Assert.True(boxes.ContainsKey(td), $"TD {((Element)td).TagName} should have a box");
                 var tdBox = boxes[td];
                 Assert.True(tdBox.BorderBox.Width > 0, $"TD width should be > 0");
                 Assert.True(tdBox.BorderBox.Height > 0, $"TD height should be > 0");
             }
             
             // Check TRs
             var trs = table.Children.OfType<Element>().Where(e => e.TagName == "TR").ToList();
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
             
             var table = body.Children.OfType<Element>().First(e => e.TagName == "TABLE");

             // 2. Styles
             var styles = new Dictionary<Node, CssComputed>();
             void ApplyStyles(Node node)
             {
                 var style = new CssComputed();
                 if (node is Element e)
                 {
                     if (e.TagName == "BODY") style.Display = "block";
                     else if (e.TagName == "TABLE") style.Display = "table";
                     else if (e.TagName == "TR") style.Display = "table-row";
                     else if (e.TagName == "TD") style.Display = "table-cell";
                     else style.Display = "block";
                 }
                 styles[node] = style;
                 if (node.Children != null) foreach (var child in node.Children) ApplyStyles(child);
             }
             ApplyStyles(body);

             // 3. Layout
             var layout = new MinimalLayoutComputer(styles, 800, 600);
             layout.Measure(body, new SKSize(800, 600));
             layout.Arrange(body, new SKRect(0, 0, 800, 600));

             // 4. Assert
             var boxes = layout.GetAllBoxes().ToDictionary(k => k.Key, v => v.Value);
             
             // Get the colspan cell
             var rows = table.Children.Where(c => c is Element).ToList();
             var row1 = rows[0];
             var colSpanCell = row1.Children.First(c => c is Element);
             
             var row2 = rows[1];
             var cellA = row2.Children.OfType<Element>().ElementAt(0);
             var cellB = row2.Children.OfType<Element>().ElementAt(1);
             
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
    }
}
