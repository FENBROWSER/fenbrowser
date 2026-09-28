using System;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // CSS Counter Styles 3 §3: author @counter-style rules, which belong to their
    // document.
    public class CounterStyleRegistryTests
    {
        private static Document NewDocument() =>
            new HtmlParser("<!doctype html><html><body></body></html>", new Uri("https://counters.test/")).Parse();

        private static Document Define(string name, string body)
        {
            var document = NewDocument();
            CounterStyleRegistry.Register(document, name, body);
            return document;
        }

        [Fact]
        public void ExtendsInheritsTheSymbolsAndTheSuffix()
        {
            var document = Define("my-disc", "system: extends disc;");

            Assert.Equal("•", ListMarkerFormatter.FormatCounterValue(3, "my-disc", document));
            Assert.Equal(ListMarkerFormatter.Format(3, "disc"), ListMarkerFormatter.Format(3, "my-disc", document));
        }

        [Fact]
        public void ExtendsOverridesPrefixAndSuffix()
        {
            var document = Define("paren", "system: extends decimal; prefix: '('; suffix: ') '");

            Assert.Equal("(4) ", ListMarkerFormatter.Format(4, "paren", document));
        }

        [Theory]
        [InlineData("system: cyclic; symbols: A B C", 4, "A")]
        [InlineData("system: fixed 2; symbols: x y", 3, "y")]
        [InlineData("system: fixed 2; symbols: x y", 5, "5")]
        [InlineData("system: numeric; symbols: '0' '1'", 5, "101")]
        [InlineData("system: numeric; symbols: '0' '1'", -5, "-101")]
        [InlineData("system: numeric; symbols: '0' '1'; negative: '(' ')'", -2, "(10)")]
        [InlineData("system: numeric; symbols: '0' '1'; pad: 4 '0'", 2, "0010")]
        [InlineData("system: alphabetic; symbols: a b", 3, "aa")]
        [InlineData("system: alphabetic; symbols: a b", 0, "0")]
        [InlineData("system: symbolic; symbols: '*' '\\2020'", 3, "**")]
        [InlineData("system: additive; additive-symbols: 10 X, 5 V, 1 I", 17, "XVII")]
        [InlineData("system: additive; additive-symbols: 10 X, 5 V, 1 I", 0, "0")]
        [InlineData("system: fixed; symbols: a; fallback: upper-roman", 4, "IV")]
        public void AlgorithmsFollowTheSystem(string body, int value, string expected)
        {
            var document = Define("s", body);

            Assert.Equal(expected, ListMarkerFormatter.FormatCounterValue(value, "s", document));
        }

        [Fact]
        public void CustomStylesDefaultToTheDotSuffix()
        {
            var document = Define("letters", "system: alphabetic; symbols: p q");

            Assert.Equal("q. ", ListMarkerFormatter.Format(2, "letters", document));
        }

        [Fact]
        public void AnExtendsCycleFallsBackToDecimal()
        {
            var document = NewDocument();
            CounterStyleRegistry.Register(document, "cycle-a", "system: extends cycle-b");
            CounterStyleRegistry.Register(document, "cycle-b", "system: extends cycle-a");

            Assert.Equal("7", ListMarkerFormatter.FormatCounterValue(7, "cycle-a", document));
        }

        // §3.1: a rule whose symbols do not suit its system, or that names a
        // non-overridable predefined style, is invalid and leaves its name undefined.
        [Theory]
        [InlineData("a", "system: cyclic; suffix: ':'")]
        [InlineData("a", "system: alphabetic; symbols: x")]
        [InlineData("a", "system: numeric; symbols: x")]
        [InlineData("a", "system: additive")]
        [InlineData("a", "system: extends decimal; symbols: x")]
        [InlineData("a", "system: alphabetic; symbols: x inherit")]
        [InlineData("a", "system: bogus; symbols: x")]
        [InlineData("disc", "system: cyclic; symbols: x")]
        public void InvalidRulesAreDropped(string name, string body)
        {
            var document = Define(name, body);

            Assert.Equal(ListMarkerFormatter.Format(2, name == "a" ? "decimal" : name), ListMarkerFormatter.Format(2, name, document));
        }

        [Fact]
        public void PredefinedStylesCanBeRedefinedPerDocument()
        {
            var document = Define("lower-roman", "system: cyclic; symbols: X");

            Assert.Equal("X", ListMarkerFormatter.FormatCounterValue(3, "lower-roman", document));
            Assert.Equal("iii", ListMarkerFormatter.FormatCounterValue(3, "lower-roman", NewDocument()));
        }

        // §4 symbols(): an anonymous style, symbolic by default, with a space suffix.
        [Theory]
        [InlineData("symbols('*')", 3, "*** ")]
        [InlineData("symbols(cyclic '*' '\\2020')", 3, "* ")]
        [InlineData("symbols(numeric '0' '1')", 2, "10 ")]
        [InlineData("symbols(fixed 'x')", 2, "2 ")]
        [InlineData("symbols(alphabetic '*')", 2, "2. ")]
        public void SymbolsFunctionDefinesAnAnonymousStyle(string type, int value, string expected)
        {
            Assert.Equal(expected, ListMarkerFormatter.Format(value, type, NewDocument()));
        }

        // Rules reach the registry through the document's cascade.
        [Fact]
        public async Task StylesheetRulesAreRegisteredForTheirDocument()
        {
            var uri = new Uri("https://counters.test/");
            var document = new HtmlParser(
                "<!doctype html><html><head><style>@counter-style stars { system: cyclic; symbols: '*'; }</style></head><body></body></html>",
                uri).Parse();

            await CssLoader.ComputeAsync(document.DocumentElement, uri, null);

            Assert.Equal("*. ", ListMarkerFormatter.Format(1, "stars", document));
            Assert.Equal("1. ", ListMarkerFormatter.Format(1, "stars", NewDocument()));
        }
    }
}
