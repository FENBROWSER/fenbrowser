using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class WeakRefTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DerefReturnsTarget()
    {
        Assert.Equal(7d, Run("var t={x:7}; new WeakRef(t).deref().x;").AsNumber());
    }

    [Fact]
    public void DerefSameIdentity()
    {
        Assert.True(Run("var t={}; var r=new WeakRef(t); r.deref() === t;").AsBoolean());
    }

    [Fact]
    public void ConstructorRequiresObjectTarget()
    {
        Assert.Throws<JsThrownException>(() => Run("new WeakRef(5);"));
        Assert.Throws<JsThrownException>(() => Run("new WeakRef();"));
    }

    [Fact]
    public void ConstructorRequiresNew()
    {
        Assert.Throws<JsThrownException>(() => Run("WeakRef({});"));
    }

    [Fact]
    public void DerefOnNonWeakRefThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("WeakRef.prototype.deref.call({});"));
    }

    [Fact]
    public void WeakTargetCallbackFires_WhenTargetIsCollected()
    {
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);

        // Allocate a target and a WeakRef-style owner; only the owner is rooted.
        var target = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(owner);

        var callbackFired = false;
        heap.RegisterWeakTarget(owner.Index, target, () => callbackFired = true);

        // Target is unrooted -> collected in a major GC.
        heap.CollectGarbage();

        Assert.True(callbackFired, "Weak-target callback should fire when the target is collected.");
        Assert.Equal(1, heap.WeakTargetCallbacksFired);
        Assert.Throws<JsEngineFatalException>(() => heap.GetObject(target));
        Assert.NotNull(heap.GetObject(owner));
    }

    [Fact]
    public void WeakTargetCallback_DoesNotFire_WhenTargetIsRooted()
    {
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);

        var target = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(owner);
        heap.PushRoot(target);

        var callbackFired = false;
        heap.RegisterWeakTarget(owner.Index, target, () => callbackFired = true);

        heap.CollectGarbage();

        Assert.False(callbackFired, "Weak-target callback must not fire while the target is reachable.");
        Assert.Equal(0, heap.WeakTargetCallbacksFired);
        Assert.NotNull(heap.GetObject(target));
    }

    [Fact]
    public void WeakTargetCallback_DoesNotFire_WhenOwnerIsCollected()
    {
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);

        var target = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        var owner = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(target);

        var callbackFired = false;
        heap.RegisterWeakTarget(owner.Index, target, () => callbackFired = true);

        heap.CollectGarbage();

        Assert.False(callbackFired, "Weak-target callback must not fire when the owning cell is gone.");
        Assert.NotNull(heap.GetObject(target));
    }

    [Fact]
    public void DerefReturnsUndefined_AfterGcCollectsTarget()
    {
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);

        // Build a WeakRef through the interpreter and drop all strong refs to the target.
        var fn = new BytecodeCompiler().CompileScript(new SourceText(
            "var wr; (function(){ var t = {}; wr = new WeakRef(t); })();"));
        new BytecodeVerifier().Verify(fn);
        interpreter.Execute(fn);

        var derefFn = new BytecodeCompiler().CompileScript(new SourceText(
            "wr.deref() === undefined;"));
        new BytecodeVerifier().Verify(derefFn);

        // First: target still alive (the local in the closure may be gone, but the
        // WeakRef target must be cleared after collection).
        heap.CollectGarbage();

        Assert.True(interpreter.Execute(derefFn).AsBoolean(),
            "deref() must return undefined after the target is collected.");
    }
}
