using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NumberToExpToPrecisionTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("new Number(100).toExponential(2);", "1.00e+2")]
    [InlineData("new Number(0).toExponential(3);", "0.000e+0")]
    [InlineData("new Number(NaN).toExponential();", "NaN")]
    [InlineData("new Number(Infinity).toExponential();", "Infinity")]
    [InlineData("new Number(-Infinity).toExponential(2);", "-Infinity")]
    public void ToExponential(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void ToExponentialRejectsOutOfRangeDigits()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toExponential(-1);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toExponential(101);"));
    }

    [Theory]
    [InlineData("new Number(3.14159).toPrecision(3);", "3.14")]
    [InlineData("new Number(0).toPrecision(1);", "0")]
    [InlineData("new Number(0).toPrecision(3);", "0.00")]
    [InlineData("new Number(NaN).toPrecision(3);", "NaN")]
    [InlineData("new Number(Infinity).toPrecision(3);", "Infinity")]
    public void ToPrecision(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void ToPrecisionWithoutArgsBehavesLikeToString()
    {
        Assert.Equal("3.14", RunStr("new Number(3.14).toPrecision();"));
    }

    [Fact]
    public void ToPrecisionRejectsOutOfRangePrecision()
    {
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toPrecision(0);"));
        Assert.Throws<JsThrownException>(() => RunStr("new Number(1).toPrecision(101);"));
    }

    [Fact]
    public void NumberConstructorUsesFunctionPrototypeAndNonEnumerableConstructorProperty()
    {
        Assert.True(RunBool("""
            Function.prototype.isPrototypeOf(Number) &&
            Object.getPrototypeOf(Number) === Function.prototype &&
            Object.getOwnPropertyDescriptor(Number.prototype, 'constructor').enumerable === false;
            """));
    }

    [Fact]
    public void NumberPrototypeHasOwnToLocaleString()
    {
        Assert.True(RunBool("""
            Number.prototype.hasOwnProperty('toLocaleString') &&
            Object.getOwnPropertyDescriptor(Number.prototype, 'toLocaleString').enumerable === false &&
            (123).toLocaleString() === '123';
            """));
    }

    [Fact]
    public void NumberConstructorUsesRealmPrototypeWhenNewTargetPrototypeIsNull()
    {
        Assert.True(RunBool("""
            var fakeRealmProto = { marker: 1 };
            var realmGlobal = { Number: { prototype: fakeRealmProto } };
            function C() {}
            C.prototype = null;
            C.__realmGlobal__ = realmGlobal;
            var o = Reflect.construct(Number, [], C);
            Object.getPrototypeOf(o) === fakeRealmProto;
            """));
    }

    [Fact]
    public void NumberTrimRecognizesEcmaWhitespace()
    {
        Assert.True(RunBool("""
            Number("\u0009\u000C\u0020\u00A0\u000B\u000A\u000D\u2028\u2029\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u202F\u205F\u3000") === 0 &&
            Number("\u0009\u000C\u0020\u00A0\u000B\u000A\u000D\u2028\u2029\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u202F\u205F\u3000Infinity\u0009\u000C\u0020\u00A0\u000B\u000A\u000D\u2028\u2029\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u202F\u205F\u3000") === Infinity;
            """));
    }
}
