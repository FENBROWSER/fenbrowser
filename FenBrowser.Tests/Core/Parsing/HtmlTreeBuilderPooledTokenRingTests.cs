using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    /// <summary>
    /// Regression tests for CORE-TB-001: pooled character tokens retained by the
    /// InTableText pending buffer while the HtmlTokenPool char ring (16384 slots)
    /// keeps recycling them, silently corrupting fostered table text.
    /// </summary>
    public class HtmlTreeBuilderPooledTokenRingTests
    {
        private static Document Parse(string html)
        {
            return new HtmlParser(html).Parse();
        }

        private static Element GetBody(Document doc)
        {
            var html = doc.DocumentElement;
            return html?.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
        }

        private static string GetTextBeforeTable(Element body)
        {
            var children = body.ChildNodes.ToList();
            var tableIndex = children.FindIndex(c => (c as Element)?.TagName == "TABLE");
            Assert.True(tableIndex > 0, "Fostered text should precede the table.");
            return string.Concat(children.Take(tableIndex).OfType<Text>().Select(t => t.Data));
        }

        [Fact]
        public void FosteredEntityText_SurvivesCharacterTokenPoolRingWrap()
        {
            // Each "A&amp;B" repetition rents three character tokens (run, entity, run),
            // so 20000 repetitions rent 60000 pooled character tokens — well past the
            // 16384-slot ring boundary — all buffered before the next non-character token.
            const int repetitions = 20000;
            var sb = new StringBuilder("<table>");
            for (var i = 0; i < repetitions; i++)
            {
                sb.Append("A&amp;B");
            }
            sb.Append("<tr><td>cell</td></tr></table>");

            var doc = Parse(sb.ToString());
            var body = GetBody(doc);

            Assert.NotNull(body);
            var expected = string.Concat(Enumerable.Repeat("A&B", repetitions));
            var actual = GetTextBeforeTable(body);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void FosteredPlainTextRun_LargerThanBatchWindow_RemainsIntact()
        {
            const int length = 40000;
            var html = "<table>" + new string('x', length) + "<tr><td>cell</td></tr></table>";

            var doc = Parse(html);
            var body = GetBody(doc);

            Assert.NotNull(body);
            Assert.Equal(new string('x', length), GetTextBeforeTable(body));
        }

        [Fact]
        public void WhitespaceOnlyTableText_KeepsCellContentIntact()
        {
            var doc = Parse("<table>   \n  <tr><td>cell</td></tr></table>");
            var body = GetBody(doc);

            Assert.NotNull(body);
            var table = body.Children.FirstOrDefault(c => (c as Element)?.TagName == "TABLE") as Element;
            Assert.NotNull(table);
            Assert.Contains("cell", table.TextContent);
        }
    }
}
