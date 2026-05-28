using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Annex B B.2.3 — legacy Date.prototype aliases (audit gap §4.1).
public class AnnexBDateLegacyTests
{
    private static double RunNum(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void GetYear_ReturnsYearMinus1900()
    {
        // Use UTC ms epoch for 2026-01-01 = (2026-1970) * 365.25 days approx;
        // simpler: build via setFullYear which is reliable.
        Assert.Equal(126d, RunNum("var d = new Date(0); d.setFullYear(2026); d.getYear();"));
    }

    [Fact]
    public void GetYear_NaNForInvalidDate()
        => Assert.True(double.IsNaN(RunNum("new Date(NaN).getYear();")));

    [Fact]
    public void SetYear_TwoDigitMapsTo1900Plus()
    {
        Assert.Equal(75d, RunNum("var d = new Date(0); d.setYear(75); d.getYear();"));
        Assert.Equal(1975d, RunNum("var d = new Date(0); d.setYear(75); d.getFullYear();"));
    }

    [Fact]
    public void SetYear_FourDigitUsedDirectly()
        => Assert.Equal(2050d, RunNum("var d = new Date(0); d.setYear(2050); d.getFullYear();"));

    [Fact]
    public void ToGMTString_AliasOfToUTCString()
    {
        // Equal output text for both methods on same instance.
        Assert.Equal("equal", RunStr("var d = new Date(0); (d.toGMTString() === d.toUTCString()) ? 'equal' : 'diff';"));
    }
}
