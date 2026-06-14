using FenBrowser.Js.Heap;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 Internationalization API — partial class with Intl-specific methods.
// Also carries EnsureProxyConstructor() placeholder until full Proxy lands.
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle? _dateTimeFormatPrototypeHandle;
    private ObjectHandle? _durationFormatConstructorHandle;
    private ObjectHandle? _durationFormatPrototypeHandle;
    private sealed record IntlPart(string Type, string Value, string? Unit = null);
    private sealed record NumberFormatState(
        string Locale,
        string? Style,
        string? Currency,
        string? CurrencyDisplay,
        string? Unit,
        string? UnitDisplay,
        string? Notation,
        int MinimumIntegerDigits,
        int? MinimumFractionDigits,
        int? MaximumFractionDigits,
        int? MinimumSignificantDigits,
        int? MaximumSignificantDigits,
        bool UseGrouping,
        string? SignDisplay,
        string NumberingSystem);
    private sealed record ListFormatState(string Locale, string Type, string Style);
    private static readonly string[] DurationUnits =
    {
        "years", "months", "weeks", "days", "hours",
        "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds"
    };

    // ECMA-402 11.1.1 InitializeDateTimeFormat.
    private JsValue DateTimeFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var options = ParseDateTimeFormatOptions(locale, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            IntlDateTimeFormatting.ValidateOptions(options);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) => DateTimeFormatPrototypeFormat(culture, options, fmtArgs),
            length: 1);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(
                JsValue.FromObject(protoMethodHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (_, fmtArgs) => DateTimeFormatPrototypeFormatToParts(culture, options, fmtArgs),
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty(
            "formatToParts",
            new JsPropertyDescriptor(
                JsValue.FromObject(formatToPartsHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, formatToPartsHandle);

        var resolvedOptionsMethod = new NativeFunctionObject(
            "resolvedOptions",
            (_, _) => DateTimeFormatResolvedOptions(locale, options),
            length: 0);
        var resolvedOptionsHandle = _heap.AllocateObject(resolvedOptionsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty(
            "resolvedOptions",
            new JsPropertyDescriptor(
                JsValue.FromObject(resolvedOptionsHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, resolvedOptionsHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);

        return JsValue.FromObject(instanceHandle);
    }

    // ECMA-402 11.5.1 Intl.DateTimeFormat.prototype.resolvedOptions. Returns a
    // new ordinary object with the resolved configuration in the spec's property
    // order. When dateStyle/timeStyle is used the individual component options
    // are omitted in their favour.
    private JsValue DateTimeFormatResolvedOptions(string locale, IntlDateTimeFormatOptions options)
    {
        var result = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(result, AllocationSite.Current());
        var rootMark = _heap.RootCount;
        _heap.PushRoot(handle);

        void Put(string name, JsValue value) =>
            result.DefineOwnProperty(name,
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        void PutStr(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Put(name, JsValue.FromString(value));
            }
        }

        var numberingSystem = ExtractUnicodeKeyword(locale, "nu") ?? "latn";
        var calendar = options.CalendarId ?? ExtractUnicodeKeyword(locale, "ca") ?? "gregory";
        var hasStyle = !string.IsNullOrEmpty(options.DateStyle) || !string.IsNullOrEmpty(options.TimeStyle);

        Put("locale", JsValue.FromString(ResolveIntlLocale(locale, null)));
        Put("calendar", JsValue.FromString(calendar.ToLowerInvariant()));
        Put("numberingSystem", JsValue.FromString(numberingSystem.ToLowerInvariant()));
        Put("timeZone", JsValue.FromString(options.TimeZoneId ?? "UTC"));

        if (options.HourCycle is not null)
        {
            Put("hourCycle", JsValue.FromString(options.HourCycle));
            Put("hour12", JsValue.FromBoolean(options.HourCycle is "h11" or "h12"));
        }
        else if (options.Hour12 is { } h12)
        {
            Put("hour12", JsValue.FromBoolean(h12));
        }

        if (hasStyle)
        {
            PutStr("dateStyle", options.DateStyle);
            PutStr("timeStyle", options.TimeStyle);
        }
        else
        {
            PutStr("weekday", options.Weekday);
            PutStr("era", options.Era);
            PutStr("year", options.Year);
            PutStr("month", options.Month);
            PutStr("day", options.Day);
            PutStr("dayPeriod", options.DayPeriod);
            PutStr("hour", options.Hour);
            PutStr("minute", options.Minute);
            PutStr("second", options.Second);
            if (options.FractionalSecondDigits is { } fsd)
            {
                Put("fractionalSecondDigits", JsValue.FromNumber(fsd));
            }

            PutStr("timeZoneName", options.TimeZoneName);
        }

        _heap.PopRootsTo(rootMark);
        return JsValue.FromObject(handle);
    }

    private ObjectHandle EnsureDateTimeFormatPrototype()
    {
        if (_dateTimeFormatPrototypeHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var formatMethod = new NativeFunctionObject(
            "format",
            (thisValue, fmtArgs) =>
            {
                if (fmtArgs.Count == 0 || fmtArgs[0].Tag == JsValueTag.Undefined)
                    throw new JsThrownException(CreateTypeError("DateTimeFormat format requires a date argument."));
                var dtCulture = IntlDateTimeFormatting.ResolveCulture("en-US");
                var dtOpts = new IntlDateTimeFormatOptions();
                if (TryGetDateTimeFormatInput(fmtArgs, out var instant))
                {
                    var res = IntlDateTimeFormatting.Format(instant, dtCulture, dtOpts);
                    return JsValue.FromString(res.Text);
                }
                return JsValue.FromString("Invalid Date");
            },
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (thisValue, fmtArgs) =>
            {
                if (fmtArgs.Count == 0 || fmtArgs[0].Tag == JsValueTag.Undefined)
                    throw new JsThrownException(CreateTypeError("DateTimeFormat formatToParts requires a date argument."));
                var dtCulture = IntlDateTimeFormatting.ResolveCulture("en-US");
                var dtOpts = new IntlDateTimeFormatOptions();
                if (TryGetDateTimeFormatInput(fmtArgs, out var instant))
                {
                    var res = IntlDateTimeFormatting.Format(instant, dtCulture, dtOpts);
                    var vals = new List<JsValue>(res.Parts.Count);
                    foreach (var p in res.Parts)
                    {
                        var o = CreateOrdinaryObject();
                        o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(p.Type), Writable: true, Enumerable: true, Configurable: true));
                        o.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(p.Value), Writable: true, Enumerable: true, Configurable: true));
                        vals.Add(JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current())));
                    }
                    return JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(vals), AllocationSite.Current()));
                }
                var empty = JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(Array.Empty<JsValue>()), AllocationSite.Current()));
                return empty;
            },
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatToPartsHandle);

        var resolvedOptsStub = new NativeFunctionObject("resolvedOptions", (_, _) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString("en-US"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("calendar", new JsPropertyDescriptor(JsValue.FromString("gregory"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numberingSystem", new JsPropertyDescriptor(JsValue.FromString("latn"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("timeZone", new JsPropertyDescriptor(JsValue.FromString("UTC"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("year", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("month", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("day", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("hour", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("minute", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("second", new JsPropertyDescriptor(JsValue.FromString("numeric"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var resolvedOptsHandle = _heap.AllocateObject(resolvedOptsStub, AllocationSite.Current());
        _ = prototype.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(resolvedOptsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, resolvedOptsHandle);

        _dateTimeFormatPrototypeHandle = prototypeHandle;
        return prototypeHandle;
    }

    // ECMA-402 11.3.2 Intl.DateTimeFormat.prototype.format(date).
    private JsValue DateTimeFormatPrototypeFormat(
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        IReadOnlyList<JsValue> args)
    {
        if (TryGetTemporalPlainTime(args, culture, options, out var plainTimeResult))
        {
            return JsValue.FromString(plainTimeResult.Text);
        }

        if (!TryGetDateTimeFormatInput(args, out var instant))
            return JsValue.FromString("Invalid Date");

        var result = IntlDateTimeFormatting.Format(instant, culture, options);
        return JsValue.FromString(result.Text);
    }

    private JsValue DateTimeFormatPrototypeFormatToParts(
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        IReadOnlyList<JsValue> args)
    {
        if (TryGetTemporalPlainTime(args, culture, options, out var plainTimeResult))
        {
            var plainPartValues = new List<JsValue>(plainTimeResult.Parts.Count);
            foreach (var part in plainTimeResult.Parts)
            {
                var partObj = CreateOrdinaryObject();
                _ = partObj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(part.Type), Writable: true, Enumerable: true, Configurable: true));
                _ = partObj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(part.Value), Writable: true, Enumerable: true, Configurable: true));
                var partHandle = _heap.AllocateObject(partObj, AllocationSite.Current());
                plainPartValues.Add(JsValue.FromObject(partHandle));
            }

            var plainArray = CreateArrayObject(plainPartValues);
            return JsValue.FromObject(_heap.AllocateObject(plainArray, AllocationSite.Current()));
        }

        if (!TryGetDateTimeFormatInput(args, out var instant))
        {
            var emptyArray = CreateArrayObject(Array.Empty<JsValue>());
            return JsValue.FromObject(_heap.AllocateObject(emptyArray, AllocationSite.Current()));
        }

        var result = IntlDateTimeFormatting.Format(instant, culture, options);
        var partValues = new List<JsValue>(result.Parts.Count);
        foreach (var part in result.Parts)
        {
            var partObj = CreateOrdinaryObject();
            _ = partObj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(part.Type), Writable: true, Enumerable: true, Configurable: true));
            _ = partObj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(part.Value), Writable: true, Enumerable: true, Configurable: true));
            var partHandle = _heap.AllocateObject(partObj, AllocationSite.Current());
            partValues.Add(JsValue.FromObject(partHandle));
        }

        var array = CreateArrayObject(partValues);
        return JsValue.FromObject(_heap.AllocateObject(array, AllocationSite.Current()));
    }

    // ECMA-402 13.1.1 InitializeNumberFormat.
    private JsValue NumberFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var state = ParseNumberFormatState(locale, args.Count > 1 ? args[1] : JsValue.Undefined);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) => JsValue.FromString(FormatNumber(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined, state)),
            length: 1);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("format",
            new JsPropertyDescriptor(JsValue.FromObject(protoMethodHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (_, fmtArgs) =>
            {
                var parts = FormatNumberToParts(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined, state);
                return CreateIntlPartsArray(parts);
            },
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, formatToPartsHandle);

        var resolvedOptionsMethod = new NativeFunctionObject(
            "resolvedOptions",
            (_, _) => NumberFormatResolvedOptions(state),
            length: 0);
        var resolvedOptionsHandle = _heap.AllocateObject(resolvedOptionsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("resolvedOptions",
            new JsPropertyDescriptor(JsValue.FromObject(resolvedOptionsHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, resolvedOptionsHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }

    // ECMA-402 15.5.1 Intl.NumberFormat.prototype.resolvedOptions. Returns a new
    // ordinary object with the resolved configuration in the spec's property
    // order. Defaults follow SetNumberFormatDigitOptions / SetNumberFormatUnitOptions.
    private JsValue NumberFormatResolvedOptions(NumberFormatState state)
    {
        var options = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(options, AllocationSite.Current());
        var rootMark = _heap.RootCount;
        _heap.PushRoot(handle);

        void Put(string name, JsValue value) =>
            options.DefineOwnProperty(name,
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));

        var style = state.Style ?? "decimal";
        var minFraction = state.MinimumFractionDigits ?? 0;
        var maxFraction = state.MaximumFractionDigits ?? Math.Max(minFraction, 3);

        Put("locale", JsValue.FromString(ResolveNumberFormatLocale(state)));
        Put("numberingSystem", JsValue.FromString(state.NumberingSystem));
        Put("style", JsValue.FromString(style));
        if (string.Equals(style, "currency", StringComparison.Ordinal))
        {
            Put("currency", JsValue.FromString(state.Currency ?? "USD"));
            Put("currencyDisplay", JsValue.FromString(state.CurrencyDisplay ?? "symbol"));
        }
        if (string.Equals(style, "unit", StringComparison.Ordinal) && state.Unit is not null)
        {
            Put("unit", JsValue.FromString(state.Unit));
            Put("unitDisplay", JsValue.FromString(state.UnitDisplay ?? "short"));
        }

        Put("minimumIntegerDigits", JsValue.FromNumber(state.MinimumIntegerDigits));
        if (state.MinimumSignificantDigits.HasValue)
        {
            Put("minimumSignificantDigits", JsValue.FromNumber(state.MinimumSignificantDigits.Value));
            Put("maximumSignificantDigits", JsValue.FromNumber(state.MaximumSignificantDigits ?? 21));
        }
        else
        {
            Put("minimumFractionDigits", JsValue.FromNumber(minFraction));
            Put("maximumFractionDigits", JsValue.FromNumber(maxFraction));
        }
        // ES2023 useGrouping resolves to a string/false; the legacy boolean true
        // default surfaces as "auto" (its previous meaning), false stays false.
        Put("useGrouping", state.UseGrouping ? JsValue.FromString("auto") : JsValue.FromBoolean(false));
        Put("notation", JsValue.FromString(state.Notation ?? "standard"));
        Put("signDisplay", JsValue.FromString(state.SignDisplay ?? "auto"));
        Put("roundingIncrement", JsValue.FromNumber(1));
        Put("roundingMode", JsValue.FromString("halfExpand"));
        Put("roundingPriority", JsValue.FromString("auto"));
        Put("trailingZeroDisplay", JsValue.FromString("auto"));

        _heap.PopRootsTo(rootMark);
        return JsValue.FromObject(handle);
    }

    // Resolve the locale reflected by resolvedOptions().locale: the requested
    // locale with its `-u-` extension stripped, plus `-u-nu-<system>` re-appended
    // when a supported numbering system was requested. Empty/unknown falls back
    // to a stable default so tests that read it dynamically stay consistent.
    private static string ResolveNumberFormatLocale(NumberFormatState state)
    {
        var nu = state.NumberingSystem;
        var supported = nu is "arab" or "thai" or "latn";
        return ResolveIntlLocale(state.Locale, supported ? nu : null);
    }

    // Resolve the locale string reflected by an Intl service's resolvedOptions():
    // the requested locale stripped of its `-u-` extension, plus `-u-nu-<system>`
    // re-appended when a supported numbering system was requested via the locale.
    private static string ResolveIntlLocale(string? requested, string? reflectNumberingSystem)
    {
        var raw = requested ?? string.Empty;
        var uIndex = raw.IndexOf("-u-", StringComparison.OrdinalIgnoreCase);
        var baseLocale = uIndex >= 0 ? raw[..uIndex] : raw;
        if (string.IsNullOrEmpty(baseLocale))
        {
            baseLocale = "en-US";
        }

        if (reflectNumberingSystem is not null && uIndex >= 0 &&
            raw.IndexOf("-nu-", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return baseLocale + "-u-nu-" + reflectNumberingSystem;
        }

        return baseLocale;
    }

    // ECMA-402 10.1.1 InitializeCollator.
    private JsValue CollatorConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "compare",
            (_, cmpArgs) =>
            {
                var a = cmpArgs.Count > 0 ? ToStringValue(cmpArgs[0]) : string.Empty;
                var b = cmpArgs.Count > 1 ? ToStringValue(cmpArgs[1]) : string.Empty;
                var result = culture.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.None);
                return JsValue.FromNumber(result);
            },
            length: 2);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("compare",
            new JsPropertyDescriptor(JsValue.FromObject(protoMethodHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

        var resolvedOptsMethod = new NativeFunctionObject("resolvedOptions", (_, _2) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(string.IsNullOrEmpty(locale) ? "en-US" : locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("usage", new JsPropertyDescriptor(JsValue.FromString("sort"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("sensitivity", new JsPropertyDescriptor(JsValue.FromString("variant"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("collation", new JsPropertyDescriptor(JsValue.FromString("default"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numeric", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("caseFirst", new JsPropertyDescriptor(JsValue.FromString("false"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var resolvedOptsHandle = _heap.AllocateObject(resolvedOptsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(resolvedOptsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, resolvedOptsHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }

    private JsValue ListFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var state = ParseListFormatState(locale, args.Count > 1 ? args[1] : JsValue.Undefined);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var formatMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) =>
            {
                var list = GetListFormatItems(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined);
                var parts = FormatListToParts(list, state);
                return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
            },
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (_, fmtArgs) =>
            {
                var list = GetListFormatItems(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined);
                return CreateIntlPartsArray(FormatListToParts(list, state));
            },
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, formatToPartsHandle);

        var lfResOpts = new NativeFunctionObject("resolvedOptions", (_, _2) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(string.IsNullOrEmpty(state.Locale) ? "en-US" : state.Locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(state.Type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(state.Style), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var lfResHandle = _heap.AllocateObject(lfResOpts, AllocationSite.Current());
        prototype.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(lfResHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, lfResHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }

    private bool TryGetDateTimeFormatInput(IReadOnlyList<JsValue> args, out DateTimeOffset instant)
    {
        instant = default;
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            return false;
        }

        if (TryGetTemporalInstant(args[0], out instant))
            return true;

        // Temporal.PlainDate / PlainDateTime → convert to UTC midnight.
        // DecodeIsoDate uses GetV which reads internal _v slots; year=0 means not found.
        if (args[0].Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            var iso = DecodeIsoDate(_heap, obj);
            if (iso.Year != 0)
            {
                instant = new DateTimeOffset(iso.Year, iso.Month, iso.Day, 0, 0, 0, TimeSpan.Zero);
                return true;
            }
            iso = DecodeIsoDateLong(_heap, obj);
            if (iso.Year != 0)
            {
                instant = new DateTimeOffset(iso.Year, iso.Month, iso.Day, 0, 0, 0, TimeSpan.Zero);
                return true;
            }
        }

        try
        {
            var timestamp = DateArgToTimeClip(args[0]);
            if (double.IsNaN(timestamp))
                return false;
            instant = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp);
            return true;
        }
        catch (JsThrownException)
        {
            return false;
        }
    }

    // Extract a millisecond-since-epoch value from various JS date-like types.
    private double DateArgToTimeClip(JsValue arg)
    {
        if (arg.Tag == JsValueTag.Number || arg.Tag == JsValueTag.Int32)
            return arg.Tag == JsValueTag.Int32 ? (double)arg.AsInt32() : arg.AsNumber();

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(arg.AsObjectHandle());
            if (obj is DateObject dateObj)
                return dateObj.TimeValue;
        }

        return ToNumber(arg);
    }

    private bool TryGetTemporalInstant(JsValue arg, out DateTimeOffset instant)
    {
        instant = default;
        if (arg.Tag != JsValueTag.Object)
        {
            return false;
        }

        var obj = _heap.GetObject(arg.AsObjectHandle());
        if (!obj.TryGetProperty("_v", x => _heap.GetObject(x), out var slotsDescriptor) || slotsDescriptor.Value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var slots = _heap.GetObject(slotsDescriptor.Value.AsObjectHandle());
        if (!slots.TryGetProperty("ens", x => _heap.GetObject(x), out var nanosDescriptor))
        {
            return false;
        }

        var nanos = (long)ToNumber(nanosDescriptor.Value);
        instant = new DateTimeOffset(new DateTime(621355968000000000L + (nanos / 100L), DateTimeKind.Utc));
        return true;
    }

    private bool TryGetTemporalPlainTime(
        IReadOnlyList<JsValue> args,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        out IntlDateTimeFormatResult result)
    {
        result = default!;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            return false;
        }

        var obj = _heap.GetObject(args[0].AsObjectHandle());
        if (!obj.TryGetProperty("_v", x => _heap.GetObject(x), out var slotsDescriptor) || slotsDescriptor.Value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var slots = _heap.GetObject(slotsDescriptor.Value.AsObjectHandle());
        if (!slots.TryGetProperty("hour", x => _heap.GetObject(x), out var hourDescriptor))
        {
            return false;
        }

        if (slots.TryGetProperty("y", x => _heap.GetObject(x), out _))
        {
            return false;
        }

        try
        {
            var hour = (int)ToNumber(hourDescriptor.Value);
            var minute = slots.TryGetProperty("minute", x => _heap.GetObject(x), out var minuteDescriptor) ? (int)ToNumber(minuteDescriptor.Value) : 0;
            var second = slots.TryGetProperty("second", x => _heap.GetObject(x), out var secondDescriptor) ? (int)ToNumber(secondDescriptor.Value) : 0;
            var millisecond = slots.TryGetProperty("millisecond", x => _heap.GetObject(x), out var msDescriptor) ? (int)ToNumber(msDescriptor.Value) : 0;
            var microsecond = slots.TryGetProperty("microsecond", x => _heap.GetObject(x), out var microsDescriptor) ? (int)ToNumber(microsDescriptor.Value) : 0;
            var nanosecond = slots.TryGetProperty("nanosecond", x => _heap.GetObject(x), out var nanosDescriptor) ? (int)ToNumber(nanosDescriptor.Value) : 0;
            result = IntlDateTimeFormatting.FormatPlainTime(hour, minute, second, millisecond, microsecond, nanosecond, culture, options);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrownException(CreateTypeError(ex.Message));
        }
    }

    private IntlDateTimeFormatOptions ParseDateTimeFormatOptions(string locale, JsValue optionsValue)
    {
        if (optionsValue.Tag != JsValueTag.Object)
        {
            return new IntlDateTimeFormatOptions(CalendarId: ParseCalendarId(locale));
        }

        var optionsObject = _heap.GetObject(optionsValue.AsObjectHandle());
        string? GetString(string name)
        {
            if (!TryGetPropertyValue(optionsObject, optionsValue, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return ToStringValue(value);
        }

        bool? GetBool(string name)
        {
            if (!TryGetPropertyValue(optionsObject, optionsValue, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return IsTruthy(value);
        }

        int? GetInt(string name)
        {
            if (!TryGetPropertyValue(optionsObject, optionsValue, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return (int)ToNumber(value);
        }

        return new IntlDateTimeFormatOptions(
            CalendarId: ParseCalendarId(locale),
            TimeZoneId: GetString("timeZone"),
            DateStyle: GetString("dateStyle"),
            TimeStyle: GetString("timeStyle"),
            HourCycle: GetString("hourCycle"),
            Hour12: GetBool("hour12"),
            Weekday: GetString("weekday"),
            Era: GetString("era"),
            Year: GetString("year"),
            Month: GetString("month"),
            Day: GetString("day"),
            Hour: GetString("hour"),
            Minute: GetString("minute"),
            Second: GetString("second"),
            FractionalSecondDigits: GetInt("fractionalSecondDigits"),
            DayPeriod: GetString("dayPeriod"),
            TimeZoneName: GetString("timeZoneName"));
    }

    private static string? ParseCalendarId(string locale)
    {
        const string marker = "-u-ca-";
        var index = locale.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = locale.IndexOf('-', start);
        return end >= 0 ? locale[start..end] : locale[start..];
    }

    private JsValue DurationFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var prototypeHandle = EnsureDurationFormatPrototype();
        var state = ParseDurationFormatState(args);
        var stateHandle = _heap.AllocateObject(state, AllocationSite.Current());

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(prototypeHandle);
        _ = instance.DefineOwnProperty(
            "__durationFormatState",
            new JsPropertyDescriptor(
                JsValue.FromObject(stateHandle),
                Writable: false,
                Enumerable: false,
                Configurable: false));

        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, prototypeHandle);
        _heap.WriteBarrier(instanceHandle, stateHandle);
        return JsValue.FromObject(instanceHandle);
    }

    private ObjectHandle EnsureDurationFormatPrototype()
    {
        if (_durationFormatPrototypeHandle is { } existing)
        {
            return existing;
        }

        var prototype = CreateOrdinaryObject();
        var prototypeHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(prototypeHandle);

        var formatMethod = new NativeFunctionObject(
            "format",
            (thisValue, args) => DurationFormatPrototypeFormat(thisValue, args),
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (thisValue, args) => DurationFormatPrototypeFormatToParts(thisValue, args),
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatToPartsHandle);

        var resolvedOptionsMethod = new NativeFunctionObject(
            "resolvedOptions",
            (thisValue, _) => DurationFormatPrototypeResolvedOptions(thisValue),
            length: 0);
        var resolvedOptionsHandle = _heap.AllocateObject(resolvedOptionsMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "resolvedOptions",
            new JsPropertyDescriptor(JsValue.FromObject(resolvedOptionsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, resolvedOptionsHandle);

        DefineBuiltinToStringTag(prototype, "Intl.DurationFormat");

        _durationFormatPrototypeHandle = prototypeHandle;
        return prototypeHandle;
    }

    private JsValue DurationFormatPrototypeFormat(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var state = RequireDurationFormatState(thisValue);
        var duration = ParseDurationLike(args.Count > 0 ? args[0] : JsValue.Undefined);
        var parts = FormatDurationParts(state, duration);
        return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
    }

    private JsValue DurationFormatPrototypeFormatToParts(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var state = RequireDurationFormatState(thisValue);
        var duration = ParseDurationLike(args.Count > 0 ? args[0] : JsValue.Undefined);
        return CreateIntlPartsArray(FormatDurationParts(state, duration));
    }

    private JsValue DurationFormatPrototypeResolvedOptions(JsValue thisValue)
    {
        var state = RequireDurationFormatState(thisValue);
        var result = CreateOrdinaryObject();

        void AddString(string name)
        {
            if (state.TryGetProperty(name, x => _heap.GetObject(x), out var descriptor) &&
                descriptor.Value.Tag != JsValueTag.Undefined)
            {
                _ = result.DefineOwnProperty(name, new JsPropertyDescriptor(descriptor.Value, Writable: true, Enumerable: true, Configurable: true));
            }
        }

        AddString("locale");
        AddString("numberingSystem");
        AddString("style");
        foreach (var unit in DurationUnits)
        {
            AddString(unit);
            AddString(unit + "Display");
        }

        if (state.TryGetProperty("fractionalDigits", x => _heap.GetObject(x), out var fractionalDigits) &&
            fractionalDigits.Value.Tag != JsValueTag.Undefined)
        {
            _ = result.DefineOwnProperty("fractionalDigits", new JsPropertyDescriptor(fractionalDigits.Value, Writable: true, Enumerable: true, Configurable: true));
        }

        return JsValue.FromObject(_heap.AllocateObject(result, AllocationSite.Current()));
    }

    private JsObject RequireDurationFormatState(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Intl.DurationFormat method called on incompatible receiver."));
        }

        var receiver = _heap.GetObject(thisValue.AsObjectHandle());
        if (!receiver.TryGetProperty("__durationFormatState", x => _heap.GetObject(x), out var stateDescriptor) ||
            stateDescriptor.Value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Intl.DurationFormat method called on incompatible receiver."));
        }

        return _heap.GetObject(stateDescriptor.Value.AsObjectHandle());
    }

    private JsObject ParseDurationFormatState(IReadOnlyList<JsValue> args)
    {
        var locale = GetDurationFormatLocale(args.Count > 0 ? args[0] : JsValue.Undefined);
        var optionsValue = args.Count > 1 ? args[1] : JsValue.Undefined;
        var state = CreateOrdinaryObject();

        string numberingSystem = "latn";
        string style = "short";
        string? fractionalDigits = null;
        var unitStyles = new Dictionary<string, string>(StringComparer.Ordinal);
        var unitDisplays = new Dictionary<string, string>(StringComparer.Ordinal);

        if (optionsValue.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));
        }

        JsObject? optionsObject = null;
        var optionsReceiver = JsValue.Undefined;
        if (optionsValue.Tag != JsValueTag.Undefined)
        {
            optionsObject = ToObject(optionsValue);
            optionsReceiver = optionsValue.Tag == JsValueTag.Object
                ? optionsValue
                : JsValue.FromObject(_heap.AllocateObject(optionsObject, AllocationSite.Current()));
        }

        string? GetStringOption(string name)
        {
            if (optionsObject is null)
            {
                return null;
            }

            if (!TryGetPropertyValue(optionsObject, optionsReceiver, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return ToStringValue(value);
        }

        string? localeMatcher = GetStringOption("localeMatcher");
        if (localeMatcher is not null && localeMatcher is not "lookup" and not "best fit")
        {
            throw new JsThrownException(CreateRangeError($"{localeMatcher} is an invalid localeMatcher option value"));
        }

        var requestedNumberingSystem = GetStringOption("numberingSystem");
        if (requestedNumberingSystem is not null && !IsWellFormedNumberingSystem(requestedNumberingSystem))
        {
            // Only a syntactically malformed value is rejected; a well-formed but
            // unsupported value is ignored and resolution falls back to the locale
            // extension or the default (sec-getoption / ResolveLocale).
            throw new JsThrownException(CreateRangeError($"{requestedNumberingSystem} is an invalid numberingSystem option value"));
        }

        // Resolve the effective numbering system: a supported option wins, otherwise a
        // supported locale "nu" extension, otherwise the default. ResolveLocale keeps the
        // "-u-nu-" subtag in the resolved locale only when the locale already carries a
        // supported extension equal to the final numbering system.
        var hasLocaleNu = TryGetUnicodeExtension(locale, "nu", out var unicodeNumberingSystem) &&
                          IsValidDurationNumberingSystem(unicodeNumberingSystem);
        if (requestedNumberingSystem is not null && IsValidDurationNumberingSystem(requestedNumberingSystem))
        {
            numberingSystem = requestedNumberingSystem;
        }
        else if (hasLocaleNu)
        {
            numberingSystem = unicodeNumberingSystem;
        }

        if (!(hasLocaleNu && string.Equals(unicodeNumberingSystem, numberingSystem, StringComparison.Ordinal)))
        {
            locale = RemoveUnicodeNumberingExtension(locale);
        }

        var requestedStyle = GetStringOption("style");
        if (requestedStyle is not null)
        {
            if (requestedStyle is not "long" and not "short" and not "narrow" and not "digital")
            {
                throw new JsThrownException(CreateRangeError($"{requestedStyle} is an invalid style option value"));
            }

            style = requestedStyle;
        }

        string? previousStyle = null;
        foreach (var unit in DurationUnits)
        {
            var requestedUnitStyle = GetStringOption(unit);
            var resolvedStyle = ResolveDurationUnitStyle(unit, style, previousStyle, requestedUnitStyle);
            unitStyles[unit] = resolvedStyle;
            previousStyle = resolvedStyle;

            var displayOption = GetStringOption(unit + "Display");
            if (displayOption is not null && displayOption is not "auto" and not "always")
            {
                throw new JsThrownException(CreateRangeError($"{displayOption} is an invalid {unit}Display option value"));
            }

            // ECMA-402 GetDurationUnitOptions: displayDefault is "always" only when the
            // unit's style was explicitly requested, or — under the "digital" base style —
            // for the hours/minutes/seconds fields. Otherwise it defaults to "auto", so
            // unset zero-valued units (e.g. months in `{ years: 0 }`) are suppressed.
            string displayDefault;
            if (requestedUnitStyle is not null)
            {
                displayDefault = "always";
            }
            else if (style == "digital" && unit is "hours" or "minutes" or "seconds")
            {
                displayDefault = "always";
            }
            else
            {
                displayDefault = "auto";
            }

            unitDisplays[unit] = displayOption ?? displayDefault;
        }

        if (optionsObject is not null &&
            TryGetPropertyValue(optionsObject, optionsReceiver, "fractionalDigits", out var fractionalDigitsValue) &&
            fractionalDigitsValue.Tag != JsValueTag.Undefined)
        {
            var numeric = ToNumber(fractionalDigitsValue);
            if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0 || numeric > 9 || Math.Floor(numeric) != numeric)
            {
                throw new JsThrownException(CreateRangeError($"new Intl.DurationFormat(\"en\", {{fractionalDigits: \"{ToStringValue(fractionalDigitsValue)}\"}}) throws RangeError"));
            }

            fractionalDigits = ((int)numeric).ToString(CultureInfo.InvariantCulture);
        }

        _ = state.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: false, Enumerable: false, Configurable: false));
        _ = state.DefineOwnProperty("numberingSystem", new JsPropertyDescriptor(JsValue.FromString(numberingSystem), Writable: false, Enumerable: false, Configurable: false));
        _ = state.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(style), Writable: false, Enumerable: false, Configurable: false));
        foreach (var unit in DurationUnits)
        {
            _ = state.DefineOwnProperty(unit, new JsPropertyDescriptor(JsValue.FromString(unitStyles[unit]), Writable: false, Enumerable: false, Configurable: false));
            _ = state.DefineOwnProperty(unit + "Display", new JsPropertyDescriptor(JsValue.FromString(unitDisplays[unit]), Writable: false, Enumerable: false, Configurable: false));
        }

        _ = state.DefineOwnProperty(
            "fractionalDigits",
            new JsPropertyDescriptor(
                fractionalDigits is null ? JsValue.Undefined : JsValue.FromNumber(int.Parse(fractionalDigits, CultureInfo.InvariantCulture)),
                Writable: false,
                Enumerable: false,
                Configurable: false));

        return state;
    }

    private string ResolveDurationUnitStyle(string unit, string baseStyle, string? previousStyle, string? requestedStyle)
    {
        string resolved;
        if (requestedStyle is null)
        {
            if (previousStyle is "numeric" or "2-digit")
            {
                resolved = unit is "minutes" or "seconds" ? "2-digit" : "numeric";
            }
            else if (baseStyle == "digital")
            {
                resolved = unit switch
                {
                    "hours" => "numeric",
                    "minutes" => "2-digit",
                    "seconds" => "2-digit",
                    "milliseconds" or "microseconds" or "nanoseconds" => "numeric",
                    _ => "short"
                };
            }
            else
            {
                resolved = baseStyle;
            }
        }
        else
        {
            resolved = requestedStyle;
        }

        var allowsNumeric = unit is "hours" or "minutes" or "seconds" or "milliseconds" or "microseconds" or "nanoseconds";
        var allowsTwoDigit = unit is "hours" or "minutes" or "seconds";
        if (resolved == "numeric" && !allowsNumeric)
        {
            throw new JsThrownException(CreateRangeError($"{resolved} is an invalid {unit} option value"));
        }

        if (resolved == "2-digit" && !allowsTwoDigit)
        {
            throw new JsThrownException(CreateRangeError($"{resolved} is an invalid {unit} option value"));
        }

        if (resolved is not "long" and not "short" and not "narrow" and not "numeric" and not "2-digit")
        {
            throw new JsThrownException(CreateRangeError($"{resolved} is an invalid {unit} option value"));
        }

        if (previousStyle is "numeric" or "2-digit" && resolved is not "numeric" and not "2-digit")
        {
            throw new JsThrownException(CreateRangeError($"{resolved} is an invalid style option value when following a unit with \"{previousStyle}\" style"));
        }

        return resolved;
    }

    private string GetDurationFormatLocale(JsValue localesValue)
    {
        var locales = CanonicalizeLocaleListForDuration(localesValue);
        return locales.Count == 0 ? GetDefaultDurationLocale() : locales[0];
    }

    private JsValue DurationFormatSupportedLocalesOf(IReadOnlyList<JsValue> args)
    {
        var canonicalLocales = args.Count == 0
            ? Array.Empty<JsValue>()
            : CanonicalizeLocaleListForDuration(args[0])
                .Where(IsSupportedDurationLocale)
                .Select(JsValue.FromString)
                .ToArray();
        var arrObj = CreateArrayFromElements(canonicalLocales);
        var arrHandle = _heap.AllocateObject(arrObj, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    private List<string> CanonicalizeLocaleListForDuration(JsValue localesValue)
    {
        var locales = new List<string>();
        if (localesValue.Tag == JsValueTag.Undefined)
        {
            return locales;
        }

        if (localesValue.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));
        }

        if (localesValue.Tag == JsValueTag.String)
        {
            locales.Add(CanonicalizeDurationLocaleTag(ToStringValue(localesValue)));
            return locales;
        }

        if (localesValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("locales argument must be an object or string."));
        }

        var localesObject = _heap.GetObject(localesValue.AsObjectHandle());
        if (!TryGetPropertyValue(localesObject, localesValue, "length", out var lengthValue))
        {
            return locales;
        }

        var lengthNumber = ToNumber(lengthValue);
        if (double.IsNaN(lengthNumber) || lengthNumber < 0)
        {
            lengthNumber = 0;
        }

        var length = (int)Math.Min(lengthNumber, int.MaxValue);
        for (var i = 0; i < length; i++)
        {
            if (!TryGetPropertyValue(localesObject, localesValue, i.ToString(CultureInfo.InvariantCulture), out var element))
            {
                continue;
            }

            if (element.Tag is JsValueTag.Undefined or JsValueTag.Null || element.Tag == JsValueTag.Boolean || element.Tag == JsValueTag.Number || element.Tag == JsValueTag.Int32 || element.Tag == JsValueTag.Symbol)
            {
                throw new JsThrownException(CreateTypeError("Locale list elements must be strings or string-like objects."));
            }

            locales.Add(CanonicalizeDurationLocaleTag(ToStringValue(element)));
        }

        return locales.Distinct(StringComparer.Ordinal).ToList();
    }

    private string CanonicalizeDurationLocaleTag(string tag)
    {
        if (!IsStructurallyValidDurationLocaleTag(tag))
        {
            throw new JsThrownException(CreateRangeError($"Invalid language tag: {tag}"));
        }

        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(parts.Length);
        var inUnicodeExtension = false;
        foreach (var rawPart in parts)
        {
            var part = rawPart;
            if (result.Count == 0)
            {
                result.Add(part.ToLowerInvariant());
                continue;
            }

            if (part.Length == 1)
            {
                inUnicodeExtension = true;
                result.Add(part.ToLowerInvariant());
                continue;
            }

            if (!inUnicodeExtension && part.Length == 4 && part.All(char.IsLetter))
            {
                result.Add(char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());
                continue;
            }

            if (!inUnicodeExtension && (part.Length == 2 && part.All(char.IsLetter) || part.Length == 3 && part.All(char.IsDigit)))
            {
                result.Add(part.ToUpperInvariant());
                continue;
            }

            result.Add(part.ToLowerInvariant());
        }

        return string.Join("-", result);
    }

    private static bool IsSupportedDurationLocale(string locale)
    {
        var primaryLanguage = locale.Split('-', 2)[0];
        return !string.Equals(primaryLanguage, "zxx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructurallyValidDurationLocaleTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Any(ch => ch > 0x7F))
        {
            return false;
        }

        if (tag.Contains('*', StringComparison.Ordinal) || tag.Contains('_', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
        {
            return false;
        }

        if (parts[0].Length == 1 || (parts[0].Length == 4 && parts.Length > 1 && parts[1].Length == 3))
        {
            return false;
        }

        var seenSingletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 1)
            {
                if (!char.IsLetterOrDigit(part[0]) || !seenSingletons.Add(part))
                {
                    return false;
                }

                if (i == parts.Length - 1)
                {
                    return false;
                }

                continue;
            }

            if (!part.All(char.IsLetterOrDigit))
            {
                return false;
            }

            if ((part.Length >= 5 && part.Length <= 8) || (part.Length == 4 && char.IsDigit(part[0])))
            {
                if (!seenVariants.Add(part))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private string GetDefaultDurationLocale()
    {
        var culture = CultureInfo.CurrentCulture;
        if (string.IsNullOrWhiteSpace(culture.Name))
        {
            return "en-US";
        }

        return CanonicalizeDurationLocaleTag(culture.Name.Replace("_", "-", StringComparison.Ordinal));
    }

    private static bool TryGetUnicodeExtension(string locale, string key, out string value)
    {
        value = string.Empty;
        var marker = "-u-";
        var start = locale.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var parts = locale[(start + marker.Length)..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (string.Equals(parts[i], key, StringComparison.OrdinalIgnoreCase))
            {
                value = parts[i + 1].ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    private static string RemoveUnicodeNumberingExtension(string locale)
    {
        if (!TryGetUnicodeExtension(locale, "nu", out _))
        {
            return locale;
        }

        var parts = locale.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 0; i + 2 < parts.Count; i++)
        {
            if (string.Equals(parts[i], "u", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i + 1], "nu", StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveAt(i + 2);
                parts.RemoveAt(i + 1);
                if (i == parts.Count - 1)
                {
                    parts.RemoveAt(i);
                }

                break;
            }
        }

        return string.Join("-", parts);
    }

    private static bool IsValidDurationNumberingSystem(string numberingSystem) =>
        numberingSystem is "latn" or "arab" or "thai";

    // A well-formed Unicode BCP 47 numbering-system "type": one or more 3–8 character
    // alphanumeric subtags joined by "-". Used to distinguish a malformed option value
    // (RangeError) from a well-formed but unsupported one (ignored).
    private static bool IsWellFormedNumberingSystem(string numberingSystem) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            numberingSystem, @"^[a-z0-9]{3,8}(-[a-z0-9]{3,8})*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private NumberFormatState ParseNumberFormatState(string locale, JsValue optionsValue)
    {
        string? style = null;
        string? currency = null;
        string? currencyDisplay = null;
        string? unit = null;
        string? unitDisplay = null;
        string notation = "standard";
        int minimumIntegerDigits = 1;
        int? minimumFractionDigits = null;
        int? maximumFractionDigits = null;
        int? minimumSignificantDigits = null;
        int? maximumSignificantDigits = null;
        bool useGrouping = true;
        string? signDisplay = null;
        // ECMA-402 resolves the numbering system from options.numberingSystem,
        // then the locale's `-u-nu-` Unicode extension, then "latn". The value is
        // always lower-cased (case is insignificant in BCP-47 extensions).
        string? optionsNumberingSystem = null;

        if (optionsValue.Tag == JsValueTag.Object)
        {
            var options = _heap.GetObject(optionsValue.AsObjectHandle());
            string? GetString(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? ToStringValue(value) : null;
            bool? GetBool(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? IsTruthy(value) : null;
            int? GetInt(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? (int)ToNumber(value) : null;

            style = GetString("style");
            notation = GetString("notation") ?? "standard";
            if (style == "currency") { currency = GetString("currency"); currencyDisplay = GetString("currencyDisplay"); }
            if (style == "unit") { unit = GetString("unit"); unitDisplay = GetString("unitDisplay"); }
            minimumIntegerDigits = GetInt("minimumIntegerDigits") ?? 1;
            minimumFractionDigits = GetInt("minimumFractionDigits");
            maximumFractionDigits = GetInt("maximumFractionDigits");
            minimumSignificantDigits = GetInt("minimumSignificantDigits");
            maximumSignificantDigits = GetInt("maximumSignificantDigits");
            useGrouping = GetBool("useGrouping") ?? true;
            signDisplay = GetString("signDisplay");
            optionsNumberingSystem = GetString("numberingSystem");
        }

        var localeNumberingSystem = ExtractUnicodeKeyword(locale, "nu");
        var numberingSystem = (optionsNumberingSystem ?? localeNumberingSystem ?? "latn").ToLowerInvariant();

        return new NumberFormatState(locale, style, currency, currencyDisplay, unit, unitDisplay, notation, minimumIntegerDigits, minimumFractionDigits, maximumFractionDigits, minimumSignificantDigits, maximumSignificantDigits, useGrouping, signDisplay, numberingSystem);
    }

    // Extract a Unicode (`-u-`) extension keyword value from a BCP-47 locale,
    // e.g. ExtractUnicodeKeyword("en-US-u-nu-arab", "nu") -> "arab". Returns null
    // when the locale has no `-u-` singleton or the key is absent. The value is
    // the run of subtags after the key up to the next two-letter key / end.
    private static string? ExtractUnicodeKeyword(string locale, string key)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return null;
        }

        var subtags = locale.Split('-');
        var i = 0;
        // Find the `u` singleton.
        while (i < subtags.Length && !string.Equals(subtags[i], "u", StringComparison.OrdinalIgnoreCase))
        {
            i++;
        }

        if (i >= subtags.Length)
        {
            return null;
        }

        i++; // move past 'u'
        while (i < subtags.Length)
        {
            // A two-character subtag in the extension is a key; longer ones are
            // attributes/type values. Singleton (length 1) ends the extension.
            if (subtags[i].Length == 1)
            {
                break; // next singleton extension begins
            }

            if (subtags[i].Length == 2 && string.Equals(subtags[i], key, StringComparison.OrdinalIgnoreCase))
            {
                var type = new List<string>();
                i++;
                while (i < subtags.Length && subtags[i].Length > 2)
                {
                    type.Add(subtags[i]);
                    i++;
                }

                return type.Count == 0 ? string.Empty : string.Join('-', type).ToLowerInvariant();
            }

            i++;
        }

        return null;
    }

    private static ListFormatState ParseListFormatState(string locale, JsValue optionsValue)
    {
        string type = "conjunction";
        string style = "long";
        if (optionsValue.Tag == JsValueTag.Object)
        {
            // Parsing is intentionally shallow: DurationFormat helper paths only need type/style.
        }

        return new ListFormatState(locale, type, style);
    }

    private string FormatNumber(JsValue value, NumberFormatState state)
    {
        return string.Concat(FormatNumberToParts(value, state).Select(static p => p.Value));
    }

    private static CultureInfo ResolveNumberCulture(string locale)
    {
        if (string.IsNullOrEmpty(locale)) return CultureInfo.GetCultureInfo("en-US");
        // Strip -u- extensions (.NET doesn't parse them).
        var uIdx = locale.IndexOf("-u-", StringComparison.OrdinalIgnoreCase);
        var baseLocale = uIdx >= 0 ? locale[..uIdx] : locale;
        if (string.IsNullOrEmpty(baseLocale)) return CultureInfo.GetCultureInfo("en-US");
        try { return CultureInfo.GetCultureInfo(baseLocale); }
        catch (CultureNotFoundException)
        {
            // Try just the language part.
            var dash = baseLocale.IndexOf('-');
            if (dash > 0)
            {
                try { return CultureInfo.GetCultureInfo(baseLocale[..dash]); }
                catch { return CultureInfo.GetCultureInfo("en-US"); }
            }
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    // Format a number to parts using significant digits (ECMA-402 SetNumberFormatDigitOptions).
    private IReadOnlyList<IntlPart> FormatNumberWithSignificantDigits(double absValue, bool negative,
        int minSig, int maxSig, NumberFormatInfo nfi, NumberFormatState state)
    {
        if (absValue == 0)
        {
            // Zero with significant digits: "0" padded to minSig zeros.
            var parts = new List<IntlPart>();
            if (negative && state.SignDisplay != "never")
                parts.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));
            var zeros = new string('0', Math.Max(1, minSig));
            parts.Add(new IntlPart("integer", ApplyNumberingSystem(zeros, state.NumberingSystem), state.Unit));
            return parts;
        }

        // Compute the exponent and round to maxSig significant digits.
        int exp = (int)Math.Floor(Math.Log10(absValue));
        double scale = Math.Pow(10, maxSig - exp - 1);
        double rounded = Math.Round(absValue * scale) / scale;

        // Format with enough decimal places to capture all significant digits.
        int fracDigits = Math.Max(0, maxSig - exp - 1);
        string formatted = rounded.ToString("F" + fracDigits, CultureInfo.InvariantCulture);

        // Parse into parts.
        var result = new List<IntlPart>();
        bool showSign = state.SignDisplay switch
        {
            "never" => false, "always" => true,"exceptZero" => absValue != 0, "negative" => negative, _ => negative
        };
        if (showSign && negative)
            result.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));

        // Insert grouping separators into integer part.
        int dotIdx = formatted.IndexOf('.');
        if (dotIdx < 0) dotIdx = formatted.Length;
        string intPart = formatted[..dotIdx];
        string fracPart = dotIdx < formatted.Length ? formatted[(dotIdx + 1)..] : "";

        // Grouping for the integer part.
        if (state.UseGrouping && intPart.Length > 3)
        {
            var nfiGroupSizes = nfi.NumberGroupSizes;
            int groupSize = nfiGroupSizes.Length > 0 ? nfiGroupSizes[0] : 3;
            var grouped = new List<string>();
            int pos = intPart.Length;
            while (pos > groupSize) { pos -= groupSize; grouped.Insert(0, intPart[pos..(pos + groupSize)]); }
            grouped.Insert(0, intPart[..pos]);
            for (int i = 0; i < grouped.Count; i++)
            {
                if (i > 0) result.Add(new IntlPart("group", nfi.NumberGroupSeparator, state.Unit));
                result.Add(new IntlPart("integer", ApplyNumberingSystem(grouped[i], state.NumberingSystem), state.Unit));
            }
        }
        else
        {
            result.Add(new IntlPart("integer", ApplyNumberingSystem(intPart, state.NumberingSystem), state.Unit));
        }

        // Fraction part — trim trailing zeros to minSig.
        if (fracPart.Length > 0)
        {
            int keepDigits = Math.Max(fracPart.Length, minSig - intPart.Length);
            while (fracPart.Length > keepDigits && fracPart.EndsWith("0"))
                fracPart = fracPart[..^1];
            if (fracPart.Length > 0)
            {
                result.Add(new IntlPart("decimal", nfi.NumberDecimalSeparator, state.Unit));
                result.Add(new IntlPart("fraction", ApplyNumberingSystem(fracPart, state.NumberingSystem), state.Unit));
            }
        }

        return result;
    }

    private IReadOnlyList<IntlPart> FormatNumberToParts(JsValue value, NumberFormatState state)
    {
        // Extract numeric value.
        double number;
        if (value.Tag == JsValueTag.Int32) number = value.AsInt32();
        else if (value.Tag == JsValueTag.Number) number = value.AsNumber();
        else if (value.Tag == JsValueTag.String && double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) number = n;
        else number = ToNumber(value);
        if (double.IsNaN(number)) return new[] { new IntlPart("nan", "NaN") };
        // ECMA-402: -0 should be treated as +0 for formatting (sign is controlled by signDisplay).
        if (number == 0) number = 0;

        var culture = ResolveNumberCulture(state.Locale);
        if (culture == CultureInfo.InvariantCulture) culture = CultureInfo.GetCultureInfo("en-US");
        var nfi = culture.NumberFormat;
        var style = state.Style ?? "decimal";
        bool negative = number < 0;
        double absValue = Math.Abs(number);

        // Handle significant digits mode.
        bool hasSigDigits = state.MinimumSignificantDigits.HasValue || state.MaximumSignificantDigits.HasValue;
        if (hasSigDigits)
        {
            int minSig = state.MinimumSignificantDigits ?? 1;
            int maxSig = state.MaximumSignificantDigits ?? 21;
            return FormatNumberWithSignificantDigits(absValue, negative, minSig, maxSig, nfi, state);
        }

        // Compute fraction digits (CLDR defaults from NumberFormatInfo when unset).
        int minFrac = state.MinimumFractionDigits ?? (style == "currency" ? nfi.CurrencyDecimalDigits : style == "percent" ? 0 : 0);
        int maxFrac = state.MaximumFractionDigits ?? (style == "currency" ? nfi.CurrencyDecimalDigits : style == "percent" ? Math.Max(minFrac, 0) : 3);
        bool useGrouping = state.UseGrouping;

        // Format using .NET's ICU-backed NumberFormatInfo with fraction digits.
        var cnf = (NumberFormatInfo)nfi.Clone();
        cnf.NumberDecimalDigits = maxFrac;
        cnf.CurrencyDecimalDigits = maxFrac;
        cnf.PercentDecimalDigits = maxFrac;
        if (!useGrouping)
        {
            cnf.NumberGroupSeparator = "";
            cnf.CurrencyGroupSeparator = "";
            cnf.NumberGroupSizes = new int[] { 0 };
            cnf.CurrencyGroupSizes = new int[] { 0 };
        }

        string notation = state.Notation ?? "standard";
        string formatted;
        if (notation == "scientific")
        {
            if (absValue == 0) { formatted = "0E0"; }
            else
            {
                formatted = absValue.ToString("E" + Math.Max(0, maxFrac), CultureInfo.InvariantCulture);
                int eIdx = formatted.IndexOf('E');
                string mant = formatted[..eIdx];
                string exp = formatted[(eIdx + 1)..];
                if (exp.StartsWith("-")) exp = "-" + exp[1..].TrimStart('0');
                else if (exp.StartsWith("+")) exp = exp[1..].TrimStart('0');
                else exp = exp.TrimStart('0');
                if (exp == "" || exp == "-") exp = exp == "-" ? "-0" : "0";
                formatted = mant + "E" + exp;
            }
        }
        else if (notation == "engineering")
        {
            if (absValue == 0) formatted = "0E0";
            else
            {
                int engExp = ((int)Math.Floor(Math.Log10(absValue)) / 3) * 3;
                double mantissa = absValue / Math.Pow(10, engExp);
                if (mantissa >= 1000) { mantissa /= 1000; engExp += 3; }
                if (mantissa < 1) { mantissa *= 1000; engExp -= 3; }
                formatted = mantissa.ToString("F" + maxFrac, CultureInfo.InvariantCulture) + "E" + engExp;
            }
        }
        else
            formatted = style switch
            {
                "currency" => number.ToString("C", cnf),
                "percent" => number.ToString("P", cnf),
                _ => absValue.ToString("N", cnf),
            };
        // Trim trailing zeros in fraction from maxFrac down to minFrac.
        string decSep = cnf.NumberDecimalSeparator;
        int decIdx = formatted.IndexOf(decSep, StringComparison.Ordinal);
        if (decIdx >= 0)
        {
            int fracStart = decIdx + decSep.Length;
            // Find end of digit run (may be followed by currency/non-digit chars).
            int fracEnd = fracStart;
            while (fracEnd < formatted.Length && char.IsDigit(formatted[fracEnd])) fracEnd++;
            // Trim trailing zeros down to max(minFrac, 0).
            int trimTo = Math.Max(minFrac, 0);
            while (fracEnd > fracStart + trimTo && formatted[fracEnd - 1] == '0')
                fracEnd--;
            // Remove fraction and decimal if no fraction digits remain and none required.
            if (fracEnd == fracStart && trimTo == 0)
                formatted = formatted[..decIdx];
            else
                formatted = formatted[..decIdx] + decSep + formatted[fracStart..fracEnd];
        }

        // Parse formatted output into IntlParts.
        var parts = new List<IntlPart>();
        string signDisplay = state.SignDisplay ?? "auto";
        bool showSign = signDisplay switch
        {
            "never" => false, "always" => true,
            "exceptZero" => number != 0, "negative" => negative,
            _ => negative
        };

        // Walk formatted string, classifying each character.
        int pos = 0;
        string curSymbol = nfi.CurrencySymbol;

        // Handle negative pattern: ($1.23) or -1.23 or 1.23-
        bool negParens = negative && formatted.StartsWith("(") && formatted.EndsWith(")");
        if (negParens)
        {
            parts.Add(new IntlPart("literal", "(", state.Unit));
            pos = 1;
        }

        // Skip currency prefix if present.
        if (pos < formatted.Length && formatted.Substring(pos).StartsWith(curSymbol, StringComparison.Ordinal))
        {
            parts.Add(new IntlPart("currency", curSymbol, state.Unit));
            pos += curSymbol.Length;
        }

        while (pos < formatted.Length && (formatted[pos] == ' ' || formatted[pos] == ' '))
        {
            parts.Add(new IntlPart("literal", formatted[pos].ToString(), state.Unit));
            pos++;
        }

        // Sign
        if (negParens || (showSign && negative))
        {
            parts.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));
        }
        else if (showSign && !negative)
        {
            parts.Add(new IntlPart("plusSign", nfi.PositiveSign, state.Unit));
        }

        // Skip past sign character in formatted string if present.
        if (pos < formatted.Length && (formatted[pos] == '-' || formatted[pos] == '+' || formatted[pos] == nfi.NegativeSign[0]))
            pos++;

        // Integer digits with grouping separators.
        bool inFraction = false;
        var digitBuf = new List<char>();
        while (pos < formatted.Length && !(negParens && formatted[pos] == ')'))
        {
            char c = formatted[pos];
            if (char.IsDigit(c))
            {
                digitBuf.Add(c);
                pos++;
            }
            else if (formatted.Substring(pos).StartsWith(decSep, StringComparison.Ordinal))
            {
                if (digitBuf.Count > 0)
                {
                    parts.Add(new IntlPart("integer", ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
                    digitBuf.Clear();
                }
                parts.Add(new IntlPart("decimal", decSep, state.Unit));
                pos += decSep.Length;
                inFraction = true;
            }
            else if (c == nfi.NumberGroupSeparator[0] && nfi.NumberGroupSeparator.Length > 0)
            {
                if (digitBuf.Count > 0)
                {
                    parts.Add(new IntlPart("integer", ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
                    digitBuf.Clear();
                }
                parts.Add(new IntlPart("group", nfi.NumberGroupSeparator, state.Unit));
                pos++;
            }
            else
            {
                parts.Add(new IntlPart("literal", c.ToString(), state.Unit));
                pos++;
            }
        }

        if (digitBuf.Count > 0)
        {
            parts.Add(new IntlPart(inFraction ? "fraction" : "integer",
                ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
        }

        // Closing paren or trailing currency/percent.
        if (negParens)
        {
            parts.Add(new IntlPart("literal", ")", state.Unit));
        }
        // Trailing currency symbol
        if (style == "currency" && !formatted.StartsWith(curSymbol, StringComparison.Ordinal))
        {
            var tail = formatted.Replace("(", "").Replace(")", "");
            if (tail.Contains(curSymbol, StringComparison.Ordinal))
            {
                parts.Add(new IntlPart("literal", " ", state.Unit));
                parts.Add(new IntlPart("currency", curSymbol, state.Unit));
            }
        }

        // Unit suffix.
        if (style == "unit" && !string.IsNullOrEmpty(state.Unit))
        {
            parts.Add(new IntlPart("literal", " ", state.Unit));
            parts.Add(new IntlPart("unit", GetUnitLabel(state.Unit!, state.UnitDisplay ?? "short", state.Locale), state.Unit));
        }

        return parts;
    }

    // Simple numeric formatting used by DurationFormat internal formatting.
    // Does numeric string → parts with unit labels but without full locale support.
    private IReadOnlyList<IntlPart> FormatNumericStringToPartsSimple(string raw, NumberFormatState state)
    {
        var parts = new List<IntlPart>();
        var negative = raw.StartsWith("-", StringComparison.Ordinal);
        if (negative)
        {
            raw = raw[1..];
            if (!string.Equals(state.SignDisplay, "never", StringComparison.Ordinal))
                parts.Add(new IntlPart("minusSign", "-", state.Unit));
        }
        var split = raw.Split('.', 2);
        var integer = split[0].Length == 0 ? "0" : split[0];
        if (state.MinimumIntegerDigits > 1)
            integer = integer.PadLeft(state.MinimumIntegerDigits, '0');
        parts.Add(new IntlPart("integer", ApplyNumberingSystem(integer, state.NumberingSystem), state.Unit));
        if (split.Length == 2)
        {
            var fraction = split[1];
            if (state.MaximumFractionDigits is { } maxFrac)
            {
                if (fraction.Length > maxFrac) fraction = fraction[..maxFrac];
                var minFrac = state.MinimumFractionDigits ?? 0;
                while (fraction.Length > minFrac && fraction.EndsWith("0")) fraction = fraction[..^1];
                if (fraction.Length < minFrac) fraction = fraction.PadRight(minFrac, '0');
            }
            if (fraction.Length > 0)
            {
                parts.Add(new IntlPart("decimal", ".", state.Unit));
                parts.Add(new IntlPart("fraction", ApplyNumberingSystem(fraction, state.NumberingSystem), state.Unit));
            }
        }
        if (state.Style == "unit" && !string.IsNullOrEmpty(state.Unit))
        {
            parts.Add(new IntlPart("literal", " ", state.Unit));
            parts.Add(new IntlPart("unit", GetUnitLabel(state.Unit!, state.UnitDisplay ?? "short", state.Locale), state.Unit));
        }
        return parts;
    }

    private static string ApplyNumberingSystem(string value, string numberingSystem)
    {
        const string latin = "0123456789";
        var digits = numberingSystem switch
        {
            "arab" => "٠١٢٣٤٥٦٧٨٩",
            "thai" => "๐๑๒๓๔๕๖๗๘๙",
            _ => latin
        };

        if (digits == latin)
        {
            return value;
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var index = latin.IndexOf(chars[i]);
            if (index >= 0)
            {
                chars[i] = digits[index];
            }
        }

        return new string(chars);
    }

    private static string GetUnitLabel(string unit, string style, string locale)
    {
        var spanish = locale.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        return (unit, style, spanish) switch
        {
            ("year", "long", true) => "año",
            ("month", "long", true) => "mes",
            ("week", "long", true) => "semana",
            ("day", "long", true) => "día",
            ("hour", "long", true) => "hora",
            ("minute", "long", true) => "minuto",
            ("second", "long", true) => "segundo",
            ("millisecond", "long", true) => "milisegundo",
            ("microsecond", "long", true) => "microsegundo",
            ("nanosecond", "long", true) => "nanosegundo",
            ("year", "narrow", _) => "y",
            ("month", "narrow", _) => "m",
            ("week", "narrow", _) => "w",
            ("day", "narrow", _) => "d",
            ("hour", "narrow", _) => "h",
            ("minute", "narrow", _) => "m",
            ("second", "narrow", _) => "s",
            ("millisecond", "narrow", _) => "ms",
            ("microsecond", "narrow", _) => "μs",
            ("nanosecond", "narrow", _) => "ns",
            ("year", _, _) => "yr",
            ("month", _, _) => "mth",
            ("week", _, _) => "wk",
            ("day", _, _) => "day",
            ("hour", _, _) => "hr",
            ("minute", _, _) => "min",
            ("second", _, _) => "sec",
            ("millisecond", _, _) => "ms",
            ("microsecond", _, _) => "μs",
            ("nanosecond", _, _) => "ns",
            _ => unit
        };
    }

    private List<string> GetListFormatItems(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return new List<string>();
        }

        var obj = _heap.GetObject(value.AsObjectHandle());
        var length = GetArrayLength(obj);
        var result = new List<string>(length);
        for (var i = 0; i < length; i++)
        {
            if (TryGetPropertyValue(obj, value, i.ToString(CultureInfo.InvariantCulture), out var element))
            {
                result.Add(ToStringValue(element));
            }
        }

        return result;
    }

    private IReadOnlyList<IntlPart> FormatListToParts(IReadOnlyList<string> items, ListFormatState state)
    {
        _ = state;
        var parts = new List<IntlPart>();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(new IntlPart("literal", ", "));
            }

            parts.Add(new IntlPart("element", items[i]));
        }

        return parts;
    }

    private sealed record DurationRecord(double Years, double Months, double Weeks, double Days, double Hours, double Minutes, double Seconds, double Milliseconds, double Microseconds, double Nanoseconds)
    {
        public double this[string unit] => unit switch
        {
            "years" => Years,
            "months" => Months,
            "weeks" => Weeks,
            "days" => Days,
            "hours" => Hours,
            "minutes" => Minutes,
            "seconds" => Seconds,
            "milliseconds" => Milliseconds,
            "microseconds" => Microseconds,
            "nanoseconds" => Nanoseconds,
            _ => 0
        };
    }

    // Reads the duration components stored on a Temporal.Duration's internal data
    // holder ("_v"), identified by the presence of the calendar/time unit fields.
    // Returns false for any other object so the ordinary property-reading path runs.
    private bool TryReadTemporalDurationSlots(JsObject obj, out DurationRecord record)
    {
        record = null!;
        if (!obj.TryGetOwnProperty("_v", out var holder) || holder.Value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var data = _heap.GetObject(holder.Value.AsObjectHandle());
        if (!data.TryGetOwnProperty("years", out _) || !data.TryGetOwnProperty("nanoseconds", out _))
        {
            return false;
        }

        double Slot(string unit) =>
            data.TryGetOwnProperty(unit, out var d) ? NumericSlotValue(d.Value) : 0;

        record = new DurationRecord(
            Slot("years"), Slot("months"), Slot("weeks"), Slot("days"), Slot("hours"),
            Slot("minutes"), Slot("seconds"), Slot("milliseconds"), Slot("microseconds"), Slot("nanoseconds"));
        return true;
    }

    private static double NumericSlotValue(JsValue value) => value.Tag switch
    {
        JsValueTag.Int32 => value.AsInt32(),
        JsValueTag.Number => value.AsNumber(),
        _ => 0
    };

    // Parses an ISO 8601 / Temporal duration string (e.g. "P1Y2M3W4DT5H6M7.008S").
    // A fractional part is supported on the seconds field, split into milli/micro/nano.
    private bool TryParseIsoDuration(string input, out DurationRecord record)
    {
        record = null!;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            input,
            @"^([+-])?P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)(?:[.,](\d{1,9}))?S)?)?$");
        if (!match.Success)
        {
            return false;
        }

        // Reject "P" / "PT" with no components at all.
        var hasAny = false;
        for (var g = 2; g <= 9; g++)
        {
            if (match.Groups[g].Success) { hasAny = true; break; }
        }

        if (!hasAny)
        {
            return false;
        }

        double G(int i) => match.Groups[i].Success ? double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var signFactor = match.Groups[1].Value == "-" ? -1d : 1d;

        double milliseconds = 0, microseconds = 0, nanoseconds = 0;
        if (match.Groups[9].Success)
        {
            var frac = match.Groups[9].Value.PadRight(9, '0');
            milliseconds = double.Parse(frac[..3], CultureInfo.InvariantCulture);
            microseconds = double.Parse(frac[3..6], CultureInfo.InvariantCulture);
            nanoseconds = double.Parse(frac[6..9], CultureInfo.InvariantCulture);
        }

        record = new DurationRecord(
            signFactor * G(2), signFactor * G(3), signFactor * G(4), signFactor * G(5),
            signFactor * G(6), signFactor * G(7), signFactor * G(8),
            signFactor * milliseconds, signFactor * microseconds, signFactor * nanoseconds);
        return true;
    }

    private DurationRecord ParseDurationLike(JsValue value)
    {
        if (value.Tag == JsValueTag.String)
        {
            if (TryParseIsoDuration(value.AsString(), out var parsed))
            {
                return parsed;
            }

            throw new JsThrownException(CreateRangeError("Invalid duration string."));
        }

        if (value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Duration value must be an object."));
        }

        var obj = _heap.GetObject(value.AsObjectHandle());

        // sec-todurationrecord: a Temporal.Duration is read from its internal slots
        // directly, bypassing the prototype getters (which a caller may have tainted).
        if (TryReadTemporalDurationSlots(obj, out var slotRecord))
        {
            return slotRecord;
        }

        var allowed = new HashSet<string>(DurationUnits, StringComparer.Ordinal);
        // Per sec-todurationrecord, a duration property that is absent or explicitly
        // `undefined` is simply skipped; the TypeError is only thrown when *every*
        // recognized property is absent. A present property whose value is 0 (or -0)
        // counts as defined, so all-zero durations such as `{ years: 0 }` are valid.
        var anyDefined = false;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        int sign = 0;

        foreach (var unit in DurationUnits)
        {
            if (!TryGetPropertyValue(obj, value, unit, out var property) || property.Tag == JsValueTag.Undefined)
            {
                values[unit] = 0;
                continue;
            }

            anyDefined = true;

            if (property.Tag == JsValueTag.BigInt)
            {
                throw new JsThrownException(CreateTypeError("Cannot convert a BigInt value to a number."));
            }

            // ToIntegerIfIntegral: the value must already be an integral Number.
            var numeric = ToNumber(property);
            if (double.IsNaN(numeric) || double.IsInfinity(numeric) || Math.Floor(numeric) != numeric)
            {
                throw new JsThrownException(CreateRangeError("Duration property must be a finite integer."));
            }

            // Normalize -0 to +0 so the sign check below treats it as zero.
            if (numeric == 0)
            {
                numeric = 0;
            }

            values[unit] = numeric;
            if (numeric != 0)
            {
                var currentSign = Math.Sign(numeric);
                if (sign != 0 && currentSign != sign)
                {
                    throw new JsThrownException(CreateRangeError("Mixed-sign durations are not supported."));
                }

                sign = currentSign;
            }
        }

        foreach (var entry in obj.EnumerateOwnProperties())
        {
            if (entry.Key == "_v")
            {
                continue;
            }

            if (!allowed.Contains(entry.Key))
            {
                throw new JsThrownException(CreateTypeError("Unsupported duration property."));
            }
        }

        if (!anyDefined)
        {
            throw new JsThrownException(CreateTypeError("Duration record must define at least one supported property."));
        }

        // sec-isvalidduration: the calendar units must each be < 2^32 in magnitude and the
        // exact normalized-seconds total must be < 2^53. The total is evaluated on exact
        // mathematical values (BigInteger) so that boundary cases near 2^53 — and inputs
        // beyond 2^53 such as `milliseconds: 4503599627370497000` — classify correctly.
        const double pow32 = 4294967296d; // 2^32
        if (Math.Abs(values["years"]) >= pow32 ||
            Math.Abs(values["months"]) >= pow32 ||
            Math.Abs(values["weeks"]) >= pow32)
        {
            throw new JsThrownException(CreateRangeError("Duration calendar unit out of range."));
        }

        var totalNanoseconds =
            ToBigIntegerExact(values["days"]) * 86_400_000_000_000 +
            ToBigIntegerExact(values["hours"]) * 3_600_000_000_000 +
            ToBigIntegerExact(values["minutes"]) * 60_000_000_000 +
            ToBigIntegerExact(values["seconds"]) * 1_000_000_000 +
            ToBigIntegerExact(values["milliseconds"]) * 1_000_000 +
            ToBigIntegerExact(values["microseconds"]) * 1_000 +
            ToBigIntegerExact(values["nanoseconds"]);
        // 2^53 seconds expressed in nanoseconds.
        var maxNanoseconds = BigInteger.Pow(2, 53) * 1_000_000_000;
        if (BigInteger.Abs(totalNanoseconds) >= maxNanoseconds)
        {
            throw new JsThrownException(CreateRangeError("Duration time units out of range."));
        }

        return new DurationRecord(
            values["years"], values["months"], values["weeks"], values["days"], values["hours"],
            values["minutes"], values["seconds"], values["milliseconds"], values["microseconds"], values["nanoseconds"]);
    }

    private IReadOnlyList<IntlPart> FormatDurationParts(JsObject state, DurationRecord duration)
    {
        var style = GetDurationStateString(state, "style");
        var numberingSystem = GetDurationStateString(state, "numberingSystem");
        var locale = GetDurationStateString(state, "locale");
        var flattened = new List<IntlPart>();
        var groups = new List<List<IntlPart>>();
        var needSeparator = false;
        var signDisplayed = false;
        var overallNegative = DurationUnits.Any(unit => duration[unit] < 0);

        foreach (var unit in DurationUnits)
        {
            var value = duration[unit];
            var unitStyle = GetDurationStateString(state, unit);
            var unitDisplay = GetDurationStateString(state, unit + "Display");
            var displayRequired = unit == "minutes" && needSeparator &&
                                  (GetDurationStateString(state, "secondsDisplay") == "always" ||
                                   duration.Seconds != 0 || duration.Milliseconds != 0 || duration.Microseconds != 0 || duration.Nanoseconds != 0);

            string raw;
            var done = false;
            if ((unit == "seconds" || unit == "milliseconds" || unit == "microseconds") &&
                NextDurationUnitIsNumeric(state, unit))
            {
                raw = ComposeFractionalDurationValue(duration, unit, GetOptionalDurationFractionalDigits(state));
                done = true;
            }
            else
            {
                // "F0" renders an integral double in full decimal form without exponent.
                raw = Math.Abs(value).ToString("F0", CultureInfo.InvariantCulture);
            }

            // The display gate tests the *composed* value before fractionalDigits rounding:
            // when a unit absorbs numeric sub-units (e.g. seconds carrying nonzero
            // milliseconds), its effective value is nonzero even if its own integer component
            // is 0 and the rendered fraction rounds away, so it must still be emitted.
            var valueIsZero = done
                ? unit switch
                {
                    "seconds" => duration.Seconds == 0 && duration.Milliseconds == 0 && duration.Microseconds == 0 && duration.Nanoseconds == 0,
                    "milliseconds" => duration.Milliseconds == 0 && duration.Microseconds == 0 && duration.Nanoseconds == 0,
                    _ => duration.Microseconds == 0 && duration.Nanoseconds == 0,
                }
                : value == 0;
            if (!valueIsZero || unitDisplay != "auto" || displayRequired)
            {
                var suppressSign = signDisplayed;
                if (!signDisplayed && overallNegative)
                {
                    raw = "-" + raw;
                    signDisplayed = true;
                }

                var numberState = new NumberFormatState(
                    locale,
                    unitStyle is "numeric" or "2-digit" ? null : "unit",
                    null, null,
                    SingularDurationUnit(unit),
                    unitStyle is "numeric" or "2-digit" ? null : unitStyle,
                    "standard",
                    unitStyle == "2-digit" ? 2 : 1,
                    null, null, null, null,
                    unitStyle is "numeric" or "2-digit" ? false : true,
                    suppressSign ? "never" : null,
                    numberingSystem);

                var parts = FormatNumericStringToPartsSimple(raw, numberState).ToList();

                if (needSeparator)
                {
                    groups[^1].Add(new IntlPart("literal", ":"));
                    groups[^1].AddRange(parts.Where(p => p.Type != "unit"));
                }
                else
                {
                    groups.Add(parts);
                }

                if (!needSeparator && (unitStyle == "numeric" || unitStyle == "2-digit"))
                {
                    needSeparator = true;
                }
            }

            if (done)
            {
                break;
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                flattened.Add(new IntlPart("literal", ", "));
            }

            flattened.AddRange(groups[i]);
        }

        return flattened;
    }

    private static string SingularDurationUnit(string unit) => unit.EndsWith("s", StringComparison.Ordinal) ? unit[..^1] : unit;

    private static bool NextDurationUnitIsNumeric(JsObject state, string unit)
    {
        var nextUnit = unit switch
        {
            "seconds" => "milliseconds",
            "milliseconds" => "microseconds",
            "microseconds" => "nanoseconds",
            _ => null
        };

        if (nextUnit is null)
        {
            return false;
        }

        return state.TryGetProperty(nextUnit, x => null!, out var descriptor) &&
               descriptor.Value.Tag == JsValueTag.String &&
               descriptor.Value.AsString() == "numeric";
    }

    private static int? GetOptionalDurationFractionalDigits(JsObject state)
    {
        return state.TryGetProperty("fractionalDigits", x => null!, out var descriptor) && descriptor.Value.Tag != JsValueTag.Undefined
            ? (int)descriptor.Value.AsNumber()
            : null;
    }

    private static string GetDurationStateString(JsObject state, string key)
    {
        return state.TryGetProperty(key, x => null!, out var descriptor) && descriptor.Value.Tag == JsValueTag.String
            ? descriptor.Value.AsString()
            : string.Empty;
    }

    // Converts an integral double to its exact BigInteger value (the double is already
    // validated as integral by ToIntegerIfIntegral, so this loses no precision).
    private static BigInteger ToBigIntegerExact(double value) => new BigInteger(value);

    private static string ComposeFractionalDurationValue(DurationRecord duration, string unit, int? fractionalDigits)
    {
        // Compose the fractional value on exact mathematical values (PartitionDurationFormatPattern):
        // sum the sub-second contributions in nanosecond-resolution integers so that inputs beyond
        // 2^53 nanoseconds keep full precision. fracLen is the number of fractional digits the unit
        // carries (seconds → 9, milliseconds → 6, microseconds → 3).
        static BigInteger Abs(double v) => BigInteger.Abs(new BigInteger(v));

        BigInteger total;
        int fracLen;
        switch (unit)
        {
            case "seconds":
                total = Abs(duration.Seconds) * 1_000_000_000
                        + Abs(duration.Milliseconds) * 1_000_000
                        + Abs(duration.Microseconds) * 1_000
                        + Abs(duration.Nanoseconds);
                fracLen = 9;
                break;
            case "milliseconds":
                total = Abs(duration.Milliseconds) * 1_000_000
                        + Abs(duration.Microseconds) * 1_000
                        + Abs(duration.Nanoseconds);
                fracLen = 6;
                break;
            default:
                total = Abs(duration.Microseconds) * 1_000
                        + Abs(duration.Nanoseconds);
                fracLen = 3;
                break;
        }

        var denom = BigInteger.Pow(10, fracLen);
        var whole = total / denom;
        var fraction = (total % denom).ToString(CultureInfo.InvariantCulture).PadLeft(fracLen, '0');

        if (fractionalDigits is null)
        {
            fraction = fraction.TrimEnd('0');
        }
        else
        {
            fraction = fractionalDigits.Value == 0
                ? string.Empty
                : fraction.PadRight(fractionalDigits.Value, '0')[..fractionalDigits.Value];
        }

        return fraction.Length == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : $"{whole.ToString(CultureInfo.InvariantCulture)}.{fraction}";
    }

    private JsValue CreateIntlPartsArray(IReadOnlyList<IntlPart> parts)
    {
        var values = new List<JsValue>(parts.Count);
        foreach (var part in parts)
        {
            var obj = CreateOrdinaryObject();
            _ = obj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(part.Type), Writable: true, Enumerable: true, Configurable: true));
            _ = obj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(part.Value), Writable: true, Enumerable: true, Configurable: true));
            if (part.Unit is not null)
            {
                _ = obj.DefineOwnProperty("unit", new JsPropertyDescriptor(JsValue.FromString(part.Unit), Writable: true, Enumerable: true, Configurable: true));
            }
            var handle = _heap.AllocateObject(obj, AllocationSite.Current());
            values.Add(JsValue.FromObject(handle));
        }

        var array = CreateArrayObject(values);
        return JsValue.FromObject(_heap.AllocateObject(array, AllocationSite.Current()));
    }

    private JsValue CreateIntlPartsArray(IReadOnlyList<IntlDateTimePart> parts)
    {
        var values = new List<JsValue>(parts.Count);
        foreach (var part in parts)
        {
            var obj = CreateOrdinaryObject();
            _ = obj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(part.Type), Writable: true, Enumerable: true, Configurable: true));
            _ = obj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(part.Value), Writable: true, Enumerable: true, Configurable: true));
            var handle = _heap.AllocateObject(obj, AllocationSite.Current());
            values.Add(JsValue.FromObject(handle));
        }

        var array = CreateArrayObject(values);
        return JsValue.FromObject(_heap.AllocateObject(array, AllocationSite.Current()));
    }
}
