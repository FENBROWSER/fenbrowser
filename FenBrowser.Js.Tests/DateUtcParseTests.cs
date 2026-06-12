using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DateUtcParseTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void UtcOfUnixEpochIsZero()
    {
        Assert.Equal(0d, RunNum("Date.UTC(1970, 0, 1);"));
    }

    [Fact]
    public void UtcWithFullArgs()
    {
        // 2000-01-01T00:00:00Z = 946684800000
        Assert.Equal(946684800000d, RunNum("Date.UTC(2000, 0, 1, 0, 0, 0, 0);"));
    }

    [Fact]
    public void UtcWith2DigitYearMapsTo1900Plus()
    {
        // year 70 -> 1970
        Assert.Equal(0d, RunNum("Date.UTC(70, 0, 1);"));
    }

    [Fact]
    public void UtcDefaultsMissingFields()
    {
        // Just year + month -> day defaults to 1, time defaults to 00:00:00.000.
        Assert.Equal(946684800000d, RunNum("Date.UTC(2000, 0);"));
    }

    [Fact]
    public void UtcWithNoArgsIsNaN()
    {
        Assert.True(double.IsNaN(RunNum("Date.UTC();")));
    }

    [Fact]
    public void UtcWithNonFiniteComponentIsNaN()
    {
        // ECMA-262 21.4.3.4 MakeTime/MakeDate: a non-finite component yields NaN.
        // (An out-of-range-but-finite component such as month 99 is *not* invalid —
        // MakeDay normalizes the overflow, so Date.UTC(2000, 99, 1) is a valid date.)
        Assert.True(double.IsNaN(RunNum("Date.UTC(2000, Infinity, 1);")));
        Assert.False(double.IsNaN(RunNum("Date.UTC(2000, 99, 1);")));
    }

    [Fact]
    public void ParseIso8601Z()
    {
        Assert.Equal(946684800000d, RunNum("Date.parse('2000-01-01T00:00:00Z');"));
    }

    [Fact]
    public void ParseInvalidIsNaN()
    {
        Assert.True(double.IsNaN(RunNum("Date.parse('not a date');")));
    }
}
