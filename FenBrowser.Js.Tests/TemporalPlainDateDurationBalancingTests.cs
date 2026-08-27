using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalPlainDateDurationBalancingTests
{
    private static bool RunBoolean(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsBoolean();
    }

    [Fact]
    public void AddBalancesEveryDayTimeUnitExactly()
    {
        Assert.True(RunBoolean("""
            const date = new Temporal.PlainDate(2000, 5, 2);
            date.add({
                days: 1, hours: 24, minutes: 1440, seconds: 86400,
                milliseconds: 86400000, microseconds: 86400000000,
                nanoseconds: 86400000000000
            }).toString() === "2000-05-09";
            """));
    }

    [Fact]
    public void SubtractBalancesEveryDayTimeUnitExactly()
    {
        Assert.True(RunBoolean("""
            const date = new Temporal.PlainDate(2000, 5, 9);
            date.subtract({
                days: 1, hours: 24, minutes: 1440, seconds: 86400,
                milliseconds: 86400000, microseconds: 86400000000,
                nanoseconds: 86400000000000
            }).toString() === "2000-05-02";
            """));
    }

    [Theory]
    [InlineData("new Temporal.PlainDate(1976, 11, 18).add({ nanoseconds: 1 }).toString()", "1976-11-18")]
    [InlineData("new Temporal.PlainDate(1976, 11, 18).add({ hours: 36 }).toString()", "1976-11-19")]
    [InlineData("new Temporal.PlainDate(2000, 5, 2).add({ hours: -25 }).toString()", "2000-05-01")]
    public void PartialDaysTruncateTowardZero(string expression, string expected)
    {
        Assert.True(RunBoolean($"{expression} === '{expected}';"));
    }
}
