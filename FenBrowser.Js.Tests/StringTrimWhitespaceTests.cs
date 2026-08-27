using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringTrimWhitespaceTests
{
    private static bool RunBoolean(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsBoolean();
    }

    [Theory]
    [InlineData("'\\u0085x\\u0085'.trim() === '\\u0085x\\u0085'")]
    [InlineData("'\\u0085x'.trimStart() === '\\u0085x'")]
    [InlineData("'x\\u0085'.trimEnd() === 'x\\u0085'")]
    public void NextLineIsNotEcmaWhitespace(string expression)
    {
        Assert.True(RunBoolean(expression + ";"));
    }

    [Theory]
    [InlineData("'\\uFEFF\\u2028x\\u2029\\uFEFF'.trim() === 'x'")]
    [InlineData("'\\u00A0\\u3000x'.trimStart() === 'x'")]
    [InlineData("'x\\u2007\\u205F'.trimEnd() === 'x'")]
    public void EcmaWhitespaceAndLineTerminatorsAreTrimmed(string expression)
    {
        Assert.True(RunBoolean(expression + ";"));
    }
}
