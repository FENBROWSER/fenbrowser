using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TemporalPlainTimeIntlTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void PlainTimeToLocaleStringMatchesIntlFormatter()
    {
        var result = Run("""
            const time = Temporal.PlainTime.from("1976-11-18T15:23:30");
            const dtf = new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York" });
            const parts = dtf.formatToParts(time);
            time.toLocaleString("en-US", { timeZone: "America/New_York" }) === dtf.format(time)
              && parts.some(part => part.type === "hour")
              && parts.some(part => part.type === "second");
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void PlainTimeIgnoresTimeZoneAndDateStyleThrows()
    {
        var result = Run("""
            const time = Temporal.PlainTime.from("2026-03-29T02:30:15+01:00");
            let threw = false;
            try { time.toLocaleString("en", { dateStyle: "short" }); } catch (e) { threw = e instanceof TypeError; }
            threw && time.toLocaleString("en", { timeStyle: "long" }) === time.toLocaleString("en", { timeStyle: "long", timeZone: "America/New_York" });
            """);

        Assert.True(result.AsBoolean());
    }
}
