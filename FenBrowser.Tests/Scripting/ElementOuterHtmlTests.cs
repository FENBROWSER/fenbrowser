using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM "outerHTML" (https://dom.spec.whatwg.org/#dom-element-outerhtml). The getter
/// existed on Element but was never reachable from script, so reading it returned
/// undefined and any probe that touched it threw.
/// </summary>
public class ElementOuterHtmlTests
{
    private static Document Parse(string html)
        => new HtmlParser(html, new System.Uri("https://example.test/")).Parse();

    private static Element FirstDiv(Document document)
        => (Element)document.Body.FirstChild;

    [Fact]
    public void GetterIncludesTheElementItselfUnlikeInnerHtml()
    {
        var div = FirstDiv(Parse("<html><body><div id='a'><span>hi</span></div></body></html>"));

        Assert.Equal("<span>hi</span>", div.InnerHTML);
        Assert.Contains("<div", div.OuterHTML);
        Assert.Contains("id=\"a\"", div.OuterHTML);
        Assert.Contains("<span>hi</span>", div.OuterHTML);
    }

    [Fact]
    public void SetterReplacesTheElementInItsParent()
    {
        var document = Parse("<html><body><div id='a'>old</div></body></html>");
        var div = FirstDiv(document);
        var body = document.Body;

        div.OuterHTML = "<p id='b'>new</p>";

        Assert.Single(body.Children);
        var replacement = (Element)body.FirstChild;
        Assert.Equal("P", replacement.TagName);
        Assert.Equal("b", replacement.GetAttribute("id"));
        Assert.Equal("new", replacement.TextContent);
        Assert.Null(div.ParentNode);
    }

    // The fragment may hold more than one top-level node, which is why the setter
    // cannot be a single ReplaceChild.
    [Fact]
    public void SetterInsertsEverySiblingInTheFragment()
    {
        var document = Parse("<html><body><div>old</div></body></html>");
        var body = document.Body;

        FirstDiv(document).OuterHTML = "<p>one</p><p>two</p><p>three</p>";

        Assert.Equal(3, body.Children.Count());
        Assert.Equal("one", ((Element)body.Children[0]).TextContent);
        Assert.Equal("three", ((Element)body.Children[2]).TextContent);
    }

    [Fact]
    public void SetterKeepsSurroundingSiblingsAndOrder()
    {
        var document = Parse("<html><body><i>before</i><div>old</div><b>after</b></body></html>");
        var body = document.Body;
        var div = (Element)body.Children[1];

        div.OuterHTML = "<p>mid</p>";

        Assert.Equal(3, body.Children.Count());
        Assert.Equal("I", ((Element)body.Children[0]).TagName);
        Assert.Equal("P", ((Element)body.Children[1]).TagName);
        Assert.Equal("B", ((Element)body.Children[2]).TagName);
    }

    [Fact]
    public void SetterWithEmptyStringJustRemovesTheElement()
    {
        var document = Parse("<html><body><div>old</div></body></html>");
        var body = document.Body;

        FirstDiv(document).OuterHTML = string.Empty;

        Assert.Empty(body.Children);
    }

    // Spec: an element with no parent has nothing to replace it in, so setting is
    // a no-op rather than an error.
    [Fact]
    public void SetterOnAParentlessElementDoesNothing()
    {
        var document = Parse("<html><body></body></html>");
        var orphan = document.CreateElement("div");

        orphan.OuterHTML = "<p>ignored</p>";

        Assert.Equal("DIV", orphan.TagName);
        Assert.Null(orphan.ParentNode);
    }

    [Fact]
    public void SetterThrowsWhenTheParentIsTheDocument()
    {
        var document = Parse("<html><body></body></html>");

        var error = Assert.Throws<DomException>(() => document.DocumentElement.OuterHTML = "<html></html>");
        Assert.Equal("NoModificationAllowedError", error.Name);
    }

    [Fact]
    public void GetterRoundTripsThroughTheSetter()
    {
        var document = Parse("<html><body><div id='a'><span>hi</span></div></body></html>");
        var original = FirstDiv(document).OuterHTML;

        FirstDiv(document).OuterHTML = original;

        Assert.Equal(original, FirstDiv(document).OuterHTML);
    }
}
