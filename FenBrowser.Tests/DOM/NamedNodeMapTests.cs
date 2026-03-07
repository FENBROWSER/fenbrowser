using System;
using System.Linq;
using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.DOM;
using System.Collections.Generic;

namespace FenBrowser.Tests.DOM
{
    public class NamedNodeMapTests
    {
        private Element _root;
        private IExecutionContext _context;
        private ElementWrapper _wrapper;
        
        public NamedNodeMapTests()
        {
            _root = new Element("div");
            _root.SetAttribute("id", "test-div");
            _root.SetAttribute("data-custom", "value123");
            
            var perms = new PermissionManager(JsPermissions.DomRead | JsPermissions.DomWrite | JsPermissions.DomEvents);
            _context = new FenBrowser.FenEngine.Core.ExecutionContext(perms);
            _wrapper = new ElementWrapper(_root, _context);
        }

        [Fact]
        public void TestAttributesPropertyExposed()
        {
            var attributes = _wrapper.Get("attributes", _context);
            Assert.False(attributes.IsUndefined);
            Assert.False(attributes.IsNull);
            Assert.True(attributes.IsObject);
            
            var mapWrapper = attributes.AsObject() as NamedNodeMapWrapper;
            Assert.NotNull(mapWrapper);
        }

        [Fact]
        public void TestAttributesLength()
        {
            var attributes = _wrapper.Get("attributes", _context);
            var length = attributes.AsObject().Get("length", _context);
            
            Assert.True(length.IsNumber);
            Assert.Equal(2, length.ToNumber());
        }

        [Fact]
        public void TestGetNamedItem()
        {
            var attributes = _wrapper.Get("attributes", _context).AsObject();
            var getItem = attributes.Get("getNamedItem", _context).AsFunction();
            
            var result = getItem.Invoke(new FenValue[] { FenValue.FromString("id") }, _context);
            
            Assert.True(result.IsObject);
            var attrWrapper = result.AsObject() as AttrWrapper;
            Assert.NotNull(attrWrapper);
            Assert.Equal("id", attrWrapper.Attr.Name);
            Assert.Equal("test-div", attrWrapper.Attr.Value);
        }
        
        [Fact]
        public void TestItemByIndex()
        {
            var attributes = _wrapper.Get("attributes", _context).AsObject();
            var itemMethod = attributes.Get("item", _context).AsFunction();
            
            // Note: order is not guaranteed by spec but commonly implementation dependent
            // Our implementation uses List<Attr>, insertion order likely preserved
            var result0 = itemMethod.Invoke(new FenValue[] { FenValue.FromNumber(0) }, _context);
            var result1 = itemMethod.Invoke(new FenValue[] { FenValue.FromNumber(1) }, _context);
            
            Assert.True(result0.IsObject);
            Assert.True(result1.IsObject);
        }
        
        [Fact]
        public void TestGetAttributeNode()
        {
            var method = _wrapper.Get("getAttributeNode", _context).AsFunction();
            var result = method.Invoke(new FenValue[] { FenValue.FromString("data-custom") }, _context);
            
            Assert.True(result.IsObject);
            var attrWrapper = result.AsObject() as AttrWrapper;
            Assert.NotNull(attrWrapper);
            Assert.Equal("value123", attrWrapper.Attr.Value);
        }

        [Fact]
        public void TestSetAttributeNode()
        {
             var newAttr = new Attr("title", "hover-text");
             var attrWrapper = new AttrWrapper(newAttr, _context);
             
             var method = _wrapper.Get("setAttributeNode", _context).AsFunction();
             method.Invoke(new FenValue[] { FenValue.FromObject(attrWrapper) }, _context);
             
             Assert.Equal("hover-text", _root.GetAttribute("title"));
             Assert.Equal(3, _root.Attributes.Length);
        }

        [Fact]
        public void ObjectGetOwnPropertyNames_UsesWrapperOwnPropertyEnumeration()
        {
            var runtime = new FenRuntime();
            var collection = new HTMLCollectionWrapper(Array.Empty<Element>(), runtime.Context);
            collection.DefineOwnProperty("hidden", new PropertyDescriptor
            {
                Value = FenValue.FromString("value"),
                Enumerable = false,
                Configurable = true,
                Writable = true
            });

            var objectCtor = runtime.GlobalEnv.Get("Object").AsObject();
            var getOwnPropertyNames = objectCtor.Get("getOwnPropertyNames", runtime.Context).AsFunction();
            var names = getOwnPropertyNames.Invoke(new[] { FenValue.FromObject(collection) }, runtime.Context).AsObject();

            Assert.Equal(1, names.Get("length", runtime.Context).ToNumber());
            Assert.Equal("hidden", names.Get("0", runtime.Context).ToString());
        }

        [Fact]
        public void NamedNodeMap_GetOwnPropertyNames_ReturnsIndicesBeforeAttributeNames()
        {
            var runtime = new FenRuntime();
            var element = new Element("div");
            element.SetAttribute("id", "sample");
            element.SetAttribute("class", "fancy");
            var wrapper = new ElementWrapper(element, runtime.Context);
            var attributes = wrapper.Get("attributes", runtime.Context);

            var objectCtor = runtime.GlobalEnv.Get("Object").AsObject();
            var getOwnPropertyNames = objectCtor.Get("getOwnPropertyNames", runtime.Context).AsFunction();
            var names = getOwnPropertyNames.Invoke(new[] { attributes }, runtime.Context).AsObject();

            var actual = new List<string>();
            var length = (int)names.Get("length", runtime.Context).ToNumber();
            for (var i = 0; i < length; i++)
            {
                actual.Add(names.Get(i.ToString(), runtime.Context).ToString());
            }

            Assert.Equal(new[] { "0", "1", "id", "class" }, actual);
        }
    }
}
