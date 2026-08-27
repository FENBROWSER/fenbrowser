using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlParserConformanceHardeningTests
{
    [Fact]
    public void EndTagOpen_EofPreservesBothMarkupCharacters()
    {
        var text = string.Concat(new HtmlTokenizer("</").Tokenize()
            .OfType<CharacterToken>()
            .Select(token => token.Data));

        Assert.Equal("</", text);
    }

    [Theory]
    [InlineData("&#4294967297;")]
    [InlineData("&#x100000001;")]
    public void OversizedNumericCharacterReference_DoesNotWrap(string source)
    {
        var text = string.Concat(new HtmlTokenizer(source).Tokenize()
            .OfType<CharacterToken>()
            .Select(token => token.Data));

        Assert.Equal("\uFFFD", text);
    }

    [Fact]
    public void LeadingEquals_IsPartOfAttributeName()
    {
        var tag = new HtmlTokenizer("<div =foo>").Tokenize().OfType<StartTagToken>().Single();
        var attribute = Assert.Single(tag.Attributes);

        Assert.Equal("=foo", attribute.Name);
        Assert.Equal(string.Empty, attribute.Value);
    }

    [Fact]
    public void CommentAfterBody_IsAppendedToHtmlElement()
    {
        var document = HtmlParser.ParseDocument("<!doctype html><html><body></body><!--tail--></html>");
        var comment = document.Descendants().OfType<Comment>().Single();

        Assert.Same(document.DocumentElement, comment.ParentNode);
    }

    [Fact]
    public void ImageStartTag_InHtmlContentIsRewrittenToImg()
    {
        var document = HtmlParser.ParseDocument("<!doctype html><html><body><image id='hero'></body></html>");
        var element = document.Descendants().OfType<Element>().Single(node => node.Id == "hero");

        Assert.Equal("img", element.LocalName);
        Assert.Equal("http://www.w3.org/1999/xhtml", element.NamespaceUri);
    }

    [Fact]
    public void SvgFragment_PreservesContextNamespace()
    {
        const string svgNamespace = "http://www.w3.org/2000/svg";
        var document = Document.CreateHtmlDocument();
        var context = document.CreateElementNS(svgNamespace, "svg");

        var fragment = HtmlParser.ParseFragment(context, "<circle></circle>");
        var circle = Assert.IsType<Element>(Assert.Single(fragment.ChildNodes));

        Assert.Equal(svgNamespace, circle.NamespaceUri);
    }
}
