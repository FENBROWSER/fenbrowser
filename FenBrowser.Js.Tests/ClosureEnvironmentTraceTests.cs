using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ClosureEnvironmentTraceTests
{
    [Fact]
    public void FunctionTraceKeepsCapturedEnvironmentObjectsAlive()
    {
        // A captured binding whose only strong reference is the closure's
        // environment chain must survive a full collection: the closure's
        // Trace walks its environment chain (bindings included).
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);
        Run(interpreter, """
            var __f;
            (function () {
                var captured = { marker: 7 };
                __f = function () { return captured.marker; };
            })();
            """);

        heap.CollectGarbage();

        var marker = Run(interpreter, "__f();");
        Assert.Equal(7, marker.AsNumber());
    }

    [Fact]
    public void PendingPromiseFinallyKeepsCapturedCallbackAliveAcrossMajorCollection()
    {
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var interpreter = new BytecodeInterpreter(heap);
        Run(interpreter, """
            var __resolve;
            var __result = -1;
            var __pending = new Promise(function (resolve) { __resolve = resolve; });
            (function () {
                function Marker() {}
                Marker.prototype.marker = 42;
                __pending.finally(function () { __result = Marker.prototype.marker; });
            })();
            """);

        heap.CollectGarbage();
        Run(interpreter, "__resolve(null);");

        Assert.Equal(42, Run(interpreter, "__result;").AsNumber());
    }

    [Fact]
    public void RejectedPromiseFinallyKeepsCapturedCallbackAliveAcrossMajorCollection()
    {
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var interpreter = new BytecodeInterpreter(heap);
        Run(interpreter, """
            var __reject;
            var __result = -1;
            var __pending = new Promise(function (_, reject) { __reject = reject; });
            (function () {
                function Marker() {}
                Marker.prototype.marker = 42;
                __pending.finally(function () { __result = Marker.prototype.marker; }).catch(function () {});
            })();
            """);

        heap.CollectGarbage();
        Run(interpreter, "__reject(null);");

        Assert.Equal(42, Run(interpreter, "__result;").AsNumber());
    }

    private static JsValue Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }
}
