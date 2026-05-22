using System.Linq;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GlobalConstantsBuiltinTests
{
    private static BuiltinBinding ByName(IReadOnlyList<BuiltinBinding> bindings, string name)
        => bindings.First(b => string.Equals(b.Name, name, StringComparison.Ordinal));

    [Fact]
    public void NameIsStable()
    {
        Assert.Equal("GlobalConstants", new GlobalConstantsBuiltin().Name);
    }

    [Fact]
    public void EmitsNaNInfinityUndefinedWithFrozenAttributes()
    {
        var bindings = new GlobalConstantsBuiltin().GetBindings(new JsHeap());

        Assert.Equal(3, bindings.Count);
        foreach (var name in new[] { "NaN", "Infinity", "undefined" })
        {
            var b = ByName(bindings, name);
            Assert.False(b.Writable);
            Assert.False(b.Enumerable);
            Assert.False(b.Configurable);
        }
    }

    [Fact]
    public void NaNIsActualDoubleNaN()
    {
        var nan = ByName(new GlobalConstantsBuiltin().GetBindings(new JsHeap()), "NaN");
        Assert.True(double.IsNaN(nan.Value.AsNumber()));
    }

    [Fact]
    public void InfinityIsPositiveInfinity()
    {
        var inf = ByName(new GlobalConstantsBuiltin().GetBindings(new JsHeap()), "Infinity");
        Assert.True(double.IsPositiveInfinity(inf.Value.AsNumber()));
    }

    [Fact]
    public void UndefinedIsTheUndefinedValue()
    {
        var u = ByName(new GlobalConstantsBuiltin().GetBindings(new JsHeap()), "undefined");
        Assert.Equal(JsValueTag.Undefined, u.Value.Tag);
    }

    [Fact]
    public void GlobalThisIsOptional()
    {
        var bindings = new GlobalConstantsBuiltin().GetBindings(new JsHeap());
        Assert.DoesNotContain(bindings, b => b.Name == "globalThis");
    }

    [Fact]
    public void GlobalThisIsEmittedWithNonEnumerableAttributesWhenProvided()
    {
        var bindings = new GlobalConstantsBuiltin(JsValue.FromInt32(7)).GetBindings(new JsHeap());
        var g = ByName(bindings, "globalThis");

        Assert.True(g.Writable);
        Assert.False(g.Enumerable);
        Assert.True(g.Configurable);
        Assert.Equal(7, g.Value.AsInt32());
    }

    [Fact]
    public void RejectsNullHeap()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobalConstantsBuiltin().GetBindings(null!));
    }

    [Fact]
    public void IsRegistrableInBuiltinRegistry()
    {
        var registry = new BuiltinRegistry().Register(new GlobalConstantsBuiltin());
        Assert.True(registry.Contains("GlobalConstants"));
    }
}
