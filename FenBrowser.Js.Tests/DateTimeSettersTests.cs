using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateTimeSettersTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact] public void SetHours()   => Assert.Equal(10, RunNum("var d=new Date(1577934245678); d.setHours(10); d.getHours();"));
    [Fact] public void SetMinutes() => Assert.Equal(45, RunNum("var d=new Date(1577934245678); d.setMinutes(45); d.getMinutes();"));
    [Fact] public void SetSeconds() => Assert.Equal(30, RunNum("var d=new Date(1577934245678); d.setSeconds(30); d.getSeconds();"));
    [Fact] public void SetMillis()  => Assert.Equal(999,RunNum("var d=new Date(1577934245678); d.setMilliseconds(999); d.getMilliseconds();"));

    [Fact]
    public void SetHoursWithAllPortions()
    {
        Assert.Equal(10, RunNum("var d=new Date(0); d.setHours(10,20,30,400); d.getHours();"));
        Assert.Equal(20, RunNum("var d=new Date(0); d.setHours(10,20,30,400); d.getMinutes();"));
        Assert.Equal(30, RunNum("var d=new Date(0); d.setHours(10,20,30,400); d.getSeconds();"));
        Assert.Equal(400, RunNum("var d=new Date(0); d.setHours(10,20,30,400); d.getMilliseconds();"));
    }

    [Fact]
    public void SetMinutesOverflowsToHour()
    {
        // 0:70:00 -> 1:10:00
        Assert.Equal(1,  RunNum("var d=new Date(0); d.setMinutes(70); d.getHours();"));
        Assert.Equal(10, RunNum("var d=new Date(0); d.setMinutes(70); d.getMinutes();"));
    }

    [Fact]
    public void SetOnNaNStaysNaN()
    {
        Assert.True(double.IsNaN(RunNum("var d=new Date(NaN); d.setHours(5);")));
    }

    [Fact]
    public void UtcVariantBehavesSame()
    {
        Assert.Equal(15, RunNum("var d=new Date(0); d.setUTCHours(15); d.getUTCHours();"));
    }
}
