using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateToIsoStringTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void Epoch()
    {
        Assert.Equal("1970-01-01T00:00:00.000Z", Run("new Date(0).toISOString();").AsString());
    }

    [Fact]
    public void KnownTimestamp()
    {
        // 2020-01-02T03:04:05.678Z = 1577934245678 ms.
        Assert.Equal("2020-01-02T03:04:05.678Z", Run("new Date(1577934245678).toISOString();").AsString());
    }

    [Fact]
    public void NaNTimeThrowsRangeError()
    {
        Assert.Throws<JsThrownException>(() => Run("new Date(NaN).toISOString();"));
    }
}
