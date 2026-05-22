using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberStaticPredicateTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("Number.isFinite(0);", true)]
    [InlineData("Number.isFinite(1.5);", true)]
    [InlineData("Number.isFinite(NaN);", false)]
    [InlineData("Number.isFinite(Infinity);", false)]
    [InlineData("Number.isFinite(-Infinity);", false)]
    [InlineData("Number.isFinite('1');", false)] // no coercion (21.1.2.2)
    [InlineData("Number.isFinite();", false)]
    public void IsFinite(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Theory]
    [InlineData("Number.isNaN(NaN);", true)]
    [InlineData("Number.isNaN(0);", false)]
    [InlineData("Number.isNaN('NaN');", false)] // no coercion (21.1.2.4)
    [InlineData("Number.isNaN();", false)]
    public void IsNaN(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Theory]
    [InlineData("Number.isInteger(1);", true)]
    [InlineData("Number.isInteger(-1);", true)]
    [InlineData("Number.isInteger(0);", true)]
    [InlineData("Number.isInteger(1.5);", false)]
    [InlineData("Number.isInteger(NaN);", false)]
    [InlineData("Number.isInteger(Infinity);", false)]
    [InlineData("Number.isInteger('1');", false)] // no coercion (21.1.2.3)
    [InlineData("Number.isInteger();", false)]
    public void IsInteger(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Theory]
    [InlineData("Number.isSafeInteger(1);", true)]
    [InlineData("Number.isSafeInteger(9007199254740991);", true)]   // 2**53 - 1
    [InlineData("Number.isSafeInteger(-9007199254740991);", true)]
    [InlineData("Number.isSafeInteger(9007199254740992);", false)]  // 2**53
    [InlineData("Number.isSafeInteger(1.5);", false)]
    [InlineData("Number.isSafeInteger(NaN);", false)]
    [InlineData("Number.isSafeInteger(Infinity);", false)]
    [InlineData("Number.isSafeInteger('1');", false)]
    public void IsSafeInteger(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }
}
