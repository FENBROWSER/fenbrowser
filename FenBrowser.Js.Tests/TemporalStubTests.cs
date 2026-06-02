using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalStubTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void TemporalObjectAndConstructorsExist()
    {
        var result = Run("typeof Temporal === 'object' && typeof Temporal.Instant === 'function' && typeof Temporal.PlainDate === 'function';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainDateToStringFormatsIsoDate()
    {
        var result = Run("new Temporal.PlainDate(2024, 1, 15).toString();");
        Assert.Equal("2024-01-15", result.AsString());
    }
}
