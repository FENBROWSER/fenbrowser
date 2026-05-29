using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 §21.4.1 time math on doubles. Regression guard for the 10 Date crashes
// (built-ins/Date) caused by backing Date with .NET DateTimeOffset, which only spans
// years 1..9999 and threw for valid ECMAScript dates beyond that.
public sealed class DateMathRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void DateUtcRoundTripsYearMonthDate()
    {
        Assert.Equal(2000, RunNum("new Date(Date.UTC(2000,0,1)).getUTCFullYear();"));
        Assert.Equal(5, RunNum("new Date(Date.UTC(2021,5,15)).getUTCMonth();"));
        Assert.Equal(15, RunNum("new Date(Date.UTC(2021,5,15)).getUTCDate();"));
    }

    [Fact]
    public void FarFutureDateDoesNotCrash()
    {
        // 8.64e15 ms is the maximum ECMAScript time value (~year 275760); beyond the
        // .NET DateTimeOffset range that previously crashed.
        Assert.Equal(275760, RunNum("new Date(8.64e15).getUTCFullYear();"));
    }

    [Fact]
    public void SetFullYearBeyondRangeClipsToNaN()
    {
        Assert.True(double.IsNaN(RunNum("var d=new Date(0); d.setFullYear(285616); d.getTime();")));
    }

    [Fact]
    public void GetTimezoneOffsetIsZero()
    {
        Assert.Equal(0, RunNum("new Date(0).getTimezoneOffset();"));
    }

    [Fact]
    public void SetMonthOverflowRipplesPerMakeDay()
    {
        // setMonth(13) on Jan 2000 -> Feb 2001 (month overflow wraps the year).
        Assert.Equal(2001, RunNum("var d=new Date(Date.UTC(2000,0,15)); d.setUTCMonth(13); d.getUTCFullYear();"));
        Assert.Equal(1, RunNum("var d=new Date(Date.UTC(2000,0,15)); d.setUTCMonth(13); d.getUTCMonth();"));
    }

    [Fact]
    public void EpochComponentsAreCorrect()
    {
        Assert.Equal(1970, RunNum("new Date(0).getUTCFullYear();"));
        Assert.Equal(0, RunNum("new Date(0).getUTCMonth();"));
        Assert.Equal(1, RunNum("new Date(0).getUTCDate();"));
        Assert.Equal(4, RunNum("new Date(0).getUTCDay();")); // 1970-01-01 was a Thursday
    }
}
