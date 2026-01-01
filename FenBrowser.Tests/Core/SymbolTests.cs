using System;
using System.Collections.Generic;
using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.Tests.Core
{
    public class SymbolTests
    {
        [Fact]
        public void Symbol_Uniqueness()
        {
            var s1 = new FenSymbol("test");
            var s2 = new FenSymbol("test");

            // Same description should NOT make them equal
            Assert.False(s1.Equals(s2));
            Assert.False(ReferenceEquals(s1, s2));
        }

        [Fact]
        public void Symbol_For_ReturnsSameSymbol()
        {
            var s1 = FenSymbol.For("sharedKey");
            var s2 = FenSymbol.For("sharedKey");

            // Symbol.for should return the same symbol for the same key
            Assert.True(ReferenceEquals(s1, s2));
        }

        [Fact]
        public void Symbol_KeyFor_ReturnsKey()
        {
            var sym = FenSymbol.For("myKey");
            var key = FenSymbol.KeyFor(sym);

            Assert.Equal("myKey", key);
        }

        [Fact]
        public void Symbol_KeyFor_ReturnsNullForNonRegistered()
        {
            var sym = new FenSymbol("not-registered");
            var key = FenSymbol.KeyFor(sym);

            Assert.Null(key);
        }

        [Fact]
        public void Symbol_WellKnownSymbols_Exist()
        {
            Assert.NotNull(FenSymbol.Iterator);
            Assert.NotNull(FenSymbol.ToStringTag);
            Assert.NotNull(FenSymbol.HasInstance);
            Assert.NotNull(FenSymbol.ToPrimitive);
            Assert.NotNull(FenSymbol.AsyncIterator);
        }

        [Fact]
        public void Symbol_ToString_IncludesDescription()
        {
            var sym = new FenSymbol("myDescription");
            Assert.Equal("Symbol(myDescription)", sym.ToString());
        }

        [Fact]
        public void Symbol_IValue_Properties()
        {
            var sym = new FenSymbol("test");
            IValue val = sym;

            Assert.True(val.IsObject);
            Assert.False(val.IsFunction);
            Assert.False(val.IsUndefined);
            Assert.True(val.ToBoolean()); // Symbols are truthy
            Assert.True(double.IsNaN(val.ToNumber()));
        }

        [Fact]
        public void SymbolConstructor_CreatesObject()
        {
            var ctor = FenSymbol.CreateSymbolConstructor();
            
            Assert.NotNull(ctor);
            Assert.NotNull(ctor.Get("for"));
            Assert.NotNull(ctor.Get("keyFor"));
            Assert.NotNull(ctor.Get("iterator"));
        }
    }
}
