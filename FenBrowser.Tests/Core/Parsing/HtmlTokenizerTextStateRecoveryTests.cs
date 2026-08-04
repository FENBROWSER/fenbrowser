using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerTextStateRecoveryTests
{
    [Theory]
    [InlineData(HtmlTokenizer.TokenizerState.RcData)]
    [InlineData(HtmlTokenizer.TokenizerState.RawText)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptData)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataEscaped)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataEscapedDash)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataEscapedDashDash)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataDoubleEscaped)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataDoubleEscapedDash)]
    [InlineData(HtmlTokenizer.TokenizerState.ScriptDataDoubleEscapedDashDash)]
    [InlineData(HtmlTokenizer.TokenizerState.PlainText)]
    public void TextLikeState_ReplacesEveryNullCodePoint(HtmlTokenizer.TokenizerState state)
    {
        var tokenizer = new HtmlTokenizer("\0a\0\0b\0");
        tokenizer.SetState(state);

        string text = string.Concat(
            tokenizer.Tokenize().OfType<CharacterToken>().Select(token => token.Data));

        Assert.Equal("\uFFFDa\uFFFD\uFFFDb\uFFFD", text);
        Assert.DoesNotContain('\0', text);
    }

    [Theory]
    [InlineData("textarea")]
    [InlineData("style")]
    [InlineData("script")]
    public void DocumentTextElement_ReplacesNullAndRecognizesItsEndTag(string tagName)
    {
        Document document = HtmlParser.ParseDocument(
            $"<!doctype html><body><{tagName}>a\0b</{tagName}><p id='tail'>tail</p>");
        Element textElement = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, tagName, StringComparison.OrdinalIgnoreCase));
        Element tail = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.Id, "tail", StringComparison.Ordinal));

        Assert.Equal("a\uFFFDb", textElement.TextContent);
        Assert.Equal("tail", tail.TextContent);
    }

    [Fact]
    public void PlaintextElement_TreatsRemainingMarkupAsReplacementNormalizedText()
    {
        Document document = HtmlParser.ParseDocument(
            "<!doctype html><body><plaintext>a\0b<span>markup</span>");
        Element plaintext = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "PLAINTEXT", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("a\uFFFDb<span>markup</span>", plaintext.TextContent);
        Assert.DoesNotContain(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "SPAN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaintextEndTag_RemainsLiteralThroughEndOfFile()
    {
        Document document = HtmlParser.ParseDocument(
            "<!doctype html><body><plaintext>one</plaintext><div>two</div>");
        Element plaintext = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "PLAINTEXT", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("one</plaintext><div>two</div>", plaintext.TextContent);
        Assert.DoesNotContain(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "DIV", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaintextStartTag_ClosesOpenParagraphBeforeConsumingText()
    {
        Document document = HtmlParser.ParseDocument(
            "<!doctype html><body><p id='before'>before<plaintext>after");
        Element paragraph = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.Id, "before", StringComparison.Ordinal));
        Element plaintext = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "PLAINTEXT", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("before", paragraph.TextContent);
        Assert.Equal("after", plaintext.TextContent);
        Assert.Same(paragraph.ParentNode, plaintext.ParentNode);
    }

    [Theory]
    [InlineData("<!--a\0b-->", "<!--a\uFFFDb-->")]
    [InlineData("<!--<script>a\0b</script>-->", "<!--<script>a\uFFFDb</script>-->")]
    public void ScriptEscapedStates_ReplaceNullAndReturnAtOuterEndTag(
        string scriptSource,
        string expectedText)
    {
        Document document = HtmlParser.ParseDocument(
            $"<!doctype html><body><script>{scriptSource}</script><p id='tail'>tail</p>");
        Element script = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase));
        Element tail = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.Id, "tail", StringComparison.Ordinal));

        Assert.Equal(expectedText, script.TextContent);
        Assert.Equal("tail", tail.TextContent);
    }
}
