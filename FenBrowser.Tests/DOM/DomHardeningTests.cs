using System;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Dom
{
    public class DomHardeningTests
    {
        [Fact]
        public void SetAttributeNode_SyncsToLegacyDictionary()
        {
            var el = new Element("div");
            var attr = new Attr("id", "test-id");
            
            el.SetAttributeNode(attr);
            
            // Verify NamedNodeMap
            Assert.Same(attr, el.GetAttributeNode("id"));
            Assert.Equal("test-id", el.GetAttribute("id"));
            
            // Verify GetAttribute string helper
            Assert.Equal("test-id", el.GetAttribute("id"));
        }

        [Fact]
        public void SetAttribute_String_CreatesAttrNode()
        {
            var el = new Element("div");
            el.SetAttribute("class", "foo bar");
            
            var attr = el.GetAttributeNode("class");
            Assert.NotNull(attr);
            Assert.Equal("class", attr.Name);
            Assert.Equal("foo bar", attr.Value);
            
            // Verify legacy dictionary
            Assert.Equal("foo bar", el.GetAttribute("class"));
        }

        [Fact]
        public void RemoveAttributeNode_UpdatesDictionary()
        {
            var el = new Element("div");
            el.SetAttribute("test", "value");
            
            var attr = el.GetAttributeNode("test");
            el.RemoveAttributeNode(attr);
            
            Assert.Null(el.GetAttributeNode("test"));
            Assert.Null(el.Attributes.GetNamedItem("class"));
            Assert.Null(el.Attributes.GetNamedItem("test"));
        }
        
        [Fact]
        public void RemoveAttribute_String_RemovesNode()
        {
            var el = new Element("div");
            var attr = new Attr("data-foo", "bar");
            el.SetAttributeNode(attr);
            
            el.RemoveAttribute("data-foo");
            
            Assert.Null(el.GetAttributeNode("data-foo"));
            Assert.Null(el.Attributes.GetNamedItem("data-foo"));
        }
        
        [Fact]
        public void CaseInsensitivity_Html()
        {
            var el = new Element("DIV"); // HTML element
            el.SetAttribute("DaTa-TeSt", "123");
            
            // Should be accessible via lower case
            Assert.Equal("123", el.GetAttribute("data-test"));
            var byLowerName = el.Attributes.GetNamedItem("data-test");
            Assert.NotNull(byLowerName);
            Assert.Equal("123", byLowerName!.Value);
            
             // Node retrieval should work case-insensitively
             var attr = el.GetAttributeNode("DATA-TEST");
             Assert.NotNull(attr);
             Assert.Equal("123", attr!.Value);
        }
        
        [Fact]
        public void Iterating_Attributes()
        {
            // NamedNodeMap implements IEnumerable<Attr>
            var el = new Element("div");
            el.SetAttribute("a", "1");
            el.SetAttribute("b", "2");
            
            var attrs = el.Attributes.ToList();
            Assert.Equal(2, attrs.Count);
            Assert.Contains(attrs, a => a.Name == "a" && a.Value == "1");
            Assert.Contains(attrs, a => a.Name == "b" && a.Value == "2");
        }

        [Fact]
        public void Document_GetElementById_FallsBackToNextMatchingElementAfterRemoval()
        {
            var document = Document.CreateHtmlDocument();
            var first = document.CreateElement("div");
            var second = document.CreateElement("div");
            first.Id = "shared";
            second.Id = "shared";

            document.Body.Append(first, second);

            Assert.Same(first, document.GetElementById("shared"));

            document.Body.RemoveChild(first);

            Assert.Same(second, document.GetElementById("shared"));
        }

        [Fact]
        public void DocumentFragment_GetElementById_RebuildsIndexAfterMutations()
        {
            var document = Document.CreateHtmlDocument();
            var fragment = document.CreateDocumentFragment();
            var first = document.CreateElement("div");
            var second = document.CreateElement("div");
            first.Id = "shared";
            second.Id = "shared";

            fragment.Append(first, second);

            Assert.Same(first, fragment.GetElementById("shared"));

            fragment.RemoveChild(first);

            Assert.Same(second, fragment.GetElementById("shared"));
        }

        [Fact]
        public void TreeWalker_CurrentNode_MustStayWithinRoot()
        {
            var document = Document.CreateHtmlDocument();
            var root = document.CreateElement("div");
            var child = document.CreateElement("span");
            var outsider = document.CreateElement("p");

            root.AppendChild(child);
            document.Body.Append(root, outsider);

            var walker = document.CreateTreeWalker(root, NodeFilterShow.Element);
            walker.CurrentNode = child;
            Assert.Same(child, walker.CurrentNode);

            var exception = Assert.Throws<DomException>(() => walker.CurrentNode = outsider);
            Assert.Equal(DomExceptionNames.NotFoundError, exception.Name);
        }

        [Fact]
        public void NodeIterator_Reanchors_WhenReferenceSubtreeIsRemoved()
        {
            var document = Document.CreateHtmlDocument();
            var container = document.CreateElement("div");
            var nested = document.CreateElement("span");
            container.AppendChild(nested);
            document.Body.AppendChild(container);

            var iterator = document.CreateNodeIterator(document.Body, NodeFilterShow.Element);
            Assert.Same(document.Body, iterator.NextNode());
            Assert.Same(container, iterator.NextNode());

            document.Body.RemoveChild(container);

            Assert.Same(document.Body, iterator.ReferenceNode);
            Assert.True(iterator.PointerBeforeReferenceNode);
        }

        [Fact]
        public void ShadowRoot_ActiveElement_RequiresMembershipWithinShadowTree()
        {
            var document = Document.CreateHtmlDocument();
            var host = document.CreateElement("div");
            var inside = document.CreateElement("button");
            var outside = document.CreateElement("button");

            document.Body.Append(host, outside);

            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            shadowRoot.AppendChild(inside);
            shadowRoot.ActiveElement = inside;

            Assert.Same(inside, shadowRoot.ActiveElement);

            var exception = Assert.Throws<DomException>(() => shadowRoot.ActiveElement = outside);
            Assert.Equal(DomExceptionNames.NotFoundError, exception.Name);
        }

        [Fact]
        public void ShadowRoot_SetAdoptedStyleSheets_DeduplicatesAndRejectsNull()
        {
            var document = Document.CreateHtmlDocument();
            var host = document.CreateElement("div");
            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            var sheet = new object();

            shadowRoot.SetAdoptedStyleSheets(new[] { sheet, sheet });

            Assert.Single(shadowRoot.AdoptedStyleSheets);
            Assert.Throws<ArgumentNullException>(() => shadowRoot.SetAdoptedStyleSheets(new object[] { null }));
        }

        [Fact]
        public void DomSerializer_PreservesDoctypeAndInlineWhitespace_WhenNotPrettyPrinting()
        {
            var document = Document.CreateHtmlDocument();
            document.Body.AppendChild(document.CreateTextNode("  keep  "));

            var html = DomSerializer.Serialize(document, prettyPrint: false);

            Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
            Assert.Contains("<BODY>  keep  </BODY>", html, StringComparison.Ordinal);
        }
    }
}
