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
