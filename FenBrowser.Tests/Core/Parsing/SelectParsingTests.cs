using System.Linq;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class SelectParsingTests
    {
        private static Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }

        [Fact]
        public void Select_ImplicitlyClosesPreviousOption()
        {
            var doc = Parse("<html><body><select><option>one<option>two</select></body></html>");
            var options = doc.Descendants().OfType<Element>().Where(e => e.TagName == "OPTION").ToList();

            Assert.Equal(2, options.Count);
            Assert.Equal("one", options[0].TextContent);
            Assert.Equal("two", options[1].TextContent);
        }

        [Fact]
        public void SelectInTable_TableStartTagClosesSelectBeforeReprocess()
        {
            var doc = Parse("<html><body><table><tr><td><select><option>x</option><table><tr><td>y</td></tr></table></td></tr></table></body></html>");

            var body = doc.DocumentElement?.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
            Assert.NotNull(body);

            var allTables = body!.Descendants().OfType<Element>().Where(e => e.TagName == "TABLE").ToList();
            Assert.True(allTables.Count >= 2);

            var selects = body.Descendants().OfType<Element>().Where(e => e.TagName == "SELECT").ToList();
            Assert.Single(selects);
            Assert.DoesNotContain(selects[0].Descendants().OfType<Element>(), e => e.TagName == "TABLE");
        }

        [Fact]
        public void SelectInTable_TableEndTagClosesSelectBeforeReprocess()
        {
            var doc = Parse("<html><body><table><tr><td><select><option>x</option></table><p id='ok'>done</p></body></html>");

            var body = doc.DocumentElement?.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
            Assert.NotNull(body);

            var selects = body!.Descendants().OfType<Element>().Where(e => e.TagName == "SELECT").ToList();
            Assert.Single(selects);

            var marker = body.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "ok");
            Assert.NotNull(marker);
            Assert.Equal("done", marker!.TextContent);
        }

        [Fact]
        public void ResetInsertionMode_SelectWithTableAncestor_UsesInSelectInTable()
        {
            var builder = new HtmlTreeBuilder(string.Empty);
            var openElementsField = typeof(HtmlTreeBuilder).GetField("_openElements", BindingFlags.NonPublic | BindingFlags.Instance);
            var resetMethod = typeof(HtmlTreeBuilder).GetMethod("ResetInsertionMode", BindingFlags.NonPublic | BindingFlags.Instance);
            var insertionModeField = typeof(HtmlTreeBuilder).GetField("_insertionMode", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(openElementsField);
            Assert.NotNull(resetMethod);
            Assert.NotNull(insertionModeField);

            var stack = (System.Collections.Generic.Stack<Element>)openElementsField!.GetValue(builder)!;
            stack.Push(new Element("html"));
            stack.Push(new Element("table"));
            stack.Push(new Element("select"));

            resetMethod!.Invoke(builder, null);
            var mode = insertionModeField!.GetValue(builder)?.ToString();
            Assert.Equal("InSelectInTable", mode);
        }

        [Fact]
        public void ResetInsertionMode_SelectWithoutTableAncestor_UsesInSelect()
        {
            var builder = new HtmlTreeBuilder(string.Empty);
            var openElementsField = typeof(HtmlTreeBuilder).GetField("_openElements", BindingFlags.NonPublic | BindingFlags.Instance);
            var resetMethod = typeof(HtmlTreeBuilder).GetMethod("ResetInsertionMode", BindingFlags.NonPublic | BindingFlags.Instance);
            var insertionModeField = typeof(HtmlTreeBuilder).GetField("_insertionMode", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(openElementsField);
            Assert.NotNull(resetMethod);
            Assert.NotNull(insertionModeField);

            var stack = (System.Collections.Generic.Stack<Element>)openElementsField!.GetValue(builder)!;
            stack.Push(new Element("html"));
            stack.Push(new Element("body"));
            stack.Push(new Element("select"));

            resetMethod!.Invoke(builder, null);
            var mode = insertionModeField!.GetValue(builder)?.ToString();
            Assert.Equal("InSelect", mode);
        }
    }
}
