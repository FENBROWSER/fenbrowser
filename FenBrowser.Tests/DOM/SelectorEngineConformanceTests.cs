using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Dom
{
    public class SelectorEngineConformanceTests
    {
        [Fact]
        public void ElementMatches_SupportsNthChildOfSelector()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <ul>
        <li id='n1' class='keep'>n1</li>
        <li id='n2'>n2</li>
        <li id='n3' class='keep'>n3</li>
        <li id='n4' class='keep'>n4</li>
    </ul>
</body></html>");

            var n1 = ById(doc, "n1");
            var n3 = ById(doc, "n3");
            var n4 = ById(doc, "n4");

            Assert.False(n1.Matches("li:nth-child(2 of .keep)"));
            Assert.True(n3.Matches("li:nth-child(2 of .keep)"));
            Assert.False(n4.Matches("li:nth-child(2 of .keep)"));
        }

        [Fact]
        public void ElementMatches_AttributeFlagsAcceptCaseSensitiveOverride()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='target' data-code='AbC'></div>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(target.Matches("[data-code='abc' i]"));
            Assert.False(target.Matches("[data-code='abc' s]"));
            Assert.True(target.Matches("[data-code='AbC' s]"));
        }

        [Fact]
        public void QuerySelector_ReadsCssEscapesInAnIdentifier()
        {
            // The form wptrunner builds every selector in: each character as a hex escape
            // followed by a space. It has to find the same element the plain selector does.
            var doc = Parse(@"
<!doctype html>
<html><body>
    <button id='wpt-test-driver-bless-1'></button>
    <p class='a.b'></p>
</body></html>");

            var escaped = "#" + string.Concat("wpt-test-driver-bless-1".Select(c => "\\" + ((int)c).ToString("x") + " "));
            Assert.NotNull(doc.QuerySelector(escaped));
            Assert.Same(doc.QuerySelector("#wpt-test-driver-bless-1"), doc.QuerySelector(escaped));

            // A character that would otherwise be a selector of its own, escaped: the class
            // here really is called "a.b".
            Assert.NotNull(doc.QuerySelector(".a\\.b"));
        }

        private static Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }

        private static Element ById(Document doc, string id)
        {
            return doc.Descendants().OfType<Element>().First(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
    }
}
