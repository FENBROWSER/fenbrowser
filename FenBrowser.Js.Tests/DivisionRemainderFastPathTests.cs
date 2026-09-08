using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Division and remainder take a numeric fast path in both tiers. IEEE gives the
// same answers in both languages, but the edges are where that stops being
// obvious: a zero divisor, a negative zero result, and the sign of a remainder,
// which follows the dividend rather than the divisor.
public class DivisionRemainderFastPathTests
{
    private const int TierUp = 2000;

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("5 % 3", "2")]
    [InlineData("-5 % 3", "-2")]
    [InlineData("5 % -3", "2")]
    [InlineData("-5 % -3", "-2")]
    [InlineData("5.5 % 2", "1.5")]
    [InlineData("5 % 0", "NaN")]
    [InlineData("0 % 5", "0")]
    [InlineData("Infinity % 2", "NaN")]
    [InlineData("5 % Infinity", "5")]
    [InlineData("7 / 2", "3.5")]
    [InlineData("1 / 0", "Infinity")]
    [InlineData("-1 / 0", "-Infinity")]
    [InlineData("0 / 0", "NaN")]
    [InlineData("6 / 3", "2")]
    public void EdgeValues_MatchTheSpecification(string expression, string expected)
    {
        Assert.Equal(expected, Run($"String({expression});"));
    }

    [Fact]
    public void ANegativeZeroResult_StaysNegativeZero()
    {
        // Int32 cannot carry -0, so the fast path has to decline to re-tag it.
        var v = Run(@"
            [Object.is(-0 % 3, -0), Object.is(-1 / Infinity, -0), Object.is(0 % 3, 0), Object.is(-6 / 3, -2)].join(',');
        ");
        Assert.Equal("true,true,true,true", v);
    }

    [Fact]
    public void TheSameAnswersComeBackAfterTierUp()
    {
        // Long enough that the compiled body answers, not the dispatch loop.
        var v = Run($@"
            function work(i) {{ return (i % 7) + (i / 4) + (i % 0.5); }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = work(i);
            String(last);
        ");
        var i = TierUp - 1;
        var expected = (i % 7) + (i / 4.0) + (i % 0.5);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), v);
    }

    [Fact]
    public void ANonNumericOperand_StillTakesTheGeneralPath()
    {
        var v = Run($@"
            function divide(a, b) {{ return a / b; }}
            function remainder(a, b) {{ return a % b; }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = divide(6, 3) + ',' + remainder(7, 4);
            last + '|' + divide('6', 3) + ',' + remainder('7', 4) + ',' + divide({{}}, 2);
        ");
        Assert.Equal("2,3|2,3,NaN", v);
    }

    [Fact]
    public void BigIntOperands_AreNotDivertedThroughTheNumericPath()
    {
        var v = Run(@"
            var a = 7n, b = 2n;
            [(a / b).toString(), (a % b).toString(), typeof (a / b)].join(',');
        ");
        Assert.Equal("3,1,bigint", v);
    }

    [Fact]
    public void AnIntegralResult_KeepsItsIntegerTag()
    {
        // The tag decides which fast path the next operator can take.
        var v = Run($@"
            function masked(i) {{ return ((i / 2) | 0) + ((i % 256) & 255); }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = masked(i);
            String(last);
        ");
        var i = TierUp - 1;
        Assert.Equal(((i / 2) + (i % 256 & 255)).ToString(), v);
    }
}
