using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JsObjectBindingAdapterTests
{
    [Fact]
    public void HasPropertyAndTryGetSeeOwnDataProperties()
    {
        var (heap, handle, obj) = NewBackedObject();
        obj.DefineOwnProperty("x", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(42),
            Writable: true,
            Enumerable: true,
            Configurable: true));
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.True(adapter.HasProperty("x"));
        Assert.True(adapter.TryGet("x", out var value));
        Assert.Equal(42, value.AsInt32());
    }

    [Fact]
    public void HasPropertyWalksPrototypeChain()
    {
        var (heap, childHandle, child) = NewBackedObject();
        var (parentHandle, parent) = AllocateObject(heap);
        parent.DefineOwnProperty("inherited", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(7),
            Writable: true,
            Enumerable: true,
            Configurable: true));
        child.SetPrototype(parentHandle);
        var adapter = new JsObjectBindingAdapter(heap, childHandle);

        Assert.True(adapter.HasProperty("inherited"));
        Assert.True(adapter.TryGet("inherited", out var value));
        Assert.Equal(7, value.AsInt32());
    }

    [Fact]
    public void TryGetReturnsFalseForAccessorProperty()
    {
        // Accessor get requires running the getter, which the adapter cannot do
        // without an interpreter. It must surface "not readable" so the env-record
        // can apply spec-correct semantics rather than silently returning undefined
        // as a real data value.
        var (heap, handle, obj) = NewBackedObject();
        obj.DefineOwnProperty("g", JsPropertyDescriptor.Accessor(
            Get: JsValue.Undefined,
            Set: JsValue.Undefined,
            Enumerable: true,
            Configurable: true));
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.True(adapter.HasProperty("g"));
        Assert.False(adapter.TryGet("g", out _));
    }

    [Fact]
    public void TrySetWritesDataProperty()
    {
        var (heap, handle, _) = NewBackedObject();
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.True(adapter.TrySet("x", JsValue.FromInt32(11)));
        Assert.True(adapter.TryGet("x", out var value));
        Assert.Equal(11, value.AsInt32());
    }

    [Fact]
    public void TrySetReturnsFalseForNonWritableProperty()
    {
        var (heap, handle, obj) = NewBackedObject();
        obj.DefineOwnProperty("frozen", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(1),
            Writable: false,
            Enumerable: true,
            Configurable: true));
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.False(adapter.TrySet("frozen", JsValue.FromInt32(99)));
        adapter.TryGet("frozen", out var current);
        Assert.Equal(1, current.AsInt32());
    }

    [Fact]
    public void DefineMutableDataInstallsConfigurableProperty()
    {
        var (heap, handle, obj) = NewBackedObject();
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.True(adapter.DefineMutableData("y", JsValue.FromInt32(5), deletable: true));
        Assert.True(obj.TryGetOwnProperty("y", out var descriptor));
        Assert.True(descriptor.Writable);
        Assert.True(descriptor.Configurable);
        Assert.Equal(5, descriptor.Value.AsInt32());
    }

    [Fact]
    public void DeletePropertyRemovesConfigurableEntry()
    {
        var (heap, handle, obj) = NewBackedObject();
        obj.DefineOwnProperty("k", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(1),
            Writable: true,
            Enumerable: true,
            Configurable: true));
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.True(adapter.DeleteProperty("k"));
        Assert.False(adapter.HasProperty("k"));
    }

    [Fact]
    public void DeletePropertyReturnsFalseForNonConfigurableEntry()
    {
        var (heap, handle, obj) = NewBackedObject();
        obj.DefineOwnProperty("permanent", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(1),
            Writable: true,
            Enumerable: true,
            Configurable: false));
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.False(adapter.DeleteProperty("permanent"));
        Assert.True(adapter.HasProperty("permanent"));
    }

    [Fact]
    public void AsObjectHandleReturnsTheUnderlyingHandle()
    {
        var (heap, handle, _) = NewBackedObject();
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.Equal(handle, adapter.AsObjectHandle);
    }

    [Fact]
    public void AdapterReResolvesHandleSoMutationsAreVisible()
    {
        // Adapter holds no cached JsObject reference: a mutation made through the
        // heap between calls is visible on the next read. This protects callers
        // against the [MayExecuteJs] reentrancy rule (no cached object state
        // survives across user-JS calls).
        var (heap, handle, obj) = NewBackedObject();
        var adapter = new JsObjectBindingAdapter(heap, handle);

        Assert.False(adapter.HasProperty("late"));

        // Direct mutation via the heap, bypassing the adapter.
        var sameObj = heap.GetObject(handle);
        Assert.Same(obj, sameObj);
        sameObj.DefineOwnProperty("late", new JsPropertyDescriptor(
            Value: JsValue.FromInt32(99),
            Writable: true,
            Enumerable: true,
            Configurable: true));

        Assert.True(adapter.HasProperty("late"));
        Assert.True(adapter.TryGet("late", out var value));
        Assert.Equal(99, value.AsInt32());
    }

    [Fact]
    public void AdapterDrivesObjectEnvironmentRecord()
    {
        // End-to-end seam check: the adapter, when handed to an
        // ObjectEnvironmentRecord, drives the routing the interpreter will rely on
        // in B.6.5. CreateMutableBinding, InitializeBinding, and GetBindingValue
        // round-trip through real JsObject storage.
        var (heap, handle, _) = NewBackedObject();
        var adapter = new JsObjectBindingAdapter(heap, handle);
        var env = new ObjectEnvironmentRecord(adapter, isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.Ok, env.CreateMutableBinding("x", deletable: false));
        Assert.Equal(BindingOpResult.Ok, env.InitializeBinding("x", JsValue.FromInt32(7)));
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("x", strict: true, out var value));
        Assert.Equal(7, value.AsInt32());
    }

    private static (JsHeap heap, ObjectHandle handle, JsObject obj) NewBackedObject()
    {
        var heap = new JsHeap();
        var (handle, obj) = AllocateObject(heap);
        return (heap, handle, obj);
    }

    private static (ObjectHandle handle, JsObject obj) AllocateObject(JsHeap heap)
    {
        var obj = new JsObject();
        var handle = heap.AllocateObject(obj, AllocationSite.Current());
        return (handle, obj);
    }
}
