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

    private static JsValue Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }
}
