using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MathHyperbolicTests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact] public void Sinh0Is0() => Assert.Equal(0d, Run("Math.sinh(0);"));
    [Fact] public void Cosh0Is1() => Assert.Equal(1d, Run("Math.cosh(0);"));
    [Fact] public void Tanh0Is0() => Assert.Equal(0d, Run("Math.tanh(0);"));
    [Fact] public void TanhInfinityIs1() => Assert.Equal(1d, Run("Math.tanh(Infinity);"));
    [Fact] public void TanhNegInfinityIsMinus1() => Assert.Equal(-1d, Run("Math.tanh(-Infinity);"));

    [Fact] public void Asinh0Is0() => Assert.Equal(0d, Run("Math.asinh(0);"));
    [Fact] public void Acosh1Is0() => Assert.Equal(0d, Run("Math.acosh(1);"));
    [Fact] public void AcoshOf0IsNaN() => Assert.True(double.IsNaN(Run("Math.acosh(0);")));
    [Fact] public void Atanh0Is0() => Assert.Equal(0d, Run("Math.atanh(0);"));
    [Fact] public void Atanh1IsInfinity() => Assert.True(double.IsPositiveInfinity(Run("Math.atanh(1);")));

    [Fact] public void Expm1Of0Is0() => Assert.Equal(0d, Run("Math.expm1(0);"));
    [Fact] public void Expm1OfNegInfinityIsMinus1() => Assert.Equal(-1d, Run("Math.expm1(-Infinity);"));
    [Fact] public void Expm1OfNaNIsNaN() => Assert.True(double.IsNaN(Run("Math.expm1(NaN);")));

    [Fact] public void Log1pOf0Is0() => Assert.Equal(0d, Run("Math.log1p(0);"));
    [Fact] public void Log1pOfMinus1IsNegInfinity() => Assert.True(double.IsNegativeInfinity(Run("Math.log1p(-1);")));
    [Fact] public void Log1pOfMinus2IsNaN() => Assert.True(double.IsNaN(Run("Math.log1p(-2);")));
}
