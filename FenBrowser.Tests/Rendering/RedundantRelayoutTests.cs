using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Page loads must not trigger a whole-document restyle, which publishes a new
/// style snapshot and so forces a full relayout, when nothing about the document
/// as a whole changed.
/// </summary>
public class RedundantRelayoutTests
{
    private static (Element Html, Element Body, Element Paragraph, Dictionary<Node, CssComputed> Styles) BuildStyledDocument()
    {
        var document = new Document();
        var html = document.CreateElement("html");
        var body = document.CreateElement("body");
        var paragraph = document.CreateElement("p");
        paragraph.AppendChild(document.CreateTextNode("text nodes never get a style entry"));
        body.AppendChild(paragraph);
        body.AppendChild(document.CreateComment("nor do comments"));
        html.AppendChild(body);
        document.AppendChild(html);

        var styles = new Dictionary<Node, CssComputed>
        {
            [html] = new CssComputed(),
            [body] = new CssComputed(),
            [paragraph] = new CssComputed(),
        };

        foreach (var node in new Node[] { html, body, paragraph })
        {
            node.ClearDirty(InvalidationKind.Style);
        }

        return (html, body, paragraph, styles);
    }

    [Fact]
    public void PostScriptRefresh_IsNotNeeded_WhenOnlyTextNodesLackStyles()
    {
        var (html, _, _, styles) = BuildStyledDocument();

        Assert.False(CustomHtmlEngine.NeedsPostScriptStyleRefresh(html, styles));
    }

    [Fact]
    public void PostScriptRefresh_IsNotNeeded_ForADirtyDescendant()
    {
        var (html, _, paragraph, styles) = BuildStyledDocument();
        paragraph.MarkDirty(InvalidationKind.Style);

        Assert.True(html.ChildStyleDirty);
        Assert.False(CustomHtmlEngine.NeedsPostScriptStyleRefresh(html, styles));
    }

    [Fact]
    public void PostScriptRefresh_IsNeeded_WhenTheRootWasRestyled()
    {
        var (html, _, _, styles) = BuildStyledDocument();
        html.MarkDirty(InvalidationKind.Style);

        Assert.True(CustomHtmlEngine.NeedsPostScriptStyleRefresh(html, styles));
    }

    [Fact]
    public void PostScriptRefresh_IsNeeded_WhenAnElementHasNoStyle()
    {
        var (html, body, _, styles) = BuildStyledDocument();
        var added = html.OwnerDocument.CreateElement("div");
        body.AppendChild(added);
        html.ClearDirty(InvalidationKind.Style);
        body.ClearDirty(InvalidationKind.Style);

        Assert.True(CustomHtmlEngine.NeedsPostScriptStyleRefresh(html, styles));
    }
}
