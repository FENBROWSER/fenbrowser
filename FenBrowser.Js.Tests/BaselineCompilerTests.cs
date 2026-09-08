using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.Baseline;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The baseline tier emits IL directly, so what it produces has to agree with
// the dispatch loop instruction for instruction. Each case here runs past the
// tier-up threshold, which puts the same work through both.
public class BaselineCompilerTests
{
    private const int TierUp = 2000;

    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static double Run(string source) =>
        new BytecodeInterpreter().Execute(Compile(source)).AsNumber();

    private static string RunString(string source) =>
        new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Fact]
    public void ABroadFunctionBody_EmitsWithoutRefusal()
    {
        var fn = Compile(@"
            function body(o, list) {
                var total = 0;
                let scaled = 0;
                const factor = 3;
                for (var i = 0; i < list.length; i++) {
                    total += list[i] * factor;
                    scaled = total | 0;
                    o.seen = scaled;
                }
                for (const item of list) { total -= item; }
                for (var key in o) { total += key.length; }
                try { if (total < 0) throw new Error('negative'); }
                catch (e) { total = 0; }
                finally { total += 1; }
                return typeof total === 'number' ? total : -1;
            }
            body({}, [1, 2, 3]);
        ");

        Assert.NotNull(BaselineCompiler.TryCompile(fn));
        foreach (var nested in fn.NestedFunctions)
        {
            Assert.NotNull(BaselineCompiler.TryCompile(nested));
        }
    }

    [Fact]
    public void ASuspendingBody_IsTurnedAway()
    {
        var fn = Compile("function* gen() { yield 1; } gen();");
        var generator = Assert.Single(fn.NestedFunctions);
        Assert.Null(BaselineCompiler.TryCompile(generator));
    }

    [Fact]
    public void ArithmeticFastPaths_MatchTheDispatchLoop()
    {
        var v = Run($@"
            function arith(n) {{ return ((n + 2) * 3 - 1) / 2 % 7; }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = arith(i);
            last;
        ");
        Assert.Equal(((TierUp - 1 + 2) * 3 - 1) / 2.0 % 7, v);
    }

    [Fact]
    public void ComparisonsAgainstNaN_AreAllFalseExceptInequality()
    {
        var v = Run($@"
            function compare(x) {{
                var n = 0;
                if (x < 1) n += 1;
                if (x > 1) n += 2;
                if (x <= 1) n += 4;
                if (x >= 1) n += 8;
                if (x === x) n += 16;
                if (x !== x) n += 32;
                return n;
            }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = compare(NaN);
            last;
        ");
        Assert.Equal(32.0, v);
    }

    [Fact]
    public void ShiftCounts_AreTakenModuloThirtyTwo()
    {
        var v = Run($@"
            function shifts(n) {{ return (n << 33) + (n >> 1) + ((0 - 1) >>> 0); }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = shifts(8);
            last;
        ");
        Assert.Equal((8 << 1) + (8 >> 1) + 4294967295.0, v);
    }

    [Fact]
    public void AnIntegralResult_KeepsItsIntegerTag()
    {
        // The tag decides which fast path the next operator can take, so a
        // counter handed back as a Number would deoptimise the mask after it.
        var v = Run($@"
            function masked(n) {{ var i = n; i++; return i & 255; }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = masked(i);
            last;
        ");
        Assert.Equal(TierUp % 256, v);
    }

    [Fact]
    public void ANonNumericOperand_FallsToTheGeneralPath()
    {
        var v = RunString($@"
            function join(a, b) {{ return a + b; }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = join('a', i);
            last;
        ");
        Assert.Equal("a" + (TierUp - 1), v);
    }

    [Fact]
    public void AThrowInsideCompiledCode_ReachesItsOwnHandler()
    {
        var v = Run($@"
            function guarded(n) {{
                var seen = 0;
                try {{
                    if (n % 2 === 0) throw new RangeError('even');
                    seen = 1;
                }} catch (e) {{
                    seen = e instanceof RangeError ? 2 : 3;
                }} finally {{
                    seen += 10;
                }}
                return seen;
            }}
            var total = 0;
            for (var i = 0; i < {TierUp}; i++) total += guarded(i);
            total;
        ");
        Assert.Equal(TierUp / 2 * 12.0 + TierUp / 2 * 11.0, v);
    }

    [Fact]
    public void AReturnFromInsideFinally_WinsOverTheTryValue()
    {
        var v = Run($@"
            function overridden(n) {{
                try {{ return 1; }} finally {{ if (n >= 0) return 2; }}
            }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = overridden(i);
            last;
        ");
        Assert.Equal(2.0, v);
    }

    [Fact]
    public void ALongLoopInOneCall_HandsOverAtItsHeader()
    {
        // Never called twice, so the only way into compiled code is the loop
        // header the running frame transfers at.
        var v = Run(@"
            function once() {
                var total = 0;
                for (var i = 0; i < 200000; i++) { total += i & 7; }
                return total;
            }
            once();
        ");

        var expected = 0.0;
        for (var i = 0; i < 200000; i++) expected += i & 7;
        Assert.Equal(expected, v);
    }

    [Fact]
    public void PropertyAccess_SurvivesAShapeChange()
    {
        var v = Run($@"
            function read(o) {{ o.hits = o.hits + 1; return o.hits; }}
            var stable = {{ hits: 0 }};
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = read(stable);
            var other = {{ extra: 1, hits: 100 }};
            for (var j = 0; j < 10; j++) last = read(other);
            last;
        ");
        Assert.Equal(110.0, v);
    }

    [Fact]
    public void AnAccessorReachedFromCompiledCode_StillRuns()
    {
        var v = Run($@"
            var calls = 0;
            var source = {{ get value() {{ calls++; return 7; }} }};
            function read(o) {{ return o.value; }}
            for (var i = 0; i < {TierUp}; i++) read(source);
            calls;
        ");
        Assert.Equal((double)TierUp, v);
    }

    [Fact]
    public void PerIterationBindings_StayWithTheirIteration()
    {
        var v = Run($@"
            function captured(n) {{
                var fns = [];
                for (let i = 0; i < 3; i++) fns.push(function () {{ return i; }});
                return fns[0]() + fns[1]() + fns[2]() + n * 0;
            }}
            var last = 0;
            for (var k = 0; k < {TierUp}; k++) last = captured(k);
            last;
        ");
        Assert.Equal(3.0, v);
    }

    [Fact]
    public void IterationOpcodes_WalkTheSameValues()
    {
        var v = Run($@"
            function walk(list, o) {{
                var total = 0;
                for (var item of list) total += item;
                for (var key in o) total += o[key];
                return total;
            }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = walk([1, 2, 3], {{ a: 10, b: 20 }});
            last;
        ");
        Assert.Equal(36.0, v);
    }

    [Fact]
    public void AConstructorCalledHot_StillBrandsItsInstances()
    {
        var v = Run($@"
            function Point(x) {{ this.x = x; }}
            Point.prototype.twice = function () {{ return this.x * 2; }};
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = new Point(i).twice();
            last;
        ");
        Assert.Equal((TierUp - 1) * 2.0, v);
    }
}
