using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SymbolPrimitiveTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static string RunStr(string source) => Run(source).AsString();
    private static bool RunBool(string source) => Run(source).AsBoolean();

    [Fact]
    public void TypeofSymbol() => Assert.Equal("symbol", RunStr("typeof Symbol();"));

    [Fact]
    public void EachSymbolIsUnique() => Assert.False(RunBool("Symbol() === Symbol();"));

    [Fact]
    public void SameSymbolEqualsItself()
    {
        Assert.True(RunBool("var s = Symbol(); s === s;"));
    }

    [Fact]
    public void SymbolWithSameDescriptionStillUnique()
    {
        Assert.False(RunBool("Symbol('x') === Symbol('x');"));
    }

    [Fact]
    public void NewSymbolThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("new Symbol();"));
    }

    [Fact]
    public void WellKnownSymbolsAreStable()
    {
        Assert.True(RunBool("Symbol.iterator === Symbol.iterator;"));
        Assert.True(RunBool("Symbol.asyncIterator === Symbol.asyncIterator;"));
    }

    [Fact]
    public void WellKnownSymbolsAreDistinct()
    {
        Assert.False(RunBool("Symbol.iterator === Symbol.asyncIterator;"));
        Assert.False(RunBool("Symbol.match === Symbol.replace;"));
    }

    [Fact]
    public void WellKnownSymbolsAreNotEqualToFreshSymbol()
    {
        Assert.False(RunBool("Symbol() === Symbol.iterator;"));
    }

    [Fact]
    public void SymbolTypeofThroughVariable()
    {
        Assert.Equal("symbol", RunStr("var s = Symbol('test'); typeof s;"));
    }

    [Fact]
    public void StringConcatProducesSymbolDescriptionForm()
    {
        Assert.Equal("Symbol(hi)", RunStr("'' + Symbol('hi');"));
    }
}
