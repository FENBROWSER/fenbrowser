using System.Linq;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// CSS Syntax 3 "consume a qualified rule" with nested set: inside a declaration block
    /// a <semicolon-token> or the block's <}-token> ends a would-be nested rule as a parse
    /// error that returns nothing. IE's "*property:value" star hack starts like a nested
    /// rule (a universal selector); it must cost one declaration, not the rest of the
    /// stylesheet. YouTube's player CSS lost three quarters of its rules to one.
    /// </summary>
    public class CssInvalidNestedRuleRecoveryTests
    {
        [Fact]
        public void StarHackDeclaration_IsDropped_AndParsingGoesOn()
        {
            const string css = ".a{display:inline-block;*overflow:visible;border-radius:2px}.b{color:blue}.c{position:absolute}";

            var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
            var rules = sheet.Rules.OfType<CssStyleRule>().ToList();

            Assert.Equal(new[] { ".a", ".b", ".c" }, rules.Select(r => r.Selector.Raw));
            Assert.Equal(new[] { "display", "border-radius" }, rules[0].Declarations.Select(d => d.Property));
            Assert.Empty(rules[0].NestedRules);
            Assert.Contains(rules[2].Declarations, d => d.Property == "position" && d.Value == "absolute");
        }

        [Fact]
        public void InvalidNestedRuleBeforeTheClosingBrace_EndsWithItsBlock()
        {
            const string css = ".a{color:red;*zoom:1}.b{color:blue}";

            var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
            var rules = sheet.Rules.OfType<CssStyleRule>().ToList();

            Assert.Equal(new[] { ".a", ".b" }, rules.Select(r => r.Selector.Raw));
            Assert.Equal(new[] { "color" }, rules[0].Declarations.Select(d => d.Property));
        }

        [Fact]
        public void TenThousandMutations_OfStarHacksAndNesting_NeitherCrashNorHang()
        {
            const string seed = ".a{display:inline-block;*overflow:visible;*zoom:1}.b{color:red;& .c{color:blue}* .d{margin:0}}" +
                                "@media (min-width:600px){.e{*display:inline;top:50%}}.f{_height:1px;position:absolute}";

            var result = FenBrowser.FenEngine.Fuzzing.CssFuzzer.FuzzCssParser(
                seed,
                css => new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet());

            Assert.Equal(10000, result.Iterations);
            Assert.Equal(0, result.Crashes);
            Assert.Equal(0, result.Hangs);
        }

        [Fact]
        public void RealNestedRule_StillParses()
        {
            const string css = ".card{color:red;* .x{color:green}}.next{color:blue}";

            var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
            var rules = sheet.Rules.OfType<CssStyleRule>().ToList();

            Assert.Equal(new[] { ".card", ".next" }, rules.Select(r => r.Selector.Raw));
            var nested = Assert.IsType<CssStyleRule>(Assert.Single(rules[0].NestedRules));
            Assert.Contains(nested.Declarations, d => d.Property == "color" && d.Value == "green");
        }
    }
}
