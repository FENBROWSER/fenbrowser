using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MathBitOpsTests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Theory]
    [InlineData("Math.hypot(3, 4);", 5.0)]
    [InlineData("Math.hypot();", 0.0)]
    [InlineData("Math.hypot(5);", 5.0)]
    [InlineData("Math.hypot(0, 0);", 0.0)]
    [InlineData("Math.hypot(Infinity, NaN);", double.PositiveInfinity)]
    [InlineData("Math.hypot(-Infinity, 1);", double.PositiveInfinity)]
    public void Hypot(string source, double expected) => Assert.Equal(expected, Run(source));

    [Fact]
    public void HypotNaN() => Assert.True(double.IsNaN(Run("Math.hypot(1, NaN);")));

    [Theory]
    [InlineData("Math.clz32(1);", 31.0)]
    [InlineData("Math.clz32(0);", 32.0)]
    [InlineData("Math.clz32(NaN);", 32.0)]
    [InlineData("Math.clz32(Infinity);", 32.0)]
    [InlineData("Math.clz32(-1);", 0.0)]   // -1 -> 0xFFFFFFFF
    [InlineData("Math.clz32(0x80000000);", 0.0)]
    [InlineData("Math.clz32(0x00010000);", 15.0)]
    public void Clz32(string source, double expected) => Assert.Equal(expected, Run(source));

    [Theory]
    [InlineData("Math.imul(3, 4);", 12.0)]
    [InlineData("Math.imul(-1, 8);", -8.0)]
    [InlineData("Math.imul(0xFFFFFFFF, 1);", -1.0)] // -1 * 1 = -1
    [InlineData("Math.imul(0x10000, 0x10000);", 0.0)] // 2**32 wraps to 0
    public void Imul(string source, double expected) => Assert.Equal(expected, Run(source));

    [Fact]
    public void FroundRoundsToFloat32()
    {
        // 0.1 is not exact in float32; the rounded value differs from double 0.1.
        var rounded = Run("Math.fround(1.1);");
        Assert.Equal((double)(float)1.1, rounded);
    }

    [Fact]
    public void FroundOfIntegerIsExact() => Assert.Equal(5.0, Run("Math.fround(5);"));

    [Fact]
    public void FroundOfNaNIsNaN() => Assert.True(double.IsNaN(Run("Math.fround(NaN);")));
}
