using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;
using NewCss = FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.Tests.Core
{
    // Real pages ship the same stylesheet URL several times — github.com serves one
    // primer-react-css URL five times, and the engine parsed those 286KB from scratch
    // for each registration because the parse cache keyed on source order. Source
    // order changes exactly one thing in the parse output (CssStyleRule.Order), so
    // identical bytes must parse once and be re-stamped per registration.
    public sealed class CssDuplicateStylesheetParseTests
    {
        [Fact]
        public void IdenticalStylesheetRegisteredTwice_ParsesOnceButKeepsPerSheetIdentity()
        {
            CssLoader.ClearCaches();

            var baseUri = new System.Uri("https://dup-css.test/");
            var doc = new HtmlParser(
                "<!doctype html><html><body><div class='box'>x</div></body></html>",
                baseUri).Parse();
            var box = doc.DocumentElement.Descendants().OfType<Element>()
                .First(e => e.ClassList.Contains("box"));

            // Same bytes, same base URI, same origin — only the source order differs,
            // exactly like a page linking one stylesheet twice.
            const string duplicateCss = ".box { color: red; }";
            var sources = new List<CssLoader.CssSource>
            {
                new CssLoader.CssSource
                {
                    CssText = duplicateCss,
                    Origin = CssLoader.CssOrigin.External,
                    SourceOrder = 3,
                    BaseUri = baseUri
                },
                new CssLoader.CssSource
                {
                    CssText = duplicateCss,
                    Origin = CssLoader.CssOrigin.External,
                    SourceOrder = 7,
                    BaseUri = baseUri
                }
            };

            var matched = CssLoader.GetMatchedRules(box, sources)
                .Where(m => m.Rule is NewCss.CssStyleRule)
                .Select(m => (NewCss.CssStyleRule)m.Rule)
                .ToList();

            Assert.Equal(2, matched.Count);

            // Each registration needs its OWN rule objects: the cascade stamps
            // StylesheetSourceOrder and Origin onto rules per stylesheet
            // (CascadeEngine.IndexRules), so sharing instances would make the last
            // sheet indexed overwrite every earlier one.
            Assert.NotSame(matched[0], matched[1]);

            // And each copy carries the cascade order of its own registration.
            Assert.Equal(3 * 10000, matched[0].Order);
            Assert.Equal(7 * 10000, matched[1].Order);

            // One parse served both registrations. The per-registration copies are
            // shallow, so they share the declaration list that the single parse
            // produced — two independent parses would have built two lists. This is
            // asserted on object identity rather than CssLoader.ParsedRuleCacheCount,
            // which is process-global and moves under xUnit's parallel collections.
            Assert.Same(matched[0].Declarations, matched[1].Declarations);
        }
    }
}
