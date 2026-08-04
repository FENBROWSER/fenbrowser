using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerNameNullRecoveryTests
{
    [Theory]
    [InlineData("<a\0b>", "a\uFFFDb")]
    [InlineData("<ab\0>", "ab\uFFFD")]
    [InlineData("<a\0\0b>", "a\uFFFD\uFFFDb")]
    public void TagName_ReplacesEveryNullCodePoint(string html, string expectedName)
    {
        StartTagToken token = TokenizeSingleStartTag(html);

        Assert.Equal(expectedName, token.TagName);
        Assert.DoesNotContain('\0', token.TagName);
    }

    [Theory]
    [InlineData("<div \0=x>", "\uFFFD")]
    [InlineData("<div a\0=x>", "a\uFFFD")]
    [InlineData("<div a\0\0b=x>", "a\uFFFD\uFFFDb")]
    public void AttributeName_ReplacesEveryNullCodePoint(string html, string expectedName)
    {
        StartTagToken token = TokenizeSingleStartTag(html);
        HtmlAttribute attribute = Assert.Single(token.Attributes);

        Assert.Equal(expectedName, attribute.Name);
        Assert.Equal("x", attribute.Value);
        Assert.DoesNotContain('\0', attribute.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameNullRecovery_IsIdenticalWithAndWithoutTokenPool(bool useTokenPool)
    {
        const string html = "<x\0y a\0b=value>";
        var tokenizer = useTokenPool
            ? new HtmlTokenizer(html, new HtmlTokenPool())
            : new HtmlTokenizer(html);
        StartTagToken token = Assert.Single(tokenizer.Tokenize().OfType<StartTagToken>());
        HtmlAttribute attribute = Assert.Single(token.Attributes);

        Assert.Equal("x\uFFFDy", token.TagName);
        Assert.Equal("a\uFFFDb", attribute.Name);
        Assert.Equal("value", attribute.Value);
    }

    [Fact]
    public void ReplacementNormalizedDuplicateAttribute_KeepsFirstValue()
    {
        StartTagToken token = TokenizeSingleStartTag(
            "<div A\0B=first a\uFFFDb=second>");
        HtmlAttribute attribute = Assert.Single(token.Attributes);

        Assert.Equal("a\uFFFDb", attribute.Name);
        Assert.Equal("first", attribute.Value);
    }

    [Fact]
    public void ReplacementNormalizedEndTag_ClosesElementAndParsingContinues()
    {
        Document document = HtmlParser.ParseDocument(
            "<!doctype html><body><x\0y>content</x\0y><p id='tail'>tail</p>");
        Element recovered = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "X\uFFFDY", StringComparison.OrdinalIgnoreCase));
        Element tail = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.Id, "tail", StringComparison.Ordinal));

        Assert.Equal("content", recovered.TextContent);
        Assert.Equal("tail", tail.TextContent);
        Assert.Same(recovered.ParentNode, tail.ParentNode);
    }

    private static StartTagToken TokenizeSingleStartTag(string html)
    {
        return Assert.Single(new HtmlTokenizer(html).Tokenize().OfType<StartTagToken>());
    }
}
