using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Core
{
    // CSS Scoping 3.3 / 3.5: a shadow tree's stylesheet reaches its own host
    // through :host and the host's slotted light-DOM children through
    // ::slotted(), and nothing else outside the tree. Polymer components size
    // themselves with `:host { display: block }` and iron-pages hides every
    // unselected page with `:host > ::slotted(:not(.iron-selected))`.
    public sealed class CssShadowScopingTests
    {
        private static CssStyleRule Rule(string selector, ShadowRoot scope, string property, string value, bool important = false)
        {
            var rule = new CssStyleRule
            {
                Selector = new CssSelector { Raw = selector },
                ShadowScopeRoot = scope
            };
            rule.Declarations.Add(new CssDeclaration { Property = property, Value = value, IsImportant = important });
            return rule;
        }

        private static string Color(CascadeEngine engine, Element element)
            => engine.ComputeCascadedValues(element).TryGetValue("color", out var declaration) ? declaration.Value : null;

        [Fact]
        public void HostAndSlottedRulesReachAcrossTheShadowBoundary()
        {
            var baseUri = new System.Uri("https://scoping.test/");
            var doc = new HtmlParser(
                "<!doctype html><html><body>" +
                "<x-pages id='pages'><x-page id='selected' class='iron-selected'></x-page><x-page id='other'></x-page></x-pages>" +
                "<x-pages id='unrelated'></x-pages>" +
                "</body></html>",
                baseUri).Parse();

            var host = Assert.IsType<Element>(doc.GetElementById("pages"));
            var shadow = host.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            shadow.AppendChild(doc.CreateElement("slot"));
            var inner = doc.CreateElement("div");
            inner.ClassList.Add("inner");
            shadow.AppendChild(inner);

            var selected = Assert.IsType<Element>(doc.GetElementById("selected"));
            var other = Assert.IsType<Element>(doc.GetElementById("other"));
            var unrelatedHost = Assert.IsType<Element>(doc.GetElementById("unrelated"));
            unrelatedHost.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });

            var sheet = new CssStylesheet();
            sheet.Rules.Add(Rule(":host", shadow, "color", "blue"));
            sheet.Rules.Add(Rule(":host > ::slotted(:not(slot):not(.iron-selected))", shadow, "color", "gray", important: true));
            sheet.Rules.Add(Rule(".inner", shadow, "color", "red"));
            var styleSet = new StyleSet();
            styleSet.AddSheet(sheet, CssOrigin.Author, 1);
            var engine = new CascadeEngine(styleSet);

            // The host takes :host from its own tree; another host does not.
            Assert.Equal("blue", Color(engine, host));
            Assert.Null(Color(engine, unrelatedHost));

            // Slotted light-DOM children: the unselected one is hidden, the
            // selected one is untouched by the shadow sheet.
            Assert.Equal("gray", Color(engine, other));
            Assert.Null(Color(engine, selected));

            // Ordinary rules still apply only inside the tree.
            Assert.Equal("red", Color(engine, inner));
        }
    }
}
