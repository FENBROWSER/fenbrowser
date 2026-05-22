using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberToExpToPrecisionTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("new Number(100).toExponential(2);", "1.00e+2")]
    [InlineData("new Number(0).toExponential(3);", "0.000e+0")]
    [InlineData("new Number(NaN).toExponential();", "NaN")]
    [InlineData("new Number(Infinity).toExponential();", "Infinity")]
    [InlineData("new Number(-Infinity).toExponential(2);", "-Infinity")]
    public void ToExponential(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void ToExponentialRejectsOutOfRangeDigits()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toExponential(-1);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toExponential(101);"));
    }

    [Theory]
    [InlineData("new Number(3.14159).toPrecision(3);", "3.14")]
    [InlineData("new Number(0).toPrecision(1);", "0")]
    [InlineData("new Number(0).toPrecision(3);", "0.00")]
    [InlineData("new Number(NaN).toPrecision(3);", "NaN")]
    [InlineData("new Number(Infinity).toPrecision(3);", "Infinity")]
    public void ToPrecision(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void ToPrecisionWithoutArgsBehavesLikeToString()
    {
        Assert.Equal("3.14", RunStr("new Number(3.14).toPrecision();"));
    }

    [Fact]
    public void ToPrecisionRejectsOutOfRangePrecision()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toPrecision(0);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toPrecision(101);"));
    }
}
