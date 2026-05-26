using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class IntlDateTimeFormatTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static string RunStr(string source) => Run(source).AsString();

    [Fact]
    public void DateTimeFormatFormatReturnsString()
    {
        Assert.Equal("string", RunStr("typeof new Intl.DateTimeFormat().format(new Date());"));
    }

    [Fact]
    public void DateTimeFormatFormatWithUndefinedReturnsInvalidDate()
    {
        Assert.Equal("Invalid Date", RunStr("new Intl.DateTimeFormat().format(undefined);"));
    }

    [Fact]
    public void DateTimeFormatFormatWithZeroArgsReturnsInvalidDate()
    {
        Assert.Equal("Invalid Date", RunStr("new Intl.DateTimeFormat().format();"));
    }

    [Fact]
    public void DateTimeFormatFormatWithTimestamp()
    {
        var result = RunStr("new Intl.DateTimeFormat('en-US').format(0);");
        Assert.NotEqual("Invalid Date", result);
        Assert.Contains("1970", result);
    }

    [Fact]
    public void DateTimeFormatFormatWithDateObject()
    {
        var result = RunStr("new Intl.DateTimeFormat('en-US').format(new Date(Date.UTC(2020, 0, 1)));");
        Assert.NotEqual("Invalid Date", result);
        Assert.Contains("2020", result);
    }

    [Fact]
    public void DateTimeFormatFormatWithNumberString()
    {
        var result = RunStr("new Intl.DateTimeFormat('en-US').format('0');");
        Assert.NotEqual("Invalid Date", result);
    }

    [Fact]
    public void DateTimeFormatWithEnGbLocale()
    {
        var result = RunStr("new Intl.DateTimeFormat('en-GB').format(0);");
        Assert.NotEqual("Invalid Date", result);
    }

    [Fact]
    public void DateTimeFormatWithUnknownLocaleFallsBack()
    {
        var result = RunStr("new Intl.DateTimeFormat('zz-ZZ').format(0);");
        Assert.NotEqual("Invalid Date", result);
    }

    [Fact]
    public void DateTimeFormatFormatIsFunction()
    {
        Assert.Equal("function", RunStr("typeof new Intl.DateTimeFormat().format;"));
    }
}
