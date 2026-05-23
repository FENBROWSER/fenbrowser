using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GroupByTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ObjectGroupByStringKey()
    {
        Assert.Equal("1,3", Run("Object.groupBy([1,2,3,4], function(x){return x%2===1?'odd':'even';}).odd.join(',');").AsString());
        Assert.Equal("2,4", Run("Object.groupBy([1,2,3,4], function(x){return x%2===1?'odd':'even';}).even.join(',');").AsString());
    }

    [Fact]
    public void ObjectGroupByPassesIndex()
    {
        Assert.Equal("a,c", Run("Object.groupBy(['a','b','c','d'], function(v,i){return i%2===0?'p':'q';}).p.join(',');").AsString());
    }

    [Fact]
    public void ObjectGroupByEmptyIterable()
    {
        Assert.Equal(0d, Run("Object.keys(Object.groupBy([], function(){return 'x';})).length;").AsNumber());
    }

    [Fact]
    public void ObjectGroupByRejectsNonFunctionCallback()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.groupBy([1,2], 5);"));
    }

    [Fact]
    public void MapGroupByStringKey()
    {
        Assert.Equal(2d, Run("Map.groupBy([1,2,3,4], function(x){return x%2===0;}).size;").AsNumber());
        Assert.Equal("1,3", Run("Map.groupBy([1,2,3,4], function(x){return x%2===1;}).get(true).join(',');").AsString());
    }

    [Fact]
    public void MapGroupByObjectKey()
    {
        // Object keys retain identity through Map.
        Assert.Equal(2d, Run("var k={}; Map.groupBy([1,2], function(){return k;}).get(k).length;").AsNumber());
    }
}
