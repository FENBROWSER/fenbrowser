using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringPrototypeDispatchTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

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

    [Fact] public void LengthOnStringPrimitive() => Assert.Equal(5, RunNum("'hello'.length;"));
    [Fact] public void LengthOnEmptyString() => Assert.Equal(0, RunNum("''.length;"));
    [Fact] public void IndexerOnStringPrimitive() => Assert.Equal("e", RunStr("'hello'[1];"));
    [Fact] public void OutOfRangeIndexerIsUndefined() => Assert.True(RunBool("'abc'[99] === undefined;"));

    [Theory]
    [InlineData("'abc'.charAt(0);", "a")]
    [InlineData("'abc'.charAt(2);", "c")]
    [InlineData("'abc'.charAt(99);", "")]
    [InlineData("'abc'.charAt(-1);", "")]
    public void CharAt(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Theory]
    [InlineData("'abc'.charCodeAt(0);", 97)]
    [InlineData("'A'.charCodeAt(0);", 65)]
    public void CharCodeAt(string source, double expected) => Assert.Equal(expected, RunNum(source));

    [Fact] public void CharCodeAtOutOfRangeIsNaN() => Assert.True(double.IsNaN(RunNum("'abc'.charCodeAt(99);")));

    [Theory]
    [InlineData("'hello world'.indexOf('world');", 6)]
    [InlineData("'hello'.indexOf('z');", -1)]
    [InlineData("'aaa'.indexOf('a', 1);", 1)]
    public void IndexOf(string source, double expected) => Assert.Equal(expected, RunNum(source));

    [Theory]
    [InlineData("'aaa'.lastIndexOf('a');", 2)]
    [InlineData("'hello'.lastIndexOf('z');", -1)]
    public void LastIndexOf(string source, double expected) => Assert.Equal(expected, RunNum(source));

    [Fact] public void Includes() => Assert.True(RunBool("'hello world'.includes('world');"));
    [Fact] public void IncludesNot() => Assert.False(RunBool("'hello'.includes('z');"));
    [Fact] public void StartsWith() => Assert.True(RunBool("'hello'.startsWith('hel');"));
    [Fact] public void EndsWith() => Assert.True(RunBool("'hello'.endsWith('llo');"));

    [Theory]
    [InlineData("'hello'.slice(0, 3);", "hel")]
    [InlineData("'hello'.slice(-3);", "llo")]
    [InlineData("'hello'.slice(1);", "ello")]
    [InlineData("'hello'.slice(0);", "hello")]
    public void Slice(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Theory]
    [InlineData("'hello'.substring(1, 4);", "ell")]
    [InlineData("'hello'.substring(4, 1);", "ell")]
    [InlineData("'hello'.substring(-1);", "hello")]
    public void Substring(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Theory]
    [InlineData("'hello'.substr(1, 3);", "ell")]
    [InlineData("'hello'.substr(-2);", "lo")]
    public void Substr(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact] public void Concat() => Assert.Equal("abc", RunStr("'a'.concat('b', 'c');"));
    [Fact] public void Repeat() => Assert.Equal("ababab", RunStr("'ab'.repeat(3);"));
    [Fact] public void RepeatZero() => Assert.Equal("", RunStr("'ab'.repeat(0);"));
    [Fact] public void RepeatNegativeThrows() => Assert.Throws<JsThrownException>(() => RunStr("'ab'.repeat(-1);"));

    [Fact] public void PadStart() => Assert.Equal("  5", RunStr("'5'.padStart(3);"));
    [Fact] public void PadStartCustomFill() => Assert.Equal("005", RunStr("'5'.padStart(3, '0');"));
    [Fact] public void PadEnd() => Assert.Equal("5  ", RunStr("'5'.padEnd(3);"));

    [Fact] public void Trim() => Assert.Equal("abc", RunStr("'  abc  '.trim();"));
    [Fact] public void TrimStart() => Assert.Equal("abc  ", RunStr("'  abc  '.trimStart();"));
    [Fact] public void TrimEnd() => Assert.Equal("  abc", RunStr("'  abc  '.trimEnd();"));

    [Fact] public void ToUpperCase() => Assert.Equal("ABC", RunStr("'abc'.toUpperCase();"));
    [Fact] public void ToLowerCase() => Assert.Equal("abc", RunStr("'ABC'.toLowerCase();"));

    [Fact] public void SplitByString()
    {
        Assert.Equal(3, RunNum("'a,b,c'.split(',').length;"));
        Assert.Equal("b", RunStr("'a,b,c'.split(',')[1];"));
    }

    [Fact] public void SplitEmptyStringSeparator()
    {
        Assert.Equal(3, RunNum("'abc'.split('').length;"));
        Assert.Equal("b", RunStr("'abc'.split('')[1];"));
    }

    [Fact] public void SplitNoSeparatorReturnsSingleElement()
    {
        Assert.Equal(1, RunNum("'abc'.split().length;"));
    }

    [Fact] public void Replace() => Assert.Equal("hexxo", RunStr("'hello'.replace('ll', 'xx');"));
    [Fact] public void ReplaceFunction() => Assert.Equal("HELLO", RunStr("'hello'.replace('hello', function(m){return m.toUpperCase();});"));
    [Fact] public void ReplaceAll() => Assert.Equal("xxx", RunStr("'aaa'.replaceAll('a', 'x');"));
    [Fact] public void ReplaceMissingPattern() => Assert.Equal("hello", RunStr("'hello'.replace('z', 'x');"));

    [Fact] public void NumberMethodsViaPrimitive()
    {
        // Number.prototype dispatch also benefits from the new GetReceiverProperty.
        Assert.Equal("2", RunStr("(2).toString();"));
    }

    [Fact] public void ChainedStringOperations()
    {
        Assert.Equal("HELLOWORLD", RunStr("'  hello world  '.trim().toUpperCase().replaceAll(' ', '');"));
    }

    [Fact] public void PropertyAccessOnUndefinedThrows()
    {
        Assert.Throws<JsThrownException>(() => RunStr("var x; x.foo;"));
    }

    [Fact] public void PropertyAccessOnNullThrows()
    {
        Assert.Throws<JsThrownException>(() => RunStr("null.foo;"));
    }
}
