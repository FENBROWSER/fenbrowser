using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.Baseline;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The baseline tier used to decline a whole body for an object literal's
// property (SetPropByName D=1). Each case here compiles
// the function up front and makes calls prefer the compiled body, so the
// result is what compiled code produced.
public sealed class BaselineLiteralAndTailCallTests
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
            // Loop-heavy by the record, so a call runs the compiled body. A
            // body without a loop never does, and stays interpreted.
            fn.BackEdges = int.MaxValue;
            Assert.Equal(fn.LoopsSuitCompiledCode, JitCompiler.PrefersCompiled(fn));
        }

        return new BytecodeInterpreter().Execute(script);
    }

    [Fact]
    public void ALiteralPropertyIsDefinedNotAssigned()
    {
        // An inherited setter must not run for a literal's own property
        // (ECMA-262 13.2.5.5), and the property must end up own.
        var result = RunCompiled(@"
            var hit = false;
            Object.defineProperty(Object.prototype, 'total', { set: function () { hit = true; }, configurable: true });
            function lit(n) {
                var s = 0;
                for (var i = 0; i < n; i++) s = (s + i) | 0;
                return { total: s, n: n };
            }
            var r = lit(10);
            delete Object.prototype.total;
            !hit && r.hasOwnProperty('total') && r.total === 45 && r.n === 10;");
        Assert.True(result.AsBoolean());
    }
}
