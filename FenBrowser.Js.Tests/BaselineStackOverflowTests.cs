using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.Baseline;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Runaway recursion through compiled frames must end in the catchable
// RangeError the dispatch loop gives, not a process-ending StackOverflow. It
// used to overflow a second time while unwinding: every compiled frame caught
// the error and rethrew it on top of the stack the first throw still held.
public sealed class BaselineStackOverflowTests
{
    private static JsValue RunCompiled(string source)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(script);
        foreach (var fn in script.NestedFunctions)
        {
            var compiled = BaselineCompiler.TryCompile(fn);
            Assert.NotNull(compiled);
            fn.JitCompileAttempted = true;
            fn.JitDelegate = compiled;
            fn.BackEdges = int.MaxValue;
            Assert.True(JitCompiler.PrefersCompiled(fn));
        }

        return new BytecodeInterpreter().Execute(script);
    }

    [Fact]
    public void RunawayRecursionInCompiledCodeThrowsRangeError()
    {
        var result = RunCompiled(@"
            function deep(n) {
                var t = 0;
                for (var i = 0; i < 2; i++) t = (t + i) | 0;
                return deep(n + 1) + t;
            }
            var name;
            try { deep(0); } catch (e) { name = e.constructor.name; }
            name;");
        Assert.Equal("RangeError", result.AsString());
    }

    [Fact]
    public void ATryInACompiledFrameStillCatchesWhatItsCalleeThrows()
    {
        var result = RunCompiled(@"
            function thrower(n) {
                var t = 0;
                for (var i = 0; i < 2; i++) t = (t + i) | 0;
                if (n > 3) throw new TypeError('deep ' + t);
                return thrower(n + 1);
            }
            function guard() {
                var t = 0;
                for (var i = 0; i < 2; i++) t = (t + i) | 0;
                try { return thrower(0); } catch (e) { return e.message; }
            }
            guard();");
        Assert.Equal("deep 1", result.AsString());
    }
}
