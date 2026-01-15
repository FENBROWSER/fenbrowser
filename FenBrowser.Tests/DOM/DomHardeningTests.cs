using System;
using System.Linq;
using FenBrowser.Core.Dom;
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
            Assert.Equal("test-id", el.Attributes["id"]);
            
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
            Assert.Equal("foo bar", el.Attributes["class"]);
        }

        [Fact]
        public void RemoveAttributeNode_UpdatesDictionary()
        {
            var el = new Element("div");
            el.SetAttribute("test", "value");
            
            var attr = el.GetAttributeNode("test");
            el.RemoveAttributeNode(attr);
            
            Assert.Null(el.GetAttributeNode("test"));
            Assert.False(el.Attributes.ContainsKey("test"));
            Assert.Null(el.GetAttribute("test"));
        }
        
        [Fact]
        public void RemoveAttribute_String_RemovesNode()
        {
            var el = new Element("div");
            var attr = new Attr("data-foo", "bar");
            el.SetAttributeNode(attr);
            
            el.RemoveAttribute("data-foo");
            
            Assert.Null(el.GetAttributeNode("data-foo"));
            Assert.False(el.Attributes.ContainsKey("data-foo"));
        }
        
        [Fact]
        public void CaseInsensitivity_Html()
        {
            var el = new Element("DIV"); // HTML element
            el.SetAttribute("DaTa-TeSt", "123");
            
            // Should be accessible via lower case
            Assert.Equal("123", el.GetAttribute("data-test"));
            Assert.Equal("123", el.Attributes["data-test"]);
            
             // Node retrieval should work case-insensitively
             var attr = el.GetAttributeNode("DATA-TEST");
             Assert.NotNull(attr);
             // Original name preserved
             Assert.Equal("DaTa-TeSt", attr.Name);
        }
        
        [Fact]
        public void Iterating_Attributes()
        {
            // NamedNodeMap implements IEnumerable<Attr>
            var el = new Element("div");
            el.SetAttribute("a", "1");
            el.SetAttribute("b", "2");
            
            var attrs = el.NamedAttributes.ToList();
            Assert.Equal(2, attrs.Count);
            Assert.Contains(attrs, a => a.Name == "a" && a.Value == "1");
            Assert.Contains(attrs, a => a.Name == "b" && a.Value == "2");
        }
    }
}
