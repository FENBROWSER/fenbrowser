using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberToFixedTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("new Number(1.5).toFixed(0);", "2")]
    [InlineData("new Number(3.14159).toFixed(2);", "3.14")]
    [InlineData("new Number(0).toFixed(3);", "0.000")]
    [InlineData("new Number(-1.5).toFixed(0);", "-2")]
    [InlineData("new Number(1).toFixed();", "1")]
    [InlineData("new Number(NaN).toFixed(2);", "NaN")]
    [InlineData("new Number(Infinity).toFixed(2);", "Infinity")]
    [InlineData("new Number(-Infinity).toFixed(2);", "-Infinity")]
    public void ToFixed(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void OutOfRangeDigitsThrows()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toFixed(-1);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toFixed(101);"));
    }
}
