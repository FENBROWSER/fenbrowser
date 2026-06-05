using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalInstantIntlTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void InstantToLocaleStringMatchesIntlDateTimeFormat()
    {
        var result = Run("""
            const instant = Temporal.Instant.from("1976-11-18T14:23:30Z");
            const dtf = new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York" });
            const parts = dtf.formatToParts(instant);
            instant.toLocaleString("en-US", { timeZone: "America/New_York" }) === dtf.format(instant)
              && parts.some(part => part.type === "year")
              && parts.some(part => part.type === "hour");
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InstantTimeZoneFormattingUsesResolvedZone()
    {
        var result = Run("""
            const instant = new Temporal.Instant(0n);
            const zoned = instant.toZonedDateTimeISO("2021-08-19T17:30[America/Vancouver]");
            instant.toString({ timeZone: "Europe/Berlin" }) === "1970-01-01T01:00:00+01:00"
              && zoned.timeZoneId === "America/Vancouver";
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DateToLocaleStringMatchesInstantForSharedOptions()
    {
        var result = Run("""
            const instant = new Temporal.Instant(0n);
            instant.toLocaleString("en", { era: "narrow" }) === new Date(0).toLocaleString("en", { era: "narrow" });
            """);

        Assert.True(result.AsBoolean());
    }
}
