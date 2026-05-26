using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegExpLiteralTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void RegexLiteralIsRegExpObject()
    {
        var result = Run("var r = /abc/; r instanceof RegExp;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodReturnsBoolean()
    {
        var result = Run("/abc/.test('abc');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodMatches()
    {
        var result = Run("/abc/.test('hello abc world');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralTestMethodNonMatch()
    {
        var result = Run("/abc/.test('hello world');");
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralWithFlagsReadable()
    {
        var result = Run("var r = /abc/gi; r.global && r.ignoreCase;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralIgnoringCase()
    {
        var result = Run("/abc/i.test('ABC');");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralGlobalFlag()
    {
        var result = Run("var r = /abc/g; r.global;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralMultilineFlag()
    {
        var result = Run("var r = /abc/m; r.multiline;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralDotAllFlag()
    {
        var result = Run("var r = /a.b/s; r.dotAll;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexLiteralToString()
    {
        var result = Run("/abc/gi.toString();");
        Assert.Equal("/abc/gi", result.AsString());
    }

    [Fact]
    public void RegexLiteralSource()
    {
        var result = Run("/abc/gi.source;");
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void RegexLiteralLastIndex()
    {
        var result = Run("/abc/g.lastIndex;");
        Assert.Equal(0.0, result.AsNumber(), 4);
    }
}
