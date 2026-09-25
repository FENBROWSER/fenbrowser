using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The register-window loop gathers the same tier-up evidence as the old loop
// and hands a call to compiled code only for a body whose loops stay on what
// compiled code is faster at (BytecodeFunction.LoopsSuitCompiledCode).
public sealed class Interp2TierUpTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static BytecodeFunction Nested(string source) => Compile(source).NestedFunctions[0];

    [Theory]
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i) | 0; return t; }")]
    [InlineData("function f(a) { var t = 0; for (var i = 0; i < a.length; i++) t += a[i]; return t; }")]
    [InlineData("function f(o, n) { for (var i = 0; i < n; i++) o.x = i; return o.x; }")]
    [InlineData("function f(n) { var s = ''; while (n-- > 0) s = 'a' + n; return s; }")]
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) t += outer; return t; }")]
    [InlineData("function f(n) { for (var i = 0; i < n; i++) outer = i; }")]
    public void LocalOnlyLoopsSuitCompiledCode(string source)
    {
        Assert.True(Nested(source).LoopsSuitCompiledCode);
    }

    [Theory]
    [InlineData("function f(n) { var t = 0; return t + n; }")]
    [InlineData("function f(n, g) { for (var i = 0; i < n; i++) g(i); }")]
    [InlineData("function f(n, o) { for (var i = 0; i < n; i++) o.m(); }")]
    [InlineData("function f(n) { var r; for (var i = 0; i < n; i++) r = { x: i }; return r; }")]
    [InlineData("function f(n) { var r; for (var i = 0; i < n; i++) r = [i]; return r; }")]
    [InlineData("function f(n) { var r; for (var i = 0; i < n; i++) r = function () {}; return r; }")]
    [InlineData("function f(n) { var t = 0; for (let i = 0; i < n; i++) t += i; return t; }")]
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) { const k = i; t += k; } return t; }")]
    public void LoopsCompiledCodeIsSlowerAtDoNotSuitIt(string source)
    {
        Assert.False(Nested(source).LoopsSuitCompiledCode);
    }

    [Fact]
    public void BackEdgesTakenOnTheRegisterWindowLoopAreCounted()
    {
        var script = Compile(@"
            function hot(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i) | 0; return t; }
            function caller() { return hot(5000) + hot(5000); }
            caller();");
        new BytecodeInterpreter().Execute(script);

        var hot = script.NestedFunctions[0];
        var caller = script.NestedFunctions[1];
        // At least the first call's: with FEN_JIT_SYNC=1 the second already runs
        // compiled, and compiled code does not count.
        Assert.True(hot.BackEdgesObserved >= 5000, $"hot took {hot.BackEdgesObserved} back-edges");
        // The caller has no loop of its own: its callee's loops are not credited to it.
        Assert.Equal(0, caller.BackEdgesObserved);
#if !PUBLISH_AOT
        Assert.True(hot.JitCompileWasAttempted);
        Assert.False(caller.JitCompileWasAttempted);
#endif
    }

    [Fact]
    public void ALoopThatCallsOutIsNotCompiled()
    {
        var script = Compile(@"
            function id(x) { return x; }
            function busy(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + id(i)) | 0; return t; }
            busy(20000);");
        new BytecodeInterpreter().Execute(script);

#if !PUBLISH_AOT
        Assert.False(script.NestedFunctions[1].JitCompileWasAttempted);
#endif
    }

    [Fact]
    public void ResultsStayTheSameOnceCallsMoveToCompiledCode()
    {
        // Enough rounds that the compile, requested after the first, has been
        // published and later calls run compiled - if the JIT accepts the body.
        var result = new BytecodeInterpreter().Execute(Compile(@"
            function sum(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i * 3) | 0; return t; }
            var ok = true;
            for (var r = 0; r < 40; r++) { if (sum(20000) !== 599970000) ok = false; }
            ok;"));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void OuterVariableWritesStayCorrectOnceCallsMoveToCompiledCode()
    {
        var result = new BytecodeInterpreter().Execute(Compile(@"
            var total = 0;
            function make() {
                var c = 0;
                return function (n) { for (var i = 0; i < n; i++) { c = c + 1; total = total + 2; } return c; };
            }
            var add = make(), last = 0;
            for (var r = 0; r < 40; r++) last = add(5000);
            last === 200000 && total === 400000;"));
        Assert.True(result.AsBoolean());
    }
}
