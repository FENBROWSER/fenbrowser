using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayFromIterableTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void FromSetDrainsViaIterator()
    {
        Assert.Equal("1,2,3", Run("Array.from(new Set([1,2,3])).join(',');").AsString());
    }

    [Fact]
    public void FromMapYieldsEntryPairs()
    {
        Assert.Equal(2d, Run("Array.from(new Map([['a',1],['b',2]])).length;").AsNumber());
    }

    [Fact]
    public void FromStringYieldsCharacters()
    {
        Assert.Equal("a,b,c", Run("Array.from('abc').join(',');").AsString());
    }

    [Fact]
    public void FromArrayLikeStillWorks()
    {
        Assert.Equal("x,y", Run("Array.from({length:2, 0:'x', 1:'y'}).join(',');").AsString());
    }

    [Fact]
    public void MapFnAppliedWithIndex()
    {
        Assert.Equal("0,1,2", Run("Array.from(new Set([10,20,30]), function(v,i){return i;}).join(',');").AsString());
    }

    [Fact]
    public void NullThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Array.from(null);"));
    }
}
