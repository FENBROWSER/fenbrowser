using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// DOM §4.5 document factories and §3.1.3 collections, and the NodeIterator
/// pre-removing steps of DOM §6.1.
/// </summary>
public class DomTraversalAndCollectionsTests
{
    [Fact]
    public void CreateCdataSectionIsRefusedOnHtmlDocumentsAndValidatedOnXml()
    {
        var html = HtmlParser.ParseDocument("<p></p>");
        var ex = Assert.Throws<DomException>(() => html.CreateCDATASection("x"));
        Assert.Equal("NotSupportedError", ex.Name);

        var xml = Document.CreateXmlDocument();
        Assert.Equal("InvalidCharacterError", Assert.Throws<DomException>(() => xml.CreateCDATASection("a]]>b")).Name);
        var cdata = xml.CreateCDATASection("1234");
        Assert.Equal(NodeType.CDataSection, cdata.NodeType);
        Assert.Equal("#cdata-section", cdata.NodeName);
        Assert.IsAssignableFrom<Text>(cdata);
    }

    [Fact]
    public void ProcessingInstructionsHaveTargetsAndMayBeDocumentChildren()
    {
        var xml = Document.CreateXmlDocument();
        var pi = xml.CreateProcessingInstruction("somePI", "data");
        Assert.Equal("somePI", pi.Target);
        Assert.Equal("somePI", pi.NodeName);
        Assert.Equal(NodeType.ProcessingInstruction, pi.NodeType);
        Assert.Equal("InvalidCharacterError", Assert.Throws<DomException>(() => xml.CreateProcessingInstruction("<", "")).Name);
        Assert.Equal("InvalidCharacterError", Assert.Throws<DomException>(() => xml.CreateProcessingInstruction("a", "?>")).Name);

        xml.AppendChild(pi);
        Assert.Same(pi, xml.LastChild);
    }

    [Fact]
    public void DocumentCollectionsAreLiveAndSupportNamedAccess()
    {
        var doc = HtmlParser.ParseDocument(
            "<form name='f'></form><a href='#' id='l1'>x</a><a name='anchor'>y</a><img><script></script>");
        var forms = doc.Forms;
        Assert.Equal(1, forms.Length);
        Assert.Same(forms[0], forms.NamedItem("f"));
        Assert.Equal(1, doc.Links.Length);
        Assert.Equal("l1", doc.Links.NamedItem("l1").Id);
        Assert.Equal(1, doc.Anchors.Length);
        Assert.Equal(1, doc.Images.Length);
        Assert.Equal(1, doc.Scripts.Length);

        doc.Body.AppendChild(doc.CreateElement("form"));
        Assert.Equal(2, forms.Length);
    }

    [Fact]
    public void RemovingTheRootLeavesTheIteratorAlone()
    {
        var doc = HtmlParser.ParseDocument("<div id='r'><p></p></div>");
        var root = doc.GetElementById("r");
        var iterator = doc.CreateNodeIterator(root);

        ((ContainerNode)root.ParentNode).RemoveChild(root);

        Assert.Same(root, iterator.ReferenceNode);
        Assert.True(iterator.PointerBeforeReferenceNode);
    }

    [Fact]
    public void RemovingTheReferenceWithPointerAfterMovesToThePrecedingNode()
    {
        var doc = HtmlParser.ParseDocument("<div id='r'><a id='a'></a><b id='b'></b><c id='c'></c></div>");
        var root = doc.GetElementById("r");
        var iterator = doc.CreateNodeIterator(root);
        iterator.NextNode(); // root
        iterator.NextNode(); // a
        iterator.NextNode(); // b

        root.RemoveChild(doc.GetElementById("b"));

        Assert.Same(doc.GetElementById("a"), iterator.ReferenceNode);
        Assert.False(iterator.PointerBeforeReferenceNode);
        Assert.Same(doc.GetElementById("c"), iterator.NextNode());
    }

    [Fact]
    public void IteratorsOverAnotherDocumentSeeThatDocumentsRemovals()
    {
        var doc = HtmlParser.ParseDocument("<p></p>");
        var foreign = Document.CreateHtmlDocument("t");
        var p = foreign.CreateElement("p");
        foreign.Body.AppendChild(p);
        var iterator = doc.CreateNodeIterator(foreign);
        while (iterator.NextNode() != p)
        {
        }

        foreign.Body.RemoveChild(p);

        Assert.Same(foreign.Body, iterator.ReferenceNode);
    }

    [Fact]
    public void FilterThatRemovesTheCandidateRetargetsTheInFlightPosition()
    {
        var doc = HtmlParser.ParseDocument("<div id='r'><a id='a'></a><b id='b'></b><c id='c'></c></div>");
        var root = doc.GetElementById("r");
        var b = doc.GetElementById("b");
        var iterator = doc.CreateNodeIterator(root, 0xFFFFFFFF, node =>
        {
            if (node == b)
            {
                ((ContainerNode)b.ParentNode).RemoveChild(b);
            }

            return NodeFilterResult.Accept;
        });
        iterator.NextNode();
        iterator.NextNode();

        Assert.Same(b, iterator.NextNode());
        Assert.Same(doc.GetElementById("a"), iterator.ReferenceNode);
        Assert.False(iterator.PointerBeforeReferenceNode);
        Assert.Same(doc.GetElementById("c"), iterator.NextNode());
    }

    [Fact]
    public void NamespaceLookupWalksAncestorsAndXmlnsAttributes()
    {
        var xml = Document.CreateXmlDocument("urn:a", "a:root");
        var child = xml.CreateElementNS("urn:b", "child");
        xml.DocumentElement.AppendChild(child);
        xml.DocumentElement.SetAttributeNS("http://www.w3.org/2000/xmlns/", "xmlns:z", "urn:z");

        Assert.Equal("urn:a", DomNamespaceLookup.LookupNamespaceUri(child, "a"));
        Assert.Equal("urn:z", DomNamespaceLookup.LookupNamespaceUri(child, "z"));
        Assert.Equal("urn:b", DomNamespaceLookup.LookupNamespaceUri(child, null));
        Assert.Equal("a", DomNamespaceLookup.LookupPrefix(child, "urn:a"));
        Assert.Equal("z", DomNamespaceLookup.LookupPrefix(child, "urn:z"));
        Assert.Null(DomNamespaceLookup.LookupPrefix(child, "urn:none"));
    }
}
