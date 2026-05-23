using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateUtcGetterTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private const string Base = "var d = new Date(1577934245678);";

    [Fact] public void Year()   => Assert.Equal(2020, RunNum(Base + " d.getUTCFullYear();"));
    [Fact] public void Month()  => Assert.Equal(0,    RunNum(Base + " d.getUTCMonth();"));
    [Fact] public void Date()   => Assert.Equal(2,    RunNum(Base + " d.getUTCDate();"));
    [Fact] public void Day()    => Assert.Equal(4,    RunNum(Base + " d.getUTCDay();"));
    [Fact] public void Hours()  => Assert.Equal(3,    RunNum(Base + " d.getUTCHours();"));
    [Fact] public void Minutes()=> Assert.Equal(4,    RunNum(Base + " d.getUTCMinutes();"));
    [Fact] public void Seconds()=> Assert.Equal(5,    RunNum(Base + " d.getUTCSeconds();"));
    [Fact] public void Millis() => Assert.Equal(678,  RunNum(Base + " d.getUTCMilliseconds();"));
    [Fact] public void TimezoneOffset() => Assert.Equal(0, RunNum(Base + " d.getTimezoneOffset();"));
}
