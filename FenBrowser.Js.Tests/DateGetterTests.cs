using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateGetterTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    // 1577934245678 = 2020-01-02T03:04:05.678Z (Thursday)
    private const string Base = "var d = new Date(1577934245678);";

    [Fact] public void Year()   => Assert.Equal(2020, RunNum(Base + " d.getFullYear();"));
    [Fact] public void Month()  => Assert.Equal(0,    RunNum(Base + " d.getMonth();"));
    [Fact] public void Date()   => Assert.Equal(2,    RunNum(Base + " d.getDate();"));
    [Fact] public void Day()    => Assert.Equal(4,    RunNum(Base + " d.getDay();")); // Thursday
    [Fact] public void Hours()  => Assert.Equal(3,    RunNum(Base + " d.getHours();"));
    [Fact] public void Minutes()=> Assert.Equal(4,    RunNum(Base + " d.getMinutes();"));
    [Fact] public void Seconds()=> Assert.Equal(5,    RunNum(Base + " d.getSeconds();"));
    [Fact] public void Millis() => Assert.Equal(678,  RunNum(Base + " d.getMilliseconds();"));

    [Fact]
    public void NaNDateProducesNaN()
    {
        Assert.True(double.IsNaN(RunNum("new Date(NaN).getFullYear();")));
    }
}
