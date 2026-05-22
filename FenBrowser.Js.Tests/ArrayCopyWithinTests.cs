using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayCopyWithinTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void CopyForward()
    {
        // Copy [1,2,3,4,5][3,5) (= 4,5) to index 0 -> [4,5,3,4,5]
        Assert.Equal("4,5,3,4,5", RunStr("[1,2,3,4,5].copyWithin(0, 3).join(',');"));
    }

    [Fact]
    public void CopyWithExplicitEnd()
    {
        // Copy [1,2,3,4,5][3,4) (= 4) to index 1 -> [1,4,3,4,5]
        Assert.Equal("1,4,3,4,5", RunStr("[1,2,3,4,5].copyWithin(1, 3, 4).join(',');"));
    }

    [Fact]
    public void CopyOverlappingDownward()
    {
        // Copy [1,2,3,4,5][1,4) (= 2,3,4) to index 0 -> [2,3,4,4,5]
        Assert.Equal("2,3,4,4,5", RunStr("[1,2,3,4,5].copyWithin(0, 1, 4).join(',');"));
    }

    [Fact]
    public void CopyOverlappingUpward()
    {
        // Copy [1,2,3,4,5][0,3) (= 1,2,3) to index 2 -> [1,2,1,2,3]
        Assert.Equal("1,2,1,2,3", RunStr("[1,2,3,4,5].copyWithin(2, 0).join(',');"));
    }

    [Fact]
    public void NegativeIndicesWrap()
    {
        // [1,2,3,4,5].copyWithin(-2) -> target=3, start=0 -> copy 0..2 (1,2) to 3 -> [1,2,3,1,2]
        Assert.Equal("1,2,3,1,2", RunStr("[1,2,3,4,5].copyWithin(-2).join(',');"));
    }

    [Fact]
    public void ReturnsReceiver()
    {
        Assert.Equal(1, RunNum("var a = [1,2,3]; (a.copyWithin(0,1) === a) ? 1 : 0;"));
    }

    [Fact]
    public void NoOpWhenCountZero()
    {
        Assert.Equal("1,2,3", RunStr("[1,2,3].copyWithin(2, 3).join(',');"));
    }
}
