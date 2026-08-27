using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTreeConstructionRecoveryTests
{
    [Fact]
    public void NestedAnchor_RunsAdoptionAgencyBeforeOpeningReplacement()
    {
        var body = ParseBody("<p><a id=outer>one<b>bold<a id=inner>two</a>tail</b>end</a></p>");
        var paragraph = body.Children.OfType<Element>().Single(node => node.LocalName == "p");
        var outer = paragraph.Descendants().OfType<Element>().Single(node => node.Id == "outer");
        var inner = paragraph.Descendants().OfType<Element>().Single(node => node.Id == "inner");
        var boldNodes = paragraph.Descendants().OfType<Element>().Where(node => node.LocalName == "b").ToArray();

        Assert.Equal("onebold", outer.TextContent);
        Assert.Equal(2, boldNodes.Length);
        Assert.Same(boldNodes[1], inner.ParentNode);
        Assert.Equal("twotail", boldNodes[1].TextContent);
        Assert.Equal("oneboldtwotailend", paragraph.TextContent);
    }

    [Fact]
    public void IdenticalFormattingElements_ReconstructAtMostThreeEntries()
    {
        var body = ParseBody("<p><b class=x><b class=x><b class=x><b class=x>text</p>after");
        var after = body.ChildNodes.OfType<Element>().Last(node => node.LocalName == "b");

        var depth = 0;
        Element current = after;
        while (current is not null && current.LocalName == "b")
        {
            depth++;
            current = current.FirstChild as Element;
        }

        Assert.Equal(3, depth);
        Assert.Equal("after", after.TextContent);
    }

    [Fact]
    public void AdoptionAgency_UsesTableFosterParentLocation()
    {
        var body = ParseBody("<table><b><div></b>x</div></table>");
        var elements = body.Children.OfType<Element>().ToArray();

        Assert.Equal(new[] { "b", "div", "table" }, elements.Select(node => node.LocalName));
        Assert.Equal("x", elements[1].TextContent);
        Assert.Equal("b", Assert.IsType<Element>(elements[1].FirstChild).LocalName);
    }

    [Fact]
    public void ParamInTable_IsVoidAndDoesNotCaptureFollowingTable()
    {
        Assert.True(HtmlParser.IsVoid("param"));
        var body = ParseBody("<table><param id=p><tr><td>x</td></tr></table>");
        var parameter = body.Descendants().OfType<Element>().Single(node => node.Id == "p");
        var table = body.Descendants().OfType<Element>().Single(node => node.LocalName == "table");

        Assert.Same(body, parameter.ParentNode);
        Assert.Same(body, table.ParentNode);
        Assert.Empty(parameter.ChildNodes);
    }

    [Fact]
    public void Noscript_WhenScriptingDisabled_UsesNormalHeadRules()
    {
        var options = new HtmlParserOptions { ScriptingEnabled = false };

        var document = HtmlParser.ParseDocument(
            "<head><noscript><meta name=x><link rel=x></noscript></head><body>x",
            options);
        var noscript = document.Descendants().OfType<Element>().Single(node => node.LocalName == "noscript");

        Assert.Equal(new[] { "meta", "link" }, noscript.Children.OfType<Element>().Select(node => node.LocalName));
        Assert.Equal(string.Empty, noscript.TextContent);
    }

    [Fact]
    public void Noscript_WhenScriptingEnabled_RemainsRawText()
    {
        var document = HtmlParser.ParseDocument(
            "<head><noscript><meta name=x><link rel=x></noscript></head><body>x");
        var noscript = document.Descendants().OfType<Element>().Single(node => node.LocalName == "noscript");

        Assert.Empty(noscript.Children);
        Assert.Equal("<meta name=x><link rel=x>", noscript.TextContent);
    }

    [Fact]
    public void FosteredFormattingSubtree_DoesNotCaptureTrailingTextAfterTable()
    {
        var body = ParseBody("<table><section><em>x</table>y");
        var children = body.ChildNodes.ToArray();
        var section = Assert.IsType<Element>(children[0]);
        var table = Assert.IsType<Element>(children[1]);
        var trailing = Assert.IsType<Element>(children[2]);

        Assert.Equal("section", section.LocalName);
        Assert.Equal("x", section.TextContent);
        Assert.Equal("table", table.LocalName);
        Assert.Equal("em", trailing.LocalName);
        Assert.Equal("y", trailing.TextContent);
    }

    private static Element ParseBody(string source)
    {
        var document = HtmlParser.ParseDocument(source);
        return document.Body!;
    }
}
