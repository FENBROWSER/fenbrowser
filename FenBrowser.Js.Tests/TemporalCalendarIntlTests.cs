using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalCalendarIntlTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void EraLessCalendarsIgnoreEraFields()
    {
        var result = Run("""
            const chinese = Temporal.PlainDate.from({ year: 2025, month: 1, day: 1, era: "xyz", eraYear: 2025, calendar: "chinese" });
            const dangi = Temporal.PlainDateTime.from({ year: 2025, month: 1, day: 1, hour: 12, minute: 34, era: "xyz", eraYear: 2025, calendar: "dangi" });
            chinese instanceof Temporal.PlainDate && dangi instanceof Temporal.PlainDateTime;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InvalidEraThrowsForEraCalendars()
    {
        var result = Run("""
            let gregoryRange = false;
            let japaneseRange = false;
            try { Temporal.PlainDate.from({ year: 2025, month: 1, day: 1, era: "xyz", eraYear: 2025, calendar: "gregory" }); } catch (e) { gregoryRange = e instanceof RangeError; }
            try { Temporal.PlainDateTime.from({ year: 2025, month: 1, day: 1, era: "xyz", eraYear: 2025, calendar: "japanese" }); } catch (e) { japaneseRange = e instanceof RangeError; }
            gregoryRange && japaneseRange;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void EastAsianPlainMonthDayValidatesMissingFieldsBeforeMonthConflicts()
    {
        var result = Run("""
            let missingYearType = false;
            let conflictRange = false;
            try { Temporal.PlainMonthDay.from({ calendar: "chinese", monthCode: "M04", month: 5, day: 1 }); } catch (e) { missingYearType = e instanceof TypeError; }
            try { Temporal.PlainMonthDay.from({ calendar: "chinese", year: 2020, monthCode: "M04", month: 5, day: 1 }); } catch (e) { conflictRange = e instanceof RangeError; }
            missingYearType && conflictRange;
            """);

        Assert.True(result.AsBoolean());
    }
}
