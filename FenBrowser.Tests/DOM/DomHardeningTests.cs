using System;
using System.Linq;
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
    }
}
