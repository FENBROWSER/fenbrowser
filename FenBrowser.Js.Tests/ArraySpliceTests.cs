using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArraySpliceTests
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
    public void RemoveFromMiddleReturnsRemoved()
    {
        Assert.Equal("3,4", RunStr("[1,2,3,4,5].splice(2,2).join(',');"));
    }

    [Fact]
    public void RemoveFromMiddleShortensArray()
    {
        Assert.Equal(3, RunNum("var a=[1,2,3,4,5]; a.splice(2,2); a.length;"));
        Assert.Equal("1,2,5", RunStr("var a=[1,2,3,4,5]; a.splice(2,2); a.join(',');"));
    }

    [Fact]
    public void InsertWithoutRemove()
    {
        Assert.Equal("1,2,9,9,3", RunStr("var a=[1,2,3]; a.splice(2,0,9,9); a.join(',');"));
        Assert.Equal(0, RunNum("[1,2,3].splice(2,0,9,9).length;"));
    }

    [Fact]
    public void ReplaceWithDifferentCount()
    {
        Assert.Equal("1,8,9,3", RunStr("var a=[1,2,3]; a.splice(1,1,8,9); a.join(',');"));
    }

    [Fact]
    public void SingleArgRemovesTail()
    {
        Assert.Equal("1,2", RunStr("var a=[1,2,3,4]; a.splice(2); a.join(',');"));
        Assert.Equal("3,4", RunStr("[1,2,3,4].splice(2).join(',');"));
    }

    [Fact]
    public void NegativeStartWrapsFromLength()
    {
        Assert.Equal("1,2", RunStr("var a=[1,2,3,4]; a.splice(-2); a.join(',');"));
    }

    [Fact]
    public void DeleteCountGreaterThanRemainingClamps()
    {
        // splice(2, 99) on a 3-element array removes index 2 only.
        Assert.Equal("3", RunStr("[1,2,3].splice(2, 99).join(',');"));
        Assert.Equal("1,2", RunStr("var a=[1,2,3]; a.splice(2, 99); a.join(',');"));
    }

    [Fact]
    public void EmptyArraySpliceReturnsEmpty()
    {
        Assert.Equal(0, RunNum("[].splice(0, 0).length;"));
    }
}
