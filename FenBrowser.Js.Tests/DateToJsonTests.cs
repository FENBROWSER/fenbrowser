using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateToJsonTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ToJsonOfFiniteDateMatchesToIsoString()
    {
        Assert.Equal("2020-01-02T03:04:05.678Z", Run("new Date(1577934245678).toJSON();").AsString());
    }

    [Fact]
    public void ToJsonOfNaNDateReturnsNull()
    {
        Assert.Equal(JsValueTag.Null, Run("new Date(NaN).toJSON();").Tag);
    }
}
