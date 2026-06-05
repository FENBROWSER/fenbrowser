using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DurationFormatTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void DurationFormatConstructorAndPrototypeSurfaceExists()
    {
        Assert.True(RunBool("""
            typeof Intl.DurationFormat === "function" &&
            Object.getPrototypeOf(new Intl.DurationFormat()) === Intl.DurationFormat.prototype &&
            Intl.DurationFormat.prototype.constructor === Intl.DurationFormat
            """));
    }

    [Fact]
    public void DurationFormatResolvedOptionsExposeExpectedDefaults()
    {
        Assert.True(RunBool("""
            var options = new Intl.DurationFormat("en").resolvedOptions();
            options.style === "short" &&
            options.years === "short" &&
            options.hours === "short" &&
            options.hoursDisplay === "always" &&
            options.numberingSystem === "latn"
            """));
    }

    [Fact]
    public void DurationFormatMethodsRejectWrongReceiver()
    {
        Assert.Equal("TypeError", RunStr("""
            try {
              Intl.DurationFormat.prototype.resolvedOptions.call({});
              "no";
            } catch (e) {
              e && e.name;
            }
            """));
    }

    [Fact]
    public void DurationFormatConstructorRejectsNullOptionsAndFiltersUnsupportedLocales()
    {
        Assert.True(RunBool("""
            var nullOptionsThrows = false;
            try {
              new Intl.DurationFormat([], null);
            } catch (e) {
              nullOptionsThrows = e && e.name === "TypeError";
            }

            var supported = Intl.DurationFormat.supportedLocalesOf(["en", "zxx"]);
            nullOptionsThrows &&
            supported.length === 1 &&
            supported[0] === "en"
            """));
    }

    [Fact]
    public void DurationFormatNegativePartsKeepSingleLeadingMinusSign()
    {
        Assert.True(RunBool("""
            var parts = new Intl.DurationFormat("en").formatToParts({ years: -1, months: -2 });
            parts[0].type === "minusSign" &&
            parts[0].value === "-" &&
            parts.filter(function (part) { return part.type === "minusSign"; }).length === 1
            """));
    }
}
