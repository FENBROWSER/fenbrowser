using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>Selectors 4 §3.1 and §17: when one invalid selector makes a whole list invalid.</summary>
public sealed class SelectorListValidityTests
{
    [Theory]
    [InlineData("video, :picture-in-picture(*)")]
    [InlineData("a, :hover()")]
    [InlineData("a, .")]
    [InlineData("a:not(b, :checked(1))")]
    public void OneInvalidSelectorInvalidatesTheList(string selector)
    {
        Assert.Empty(SelectorMatcher.ParseSelectorList(selector));
    }

    [Fact]
    public void IsAndWhereForgiveTheirInvalidArguments()
    {
        var parsed = SelectorMatcher.ParseSelectorList("a:is(b, :hover(1)), c:where(d, :empty(1))");

        Assert.Equal(2, parsed.Count);
        Assert.Single(parsed[0].Segments[0].PseudoClasses[0].ParsedArgs);
        Assert.Single(parsed[1].Segments[0].PseudoClasses[0].ParsedArgs);
    }

    [Fact]
    public void AValidListKeepsEverySelector()
    {
        Assert.Equal(3, SelectorMatcher.ParseSelectorList("video, :picture-in-picture, a:nth-child(2 of .x)").Count);
    }
}
