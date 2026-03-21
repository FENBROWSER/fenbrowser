using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class SelectorMatcherConformanceTests
    {
        [Fact]
        public void NthChild_WithOfSelector_MatchesFilteredSiblingSet()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <ul>
        <li id='a' class='keep'>a</li>
        <li id='b'>b</li>
        <li id='c' class='keep'>c</li>
        <li id='d' class='keep'>d</li>
    </ul>
</body></html>");

            var a = ById(doc, "a");
            var c = ById(doc, "c");
            var d = ById(doc, "d");

            Assert.True(SelectorMatcher.Matches(c, "li:nth-child(2 of .keep)"));
            Assert.False(SelectorMatcher.Matches(a, "li:nth-child(2 of .keep)"));
            Assert.False(SelectorMatcher.Matches(d, "li:nth-child(2 of .keep)"));
            Assert.True(SelectorMatcher.Matches(d, "li:nth-last-child(1 of .keep)"));
        }

        [Fact]
        public void AttributeSelectors_SupportCaseFlagsAndBracketInQuotedValue()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='target' data-code='AbC' data-note='A ] B'></div>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(SelectorMatcher.Matches(target, "[data-code='abc' i]"));
            Assert.False(SelectorMatcher.Matches(target, "[data-code='abc' s]"));
            Assert.True(SelectorMatcher.Matches(target, "[data-code='AbC' s]"));
            Assert.True(SelectorMatcher.Matches(target, "[data-note='a ] b' i]"));
        }

        [Fact]
        public void EmptyPseudoClass_IgnoresComments_ButNotWhitespaceText()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='comment-only'><!--comment--></div>
    <div id='whitespace'> </div>
    <div id='empty'></div>
</body></html>");

            var commentOnly = ById(doc, "comment-only");
            var whitespace = ById(doc, "whitespace");
            var empty = ById(doc, "empty");

            Assert.True(SelectorMatcher.Matches(commentOnly, ":empty"));
            Assert.False(SelectorMatcher.Matches(whitespace, ":empty"));
            Assert.True(SelectorMatcher.Matches(empty, ":empty"));
        }

        [Fact]
        public void HasPseudoClass_SupportsRelativeCombinators()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='child-host'><img alt='x' /></div>
    <div id='child-miss'><span></span></div>

    <div id='adjacent-host'></div><div class='next-hit'></div>
    <div id='sibling-host'></div><div></div><div class='later-hit'></div>

    <section id='nested-host'>
        <article class='card'><span class='leaf'></span></article>
    </section>
    <section id='nested-miss'>
        <article class='card'></article>
    </section>
</body></html>");

            var childHost = ById(doc, "child-host");
            var childMiss = ById(doc, "child-miss");
            var adjacentHost = ById(doc, "adjacent-host");
            var siblingHost = ById(doc, "sibling-host");
            var nestedHost = ById(doc, "nested-host");
            var nestedMiss = ById(doc, "nested-miss");

            Assert.True(SelectorMatcher.Matches(childHost, "div:has(> img)"));
            Assert.False(SelectorMatcher.Matches(childMiss, "div:has(> img)"));
            Assert.True(SelectorMatcher.Matches(adjacentHost, "div:has(+ .next-hit)"));
            Assert.True(SelectorMatcher.Matches(siblingHost, "div:has(~ .later-hit)"));
            Assert.True(SelectorMatcher.Matches(nestedHost, "section:has(> article .leaf)"));
            Assert.False(SelectorMatcher.Matches(nestedMiss, "section:has(> article .leaf)"));
        }

        [Fact]
        public async Task CascadeAppliesNthChildOfSelectorRules()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        li { color: black; }
        li.keep { color: blue; }
        li:nth-child(2 of .keep) { color: red; }
    </style>
</head>
<body>
    <ul>
        <li id='x1' class='keep'>x1</li>
        <li id='x2'>x2</li>
        <li id='x3' class='keep'>x3</li>
        <li id='x4' class='keep'>x4</li>
    </ul>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var x3 = ById(doc, "x3");
            var x4 = ById(doc, "x4");

            Assert.Equal("red", computed[x3].Map["color"]);
            Assert.Equal("blue", computed[x4].Map["color"]);
        }

        [Fact]
        public void CompoundSelector_DoesNotMatchWhenRequiredClassIsMissing()
        {
            var doc = Parse(@"
<!doctype html>
<html class='wp25eastereggs-enable-clientpref-1'><body>
    <div id='notice' class='wp25eastereggs-sitenotice'></div>
</body></html>");

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            Assert.False(
                SelectorMatcher.Matches(
                    root,
                    "html.wp25eastereggs-companion-enabled.wp25eastereggs-enable-clientpref-1:not(.ve-active)"));
        }

        [Fact]
        public async Task Cascade_KeepsWikipediaEasterEggNoticeHiddenWithoutCompanionClass()
        {
            const string html = @"
<!doctype html>
<html class='wp25eastereggs-enable-clientpref-1'>
<head>
    <style>
        .wp25eastereggs-sitenotice,
        .wp25eastereggs-sitenotice-learn-more-link,
        .wp25eastereggs-vector-sitenotice-landmark,
        .wp25eastereggs-video-container { display: none; }

        @media screen and (min-width: 1120px) {
            html.wp25eastereggs-companion-enabled.wp25eastereggs-enable-clientpref-1:not(.ve-active) .wp25eastereggs-vector-sitenotice-landmark {
                display: block;
                width: 12.25rem;
                height: 12.25rem;
            }
        }
    </style>
</head>
<body>
    <div class='vector-sitenotice-container'>
        <div id='siteNotice'>
            <div class='wp25eastereggs-sitenotice'>
                <div id='landmark' class='wp25eastereggs-vector-sitenotice-landmark'></div>
            </div>
        </div>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://en.wikipedia.org"), null);

            var notice = ById(doc, "siteNotice").Descendants().OfType<Element>()
                .First(e => e.ClassName?.Contains("wp25eastereggs-sitenotice", StringComparison.Ordinal) == true);
            var landmark = ById(doc, "landmark");

            Assert.Equal("none", computed[notice].Display);
            Assert.Equal("none", computed[landmark].Display);
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
