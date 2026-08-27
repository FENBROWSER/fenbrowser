using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class FosterParentingLocalFixtureTests
{
    [Fact]
    public void FosteredElementUsesNormalTreeConstructionAndContainsItsChildren()
    {
        var document = HtmlParser.ParseDocument(
            "<div id=host><table><div id=orphan data-kind=test><span>inside</span></div></table></div>");

        var host = document.GetElementById("host")!;
        var orphan = document.GetElementById("orphan")!;
        Assert.Same(host, orphan.ParentNode);
        Assert.Equal("inside", orphan.TextContent);
        Assert.Equal("test", orphan.GetAttribute("data-kind"));
        Assert.Same(document, orphan.OwnerDocument);
        Assert.True(orphan.SourceOffset >= 0, "Fostered elements must retain their start-tag source location.");
        Assert.Equal("table", Assert.IsType<Element>(host.LastChild).LocalName);
    }

    [Fact]
    public void TemplateContentIsFosterParentWhenTableIsImmediatelyBelowTemplate()
    {
        var document = HtmlParser.ParseDocument(
            "<template id=tmpl><table><tr><td>one</td></tr>" +
            "<div id=orphan>content</div><tr><td>two</td></tr></table></template>");

        var template = Assert.IsType<HtmlTemplateElement>(document.GetElementById("tmpl"));
        var orphan = template.Content.GetElementById("orphan")!;
        Assert.Same(template.Content, orphan.ParentNode);
    }

    [Fact]
    public void ContainerInsideTemplateRemainsFosterParentWhenItOwnsTable()
    {
        var document = HtmlParser.ParseDocument(
            "<template id=tmpl><div id=parent><table><tr><td>one</td></tr>" +
            "<div id=orphan>content</div><tr><td>two</td></tr></table></div></template>");

        var template = Assert.IsType<HtmlTemplateElement>(document.GetElementById("tmpl"));
        var parent = template.Content.GetElementById("parent")!;
        var orphan = template.Content.GetElementById("orphan")!;
        Assert.Same(parent, orphan.ParentNode);
    }

    [Fact]
    public void LocalHtml5libTableTextIsCoalescedBeforeTable()
    {
        var document = HtmlParser.ParseDocument("<table>A<td>B</td>C</table>");

        Assert.Equal("AC<table><tbody><tr><td>B</td></tr></tbody></table>", document.Body!.InnerHTML);
    }

    [Fact]
    public void FosteredFormattingRunsTheNormalAdoptionAgencyPath()
    {
        var document = HtmlParser.ParseDocument("<table><a>1<p>2</a>3</p>");

        Assert.Equal("<a>1</a><p><a>2</a>3</p><table></table>", document.Body!.InnerHTML);
    }
}
