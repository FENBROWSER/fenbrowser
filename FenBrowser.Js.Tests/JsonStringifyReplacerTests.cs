using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JsonStringifyReplacerTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ArrayReplacerFiltersObjectKeys()
    {
        Assert.Equal("{\"a\":1,\"c\":3}", RunStr("JSON.stringify({a:1,b:2,c:3}, ['a','c']);"));
    }

    [Fact]
    public void ArrayReplacerDoesNotAffectArrays()
    {
        Assert.Equal("[1,2,3]", RunStr("JSON.stringify([1,2,3], ['0']);"));
    }

    [Fact]
    public void FunctionReplacerTransformsValues()
    {
        Assert.Equal("{\"a\":\"2\"}", RunStr("JSON.stringify({a:1}, function(k,v){return typeof v==='number'?String(v+1):v;});"));
    }

    [Fact]
    public void FunctionReplacerCanOmitKey()
    {
        Assert.Equal("{\"a\":1}", RunStr("JSON.stringify({a:1,b:2}, function(k,v){return k==='b'?undefined:v;});"));
    }

    [Fact]
    public void NonCallableNonArrayReplacerIgnored()
    {
        Assert.Equal("{\"a\":1}", RunStr("JSON.stringify({a:1}, 'not-a-replacer');"));
    }

    [Fact]
    public void NumericKeysInArrayReplacerCoerced()
    {
        Assert.Equal("{\"5\":\"x\"}", RunStr("JSON.stringify({5:'x', 6:'y'}, [5]);"));
    }
}
