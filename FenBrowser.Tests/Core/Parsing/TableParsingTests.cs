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
    }
}
