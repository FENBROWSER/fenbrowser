using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// XML 1.0 lets whitespace, comments and processing instructions surround the
/// document element, but a DOM Document cannot hold Text. XmlDomParser appended
/// that whitespace anyway, so almost every real XML file - they end with a
/// newline after the root - threw a DomException instead of parsing.
/// </summary>
public sealed class XmlDomParserWhitespaceTests
{
    [Fact]
    public void WhitespaceAroundTheDocumentElementIsNotPartOfTheDom()
    {
        var document = XmlDomParser.Parse("\n<root>\n  <child/>\n</root>\n\n");

        Assert.Equal("root", document.DocumentElement.LocalName);
        foreach (var child in document.ChildNodes)
        {
            Assert.IsNotType<Text>(child);
        }
    }

    [Fact]
    public void CommentsAroundTheDocumentElementAreKept()
    {
        var document = XmlDomParser.Parse("<!-- before -->\n<root/>\n<!-- after -->\n");

        Assert.Equal(3, document.ChildNodes.Length);
        Assert.IsType<Comment>(document.FirstChild);
        Assert.IsType<Comment>(document.LastChild);
    }

    [Fact]
    public void WhitespaceInsideElementsIsStillText()
    {
        var document = XmlDomParser.Parse("<root> <child/> </root>\n");

        Assert.IsType<Text>(document.DocumentElement.FirstChild);
    }
}
