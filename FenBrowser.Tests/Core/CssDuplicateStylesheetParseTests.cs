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

        // Shadow-scoped sheets share the cache too, keyed on the shadow root's unique
        // ScopeIdentity. Identical CSS in two different shadow trees must NOT share a
        // parse, or ApplyShadowScope would stamp one tree's root onto the other's
        // rules (they are shared shallowly, including nested rules).
        [Fact]
        public void IdenticalCssInTwoShadowTrees_DoesNotShareRules()
        {
            CssLoader.ClearCaches();

            var baseUri = new System.Uri("https://dup-css.test/");
            var doc = new HtmlParser(
                "<!doctype html><html><body><div id='a'></div><div id='b'></div></body></html>",
                baseUri).Parse();
            var hostA = Assert.IsType<Element>(doc.GetElementById("a"));
            var hostB = Assert.IsType<Element>(doc.GetElementById("b"));
            var shadowA = hostA.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            var shadowB = hostB.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });

            Assert.NotEqual(shadowA.ScopeIdentity, shadowB.ScopeIdentity);

            var boxA = doc.CreateElement("div");
            boxA.ClassList.Add("box");
            shadowA.AppendChild(boxA);
            var boxB = doc.CreateElement("div");
            boxB.ClassList.Add("box");
            shadowB.AppendChild(boxB);

            // Nested rules matter here: the per-registration copies are shallow, so
            // they share NestedRules. ApplyShadowScope recurses into them, so if two
            // shadow trees shared one parse template the second would stamp its root
            // over the first's nested rules. Flat CSS cannot detect that.
            const string shadowCss = ".box { color: green; & .inner { color: blue; } }";
            List<NewCss.CssStyleRule> Match(Element target, ShadowRoot scope) =>
                CssLoader.GetMatchedRules(target, new List<CssLoader.CssSource>
                {
                    new CssLoader.CssSource
                    {
                        CssText = shadowCss,
                        Origin = CssLoader.CssOrigin.Inline,
                        SourceOrder = 1,
                        BaseUri = baseUri,
                        ShadowScopeRoot = scope
                    }
                })
                .Where(m => m.Rule is NewCss.CssStyleRule)
                .Select(m => (NewCss.CssStyleRule)m.Rule)
                .ToList();

            var fromA = Match(boxA, shadowA);
            var fromB = Match(boxB, shadowB);

            Assert.Single(fromA);
            Assert.Single(fromB);

            // Distinct rule objects, each still scoped to its own shadow root.
            Assert.NotSame(fromA[0], fromB[0]);
            Assert.Same(shadowA, fromA[0].ShadowScopeRoot);
            Assert.Same(shadowB, fromB[0].ShadowScopeRoot);

            // And the nested rules must be scoped to their own tree as well.
            Assert.NotEmpty(fromA[0].NestedRules);
            Assert.NotEmpty(fromB[0].NestedRules);
            Assert.NotSame(fromA[0].NestedRules, fromB[0].NestedRules);
            Assert.Same(shadowA, fromA[0].NestedRules[0].ShadowScopeRoot);
            Assert.Same(shadowB, fromB[0].NestedRules[0].ShadowScopeRoot);
        }
    }
}
