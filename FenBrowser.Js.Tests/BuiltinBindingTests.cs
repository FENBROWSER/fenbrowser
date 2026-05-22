using FenBrowser.Js.Builtins;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BuiltinBindingTests
{
    [Fact]
    public void DefaultsMatchStandardBuiltinPropertyShape()
    {
        var binding = new BuiltinBinding("X", JsValue.FromInt32(1));

        Assert.Equal("X", binding.Name);
        Assert.Equal(1, binding.Value.AsInt32());
        Assert.True(binding.Writable);
        Assert.True(binding.Enumerable);
        Assert.True(binding.Configurable);
    }

    [Fact]
    public void NonEnumerableFactoryMatchesSpecBuiltinDefaults()
    {
        var binding = BuiltinBinding.NonEnumerable("Math", JsValue.FromInt32(0));

        Assert.True(binding.Writable);
        Assert.False(binding.Enumerable);
        Assert.True(binding.Configurable);
    }

    [Fact]
    public void FrozenFactoryMatchesNaNInfinityUndefinedAttributes()
    {
        // ECMA-262 19.1.1.1-3: NaN, Infinity, undefined are {[[Writable]]: false,
        // [[Enumerable]]: false, [[Configurable]]: false}.
        var binding = BuiltinBinding.Frozen("Infinity", JsValue.FromNumber(double.PositiveInfinity));

        Assert.False(binding.Writable);
        Assert.False(binding.Enumerable);
        Assert.False(binding.Configurable);
        Assert.True(double.IsPositiveInfinity(binding.Value.AsNumber()));
    }

    [Fact]
    public void RecordEqualityComparesAllFields()
    {
        var a = new BuiltinBinding("k", JsValue.FromInt32(1), Writable: true, Enumerable: false, Configurable: true);
        var b = new BuiltinBinding("k", JsValue.FromInt32(1), Writable: true, Enumerable: false, Configurable: true);
        var c = new BuiltinBinding("k", JsValue.FromInt32(2), Writable: true, Enumerable: false, Configurable: true);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
