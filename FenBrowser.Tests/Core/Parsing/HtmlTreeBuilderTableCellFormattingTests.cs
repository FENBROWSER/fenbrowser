using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderTableCellFormattingTests
    {
        [Fact]
        public void Build_FormattingEndTagInsideTableCell_DoesNotCrashAndPreservesContent()
        {
            const string html = """
<!doctype html>
<html>
  <body>
    <table>
      <tr>
        <td><a href="/wiki/Main_Page"><span>Main page</span></a></td>
      </tr>
    </table>
  </body>
</html>
""";

            var builder = new HtmlTreeBuilder(html);
            var document = builder.Build();

            Assert.NotNull(document.DocumentElement);

            var td = document.Descendants().OfType<Element>().Single(e => e.TagName == "TD");
            var anchor = document.Descendants().OfType<Element>().Single(e => e.TagName == "A");
            var span = document.Descendants().OfType<Element>().Single(e => e.TagName == "SPAN");

            Assert.Equal("/wiki/Main_Page", anchor.GetAttribute("href"));
            Assert.Equal("Main page", span.TextContent);
            Assert.Contains(anchor, td.Children);
        }
    }
}
