using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegExpFlagTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DotAllFlagReadable()
    {
        var result = Run("var r = new RegExp('a', 's'); r.dotAll;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void DotAllFlagDefaultFalse()
    {
        var result = Run("var r = new RegExp('a', ''); r.dotAll;");
        Assert.False(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void UnicodeFlagReadable()
    {
        var result = Run("var r = new RegExp('a', 'u'); r.unicode;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void UnicodeFlagDefaultFalse()
    {
        var result = Run("var r = new RegExp('a', ''); r.unicode;");
        Assert.False(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void StickyFlagReadable()
    {
        var result = Run("var r = new RegExp('a', 'y'); r.sticky;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void StickyFlagDefaultFalse()
    {
        var result = Run("var r = new RegExp('a', ''); r.sticky;");
        Assert.False(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void HasIndicesFlagReadable()
    {
        var result = Run("var r = new RegExp('a', 'd'); r.hasIndices;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void HasIndicesFlagDefaultFalse()
    {
        var result = Run("var r = new RegExp('a', ''); r.hasIndices;");
        Assert.False(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void FlagsPropertyIsReadable()
    {
        var result = Run("var r = new RegExp('a', 'gimsuy'); r.flags;");
        Assert.Equal("gimsuy", result.AsString());
    }

    [Fact]
    public void FlagsPropertyIsNormalized()
    {
        var result = Run("var r = new RegExp('a', 'ygim'); r.flags;");
        Assert.Equal("gimy", result.AsString());
    }

    [Fact]
    public void DuplicateFlagsThrow()
    {
        Assert.Throws<JsThrownException>(() => Run("new RegExp('a', 'gg');"));
    }

    [Fact]
    public void UnknownFlagThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("new RegExp('a', 'x');"));
    }

    [Fact]
    public void MultipleNewFlagsCombined()
    {
        var result = Run("var r = new RegExp('a', 'suy'); r.dotAll && r.unicode && r.sticky;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void UnicodeSetsFlagReadable()
    {
        var result = Run("var r = new RegExp('a', 'v'); r.unicodeSets;");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void DotAllMatchesNewlines()
    {
        var result = Run("var r = new RegExp('a.b', 's'); r.test('a\\nb');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void UnicodeMatchesUnicodeClass()
    {
        var result = Run("var r = new RegExp('^\\\\d+$', 'u'); r.test('123');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void WhitespaceEscapeMatchesExtendedWhitespace()
    {
        var result = Run("var r = /\\s/; r.test('\\u00A0') && r.test('\\u2028') && r.test('\\uFEFF');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void NonWhitespaceEscapeRejectsExtendedWhitespace()
    {
        var result = Run("var r = /\\S/; !r.test('\\u00A0') && !r.test('\\u2028') && !r.test('\\uFEFF');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void DigitEscapeWithUnicodeFlagIsAsciiOnly()
    {
        var result = Run("var r = /\\d/u; !r.test('\\u0660') && r.test('0');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }

    [Fact]
    public void WordEscapeWithUnicodeFlagIsAsciiOnly()
    {
        var result = Run("var r = /\\w/u; !r.test('\\u00E9') && r.test('_');");
        Assert.True(result.Tag == JsValueTag.Boolean && result.AsBoolean());
    }
}
