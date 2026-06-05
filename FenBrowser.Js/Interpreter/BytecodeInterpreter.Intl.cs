using FenBrowser.Js.Heap;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using System.Globalization;
using System.Linq;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 Internationalization API — partial class with Intl-specific methods.
// Also carries EnsureProxyConstructor() placeholder until full Proxy lands.
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle? _durationFormatConstructorHandle;
    private ObjectHandle? _durationFormatPrototypeHandle;
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

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) =>
            {
                var num = fmtArgs.Count > 0 ? fmtArgs[0].AsNumber() : double.NaN;
                if (double.IsNaN(num))
                    return JsValue.FromString("NaN");
                try { return JsValue.FromString(num.ToString("N", culture)); }
                catch { return JsValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            },
            length: 1);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("format",
            new JsPropertyDescriptor(JsValue.FromObject(protoMethodHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

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
        _ = args;
        var state = RequireDurationFormatState(thisValue);
        _ = state;
        return JsValue.FromString("[DurationFormat]");
    }

    private JsValue DurationFormatPrototypeFormatToParts(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var state = RequireDurationFormatState(thisValue);
        _ = state;
        var emptyArray = CreateArrayObject(Array.Empty<JsValue>());
        return JsValue.FromObject(_heap.AllocateObject(emptyArray, AllocationSite.Current()));
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

        foreach (var unit in DurationUnits)
        {
            unitStyles[unit] = "short";
            unitDisplays[unit] = "always";
        }

        if (optionsValue.Tag == JsValueTag.Object)
        {
            var optionsObject = _heap.GetObject(optionsValue.AsObjectHandle());

            string? GetStringOption(string name)
            {
                if (!TryGetPropertyValue(optionsObject, optionsValue, name, out var value) || value.Tag == JsValueTag.Undefined)
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

                unitDisplays[unit] = displayOption ?? "always";
            }

            if (TryGetPropertyValue(optionsObject, optionsValue, "fractionalDigits", out var fractionalDigitsValue) &&
                fractionalDigitsValue.Tag != JsValueTag.Undefined)
            {
                var numeric = ToNumber(fractionalDigitsValue);
                if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0 || numeric > 9 || Math.Floor(numeric) != numeric)
                {
                    throw new JsThrownException(CreateRangeError($"new Intl.DurationFormat(\"en\", {{fractionalDigits: \"{ToStringValue(fractionalDigitsValue)}\"}}) throws RangeError"));
                }

                fractionalDigits = ((int)numeric).ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (TryGetUnicodeExtension(locale, "nu", out var localeNumberingSystem) &&
                 IsValidDurationNumberingSystem(localeNumberingSystem))
        {
            numberingSystem = localeNumberingSystem;
        }
        else
        {
            locale = RemoveUnicodeNumberingExtension(locale);
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
                resolved = "short";
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
}
