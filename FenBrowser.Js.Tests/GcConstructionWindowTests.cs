using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Regression cover for the missing-GC-root family reproduced against
// google.com/recaptcha/api2/demo under FEN_FENJS_GC_STRESS=after-every-alloc:
// a builder holds a handle in a CLR local across a later allocation, the
// collection that allocation provokes sweeps the cell, and the surviving
// handle later resolves to a freed-and-reused slot ("Stale heap handle.").
public sealed class GcConstructionWindowTests
{
    // A construction window must not disturb permanent roots pushed while it is
    // open. Permanent intrinsic roots go on the root stack (PushRoot, never
    // popped); if the window marked and popped that same stack, closing it would
    // discard them and the next collection would sweep live intrinsics.
    [Fact]
    public void ConstructionWindow_DoesNotPopPermanentRootsPushedWhileOpen()
    {
        var heap = new JsHeap();
        ObjectHandle permanent;

        using (heap.BeginConstructionWindow())
        {
            permanent = heap.AllocateObject(new JsObject(), AllocationSite.Current());
            heap.PushRoot(permanent);
        }

        heap.CollectGarbage();

        // Resolves only if the root survived the window's close.
        Assert.NotNull(heap.GetObject(permanent));
    }

    // The window keeps every cell allocated inside it alive even though nothing
    // references them yet — the bottom-up host/builtin build pattern.
    [Fact]
    public void ConstructionWindow_KeepsUnreferencedIntermediatesAliveUntilClose()
    {
        var heap = new JsHeap();
        ObjectHandle first;
        ObjectHandle second;

        using (heap.BeginConstructionWindow())
        {
            // `first` is reachable from a CLR local only. Allocating `second`
            // and collecting would sweep it without the window.
            first = heap.AllocateObject(new JsObject(), AllocationSite.Current());
            second = heap.AllocateObject(new JsObject(), AllocationSite.Current());
            heap.CollectGarbage();

            Assert.NotNull(heap.GetObject(first));
            Assert.NotNull(heap.GetObject(second));

            // Link the graph the way a real builder does, then root the top.
            _ = heap.GetObject(second).SetProperty("child", JsValue.FromObject(first));
            heap.WriteBarrier(second, first);
            heap.PushRoot(second);
        }

        heap.CollectGarbage();

        Assert.NotNull(heap.GetObject(second));
        Assert.NotNull(heap.GetObject(first));
    }

    // Boot and the lazy builtin installers must survive a full collection after
    // every single allocation — the mode that reproduced the browser failure.
    [Fact]
    public void BuiltinBootstrap_SurvivesCollectionAfterEveryAllocation()
    {
        var interpreter = new BytecodeInterpreter(new JsHeap(GcStressMode.AfterEveryAlloc));

        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var o = {};
            o.__defineGetter__('x', function () { return 41; });
            var viaAnnexB = o.x + 1;
            var viaCall = (function () { return this.v; }).call({ v: 1 });
            var viaObject = Object.keys({ a: 1, b: 2 }).length;
            viaAnnexB + viaCall + viaObject;
        "));
        new BytecodeVerifier().Verify(fn);

        Assert.Equal(45d, interpreter.Execute(fn).AsNumber());
    }
}
