using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-5: Symbol protocol + Symbol.toStringTag regression tests.
    /// Validates Symbol creation, Symbol.for/keyFor, well-known symbols, and toStringTag.
    /// </summary>
    [Collection("Engine Tests")]
    public class SymbolConformanceTests
    {
        public SymbolConformanceTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        [Fact]
        public void Symbol_TypeofIsSymbol()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = Symbol('desc'); var t = typeof s;");
            Assert.Equal("symbol", rt.GetGlobal("t").ToString());
        }

        [Fact]
        public void Symbol_WithSameDescription_AreNotEqual()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = Symbol('x'); var b = Symbol('x'); var eq = (a === b);");
            Assert.False(rt.GetGlobal("eq").ToBoolean(),
                "Two symbols with same description should not be strictly equal");
        }

        [Fact]
        public void Symbol_For_SameKeyReturnsExactSameSymbol()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = Symbol.for('shared'); var b = Symbol.for('shared'); var eq = (a === b);");
            Assert.True(rt.GetGlobal("eq").ToBoolean(), "Symbol.for with same key should return identical symbol");
        }

        [Fact]
        public void Symbol_KeyFor_ReturnsKey()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = Symbol.for('myKey'); var k = Symbol.keyFor(s);");
            Assert.Equal("myKey", rt.GetGlobal("k").ToString());
        }

        [Fact]
        public void Symbol_WellKnown_IteratorExists()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var t = typeof Symbol.iterator;");
            Assert.Equal("symbol", rt.GetGlobal("t").ToString());
        }

        [Fact]
        public void Symbol_WellKnown_ToStringTagExists()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var t = typeof Symbol.toStringTag;");
            Assert.Equal("symbol", rt.GetGlobal("t").ToString());
        }

        [Fact]
        public void Symbol_ToStringTag_UsedInObjectPrototypeToString()
        {
            var rt = CreateRuntime();
            // This tests our fixed Object.prototype.toString that checks toStringTag
            rt.ExecuteSimple(@"
                var obj = {};
                obj[Symbol.toStringTag] = 'MyCustomType';
                var tag = Object.prototype.toString.call(obj);
            ");
            Assert.Equal("[object MyCustomType]", rt.GetGlobal("tag").ToString());
        }

        [Fact]
        public void Symbol_ToStringTag_NotSet_UsesInternalClass()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var tag = Object.prototype.toString.call({});");
            Assert.Equal("[object Object]", rt.GetGlobal("tag").ToString());
        }

        [Fact]
        public void Symbol_ToStringTag_NullIsObjectNull()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var tag = Object.prototype.toString.call(null);");
            Assert.Equal("[object Null]", rt.GetGlobal("tag").ToString());
        }

        [Fact]
        public void Symbol_ToStringTag_UndefinedIsObjectUndefined()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var tag = Object.prototype.toString.call(undefined);");
            Assert.Equal("[object Undefined]", rt.GetGlobal("tag").ToString());
        }

        [Fact]
        public void Symbol_AsObjectKey_BracketNotation()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var sym = Symbol('key');
                var obj = {};
                obj[sym] = 'symbolValue';
                var val = obj[sym];
            ");
            Assert.Equal("symbolValue", rt.GetGlobal("val").ToString());
        }

        [Fact]
        public void Symbol_Description_Property()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = Symbol('mydesc'); var d = s.description;");
            Assert.Equal("mydesc", rt.GetGlobal("d").ToString());
        }
    }
}
