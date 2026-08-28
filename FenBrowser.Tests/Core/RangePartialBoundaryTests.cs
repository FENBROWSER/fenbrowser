using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;
using DomRange = FenBrowser.Core.Dom.V2.Range;

namespace FenBrowser.Tests.Core;

public sealed class RangePartialBoundaryTests
{
    [Fact]
    public void CloneContentsPreservesPartialTextAndAncestorStructure()
    {
        var document = HtmlParser.ParseDocument(
            "<html><body><p><b>abc</b><span><i>def</i></span></p></body></html>");
        var paragraph = Find(document, "p");
        var start = Assert.IsType<Text>(Find(document, "b").FirstChild);
        var end = Assert.IsType<Text>(Find(document, "i").FirstChild);
        var range = CreateRange(document, start, 1, end, 2);

        var fragment = range.CloneContents();

        Assert.Equal("<b>bc</b><span><i>de</i></span>", SerializeChildren(fragment));
        Assert.Equal("<b>abc</b><span><i>def</i></span>", paragraph.InnerHTML);
        Assert.False(range.Collapsed);
    }

    [Fact]
    public void ExtractContentsPreservesPartialTextAndRemovesOnlySelection()
    {
        var document = HtmlParser.ParseDocument(
            "<html><body><p><b>abc</b><i>def</i></p></body></html>");
        var paragraph = Find(document, "p");
        var start = Assert.IsType<Text>(Find(document, "b").FirstChild);
        var end = Assert.IsType<Text>(Find(document, "i").FirstChild);
        var range = CreateRange(document, start, 1, end, 2);

        var fragment = range.ExtractContents();

        Assert.Equal("<b>bc</b><i>de</i>", SerializeChildren(fragment));
        Assert.Equal("<b>a</b><i>f</i>", paragraph.InnerHTML);
        Assert.True(range.Collapsed);
        Assert.Same(paragraph, range.StartContainer);
        Assert.Equal(1, range.StartOffset);
    }

    [Fact]
    public void DeleteContentsRetainsUnselectedPartialBoundaryText()
    {
        var document = HtmlParser.ParseDocument(
            "<html><body><p><b>abc</b><em>middle</em><i>def</i></p></body></html>");
        var paragraph = Find(document, "p");
        var start = Assert.IsType<Text>(Find(document, "b").FirstChild);
        var end = Assert.IsType<Text>(Find(document, "i").FirstChild);
        var range = CreateRange(document, start, 1, end, 2);

        range.DeleteContents();

        Assert.Equal("<b>a</b><i>f</i>", paragraph.InnerHTML);
        Assert.True(range.Collapsed);
        Assert.Same(paragraph, range.StartContainer);
        Assert.Equal(1, range.StartOffset);
    }

    [Fact]
    public void ExtractContentsHandlesStartBoundaryInCommonAncestor()
    {
        var document = HtmlParser.ParseDocument(
            "<html><body><p><b>keep</b><em>middle</em><i>def</i></p></body></html>");
        var paragraph = Find(document, "p");
        var end = Assert.IsType<Text>(Find(document, "i").FirstChild);
        var range = CreateRange(document, paragraph, 1, end, 2);

        var fragment = range.ExtractContents();

        Assert.Equal("<em>middle</em><i>de</i>", SerializeChildren(fragment));
        Assert.Equal("<b>keep</b><i>f</i>", paragraph.InnerHTML);
        Assert.Same(paragraph, range.StartContainer);
        Assert.Equal(1, range.StartOffset);
    }

    [Fact]
    public void CloneContentsHandlesEndBoundaryInCommonAncestor()
    {
        var document = HtmlParser.ParseDocument(
            "<html><body><p><b>abc</b><em>middle</em><i>keep</i></p></body></html>");
        var paragraph = Find(document, "p");
        var start = Assert.IsType<Text>(Find(document, "b").FirstChild);
        var range = CreateRange(document, start, 1, paragraph, 2);

        var fragment = range.CloneContents();

        Assert.Equal("<b>bc</b><em>middle</em>", SerializeChildren(fragment));
        Assert.Equal("<b>abc</b><em>middle</em><i>keep</i>", paragraph.InnerHTML);
        Assert.False(range.Collapsed);
    }

    private static DomRange CreateRange(
        Document document,
        Node start,
        int startOffset,
        Node end,
        int endOffset)
    {
        var range = new DomRange(document);
        range.SetStart(start, startOffset);
        range.SetEnd(end, endOffset);
        return range;
    }

    private static Element Find(Document document, string localName) =>
        document.Descendants().OfType<Element>().Single(element => element.LocalName == localName);

    private static string SerializeChildren(DocumentFragment fragment)
    {
        var result = string.Empty;
        for (var child = fragment.FirstChild; child != null; child = child.NextSibling)
            result += child.ToHtml();
        return result;
    }
}
