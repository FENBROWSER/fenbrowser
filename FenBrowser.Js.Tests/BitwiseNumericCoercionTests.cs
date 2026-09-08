using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A bitwise operator coerces through ToInt32 (ECMA-262 13.9, 13.12), so a
// number reaches it however it happens to be tagged. The dispatch loop settles
// those without entering the generic operator, which is where the modulo-2^32
// wrap, the infinities and NaN all have to come out right.
public class BitwiseNumericCoercionTests
{
    private const int TierUp = 2000;

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    // Values past the signed range wrap, which a plain cast would not do.
    [InlineData("2147483648 | 0", "-2147483648")]
    [InlineData("4294967296 | 0", "0")]
    [InlineData("1e21 | 0", "-559939584")]
    [InlineData("4294967295.5 | 0", "-1")]
    [InlineData("-1 >>> 0", "4294967295")]
    // Truncation is toward zero, not floor.
    [InlineData("1.9 | 0", "1")]
    [InlineData("-1.9 | 0", "-1")]
    // Non-finite operands become zero.
    [InlineData("NaN | 0", "0")]
    [InlineData("Infinity | 0", "0")]
    [InlineData("-Infinity | 0", "0")]
    // Shift counts are taken modulo 32.
    [InlineData("1 << 33", "2")]
    [InlineData("-8 >> 1", "-4")]
    [InlineData("-8 >>> 28", "15")]
    [InlineData("(2.5 ^ 0)", "2")]
    [InlineData("(7.9 & 3.9)", "3")]
    public void NumericOperands_CoerceThroughToInt32(string expression, string expected)
    {
        Assert.Equal(expected, Run($"String({expression});"));
    }

    [Fact]
    public void ADoubleTaggedOperand_GivesTheSameAnswerAsAnIntegerOne()
    {
        // x is forced to carry a Number tag; y carries an integer one. The
        // operator must not be able to tell.
        var v = Run(@"
            var x = 6.0 / 2;
            var y = 3;
            [(x & 1) === (y & 1), (x | 8) === (y | 8), (x ^ 5) === (y ^ 5),
             (x << 2) === (y << 2), (x >> 1) === (y >> 1), (x >>> 1) === (y >>> 1)].join(',');
        ");
        Assert.Equal("true,true,true,true,true,true", v);
    }

    [Fact]
    public void TheSameAnswersComeBackAfterTierUp()
    {
        var v = Run($@"
            function mix(i) {{ var f = i * 1.5; return ((f ^ 0xff) & 0xffff) | (f >> 2); }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = mix(i);
            String(last);
        ");
        var f = (TierUp - 1) * 1.5;
        var truncated = (int)f;
        var expected = ((truncated ^ 0xff) & 0xffff) | (truncated >> 2);
        Assert.Equal(expected.ToString(), v);
    }

    [Fact]
    public void AResultStaysTaggedAsAnInteger()
    {
        // The tag decides which fast path the operator after it can take.
        var v = Run($@"
            function chain(i) {{ var f = i * 1.5; var m = f | 0; return (m & 255) + (m % 7); }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = chain(i);
            String(last);
        ");
        var m = (int)((TierUp - 1) * 1.5);
        Assert.Equal(((m & 255) + (m % 7)).ToString(), v);
    }

    [Fact]
    public void ANonNumericOperand_StillTakesTheGeneralPath()
    {
        var v = Run(@"
            ['3' | 0, true | 0, null | 0, undefined | 0, [] | 0, ({}) | 0, '' | 0].join(',');
        ");
        Assert.Equal("3,1,0,0,0,0,0", v);
    }

    [Fact]
    public void BigIntOperands_AreNotDivertedThroughTheNumericPath()
    {
        var v = Run(@"
            var a = 12n, b = 10n;
            var threw = false;
            try { var bad = 1n | 1; } catch (e) { threw = e instanceof TypeError; }
            [(a & b).toString(), (a | b).toString(), (a ^ b).toString(), threw].join(',');
        ");
        Assert.Equal("8,14,6,true", v);
    }
}
