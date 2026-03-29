using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderFormattingRecoveryTests
    {
        [Fact]
        public void Build_MisnestedFormattingTags_ReparentsWithoutLosingText()
        {
            const string html = """
<!doctype html>
<html>
  <body>
    <p><b>one<i>two</b>three</i></p>
  </body>
</html>
""";

            var builder = new HtmlTreeBuilder(html);
            var document = builder.Build();

            var paragraph = document.Descendants().OfType<Element>().Single(e => e.TagName == "P");
            var bold = paragraph.Descendants().OfType<Element>().First(e => e.TagName == "B");
            var italics = paragraph.Descendants().OfType<Element>().Where(e => e.TagName == "I").ToList();

            Assert.Equal("onetwothree", paragraph.TextContent.Replace(" ", string.Empty));
            Assert.Equal("onetwo", bold.TextContent.Replace(" ", string.Empty));
            Assert.NotEmpty(italics);
            Assert.Contains("three", italics.Last().TextContent);
        }
    }
}
