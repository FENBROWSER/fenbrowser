using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateToStringTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Base = "var d = new Date(1577934245678);";

    [Fact]
    public void ToString_FullFormat()
    {
        Assert.Equal("Thu Jan 02 2020 03:04:05 GMT+0000 (Coordinated Universal Time)", RunStr(Base + " d.toString();"));
    }

    [Fact]
    public void ToDateString_DateOnly()
    {
        Assert.Equal("Thu Jan 02 2020", RunStr(Base + " d.toDateString();"));
    }

    [Fact]
    public void ToTimeString_TimeOnly()
    {
        Assert.Equal("03:04:05 GMT+0000 (Coordinated Universal Time)", RunStr(Base + " d.toTimeString();"));
    }

    [Fact]
    public void ToUtcString_RfcLike()
    {
        Assert.Equal("Thu, 02 Jan 2020 03:04:05 GMT", RunStr(Base + " d.toUTCString();"));
    }

    [Fact]
    public void InvalidDateAllVariants()
    {
        Assert.Equal("Invalid Date", RunStr("new Date(NaN).toString();"));
        Assert.Equal("Invalid Date", RunStr("new Date(NaN).toDateString();"));
        Assert.Equal("Invalid Date", RunStr("new Date(NaN).toTimeString();"));
        Assert.Equal("Invalid Date", RunStr("new Date(NaN).toUTCString();"));
    }
}
