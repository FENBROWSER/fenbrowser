using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayToSplicedWithTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ToSplicedRemovesAndInserts()
    {
        Assert.Equal("1,9,8,4", Run("[1,2,3,4].toSpliced(1,2,9,8).join(',');").AsString());
    }

    [Fact]
    public void ToSplicedRemoveOnly()
    {
        Assert.Equal("1,4", Run("[1,2,3,4].toSpliced(1,2).join(',');").AsString());
    }

    [Fact]
    public void ToSplicedNegativeStart()
    {
        Assert.Equal("1,2,9", Run("[1,2,3,4].toSpliced(-2,2,9).join(',');").AsString());
    }

    [Fact]
    public void ToSplicedDoesNotMutate()
    {
        Assert.Equal("1,2,3,4", Run("var a=[1,2,3,4]; a.toSpliced(0,2); a.join(',');").AsString());
    }

    [Fact]
    public void WithReplacesIndex()
    {
        Assert.Equal("1,9,3", Run("[1,2,3].with(1,9).join(',');").AsString());
    }

    [Fact]
    public void WithNegativeIndex()
    {
        Assert.Equal("1,2,9", Run("[1,2,3].with(-1,9).join(',');").AsString());
    }

    [Fact]
    public void WithDoesNotMutate()
    {
        Assert.Equal("1,2,3", Run("var a=[1,2,3]; a.with(0,99); a.join(',');").AsString());
    }

    [Fact]
    public void WithOutOfRangeThrowsRangeError()
    {
        Assert.Throws<JsThrownException>(() => Run("[1,2,3].with(10,99);"));
        Assert.Throws<JsThrownException>(() => Run("[1,2,3].with(-10,99);"));
    }
}
