using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegExpEscapeTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void EscapesSyntaxCharacters()
    {
        Assert.Equal("\\(abc\\)", RunStr("RegExp.escape('(abc)');"));
    }

    [Fact]
    public void FirstCharLetterUsesHexEscape()
    {
        // 'a' = 0x61 -> \x61bc
        Assert.Equal("\\x61bc", RunStr("RegExp.escape('abc');"));
    }

    [Fact]
    public void FirstCharDigitUsesHexEscape()
    {
        // '5' = 0x35
        Assert.Equal("\\x35", RunStr("RegExp.escape('5');"));
    }

    [Fact]
    public void NonStringThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("RegExp.escape(5);"));
    }

    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }
}
