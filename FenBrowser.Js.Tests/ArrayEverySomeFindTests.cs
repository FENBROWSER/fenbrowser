using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayEverySomeFindTests
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

    [Fact]
    public void EveryAllPositive() => Assert.True(RunBool("[1,2,3].every(function(v){return v > 0;});"));

    [Fact]
    public void EverySomeNegative() => Assert.False(RunBool("[1,-1,3].every(function(v){return v > 0;});"));

    [Fact]
    public void EveryOnEmptyIsTrue() => Assert.True(RunBool("[].every(function(){return false;});"));

    [Fact]
    public void SomeAnyPositive() => Assert.True(RunBool("[-1,-2,3].some(function(v){return v > 0;});"));

    [Fact]
    public void SomeNonePositive() => Assert.False(RunBool("[-1,-2,-3].some(function(v){return v > 0;});"));

    [Fact]
    public void SomeOnEmptyIsFalse() => Assert.False(RunBool("[].some(function(){return true;});"));

    [Fact]
    public void FindReturnsFirstMatching()
    {
        Assert.Equal(3, RunNum("[1,2,3,4].find(function(v){return v > 2;});"));
    }

    [Fact]
    public void FindReturnsUndefinedWhenNoMatch()
    {
        Assert.True(RunBool("[1,2].find(function(v){return v > 99;}) === undefined;"));
    }

    [Fact]
    public void FindIndexReturnsIndex()
    {
        Assert.Equal(2, RunNum("[1,2,3,4].findIndex(function(v){return v > 2;});"));
    }

    [Fact]
    public void FindIndexReturnsMinusOneWhenNoMatch()
    {
        Assert.Equal(-1, RunNum("[1,2].findIndex(function(v){return v > 99;});"));
    }

    [Fact]
    public void CallbackIndexIsForwarded()
    {
        Assert.Equal(0, RunNum("[10,20,30].findIndex(function(v,i){return i === 0;});"));
    }
}
