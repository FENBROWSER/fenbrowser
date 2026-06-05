using FenBrowser.Js.Heap;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using System.Globalization;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 Internationalization API — partial class with Intl-specific methods.
// Also carries EnsureProxyConstructor() placeholder until full Proxy lands.
public sealed partial class BytecodeInterpreter
{
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
}
