using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ForOfTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void IteratesArrayValues()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of [1,2,3]) s = s + v; s;"));
    }

    [Fact]
    public void IteratesEmptyArrayProducesNoIterations()
    {
        Assert.Equal(0, RunNum("var c = 0; for (var v of []) c = c + 1; c;"));
    }

    [Fact]
    public void IteratesStringYieldsCodeUnits()
    {
        Assert.Equal("abc", RunStr("var r = ''; for (var c of 'abc') r = r + c; r;"));
    }

    [Fact]
    public void IteratesArrayLikeObject()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of {length:3, 0:1, 1:2, 2:3}) s = s + v; s;"));
    }

    [Fact]
    public void BreakWorks()
    {
        Assert.Equal(3, RunNum("var c = 0; for (var v of [1,2,3,4,5]) { if (v > 3) break; c = c + 1; } c;"));
    }

    [Fact]
    public void ContinueWorks()
    {
        // Sum of evens in [1,2,3,4] = 2 + 4 = 6.
        Assert.Equal(6, RunNum("var s = 0; for (var v of [1,2,3,4]) { if (v % 2 === 1) continue; s = s + v; } s;"));
    }

    [Fact]
    public void NullAndUndefinedThrow()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of null);"));
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of undefined);"));
    }

    [Fact]
    public void PlainObjectThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of {a:1});"));
    }

    [Fact]
    public void NumberIsNotIterable()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of 42);"));
    }
}
