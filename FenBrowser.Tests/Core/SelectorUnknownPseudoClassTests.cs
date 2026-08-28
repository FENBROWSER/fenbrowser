using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Dom.V2.Selectors;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class SelectorUnknownPseudoClassTests
{
    [Theory]
    [InlineData(":fist-child")]
    [InlineData("div:totally-unknown")]
    [InlineData("a:unknown(value)")]
    [InlineData("button:hover(value)")]
    public void ParseRejectsUnknownPseudoClasses(string selector)
    {
        var error = Assert.Throws<DomException>(() => SelectorParser.Parse(selector));

        Assert.Equal("SyntaxError", error.Name);
    }

    [Theory]
    [InlineData(":first-child")]
    [InlineData("input:disabled")]
    [InlineData("article:not(.hidden)")]
    public void ParseStillAcceptsImplementedPseudoClasses(string selector)
    {
        Assert.NotNull(SelectorParser.Parse(selector));
    }
}
