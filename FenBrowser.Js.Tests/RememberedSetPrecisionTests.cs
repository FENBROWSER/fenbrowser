using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Focused GC correctness tests for the precise remembered set. Environment
// records are not heap cells, so object-valued binding stores register the
// record in the heap's remembered-environment set; internal-slot stores on
// Old cells (promise reactions/results, generator and async-context suspend
// state) emit write barriers. Together these replace the former blanket
// sticky-card rescans of the entire Old population.
public sealed class RememberedSetPrecisionTests
{
    private static (BytecodeInterpreter Interpreter, JsHeap Heap) CreateEngine()
    {
        var heap = new JsHeap
        {
            YoungAllocationsPerMinorGc = 0,
            PromotionThreshold = 1
        };
        var interpreter = new BytecodeInterpreter(heap);
        return (interpreter, heap);
    }

    private static JsValue Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }

    [Fact]
    public void ClosureBindingStoreKeepsYoungObjectAliveAcrossMinorCollection()
    {
        var (interpreter, heap) = CreateEngine();

        Run(interpreter, """
            var __get;
            var __set;
            function makeCell() {
                var captured = null;
                __set = function (value) { captured = value; };
                __get = function () { return captured; };
            }
            makeCell();
            """);

        // Promote the closure cells to Old.
        heap.MinorCollect();

        // Store a fresh Young object through the Old closure's shared
        // environment — an old→young edge carried only by the binding.
        Run(interpreter, "__set({ marker: 42 });");
        heap.MinorCollect();

        // Reading the binding must observe the surviving object, not a stale
        // handle.
        var marker = Run(interpreter, "__get().marker;");
        Assert.Equal(42, marker.AsNumber());
    }

    [Fact]
    public void RememberedEnvironmentSelfCleansOnceBindingsHoldNoYoungCells()
    {
        var (interpreter, heap) = CreateEngine();

        Run(interpreter, """
            var __set;
            var __keep;
            function makeCell() {
                var captured = null;
                __set = function (value) { captured = value; };
                __keep = function () { return captured; };
            }
            makeCell();
            """);
        heap.MinorCollect();

        Run(interpreter, "__set({ fresh: true });");
        heap.MinorCollect();
        Assert.True(heap.RememberedEnvironmentCount > 0);

        // The stored object survives this collection and is promoted on the
        // next one; once no Old closure's bindings reference Young cells, the
        // remembered-environment set must drain (the perf property that keeps
        // long-running MessagePort/promise workloads fast).
        heap.MinorCollect();
        var marker = Run(interpreter, "__keep().fresh;");
        Assert.True(marker.AsBoolean());
        heap.MinorCollect();
        Assert.Equal(0, heap.RememberedEnvironmentCount);
    }

    [Fact]
    public void OldPromiseKeepsYoungReactionAndResultAliveAcrossMinorCollection()
    {
        var (interpreter, heap) = CreateEngine();

        Run(interpreter, "var __settled = 0; var __p = new Promise(function (resolve) { __resolve = resolve; });");
        heap.MinorCollect();

        // Attach a fresh Young reaction to the now-Old promise, then let the
        // stored reaction survive a minor collection before settlement.
        Run(interpreter, "__p.then(function (value) { __settled = value; });");
        heap.MinorCollect();
        heap.MinorCollect();

        Run(interpreter, "__resolve(7);");
        interpreter.PumpMicrotasks();

        var settled = Run(interpreter, "__settled;");
        Assert.Equal(7, settled.AsNumber());
    }

    [Fact]
    public void OldGeneratorSuspendKeepsYoungRegisterValuesAliveAcrossMinorCollection()
    {
        var (interpreter, heap) = CreateEngine();

        Run(interpreter, """
            function* __gen() {
                var received = yield 1;
                yield 42;
                return received.payload;
            }
            var __g = __gen();
            __g.next();
            """);

        // Promote the generator object to Old while suspended.
        heap.MinorCollect();

        // Resume with a fresh Young object. The generator stores it in its
        // registers/binding and suspends again at the second yield.
        Run(interpreter, "__g.next({ payload: 99 });");
        // The Young object must survive a minor collection while the only
        // reference is the Old suspended generator's saved state.
        heap.MinorCollect();

        // Resuming reads the saved value through the generator's registers.
        var payload = Run(interpreter, "__g.next().value;");
        Assert.Equal(99, payload.AsNumber());
    }

    [Fact]
    public void OldIteratorHelperKeepsFreshInnerIteratorAliveAcrossMinorCollection()
    {
        var (interpreter, heap) = CreateEngine();

        Run(interpreter, """
            var __it = Iterator.from([0, 1, 2]).flatMap(function (v) {
                return [{ n: v }];
            });
            __it.next();
            """);

        // Promote the helper object to Old while suspended mid-iteration.
        heap.MinorCollect();

        // next() exhausts the first inner iterator and stores a fresh Young
        // inner iterator record into the Old helper.
        var second = Run(interpreter, "__it.next().value.n;");
        Assert.Equal(1, second.AsNumber());

        // The fresh inner record must survive a minor collection held only by
        // the Old helper's internal slots (write barrier).
        heap.MinorCollect();

        var third = Run(interpreter, "__it.next().value.n;");
        Assert.Equal(2, third.AsNumber());
    }

    [Fact]
    public void OldAsyncContextSuspendKeepsYoungRegisterValuesAliveAcrossMinorCollection()
    {
        var (interpreter, heap) = CreateEngine();

        // An async function that suspends at await with a Young object live in
        // its registers. The awaited promise resolves only after a minor
        // collection, so the resume path reads registers restored across GC.
        Run(interpreter, """
            var __resume;
            var __got = 0;
            var __awaited = new Promise(function (resolve) { __resume = resolve; });
            async function __run() {
                var held = { payload: 77 };
                await __awaited;
                return held.payload;
            }
            var __result = __run();
            """);
        heap.MinorCollect();

        Run(interpreter, "__resume(true);");
        Run(interpreter, "__result.then(function (value) { __got = value; });");
        interpreter.PumpMicrotasks();

        var payload = Run(interpreter, "__got;");
        Assert.Equal(77, payload.AsNumber());
    }
}
