using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BigIntTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void BigIntLiteralTypeof()
    {
        Assert.Equal("bigint", Run("typeof 123n;").AsString());
    }

    [Fact]
    public void BigIntLiteralValue()
    {
        Assert.True(Run("123n === 123n;").AsBoolean());
    }

    [Fact]
    public void BigIntHexLiteral()
    {
        Assert.True(Run("typeof 0xFFn === 'bigint';").AsBoolean());
    }

    [Fact]
    public void BigIntHexValue()
    {
        // Verify hex BigInt equals the same decimal BigInt value
        Assert.True(Run("0xFFn === 255n;").AsBoolean());
    }

    [Fact]
    public void BigIntSameValueStrictEqual()
    {
        Assert.True(Run("255n === 255n;").AsBoolean());
    }

    [Fact]
    public void BigIntOctalLiteral()
    {
        Assert.True(Run("0o7n == 7n;").AsBoolean());
    }

    [Fact]
    public void BigIntBinaryLiteral()
    {
        Assert.True(Run("0b1010n == 10n;").AsBoolean());
    }

    [Fact]
    public void BigIntAddition()
    {
        Assert.True(Run("1n + 2n === 3n;").AsBoolean());
    }

    [Fact]
    public void BigIntSubtraction()
    {
        Assert.True(Run("5n - 2n === 3n;").AsBoolean());
    }

    [Fact]
    public void BigIntMultiplication()
    {
        Assert.True(Run("2n * 3n === 6n;").AsBoolean());
    }

    [Fact]
    public void BigIntDivision()
    {
        Assert.True(Run("5n / 2n === 2n;").AsBoolean());
    }

    [Fact]
    public void BigIntModulo()
    {
        Assert.True(Run("5n % 2n === 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntStrictEquality()
    {
        Assert.True(Run("1n === 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntStrictInequalityDifferent()
    {
        Assert.True(Run("1n !== 2n;").AsBoolean());
    }

    [Fact]
    public void BigIntLooseEquality()
    {
        Assert.True(Run("1n == 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntLessThan()
    {
        Assert.True(Run("1n < 2n;").AsBoolean());
    }

    [Fact]
    public void BigIntGreaterThan()
    {
        Assert.True(Run("2n > 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntLessThanOrEqual()
    {
        Assert.True(Run("1n <= 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntGreaterThanOrEqual()
    {
        Assert.True(Run("2n >= 1n;").AsBoolean());
    }

    [Fact]
    public void BigIntMixedWithNumberThrowsInAdd()
    {
        var code = @"var ok = false; try { 1n + 1; } catch (e) { ok = e instanceof TypeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntMixedWithNumberThrowsInSub()
    {
        var code = @"var ok = false; try { 1n - 1; } catch (e) { ok = e instanceof TypeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntMixedWithNumberThrowsInMul()
    {
        var code = @"var ok = false; try { 1n * 1; } catch (e) { ok = e instanceof TypeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntDivideByZeroThrowsRangeError()
    {
        // ECMA-262 6.1.6.2.5 BigInt::divide: "If y is 0ℤ, throw a RangeError."
        // Previously the underlying BigInteger divide threw DivideByZeroException and
        // crashed the host instead of surfacing a catchable JS RangeError.
        var code = @"var ok = false; try { 5n / 0n; } catch (e) { ok = e instanceof RangeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntModuloByZeroThrowsRangeError()
    {
        // ECMA-262 6.1.6.2.6 BigInt::remainder: "If d is 0ℤ, throw a RangeError."
        var code = @"var ok = false; try { 5n % 0n; } catch (e) { ok = e instanceof RangeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntVariableAssignment()
    {
        Assert.True(Run("var x = 42n; x === 42n;").AsBoolean());
    }

    [Fact]
    public void BigIntLargeValue()
    {
        Assert.True(Run("9007199254740993n === 9007199254740993n;").AsBoolean());
    }

    // Verify BigInts are not equal to numbers.
    [Fact]
    public void BigIntNotStrictlyEqualToNumber()
    {
        Assert.True(Run("1n !== 1;").AsBoolean());
    }

    // Unary minus on BigInt literal.
    [Fact]
    public void BigIntUnaryMinus()
    {
        Assert.True(Run("-1n === -1n;").AsBoolean());
    }

    [Fact]
    public void BigIntGlobalConstructorExists()
    {
        Assert.True(Run("typeof BigInt === 'function';").AsBoolean());
    }

    [Fact]
    public void BigIntDecimalLiteralSupportsNumericSeparators()
    {
        Assert.True(Run("1_234_567n === 1234567n;").AsBoolean());
    }

    [Fact]
    public void InvalidBigIntLiteralThrowsParserException()
    {
        var compiler = new BytecodeCompiler();
        Assert.Throws<JsParserException>(() => compiler.CompileScript(new SourceText("0x_ggn;")));
    }

    [Fact]
    public void BigIntConstructorConvertsIntegerNumber()
    {
        Assert.True(Run("BigInt(123) === 123n;").AsBoolean());
    }

    [Fact]
    public void BigIntConstructorConvertsBoolean()
    {
        Assert.True(Run("BigInt(true) === 1n && BigInt(false) === 0n;").AsBoolean());
    }

    [Fact]
    public void BigIntConstructorParsesPrefixedStrings()
    {
        Assert.True(Run("BigInt('0x10') === 16n && BigInt('0o10') === 8n && BigInt('0b10') === 2n;").AsBoolean());
    }

    [Fact]
    public void BigIntConstructorRejectsNonIntegerNumber()
    {
        var code = @"var ok = false; try { BigInt(1.1); } catch (e) { ok = e instanceof RangeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntConstructorRejectsInvalidString()
    {
        var code = @"var ok = false; try { BigInt('12.5'); } catch (e) { ok = e instanceof SyntaxError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void NewBigIntThrowsTypeError()
    {
        var code = @"var ok = false; try { new BigInt(1); } catch (e) { ok = e instanceof TypeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntAsIntNWrapsSigned()
    {
        Assert.True(Run("BigInt.asIntN(4, 25n) === -7n;").AsBoolean());
    }

    [Fact]
    public void BigIntAsUintNWrapsUnsigned()
    {
        Assert.True(Run("BigInt.asUintN(4, -1n) === 15n;").AsBoolean());
    }

    [Fact]
    public void BigIntAsIntNRejectsNumberBigIntArgument()
    {
        var code = @"var ok = false; try { BigInt.asIntN(4, 1); } catch (e) { ok = e instanceof TypeError; } ok;";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void BigIntAsIntNAllowsStringBigIntArgument()
    {
        Assert.True(Run("BigInt.asIntN(8, '257') === 1n;").AsBoolean());
    }
}
