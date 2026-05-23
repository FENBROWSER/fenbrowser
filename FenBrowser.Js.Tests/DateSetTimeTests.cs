using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateSetTimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void SetTimeMutatesReceiverAndReturnsValue()
    {
        Assert.Equal(123d, RunNum("var d=new Date(0); d.setTime(123);"));
        Assert.Equal(123d, RunNum("var d=new Date(0); d.setTime(123); d.getTime();"));
    }

    [Fact]
    public void SetTimeTruncatesTowardZero()
    {
        Assert.Equal(5d, RunNum("var d=new Date(0); d.setTime(5.9);"));
        Assert.Equal(-5d, RunNum("var d=new Date(0); d.setTime(-5.9);"));
    }

    [Fact]
    public void SetTimeOutOfRangeBecomesNaN()
    {
        Assert.True(double.IsNaN(RunNum("var d=new Date(0); d.setTime(1e30);")));
    }

    [Fact]
    public void SetTimeNaNTime()
    {
        Assert.True(double.IsNaN(RunNum("var d=new Date(0); d.setTime(NaN);")));
    }
}
