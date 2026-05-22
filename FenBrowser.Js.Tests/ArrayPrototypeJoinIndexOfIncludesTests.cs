using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayPrototypeJoinIndexOfIncludesTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("[1,2,3].join();", "1,2,3")]
    [InlineData("[1,2,3].join('-');", "1-2-3")]
    [InlineData("[].join('-');", "")]
    [InlineData("['a',null,'c'].join(',');", "a,,c")]   // null stringifies to empty
    [InlineData("['a',undefined,'c'].join(',');", "a,,c")] // undefined stringifies to empty
    [InlineData("[1].join('-');", "1")]
    public void Join(string source, string expected)
    {
        Assert.Equal(expected, RunStr(source));
    }

    [Theory]
    [InlineData("[10, 20, 30].indexOf(20);", 1)]
    [InlineData("[10, 20, 30].indexOf(99);", -1)]
    [InlineData("[10, 20, 30].indexOf(10, 1);", -1)]
    [InlineData("[10, 20, 30].indexOf(30, -1);", 2)]
    [InlineData("[].indexOf(1);", -1)]
    [InlineData("[NaN].indexOf(NaN);", -1)]   // strict-eq; NaN !== NaN
    public void IndexOf(string source, double expected)
    {
        Assert.Equal(expected, RunNum(source));
    }

    [Theory]
    [InlineData("[10, 20, 30].includes(20);", true)]
    [InlineData("[10, 20, 30].includes(99);", false)]
    [InlineData("[10, 20, 30].includes(10, 1);", false)]
    [InlineData("[NaN].includes(NaN);", true)]   // SameValueZero catches NaN
    [InlineData("[0].includes(-0);", true)]      // SameValueZero treats +0 == -0
    [InlineData("[].includes(1);", false)]
    public void Includes(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }
}
