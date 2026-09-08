using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The collector finds live objects by tracing frame.Registers. A value that
// lives anywhere else while a collection runs is invisible to it, and the object
// it names is swept while still in use -- a failure that stays silent until a
// later collection dereferences the handle.
//
// Each of these holds an object in a temporary across something that allocates,
// which is exactly the window a promoted register opens. They run with a
// collection before every allocation, so the window is always taken.
public class RegisterRootGcStressTests
{
    private static string RunUnderStress(string source, GcStressMode mode = GcStressMode.BeforeEveryAlloc)
    {
        var heap = new JsHeap(verifyHeapAfterGc: true);
        var interpreter = new BytecodeInterpreter(heap);

        // Boot the intrinsics first: stressing that is slow and tests nothing
        // these are about.
        _ = interpreter.Execute(Compile("1;"));

        heap.SetStressModeForDiagnostics(mode);
        return interpreter.Execute(Compile(source)).AsString();
    }

    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    [Fact]
    public void AnArgumentHeldWhileTheNextOneAllocates_Survives()
    {
        // The first argument sits in a register while the second expression
        // allocates its way through several objects.
        var v = RunUnderStress(@"
            function pair(a, b) { return a.n + b.n; }
            function make(n) { var o = { n: n, pad: { deep: [n, n + 1] } }; return o; }
            String(pair(make(10), make(20)));
        ");
        Assert.Equal("30", v);
    }

    [Fact]
    public void NestedTemporaries_AllStayReachable()
    {
        var v = RunUnderStress(@"
            function join(a, b) { return { n: a.n + b.n }; }
            function make(n) { return { n: n }; }
            String(join(join(make(1), make(2)), join(make(4), make(8))).n);
        ");
        Assert.Equal("15", v);
    }

    [Fact]
    public void AnArrayLiteralOfFreshObjects_KeepsEveryElement()
    {
        // Each element is allocated and parked in a register while the next one
        // allocates, before any of them reaches the array.
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            var list = [make(1), make(2), make(3), make(4), make(5)];
            var total = 0;
            for (var i = 0; i < list.length; i++) total += list[i].n;
            String(total);
        ");
        Assert.Equal("15", v);
    }

    [Fact]
    public void AReceiverHeldAcrossAnAllocatingValueExpression_Survives()
    {
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            var target = { slot: null };
            target.slot = make(7);
            String(target.slot.n);
        ");
        Assert.Equal("7", v);
    }

    [Fact]
    public void AnAccumulatorHeldAcrossLoopAllocation_Survives()
    {
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            var acc = make(0);
            for (var i = 1; i <= 12; i++) { acc = { n: acc.n + make(i).n }; }
            String(acc.n);
        ");
        Assert.Equal("78", v);
    }

    [Fact]
    public void AClosureBuiltBesideAnAllocation_Survives()
    {
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            function build(o) { return function () { return o.n; }; }
            var fns = [build(make(3)), build(make(4))];
            String(fns[0]() * fns[1]());
        ");
        Assert.Equal("12", v);
    }

    [Fact]
    public void ObjectsHeldOnlyInTemporariesAcrossAHotLoop_Survive()
    {
        // Long enough to reach compiled code, so the loop runs with whatever
        // register discipline the compiled body uses; random stress keeps the
        // cost of that survivable.
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            function sum(a, b, c) { return a.n + b.n + c.n; }
            var total = 0;
            for (var i = 0; i < 3000; i++) { total = (total + sum(make(i), make(1), make(2))) % 100000; }
            String(total);
        ", GcStressMode.Random);

        var expected = 0;
        for (var i = 0; i < 3000; i++) expected = (expected + i + 3) % 100000;
        Assert.Equal(expected.ToString(), v);
    }

    [Fact]
    public void AThrownObjectHeldWhileTheHandlerAllocates_Survives()
    {
        var v = RunUnderStress(@"
            function make(n) { return { n: n }; }
            var seen = 0;
            try {
                throw make(5);
            } catch (e) {
                var noise = { a: make(1), b: make(2) };
                seen = e.n + noise.a.n;
            }
            String(seen);
        ");
        Assert.Equal("6", v);
    }
}
