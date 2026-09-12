using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 21.1.3.3 Number.prototype.toFixed: n is the integer closest to
/// x * 10^f against the exact binary value of x, ties to the larger n, and the
/// sign is emitted only when x &lt; 0 (so -0 prints unsigned).
/// </summary>
public class NumberToFixedTests
{
    private static string RunString(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("(-0).toFixed(4)", "0.0000")]
    [InlineData("(-0.00001).toFixed(4)", "-0.0000")]
    [InlineData("(0.5).toFixed(0)", "1")]
    [InlineData("(2.5).toFixed(0)", "3")]
    [InlineData("(-2.5).toFixed(0)", "-3")]
    [InlineData("(1.005).toFixed(2)", "1.00")]
    [InlineData("(1.45).toFixed(1)", "1.4")]
    [InlineData("(0.000001).toFixed(7)", "0.0000010")]
    [InlineData("(1e21).toFixed(2)", "1e+21")]
    [InlineData("(0.1).toFixed(20)", "0.10000000000000000555")]
    [InlineData("(12345678901234567890).toFixed(2)", "12345678901234567168.00")]
    public void MatchesSpecRounding(string expression, string expected)
    {
        Assert.Equal(expected, RunString(expression));
    }
}
