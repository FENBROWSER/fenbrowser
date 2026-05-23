using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringNormalizeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DefaultNfcComposes()
    {
        // "A" + U+0301 combining acute -> "Á" U+00C1 under NFC (1 code unit).
        Assert.Equal(1d, Run("'Á'.normalize().length;").AsNumber());
    }

    [Fact]
    public void NfdDecomposes()
    {
        // "Á" U+00C1 -> "A" + U+0301 under NFD (2 code units).
        Assert.Equal(2d, Run("'Á'.normalize('NFD').length;").AsNumber());
    }

    [Fact]
    public void InvalidFormThrowsRangeError()
    {
        Assert.Throws<JsThrownException>(() => Run("'a'.normalize('NFZ');"));
    }

    [Fact]
    public void AlreadyNormalizedRoundTrip()
    {
        Assert.Equal("hello", Run("'hello'.normalize();").AsString());
    }
}
