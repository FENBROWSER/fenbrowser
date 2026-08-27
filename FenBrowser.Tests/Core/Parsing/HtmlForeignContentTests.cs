using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlForeignContentTests
{
    [Fact]
    public void SvgPathRetainsTextAndElementChildren()
    {
        var body = ParseBody("<svg><path>before<title>label</title>after</path></svg>");
        var path = body.Descendants().OfType<Element>().Single(element => element.LocalName == "path");
        var title = body.Descendants().OfType<Element>().Single(element => element.LocalName == "title");

        Assert.Equal(Namespaces.Svg, path.NamespaceUri);
        Assert.Same(path, title.ParentNode);
        Assert.Equal("beforelabelafter", path.TextContent);
    }

    [Fact]
    public void SelfClosingSvgElementDoesNotCaptureFollowingSibling()
    {
        var body = ParseBody("<svg><path/><text>label</text></svg>");
        var svg = body.Descendants().OfType<Element>().Single(element => element.LocalName == "svg");
        var path = svg.Children.OfType<Element>().Single(element => element.LocalName == "path");
        var text = svg.Children.OfType<Element>().Single(element => element.LocalName == "text");

        Assert.Same(svg, path.ParentNode);
        Assert.Same(svg, text.ParentNode);
        Assert.Equal("label", text.TextContent);
    }

    [Fact]
    public void HtmlBreakoutTagExitsForeignContent()
    {
        var body = ParseBody("<svg><g><p>html</p></g></svg>");
        var svg = body.Children.OfType<Element>().Single(element => element.LocalName == "svg");
        var paragraph = body.Children.OfType<Element>().Single(element => element.LocalName == "p");

        Assert.Equal(Namespaces.Svg, svg.NamespaceUri);
        Assert.Equal(Namespaces.Html, paragraph.NamespaceUri);
        Assert.Same(body, paragraph.ParentNode);
    }

    [Fact]
    public void ForeignObjectIsAnHtmlIntegrationPoint()
    {
        var body = ParseBody("<svg><foreignObject><div>x</div></foreignObject><circle/></svg>");
        var svg = body.Descendants().OfType<Element>().Single(element => element.LocalName == "svg");
        var foreignObject = svg.Children.OfType<Element>().Single(element =>
            element.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase));
        var div = foreignObject.Children.OfType<Element>().Single();
        var circle = svg.Children.OfType<Element>().Single(element => element.LocalName == "circle");

        Assert.Equal(Namespaces.Svg, foreignObject.NamespaceUri);
        Assert.Equal(Namespaces.Html, div.NamespaceUri);
        Assert.Equal(Namespaces.Svg, circle.NamespaceUri);
    }

    [Fact]
    public void VoidClassificationAppliesOnlyToHtmlNamespace()
    {
        Assert.False(HtmlElementSemantics.IsVoid("path", Namespaces.Html));
        Assert.True(HtmlElementSemantics.IsVoid("source", Namespaces.Html));
        Assert.False(HtmlElementSemantics.IsVoid("source", Namespaces.Svg));
        Assert.True(HtmlParser.IsVoid("param"));
    }

    [Fact]
    public void SerializationDoesNotApplyHtmlVoidRulesToSvgNames()
    {
        var document = Document.CreateHtmlDocument();
        var svgSource = document.CreateElementNS(Namespaces.Svg, "source");
        svgSource.AppendChild(new Text("content"));

        Assert.Equal("<source>content</source>", svgSource.OuterHTML);
        Assert.Equal("<source>content</source>", FenBrowser.Core.DomSerializer.Serialize(svgSource, prettyPrint: false));
    }

    private static Element ParseBody(string markup)
    {
        return HtmlParser.ParseDocument(markup).Body!;
    }
}
