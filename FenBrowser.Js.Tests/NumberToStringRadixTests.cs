using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberToStringRadixTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("new Number(255).toString(16);", "ff")]
    [InlineData("new Number(255).toString();", "255")]
    [InlineData("new Number(10).toString(2);", "1010")]
    [InlineData("new Number(36).toString(36);", "10")]
    [InlineData("new Number(0).toString(2);", "0")]
    [InlineData("new Number(-15).toString(16);", "-f")]
    [InlineData("new Number(35).toString(36);", "z")]
    [InlineData("new Number(NaN).toString(2);", "NaN")]
    [InlineData("new Number(Infinity).toString(16);", "Infinity")]
    public void ToStringWithRadix(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void DefaultRadixIsDecimal()
    {
        Assert.Equal("42", RunStr("new Number(42).toString();"));
    }

    [Fact]
    public void RejectsOutOfRangeRadix()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(10).toString(1);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(10).toString(37);"));
    }
}
