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
        string? Unit,
        string? UnitDisplay,
        int MinimumIntegerDigits,
        int? MinimumFractionDigits,
        int? MaximumFractionDigits,
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

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);

        return JsValue.FromObject(instanceHandle);
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
            (_, _) => JsValue.FromString("Invalid Date"),
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (_, _) =>
            {
                var emptyArray = CreateArrayObject(Array.Empty<JsValue>());
                return JsValue.FromObject(_heap.AllocateObject(emptyArray, AllocationSite.Current()));
            },
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatToPartsHandle);

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

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
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
        {
            return true;
        }

        var timestamp = DateArgToTimeClip(args[0]);
        if (double.IsNaN(timestamp))
        {
            return false;
        }

        instant = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp);
        return true;
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
        if (requestedNumberingSystem is not null)
        {
            if (!IsValidDurationNumberingSystem(requestedNumberingSystem))
            {
                throw new JsThrownException(CreateRangeError($"{requestedNumberingSystem} is an invalid numberingSystem option value"));
            }

            numberingSystem = requestedNumberingSystem;
            locale = RemoveUnicodeNumberingExtension(locale);
        }
        else if (TryGetUnicodeExtension(locale, "nu", out var unicodeNumberingSystem) &&
                 IsValidDurationNumberingSystem(unicodeNumberingSystem))
        {
            numberingSystem = unicodeNumberingSystem;
        }
        else
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

    private NumberFormatState ParseNumberFormatState(string locale, JsValue optionsValue)
    {
        string? style = null;
        string? unit = null;
        string? unitDisplay = null;
        int minimumIntegerDigits = 1;
        int? minimumFractionDigits = null;
        int? maximumFractionDigits = null;
        bool useGrouping = true;
        string? signDisplay = null;
        string numberingSystem = "latn";

        if (optionsValue.Tag == JsValueTag.Object)
        {
            var options = _heap.GetObject(optionsValue.AsObjectHandle());
            string? GetString(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? ToStringValue(value) : null;
            bool? GetBool(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? IsTruthy(value) : null;
            int? GetInt(string name) => TryGetPropertyValue(options, optionsValue, name, out var value) && value.Tag != JsValueTag.Undefined ? (int)ToNumber(value) : null;

            style = GetString("style");
            unit = GetString("unit");
            unitDisplay = GetString("unitDisplay");
            minimumIntegerDigits = GetInt("minimumIntegerDigits") ?? 1;
            minimumFractionDigits = GetInt("minimumFractionDigits");
            maximumFractionDigits = GetInt("maximumFractionDigits");
            useGrouping = GetBool("useGrouping") ?? true;
            signDisplay = GetString("signDisplay");
            numberingSystem = GetString("numberingSystem") ?? "latn";
        }

        return new NumberFormatState(locale, style, unit, unitDisplay, minimumIntegerDigits, minimumFractionDigits, maximumFractionDigits, useGrouping, signDisplay, numberingSystem);
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

    private IReadOnlyList<IntlPart> FormatNumberToParts(JsValue value, NumberFormatState state)
    {
        string raw;
        if (value.Tag == JsValueTag.String)
        {
            raw = value.AsString();
        }
        else if (value.Tag == JsValueTag.Int32)
        {
            raw = value.AsInt32().ToString(CultureInfo.InvariantCulture);
        }
        else if (value.Tag == JsValueTag.Number)
        {
            var number = value.AsNumber();
            if (double.IsNaN(number))
            {
                return new[] { new IntlPart("nan", "NaN") };
            }

            raw = number.ToString("0.############################", CultureInfo.InvariantCulture);
        }
        else
        {
            raw = ToStringValue(value);
        }

        return FormatNumericStringToParts(raw, state);
    }

    private IReadOnlyList<IntlPart> FormatNumericStringToParts(string raw, NumberFormatState state)
    {
        var parts = new List<IntlPart>();
        var negative = raw.StartsWith("-", StringComparison.Ordinal);
        if (negative)
        {
            raw = raw[1..];
            if (!string.Equals(state.SignDisplay, "never", StringComparison.Ordinal))
            {
                parts.Add(new IntlPart("minusSign", "-", state.Unit));
            }
        }

        var split = raw.Split('.', 2);
        var integer = split[0].Length == 0 ? "0" : split[0];
        if (state.MinimumIntegerDigits > 1)
        {
            integer = integer.PadLeft(state.MinimumIntegerDigits, '0');
        }

        parts.Add(new IntlPart("integer", ApplyNumberingSystem(integer, state.NumberingSystem), state.Unit));
        if (split.Length == 2)
        {
            var fraction = split[1];
            if (state.MaximumFractionDigits is { } maxFrac)
            {
                if (fraction.Length > maxFrac)
                {
                    fraction = fraction[..maxFrac];
                }

                // Trailing zeros are removed down to minimumFractionDigits, then the
                // result is padded back up to that minimum (ToRawFixed / SetNumberFormatDigitOptions).
                var minFrac = state.MinimumFractionDigits ?? 0;
                while (fraction.Length > minFrac && fraction.EndsWith("0", StringComparison.Ordinal))
                {
                    fraction = fraction[..^1];
                }

                if (fraction.Length < minFrac)
                {
                    fraction = fraction.PadRight(minFrac, '0');
                }
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

    private DurationRecord ParseDurationLike(JsValue value)
    {
        if (value.Tag == JsValueTag.String)
        {
            throw new JsThrownException(CreateRangeError("Invalid duration string."));
        }

        if (value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Duration value must be an object."));
        }

        var obj = _heap.GetObject(value.AsObjectHandle());
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

            // The display gate tests the *composed* value: when a unit absorbs numeric
            // sub-units (e.g. seconds carrying nonzero milliseconds), its effective value is
            // nonzero even if its own integer component is 0, so it must still be emitted.
            var valueIsZero = done ? raw == "0" : value == 0;
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
                    SingularDurationUnit(unit),
                    unitStyle is "numeric" or "2-digit" ? null : unitStyle,
                    unitStyle == "2-digit" ? 2 : 1,
                    null,
                    null,
                    unitStyle is "numeric" or "2-digit" ? false : true,
                    suppressSign ? "never" : null,
                    numberingSystem);

                var parts = FormatNumericStringToParts(raw, numberState).ToList();

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
