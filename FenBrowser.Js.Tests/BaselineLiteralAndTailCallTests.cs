using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.Baseline;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The baseline tier used to decline a whole body for an object literal's
// property (SetPropByName D=1) or a strict tail call. Each case here compiles
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

    [Fact]
    public void ATailRecursiveFunctionDoesNotGrowTheStack()
    {
        // Far past the 10,000-frame limit: only a proper tail call finishes.
        var result = RunCompiled(@"
            function down(k, acc) {
                'use strict';
                var t = 0;
                for (var i = 0; i < 2; i++) t = (t + i) | 0;
                if (k === 0) return acc + t;
                return down(k - 1, acc + 1);
            }
            down(50000, 0);");
        Assert.Equal(50001.0, result.AsNumber());
    }

    [Fact]
    public void TailCallsWithNoneOrOneArgumentReachTheirTarget()
    {
        var result = RunCompiled(@"
            'use strict';
            var seen = 0;
            function zero() { var t = 0; for (var i = 0; i < 3; i++) t += i; seen = t; return 7; }
            function one(x) { var t = 0; for (var i = 0; i < 3; i++) t += i; return zero(); }
            function start(n) { var t = 0; for (var i = 0; i < 3; i++) t += i; return one(n + t); }
            start(1) === 7 && seen === 3;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ATailCallToANativeOrBoundFunctionStillCompletes()
    {
        var result = RunCompiled(@"
            'use strict';
            function viaNative(a, b) { var t = 0; for (var i = 0; i < 3; i++) t += i; return Math.max(a, b, t); }
            function target(x) { return x * 2; }
            var bound = target.bind(null, 21);
            function viaBound() { var t = 0; for (var i = 0; i < 3; i++) t += i; return bound(); }
            viaNative(1, 9) === 9 && viaBound() === 42;");
        Assert.True(result.AsBoolean());
    }
}
