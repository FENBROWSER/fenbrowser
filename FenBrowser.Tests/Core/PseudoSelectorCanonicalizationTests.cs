using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class PseudoSelectorCanonicalizationTests
{
    [Fact]
    public void ParseSelectorList_CanonicalizesFunctionalPseudoClassOnce()
    {
        var element = new Element("button");
        element.SetAttribute("class", "primary");

        var parsed = SelectorMatcher.ParseSelectorList("button:IS(.primary, .ghost)");

        var pseudo = Assert.Single(Assert.Single(parsed).Segments[0].PseudoClasses);
        Assert.Equal("is", pseudo.Name);
        Assert.Equal(2, pseudo.ParsedArgs.Count);
        Assert.Equal(0, parsed[0].Specificity.A);
        Assert.Equal(1, parsed[0].Specificity.B);
        Assert.Equal(1, parsed[0].Specificity.C);
        Assert.True(SelectorMatcher.MatchesChain(element, parsed[0]));
    }

    [Theory]
    [InlineData("p:BEFORE", "before")]
    [InlineData("p::SLOTTED(*)", "slotted")]
    public void ParseSelectorList_CanonicalizesPseudoElements(string selector, string expectedName)
    {
        var parsed = SelectorMatcher.ParseSelectorList(selector);

        var pseudo = Assert.Single(Assert.Single(parsed).Segments[0].PseudoElements);
        Assert.Equal(expectedName, pseudo.Name);
    }

    [Fact]
    public void PseudoSelector_NameMaintainsCanonicalModelInvariant()
    {
        var pseudo = new PseudoSelector { Name = "FIRST-CHILD" };

        Assert.Equal("first-child", pseudo.Name);
    }
}
