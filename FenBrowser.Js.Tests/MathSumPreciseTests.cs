using System;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Regression tests for JSLIB-001/JSLIB-004: Math.sumPrecise must keep
/// non-finite states sticky and preserve a lone -0 result.
/// </summary>
public sealed class MathSumPreciseTests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static double Sum(string arrayLiteral) => Run($"Math.sumPrecise({arrayLiteral});");

    private static void AssertSum(string arrayLiteral, string expected)
    {
        var actual = Sum(arrayLiteral);
        switch (expected)
        {
            case "NaN":
                Assert.True(double.IsNaN(actual), $"Expected NaN for {arrayLiteral}, got {actual}.");
                break;
            case "+Infinity":
                Assert.True(double.IsPositiveInfinity(actual), $"Expected +Infinity for {arrayLiteral}, got {actual}.");
                break;
            case "-Infinity":
                Assert.True(double.IsNegativeInfinity(actual), $"Expected -Infinity for {arrayLiteral}, got {actual}.");
                break;
            case "+0":
                Assert.Equal(0d, actual);
                Assert.Equal(BitConverter.DoubleToInt64Bits(0d), BitConverter.DoubleToInt64Bits(actual));
                break;
            case "-0":
                Assert.Equal(0d, actual);
                Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(actual));
                break;
            default:
                Assert.Equal(double.Parse(expected), actual);
                break;
        }
    }

    [Theory]
    [InlineData("[Infinity, 1]", "+Infinity")]
    [InlineData("[-Infinity, 1]", "-Infinity")]
    [InlineData("[Infinity, -Infinity, 5]", "NaN")]
    [InlineData("[1, Infinity]", "+Infinity")]
    [InlineData("[1, -Infinity]", "-Infinity")]
    [InlineData("[5, Infinity, -Infinity]", "NaN")]
    [InlineData("[Infinity, -Infinity]", "NaN")]
    public void NonFiniteStates_AreSticky_AgainstLaterFiniteValues(string arrayLiteral, string expected)
        => AssertSum(arrayLiteral, expected);

    [Theory]
    [InlineData("[]", "-0")]
    [InlineData("[-0]", "-0")]
    [InlineData("[-0, -0]", "-0")]
    [InlineData("[0]", "+0")]
    [InlineData("[0, -0]", "+0")]
    [InlineData("[-0, 1]", "1")]
    [InlineData("[10, 20, 30]", "60")]
    public void ZeroAndFiniteCases_MatchSpecification(string arrayLiteral, string expected)
        => AssertSum(arrayLiteral, expected);
}
