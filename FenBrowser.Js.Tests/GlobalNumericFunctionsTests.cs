using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GlobalNumericFunctionsTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("parseInt('42');", 42.0)]
    [InlineData("parseInt('  -7  ');", -7.0)]
    [InlineData("parseInt('+9');", 9.0)]
    [InlineData("parseInt('0xff');", 255.0)]
    [InlineData("parseInt('0xff', 16);", 255.0)]
    [InlineData("parseInt('ff', 16);", 255.0)]
    [InlineData("parseInt('10', 2);", 2.0)]
    [InlineData("parseInt('z', 36);", 35.0)]
    [InlineData("parseInt('abc');", double.NaN)]
    [InlineData("parseInt('', 10);", double.NaN)]
    [InlineData("parseInt('10', 1);", double.NaN)]   // radix out of [2,36]
    [InlineData("parseInt('10', 37);", double.NaN)]
    [InlineData("parseInt('123abc');", 123.0)]       // stops at first invalid
    public void ParseInt(string source, double expected)
    {
        var actual = RunNum(source);
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual));
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("parseFloat('3.14');", 3.14)]
    [InlineData("parseFloat('-2.5');", -2.5)]
    [InlineData("parseFloat('1e3');", 1000.0)]
    [InlineData("parseFloat('Infinity');", double.PositiveInfinity)]
    [InlineData("parseFloat('-Infinity');", double.NegativeInfinity)]
    [InlineData("parseFloat('abc');", double.NaN)]
    public void ParseFloat(string source, double expected)
    {
        var actual = RunNum(source);
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual));
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("isNaN(NaN);", true)]
    [InlineData("isNaN('NaN');", true)]    // coerces via ToNumber
    [InlineData("isNaN(0);", false)]
    [InlineData("isNaN('5');", false)]
    public void IsNaNGlobal(string source, bool expected) => Assert.Equal(expected, RunBool(source));

    [Theory]
    [InlineData("isFinite(1);", true)]
    [InlineData("isFinite('5');", true)]   // coerces
    [InlineData("isFinite(Infinity);", false)]
    [InlineData("isFinite(NaN);", false)]
    public void IsFiniteGlobal(string source, bool expected) => Assert.Equal(expected, RunBool(source));

    [Fact]
    public void NumberParseIntIsSameFunctionAsGlobal()
    {
        Assert.True(RunBool("Number.parseInt === parseInt;"));
    }

    [Fact]
    public void NumberParseFloatIsSameFunctionAsGlobal()
    {
        Assert.True(RunBool("Number.parseFloat === parseFloat;"));
    }
}
