using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayReverseFillTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void ReverseFlipsInPlace()
    {
        Assert.Equal(3, RunNum("var a = [1,2,3]; a.reverse(); a[0];"));
        Assert.Equal(1, RunNum("var a = [1,2,3]; a.reverse(); a[2];"));
    }

    [Fact]
    public void ReverseReturnsSameArray()
    {
        Assert.Equal(2, RunNum("var a = [1,2]; (a.reverse() === a) ? 2 : 0;"));
    }

    [Fact]
    public void ReverseOnSingleElement()
    {
        Assert.Equal(7, RunNum("var a = [7]; a.reverse(); a[0];"));
    }

    [Fact]
    public void ReverseOnEmpty()
    {
        Assert.Equal(0, RunNum("var a = []; a.reverse(); a.length;"));
    }

    [Fact]
    public void FillEntireArray()
    {
        Assert.Equal(9, RunNum("var a = [1,2,3]; a.fill(9); a[0];"));
        Assert.Equal(9, RunNum("var a = [1,2,3]; a.fill(9); a[2];"));
    }

    [Fact]
    public void FillWithStartAndEnd()
    {
        Assert.Equal(1, RunNum("var a = [1,2,3,4]; a.fill(0, 1, 3); a[0];"));
        Assert.Equal(0, RunNum("var a = [1,2,3,4]; a.fill(0, 1, 3); a[1];"));
        Assert.Equal(0, RunNum("var a = [1,2,3,4]; a.fill(0, 1, 3); a[2];"));
        Assert.Equal(4, RunNum("var a = [1,2,3,4]; a.fill(0, 1, 3); a[3];"));
    }

    [Fact]
    public void FillWithNegativeIndices()
    {
        Assert.Equal(0, RunNum("var a = [1,2,3,4]; a.fill(0, -2); a[2];"));
        Assert.Equal(0, RunNum("var a = [1,2,3,4]; a.fill(0, -2); a[3];"));
        Assert.Equal(1, RunNum("var a = [1,2,3,4]; a.fill(0, -2); a[0];"));
    }

    [Fact]
    public void FillReturnsSameArray()
    {
        Assert.Equal(5, RunNum("var a = [1]; (a.fill(0) === a) ? 5 : 0;"));
    }
}
