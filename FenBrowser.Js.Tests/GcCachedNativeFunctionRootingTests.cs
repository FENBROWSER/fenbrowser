using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Two ways a NativeFunctionObject stayed reachable to C# but not to the
/// tracer, so a collection swept it and the next use handed out a dangling
/// handle. Both were found on a live github.com load, where the collector
/// reported "Stale heap handle" naming the alloc site.
///
/// An Intl prototype accessor kept its implementation function only inside the
/// getter's C# closure, which is no property, no register and no root; and the
/// cached dummy promise capability rooted itself with PushRoot, which pushes
/// onto the *scoped* root stack that any enclosing HandleScope unwinds.
/// </summary>
public sealed class GcCachedNativeFunctionRootingTests
{
    private static JsValue Exec(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }

    private static BytecodeInterpreter NewCollectingInterpreter()
    {
        var interpreter = new BytecodeInterpreter();
        // Collect aggressively so the window between allocation and use is
        // actually crossed by a collection rather than surviving by luck.
        interpreter.Heap.YoungAllocationsPerMinorGc = 8;
        return interpreter;
    }

    [Fact]
    public void IntlPrototypeAccessorSurvivesAMajorCollection()
    {
        var interpreter = NewCollectingInterpreter();

        // Touch the accessor so the impl function is created, then collect and
        // read it again. Before the fix the impl was swept and the second read
        // threw JsEngineFatalException("Stale heap handle").
        Exec(interpreter, "typeof Intl.NumberFormat.prototype.format;");
        interpreter.Heap.CollectGarbage();
        interpreter.Heap.CollectGarbage();

        Assert.Equal(
            "function",
            Exec(interpreter, "typeof Intl.NumberFormat.prototype.format;").AsString());
    }

    [Fact]
    public void IntlAccessorRemainsCallableAfterCollection()
    {
        var interpreter = NewCollectingInterpreter();

        Exec(interpreter, "globalThis.f = new Intl.NumberFormat('en-US');");
        interpreter.Heap.CollectGarbage();
        interpreter.Heap.CollectGarbage();

        Assert.Equal("function", Exec(interpreter, "typeof f.format;").AsString());
    }

    [Fact]
    public void SeveralIntlAccessorsSurviveRepeatedCollections()
    {
        var interpreter = NewCollectingInterpreter();

        Exec(interpreter, "typeof Intl.NumberFormat.prototype.resolvedOptions;");
        Exec(interpreter, "typeof Intl.NumberFormat.prototype.formatToParts;");
        for (var i = 0; i < 4; i++)
        {
            interpreter.Heap.CollectGarbage();
        }

        Assert.Equal(
            "function|function|function",
            Exec(
                interpreter,
                "typeof Intl.NumberFormat.prototype.format + '|' +" +
                "typeof Intl.NumberFormat.prototype.resolvedOptions + '|' +" +
                "typeof Intl.NumberFormat.prototype.formatToParts;").AsString());
    }

    // The dummy capability is handed to every await that does not need a real
    // one. Awaiting across repeated collections exercises it after the scoped
    // root that used to hold it would have been popped.
    [Fact]
    public void AwaitAcrossCollectionsKeepsTheDummyCapabilityAlive()
    {
        var interpreter = NewCollectingInterpreter();

        Exec(interpreter, """
            globalThis.out = 0;
            async function run() {
              for (var i = 0; i < 25; i++) {
                globalThis.out = await Promise.resolve(i);
              }
            }
            run();
            """);
        interpreter.Heap.CollectGarbage();
        interpreter.Heap.CollectGarbage();

        Assert.Equal(24d, Exec(interpreter, "globalThis.out;").AsNumber());
    }

    [Fact]
    public void PromiseChainsSurviveRepeatedCollections()
    {
        var interpreter = NewCollectingInterpreter();

        Exec(interpreter, """
            globalThis.seen = '';
            Promise.resolve('a')
              .then(function (v) { globalThis.seen += v; return 'b'; })
              .then(function (v) { globalThis.seen += v; return 'c'; })
              .then(function (v) { globalThis.seen += v; });
            """);
        for (var i = 0; i < 3; i++)
        {
            interpreter.Heap.CollectGarbage();
        }

        Assert.Equal("abc", Exec(interpreter, "globalThis.seen;").AsString());
    }

    // A rejecting executor is the path that used rejectFn after arbitrary user
    // code had run, aging it out of the allocation pin ring.
    [Fact]
    public void ThrowingExecutorRejectsAfterAllocationPressure()
    {
        var interpreter = NewCollectingInterpreter();

        Exec(interpreter, """
            globalThis.reason = '';
            new Promise(function (resolve, reject) {
              var churn = [];
              for (var i = 0; i < 200; i++) { churn.push({ i: i }); }
              throw 'boom';
            }).catch(function (e) { globalThis.reason = String(e); });
            """);
        interpreter.Heap.CollectGarbage();

        Assert.Equal("boom", Exec(interpreter, "globalThis.reason;").AsString());
    }
}
