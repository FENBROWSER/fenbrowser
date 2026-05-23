using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AggregateErrorTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void NameAndMessage()
    {
        Assert.Equal("AggregateError", Run("new AggregateError([], 'multi').name;").AsString());
        Assert.Equal("multi", Run("new AggregateError([], 'multi').message;").AsString());
    }

    [Fact]
    public void ErrorsArrayPopulatedFromArray()
    {
        Assert.Equal(2d, Run("new AggregateError([new Error('a'), new TypeError('b')]).errors.length;").AsNumber());
        Assert.Equal("a", Run("new AggregateError([new Error('a'), new TypeError('b')]).errors[0].message;").AsString());
    }

    [Fact]
    public void ErrorsArrayPopulatedFromIterable()
    {
        Assert.Equal(3d, Run("new AggregateError(new Set([1,2,3]), '').errors.length;").AsNumber());
    }

    [Fact]
    public void InheritsErrorPrototype()
    {
        Assert.True(Run("new AggregateError([]) instanceof Error;").AsBoolean());
    }

    [Fact]
    public void NonIterableFirstArgThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("new AggregateError(undefined);"));
    }
}
