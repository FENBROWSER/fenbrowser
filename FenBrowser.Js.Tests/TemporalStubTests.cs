using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalStubTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void TemporalObjectAndConstructorsExist()
    {
        var result = Run("typeof Temporal === 'object' && typeof Temporal.Instant === 'function' && typeof Temporal.PlainDate === 'function';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TemporalNowSurfaceExists()
    {
        var result = Run("typeof Temporal.Now === 'object' && typeof Temporal.Now.instant === 'function' && typeof Temporal.Now.plainDateISO === 'function';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainDateDifferencesApplyRoundingSettings()
    {
        var result = Run("""
            const start = Temporal.PlainDate.from("2000-01-01");
            const end = Temporal.PlainDate.from("2000-01-04");
            const truncated = start.until(end, { smallestUnit: "day", roundingIncrement: 2.5 });
            const expanded = start.until(end, { smallestUnit: "day", roundingIncrement: 2, roundingMode: "expand" });
            const since = end.since(start, { smallestUnit: "day", roundingIncrement: 2, roundingMode: "floor" });
            truncated.days === 2 && expanded.days === 4 && since.days === 2;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainYearMonthDifferencesApplyRoundingSettings()
    {
        var result = Run("""
            const start = Temporal.PlainYearMonth.from("2000-01");
            const end = Temporal.PlainYearMonth.from("2001-07");
            const truncated = start.until(end, { smallestUnit: "year", roundingMode: "trunc" });
            const expanded = start.until(end, { smallestUnit: "year", roundingMode: "expand" });
            const since = end.since(start, { smallestUnit: "month", roundingIncrement: 5 });
            truncated.years === 1 && expanded.years === 2 && since.years === 1 && since.months === 3;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainMonthDayToPlainDateUsesYearAndConstrainsByDefault()
    {
        var result = Run("""
            const leapDay = Temporal.PlainMonthDay.from("02-29");
            let optionsRead = false;
            const options = { get overflow() { optionsRead = true; return "reject"; } };
            const leap = leapDay.toPlainDate({ year: 2020 }, options);
            const constrained = leapDay.toPlainDate({ year: 2023 });
            leap.year === 2020 && leap.month === 2 && leap.day === 29 &&
                constrained.year === 2023 && constrained.month === 2 && constrained.day === 28 &&
                !optionsRead;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeDifferencesRoundSubDayUnits()
    {
        var result = Run("""
            const start = new Temporal.ZonedDateTime(0n, "UTC");
            const end = new Temporal.ZonedDateTime(12600000000000n, "UTC");
            const expanded = start.until(end, {
                largestUnit: "hour",
                smallestUnit: "hour",
                roundingIncrement: 2,
                roundingMode: "expand"
            });
            const truncated = end.since(start, {
                largestUnit: "hour",
                smallestUnit: "hour",
                roundingIncrement: 2,
                roundingMode: "trunc"
            });
            expanded.hours === 4 && truncated.hours === 2;
            """);

        Assert.True(result.AsBoolean());
    }
}
