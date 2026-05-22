using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringFromCharCodeAndCodePointTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("String.fromCharCode(65);", "A")]
    [InlineData("String.fromCharCode(72,105);", "Hi")]
    [InlineData("String.fromCharCode();", "")]
    public void FromCharCode(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void FromCharCodeTruncatesAbove16Bits()
    {
        // 0x1F600 truncates to its low 16 bits (0xF600), yielding a single UTF-16
        // code unit. We measure the result length from C# rather than via a JS
        // .length lookup because primitive-string method dispatch is not wired yet.
        Assert.Equal(1, RunStr("String.fromCharCode(0x1F600);").Length);
    }

    [Theory]
    [InlineData("String.fromCodePoint(65);", "A")]
    [InlineData("String.fromCodePoint(0x1F600);", "😀")]   // grinning face emoji (surrogate pair)
    [InlineData("String.fromCodePoint(72, 105);", "Hi")]
    public void FromCodePoint(string source, string expected) => Assert.Equal(expected, RunStr(source));

    [Fact]
    public void FromCodePointOutOfRangeThrows()
    {
        Assert.Throws<JsThrownException>(() => RunStr("String.fromCodePoint(-1);"));
        Assert.Throws<JsThrownException>(() => RunStr("String.fromCodePoint(0x110000);"));
        Assert.Throws<JsThrownException>(() => RunStr("String.fromCodePoint(NaN);"));
        Assert.Throws<JsThrownException>(() => RunStr("String.fromCodePoint(1.5);"));
    }
}
