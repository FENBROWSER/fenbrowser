using System;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssSyntaxParserTests
    {
        [Fact]
        public void ParseStylesheet_FontFaceRule_IsParsedWithDescriptors()
        {
            const string css = @"
@font-face {
  font-family: ""MyFont"";
  src: url(""myfont.woff2"") format(""woff2"");
  font-weight: 400;
}
body { color: red; }";

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var fontFace = Assert.IsType<CssFontFaceRule>(sheet.Rules.First());
            Assert.Contains(fontFace.Declarations, d => d.Property == "font-family" && d.Value.Contains("MyFont", StringComparison.Ordinal));
            Assert.Contains(fontFace.Declarations, d => d.Property == "src" && d.Value.Contains("myfont.woff2", StringComparison.Ordinal));
            Assert.Contains(fontFace.Declarations, d => d.Property == "font-weight" && d.Value == "400");

            Assert.Contains(sheet.Rules, r => r is CssStyleRule);
        }

        [Fact]
        public void ParseStylesheet_MalformedDeclaration_RecoversAndParsesFollowingDeclaration()
        {
            const string css = "div { color red; background: blue; }";

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var styleRule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
            Assert.DoesNotContain(styleRule.Declarations, d => d.Property == "color");
            Assert.Contains(styleRule.Declarations, d => d.Property == "background" && d.Value == "blue");
        }

        [Fact]
        public void ParseStylesheet_CustomProperty_PreservesCase()
        {
            const string css = "div { --MyBrandColor: red; color: var(--MyBrandColor); }";

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var styleRule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
            Assert.Contains(styleRule.Declarations, d => d.Property == "--MyBrandColor" && d.Value == "red");
            Assert.Contains(styleRule.Declarations, d => d.Property == "color" && d.Value == "var(--MyBrandColor)");
        }

        [Fact]
        public void ParseStylesheet_StopsAtConfiguredRuleLimit()
        {
            var css = string.Join(Environment.NewLine, Enumerable.Range(0, 40).Select(i => $".c{i} {{ color: red; }}"));

            var parser = new CssSyntaxParser(new CssTokenizer(css))
            {
                MaxRules = 7
            };

            var sheet = parser.ParseStylesheet();

            Assert.Equal(7, sheet.Rules.Count);
        }

        [Fact]
        public void ParseStylesheet_StopsDeclarationBlockAtConfiguredLimit()
        {
            var decls = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"p{i}: {i}px;"));
            var css = $"div {{ {decls} }}";

            var parser = new CssSyntaxParser(new CssTokenizer(css))
            {
                MaxDeclarationsPerBlock = 5
            };

            var sheet = parser.ParseStylesheet();
            var styleRule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));

            Assert.Equal(5, styleRule.Declarations.Count);
            Assert.Equal("p0", styleRule.Declarations[0].Property);
            Assert.Equal("p4", styleRule.Declarations[4].Property);
        }
    }
}
