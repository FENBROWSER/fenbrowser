using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class IntlSmokeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static string RunStr(string source) => Run(source).AsString();
    private static bool RunBool(string source) => Run(source).AsBoolean();

    [Fact]
    public void IntlIsObject()
    {
        Assert.Equal("object", RunStr("typeof Intl;"));
    }

    [Fact]
    public void IntlDateTimeFormatIsFunction()
    {
        Assert.Equal("function", RunStr("typeof Intl.DateTimeFormat;"));
    }

    [Fact]
    public void IntlNumberFormatIsFunction()
    {
        Assert.Equal("function", RunStr("typeof Intl.NumberFormat;"));
    }

    [Fact]
    public void IntlCollatorIsFunction()
    {
        Assert.Equal("function", RunStr("typeof Intl.Collator;"));
    }

    [Fact]
    public void IntlGetCanonicalLocalesIsFunction()
    {
        Assert.Equal("function", RunStr("typeof Intl.getCanonicalLocales;"));
    }

    [Fact]
    public void DateTimeFormatThrowsWhenCalledWithoutNew()
    {
        Assert.Throws<JsThrownException>(() => Run("Intl.DateTimeFormat();"));
    }

    [Fact]
    public void NumberFormatThrowsWhenCalledWithoutNew()
    {
        Assert.Throws<JsThrownException>(() => Run("Intl.NumberFormat();"));
    }

    [Fact]
    public void CollatorThrowsWhenCalledWithoutNew()
    {
        Assert.Throws<JsThrownException>(() => Run("Intl.Collator();"));
    }

    [Fact]
    public void DateTimeFormatNewReturnsObject()
    {
        Assert.Equal("object", RunStr("typeof new Intl.DateTimeFormat();"));
    }

    [Fact]
    public void NumberFormatNewReturnsObject()
    {
        Assert.Equal("object", RunStr("typeof new Intl.NumberFormat();"));
    }

    [Fact]
    public void CollatorNewReturnsObject()
    {
        Assert.Equal("object", RunStr("typeof new Intl.Collator();"));
    }

    [Fact]
    public void GetCanonicalLocalesReturnsArrayForStringArg()
    {
        Assert.True(RunBool("Array.isArray(Intl.getCanonicalLocales('en-US'));"));
    }

    [Fact]
    public void GetCanonicalLocalesReturnsArrayForArrayArg()
    {
        Assert.True(RunBool("Array.isArray(Intl.getCanonicalLocales(['en-US', 'fr-FR']));"));
    }

    [Fact]
    public void GetCanonicalLocalesReturnsEmptyArrayForNoArgs()
    {
        Assert.True(RunBool("Array.isArray(Intl.getCanonicalLocales());"));
    }
}
