using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    // ParallelCascadeScheduler fans body's subtrees across threads and shares a
    // single CascadeEngine between them. Everything that engine writes during the
    // fan-out has to tolerate that, and its per-element style cache did not: a
    // plain Dictionary written from several threads drops entries, and an element
    // whose computed style went missing renders with its initial values instead.
    //
    // On a 400-row page that showed up as rows losing `display: flex` and laying
    // out as blocks - different rows on every run, one to five of them, never
    // none. This asserts the property directly rather than through pixels.
    public class ParallelCascadeSharedCacheTests
    {
        private const int RowCount = 400;

        [Fact]
        public void EveryElementGetsItsComputedStyle_WhenSubtreesCascadeInParallel()
        {
            // Repeated because the failure is a race: a single pass could get
            // lucky, and the point is that no pass may lose an element.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var (root, styleSet) = BuildDocument();

                var computed = ParallelCascadeScheduler.Cascade(root, styleSet, _ => { }, deadline: null);

                var rows = root.Descendants()
                    .OfType<Element>()
                    .Where(e => (e.GetAttribute("class") ?? string.Empty).Contains("row"))
                    .ToArray();
                Assert.Equal(RowCount, rows.Length);

                var missing = rows.Where(r => !computed.ContainsKey(r)).ToArray();
                Assert.True(
                    missing.Length == 0,
                    $"attempt {attempt}: {missing.Length} of {RowCount} rows have no computed style at all.");

                var notFlex = rows
                    .Where(r => !string.Equals(ReadDisplay(computed[r]), "flex", System.StringComparison.Ordinal))
                    .ToArray();
                Assert.True(
                    notFlex.Length == 0,
                    $"attempt {attempt}: {notFlex.Length} of {RowCount} rows lost 'display: flex' " +
                    $"(first: '{(notFlex.Length > 0 ? ReadDisplay(computed[notFlex[0]]) : string.Empty)}').");
            }
        }

        private static string ReadDisplay(CssComputed style) =>
            style != null && style.Map.TryGetValue("display", out var value) ? value : string.Empty;

        private static (Element Root, StyleSet Styles) BuildDocument()
        {
            var markup = new System.Text.StringBuilder("<html><head></head><body>");
            for (var i = 0; i < RowCount; i++)
            {
                markup.Append("<div class=\"row\"><span class=\"k\">Item ").Append(i)
                      .Append("</span><span class=\"v\">").Append(1000 + i)
                      .Append("</span><p>text ").Append(i).Append("</p></div>");
            }
            markup.Append("</body></html>");

            var document = new HtmlParser(markup.ToString(), new System.Uri("https://cascade.test/")).Parse();

            const string css = ".row { display: flex; } .k { width: 120px; } .v { width: 80px; } p { margin: 0; }";
            var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
            var styleSet = new StyleSet();
            styleSet.AddSheet(sheet, CssOrigin.Author, 0);

            return (document.DocumentElement, styleSet);
        }
    }
}
