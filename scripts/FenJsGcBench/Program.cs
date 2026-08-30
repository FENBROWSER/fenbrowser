using System;
using System.Linq;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

// Deterministic reproduction of the reCAPTCHA worker-turn hot shape:
//   MessagePort handler invocation -> promise/microtask chain ->
//   array map with closures -> sustained allocation/GC pressure.
// Mirrors browser heap cadence defaults (minor GC every 4096 young
// allocations, major every 32 minors, deferred-to-safe-point collection).

var gcOff = args.Contains("--gc-off");
var stress = args.Contains("--stress");

var heap = new JsHeap(stress ? GcStressMode.Random : GcStressMode.None);
if (gcOff)
{
    heap.YoungAllocationsPerMinorGc = int.MaxValue;
}

var interpreter = new BytecodeInterpreter(heap) { InstructionBudget = int.MaxValue };
if (args.Contains("--stress-after-boot"))
{
    // Stress collections after construction only: Random stress during
    // bootstrap sweeps unrooted builtin-construction handles (pre-existing,
    // see SetStressModeForDiagnostics contract). Internal API -> reflection.
    typeof(JsHeap)
        .GetMethod("SetStressModeForDiagnostics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        !.Invoke(heap, new object[] { GcStressMode.Random });
}

const string setup = """
var __listeners = [];
function addListener(fn) { __listeners.push(fn); }

function makePayload(i) {
    return { id: i, tag: "v" + i, nested: { a: i, b: "n" + i } };
}

// Long-lived state: mimics a page accumulating listeners/bound closures that
// survive nursery collections, promote to Old, and sit behind sticky cards.
globalThis.__keep = [];

// One MessagePort delivery: handler -> promise chain (microtasks) ->
// array map with closure callbacks -> allocation churn.
globalThis.__deliver = function (msg) {
    return Promise.resolve(msg).then(function (m) {
        var arr = [];
        for (var i = 0; i < 64; i++) {
            arr.push(makePayload(i));
        }
        var mapped = arr.map(function (item) {
            var local = item.tag;
            var fn = function () { return item.id + local.length; };
            if (msg % 4 === 0) {
                __keep.push(fn.bind(null, item));
                __keep.push(function () { return item; });
                // mimic addEventListener with fresh closures on long-lived objects
                var longLived = __keep[0] || { tag: "ll" };
                longLived["on" + msg] = function () { return item.id; };
                addListener(function () { return msg; });
            }
            return fn;
        });
        var sum = 0;
        for (var j = 0; j < mapped.length; j++) {
            sum = sum + mapped[j]();
        }
        return Promise.resolve(sum).then(function (s) {
            return Promise.resolve(s + 1).then(function (s2) {
                return s2 + m;
            });
        });
    });
};
""";

var fn = new BytecodeCompiler().CompileScript(new SourceText(setup));
new BytecodeVerifier().Verify(fn);
using (args.Contains("--stress-after-boot") ? heap.BeginConstructionWindow() : null)
{
    interpreter.Execute(fn);
}

var deliver = interpreter.ReadGlobalValueOrUndefined("__deliver");
if (deliver.Tag != JsValueTag.Object)
{
    Console.Error.WriteLine("__deliver missing");
    return 1;
}

const int deliveries = 1200;

// Warm up JIT/IC state like the browser page does, excluded from measurement.
// Under stress-after-boot, the first execution lazily installs globals and
// builtins — pin that construction window exactly like host builders do.
using (args.Contains("--stress-after-boot") ? heap.BeginConstructionWindow() : null)
{
    RunDeliveries(interpreter, deliver, 1);
}
RunDeliveries(interpreter, deliver, 19);

var instructionsBefore = (long)interpreter.InstructionsExecuted;
var allocBefore = heap.AllocationCount;
var minorBefore = heap.MinorCollectionCount;
var majorBefore = heap.GcCollectionCount;

var sw = System.Diagnostics.Stopwatch.StartNew();
var latencies = new double[deliveries];
var useBoundaryGc = stress || args.Contains("--boundary-gc");
var lastBoundary = heap.AllocationCount;
var minorPerChunk = new long[deliveries / 100];
for (var i = 0; i < deliveries; i++)
{
    var t0 = sw.Elapsed.TotalMilliseconds;
    if (useBoundaryGc && heap.AllocationCount - lastBoundary >= 4096)
    {
        lastBoundary = heap.AllocationCount;
        heap.CollectGarbage();
    }
    RunDeliveries(interpreter, deliver, 1);
    latencies[i] = sw.Elapsed.TotalMilliseconds - t0;
    if ((i + 1) % 100 == 0)
    {
        var chunk = (i + 1) / 100 - 1;
        minorPerChunk[chunk] = heap.MinorCollectionCount - minorBefore;
    }
}
sw.Stop();

var instructionsAfter = (long)interpreter.InstructionsExecuted;
var allocAfter = heap.AllocationCount;
var minorAfter = heap.MinorCollectionCount;
var majorAfter = heap.GcCollectionCount;

var label = gcOff ? "gc-off " : "default";
var seconds = sw.Elapsed.TotalSeconds;
Console.WriteLine(
    $"[{label}] deliveries={deliveries} elapsedMs={sw.ElapsedMilliseconds:F0} " +
    $"instructions={instructionsAfter - instructionsBefore} instrPerSec={(instructionsAfter - instructionsBefore) / seconds:N0} " +
    $"allocations={allocAfter - allocBefore} minorGc={minorAfter - minorBefore} majorGc={majorAfter - majorBefore} " +
    $"dirtyEdgesAtEnd={heap.RememberedSetEdgeCount} liveCells={heap.LiveCellCount}");

var sorted = (double[])latencies.Clone();
Array.Sort(sorted);
Console.WriteLine(
    $"[{label}] latencyMs p50={sorted[deliveries / 2]:F3} p90={sorted[(int)(deliveries * 0.9)]:F3} " +
    $"p99={sorted[(int)(deliveries * 0.99)]:F3} max={sorted[^1]:F3}");
for (var c = 0; c < minorPerChunk.Length; c++)
{
    var slice = new double[100];
    Array.Copy(latencies, c * 100, slice, 0, 100);
    Array.Sort(slice);
    Console.WriteLine(
        $"[{label}] chunk{c:D2} medianMs={slice[50]:F3} minorsSoFar={minorPerChunk[c]} majorSoFar={heap.GcCollectionCount - majorBefore}");
}
return 0;

static void RunDeliveries(BytecodeInterpreter interpreter, JsValue deliver, int count)
{
    for (var i = 0; i < count; i++)
    {
        interpreter.InvokeFunction(
            deliver,
            new[] { JsValue.FromInt32(i) },
            JsValue.Undefined);
        interpreter.PumpMicrotasks();
    }
}

