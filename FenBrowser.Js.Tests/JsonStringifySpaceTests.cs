using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JsonStringifySpaceTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void NumericIndentInsertsSpacesAndNewlines()
    {
        Assert.Equal("{\n  \"a\": 1\n}", RunStr("JSON.stringify({a:1}, null, 2);"));
    }

    [Fact]
    public void StringIndentLiteral()
    {
        Assert.Equal("{\n>>>\"a\": 1\n}", RunStr("JSON.stringify({a:1}, null, '>>>');"));
    }

    [Fact]
    public void NestedObjectIndentsProgressively()
    {
        Assert.Equal("{\n  \"a\": {\n    \"b\": 2\n  }\n}", RunStr("JSON.stringify({a:{b:2}}, null, 2);"));
    }

    [Fact]
    public void ArrayIndents()
    {
        Assert.Equal("[\n  1,\n  2\n]", RunStr("JSON.stringify([1,2], null, 2);"));
    }

    [Fact]
    public void NumericIndentClampsTo10()
    {
        Assert.Equal("{\n          \"a\": 1\n}", RunStr("JSON.stringify({a:1}, null, 99);"));
    }

    [Fact]
    public void ZeroIndentStaysCompact()
    {
        Assert.Equal("{\"a\":1}", RunStr("JSON.stringify({a:1}, null, 0);"));
    }

    [Fact]
    public void EmptyContainersStayOnOneLine()
    {
        Assert.Equal("{}", RunStr("JSON.stringify({}, null, 2);"));
        Assert.Equal("[]", RunStr("JSON.stringify([], null, 2);"));
    }
}
