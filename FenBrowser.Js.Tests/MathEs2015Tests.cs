using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MathEs2015Tests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Theory]
    [InlineData("Math.sign(5);", 1.0)]
    [InlineData("Math.sign(-5);", -1.0)]
    [InlineData("Math.sign(0);", 0.0)]
    [InlineData("Math.sign(-0);", -0.0)]
    [InlineData("Math.sign(Infinity);", 1.0)]
    [InlineData("Math.sign(-Infinity);", -1.0)]
    public void Sign(string source, double expected)
    {
        var actual = Run(source);
        Assert.Equal(expected, actual);
        // distinguish +0 and -0 by sign bit
        if (expected == 0d)
        {
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected), System.BitConverter.DoubleToInt64Bits(actual));
        }
    }

    [Fact]
    public void SignOfNaNIsNaN() => Assert.True(double.IsNaN(Run("Math.sign(NaN);")));

    [Theory]
    [InlineData("Math.trunc(1.7);", 1.0)]
    [InlineData("Math.trunc(-1.7);", -1.0)]
    [InlineData("Math.trunc(0);", 0.0)]
    [InlineData("Math.trunc(13);", 13.0)]
    public void Trunc(string source, double expected) => Assert.Equal(expected, Run(source));

    [Fact]
    public void TruncOfNaNIsNaN() => Assert.True(double.IsNaN(Run("Math.trunc(NaN);")));

    [Fact]
    public void TruncOfInfinityIsInfinity()
    {
        Assert.True(double.IsPositiveInfinity(Run("Math.trunc(Infinity);")));
        Assert.True(double.IsNegativeInfinity(Run("Math.trunc(-Infinity);")));
    }

    [Theory]
    [InlineData("Math.cbrt(27);", 3.0)]
    [InlineData("Math.cbrt(-8);", -2.0)]
    [InlineData("Math.cbrt(0);", 0.0)]
    public void Cbrt(string source, double expected) => Assert.Equal(expected, Run(source));

    [Fact]
    public void Log2() => Assert.Equal(3.0, Run("Math.log2(8);"));

    [Fact]
    public void Log10() => Assert.Equal(2.0, Run("Math.log10(100);"));
}
