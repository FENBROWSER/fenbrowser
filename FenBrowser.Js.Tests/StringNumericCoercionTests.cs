using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringNumericCoercionTests
{
    [Theory]
    [InlineData("'abc'.charAt(NaN) === 'a'")]
    [InlineData("'abc'.charCodeAt(NaN) === 97")]
    [InlineData("'abc'.codePointAt(NaN) === 97")]
    [InlineData("'abc'.at(NaN) === 'a'")]
    [InlineData("'abc'.charAt(1.9) === 'b'")]
    [InlineData("'abc'.charAt(-0.9) === 'a'")]
    [InlineData("'abc'.at(-1.9) === 'c'")]
    [InlineData("'abc'.charAt(Infinity) === ''")]
    [InlineData("Number.isNaN('abc'.charCodeAt(-Infinity))")]
    [InlineData("'abc'.codePointAt(Infinity) === undefined")]
    [InlineData("'abc'.at(-Infinity) === undefined")]
    public void StringIndexMethodsApplyToIntegerOrInfinity(string expression)
    {
        Assert.True(RunBoolean(expression + ";"));
    }

    [Theory]
    [InlineData("'a,b'.split(',', -1).length === 2")]
    [InlineData("'a,b'.split(',', -1)[1] === 'b'")]
    [InlineData("'a,b'.split(',', NaN).length === 0")]
    [InlineData("'a,b'.split(',', Infinity).length === 0")]
    [InlineData("'a,b'.split(',', 4294967297).length === 1")]
    [InlineData("'a,b'.split(',', -4294967295).length === 1")]
    public void SplitLimitAppliesToUint32(string expression)
    {
        Assert.True(RunBoolean(expression + ";"));
    }

    private static bool RunBoolean(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsBoolean();
    }
}
