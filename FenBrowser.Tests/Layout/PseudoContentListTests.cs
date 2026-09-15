using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Generated Content §2: 'content' is a list of strings, attr() and quote keywords
    /// concatenated in order. bing.com's search field uses
    /// `content: attr(data-replicated-value) " "`, which used to paint as literal CSS text.
    /// </summary>
    public sealed class PseudoContentListTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #replica::after { content: attr(data-replicated-value) "" ""; white-space: pre-wrap; }
  #mixed::before { content: ""["" attr(title) ""]""; }
  #plain::after { content: 'x'; }
</style></head>
<body>
  <div id='replica' data-replicated-value='hello'></div>
  <div id='mixed' title='t'></div>
  <div id='plain'></div>
</body></html>";

        [Fact]
        public async Task ContentList_ConcatenatesStringsAndAttributes()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            Element El(string id) => doc.Descendants().OfType<Element>().First(e => e.Id == id);
            string Generated(PseudoElement pseudo) =>
                string.Concat(pseudo.ChildNodes.OfType<Text>().Select(t => t.Data));

            Assert.Equal("hello ", Generated(styles[El("replica")].After.PseudoElementInstance));
            Assert.Equal("[t]", Generated(styles[El("mixed")].Before.PseudoElementInstance));
            Assert.Equal("x", Generated(styles[El("plain")].After.PseudoElementInstance));
        }
    }
}
