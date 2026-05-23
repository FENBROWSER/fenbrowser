using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateDateSettersTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void SetFullYearChangesYearKeepsTime()
    {
        Assert.Equal(2030, RunNum("var d=new Date(1577934245678); d.setFullYear(2030); d.getFullYear();"));
        // Time portion preserved.
        Assert.Equal(3, RunNum("var d=new Date(1577934245678); d.setFullYear(2030); d.getHours();"));
    }

    [Fact]
    public void SetFullYearMonthAndDay()
    {
        Assert.Equal(5,  RunNum("var d=new Date(0); d.setFullYear(2030, 5, 20); d.getMonth();"));
        Assert.Equal(20, RunNum("var d=new Date(0); d.setFullYear(2030, 5, 20); d.getDate();"));
    }

    [Fact]
    public void SetMonthAdvances()
    {
        Assert.Equal(7, RunNum("var d=new Date(1577934245678); d.setMonth(7); d.getMonth();"));
    }

    [Fact]
    public void SetDateOverflowsToNextMonth()
    {
        // Jan 32 -> Feb 1.
        Assert.Equal(1, RunNum("var d=new Date(Date.UTC(2020,0,1)); d.setUTCDate(32); d.getUTCMonth();"));
    }

    [Fact]
    public void SetFullYearOnNaNResurrects()
    {
        Assert.Equal(2030, RunNum("var d=new Date(NaN); d.setFullYear(2030); d.getFullYear();"));
    }

    [Fact]
    public void UtcVariantsBehaveSame()
    {
        Assert.Equal(2030, RunNum("var d=new Date(1577934245678); d.setUTCFullYear(2030); d.getUTCFullYear();"));
    }
}
