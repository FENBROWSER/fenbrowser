using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberFormattingArgumentCoercionTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    [Theory]
    [InlineData("(12.4).toFixed(NaN);", "12")]
    [InlineData("(123.456).toExponential(NaN);", "1e+2")]
    public void NaNFormattingArgumentsCoerceToZero(string source, string expected)
    {
        Assert.Equal(expected, Run(source).AsString());
    }

    [Fact]
    public void NaNPrecisionCoercesToZeroAndIsRejected()
    {
        Assert.Throws<JsThrownException>(() => Run("(12.5).toPrecision(NaN);"));
    }

    [Theory]
    [InlineData("(1).toFixed(Infinity);")]
    [InlineData("(1).toExponential(-Infinity);")]
    [InlineData("(1).toPrecision(Infinity);")]
    [InlineData("(1).toString(NaN);")]
    [InlineData("(1).toString(Infinity);")]
    [InlineData("1n.toString(NaN);")]
    [InlineData("1n.toString(-Infinity);")]
    public void NonFiniteOutOfRangeArgumentsThrowRangeError(string source)
    {
        Assert.Throws<JsThrownException>(() => Run(source));
    }

    [Theory]
    [InlineData("NaN.toExponential(Infinity);", "NaN")]
    [InlineData("Infinity.toPrecision(Infinity);", "Infinity")]
    public void NonFiniteReceiversReturnBeforeRangeValidation(string source, string expected)
    {
        Assert.Equal(expected, Run(source).AsString());
    }

    [Theory]
    [InlineData("(255).toString(16.9);", "ff")]
    [InlineData("255n.toString(16.9);", "ff")]
    public void FractionalRadixIsTruncatedTowardZero(string source, string expected)
    {
        Assert.Equal(expected, Run(source).AsString());
    }

    [Theory]
    [InlineData("(2 ** 64).toString(16);", "10000000000000000")]
    [InlineData("(-(2 ** 64)).toString(16);", "-10000000000000000")]
    public void RadixConversionSupportsIntegersBeyondInt64(string source, string expected)
    {
        Assert.Equal(expected, Run(source).AsString());
    }
}
