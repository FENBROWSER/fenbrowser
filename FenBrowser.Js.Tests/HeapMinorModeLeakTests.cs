using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

// A minor collection marks with old-generation traversal disabled. If an
// exception escapes MinorCollect while that mode is set, the flag must not
// survive: the next major collection would otherwise leave every Old cell
// unmarked and sweep the whole old generation, turning one recoverable fault
// into heap-wide "Stale heap handle." fatals.
public sealed class HeapMinorModeLeakTests
{
    private sealed class HandleRootSource : IHeapRootSource
    {
        private readonly ObjectHandle _handle;

        public HandleRootSource(ObjectHandle handle) => _handle = handle;

        public void TraceRoots(IHeapTracer tracer) => tracer.Trace(_handle);
    }

    private sealed class ThrowingRootSource : IHeapRootSource
    {
        public void TraceRoots(IHeapTracer tracer) =>
            throw new InvalidOperationException("root source failure during marking");
    }

    [Fact]
    public void MajorCollect_KeepsOldGeneration_AfterMinorCollectThrows()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.AddRootSource(new HandleRootSource(handle));

        // PromotionThreshold surviving minor collections promote the cell to Old.
        for (var i = 0; i < heap.PromotionThreshold + 1; i++)
        {
            heap.MinorCollect();
        }

        Assert.True(heap.IsLiveObjectHandle(handle), "Rooted object died during minor collection.");

        var thrower = new ThrowingRootSource();
        heap.AddRootSource(thrower);
        Assert.Throws<InvalidOperationException>(heap.MinorCollect);
        Assert.True(heap.RemoveRootSource(thrower));

        heap.CollectGarbage();

        Assert.True(
            heap.IsLiveObjectHandle(handle),
            "Major collection swept a rooted old-generation cell; minor-traversal mode leaked out of the failed MinorCollect.");
    }
}
