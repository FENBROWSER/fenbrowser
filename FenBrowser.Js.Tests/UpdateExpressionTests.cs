using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 13.4 Update Expressions. Postfix yields the old (ToNumeric-coerced)
// value; prefix yields the new value; both coerce with ToNumeric so non-number
// operands are not string-concatenated, and BigInt steps by 1n.
public sealed class UpdateExpressionTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static double Num(string s) => Run(s).AsNumber();
    private static string Str(string s) => Run(s).AsString();

    [Fact]
    public void PostfixIncrementYieldsOldValue()
        => Assert.Equal(0, Num("var i = 0; var a = i++; a;"));

    [Fact]
    public void PostfixIncrementStoresNewValue()
        => Assert.Equal(1, Num("var i = 0; i++; i;"));

    [Fact]
    public void PrefixIncrementYieldsNewValue()
        => Assert.Equal(1, Num("var i = 0; var a = ++i; a;"));

    [Fact]
    public void PostfixDecrementYieldsOldValue()
        => Assert.Equal(5, Num("var i = 5; var a = i--; a;"));

    [Fact]
    public void PrefixDecrementYieldsNewValueAndStores()
        => Assert.Equal("4,4", Str("var i = 5; var a = --i; a + ',' + i;"));

    [Fact]
    public void PostfixCoercesStringToNumberNotConcatenation()
    {
        // "5"++ must coerce to the number 5, store 6, and yield 5 — not "51".
        Assert.Equal("5,6", Str("var x = '5'; var a = x++; a + ',' + x;"));
    }

    [Fact]
    public void IncrementOnMemberYieldsOldValueAndStores()
        => Assert.Equal("7,8", Str("var o = { n: 7 }; var a = o.n++; a + ',' + o.n;"));

    [Fact]
    public void PrefixIncrementOnComputedMember()
        => Assert.Equal("3,3", Str("var a = [2]; var r = ++a[0]; r + ',' + a[0];"));

    [Fact]
    public void IncrementMemberEvaluatesObjectExpressionOnce()
    {
        // The reference (object + key) is evaluated once; calls++ must run a
        // single time even though the property is both read and written.
        Assert.Equal("1", Str(@"
            var calls = 0;
            var obj = { v: 10 };
            function get() { calls = calls + 1; return obj; }
            get().v++;
            '' + calls;
        "));
    }

    [Fact]
    public void BigIntPostfixIncrementStepsByOne()
        => Assert.Equal("10,11", Str("var b = 10n; var a = b++; a + ',' + b;"));

    [Fact]
    public void LoopCounterIncrementsCorrectly()
        => Assert.Equal(10, Num("var s = 0; for (var i = 0; i < 10; i++) s = s + 1; s;"));

    [Fact]
    public void SymbolIncrementThrowsTypeError()
        => Assert.Throws<JsThrownException>(() => Run("var s = Symbol(); s++;"));
}
