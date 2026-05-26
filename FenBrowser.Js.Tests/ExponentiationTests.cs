using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ExponentiationTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void BasicExponentiation()
    {
        Assert.Equal(8, RunNum("2 ** 3;"));
    }

    [Fact]
    public void ExponentZero()
    {
        Assert.Equal(1, RunNum("5 ** 0;"));
    }

    [Fact]
    public void ExponentOne()
    {
        Assert.Equal(7, RunNum("7 ** 1;"));
    }

    [Fact]
    public void NegativeBase()
    {
        Assert.Equal(-8, RunNum("(-2) ** 3;"));
    }

    [Fact]
    public void FractionalExponent()
    {
        Assert.Equal(3, RunNum("9 ** 0.5;"));
    }

    [Fact]
    public void BigIntExponentiation()
    {
        var result = Run("2n ** 3n;");
        Assert.Equal(JsValueTag.BigInt, result.Tag);
    }

    [Fact]
    public void ExponentiationAssignment()
    {
        Assert.Equal(8, RunNum("var x = 2; x **= 3; x;"));
    }

    [Fact]
    public void ExponentiationInExpression()
    {
        Assert.Equal(11, RunNum("2 ** 3 + 3;"));
    }
}
