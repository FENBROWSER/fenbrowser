using FenBrowser.Core.Dom;
using FenBrowser.Core.Parsing;
using Xunit;
using System.Linq;

namespace FenBrowser.Core.Tests.Parsing
{
    public class TableParsingTests
    {
        private Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }

        [Fact]
        public void BasicTable_ParsesCorrectly()
        {
            var html = "<table><tr><td>Cell 1</td><td>Cell 2</td></tr></table>";
            var doc = Parse(html);
            
            var table = doc.Body.Children.FirstOrDefault(c => (c as Element)?.TagName == "table") as Element;
            Assert.NotNull(table);
            
            var tbody = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "tbody") as Element;
            Assert.NotNull(tbody); // Implicit tbody
            
            var tr = tbody.Children.FirstOrDefault(c => (c as Element)?.TagName == "tr") as Element;
            Assert.NotNull(tr);
            Assert.Equal(2, tr.Children.Count(c => (c as Element)?.TagName == "td"));
        }

        [Fact]
        public void FosterParenting_MovesContentBeforeTable()
        {
            // "abc" should be foster parented BEFORE the table
            var html = "<table>abc<tr><td>Cell</td></tr></table>";
            var doc = Parse(html);
            
            var bodyChildren = doc.Body.Children.ToList();
            var tableIndex = bodyChildren.FindIndex(c => (c as Element)?.TagName == "table");
            
            Assert.True(tableIndex > 0, "Table should not be first child");
            
            // Checking content before table
            // Due to text node merging, "abc" might be in a text node before table
            var nodeBefore = bodyChildren[tableIndex - 1];
            Assert.IsType<Text>(nodeBefore);
            Assert.Equal("abc", ((Text)nodeBefore).Data);
        }

        [Fact]
        public void TableHeaders_ParseCorrectly()
        {
            var html = "<table><thead><tr><th>Header</th></tr></thead><tbody><tr><td>Data</td></tr></tbody></table>";
            var doc = Parse(html);
            
            var table = doc.Body.FirstElementChild;
            Assert.Equal("table", table.TagName);
            
            var thead = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "thead") as Element;
            Assert.NotNull(thead);
            
            var th = thead.FirstElementChild?.FirstElementChild;
            Assert.Equal("th", th.TagName);
            Assert.Equal("Header", th.InnerText);
        }
        
        [Fact]
        public void ColGroup_ParsesCorrectly()
        {
            var html = "<table><colgroup><col span=\"2\"><col></colgroup><tr><td>1</td><td>2</td><td>3</td></tr></table>";
            var doc = Parse(html);
            
            var table = doc.Body.FirstElementChild;
            var colgroup = table.Children.FirstOrDefault(c => (c as Element)?.TagName == "colgroup") as Element;
            Assert.NotNull(colgroup);
            
            Assert.Equal(2, colgroup.Children.Count(c => (c as Element)?.TagName == "col"));
        }
    }
}
