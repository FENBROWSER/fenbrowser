using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TryFinallyRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void TryFinally_NormalExit_FinallyExecutes()
    {
        Assert.Equal(3, RunNum("var x = 1; try { x = 2; } finally { x = 3; } x;"));
    }

    [Fact]
    public void TryFinally_BreakInTry_FinallyExecutes()
    {
        // ECMA-262 14.15.3: break out of a try-finally must run finally first.
        Assert.Equal(1, RunNum("var fin = 0, c = 0; while (c < 5) { try { break; } finally { fin = 1; } } fin;"));
    }

    [Fact]
    public void TryFinally_ContinueInTry_FinallyExecutes()
    {
        Assert.Equal(2, RunNum("var fin = 0, c = 0; while (c < 2) { try { c += 1; continue; } finally { fin += 1; } } fin;"));
    }

    [Fact]
    public void TryFinally_ReturnInTry_FinallyExecutesAndValuePreserved()
    {
        // return evaluates the value, runs finally, then returns the original value.
        Assert.Equal(10, RunNum("function f() { var x = 10; try { return x; } finally { x = 99; } } f();"));
    }

    [Fact]
    public void TryFinally_ReturnInFinally_OverridesTryReturn()
    {
        Assert.Equal(2, RunNum("function f() { try { return 1; } finally { return 2; } } f();"));
    }

    [Fact]
    public void TryFinally_NestedReturn_RunsBothFinallariesInnermostFirst()
    {
        Assert.Equal(12, RunNum(@"
var order = 0;
function f() {
    try { try { return 7; } finally { order = order * 10 + 1; } }
    finally { order = order * 10 + 2; }
}
f();
order;"));
    }

    [Fact]
    public void TryFinally_LabeledBreakAcrossNestedFinallies_RunsInnermostFirst()
    {
        // break L exits both loops after one iteration, running the inner finally
        // (j-loop body) then the outer finally (i-loop body): order = 0*10+1 then *10+2.
        Assert.Equal(12, RunNum(@"
var order = 0;
L: for (var i = 0; i < 2; i++) {
    try {
        for (var j = 0; j < 2; j++) {
            try { break L; } finally { order = order * 10 + 1; }
        }
    } finally { order = order * 10 + 2; }
}
order;"));
    }

    [Fact]
    public void TryFinally_ThrowInTry_FinallyExecutesBeforePropagation()
    {
        Assert.Equal(3, RunNum(@"
var x = 1;
try {
    try { throw 42; } finally { x = 3; }
} catch (e) { }
x;"));
    }

    [Fact]
    public void TryFinally_ThrowInTry_PropagatesAfterFinally()
    {
        Assert.Equal(42, RunNum(@"
var caught = 0;
try {
    try { throw 42; } finally { caught = 1; }
} catch (e) { caught = e; }
caught;"));
    }

    [Fact]
    public void TryCatchFinally_NormalExit_FinallyRunsCatchSkipped()
    {
        Assert.Equal(3, RunNum("var x = 1; try { x = 2; } catch (e) { x = 5; } finally { x = 3; } x;"));
    }

    [Fact]
    public void TryCatchFinally_ThrowCaught_FinallyRuns()
    {
        Assert.Equal(3, RunNum(@"
var x = 1;
try { throw 42; } catch (e) { x = 2; } finally { x = 3; }
x;"));
    }

    [Fact]
    public void TryCatchFinally_ThrowInCatch_FinallyRunsBeforePropagation()
    {
        Assert.Equal(3, RunNum(@"
var x = 1;
try {
    try { throw 42; } catch (e) { x = 2; throw 'inner'; } finally { x = 3; }
} catch (e2) { }
x;"));
    }

    [Fact]
    public void TryFinally_Nested_InnerFinallyRunsBeforeOuter()
    {
        Assert.Equal(10, RunNum(@"
var x = 0;
try {
    try { throw 1; } finally { x = x + 5; }
} catch (e) { x = x + 5; }
x;"));
    }

    [Fact]
    public void TryCatch_WithoutFinally_StillWorks()
    {
        Assert.Equal(42, RunNum("var x = 0; try { throw 42; } catch (e) { x = e; } x;"));
    }

    [Fact]
    public void TryCatchFinally_DeepNested_AllFinallyBlocksRun()
    {
        Assert.Equal(1234, RunNum(@"
var result = 0;
try {
    try {
        try { throw 1; } finally { result = result * 10 + 1; }
    } catch (e) { result = result * 10 + 2; } finally { result = result * 10 + 3; }
} finally { result = result * 10 + 4; }
result;"));
    }
}
