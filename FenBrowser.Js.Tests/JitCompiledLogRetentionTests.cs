using System.Runtime.CompilerServices;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// JitCompiler kept every function it compiled in a static log for its cost
// report. A compiled function's inline caches point at the prototypes they
// learned (the holder of a method, a getter's home object), and those point
// at their heap - so the log kept every realm that had ever compiled anything
// alive: a JIT-heavy test262 run grew by ~1.5MB a test until it was killed.
// The log is now kept only when a report is asked for (FEN_JIT_REPORT=1).
public sealed class JitCompiledLogRetentionTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunCompileAndForget(int realm)
    {
        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);
        var script = new BytecodeCompiler().CompileScript(new SourceText($@"/* realm {realm} */
            class A {{ get g() {{ return 1; }} m() {{ return 2; }} }} class B extends A {{}} class C extends B {{}}
            function work(o) {{ return o.g + o.m() + o.hasOwnProperty('x'); }}
            var c = new C(), r; for (var i = 0; i < 60; i++) r = work(c); r;"));
        new BytecodeVerifier().Verify(script);
        interpreter.Execute(script);

        // `work` has run, so its caches hold A.prototype; compile it the way
        // tier-up would.
        var work = script.NestedFunctions.First(f => f.Name == "work");
        Assert.NotNull(JitCompiler.TryCompile(work));
        return new WeakReference(heap);
    }

    [Fact]
    public void ARealmWhoseFunctionWasCompiledIsStillCollected()
    {
        var realms = Enumerable.Range(0, 3).Select(RunCompileAndForget).ToList();
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.All(realms, realm => Assert.False(realm.IsAlive));
    }
}
