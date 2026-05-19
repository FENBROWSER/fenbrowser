using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RuntimeHeapTests
{
    [Fact]
    public void JsValuePreservesTagsAndPayloads()
    {
        Assert.NotEqual(JsValue.Undefined.Tag, JsValue.Null.Tag);

        var boolean = JsValue.FromBoolean(true);
        Assert.Equal(JsValueTag.Boolean, boolean.Tag);
        Assert.True(boolean.AsBoolean());

        var intValue = JsValue.FromInt32(-42);
        Assert.Equal(JsValueTag.Int32, intValue.Tag);
        Assert.Equal(-42, intValue.AsInt32());

        var number = JsValue.FromNumber(-0.0d);
        Assert.Equal(JsValueTag.Number, number.Tag);
        Assert.Equal(double.NegativeZero, number.AsNumber());
    }

    [Fact]
    public void ObjectAndHostHandlesRoundTripThroughJsValue()
    {
        var objectHandle = new ObjectHandle(7, 3);
        var objectValue = JsValue.FromObject(objectHandle);
        Assert.Equal(objectHandle, objectValue.AsObjectHandle());

        var hostHandle = new HostObjectHandle(8, 5, 12, 99);
        var hostValue = JsValue.FromHostObject(hostHandle);
        var decoded = hostValue.AsHostObjectHandle();
        Assert.Equal(hostHandle, decoded);
    }

    [Fact]
    public void StringAndSymbolHandlesRoundTripEncodedPayload()
    {
        var stringHandle = new StringHandle(11, 2);
        var stringDecoded = StringHandle.FromInt64(stringHandle.ToInt64());
        Assert.Equal(stringHandle, stringDecoded);

        var symbolHandle = new SymbolHandle(17, 4);
        var symbolDecoded = SymbolHandle.FromInt64(symbolHandle.ToInt64());
        Assert.Equal(symbolHandle, symbolDecoded);
    }

    [Fact]
    public void HostObjectHandleRejectsOverflowingParts()
    {
        var tooLarge = new HostObjectHandle(ushort.MaxValue + 1, 1, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => tooLarge.ToInt64());
    }

    [Fact]
    public void HeapAllocatesAndResolvesObjectHandles()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var obj = heap.GetObject(handle);

        Assert.NotNull(obj);
        Assert.Equal(0, handle.Index);
        Assert.Equal(1, handle.Generation);
    }

    [Fact]
    public void CollectGarbageSweepsUnrootedObjects()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.CollectGarbage();

        Assert.Equal(1, heap.GcCollectionCount);
        Assert.Equal(0, heap.LastGcMarkedCells);
        Assert.Equal(1, heap.LastGcSweptCells);
        Assert.Equal(0, heap.LiveCellCount);
        Assert.Throws<JsEngineFatalException>(() => heap.GetObject(handle));
    }

    [Fact]
    public void CollectGarbageKeepsRootedObjectsAlive()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(handle);
        heap.CollectGarbage();

        var obj = heap.GetObject(handle);
        Assert.NotNull(obj);
        Assert.Equal(1, heap.GcCollectionCount);
        Assert.Equal(1, heap.LastGcMarkedCells);
        Assert.Equal(0, heap.LastGcSweptCells);
        Assert.Equal(1, heap.LiveCellCount);
    }

    [Fact]
    public void HeapDetectsStaleHandleAfterFree()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(handle);

        Assert.Throws<JsEngineFatalException>(() => heap.GetObject(handle));
    }

    [Fact]
    public void ReallocatedFreedSlotGetsNewGeneration()
    {
        var heap = new JsHeap();
        var oldHandle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(oldHandle);
        var newHandle = heap.AllocateObject(new JsObject(), AllocationSite.Current());

        Assert.Equal(oldHandle.Index, newHandle.Index);
        Assert.True(newHandle.Generation > oldHandle.Generation);
        Assert.Throws<JsEngineFatalException>(() => heap.GetObject(oldHandle));
    }

    [Fact]
    public void HandleScopeRootsAndUnrootsHandles()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());

        Assert.Equal(0, heap.RootCount);
        using (var scope = new HandleScope(heap))
        {
            _ = scope.Create(handle);
            Assert.Equal(1, heap.RootCount);
        }

        Assert.Equal(0, heap.RootCount);
    }

    [Fact]
    public void HeapVerifierRejectsInvalidRoots()
    {
        var heap = new JsHeap();
        var verifier = new HeapVerifier();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());

        heap.PushRoot(handle);
        verifier.Verify(heap);

        heap.FreeForTest(handle);
        Assert.Throws<JsEngineFatalException>(() => verifier.Verify(heap));
    }

    [Fact]
    public void HeapVerifierRejectsStaleTracedPropertyHandle()
    {
        var heap = new JsHeap();
        var verifier = new HeapVerifier();
        var child = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var owner = new JsObject();
        _ = owner.SetProperty("child", JsValue.FromObject(child));
        _ = heap.AllocateObject(owner, AllocationSite.Current());

        verifier.Verify(heap);
        heap.FreeForTest(child);

        Assert.Throws<JsEngineFatalException>(() => verifier.Verify(heap));
    }

    [Fact]
    public void HeapVerifierRejectsStaleWriteBarrierEdges()
    {
        var heap = new JsHeap();
        var verifier = new HeapVerifier();
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var child = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.WriteBarrier(owner, child);

        verifier.Verify(heap);
        heap.FreeForTest(child);

        Assert.Throws<JsEngineFatalException>(() => verifier.Verify(heap));
    }

    [Fact]
    public void WriteBarrierRejectsStaleOwnerHandle()
    {
        var heap = new JsHeap();
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var child = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(owner);

        Assert.Throws<JsEngineFatalException>(() => heap.WriteBarrier(owner, child));
    }

    [Fact]
    public void WriteBarrierRejectsStaleChildHandle()
    {
        var heap = new JsHeap();
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var child = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(child);

        Assert.Throws<JsEngineFatalException>(() => heap.WriteBarrier(owner, child));
    }

    [Fact]
    public void IsolateAllocatesObjectsInsideHandleScope()
    {
        var isolate = new JsIsolate(new JsHeap());
        Assert.Equal(0, isolate.Heap.RootCount);

        using (var scope = isolate.EnterHandleScope())
        {
            var rooted = isolate.AllocateObjectInScope(scope, new JsObject(), AllocationSite.Current());
            Assert.Equal(1, isolate.Heap.RootCount);
            Assert.Equal(0, rooted.Value.Index);
        }

        Assert.Equal(0, isolate.Heap.RootCount);
    }

    [Fact]
    public void InterpreterExecutionEntrypointsAreMarkedMayExecuteJs()
    {
        var publicExecute = typeof(BytecodeInterpreter).GetMethod(nameof(BytecodeInterpreter.Execute));
        Assert.NotNull(publicExecute);
        Assert.NotNull(publicExecute!.GetCustomAttributes(typeof(MayExecuteJsAttribute), inherit: false).SingleOrDefault());

        var executeInternal = typeof(BytecodeInterpreter).GetMethod("ExecuteInternal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(executeInternal);
        Assert.NotNull(executeInternal!.GetCustomAttributes(typeof(MayExecuteJsAttribute), inherit: false).SingleOrDefault());

        var executeConstruct = typeof(BytecodeInterpreter).GetMethod("ExecuteConstruct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(executeConstruct);
        Assert.NotNull(executeConstruct!.GetCustomAttributes(typeof(MayExecuteJsAttribute), inherit: false).SingleOrDefault());
    }
}
