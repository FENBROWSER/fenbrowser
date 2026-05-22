using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayReduceTests
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
    public void ReduceWithInitial()
    {
        Assert.Equal(10, RunNum("[1,2,3,4].reduce(function(a,v){return a+v;}, 0);"));
    }

    [Fact]
    public void ReduceWithoutInitialUsesFirstElement()
    {
        Assert.Equal(10, RunNum("[1,2,3,4].reduce(function(a,v){return a+v;});"));
    }

    [Fact]
    public void ReduceSingleElementWithoutInitialReturnsThatElement()
    {
        Assert.Equal(42, RunNum("[42].reduce(function(a,v){return a*v;});"));
    }

    [Fact]
    public void ReduceEmptyWithInitialReturnsInitial()
    {
        Assert.Equal(99, RunNum("[].reduce(function(a,v){return v;}, 99);"));
    }

    [Fact]
    public void ReduceEmptyWithoutInitialThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("[].reduce(function(a,v){return v;});"));
    }

    [Fact]
    public void ReduceRightWalksReverse()
    {
        // String concat exposes order.
        Assert.Equal("dcba", RunStr("['a','b','c','d'].reduceRight(function(a,v){return a+v;});"));
    }

    [Fact]
    public void ReduceRightEmptyWithoutInitialThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("[].reduceRight(function(a,v){return v;});"));
    }

    [Fact]
    public void ReduceNonFunctionCallbackThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("[1].reduce(42, 0);"));
    }
}
