using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class IntlImplementationTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DateTimeFormat_Format_ReturnsString()
    {
        var result = Run("typeof new Intl.DateTimeFormat('en-US').format(new Date())");
        Assert.Equal("string", result.AsString());
    }

    [Fact]
    public void DateTimeFormat_Format_InvalidDate()
    {
        var result = Run("new Intl.DateTimeFormat('en-US').format(undefined)");
        Assert.Equal("Invalid Date", result.AsString());
    }

    [Fact]
    public void NumberFormat_Format_ReturnsString()
    {
        var result = Run("typeof new Intl.NumberFormat('en-US').format(12345.67)");
        Assert.Equal("string", result.AsString());
    }

    [Fact]
    public void NumberFormat_Format_NaN()
    {
        var result = Run("new Intl.NumberFormat('en-US').format(NaN)");
        Assert.Equal("NaN", result.AsString());
    }

    [Fact]
    public void Collator_Compare_ReturnsNumber()
    {
        var result = Run("typeof new Intl.Collator('en-US').compare('a', 'b')");
        Assert.Equal("number", result.AsString());
    }

    [Fact]
    public void Collator_Compare_Ordering()
    {
        var result = Run("new Intl.Collator('en-US').compare('a', 'b')");
        Assert.True(result.AsNumber() < 0);
    }

    [Fact]
    public void Collator_Compare_Equal()
    {
        var result = Run("new Intl.Collator('en-US').compare('hello', 'hello')");
        Assert.Equal(0d, result.AsNumber());
    }

    [Fact]
    public void GetCanonicalLocales_ReturnsArray()
    {
        Assert.True(Run("Array.isArray(Intl.getCanonicalLocales('en-US'))").AsBoolean());
    }

    [Fact]
    public void GetCanonicalLocales_SingleLocale()
    {
        var result = Run("Intl.getCanonicalLocales('en-US')[0]");
        Assert.Equal("en-US", result.AsString());
    }

    [Fact]
    public void DateTimeFormat_MustBeConstructed()
    {
        Assert.Throws<JsThrownException>(() => Run("Intl.DateTimeFormat()"));
    }

    [Fact]
    public void NumberFormat_MustBeConstructed()
    {
        Assert.Throws<JsThrownException>(() => Run("Intl.NumberFormat()"));
    }
}
