using System;
using FenBrowser.Core.Dom;
using Xunit;

namespace FenBrowser.Tests.Dom
{
    public class AttrTests
    {
        [Fact]
        public void Attr_Constructor_SetsNameAndValue()
        {
            var attr = new Attr("class", "container");
            
            Assert.Equal("class", attr.Name);
            Assert.Equal("container", attr.Value);
            Assert.Equal("class", attr.LocalName);
            Assert.Null(attr.Prefix);
            Assert.Null(attr.NamespaceURI);
        }

        [Fact]
        public void Attr_NamespaceConstructor_SetsAllProperties()
        {
            var attr = new Attr("http://www.w3.org/1999/xlink", "xlink:href", "/path");
            
            Assert.Equal("xlink:href", attr.Name);
            Assert.Equal("/path", attr.Value);
            Assert.Equal("href", attr.LocalName);
            Assert.Equal("xlink", attr.Prefix);
            Assert.Equal("http://www.w3.org/1999/xlink", attr.NamespaceURI);
        }

        [Fact]
        public void Attr_NodeType_IsAttribute()
        {
            var attr = new Attr("id", "main");
            
            Assert.Equal(NodeType.Attribute, attr.NodeType);
            Assert.Equal("id", attr.NodeName);
        }

        [Fact]
        public void Attr_NodeValue_ReturnsAndSetsValue()
        {
            var attr = new Attr("data-value", "initial");
            
            Assert.Equal("initial", attr.NodeValue);
            attr.NodeValue = "updated";
            Assert.Equal("updated", attr.Value);
        }

        [Fact]
        public void Attr_CloneNode_CreatesIndependentCopy()
        {
            var original = new Attr("title", "Original");
            var clone = (Attr)original.CloneNode(false);
            
            Assert.NotSame(original, clone);
            Assert.Equal(original.Name, clone.Name);
            Assert.Equal(original.Value, clone.Value);
            
            clone.Value = "Modified";
            Assert.Equal("Original", original.Value);
        }

        [Fact]
        public void Attr_ToString_ReturnsCorrectFormat()
        {
            var attr = new Attr("href", "http://example.com");
            
            Assert.Equal("href=\"http://example.com\"", attr.ToString());
        }
    }

    public class NamedNodeMapTests
    {
        [Fact]
        public void NamedNodeMap_Empty_HasZeroLength()
        {
            var elem = new Element("div");
            
            Assert.Equal(0, elem.NamedAttributes.Length);
        }

        [Fact]
        public void NamedNodeMap_SetNamedItem_AddsAttribute()
        {
            var elem = new Element("div");
            var attr = new Attr("id", "main");
            
            elem.NamedAttributes.SetNamedItem(attr);
            
            Assert.Equal(1, elem.NamedAttributes.Length);
            Assert.Same(attr, elem.NamedAttributes["id"]);
            Assert.Same(elem, attr.OwnerElement);
        }

        [Fact]
        public void NamedNodeMap_SetNamedItem_ReplacesExisting()
        {
            var elem = new Element("div");
            var attr1 = new Attr("class", "old");
            var attr2 = new Attr("class", "new");
            
            elem.NamedAttributes.SetNamedItem(attr1);
            var replaced = elem.NamedAttributes.SetNamedItem(attr2);
            
            Assert.Same(attr1, replaced);
            Assert.Null(attr1.OwnerElement);
            Assert.Same(attr2, elem.NamedAttributes["class"]);
        }

        [Fact]
        public void NamedNodeMap_GetNamedItem_ReturnsNullForMissing()
        {
            var elem = new Element("div");
            
            Assert.Null(elem.NamedAttributes.GetNamedItem("nonexistent"));
        }

        [Fact]
        public void NamedNodeMap_RemoveNamedItem_RemovesAttribute()
        {
            var elem = new Element("div");
            var attr = new Attr("id", "main");
            elem.NamedAttributes.SetNamedItem(attr);
            
            var removed = elem.NamedAttributes.RemoveNamedItem("id");
            
            Assert.Same(attr, removed);
            Assert.Null(removed.OwnerElement);
            Assert.Equal(0, elem.NamedAttributes.Length);
        }

        [Fact]
        public void NamedNodeMap_RemoveNamedItem_ThrowsForMissing()
        {
            var elem = new Element("div");
            
            Assert.Throws<DomException>(() => elem.NamedAttributes.RemoveNamedItem("id"));
        }

        [Fact]
        public void NamedNodeMap_IndexByInt_ReturnsCorrectAttribute()
        {
            var elem = new Element("div");
            elem.NamedAttributes.SetNamedItem(new Attr("a", "1"));
            elem.NamedAttributes.SetNamedItem(new Attr("b", "2"));
            
            Assert.Equal("a", elem.NamedAttributes[0].Name);
            Assert.Equal("b", elem.NamedAttributes[1].Name);
            Assert.Null(elem.NamedAttributes[5]); // Out of range
        }

        [Fact]
        public void NamedNodeMap_Contains_ChecksExistence()
        {
            var elem = new Element("div");
            elem.NamedAttributes.SetNamedItem(new Attr("class", "test"));
            
            Assert.True(elem.NamedAttributes.Contains("class"));
            Assert.False(elem.NamedAttributes.Contains("id"));
        }

        [Fact]
        public void Element_SetAttribute_UpdatesNamedNodeMap()
        {
            var elem = new Element("div");
            elem.SetAttribute("id", "main");
            
            var attr = elem.NamedAttributes.GetNamedItem("id");
            Assert.NotNull(attr);
            Assert.Equal("main", attr.Value);
        }

        [Fact]
        public void Element_RemoveAttribute_UpdatesNamedNodeMap()
        {
            var elem = new Element("div");
            elem.SetAttribute("id", "main");
            elem.RemoveAttribute("id");
            
            Assert.False(elem.NamedAttributes.Contains("id"));
        }
    }
}
