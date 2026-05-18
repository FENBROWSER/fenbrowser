using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;
using System.Linq;

namespace FenBrowser.Tests.Core.Parsing
{
    public class TableParsingTests
    {
        private Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }
        
        private Element GetBody(Document doc)
        {
            var html = doc.DocumentElement;
            if (html == null) return null;
            return html.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
        }

        [Fact]
        public void BasicTable_ParsesCorrectly()
        {
            var html = "<table><tr><td>Cell 1</td><td>Cell 2</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);
            
            var table = body.Children.FirstOrDefault(c => (c as Element)?.TagName == "TABLE") as Element;
            Assert.NotNull(table);
            
            var tbody = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "TBODY") as Element;
            Assert.NotNull(tbody); // Implicit tbody
            
            var tr = tbody.Children.FirstOrDefault(c => (c as Element)?.TagName == "TR") as Element;
            Assert.NotNull(tr);
            Assert.Equal(1, table.Children.Length);
        }

        [Fact]
        public void FosterParenting_MovesContentBeforeTable()
        {
            // "abc" should be foster parented BEFORE the table
            var html = "<table>abc<tr><td>Cell</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);
            
            var bodyChildren = body.ChildNodes.ToList();
            var tableIndex = bodyChildren.FindIndex(c => (c as Element)?.TagName == "TABLE");
            
            Assert.True(tableIndex > 0, "Table should not be first child (index: " + tableIndex + ")");
            
            // Checking content before table
            var nodeBefore = bodyChildren[tableIndex - 1];
            Assert.IsType<Text>(nodeBefore);
            Assert.Equal("abc", ((Text)nodeBefore).Data);
        }

        [Fact]
        public void TableHeaders_ParseCorrectly()
        {
            var html = "<table><thead><tr><th>Header</th></tr></thead><tbody><tr><td>Data</td></tr></tbody></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            
            var table = body.Children.FirstOrDefault(c => (c as Element)?.TagName == "TABLE") as Element;
            Assert.Equal("TABLE", table.TagName);
            
            var thead = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "THEAD") as Element;
            Assert.NotNull(thead);
            
            var tr = thead.Children.FirstOrDefault(c => (c as Element)?.TagName == "TR") as Element;
            var th = tr.Children.FirstOrDefault(c => (c as Element)?.TagName == "TH") as Element;
            
            Assert.Equal("TH", th.TagName);
            Assert.Equal("Header", th.Text);
        }

        [Fact]
        public void Caption_ParsesAsSingleCaptionElement()
        {
            var html = "<table><caption>Title</caption><tr><td>Cell</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var table = body.Children.FirstOrDefault(c => (c as Element)?.TagName == "TABLE") as Element;
            Assert.NotNull(table);

            var captions = table.Children.Where(c => (c as Element)?.TagName == "CAPTION").Cast<Element>().ToList();
            Assert.Single(captions);
            Assert.Equal("Title", captions[0].Text);
        }
        
        [Fact]
        public void ColGroup_ParsesCorrectly()
        {
            var html = "<table><colgroup><col span=\"2\"><col></colgroup><tr><td>1</td><td>2</td><td>3</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            
            var table = body.Children.FirstOrDefault(c => (c as Element)?.TagName == "TABLE") as Element;
            var colgroup = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "COLGROUP") as Element;
            Assert.NotNull(colgroup);
            
            Assert.Equal(2, colgroup.Children.Count(c => (c as Element)?.TagName == "COL"));
        }

        [Fact]
        public void Acid2_MisnestedParagraphAroundTable_ClosesParagraphBeforeTable()
        {
            var html = "<div class=\"picture\"><p><table><tr><td></table><p class=\"bad\"></div>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var picture = body.Children.FirstOrDefault(c => (c as Element)?.GetAttribute("class") == "picture") as Element;
            Assert.NotNull(picture);

            var pictureChildren = picture.ChildNodes.Where(n => n is Element).Cast<Element>().ToList();
            Assert.True(pictureChildren.Count >= 3, "Expected an empty paragraph, a table, and the following paragraph under .picture.");

            Assert.Equal("P", pictureChildren[0].TagName);
            Assert.Empty(pictureChildren[0].ChildNodes.OfType<Element>());

            Assert.Equal("TABLE", pictureChildren[1].TagName);

            Assert.Equal("P", pictureChildren[2].TagName);
            Assert.Equal("bad", pictureChildren[2].GetAttribute("class"));

            Assert.DoesNotContain(pictureChildren[1].ChildNodes, n => (n as Element)?.TagName == "P");
            Assert.DoesNotContain(pictureChildren[2].ChildNodes, n => (n as Element)?.TagName == "TABLE");
        }

        [Fact]
        public void FosterParenting_ClosesMisnestedElementBeforeLeavingTableMode()
        {
            var html = "<table><div>a</div></table>b";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var bodyChildren = body.ChildNodes.ToList();
            var tableIndex = bodyChildren.FindIndex(c => (c as Element)?.TagName == "TABLE");
            Assert.True(tableIndex > 0, "Expected foster-parented element before table.");

            var table = bodyChildren[tableIndex] as Element;
            Assert.NotNull(table);
            Assert.DoesNotContain(table.Descendants().OfType<Element>(), el => el.TagName == "DIV");
            Assert.DoesNotContain(table.Descendants().OfType<Text>(), text => text.Data.Contains("b"));

            Assert.True(tableIndex + 1 < bodyChildren.Count, "Expected trailing text node after table.");
            var trailingText = bodyChildren[tableIndex + 1] as Text;
            Assert.NotNull(trailingText);
            Assert.Equal("b", trailingText.Data);
        }

        [Fact]
        public void FosterParenting_UnclosedMisnestedElement_DoesNotCaptureTrailingTextAfterTable()
        {
            var html = "<table><div><span>a</table>b";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var bodyChildren = body.ChildNodes.ToList();
            var tableIndex = bodyChildren.FindIndex(c => (c as Element)?.TagName == "TABLE");
            Assert.True(tableIndex > 0, "Expected foster-parented element before table.");

            var table = bodyChildren[tableIndex] as Element;
            Assert.NotNull(table);
            Assert.DoesNotContain(table.Descendants().OfType<Element>(), el => el.TagName == "DIV" || el.TagName == "SPAN");
            Assert.DoesNotContain(table.Descendants().OfType<Text>(), text => text.Data.Contains("b"));

            Assert.True(tableIndex + 1 < bodyChildren.Count, "Expected trailing text node after table.");
            var trailingText = bodyChildren[tableIndex + 1] as Text;
            Assert.NotNull(trailingText);
            Assert.Equal("b", trailingText.Data);
        }

        [Fact]
        public void FosterParenting_MisnestedFormattingElement_DoesNotWrapTableCellContent()
        {
            var html = "<table><b>lead<tr><td>cell</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var bodyElements = body.ChildNodes.OfType<Element>().ToList();
            var table = bodyElements.FirstOrDefault(e => e.TagName == "TABLE");
            Assert.NotNull(table);

            var b = bodyElements.FirstOrDefault(e => e.TagName == "B");
            Assert.NotNull(b);
            Assert.Contains("lead", b!.TextContent);

            var td = table!.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TD");
            Assert.NotNull(td);
            Assert.Equal("cell", td!.TextContent);
            Assert.DoesNotContain(td.Ancestors().OfType<Element>(), e => e.TagName == "B");
        }

        [Fact]
        public void TableText_MixedWhitespaceAndContent_IsFosterParentedBeforeTable()
        {
            var html = "<table> \nX<tr><td>cell</td></tr></table>";
            var doc = Parse(html);
            var body = GetBody(doc);
            Assert.NotNull(body);

            var bodyChildren = body.ChildNodes.ToList();
            var tableIndex = bodyChildren.FindIndex(c => (c as Element)?.TagName == "TABLE");
            Assert.True(tableIndex > 0, "Expected foster-parented text before table.");

            var textBeforeTable = bodyChildren[tableIndex - 1] as Text;
            Assert.NotNull(textBeforeTable);
            Assert.Equal(" \nX", textBeforeTable!.Data);

            var table = bodyChildren[tableIndex] as Element;
            var td = table!.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName == "TD");
            Assert.NotNull(td);
            Assert.Equal("cell", td!.TextContent);
        }
    }
}
