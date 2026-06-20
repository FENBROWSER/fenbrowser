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

    [Fact]
    public void IntlSupportedTimeZonesAreCanonicalTemporalIdentifiers()
    {
        var count = Run("""Intl.supportedValuesOf("timeZone").length;""");
        var result = Run("""
            const zones = Intl.supportedValuesOf("timeZone");
            let valid = zones.length > 100 && !zones.includes("AUS Central Standard Time");
            for (const id of zones) {
                const value = new Temporal.ZonedDateTime(0n, id);
                if (value.timeZoneId !== id) valid = false;
            }
            valid;
            """);

        Assert.True(count.AsNumber() > 100, $"Expected TZDB inventory, got {count.AsNumber()} identifiers.");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimePreservesIanaLinksButComparesCanonicalZones()
    {
        var result = Run("""
            const calcutta = Temporal.ZonedDateTime.from(
                "2020-01-01T00:00:00+05:30[Asia/Calcutta]");
            const kolkata = Temporal.ZonedDateTime.from(
                "2020-01-01T00:00:00+05:30[Asia/Kolkata]");
            const lowerCase = new Temporal.ZonedDateTime(0n, "america/los_angeles");

            calcutta.timeZoneId === "Asia/Calcutta" &&
                kolkata.timeZoneId === "Asia/Kolkata" &&
                lowerCase.timeZoneId === "america/los_angeles" &&
                calcutta.equals(kolkata);
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeDifferenceUsesCalendarAnchorsAndZoneWallTime()
    {
        var result = Run("""
            const earlier = Temporal.ZonedDateTime.from({
                year: 1997, month: 7, day: 16, hour: 12, minute: 34,
                timeZone: "UTC", calendar: "gregory"
            });
            const later = Temporal.ZonedDateTime.from({
                year: 2021, month: 7, day: 15, hour: 12, minute: 34,
                timeZone: "UTC", calendar: "gregory"
            });
            const backward = earlier.since(later, { largestUnit: "year" });
            const forward = later.since(earlier, { largestUnit: "year" });

            backward.years === -23 && backward.months === -11 && backward.days === -29 &&
                forward.years === 23 && forward.months === 11 && forward.days === 30;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainMonthDayPropertyBagsUseTheLatestReferenceDateBefore1973()
    {
        var result = Run("""
            const gregory = Temporal.PlainMonthDay.from({
                year: 2021, monthCode: "M02", day: 29, calendar: "gregory"
            });
            const hebrew = Temporal.PlainMonthDay.from({
                year: 5781, monthCode: "M02", day: 30, calendar: "hebrew"
            });
            const fromString = Temporal.PlainMonthDay.from("2023-01-01[u-ca=hebrew]");

            gregory.referenceISOYear === 1972 && gregory.day === 28 &&
                hebrew.referenceISOYear === 1972 && hebrew.day === 29 &&
                fromString.referenceISOYear === 1972;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeReturnsStrictTzdbTransitions()
    {
        var result = Run("""
            const before = Temporal.ZonedDateTime.from(
                "2020-03-08T01:00-08:00[America/Los_Angeles]");
            const transition = before.getTimeZoneTransition("next");
            const previous = transition.getTimeZoneTransition("previous");
            const utc = new Temporal.ZonedDateTime(0n, "UTC");

            transition.epochNanoseconds === 1_583_661_600_000_000_000n &&
                previous.epochNanoseconds < transition.epochNanoseconds &&
                utc.getTimeZoneTransition("next") === null;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ZonedDateTimeTransitionPreservesNanosecondBoundary()
    {
        var result = Run("""
            const zdt = new Temporal.PlainDateTime(1800, 1, 1)
                .toZonedDateTime("Europe/Paris");
            const first = zdt.getTimeZoneTransition("next");
            const minusSecond = first.add({ seconds: -1 });
            const minusNanosecond = first.add({ nanoseconds: -1 });
            let mask = 0;
            if (zdt.toString() === "1800-01-01T00:00:00+00:09[Europe/Paris]") mask += 1;
            if (first.toString() === "1911-03-10T23:50:39+00:00[Europe/Paris]") mask += 2;
            if (minusSecond.toString() === "1911-03-10T23:59:59+00:09[Europe/Paris]") mask += 4;
            if (minusSecond.getTimeZoneTransition("next").equals(first)) mask += 8;
            if (minusNanosecond.toString() === "1911-03-10T23:59:59.999999999+00:09[Europe/Paris]") mask += 16;
            if (minusNanosecond.getTimeZoneTransition("next").equals(first)) mask += 32;
            mask;
            """);
        var nanosecondText = Run("""
            const first = new Temporal.PlainDateTime(1800, 1, 1)
                .toZonedDateTime("Europe/Paris")
                .getTimeZoneTransition("next");
            first.add({ nanoseconds: -1 }).toString();
            """);

        Assert.Equal("1911-03-10T23:59:59.999999999+00:09[Europe/Paris]", nanosecondText.AsString());
        Assert.Equal(63, result.AsNumber());
    }

    [Fact]
    public void ZonedDateTimeStartOfDayUsesTheFirstValidInstant()
    {
        var result = Run("""
            const start = Temporal.ZonedDateTime.from("1919-03-31[America/Toronto]");
            const midnight = Temporal.ZonedDateTime.from("1919-03-31T00[America/Toronto]");
            const delta = start.until(midnight, { largestUnit: "minute" });
            const samoa = Temporal.ZonedDateTime.from("2011-12-29T12:00-10:00[Pacific/Apia]");

            start.hour === 0 && start.minute === 30 && delta.minutes === 30 &&
                samoa.hoursInDay === 24;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainYearMonthRejectsMissingLeapMonthDuringArithmetic()
    {
        var result = Run("""
            const leap = Temporal.PlainYearMonth.from({
                year: 5784, monthCode: "M05L", calendar: "hebrew"
            });
            let addRejected = false;
            let subtractRejected = false;
            try { leap.add({ years: 1 }, { overflow: "reject" }); }
            catch (e) { addRejected = e instanceof RangeError; }
            try { leap.subtract({ years: 1 }, { overflow: "reject" }); }
            catch (e) { subtractRejected = e instanceof RangeError; }

            addRejected && subtractRejected;
            """);

        Assert.True(result.AsBoolean());
    }
}
