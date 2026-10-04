using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>Parses, cascades and lays out an HTML body, then answers border boxes by id.</summary>
    internal sealed class HtmlLayoutProbe
    {
        private readonly Document _doc;
        private readonly LayoutEngineComputer _computer;
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;

        private HtmlLayoutProbe(Document doc, LayoutEngineComputer computer, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            _doc = doc;
            _computer = computer;
            _styles = styles;
        }

        public static async Task<HtmlLayoutProbe> LayoutAsync(string body, string css = "", float viewportWidth = 1200f, float viewportHeight = 800f)
        {
            string html = "<!doctype html><html><head><style>" +
                          "body{margin:0;font-size:16px;font-family:sans-serif}" + css +
                          "</style></head><body>" + body + "</body></html>";
            var uri = new Uri("https://example.test/");
            var doc = new HtmlParser(html, uri).Parse();
            var styles = await CssLoader.ComputeAsync(doc.DocumentElement, uri, null, viewportWidth: viewportWidth, viewportHeight: viewportHeight);
            var computer = new LayoutEngineComputer(styles, viewportWidth, viewportHeight);
            computer.Measure(doc, new SKSize(viewportWidth, viewportHeight));
            computer.Arrange(doc, new SKRect(0, 0, viewportWidth, viewportHeight));
            return new HtmlLayoutProbe(doc, computer, styles);
        }

        public Element Element(string id) => _doc.Descendants().OfType<Element>().First(e => e.Id == id);

        public CssComputed Style(string id) => _styles.TryGetValue(Element(id), out var style) ? style : null;

        public SKRect Rect(string id)
        {
            var box = _computer.GetBox(Element(id));
            Assert.NotNull(box);
            return box.BorderBox;
        }
    }
}
