using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class UriGlobalsTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static string RunStr(string source) => Run(source).AsString();

    [Fact]
    public void EncodeUriKeepsReserved()
    {
        Assert.Equal("http://a.b/c?x=1&y=2#h", RunStr("encodeURI('http://a.b/c?x=1&y=2#h');"));
    }

    [Fact]
    public void EncodeUriPercentEncodesSpace()
    {
        Assert.Equal("a%20b", RunStr("encodeURI('a b');"));
    }

    [Fact]
    public void EncodeUriComponentEncodesReserved()
    {
        Assert.Equal("a%3Fb%26c%3Dd", RunStr("encodeURIComponent('a?b&c=d');"));
    }

    [Fact]
    public void EncodeUriEncodesNonAscii()
    {
        // 'ñ' (U+00F1) is C3 B1 in UTF-8.
        Assert.Equal("%C3%B1", RunStr("encodeURIComponent('ñ');"));
    }

    [Fact]
    public void EncodeUriEncodesAstralPlane()
    {
        // '𝄞' (U+1D11E musical G-clef) is F0 9D 84 9E in UTF-8.
        Assert.Equal("%F0%9D%84%9E", RunStr("encodeURIComponent('𝄞');"));
    }

    [Fact]
    public void DecodeUriRoundTrips()
    {
        Assert.Equal("a bñ", RunStr("decodeURIComponent(encodeURIComponent('a bñ'));"));
    }

    [Fact]
    public void DecodeUriPreservesReservedTriplets()
    {
        // decodeURI leaves reserved-character escapes literal so already-built URIs survive.
        Assert.Equal("%3F", RunStr("decodeURI('%3F');"));
        Assert.Equal("?", RunStr("decodeURIComponent('%3F');"));
    }

    [Fact]
    public void EncodeUriThrowsOnLoneSurrogate()
    {
        Assert.Throws<JsThrownException>(() => Run("encodeURI('\ud834');"));
    }

    [Fact]
    public void DecodeUriThrowsOnTruncatedEscape()
    {
        Assert.Throws<JsThrownException>(() => Run("decodeURIComponent('%2');"));
    }

    [Fact]
    public void DecodeUriThrowsOnBadUtf8()
    {
        Assert.Throws<JsThrownException>(() => Run("decodeURIComponent('%C3%28');"));
    }

    [Fact]
    public void UriErrorConstructorMakesInstance()
    {
        Assert.Equal("URIError", RunStr("new URIError('bad').name;"));
        Assert.Equal("bad", RunStr("new URIError('bad').message;"));
    }
}
