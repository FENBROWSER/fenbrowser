using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlLeadingBomTests
{
    [Fact]
    public void DocumentParserStripsOnlyAnInitialBomBeforeDoctype()
    {
        var document = HtmlParser.ParseDocument("\uFEFF<!doctype html><html><body>ok</body></html>");

        Assert.Equal(QuirksMode.NoQuirks, document.Mode);
        Assert.IsType<DocumentType>(document.FirstChild);
        Assert.Equal("ok", document.Body.TextContent);
    }

    [Fact]
    public void BomAfterAnotherInputTokenIsPreserved()
    {
        var document = HtmlParser.ParseDocument("<!--before-->\uFEFF<html><body>ok</body></html>");

        Assert.Equal(QuirksMode.Quirks, document.Mode);
        Assert.Contains("\uFEFF", document.DocumentElement.TextContent);
    }

    [Fact]
    public void FragmentParserPreservesLeadingBomCharacter()
    {
        var document = Document.CreateHtmlDocument();
        var context = document.CreateElement("div");

        var fragment = HtmlParser.ParseFragment(context, "\uFEFF<span>ok</span>");

        Assert.StartsWith("\uFEFF", fragment.TextContent);
    }
}
