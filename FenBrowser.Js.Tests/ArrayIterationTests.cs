using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayIterationTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void ForEachInvokesForEveryElement()
    {
        Assert.Equal(6, RunNum("var s = 0; [1,2,3].forEach(function(v){ s = s + v; }); s;"));
    }

    [Fact]
    public void ForEachReceivesIndex()
    {
        Assert.Equal(3, RunNum("var s = 0; [10,20,30].forEach(function(v,i){ s = s + i; }); s;"));
    }

    [Fact]
    public void ForEachReturnsUndefined()
    {
        Assert.Equal(1, RunNum("var r = [1].forEach(function(){}); r === undefined ? 1 : 0;"));
    }

    [Fact]
    public void MapBuildsArrayOfReturnValues()
    {
        Assert.Equal(2, RunNum("[1,2,3].map(function(v){ return v*2; })[0];"));
        Assert.Equal(6, RunNum("[1,2,3].map(function(v){ return v*2; })[2];"));
        Assert.Equal(3, RunNum("[1,2,3].map(function(v){ return v*2; }).length;"));
    }

    [Fact]
    public void MapPreservesLengthForEmpty()
    {
        Assert.Equal(0, RunNum("[].map(function(){}).length;"));
    }

    [Fact]
    public void FilterKeepsTruthyResults()
    {
        Assert.Equal(2, RunNum("[1,2,3,4].filter(function(v){ return v % 2 === 0; }).length;"));
        Assert.Equal(2, RunNum("[1,2,3,4].filter(function(v){ return v % 2 === 0; })[0];"));
        Assert.Equal(4, RunNum("[1,2,3,4].filter(function(v){ return v % 2 === 0; })[1];"));
    }

    [Fact]
    public void FilterRejectsAllReturnsEmptyArray()
    {
        Assert.Equal(0, RunNum("[1,2,3].filter(function(){ return false; }).length;"));
    }

    [Fact]
    public void CallbackThisArgIsHonored()
    {
        Assert.Equal(7, RunNum("[1].forEach(function(){ this.v = 7; }, this); this.v;"));
    }

    [Fact]
    public void NonFunctionCallbackThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("[1,2,3].forEach(42);"));
        Assert.Throws<JsThrownException>(() => RunNum("[1,2,3].map(undefined);"));
        Assert.Throws<JsThrownException>(() => RunNum("[1,2,3].filter('x');"));
    }
}
