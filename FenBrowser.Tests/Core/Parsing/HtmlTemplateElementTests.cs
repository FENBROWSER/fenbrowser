using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTemplateElementTests
{
    [Fact]
    public void ParsedTemplateStoresNodesInSeparateContentFragment()
    {
        var document = HtmlParser.ParseDocument("<template id=t><!--a--><div>content</div></template>");

        var template = Assert.IsType<HtmlTemplateElement>(document.GetElementById("t"));
        Assert.Empty(template.ChildNodes);
        Assert.Equal(2, template.Content.ChildNodes.Length);
        Assert.Equal("content", template.Content.TextContent);
        Assert.Equal("<!--a--><div>content</div>", template.InnerHTML);
        Assert.Equal("<template id=\"t\"><!--a--><div>content</div></template>", template.OuterHTML);
    }

    [Fact]
    public void NestedTemplatesOwnIndependentContentFragments()
    {
        var document = HtmlParser.ParseDocument("<template id=outer><div><template id=inner><span>x</span></template></div></template>");

        var outer = Assert.IsType<HtmlTemplateElement>(document.GetElementById("outer"));
        var inner = Assert.IsType<HtmlTemplateElement>(outer.Content.GetElementById("inner"));
        Assert.Empty(outer.ChildNodes);
        Assert.Empty(inner.ChildNodes);
        Assert.Equal("x", inner.Content.TextContent);
        Assert.NotSame(outer.Content, inner.Content);
    }

    [Fact]
    public void DeepCloneAndImportPreserveTemplateContents()
    {
        var sourceDocument = new Document();
        var template = Assert.IsType<HtmlTemplateElement>(sourceDocument.CreateElement("template"));
        template.InnerHTML = "<strong>copied</strong>";

        var clone = Assert.IsType<HtmlTemplateElement>(template.CloneNode(deep: true));
        var targetDocument = new Document();
        var imported = Assert.IsType<HtmlTemplateElement>(targetDocument.ImportNode(template, deep: true));

        Assert.Empty(clone.ChildNodes);
        Assert.Equal("<strong>copied</strong>", clone.InnerHTML);
        Assert.Empty(imported.ChildNodes);
        Assert.Equal("<strong>copied</strong>", imported.InnerHTML);
        Assert.NotSame(targetDocument, imported.Content.OwnerDocument);
        Assert.Same(imported.Content.OwnerDocument, imported.Content.FirstChild!.OwnerDocument);
    }

    [Fact]
    public void TemplatesShareSeparateContentOwnerAndAdoptItAcrossDocuments()
    {
        var sourceDocument = new Document();
        var first = Assert.IsType<HtmlTemplateElement>(sourceDocument.CreateElement("template"));
        var second = Assert.IsType<HtmlTemplateElement>(sourceDocument.CreateElement("template"));
        first.InnerHTML = "<template id=nested><i>x</i></template>";

        Assert.NotSame(sourceDocument, first.Content.OwnerDocument);
        Assert.Same(first.Content.OwnerDocument, second.Content.OwnerDocument);
        var nested = Assert.IsType<HtmlTemplateElement>(first.Content.GetElementById("nested"));
        Assert.Same(first.Content.OwnerDocument, nested.OwnerDocument);
        Assert.Same(first.Content.OwnerDocument, nested.Content.OwnerDocument);

        var targetDocument = Document.CreateHtmlDocument();
        var targetTemplate = Assert.IsType<HtmlTemplateElement>(targetDocument.CreateElement("template"));
        targetDocument.Body!.AppendChild(first);

        Assert.Same(targetDocument, first.OwnerDocument);
        Assert.Same(targetTemplate.Content.OwnerDocument, first.Content.OwnerDocument);
        Assert.Same(first.Content.OwnerDocument, nested.OwnerDocument);
        Assert.Same(first.Content.OwnerDocument, nested.Content.OwnerDocument);
    }

    [Fact]
    public void TemplateFragmentContextReturnsParsedNodes()
    {
        var document = new Document();
        var template = Assert.IsType<HtmlTemplateElement>(document.CreateElement("template"));

        var fragment = HtmlParser.ParseFragment(template, "<tr><td>cell</td></tr>");

        Assert.NotEmpty(fragment.ChildNodes);
        Assert.Equal("cell", fragment.TextContent);
    }

    [Fact]
    public void TemplateInsertionModeStackRestoresOuterAndDocumentParsing()
    {
        var document = HtmlParser.ParseDocument(
            "<body><template id=outer><table><tr><td>table</td></tr></table>" +
            "<template id=inner><tr><td>inner</td></tr></template><p id=outer-tail>tail</p>" +
            "</template><div id=after>after</div>");

        var outer = Assert.IsType<HtmlTemplateElement>(document.GetElementById("outer"));
        var inner = Assert.IsType<HtmlTemplateElement>(outer.Content.GetElementById("inner"));
        Assert.Equal("tabletail", outer.Content.TextContent);
        Assert.Equal("inner", inner.Content.TextContent);
        Assert.NotNull(outer.Content.GetElementById("outer-tail"));
        Assert.Equal("after", document.GetElementById("after")!.TextContent);
        Assert.Single(outer.Content.Descendants().OfType<Element>(), element => element.LocalName == "table");
    }
}
