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

    [Fact]
    public void PlainMonthDayRejectsMonthCodesOutsideCalendarRange()
    {
        var result = Run("""
            const cases = [
                ["gregory", "M13"],
                ["coptic", "M14"],
                ["hebrew", "M14"],
                ["islamic-umalqura", "M13"]
            ];
            cases.every(([calendar, monthCode]) => {
                for (const overflow of [undefined, "constrain", "reject"]) {
                    try {
                        Temporal.PlainMonthDay.from({ calendar, monthCode, day: 1 }, { overflow });
                        return false;
                    } catch (e) {
                        if (!(e instanceof RangeError)) return false;
                    }
                }
                return true;
            });
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PartialDateLocaleFormattingOmitsReferenceFieldsAndRequiresMatchingCalendar()
    {
        var result = Run("""
            const monthDay = Temporal.PlainMonthDay.from({ monthCode: "M12", day: 26, calendar: "gregory" });
            const yearMonth = new Temporal.PlainDate(2024, 12, 26, "gregory").toPlainYearMonth();
            const monthDayText = monthDay.toLocaleString("en-US");
            const yearMonthText = yearMonth.toLocaleString("en-US");

            let monthDayMismatch = false;
            let yearMonthMismatch = false;
            try {
                Temporal.PlainMonthDay.from({ monthCode: "M01", day: 1, calendar: "iso8601" }).toLocaleString();
            } catch (e) {
                monthDayMismatch = e instanceof RangeError;
            }
            try {
                new Temporal.PlainDate(2000, 1, 1, "iso8601").toPlainYearMonth().toLocaleString();
            } catch (e) {
                yearMonthMismatch = e instanceof RangeError;
            }

            !monthDayText.includes("1972") &&
                !yearMonthText.includes("26") &&
                monthDayMismatch &&
                yearMonthMismatch;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeStringOffsetsAllowNamedZoneMinuteRoundingOnly()
    {
        var result = Run("""
            const rounded = Temporal.ZonedDateTime.from("1970-01-01T00:00:00-00:45[Africa/Monrovia]");
            const exact = Temporal.ZonedDateTime.from("1970-01-01T00:00:00-00:44:30[Africa/Monrovia]");
            let secondsRejected = false;
            let propertyBagRejected = false;
            try {
                Temporal.ZonedDateTime.from("1970-01-01T00:00:00-00:45:00[Africa/Monrovia]");
            } catch (e) {
                secondsRejected = e instanceof RangeError;
            }
            try {
                Temporal.ZonedDateTime.from({
                    year: 1970,
                    month: 1,
                    day: 1,
                    offset: "-00:45",
                    timeZone: "Africa/Monrovia"
                });
            } catch (e) {
                propertyBagRejected = e instanceof RangeError;
            }
            rounded.epochNanoseconds === exact.epochNanoseconds &&
                secondsRejected &&
                propertyBagRejected;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeFromHonorsOffsetAndDisambiguationOptions()
    {
        var result = Run("""
            const use = Temporal.ZonedDateTime.from(
                "2020-11-01T04:00-07:00[America/Los_Angeles]",
                { offset: "use" });
            const ignore = Temporal.ZonedDateTime.from(
                "2020-11-01T04:00-12:00[America/Los_Angeles]",
                { offset: "ignore" });
            const earlier = Temporal.ZonedDateTime.from(
                "2020-03-08T02:30[America/Los_Angeles]",
                { offset: "ignore", disambiguation: "earlier" });
            const later = Temporal.ZonedDateTime.from(
                "2020-03-08T02:30[America/Los_Angeles]",
                { offset: "ignore", disambiguation: "later" });
            let rejected = false;
            try {
                Temporal.ZonedDateTime.from(
                    "2020-03-08T02:30[America/Los_Angeles]",
                    { offset: "ignore", disambiguation: "reject" });
            } catch (e) {
                rejected = e instanceof RangeError;
            }

            use.hour === 3 && ignore.hour === 4 &&
                earlier.hour === 1 && later.hour === 3 && rejected;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeFromPreservesHistoricalSubMinuteOffsets()
    {
        var epochMatches = Run("""
            Temporal.ZonedDateTime.from(
                "1970-01-01T12:00-00:44:30[Africa/Monrovia]",
                { offset: "use" }).epochNanoseconds === 45_870_000_000_000n;
            """);
        var offsetMatches = Run("""
            Temporal.ZonedDateTime.from(
                "1970-01-01T12:00-00:44:30[Africa/Monrovia]",
                { offset: "use" }).offset === "-00:44:30";
            """);

        Assert.True(epochMatches.AsBoolean());
        Assert.True(offsetMatches.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeDateGettersUseTheAttachedCalendar()
    {
        var result = Run("""
            const value = Temporal.ZonedDateTime.from({
                year: 1716,
                monthCode: "M12",
                day: 1,
                hour: 12,
                timeZone: "UTC",
                calendar: "coptic"
            });
            const previous = value.add({ days: -1 });
            const shifted = previous.add({ months: 6 });

            value.year === 1716 && value.month === 12 && value.day === 1 &&
                shifted.day === Math.min(previous.day, shifted.daysInMonth);
            """);

        Assert.True(result.AsBoolean());
    }
}
