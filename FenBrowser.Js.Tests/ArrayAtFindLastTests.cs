using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayAtFindLastTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("[1,2,3].at(0);", 1.0)]
    [InlineData("[1,2,3].at(2);", 3.0)]
    [InlineData("[1,2,3].at(-1);", 3.0)]
    [InlineData("[1,2,3].at(-3);", 1.0)]
    public void AtValid(string source, double expected) => Assert.Equal(expected, RunNum(source));

    [Fact]
    public void AtOutOfRangeReturnsUndefined()
    {
        Assert.True(RunBool("[1,2,3].at(99) === undefined;"));
        Assert.True(RunBool("[1,2,3].at(-4) === undefined;"));
        Assert.True(RunBool("[].at(0) === undefined;"));
    }

    [Fact]
    public void FindLastReturnsLastMatching()
    {
        Assert.Equal(4, RunNum("[1,2,3,4,5].findLast(function(v){return v < 5;});"));
    }

    [Fact]
    public void FindLastReturnsUndefinedWhenNoMatch()
    {
        Assert.True(RunBool("[1,2].findLast(function(v){return v > 99;}) === undefined;"));
    }

    [Fact]
    public void FindLastIndexReturnsLastMatchingIndex()
    {
        Assert.Equal(3, RunNum("[1,2,3,4,5].findLastIndex(function(v){return v < 5;});"));
    }

    [Fact]
    public void FindLastIndexReturnsMinusOneWhenNoMatch()
    {
        Assert.Equal(-1, RunNum("[1,2].findLastIndex(function(v){return v > 99;});"));
    }

    [Fact]
    public void FindLastWithObjectElements()
    {
        Assert.Equal(2, RunNum("var arr = [{n:1},{n:2}]; arr.findLast(function(o){return o.n > 0;}).n;"));
    }
}
