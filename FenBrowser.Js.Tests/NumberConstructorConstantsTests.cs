using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberConstructorConstantsTests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void EpsilonEqualsTwoToTheMinusFiftyTwo()
    {
        // ECMA-262 21.1.2.1
        Assert.Equal(System.Math.Pow(2d, -52), Run("Number.EPSILON;"));
    }

    [Fact]
    public void MaxSafeIntegerEquals2Pow53Minus1()
    {
        // ECMA-262 21.1.2.6
        Assert.Equal(9007199254740991d, Run("Number.MAX_SAFE_INTEGER;"));
    }

    [Fact]
    public void MinSafeIntegerEqualsNegativeOfMaxSafeInteger()
    {
        // ECMA-262 21.1.2.8
        Assert.Equal(-9007199254740991d, Run("Number.MIN_SAFE_INTEGER;"));
    }

    [Fact]
    public void MaxValueAndPositiveInfinityStillCorrect()
    {
        Assert.Equal(double.MaxValue, Run("Number.MAX_VALUE;"));
        Assert.True(double.IsPositiveInfinity(Run("Number.POSITIVE_INFINITY;")));
    }
}
