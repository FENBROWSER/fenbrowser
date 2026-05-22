using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayPopShiftUnshiftTests
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
    public void PopReturnsLastAndShrinks()
    {
        Assert.Equal(3, RunNum("var a = [1,2,3]; a.pop();"));
        Assert.Equal(2, RunNum("var a = [1,2,3]; a.pop(); a.length;"));
    }

    [Fact]
    public void PopOnEmptyReturnsUndefinedAndKeepsZero()
    {
        Assert.Equal(0, RunNum("var a = []; a.pop(); a.length;"));
        Assert.Equal("undefined", RunStr("var a = []; typeof a.pop();"));
    }

    [Fact]
    public void ShiftReturnsFirstAndShifts()
    {
        Assert.Equal(1, RunNum("var a = [1,2,3]; a.shift();"));
        Assert.Equal(2, RunNum("var a = [1,2,3]; a.shift(); a[0];"));
        Assert.Equal(2, RunNum("var a = [1,2,3]; a.shift(); a.length;"));
    }

    [Fact]
    public void ShiftOnEmpty() => Assert.Equal("undefined", RunStr("var a = []; typeof a.shift();"));

    [Fact]
    public void UnshiftPrependsAndReturnsNewLength()
    {
        Assert.Equal(5, RunNum("var a = [3,4,5]; a.unshift(1,2);"));
        Assert.Equal(1, RunNum("var a = [3,4,5]; a.unshift(1,2); a[0];"));
        Assert.Equal(3, RunNum("var a = [3,4,5]; a.unshift(1,2); a[2];"));
    }

    [Fact]
    public void UnshiftEmptyOnEmpty() => Assert.Equal(0, RunNum("[].unshift();"));

    [Fact]
    public void PushPopRoundTrip()
    {
        Assert.Equal(42, RunNum("var a = [1,2]; a.push(42); a.pop();"));
    }
}
