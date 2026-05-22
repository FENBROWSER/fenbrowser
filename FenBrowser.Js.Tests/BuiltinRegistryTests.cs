using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BuiltinRegistryTests
{
    private sealed class FakeModule(string name, params BuiltinBinding[] bindings) : IBuiltinModule
    {
        public int MaterializeCalls { get; private set; }

        public string Name { get; } = name;

        public IReadOnlyList<BuiltinBinding> GetBindings(JsHeap heap)
        {
            MaterializeCalls++;
            return bindings;
        }
    }

    [Fact]
    public void RegisterAddsModuleAndPreservesOrder()
    {
        var registry = new BuiltinRegistry();
        var a = new FakeModule("A");
        var b = new FakeModule("B");

        registry.Register(a).Register(b);

        Assert.Equal(2, registry.Modules.Count);
        Assert.Same(a, registry.Modules[0]);
        Assert.Same(b, registry.Modules[1]);
    }

    [Fact]
    public void DuplicateRegistrationThrows()
    {
        var registry = new BuiltinRegistry();
        registry.Register(new FakeModule("Dup"));

        Assert.Throws<ArgumentException>(() => registry.Register(new FakeModule("Dup")));
    }

    [Fact]
    public void RegisterRejectsNullModule()
    {
        var registry = new BuiltinRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void RegisterRejectsEmptyName()
    {
        var registry = new BuiltinRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeModule(string.Empty)));
    }

    [Fact]
    public void ContainsReturnsTrueForRegisteredName()
    {
        var registry = new BuiltinRegistry();
        registry.Register(new FakeModule("Math"));

        Assert.True(registry.Contains("Math"));
        Assert.False(registry.Contains("Date"));
    }

    [Fact]
    public void MaterializeFlattensBindingsInOrder()
    {
        var registry = new BuiltinRegistry();
        registry.Register(new FakeModule("A", new BuiltinBinding("a1", JsValue.FromInt32(1))));
        registry.Register(new FakeModule("B",
            new BuiltinBinding("b1", JsValue.FromInt32(2)),
            new BuiltinBinding("b2", JsValue.FromInt32(3))));

        var bindings = registry.Materialize(new JsHeap());

        Assert.Equal(3, bindings.Count);
        Assert.Equal("a1", bindings[0].Name);
        Assert.Equal("b1", bindings[1].Name);
        Assert.Equal("b2", bindings[2].Name);
    }

    [Fact]
    public void MaterializeCallsEachModuleExactlyOnce()
    {
        var registry = new BuiltinRegistry();
        var a = new FakeModule("A", new BuiltinBinding("a1", JsValue.FromInt32(1)));
        var b = new FakeModule("B", new BuiltinBinding("b1", JsValue.FromInt32(2)));
        registry.Register(a).Register(b);

        _ = registry.Materialize(new JsHeap());

        Assert.Equal(1, a.MaterializeCalls);
        Assert.Equal(1, b.MaterializeCalls);
    }

    [Fact]
    public void MaterializeRejectsNullHeap()
    {
        var registry = new BuiltinRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Materialize(null!));
    }
}
