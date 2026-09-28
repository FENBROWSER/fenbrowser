using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // CSS Lists 3 §4 list-item numbering. The expected values are those the WPT
    // references for css/css-lists/li-value-reversed-* and li-list-item-counter-*
    // spell out with explicit value attributes.
    public class ListItemCountersTests
    {
        private static async Task<string> NumbersAsync(string body)
        {
            var html = "<!doctype html><html><head><style>html,body{margin:0}</style></head><body>" + body + "</body></html>";
            var uri = new Uri("https://lists.test/");
            var document = new HtmlParser(html, uri).Parse();
            var root = document.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, uri, null);
            var values = ListItemCounters.Compute(
                document,
                element => styles.TryGetValue(element, out var style) ? style : null);
            return string.Join(",", document.Descendants().OfType<Element>()
                .Where(element => string.Equals(element.LocalName, "li", StringComparison.OrdinalIgnoreCase))
                .Select(element => values.TryGetValue(element, out var value) ? value.ToString() : "?"));
        }

        [Theory]
        [InlineData("<ol><li>a<li>b<li>c</ol>", "1,2,3")]
        [InlineData("<ol start=5><li>a<li>b</ol>", "5,6")]
        [InlineData("<ol reversed><li>a<li>b<li>c</ol>", "3,2,1")]
        [InlineData("<ol reversed start=5><li>a<li>b</ol>", "5,4")]
        // li-value-reversed-001, -010, -013, -015, -016, -018, -022
        [InlineData("<ol reversed><li>Seven<li value=6>Six<li>Five</ol>", "7,6,5")]
        [InlineData("<ol reversed><li>Four<li style='counter-increment: list-item -2'>Two<li>One</ol>", "4,2,1")]
        [InlineData("<ol reversed><li>Five<li value=3 style='counter-increment: list-item -2'>Three<li>Two</ol>", "5,3,2")]
        [InlineData("<ol reversed><li>Minus One<li style='counter-increment: list-item 3'>Two<li>One</ol>", "-1,2,1")]
        [InlineData("<ol reversed><li value=5>Five<div style='counter-set: list-item 2'></div><div style='counter-increment: list-item -1'></div><li>Zero</ol>", "5,0")]
        [InlineData("<ol reversed><li>One<div style='counter-increment: list-item 1'></div><li>One</ol>", "1,1")]
        [InlineData("<ol reversed><li>Six<li style='counter-set: list-item 5'>Five<li>Four<li style='counter-set: list-item 2'>Two<li>One</ol>", "6,5,4,2,1")]
        // li-value-reversed-014 and -019: a nested list keeps its own counter, and the
        // item after it continues the outer one.
        [InlineData("<ol reversed><li>Five<li style='counter-increment: list-item -3'>Two<ol reversed><li>Four<li style='counter-increment: list-item -2'>Two<li>One</ol><li>One</ol>", "5,2,4,2,1,1")]
        [InlineData("<ol reversed><li>Three<div style='counter-increment: list-item -1'></div><ol reversed><li>Four<div style='counter-set: list-item 3'></div><li>Two</ol><li>One</ol>", "3,4,2,1")]
        // li-value-reversed-023 to -030: an author counter-reset replaces HTML's.
        [InlineData("<ol style='counter-reset: reversed(list-item)'><li>Three<li>Two<li>One</ol>", "3,2,1")]
        [InlineData("<ol reversed style='counter-reset: list-item'><li>One<li>Two<li>Three</ol>", "1,2,3")]
        [InlineData("<ol reversed style='counter-reset: list-item 3'><li>Four<li>Five<li>Six</ol>", "4,5,6")]
        [InlineData("<ol start=5 style='counter-reset: reversed(list-item)'><li>Three<li>Two<li>One</ol>", "3,2,1")]
        [InlineData("<ol start=9 style='counter-reset: reversed(list-item) 4'><li>Three<li>Two<li>One</ol>", "3,2,1")]
        [InlineData("<ol><div style='counter-reset: reversed(list-item)'><li>Three</li><li>Two</li><li>One</li></div></ol>", "3,2,1")]
        [InlineData("<ol><div style='counter-reset: list-item 3'><li>Four</li><li>Five</li><li>Six</li></div></ol>", "4,5,6")]
        // li-list-item-counter-001 and li-value-counter-reset-001: an explicit
        // increment replaces the implied one, and counter-set beats value.
        [InlineData("<ol><li class=a style='counter-increment: list-item 0'>a<li value=4 style='counter-increment: list-item 0'>b<li style='counter-increment: list-item 0'>c</ol>", "0,4,4")]
        [InlineData("<ol><li style='counter-increment: list-item 1 list-item -1'>a<li value=4 style='counter-increment: list-item 1 list-item -1'>b<li style='counter-increment: list-item 1 list-item -1'>c</ol>", "0,4,4")]
        [InlineData("<ol><li>a<li value=4 style='counter-set: list-item 99; counter-increment: list-item 50'>b</ol>", "1,99")]
        [InlineData("<ol><li>a<li value=4 style='counter-increment: list-item 50'>b</ol>", "1,4")]
        [InlineData("<ol><li>a<li style='counter-increment: list-item 50'>b</ol>", "1,51")]
        [InlineData("<ol><li value=' 7x'>a<li>b</ol>", "7,8")]
        public async Task ListItemCounterFollowsCssLists(string body, string expected)
        {
            Assert.Equal(expected, await NumbersAsync(body));
        }

        // CSS Lists 3 §4.5: an author counter nests like list-item, without the HTML
        // list defaults. Each <p> reports the counter's instances in scope, outermost
        // first, as counters() would show them. A reset's scope takes in the
        // element's following siblings (CSS 2.1 §12.4.1), so the last <p> counts on
        // in the inner counter.
        [Theory]
        [InlineData("<div style='counter-reset: c'><p style='counter-increment: c'></p><p style='counter-increment: c'></p></div>", "1|2")]
        [InlineData("<p style='counter-reset: c 4; counter-increment: c'></p><p style='counter-increment: c 2'></p>", "5|7")]
        [InlineData("<div style='counter-reset: c'><p style='counter-increment: c'></p><div style='counter-reset: c'><p style='counter-increment: c'></p></div><p style='counter-increment: c'></p></div>", "1|1.1|1.2")]
        // Counter names are case-sensitive, and list-item is a different counter: 'c'
        // is not in scope anywhere here.
        [InlineData("<ol><li><p style='counter-increment: C'></p></ol>", "-")]
        [InlineData("<ol><li><p></p></ol>", "-")]
        public async Task NamedCountersNestAndInherit(string body, string expected)
        {
            var html = "<!doctype html><html><body>" + body + "</body></html>";
            var uri = new Uri("https://counters.test/");
            var document = new HtmlParser(html, uri).Parse();
            var styles = await CssLoader.ComputeAsync(document.DocumentElement, uri, null);
            var chains = ListItemCounters.ComputeCounter(
                document,
                element => styles.TryGetValue(element, out var style) ? style : null,
                "c");

            var shown = document.Descendants().OfType<Element>()
                .Where(element => string.Equals(element.LocalName, "p", StringComparison.OrdinalIgnoreCase))
                .Select(element => chains.TryGetValue(element, out var chain) ? string.Join(".", chain) : "-");
            Assert.Equal(expected, string.Join("|", shown));
        }

        // counter-list-item-slot-order: numbering follows the flat tree, so slotted
        // items count in slot order inside the shadow tree's lists.
        [Fact]
        public async Task CountersFollowTheFlatTree()
        {
            var uri = new Uri("https://counters.test/");
            var document = new HtmlParser(
                "<!doctype html><html><body><div id=host>" +
                "<li id=c slot=list3>c</li><li id=b slot=list2>b</li><li id=a slot=list1>a</li>" +
                "</div></body></html>",
                uri).Parse();
            var host = Assert.IsType<Element>(document.GetElementById("host"));
            var shadow = host.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Closed });
            var outer = document.CreateElement("ol");
            shadow.AppendChild(outer);
            foreach (var name in new[] { "list1", "list2" })
            {
                var slot = document.CreateElement("slot");
                slot.SetAttribute("name", name);
                outer.AppendChild(slot);
            }

            var inner = document.CreateElement("ol");
            var innerSlot = document.CreateElement("slot");
            innerSlot.SetAttribute("name", "list3");
            inner.AppendChild(innerSlot);
            outer.AppendChild(inner);

            var styles = await CssLoader.ComputeAsync(document.DocumentElement, uri, null);
            var chains = ListItemCounters.ComputeCounter(
                document,
                element => styles.TryGetValue(element, out var style) ? style : null,
                "list-item",
                FlatChildren);

            string Show(string id) => string.Join(".", chains[Assert.IsType<Element>(document.GetElementById(id))]);
            Assert.Equal("1", Show("a"));
            Assert.Equal("2", Show("b"));
            Assert.Equal("2.1", Show("c"));
        }

        private static System.Collections.Generic.IEnumerable<Node> FlatChildren(Node node)
        {
            if (node is Element element)
            {
                if (string.Equals(element.LocalName, "slot", StringComparison.OrdinalIgnoreCase) &&
                    element.GetRootNode() is ShadowRoot root)
                {
                    return root.GetAssignedNodesForSlot(element);
                }

                if (element.GetAttachedShadowRoot() is { } attached)
                {
                    return attached.ChildNodes;
                }
            }

            return node.ChildNodes;
        }
    }
}
