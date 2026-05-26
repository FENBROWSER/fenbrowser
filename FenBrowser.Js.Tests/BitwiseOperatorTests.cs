using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BitwiseOperatorTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    // -- BitAnd (&) --

    [Fact]
    public void BitAnd_Basic() => Assert.Equal(1, RunNum("5 & 3;"));

    [Fact]
    public void BitAnd_NegativeAndMask() => Assert.Equal(255, RunNum("(-1) & 255;"));

    [Fact]
    public void BitAnd_ZeroWithAnything() => Assert.Equal(0, RunNum("0 & 42;"));

    [Fact]
    public void BitAnd_Self() => Assert.Equal(5, RunNum("5 & 5;"));

    // -- BitOr (|) --

    [Fact]
    public void BitOr_Basic() => Assert.Equal(7, RunNum("5 | 3;"));

    [Fact]
    public void BitOr_ZeroWithZero() => Assert.Equal(0, RunNum("0 | 0;"));

    [Fact]
    public void BitOr_Self() => Assert.Equal(5, RunNum("5 | 5;"));

    // -- BitXor (^) --

    [Fact]
    public void BitXor_Basic() => Assert.Equal(6, RunNum("5 ^ 3;"));

    [Fact]
    public void BitXor_Self() => Assert.Equal(0, RunNum("5 ^ 5;"));

    [Fact]
    public void BitXor_WithZero() => Assert.Equal(5, RunNum("5 ^ 0;"));

    // -- BitNot (~) --

    [Fact]
    public void BitNot_Zero() => Assert.Equal(-1, RunNum("~0;"));

    [Fact]
    public void BitNot_NegativeOne() => Assert.Equal(0, RunNum("~(-1);"));

    [Fact]
    public void BitNot_One() => Assert.Equal(-2, RunNum("~1;"));

    [Fact]
    public void BitNot_Two() => Assert.Equal(-3, RunNum("~2;"));

    // -- ShiftLeft (<<) --

    [Fact]
    public void ShiftLeft_Basic() => Assert.Equal(8, RunNum("1 << 3;"));

    [Fact]
    public void ShiftLeft_Negative() => Assert.Equal(-2, RunNum("-1 << 1;"));

    [Fact]
    public void ShiftLeft_MaskShiftCount() => Assert.Equal(1, RunNum("1 << 32;"));

    [Fact]
    public void ShiftLeft_ByZero() => Assert.Equal(5, RunNum("5 << 0;"));

    // -- ShiftRight (>>) --

    [Fact]
    public void ShiftRight_Basic() => Assert.Equal(2, RunNum("8 >> 2;"));

    [Fact]
    public void ShiftRight_SignPropagating() => Assert.Equal(-2, RunNum("-8 >> 2;"));

    [Fact]
    public void ShiftRight_ByZero() => Assert.Equal(5, RunNum("5 >> 0;"));

    // -- UnsignedShiftRight (>>>) --

    [Fact]
    public void UnsignedShiftRight_Basic() => Assert.Equal(2, RunNum("8 >>> 2;"));

    [Fact]
    public void UnsignedShiftRight_NegativeToLarge() =>
        Assert.Equal(4294967295d, RunNum("-1 >>> 0;"));

    [Fact]
    public void UnsignedShiftRight_ByZero() => Assert.Equal(5, RunNum("5 >>> 0;"));

    // -- Compound assignment --

    [Fact]
    public void Compound_BitAnd() => Assert.Equal(1, RunNum("var x = 5; x &= 3; x;"));

    [Fact]
    public void Compound_BitOr() => Assert.Equal(7, RunNum("var x = 5; x |= 3; x;"));

    [Fact]
    public void Compound_BitXor() => Assert.Equal(6, RunNum("var x = 5; x ^= 3; x;"));

    [Fact]
    public void Compound_ShiftLeft() => Assert.Equal(8, RunNum("var x = 1; x <<= 3; x;"));

    [Fact]
    public void Compound_ShiftRight() => Assert.Equal(2, RunNum("var x = 8; x >>= 2; x;"));

    [Fact]
    public void Compound_UnsignedShiftRight() =>
        Assert.Equal(4294967295d, RunNum("var x = -1; x >>>= 0; x;"));

    // -- Precedence (| < ^ < & < Equality < Relational < Shift < Additive < Mult) --

    [Fact]
    public void Precedence_BitAndBindsTighterThanBitOr() =>
        Assert.Equal(3, RunNum("1 | 2 & 3;"));

    [Fact]
    public void Precedence_EqualityBindsTighterThanBitAnd() =>
        // 0 & 1 == 1  =  0 & (1 == 1)  =  0 & 1  =  0
        Assert.Equal(0, RunNum("0 & 1 == 1;"));

    [Fact]
    public void Precedence_AdditiveBindsTighterThanBitwise() =>
        Assert.Equal(3, RunNum("1 + 2 & 3;"));

    [Fact]
    public void Precedence_ShiftBindsTighterThanBitAnd() =>
        Assert.Equal(0, RunNum("1 & 2 << 1;"));

    [Fact]
    public void Precedence_BitAndBindsTighterThanLogicalAnd() =>
        Assert.Equal(0, RunNum("1 & 3 && 0;"));
}
