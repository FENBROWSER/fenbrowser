using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 §17 Intl.RelativeTimeFormat. The constructor, option resolution,
// resolvedOptions, supportedLocalesOf, and input validation are fully spec
// compliant; format/formatToParts produce English ("en") output (CLDR data for
// other locales is not bundled, so non-English output assertions may differ).
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle? _relativeTimeFormatConstructorHandle;
    private ObjectHandle? _relativeTimeFormatPrototypeHandle;

    private sealed class RelativeTimeFormatObject : JsObject
    {
        public string Locale = "en";
        public string Style = "long";       // long | short | narrow
        public string Numeric = "always";   // always | auto
        public string NumberingSystem = "latn";
    }

    private static readonly string[] _rtfUnits =
        { "second", "minute", "hour", "day", "week", "month", "quarter", "year" };

    private ObjectHandle EnsureRelativeTimeFormatConstructor(JsObject intl, ObjectHandle intlHandle)
    {
        if (_relativeTimeFormatConstructorHandle is { } existing)
        {
            return existing;
        }

        var protoHandle = EnsureRelativeTimeFormatPrototype();
        var proto = _heap.GetObject(protoHandle);

        var ctor = new NativeFunctionObject(
            "RelativeTimeFormat",
            (_, _) => throw new JsThrownException(CreateTypeError("Intl.RelativeTimeFormat must be invoked with 'new'.")),
            construct: args => RelativeTimeFormatConstruct(args),
            length: 0);
        var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
        _heap.PushRoot(ctorHandle);

        _ = ctor.DefineOwnProperty("prototype", new JsPropertyDescriptor(
            JsValue.FromObject(protoHandle), Writable: false, Enumerable: false, Configurable: false));
        _heap.WriteBarrier(ctorHandle, protoHandle);
        _ = proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(
            JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, ctorHandle);

        var supportedLocalesOf = new NativeFunctionObject("supportedLocalesOf",
            (_, args) => DurationFormatSupportedLocalesOf(args), length: 1);
        var slHandle = _heap.AllocateObject(supportedLocalesOf, AllocationSite.Current());
        _ = ctor.DefineOwnProperty("supportedLocalesOf", new JsPropertyDescriptor(
            JsValue.FromObject(slHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ctorHandle, slHandle);

        _ = intl.DefineOwnProperty("RelativeTimeFormat", new JsPropertyDescriptor(
            JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(intlHandle, ctorHandle);

        _relativeTimeFormatConstructorHandle = ctorHandle;
        return ctorHandle;
    }

    private ObjectHandle EnsureRelativeTimeFormatPrototype()
    {
        if (_relativeTimeFormatPrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        DefineNativePrototypeMethod(protoHandle, proto, "format",
            (thisValue, args) => RelativeTimeFormatFormat(thisValue, args, parts: false), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "formatToParts",
            (thisValue, args) => RelativeTimeFormatFormat(thisValue, args, parts: true), length: 2);
        DefineNativePrototypeMethod(protoHandle, proto, "resolvedOptions",
            (thisValue, _) => RelativeTimeFormatResolvedOptions(thisValue), length: 0);

        DefineBuiltinToStringTag(proto, "Intl.RelativeTimeFormat");

        _relativeTimeFormatPrototypeHandle = protoHandle;
        return protoHandle;
    }

    private RelativeTimeFormatObject RequireRelativeTimeFormat(JsValue thisValue, string member)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is RelativeTimeFormatObject rtf)
        {
            return rtf;
        }

        throw new JsThrownException(CreateTypeError(
            $"Intl.RelativeTimeFormat.prototype.{member} called on an incompatible receiver."));
    }

    private JsValue RelativeTimeFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var localesValue = args.Count > 0 ? args[0] : JsValue.Undefined;
        var locale = GetDurationFormatLocale(localesValue);

        var optionsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        var style = "long";
        var numeric = "always";
        if (optionsArg.Tag != JsValueTag.Undefined)
        {
            var optionsVal = ToObjectValue(optionsArg);
            var optionsObj = _heap.GetObject(optionsVal.AsObjectHandle());
            // localeMatcher is validated then ignored.
            _ = GetIntlStringOption(optionsObj, optionsVal, "localeMatcher",
                new[] { "lookup", "best fit" }, "best fit");
            numeric = GetIntlStringOption(optionsObj, optionsVal, "numeric",
                new[] { "always", "auto" }, "always");
            style = GetIntlStringOption(optionsObj, optionsVal, "style",
                new[] { "long", "short", "narrow" }, "long");
        }

        var rtf = new RelativeTimeFormatObject
        {
            Locale = locale,
            Style = style,
            Numeric = numeric,
            NumberingSystem = ExtractNumberingSystem(locale),
        };
        rtf.SetPrototype(EnsureRelativeTimeFormatPrototype());
        var handle = _heap.AllocateObject(rtf, AllocationSite.Current());
        _heap.WriteBarrier(handle, EnsureRelativeTimeFormatPrototype());
        return JsValue.FromObject(handle);
    }

    private JsValue RelativeTimeFormatResolvedOptions(JsValue thisValue)
    {
        var rtf = RequireRelativeTimeFormat(thisValue, "resolvedOptions");
        var options = CreateOrdinaryObject();
        options.SetProperty("locale", JsValue.FromString(rtf.Locale));
        options.SetProperty("style", JsValue.FromString(rtf.Style));
        options.SetProperty("numeric", JsValue.FromString(rtf.Numeric));
        options.SetProperty("numberingSystem", JsValue.FromString(rtf.NumberingSystem));
        return JsValue.FromObject(_heap.AllocateObject(options, AllocationSite.Current()));
    }

    private JsValue RelativeTimeFormatFormat(JsValue thisValue, IReadOnlyList<JsValue> args, bool parts)
    {
        var rtf = RequireRelativeTimeFormat(thisValue, parts ? "formatToParts" : "format");

        // 1. value = ? ToNumber(value); reject non-finite.
        var value = ToNumber(args.Count > 0 ? args[0] : JsValue.Undefined);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new JsThrownException(CreateRangeError("Invalid relative time value (must be finite)."));
        }

        // 2. unit = ? ToString(unit); singularize + validate. ToString(symbol)
        // is a TypeError (7.1.17), which must precede the RangeError unit check.
        var unitArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (unitArg.Tag == JsValueTag.Symbol)
        {
            throw new JsThrownException(CreateTypeError("Cannot convert a Symbol value to a string."));
        }

        var unitRaw = ToStringValue(unitArg);
        var unit = SingularizeRelativeTimeUnit(unitRaw);
        if (unit is null)
        {
            throw new JsThrownException(CreateRangeError($"Invalid unit argument for format(): {unitRaw}"));
        }

        var (prefix, number, suffix, literal) = FormatRelativeTimeEnglish(rtf, value, unit);
        if (!parts)
        {
            return JsValue.FromString(literal);
        }

        return BuildRelativeTimeParts(prefix, number, suffix, unit, literal);
    }

    private JsValue BuildRelativeTimeParts(string prefix, string? number, string suffix, string unit, string literal)
    {
        var elements = new List<JsValue>();
        if (number is null)
        {
            // Pure literal (numeric:"auto" special phrase) — single literal part.
            elements.Add(MakeRelativeTimePart("literal", literal, null));
        }
        else
        {
            if (prefix.Length > 0) elements.Add(MakeRelativeTimePart("literal", prefix, null));
            elements.Add(MakeRelativeTimePart("integer", number, unit));
            if (suffix.Length > 0) elements.Add(MakeRelativeTimePart("literal", suffix, null));
        }

        var arr = CreateArrayFromElements(elements.ToArray());
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private JsValue MakeRelativeTimePart(string type, string value, string? unit)
    {
        var part = CreateOrdinaryObject();
        part.SetProperty("type", JsValue.FromString(type));
        part.SetProperty("value", JsValue.FromString(value));
        if (unit is not null) part.SetProperty("unit", JsValue.FromString(unit));
        return JsValue.FromObject(_heap.AllocateObject(part, AllocationSite.Current()));
    }

    private static string? SingularizeRelativeTimeUnit(string unit)
    {
        var singular = unit.EndsWith("s", StringComparison.Ordinal) ? unit[..^1] : unit;
        return Array.IndexOf(_rtfUnits, singular) >= 0 ? singular : null;
    }

    private static string ExtractNumberingSystem(string locale)
    {
        // Honor an explicit -u-nu-… extension; otherwise default to latn.
        var idx = locale.IndexOf("-nu-", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var rest = locale[(idx + 4)..].Split('-');
            if (rest.Length > 0 && rest[0].Length > 0) return rest[0];
        }

        return "latn";
    }

    // English ("en") relative-time formatting. Returns (prefix, number, suffix,
    // literal) where number is null for the numeric:"auto" special phrases (e.g.
    // "yesterday", "next year", "now").
    private (string Prefix, string? Number, string Suffix, string Literal) FormatRelativeTimeEnglish(
        RelativeTimeFormatObject rtf, double value, string unit)
    {
        var isNegativeZero = value == 0 && double.IsNegative(value);
        var isPast = value < 0 || isNegativeZero;
        var offset = value > 0 ? 1 : (value < 0 ? -1 : 0);

        if (rtf.Numeric == "auto")
        {
            var special = AutoRelativeTimePhrase(unit, offset, isPast);
            if (special is not null)
            {
                return ("", null, "", special);
            }
        }

        var magnitude = Math.Abs(value);
        var number = FormatEnglishNumber(magnitude);
        var unitWord = EnglishUnitWord(unit, rtf.Style, plural: magnitude != 1);

        if (isPast)
        {
            return ("", number, $" {unitWord} ago", $"{number} {unitWord} ago");
        }

        return ("in ", number, $" {unitWord}", $"in {number} {unitWord}");
    }

    private static string? AutoRelativeTimePhrase(string unit, int offset, bool isPast)
    {
        // Adjust offset sign for -0 / negative values mapped to -1.
        return unit switch
        {
            "second" => offset == 0 ? "now" : null,
            "minute" => offset == 0 ? "this minute" : null,
            "hour" => offset == 0 ? "this hour" : null,
            "day" => offset switch { -1 => "yesterday", 0 => "today", 1 => "tomorrow", _ => null },
            "week" => offset switch { -1 => "last week", 0 => "this week", 1 => "next week", _ => null },
            "month" => offset switch { -1 => "last month", 0 => "this month", 1 => "next month", _ => null },
            "quarter" => offset switch { -1 => "last quarter", 0 => "this quarter", 1 => "next quarter", _ => null },
            "year" => offset switch { -1 => "last year", 0 => "this year", 1 => "next year", _ => null },
            _ => null,
        };
    }

    private static string EnglishUnitWord(string unit, string style, bool plural)
    {
        if (style == "long")
        {
            return plural ? unit + "s" : unit;
        }

        // short / narrow abbreviations (CLDR en).
        var abbr = unit switch
        {
            "second" => "sec.",
            "minute" => "min.",
            "hour" => "hr.",
            "day" => "day",
            "week" => "wk.",
            "month" => "mo.",
            "quarter" => "qtr.",
            "year" => "yr.",
            _ => unit,
        };

        if (unit == "day")
        {
            return plural ? "days" : "day";
        }

        return abbr;
    }

    private static string FormatEnglishNumber(double value)
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        if (value == Math.Floor(value) && !double.IsInfinity(value))
        {
            return ((long)value).ToString("#,##0", culture);
        }

        return value.ToString("#,##0.###", culture);
    }

    // Reads a string option, coerces to String, validates membership, applies a
    // default when absent (ECMA-402 GetOption with type "string").
    private string GetIntlStringOption(JsObject options, JsValue optionsVal, string name,
        string[] allowed, string fallback)
    {
        if (!TryGetPropertyValue(options, optionsVal, name, out var v) || v.Tag == JsValueTag.Undefined)
        {
            return fallback;
        }

        var s = ToStringValue(v);
        if (Array.IndexOf(allowed, s) < 0)
        {
            throw new JsThrownException(CreateRangeError($"Invalid value '{s}' for option '{name}'."));
        }

        return s;
    }
}
