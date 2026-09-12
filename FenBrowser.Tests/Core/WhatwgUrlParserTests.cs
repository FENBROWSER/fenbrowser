using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class WhatwgUrlParserTests
{
    // WHATWG URL 4.4 scheme state: an input consisting only of scheme characters
    // reaches EOF inside the scheme state and must start over as a relative
    // reference against the base, not end the parse.
    [Theory]
    [InlineData("b", "http://a", "http://a/b")]
    [InlineData("b", "https://wpt.fyi/results/", "https://wpt.fyi/results/b")]
    [InlineData("components", "https://wpt.fyi/results/x", "https://wpt.fyi/results/components")]
    [InlineData("b1", "http://a/c/d", "http://a/c/b1")]
    public void SchemeLikeRelativeReferenceResolvesAgainstBase(string input, string baseUrl, string expected)
    {
        var url = WhatwgUrl.Parse(input, WhatwgUrl.Parse(baseUrl));

        Assert.NotNull(url);
        Assert.Equal(expected, url.Href);
    }

    [Theory]
    [InlineData("/b", "https://wpt.fyi/results/", "https://wpt.fyi/b")]
    [InlineData("../x?y#z", "https://wpt.fyi/results/a/b", "https://wpt.fyi/results/x?y#z")]
    [InlineData("//h/p", "https://a/", "https://h/p")]
    [InlineData("?q", "https://a/p", "https://a/p?q")]
    [InlineData("#f", "https://a/p", "https://a/p#f")]
    [InlineData("[[x]]", "https://wpt.fyi/results/", "https://wpt.fyi/results/[[x]]")]
    public void RelativeReferencesResolveAgainstBase(string input, string baseUrl, string expected)
    {
        var url = WhatwgUrl.Parse(input, WhatwgUrl.Parse(baseUrl));

        Assert.NotNull(url);
        Assert.Equal(expected, url.Href);
    }

    [Theory]
    [InlineData("::")]
    [InlineData("http://")]
    [InlineData("b")]
    public void InvalidInputsFailToParse(string input)
    {
        Assert.Null(WhatwgUrl.Parse(input));
    }
}
