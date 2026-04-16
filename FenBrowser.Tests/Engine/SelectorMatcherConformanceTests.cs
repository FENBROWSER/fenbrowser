using System;
using System.Linq;
using System.Reflection;
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
        public void AttributeSelectors_UnescapeEscapedWhitespaceInUnquotedValue()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <address id='target' class='second two'></address>
</body></html>");

            var target = ById(doc, "target");

            Assert.True(SelectorMatcher.Matches(target, @"[class=second\ two]"));
            Assert.True(SelectorMatcher.Matches(target, @"[class=second\ two][class=""second two""]"));
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
        public void DescendantSelector_WithTagAndIdAncestor_DoesNotFastRejectValidMatch()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <nav id='site'>
        <div id='top-logo-and-name'></div>
    </nav>
</body></html>");

            var target = ById(doc, "top-logo-and-name");

            Assert.True(SelectorMatcher.Matches(target, "nav#site #top-logo-and-name"));
            Assert.True(SelectorMatcher.Matches(target, "#site #top-logo-and-name"));
        }

        [Fact]
        public async Task EscapedSpaceIdSelector_MatchesAndAppliesCascade()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #\  { display: none; }
    </style>
</head>
<body>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var hidden = doc.CreateElement("div");
            hidden.Id = " ";
            hidden.AppendChild(doc.CreateTextNode("FAIL"));
            doc.Body.AppendChild(hidden);

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            Assert.True(SelectorMatcher.Matches(hidden, @"#\ "));
            Assert.Equal("none", computed[hidden].Display);
        }

        [Fact]
        public void EscapedSpaceIdDescendantSelector_DoesNotMatchWithoutEscapedAncestor()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <p id='result'><span id='score'>66</span></p>
    <div id=' '>FAIL</div>
</body></html>");

            var result = ById(doc, "result");
            var score = ById(doc, "score");

            Assert.False(SelectorMatcher.Matches(result, @"#\  #result"));
            Assert.False(SelectorMatcher.Matches(score, @"#\  #score"));
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
        public void EnabledPseudoClass_DoesNotMatchNonFormElements()
        {
            var doc = Parse(@"
<!doctype html>
<html><body>
    <div id='plain'></div>
    <input id='control' />
</body></html>");

            var plain = ById(doc, "plain");
            var control = ById(doc, "control");

            Assert.False(SelectorMatcher.Matches(plain, ":enabled"));
            Assert.True(SelectorMatcher.Matches(control, ":enabled"));
        }

        [Fact]
        public void MalformedCompoundSelector_IsRejected()
        {
            var doc = Parse(@"
<!doctype html>
<html class='test'><body></body></html>");

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            Assert.False(SelectorMatcher.Matches(root, "html*.test"));
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

        [Fact]
        public async Task Cascade_AppliesFirstChildPseudoClassRule_ToLeadingHeading()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        html { font-size: 20px; }
        h1 { font-size: 2em; }
        h1:first-child { font-size: 5em; }
        #result { font-size: 5em; }
    </style>
</head>
<body>
    <h1 id='title'>Acid3</h1>
    <div id='result'>64</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid3.acidtests.org/"), null);

            var title = ById(doc, "title");
            var result = ById(doc, "result");

            Assert.Equal("5em", computed[title].Map["font-size"]);
            Assert.InRange(computed[title].FontSize ?? 0d, 99.999d, 100.001d);
            Assert.Equal("5em", computed[result].Map["font-size"]);
            Assert.InRange(computed[result].FontSize ?? 0d, 99.999d, 100.001d);
        }

        [Fact]
        public async Task Cascade_Acid3HeaderRules_SurviveDataUrlBodyDeclaration()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style type='text/css'>
        * { margin: 0; border: 1px blue; padding: 0; border-spacing: 0; font: inherit; line-height: 1.2; color: inherit; background: transparent; }
        html { font: 20px Arial, sans-serif; border: 2cm solid gray; width: 32em; margin: 1em; }
        :root { background: silver; color: black; border-width: 0 0.2em 0.2em 0; }
        body { padding: 2em 2em 0; background: url(data:image/gif;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAIAAAAC64paAAAABGdBTUEAAK/INwWK6QAAAAlwSFlzAAAASAAAAEgARslrPgAAABtJREFUOMtj/M9APmCiQO+o5lHNo5pHNVNBMwAinAEnIWw89gAAACJ6VFh0U29mdHdhcmUAAHjac0zJT0pV8MxNTE8NSk1MqQQAL5wF1K4MqU0AAAAASUVORK5CYII=) no-repeat 99.8392283% 1px white; border: solid 1px black; margin: -0.2em 0 0 -0.2em; }
        h1:first-child { cursor: help; font-size: 5em; font-weight: bolder; margin-bottom: -0.4em; text-shadow: rgba(192, 192, 192, 1.0) 3px 3px; }
        #result { font-weight: bolder; width: 5.68em; text-align: right; }
        #result { font-size: 5em; margin: -2.19em 0 0; }
    </style>
</head>
<body>
    <h1 id='title'>Acid3</h1>
    <div id='result'>64</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://acid3.acidtests.org/"), null);

            var title = ById(doc, "title");
            var result = ById(doc, "result");

            Assert.Equal("5em", computed[title].Map["font-size"]);
            Assert.Equal("5em", computed[result].Map["font-size"]);
        }

        [Fact]
        public void ParseRules_Acid3HeaderRules_AreNotDroppedAfterDataUrlBodyDeclaration()
        {
            const string css = @"
                * { margin: 0; border: 1px blue; padding: 0; border-spacing: 0; font: inherit; line-height: 1.2; color: inherit; background: transparent; }
                html { font: 20px Arial, sans-serif; border: 2cm solid gray; width: 32em; margin: 1em; }
                :root { background: silver; color: black; border-width: 0 0.2em 0.2em 0; }
                body { padding: 2em 2em 0; background: url(data:image/gif;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAIAAAAC64paAAAABGdBTUEAAK/INwWK6QAAAAlwSFlzAAAASAAAAEgARslrPgAAABtJREFUOMtj/M9APmCiQO+o5lHNo5pHNVNBMwAinAEnIWw89gAAACJ6VFh0U29mdHdhcmUAAHjac0zJT0pV8MxNTE8NSk1MqQQAL5wF1K4MqU0AAAAASUVORK5CYII=) no-repeat 99.8392283% 1px white; border: solid 1px black; margin: -0.2em 0 0 -0.2em; }
                h1:first-child { cursor: help; font-size: 5em; font-weight: bolder; margin-bottom: -0.4em; text-shadow: rgba(192, 192, 192, 1.0) 3px 3px; }
                #result { font-weight: bolder; width: 5.68em; text-align: right; }
                #result { font-size: 5em; margin: -2.19em 0 0; }";

            var parseRules = typeof(CssLoader).GetMethod("ParseRules", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(parseRules);

            var rules = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                parseRules!.Invoke(null, new object[] { css, 0, new Uri("https://acid3.acidtests.org/"), null, null, null, CssOrigin.Author }));

            var styleSelectors = rules.Cast<CssRule>()
                .OfType<CssStyleRule>()
                .Select(rule => rule.Selector?.Raw)
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .ToArray();

            Assert.Contains("h1:first-child", styleSelectors);
            Assert.Equal(2, styleSelectors.Count(raw => raw == "#result"));
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
