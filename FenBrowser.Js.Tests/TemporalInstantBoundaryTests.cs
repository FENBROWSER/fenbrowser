using System;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Regression tests for JSLIB-002: legal Temporal.Instant extremes
/// (±8.64e21 ns, beyond long.MaxValue) must never escape as raw CLR
/// OverflowException from unguarded BigInteger-to-long casts.
/// </summary>
public sealed class TemporalInstantBoundaryTests
{
    private const string MaxInstantNs = "8640000000000000000000";
    private const string MinInstantNs = "-8640000000000000000000";

    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData(MaxInstantNs)]
    [InlineData(MinInstantNs)]
    public void Instant_AtLegalExtremes_ConstructsAndKeepsEpochNanoseconds(string epochNs)
    {
        var result = RunString($$"""
            try {
                const instant = new Temporal.Instant({{epochNs}}n);
                instant.epochNanoseconds === {{epochNs}}n ? "ok" : "wrong-value";
            } catch (e) {
                e instanceof RangeError ? "range-error" : "clr-escape:" + String(e);
            }
            """);

        Assert.Equal("ok", result);
    }

    [Theory]
    [InlineData(MaxInstantNs)]
    [InlineData(MinInstantNs)]
    public void TimeZoneGetOffsetNanosecondsFor_AtExtremes_ReturnsNumber(string epochNs)
    {
        // Pre-fix this path cast the full BigInteger to long and escaped as a
        // raw CLR OverflowException instead of returning a JS number.
        var result = RunString($$"""
            try {
                const offset = Temporal.TimeZone.from("UTC").getOffsetNanosecondsFor(
                    new Temporal.Instant({{epochNs}}n));
                typeof offset === "number" && Number.isFinite(offset) ? "ok" : "not-a-number";
            } catch (e) {
                e instanceof RangeError || e instanceof TypeError ? "js-error" : "clr-escape:" + String(e);
            }
            """);

        Assert.True(
            result == "ok" || result == "js-error",
            $"Expected a JS number or JS error at the legal extreme, got: {result}");
    }

    [Fact]
    public void TimeZoneGetOffsetNanosecondsFor_NearExtremes_StillResolvesOffset()
    {
        var result = RunString($$"""
            const ns = {{MaxInstantNs}}n - 1n;
            const offset = Temporal.TimeZone.from("UTC").getOffsetNanosecondsFor(new Temporal.Instant(ns));
            offset === 0 ? "utc-zero-offset" : "unexpected:" + offset;
            """);

        Assert.Equal("utc-zero-offset", result);
    }

    [Theory]
    [InlineData(MaxInstantNs)]
    [InlineData(MinInstantNs)]
    public void ZonedDateTimeToLocaleString_AtExtremes_DoesNotEscapeClrException(string epochNs)
    {
        var result = RunString($$"""
            try {
                const zdt = new Temporal.ZonedDateTime({{epochNs}}n, "UTC");
                zdt.toLocaleString();
                "formatted-or-noop";
            } catch (e) {
                e instanceof RangeError ? "range-error"
                    : e instanceof TypeError ? "type-error"
                    : String(e).indexOf("Overflow") >= 0 ? "clr-escape" : "other-js-error";
            }
            """);

        Assert.True(
            result == "formatted-or-noop" || result == "range-error",
            $"Expected formatting or a spec RangeError at the legal extreme, got: {result}");
    }
}
