using System.Linq;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssNestingTests
    {
        [Fact]
        public void ParseStylesheet_Nesting_ResolvesAmpersandAndImplicitSelectors()
        {
            const string css = """
                .card {
                  color: red;
                  & .title { color: blue; }
                  .body { color: green; }
                }
                """;

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var parent = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
            Assert.Equal(".card", parent.Selector.Raw);
            Assert.Equal(2, parent.NestedRules.Count);

            var nestedExplicit = Assert.IsType<CssStyleRule>(parent.NestedRules[0]);
            var nestedImplicit = Assert.IsType<CssStyleRule>(parent.NestedRules[1]);

            Assert.Equal(".card .title", nestedExplicit.Selector.Raw);
            Assert.Equal(".card .body", nestedImplicit.Selector.Raw);
            Assert.Contains(nestedExplicit.Declarations, d => d.Property == "color" && d.Value == "blue");
            Assert.Contains(nestedImplicit.Declarations, d => d.Property == "color" && d.Value == "green");
        }

        [Fact]
        public void ParseStylesheet_Nesting_ResolvesPseudoClassAndCombinatorChains()
        {
            const string css = """
                .item {
                  &:hover > .icon { opacity: 0.8; }
                }
                """;

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var parent = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
            var nested = Assert.IsType<CssStyleRule>(Assert.Single(parent.NestedRules));

            Assert.Equal(".item:hover > .icon", nested.Selector.Raw);
            Assert.Contains(nested.Declarations, d => d.Property == "opacity" && d.Value == "0.8");
        }

        [Fact]
        public void ParseStylesheet_NestedMediaRule_KeepsParentSelectorScope()
        {
            const string css = """
                .card {
                  @media (min-width: 600px) {
                    & .title { color: purple; }
                  }
                }
                """;

            var parser = new CssSyntaxParser(new CssTokenizer(css));
            var sheet = parser.ParseStylesheet();

            var parent = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
            var media = Assert.IsType<CssMediaRule>(Assert.Single(parent.NestedRules));

            Assert.Equal("(min-width: 600px)", media.Condition);
            var nested = Assert.IsType<CssStyleRule>(Assert.Single(media.Rules));
            Assert.Equal(".card .title", nested.Selector.Raw);
            Assert.Contains(nested.Declarations, d => d.Property == "color" && d.Value == "purple");
        }
    }
}
