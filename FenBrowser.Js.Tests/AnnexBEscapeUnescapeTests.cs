using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Annex B B.2.1.1 escape and B.2.1.2 unescape — legacy URI-ish encoder/decoder.
// Audit gap §4.1 sub-bucket — 16 + 19 test262 failures expected to close.
public class AnnexBEscapeUnescapeTests
{
    private static string Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact] public void Escape_KeepsUnreservedAscii()
        => Assert.Equal("abcABC0189@*_+-./", Run("escape('abcABC0189@*_+-./');"));

    [Fact] public void Escape_PercentEncodesPunctuation()
        => Assert.Equal("%20%3D%23", Run("escape(' =#');"));

    [Fact] public void Escape_LatinExtendedUsesPercentXx()
        => Assert.Equal("%E9", Run("escape('é');"));

    [Fact] public void Escape_AboveLatin1UsesPercentU()
        => Assert.Equal("%u4E2D", Run("escape('中');"));

    [Fact] public void Escape_NonBmpEmitsTwoSurrogateUnits()
    {
        // U+1F600 — surrogate pair D83D DE00.
        Assert.Equal("%uD83D%uDE00", Run("escape('\\uD83D\\uDE00');"));
    }

    [Fact] public void Escape_UndefinedCoercion()
        => Assert.Equal("undefined", Run("escape();"));

    [Fact] public void Unescape_DecodesPercentXx()
        => Assert.Equal(" =#", Run("unescape('%20%3D%23');"));

    [Fact] public void Unescape_DecodesPercentU()
        => Assert.Equal("é", Run("unescape('%u00E9');"));

    [Fact] public void Unescape_PassesThroughInvalidEscapes()
        => Assert.Equal("%g1", Run("unescape('%g1');"));

    [Fact] public void Unescape_PassesThroughTrailingPercent()
        => Assert.Equal("ab%", Run("unescape('ab%');"));

    [Fact] public void EscapeUnescapeRoundTrip_ForBasicLatin()
        => Assert.Equal("Hello, world!", Run("unescape(escape('Hello, world!'));"));

    [Fact] public void EscapeFunctionLengthIsOne()
        => Assert.Equal("1", Run("String(escape.length);"));

    [Fact] public void UnescapeFunctionLengthIsOne()
        => Assert.Equal("1", Run("String(unescape.length);"));
}
