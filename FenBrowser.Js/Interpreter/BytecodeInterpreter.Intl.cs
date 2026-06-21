using FenBrowser.Js.Builtins;
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
    // Helper: wrap an Intl prototype method as an accessor property per ECMA-402.
    // Creates a getter that returns the implementation function on each access.
    private void DefineIntlAccessor(ObjectHandle protoHandle, JsObject proto, string name, NativeFunctionObject impl)
    {
        impl.SetPrototype(EnsureFunctionPrototype());
        var implHandle = _heap.AllocateObject(impl, AllocationSite.Current());
        _heap.WriteBarrier(implHandle, EnsureFunctionPrototype());
        var capturedImpl = implHandle;
        var getter = new NativeFunctionObject("get " + name, (_, _2) =>
            JsValue.FromObject(capturedImpl), length: 0);
        getter.SetPrototype(EnsureFunctionPrototype());
        var getterHandle = _heap.AllocateObject(getter, AllocationSite.Current());
        _heap.WriteBarrier(getterHandle, EnsureFunctionPrototype());
        _ = proto.DefineOwnProperty(
            name,
            JsPropertyDescriptor.Accessor(
                JsValue.FromObject(getterHandle),
                JsValue.Undefined,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, getterHandle);
        _heap.WriteBarrier(protoHandle, implHandle);
    }
    private ObjectHandle? _dateTimeFormatPrototypeHandle;
    private ObjectHandle? _durationFormatConstructorHandle;
    private ObjectHandle? _durationFormatPrototypeHandle;
    private ObjectHandle? _collatorPrototypeHandle;
    private ObjectHandle? _segmenterPrototypeHandle;
    private ObjectHandle? _pluralRulesPrototypeHandle;
    private ObjectHandle? _displayNamesPrototypeHandle;
    private ObjectHandle? _listFormatPrototypeHandle;
    private ObjectHandle? _numberFormatPrototypeHandle;
    private JsValue _intlFallbackSymbol = JsValue.Undefined; // %Intl%.[[FallbackSymbol]]
    private sealed record IntlPart(string Type, string Value, string? Unit = null);
    private sealed record NumberFormatState(
        string Locale,
        string? Style,
        string? Currency,
        string? CurrencyDisplay,
        string? CurrencySign,
        string? Unit,
        string? UnitDisplay,
        string? Notation,
        string? CompactDisplay,
        int MinimumIntegerDigits,
        int? MinimumFractionDigits,
        int? MaximumFractionDigits,
        int? MinimumSignificantDigits,
        int? MaximumSignificantDigits,
        bool UseGrouping,
        string UseGroupingValue,
        string? SignDisplay,
        string? RoundingMode,
        int? RoundingIncrement,
        string? RoundingPriority,
        string? TrailingZeroDisplay,
        string NumberingSystem);
    private sealed record ListFormatState(string Locale, string Type, string Style);
    private static readonly string[] DurationUnits =
    {
        "years", "months", "weeks", "days", "hours",
        "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds"
    };

    // ECMA-402: ChainNumberFormat / ChainDateTimeFormat / etc.
    // When an Intl constructor is called without `new`, creates the instance
    // first, then checks OrdinaryHasInstance on the receiver. If the receiver
    // is already an instance, tags it with %Intl%.[[FallbackSymbol]] and returns
    // the receiver; otherwise returns the new instance.
    private JsValue ChainIntlService(
        JsValue thisValue,
        IReadOnlyList<JsValue> args,
        ObjectHandle prototypeHandle,
        Func<IReadOnlyList<JsValue>, JsValue> construct)
    {
        var instance = construct(args);

        if (thisValue.Tag == JsValueTag.Object)
        {
            var receiverHandle = thisValue.AsObjectHandle();
            try
            {
                // OrdinaryHasInstance: check if the receiver's prototype chain
                // contains the constructor's shared prototype (works for
                // Object.create(Intl.Xxx.prototype) patterns), OR a prototype
                // that carries a `format` method (works for instances created
                // via our private-per-instance prototype pattern).
                if (OrdinaryHasInstancePrototype(thisValue, prototypeHandle) ||
                    InstanceHasIntlFormatMethod(receiverHandle))
                {
                    var receiver = _heap.GetObject(receiverHandle);
                    var fallbackSymbol = EnsureIntlFallbackSymbol();
                    receiver.DefineOwnSymbolProperty(
                        fallbackSymbol.AsSymbolId(),
                        new JsPropertyDescriptor(instance,
                            Writable: false, Enumerable: false, Configurable: false));
                    return thisValue;
                }
            }
            catch (JsThrownException)
            {
                // If prototype chain walk throws (non-object prototype), fall through.
            }
        }

        return instance;
    }

    // Walk the prototype chain of `objHandle` looking for a prototype that
    // carries an own `format` method. Inserted by our per-instance prototype
    // pattern for Intl constructors (NumberFormat, DateTimeFormat, etc.).
    private bool InstanceHasIntlFormatMethod(ObjectHandle objHandle)
    {
        var current = _heap.GetObject(objHandle);
        while (true)
        {
            if (current.TryGetOwnProperty("format", out _))
            {
                return true;
            }
            var protoHandle = current.PrototypeHandle;
            if (!protoHandle.HasValue) break;
            current = _heap.GetObject(protoHandle.Value);
        }
        return false;
    }

    private JsValue EnsureIntlFallbackSymbol()
    {
        if (_intlFallbackSymbol.Tag == JsValueTag.Undefined)
        {
            _intlFallbackSymbol = JsValue.FromSymbol("IntlLegacyConstructedSymbol");
        }

        return _intlFallbackSymbol;
    }

    // ECMA-402 11.1.1 InitializeDateTimeFormat.
    private JsValue DateTimeFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locales = CanonicalizeIntlLocaleList(args.Count > 0 ? args[0] : JsValue.Undefined);
        var locale = locales.Count > 0 ? locales[0] : string.Empty;
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

        // Use shared Intl.DateTimeFormat.prototype. Instance state (locale,
        // options) is stored on the object so shared prototype methods can read it.
        var protoHandle = EnsureDateTimeFormatPrototype();

        // Store locale + serialized options state on the instance so shared
        // prototype methods (format, formatToParts, formatRange, etc.) can read them back.
        var stateObj = CreateOrdinaryObject();
        void PutStr(string k, string? v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(v is not null ? JsValue.FromString(v) : JsValue.Undefined, Writable: false, Enumerable: false, Configurable: false));
        void PutInt(string k, int? v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(v.HasValue ? JsValue.FromNumber(v.Value) : JsValue.Undefined, Writable: false, Enumerable: false, Configurable: false));
        void PutBool(string k, bool? v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(v.HasValue ? JsValue.FromBoolean(v.Value) : JsValue.Undefined, Writable: false, Enumerable: false, Configurable: false));

        PutStr("locale", locale);
        PutStr("timeZone", options.TimeZoneId);
        PutStr("calendar", options.CalendarId);
        PutStr("dateStyle", options.DateStyle);
        PutStr("timeStyle", options.TimeStyle);
        PutStr("hourCycle", options.HourCycle);
        PutBool("hour12", options.Hour12);
        PutStr("weekday", options.Weekday);
        PutStr("era", options.Era);
        PutStr("year", options.Year);
        PutStr("month", options.Month);
        PutStr("day", options.Day);
        PutStr("hour", options.Hour);
        PutStr("minute", options.Minute);
        PutStr("second", options.Second);
        PutInt("fractionalSecondDigits", options.FractionalSecondDigits);
        PutStr("dayPeriod", options.DayPeriod);
        PutStr("timeZoneName", options.TimeZoneName);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        _ = instance.DefineOwnProperty("__dtf_state", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(stateObj, AllocationSite.Current())), Writable: false, Enumerable: false, Configurable: false));
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);

        return JsValue.FromObject(instanceHandle);
    }

    // ECMA-402: Read the __dtf_state property from a DateTimeFormat instance,
    // throwing TypeError for incompatible receivers.
    private JsObject RequireDateTimeFormatState(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.DateTimeFormat method called on incompatible receiver."));
        var receiver = _heap.GetObject(thisValue.AsObjectHandle());
        if (!receiver.TryGetProperty("__dtf_state", x => _heap.GetObject(x), out var stateDesc) ||
            stateDesc.Value.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.DateTimeFormat method called on incompatible receiver."));
        return _heap.GetObject(stateDesc.Value.AsObjectHandle());
    }

    // Rebuild the locale string and IntlDateTimeFormatOptions from a stored state object.
    private (string locale, IntlDateTimeFormatOptions options) RebuildDateTimeFormatState(JsObject stateObj)
    {
        string? ReadStr(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag == JsValueTag.String ? d.Value.AsString() : null;
        int? ReadInt(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag != JsValueTag.Undefined ? (int)d.Value.AsNumber() : null;
        bool? ReadBool(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag != JsValueTag.Undefined ? d.Value.AsBoolean() : null;

        var locale = ReadStr("locale") ?? "";
        var options = new IntlDateTimeFormatOptions(
            CalendarId: ReadStr("calendar"),
            TimeZoneId: ReadStr("timeZone"),
            DateStyle: ReadStr("dateStyle"),
            TimeStyle: ReadStr("timeStyle"),
            HourCycle: ReadStr("hourCycle"),
            Hour12: ReadBool("hour12"),
            Weekday: ReadStr("weekday"),
            Era: ReadStr("era"),
            Year: ReadStr("year"),
            Month: ReadStr("month"),
            Day: ReadStr("day"),
            Hour: ReadStr("hour"),
            Minute: ReadStr("minute"),
            Second: ReadStr("second"),
            FractionalSecondDigits: ReadInt("fractionalSecondDigits"),
            DayPeriod: ReadStr("dayPeriod"),
            TimeZoneName: ReadStr("timeZoneName"));
        return (locale, options);
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

        DefineIntlPrototypeAccessor(prototypeHandle, prototype, "format",
            (thisValue, fmtArgs) =>
            {
                var stateObj = RequireDateTimeFormatState(thisValue);
                var (locale, dtOpts) = RebuildDateTimeFormatState(stateObj);
                var dtCulture = IntlDateTimeFormatting.ResolveCulture(locale);
                return DateTimeFormatPrototypeFormat(dtCulture, dtOpts, fmtArgs);
            },
            length: 1);

        DefineIntlPrototypeAccessor(prototypeHandle, prototype, "formatToParts",
            (thisValue, fmtArgs) =>
            {
                var stateObj = RequireDateTimeFormatState(thisValue);
                var (locale, dtOpts) = RebuildDateTimeFormatState(stateObj);
                var dtCulture = IntlDateTimeFormatting.ResolveCulture(locale);
                return DateTimeFormatPrototypeFormatToParts(dtCulture, dtOpts, fmtArgs);
            },
            length: 1);

        DefineIntlPrototypeAccessor(prototypeHandle, prototype, "resolvedOptions",
            (thisValue, _) =>
            {
                var stateObj = RequireDateTimeFormatState(thisValue);
                var (locale, dtOpts) = RebuildDateTimeFormatState(stateObj);
                return DateTimeFormatResolvedOptions(locale, dtOpts);
            },
            length: 0);

        DefineIntlPrototypeAccessor(prototypeHandle, prototype, "formatRange",
            (thisValue, rangeArgs) =>
            {
                var stateObj = RequireDateTimeFormatState(thisValue);
                var (locale, dtOpts) = RebuildDateTimeFormatState(stateObj);
                var dtCulture = IntlDateTimeFormatting.ResolveCulture(locale);
                return DateTimeFormatFormatRangeCore(dtCulture, dtOpts,
                    new[] { rangeArgs.Count > 0 ? rangeArgs[0] : JsValue.Undefined },
                    new[] { rangeArgs.Count > 1 ? rangeArgs[1] : JsValue.Undefined },
                    parts: false);
            },
            length: 2);

        DefineIntlPrototypeAccessor(prototypeHandle, prototype, "formatRangeToParts",
            (thisValue, rangeArgs) =>
            {
                var stateObj = RequireDateTimeFormatState(thisValue);
                var (locale, dtOpts) = RebuildDateTimeFormatState(stateObj);
                var dtCulture = IntlDateTimeFormatting.ResolveCulture(locale);
                return DateTimeFormatFormatRangeCore(dtCulture, dtOpts,
                    new[] { rangeArgs.Count > 0 ? rangeArgs[0] : JsValue.Undefined },
                    new[] { rangeArgs.Count > 1 ? rangeArgs[1] : JsValue.Undefined },
                    parts: true);
            },
            length: 2);

        _dateTimeFormatPrototypeHandle = prototypeHandle;
        return prototypeHandle;
    }

    // ECMA-402 11.3.2 Intl.DateTimeFormat.prototype.format(date).
    // Routes each Temporal type to the correct formatting path.
    private JsValue DateTimeFormatPrototypeFormat(
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        IReadOnlyList<JsValue> args)
    {
        // Temporal.PlainTime (time-only, no date fields)
        if (TryGetTemporalPlainTime(args, culture, options, out var plainTimeResult))
            return JsValue.FromString(plainTimeResult.Text);

        // Temporal date-only types (PlainDate, PlainYearMonth, PlainMonthDay)
        if (TryGetTemporalDateOnly(args, culture, options, out var dateOnlyResult))
            return JsValue.FromString(dateOnlyResult.Text);

        // Temporal.PlainDateTime (date+time, no timezone shift)
        if (TryGetTemporalPlainDateTime(args, culture, options, out var plainDtResult))
            return JsValue.FromString(plainDtResult.Text);

        // Temporal.ZonedDateTime (epoch ns + tz → wall clock)
        if (TryGetTemporalZonedDateTime(args, culture, options, out var zdtResult))
            return JsValue.FromString(zdtResult.Text);

        // Temporal.Instant, JS Date, or numeric timestamp
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
        // Temporal.PlainTime (time-only)
        if (TryGetTemporalPlainTime(args, culture, options, out var plainTimeResult))
        {
            return CreateIntlPartsArray(plainTimeResult.Parts);
        }

        // Temporal date-only types (PlainDate, PlainYearMonth, PlainMonthDay)
        if (TryGetTemporalDateOnly(args, culture, options, out var dateOnlyResult))
        {
            return CreateIntlPartsArray(dateOnlyResult.Parts);
        }

        // Temporal.PlainDateTime (date+time, no timezone shift)
        if (TryGetTemporalPlainDateTime(args, culture, options, out var plainDtResult))
        {
            return CreateIntlPartsArray(plainDtResult.Parts);
        }

        // Temporal.ZonedDateTime
        if (TryGetTemporalZonedDateTime(args, culture, options, out var zdtResult))
        {
            return CreateIntlPartsArray(zdtResult.Parts);
        }

        // Temporal.Instant, JS Date, or numeric timestamp
        if (!TryGetDateTimeFormatInput(args, out var instant))
        {
            var emptyArray = CreateArrayObject(Array.Empty<JsValue>());
            return JsValue.FromObject(_heap.AllocateObject(emptyArray, AllocationSite.Current()));
        }

        var result = IntlDateTimeFormatting.Format(instant, culture, options);
        return CreateIntlPartsArray(result.Parts);
    }

    // Helper to convert IntlDateTimePart list to an ArrayObject of {type, value} objects.
    private JsValue CreateIntlPartsArray(IReadOnlyList<IntlDateTimePart> parts)
    {
        var partValues = new List<JsValue>(parts.Count);
        foreach (var part in parts)
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

    // ECMA-402 formatRange/formatRangeToParts core implementation for DateTimeFormat.
    // Coerces both arguments to Temporal/Date values, formats each, and returns
    // either the combined range string or a parts array with source annotations.
    private JsValue DateTimeFormatFormatRangeCore(
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        IReadOnlyList<JsValue> xArgs,
        IReadOnlyList<JsValue> yArgs,
        bool parts)
    {
        // ECMA-402 §11.6.1: if either argument is undefined, throw TypeError.
        if (xArgs.Count == 0 || xArgs[0].Tag == JsValueTag.Undefined ||
            yArgs.Count == 0 || yArgs[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(CreateTypeError("formatRange requires two date arguments."));

        // Check for NaN/Infinity Date values (from JS Date or numeric timestamp).
        if (IsInvalidDateValue(xArgs[0]) || IsInvalidDateValue(yArgs[0]))
            throw new JsThrownException(CreateRangeError("formatRange requires valid dates."));

        // Coerce x and y to Temporal/Date values.
        int xKind, yKind;
        var xHas = TryGetDateTimeFormatRangeInput(xArgs, culture, options, out var xText, out var xPartsList, out xKind);
        var yHas = TryGetDateTimeFormatRangeInput(yArgs, culture, options, out var yText, out var yPartsList, out yKind);

        if (!xHas || !yHas)
        {
            if (!parts)
                return JsValue.FromString(xHas ? xText : (yHas ? yText : ""));
            var emptyArray = CreateArrayObject(Array.Empty<JsValue>());
            return JsValue.FromObject(_heap.AllocateObject(emptyArray, AllocationSite.Current()));
        }

        // ECMA-402: throw RangeError for mixed Temporal types.
        if (xKind != yKind && xKind >= 0 && yKind >= 0)
            throw new JsThrownException(CreateRangeError("formatRange requires arguments of the same Temporal type."));

        // ECMA-402: throw TypeError for ZonedDateTime in formatRangeToParts.
        if (parts && (xKind == 4 || yKind == 4))
            throw new JsThrownException(CreateTypeError("formatRangeToParts() does not support Temporal.ZonedDateTime"));

        if (!parts)
        {
            if (xText == yText)
                return JsValue.FromString(xText);
            return JsValue.FromString(xText + "-" + yText);
        }

        // formatRangeToParts: annotate each part with source.
        if (xText == yText)
        {
            var sharedParts = new List<JsValue>(xPartsList.Count);
            foreach (var p in xPartsList)
                sharedParts.Add(MakeRangePart(p, "shared"));
            var arr = CreateArrayObject(sharedParts);
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }

        var rangeParts = new List<JsValue>();
        foreach (var p in xPartsList)
            rangeParts.Add(MakeRangePart(p, "startRange"));
        rangeParts.Add(MakeRangeLiteralPart("-"));
        foreach (var p in yPartsList)
            rangeParts.Add(MakeRangePart(p, "endRange"));
        var rangeArr = CreateArrayObject(rangeParts);
        return JsValue.FromObject(_heap.AllocateObject(rangeArr, AllocationSite.Current()));
    }

    // Try to coerce a list of args to a formatted DateTime string + parts.
    // Returns the Temporal type kind: 0=Date/ms, 1=date-only, 2=PlainDateTime,
    // 3=PlainTime, 4=ZonedDateTime, -1=unknown/invalid.
    private bool TryGetDateTimeFormatRangeInput(
        IReadOnlyList<JsValue> args,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        out string text,
        out List<IntlDateTimePart> partsList,
        out int kind)
    {
        text = "";
        partsList = new List<IntlDateTimePart>();
        kind = -1;
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
            return false;

        // Try each Temporal type.
        if (TryGetTemporalPlainTime(args, culture, options, out var ptRes))
        { text = ptRes.Text; partsList.AddRange(ptRes.Parts); kind = 3; return true; }
        if (TryGetTemporalDateOnly(args, culture, options, out var doRes))
        { text = doRes.Text; partsList.AddRange(doRes.Parts); kind = 1; return true; }
        if (TryGetTemporalPlainDateTime(args, culture, options, out var pdtRes))
        { text = pdtRes.Text; partsList.AddRange(pdtRes.Parts); kind = 2; return true; }
        if (TryGetTemporalZonedDateTime(args, culture, options, out var zdtRes))
        { text = zdtRes.Text; partsList.AddRange(zdtRes.Parts); kind = 4; return true; }

        // JS Date or numeric timestamp.
        if (TryGetDateTimeFormatInput(args, out var instant))
        {
            var result = IntlDateTimeFormatting.Format(instant, culture, options);
            text = result.Text;
            partsList.AddRange(result.Parts);
            kind = 0;
            return true;
        }

        return false;
    }

    // Make a { type, value, source } part object for formatRangeToParts.
    private JsValue MakeRangePart(IntlDateTimePart part, string source)
    {
        var obj = CreateOrdinaryObject();
        _ = obj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(part.Type), Writable: true, Enumerable: true, Configurable: true));
        _ = obj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(part.Value), Writable: true, Enumerable: true, Configurable: true));
        _ = obj.DefineOwnProperty("source", new JsPropertyDescriptor(JsValue.FromString(source), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    // Check if a value is a NaN or Infinity Date/timestamp.
    private bool IsInvalidDateValue(JsValue arg)
    {
        if (arg.Tag == JsValueTag.Number)
            return double.IsNaN(arg.AsNumber()) || double.IsInfinity(arg.AsNumber());
        if (arg.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(arg.AsObjectHandle());
            if (obj is DateObject dateObj)
                return double.IsNaN(dateObj.TimeValue) || double.IsInfinity(dateObj.TimeValue);
        }
        return false;
    }

    // Make a { type: "literal", value, source } part for a range literal separator.
    private JsValue MakeRangeLiteralPart(string value)
    {
        var obj = CreateOrdinaryObject();
        _ = obj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString("literal"), Writable: true, Enumerable: true, Configurable: true));
        _ = obj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromString(value), Writable: true, Enumerable: true, Configurable: true));
        _ = obj.DefineOwnProperty("source", new JsPropertyDescriptor(JsValue.FromString("shared"), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    // ECMA-402 13.1.1 InitializeNumberFormat.
    // Stores the resolved state as internal slots on the instance so the shared
    // prototype methods can read them back. Instance inherits directly from the
    // shared Intl.NumberFormat.prototype.
    private JsValue NumberFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locales = CanonicalizeIntlLocaleList(args.Count > 0 ? args[0] : JsValue.Undefined);
        var locale = locales.Count > 0 ? locales[0] : string.Empty;
        var state = ParseNumberFormatState(locale, args.Count > 1 ? args[1] : JsValue.Undefined);

        // Store the state as a JsObject so the shared proto methods can read it.
        var stateObj = CreateOrdinaryObject();
        void PutStr(string k, string? v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(v is not null ? JsValue.FromString(v) : JsValue.Undefined, Writable: false, Enumerable: false, Configurable: false));
        void PutInt(string k, int? v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(v.HasValue ? JsValue.FromNumber(v.Value) : JsValue.Undefined, Writable: false, Enumerable: false, Configurable: false));
        void PutBool(string k, bool v) =>
            stateObj.DefineOwnProperty(k, new JsPropertyDescriptor(JsValue.FromBoolean(v), Writable: false, Enumerable: false, Configurable: false));

        PutStr("locale", state.Locale);
        PutStr("style", state.Style);
        PutStr("currency", state.Currency);
        PutStr("currencyDisplay", state.CurrencyDisplay);
        PutStr("currencySign", state.CurrencySign);
        PutStr("unit", state.Unit);
        PutStr("unitDisplay", state.UnitDisplay);
        PutStr("notation", state.Notation);
        PutStr("compactDisplay", state.CompactDisplay);
        PutInt("minIntDigits", state.MinimumIntegerDigits);
        PutInt("minFracDigits", state.MinimumFractionDigits);
        PutInt("maxFracDigits", state.MaximumFractionDigits);
        PutInt("minSigDigits", state.MinimumSignificantDigits);
        PutInt("maxSigDigits", state.MaximumSignificantDigits);
        PutBool("useGrouping", state.UseGrouping);
        PutStr("useGroupingValue", state.UseGroupingValue);
        PutStr("signDisplay", state.SignDisplay);
        PutStr("roundingMode", state.RoundingMode);
        PutInt("roundingIncrement", state.RoundingIncrement);
        PutStr("roundingPriority", state.RoundingPriority);
        PutStr("trailingZeroDisplay", state.TrailingZeroDisplay);
        PutStr("numberingSystem", state.NumberingSystem);

        var stateHandle = _heap.AllocateObject(stateObj, AllocationSite.Current());
        _heap.PushRoot(stateHandle);

        var prototypeHandle = _numberFormatPrototypeHandle ?? EnsureNumberFormatPrototypeHandle();

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(prototypeHandle);
        _ = instance.DefineOwnProperty(
            "__numberFormatState",
            new JsPropertyDescriptor(
                JsValue.FromObject(stateHandle),
                Writable: false, Enumerable: false, Configurable: false));

        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, prototypeHandle);
        _heap.WriteBarrier(instanceHandle, stateHandle);
        return JsValue.FromObject(instanceHandle);
    }

    private NumberFormatState RebuildNumberFormatState(JsObject stateObj)
    {
        string? ReadStr(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag == JsValueTag.String ? d.Value.AsString() : null;
        int? ReadInt(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag != JsValueTag.Undefined ? (int)d.Value.AsNumber() : null;
        bool ReadBool(string k) =>
            stateObj.TryGetOwnProperty(k, out var d) && d.Value.Tag != JsValueTag.Undefined && d.Value.AsBoolean();

        return new NumberFormatState(
            ReadStr("locale") ?? "en-US",
            ReadStr("style"),
            ReadStr("currency"),
            ReadStr("currencyDisplay"),
            ReadStr("currencySign"),
            ReadStr("unit"),
            ReadStr("unitDisplay"),
            ReadStr("notation"),
            ReadStr("compactDisplay"),
            ReadInt("minIntDigits") ?? 1,
            ReadInt("minFracDigits"),
            ReadInt("maxFracDigits"),
            ReadInt("minSigDigits"),
            ReadInt("maxSigDigits"),
            ReadBool("useGrouping"),
            ReadStr("useGroupingValue") ?? "auto",
            ReadStr("signDisplay"),
            ReadStr("roundingMode"),
            ReadInt("roundingIncrement"),
            ReadStr("roundingPriority"),
            ReadStr("trailingZeroDisplay"),
            ReadStr("numberingSystem") ?? "latn");
    }

    private JsObject RequireNumberFormatState(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.NumberFormat method called on incompatible receiver."));
        var receiver = _heap.GetObject(thisValue.AsObjectHandle());
        if (!receiver.TryGetProperty("__numberFormatState", x => _heap.GetObject(x), out var stateDesc) ||
            stateDesc.Value.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.NumberFormat method called on incompatible receiver."));
        return _heap.GetObject(stateDesc.Value.AsObjectHandle());
    }

    private ObjectHandle EnsureNumberFormatPrototypeHandle()
    {
        // The shared NumberFormat.prototype is created inline in EnsureIntlObject().
        // If for some reason it doesn't exist yet, create a minimal one.
        if (_numberFormatPrototypeHandle is { } existing) return existing;
        var p = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(p, AllocationSite.Current());
        _numberFormatPrototypeHandle = ph;
        return ph;
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
        Put("useGrouping", state.UseGrouping ? JsValue.FromString(state.UseGroupingValue) : JsValue.FromBoolean(false));
        Put("notation", JsValue.FromString(state.Notation ?? "standard"));
        Put("signDisplay", JsValue.FromString(state.SignDisplay ?? "auto"));
        Put("roundingIncrement", JsValue.FromNumber(state.RoundingIncrement ?? 1));
        Put("roundingMode", JsValue.FromString(state.RoundingMode ?? "halfExpand"));
        Put("roundingPriority", JsValue.FromString(state.RoundingPriority ?? "auto"));
        Put("trailingZeroDisplay", JsValue.FromString(state.TrailingZeroDisplay ?? "auto"));

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
        var locales = CanonicalizeIntlLocaleList(args.Count > 0 ? args[0] : JsValue.Undefined);
        var locale = locales.Count > 0 ? locales[0] : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);

        // Parse options from args[1] if present.
        var optionsValue = args.Count > 1 ? args[1] : JsValue.Undefined;
        string usage = "sort";
        string sensitivity = "variant";
        bool ignorePunctuation = false;
        bool numeric = false;
        string caseFirst = "false";
        string? collationOverride = null;

        if (optionsValue.Tag != JsValueTag.Undefined)
        {
            if (optionsValue.Tag == JsValueTag.Null)
                throw new JsThrownException(CreateTypeError("Cannot convert null to object."));
            JsObject optsObj;
            JsValue optsReceiver;
            if (optionsValue.Tag == JsValueTag.Object)
            { optsObj = _heap.GetObject(optionsValue.AsObjectHandle()); optsReceiver = optionsValue; }
            else
            { optsObj = ToObject(optionsValue); optsReceiver = JsValue.FromObject(_heap.AllocateObject(optsObj, AllocationSite.Current())); }

            string? GetS(string n) => TryGetPropertyValue(optsObj, optsReceiver, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;
            bool? GetB(string n) => TryGetPropertyValue(optsObj, optsReceiver, n, out var v) && v.Tag != JsValueTag.Undefined ? IsTruthy(v) : null;

            var lm = GetS("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value."));

            var u = GetS("usage");
            if (u is not null)
            {
                if (u is not "sort" and not "search") throw new JsThrownException(CreateRangeError($"Invalid usage: {u}"));
                usage = u;
            }

            var s = GetS("sensitivity");
            if (s is not null)
            {
                if (s is not "base" and not "accent" and not "case" and not "variant") throw new JsThrownException(CreateRangeError($"Invalid sensitivity: {s}"));
                sensitivity = s;
            }

            ignorePunctuation = GetB("ignorePunctuation") ?? false;
            numeric = GetB("numeric") ?? false;

            var cf = GetS("caseFirst");
            if (cf is not null)
            {
                if (cf is not "upper" and not "lower" and not "false") throw new JsThrownException(CreateRangeError($"Invalid caseFirst: {cf}"));
                caseFirst = cf;
            }

            collationOverride = GetS("collation");
            if (collationOverride is not null && collationOverride is not "default" and not "search")
                throw new JsThrownException(CreateRangeError($"Invalid collation: {collationOverride}"));
        }

        // Parse Unicode extension keys from locale. If the `co` key is present,
        // it overrides the options collation (but options still win if explicitly set).
        var localeCollation = ExtractUnicodeKeyword(locale, "co");
        var localeNumeric = ExtractUnicodeKeyword(locale, "kn");
        var localeCaseFirst = ExtractUnicodeKeyword(locale, "kf");
        var resolvedCollation = collationOverride ?? localeCollation ?? "default";
        var resolvedNumeric = numeric || HasUnicodeKeyTrue(locale, "kn");
        var resolvedCaseFirst = caseFirst != "false" ? caseFirst :
            (localeCaseFirst is "upper" or "lower" ? localeCaseFirst : "false");

        // Use the shared Collator.prototype (like DisplayNames).
        var protoHandle = EnsureCollatorPrototype();
        var capturedLocale = string.IsNullOrEmpty(locale) ? "en-US" : locale;

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        // Store state on instance so shared prototype methods can read it.
        _ = instance.DefineOwnProperty("__collator_locale",
            new JsPropertyDescriptor(JsValue.FromString(capturedLocale),
                Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_usage",
            new JsPropertyDescriptor(JsValue.FromString(usage), Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_sensitivity",
            new JsPropertyDescriptor(JsValue.FromString(sensitivity), Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_ignorePunctuation",
            new JsPropertyDescriptor(JsValue.FromBoolean(ignorePunctuation), Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_collation",
            new JsPropertyDescriptor(JsValue.FromString(resolvedCollation), Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_numeric",
            new JsPropertyDescriptor(JsValue.FromBoolean(resolvedNumeric), Writable: false, Enumerable: false, Configurable: false));
        _ = instance.DefineOwnProperty("__collator_caseFirst",
            new JsPropertyDescriptor(JsValue.FromString(resolvedCaseFirst), Writable: false, Enumerable: false, Configurable: false));
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }

    private JsValue ListFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locales = CanonicalizeIntlLocaleList(args.Count > 0 ? args[0] : JsValue.Undefined);
        var locale = locales.Count > 0 ? locales[0] : string.Empty;
        var state = ParseListFormatState(locale, args.Count > 1 ? args[1] : JsValue.Undefined);

        // Use the shared ListFormat.prototype.
        var protoHandle = EnsureListFormatPrototype();

        // Store state so shared prototype methods can read it.
        var lfState = CreateOrdinaryObject();
        lfState.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(state.Locale ?? "en-US"), Writable: false, Enumerable: false, Configurable: false));
        lfState.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(state.Type), Writable: false, Enumerable: false, Configurable: false));
        lfState.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(state.Style), Writable: false, Enumerable: false, Configurable: false));
        var lfStateHandle = _heap.AllocateObject(lfState, AllocationSite.Current());

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        _ = instance.DefineOwnProperty("__listFormatState", new JsPropertyDescriptor(JsValue.FromObject(lfStateHandle), Writable: false, Enumerable: false, Configurable: false));
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        _heap.WriteBarrier(instanceHandle, lfStateHandle);
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

        // Temporal date types (PlainDate, PlainDateTime, PlainMonthDay, PlainYearMonth).
        // These store internal slots in _v and throw on valueOf coercion.
        if (args[0].Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(args[0].AsObjectHandle());
            if (obj.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.Object)
            {
                var data = _heap.GetObject(vd.Value.AsObjectHandle());
                // All Temporal date types have calendarId. PlainTime does not.
                if (data.TryGetOwnProperty("calendarId", out _))
                {
                    int y = 0, m = 1, d = 1;
                    if (data.TryGetOwnProperty("y", out var yd))
                    { y = (int)yd.Value.AsNumber(); m = (int)(data.TryGetOwnProperty("m", out var md) ? md.Value.AsNumber() : 1); d = (int)(data.TryGetOwnProperty("d", out var dd) ? dd.Value.AsNumber() : 1); }
                    else if (data.TryGetOwnProperty("year", out var y2d))
                    { y = (int)y2d.Value.AsNumber(); m = (int)(data.TryGetOwnProperty("month", out var m2d) ? m2d.Value.AsNumber() : 1); d = (int)(data.TryGetOwnProperty("day", out var d2d) ? d2d.Value.AsNumber() : 1); }
                    if (y == 0) y = 2000; // PlainMonthDay has no year, use reference year
                    instant = new DateTimeOffset(y, Math.Clamp(m, 1, 12), Math.Clamp(d, 1, 28), 0, 0, 0, TimeSpan.Zero);
                    return true;
                }
            }
        }

        try
        {
            var timestamp = DateArgToTimeClip(args[0]);
            if (double.IsNaN(timestamp))
                return false;
            // Clamp to DateTimeOffset's representable range to avoid
            // ArgumentOutOfRangeException from FromUnixTimeMilliseconds.
            var ms = (long)Math.Clamp(timestamp, -315537897600000, 315537897600000);
            instant = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }
        catch (JsThrownException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
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

        // If ToNumber throws (e.g., Temporal objects with throwing valueOf),
        // return NaN so the caller can handle it.
        try { return ToNumber(arg); }
        catch (JsThrownException) { return double.NaN; }
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

    // ECMA-402: Detect Temporal.PlainDate / PlainYearMonth / PlainMonthDay (date-only types).
    // Returns true and sets result if the arg is a date-only Temporal type.
    private bool TryGetTemporalDateOnly(
        IReadOnlyList<JsValue> args,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        out IntlDateTimeFormatResult result)
    {
        result = default!;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            return false;

        var obj = _heap.GetObject(args[0].AsObjectHandle());
        if (!obj.TryGetProperty("_v", x => _heap.GetObject(x), out var slotsDesc) || slotsDesc.Value.Tag != JsValueTag.Object)
            return false;
        var slots = _heap.GetObject(slotsDesc.Value.AsObjectHandle());

        // PlainTime is handled separately by TryGetTemporalPlainTime.
        // ZonedDateTime has "tz" in _v; skip it.
        if (slots.TryGetProperty("tz", x => _heap.GetObject(x), out _))
            return false;
        // Instant has "ens" but no date fields; skip it.
        if (slots.TryGetProperty("ens", x => _heap.GetObject(x), out _) && !slots.TryGetProperty("y", x => _heap.GetObject(x), out _) && !slots.TryGetProperty("year", x => _heap.GetObject(x), out _))
            return false;

        // Date-only types: have calendarId and either (y,m,d) or (year,month,day) or (mc,d)
        if (!slots.TryGetProperty("calendarId", x => _heap.GetObject(x), out _))
        {
            // PlainDateTime has calendarId but also has valid time fields — handled below.
            if (!slots.TryGetProperty("hour", x => _heap.GetObject(x), out _))
                return false; // not a Temporal type we recognize for date-only
            return false; // has hour but no calendarId — PlainTime (already handled)
        }

        int y, m, d;

        string temporalType = slots.TryGetProperty("__temporalType", x => _heap.GetObject(x), out var typeDesc)
            && typeDesc.Value.Tag == JsValueTag.String
            ? typeDesc.Value.AsString()
            : "";

        if (temporalType == "PlainMonthDay")
        {
            y = slots.TryGetProperty("y", x => _heap.GetObject(x), out var mdYearDesc) ? (int)ToNumber(mdYearDesc.Value) : 1972;
            m = slots.TryGetProperty("m", x => _heap.GetObject(x), out var mdMonthDesc) ? (int)ToNumber(mdMonthDesc.Value) : 1;
            d = slots.TryGetProperty("d", x => _heap.GetObject(x), out var ddDesc) ? (int)ToNumber(ddDesc.Value) : 1;
            result = IntlDateTimeFormatting.FormatDateOnly(y, m, d, culture, options, defaultIncludesYear: false);
            return true;
        }

        if (temporalType == "PlainYearMonth"
            && slots.TryGetProperty("y", x => _heap.GetObject(x), out var yDesc)
            && slots.TryGetProperty("m", x => _heap.GetObject(x), out var mDesc))
        {
            y = (int)ToNumber(yDesc.Value);
            m = (int)ToNumber(mDesc.Value);
            d = slots.TryGetProperty("d", x => _heap.GetObject(x), out var yrDayDesc) ? (int)ToNumber(yrDayDesc.Value) : 1;
            result = IntlDateTimeFormatting.FormatDateOnly(y, m, d, culture, options, defaultIncludesDay: false);
            return true;
        }

        // PlainDate: has y, m, d but no hour
        if (slots.TryGetProperty("y", x => _heap.GetObject(x), out var yDesc2) &&
            slots.TryGetProperty("m", x => _heap.GetObject(x), out var mDesc2) &&
            slots.TryGetProperty("d", x => _heap.GetObject(x), out var dDesc2) &&
            !slots.TryGetProperty("hour", x => _heap.GetObject(x), out _))
        {
            y = (int)ToNumber(yDesc2.Value);
            m = (int)ToNumber(mDesc2.Value);
            d = (int)ToNumber(dDesc2.Value);
            result = IntlDateTimeFormatting.FormatDateOnly(y, m, d, culture, options);
            return true;
        }

        return false;
    }

    // ECMA-402: Detect Temporal.PlainDateTime (date+time, no timezone).
    private bool TryGetTemporalPlainDateTime(
        IReadOnlyList<JsValue> args,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        out IntlDateTimeFormatResult result)
    {
        result = default!;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            return false;

        var obj = _heap.GetObject(args[0].AsObjectHandle());
        if (!obj.TryGetProperty("_v", x => _heap.GetObject(x), out var slotsDesc) || slotsDesc.Value.Tag != JsValueTag.Object)
            return false;
        var slots = _heap.GetObject(slotsDesc.Value.AsObjectHandle());

        // Must have both date fields (year/month/day) and time fields (hour)
        if (!slots.TryGetProperty("hour", x => _heap.GetObject(x), out _))
            return false;
        if (!slots.TryGetProperty("year", x => _heap.GetObject(x), out _))
            return false;
        // ZonedDateTime has tz; skip
        if (slots.TryGetProperty("tz", x => _heap.GetObject(x), out _))
            return false;

        try
        {
            int y = (int)ToNumber(slots.TryGetProperty("year", x => _heap.GetObject(x), out var yDesc) ? yDesc.Value : JsValue.Undefined);
            int mo = (int)ToNumber(slots.TryGetProperty("month", x => _heap.GetObject(x), out var moDesc) ? moDesc.Value : JsValue.Undefined);
            int d = (int)ToNumber(slots.TryGetProperty("day", x => _heap.GetObject(x), out var dDesc) ? dDesc.Value : JsValue.Undefined);
            int hr = (int)ToNumber(slots.TryGetProperty("hour", x => _heap.GetObject(x), out var hrDesc) ? hrDesc.Value : JsValue.Undefined);
            int mi = (int)ToNumber(slots.TryGetProperty("minute", x => _heap.GetObject(x), out var minDesc) ? minDesc.Value : JsValue.Undefined);
            int se = (int)ToNumber(slots.TryGetProperty("second", x => _heap.GetObject(x), out var secDesc) ? secDesc.Value : JsValue.Undefined);
            int ms = (int)ToNumber(slots.TryGetProperty("millisecond", x => _heap.GetObject(x), out var msDesc) ? msDesc.Value : JsValue.Undefined);
            int us = (int)ToNumber(slots.TryGetProperty("microsecond", x => _heap.GetObject(x), out var usDesc) ? usDesc.Value : JsValue.Undefined);
            int ns = (int)ToNumber(slots.TryGetProperty("nanosecond", x => _heap.GetObject(x), out var nsDesc) ? nsDesc.Value : JsValue.Undefined);
            result = IntlDateTimeFormatting.FormatPlainDateTimeParts(y, mo, d, hr, mi, se, ms, us, ns, culture, options);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrownException(CreateTypeError(ex.Message));
        }
    }

    // ECMA-402: Detect Temporal.ZonedDateTime (epoch ns + timezone → wall time → format).
    private bool TryGetTemporalZonedDateTime(
        IReadOnlyList<JsValue> args,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        out IntlDateTimeFormatResult result)
    {
        result = default!;
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
            return false;

        var obj = _heap.GetObject(args[0].AsObjectHandle());
        if (!obj.TryGetProperty("_v", x => _heap.GetObject(x), out var slotsDesc) || slotsDesc.Value.Tag != JsValueTag.Object)
            return false;
        var slots = _heap.GetObject(slotsDesc.Value.AsObjectHandle());

        // Must have both ens and tz
        if (!slots.TryGetProperty("ens", x => _heap.GetObject(x), out _))
            return false;
        if (!slots.TryGetProperty("tz", x => _heap.GetObject(x), out var tzDesc))
            return false;

        try
        {
            var nanos = (long)ToNumber(slots.TryGetProperty("ens", x => _heap.GetObject(x), out var ensDesc) ? ensDesc.Value : JsValue.FromNumber(0));
            var tz = tzDesc.Value.Tag == JsValueTag.String ? tzDesc.Value.AsString() : "UTC";
            var instant = new DateTimeOffset(new DateTime(621355968000000000L + (nanos / 100L), DateTimeKind.Utc));
            var tzOptions = options with { TimeZoneId = tz };
            result = IntlDateTimeFormatting.Format(instant, culture, tzOptions);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrownException(CreateTypeError(ex.Message));
        }
    }

    private IntlDateTimeFormatOptions ParseDateTimeFormatOptions(string locale, JsValue optionsValue)
    {
        if (optionsValue.Tag == JsValueTag.Undefined)
        {
            return new IntlDateTimeFormatOptions(CalendarId: ParseCalendarId(locale));
        }

        // ECMA-402: options must be converted via ToObject.
        JsObject optionsObject;
        JsValue optionsReceiver;
        if (optionsValue.Tag == JsValueTag.Null)
        {
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));
        }
        if (optionsValue.Tag == JsValueTag.Object)
        {
            optionsObject = _heap.GetObject(optionsValue.AsObjectHandle());
            optionsReceiver = optionsValue;
        }
        else
        {
            optionsObject = ToObject(optionsValue);
            optionsReceiver = JsValue.FromObject(_heap.AllocateObject(optionsObject, AllocationSite.Current()));
        }

        string? GetString(string name)
        {
            if (!TryGetPropertyValue(optionsObject, optionsReceiver, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return ToStringValue(value);
        }

        bool? GetBool(string name)
        {
            if (!TryGetPropertyValue(optionsObject, optionsReceiver, name, out var value) || value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return IsTruthy(value);
        }

        // ECMA-402 GetOption: localeMatcher must be "lookup" or "best fit".
        var localeMatcher = GetString("localeMatcher");
        if (localeMatcher is not null && localeMatcher is not "lookup" and not "best fit")
            throw new JsThrownException(CreateRangeError($"Invalid localeMatcher: {localeMatcher}"));

        // Calendar: read from options, then from locale u-ca- extension.
        var calendar = GetString("calendar");
        if (calendar is not null)
        {
            if (!IsWellFormedCalendarType(calendar))
                throw new JsThrownException(CreateRangeError($"Invalid calendar: {calendar}"));
        }
        else
        {
            calendar = ParseCalendarId(locale);
        }

        // numberingSystem validation.
        var optionsNumberingSystem = GetString("numberingSystem");
        if (optionsNumberingSystem is not null && !IsWellFormedNumberingSystem(optionsNumberingSystem))
            throw new JsThrownException(CreateRangeError($"Invalid numberingSystem: {optionsNumberingSystem}"));

        // dateStyle validation.
        var dateStyle = GetString("dateStyle");
        if (dateStyle is not null && dateStyle is not "full" and not "long" and not "medium" and not "short")
            throw new JsThrownException(CreateRangeError($"Invalid dateStyle: {dateStyle}"));

        // timeStyle validation.
        var timeStyle = GetString("timeStyle");
        if (timeStyle is not null && timeStyle is not "full" and not "long" and not "medium" and not "short")
            throw new JsThrownException(CreateRangeError($"Invalid timeStyle: {timeStyle}"));

        // timeZoneName validation.
        var timeZoneName = GetString("timeZoneName");
        if (timeZoneName is not null && timeZoneName is not "long" and not "short" and not "shortOffset" and not "longOffset" and not "shortGeneric" and not "longGeneric")
            throw new JsThrownException(CreateRangeError($"Invalid timeZoneName: {timeZoneName}"));

        // hourCycle validation.
        var hourCycle = GetString("hourCycle");
        if (hourCycle is not null && hourCycle is not "h11" and not "h12" and not "h23" and not "h24")
            throw new JsThrownException(CreateRangeError($"Invalid hourCycle: {hourCycle}"));

        // dayPeriod validation.
        var dayPeriod = GetString("dayPeriod");
        if (dayPeriod is not null && dayPeriod is not "narrow" and not "short" and not "long")
            throw new JsThrownException(CreateRangeError($"Invalid dayPeriod: {dayPeriod}"));

        // weekday validation.
        var weekday = GetString("weekday");
        if (weekday is not null && weekday is not "narrow" and not "short" and not "long")
            throw new JsThrownException(CreateRangeError($"Invalid weekday: {weekday}"));

        // era validation.
        var era = GetString("era");
        if (era is not null && era is not "narrow" and not "short" and not "long")
            throw new JsThrownException(CreateRangeError($"Invalid era: {era}"));

        // year validation.
        var year = GetString("year");
        if (year is not null && year is not "numeric" and not "2-digit")
            throw new JsThrownException(CreateRangeError($"Invalid year: {year}"));

        // month validation.
        var month = GetString("month");
        if (month is not null && month is not "numeric" and not "2-digit" and not "long" and not "short" and not "narrow")
            throw new JsThrownException(CreateRangeError($"Invalid month: {month}"));

        // day validation.
        var day = GetString("day");
        if (day is not null && day is not "numeric" and not "2-digit")
            throw new JsThrownException(CreateRangeError($"Invalid day: {day}"));

        // hour validation.
        var hour = GetString("hour");
        if (hour is not null && hour is not "numeric" and not "2-digit")
            throw new JsThrownException(CreateRangeError($"Invalid hour: {hour}"));

        // minute validation.
        var minute = GetString("minute");
        if (minute is not null && minute is not "numeric" and not "2-digit")
            throw new JsThrownException(CreateRangeError($"Invalid minute: {minute}"));

        // second validation.
        var second = GetString("second");
        if (second is not null && second is not "numeric" and not "2-digit")
            throw new JsThrownException(CreateRangeError($"Invalid second: {second}"));

        // fractionalSecondDigits validation (1-3, must be an integral Number).
        double? fracSecRaw = null;
        if (TryGetPropertyValue(optionsObject, optionsReceiver, "fractionalSecondDigits", out var fsdVal) && fsdVal.Tag != JsValueTag.Undefined)
        {
            fracSecRaw = ToNumber(fsdVal);
            if (double.IsNaN(fracSecRaw.Value) || double.IsInfinity(fracSecRaw.Value) || Math.Floor(fracSecRaw.Value) != fracSecRaw.Value || fracSecRaw.Value < 1 || fracSecRaw.Value > 3)
                throw new JsThrownException(CreateRangeError($"Invalid fractionalSecondDigits: {fsdVal}"));
        }

        // timeZone validation: must be a string if present.
        var timeZoneId = GetString("timeZone");

        var hour12 = GetBool("hour12");

        return new IntlDateTimeFormatOptions(
            CalendarId: calendar,
            TimeZoneId: timeZoneId,
            DateStyle: dateStyle,
            TimeStyle: timeStyle,
            HourCycle: hourCycle,
            Hour12: hour12,
            Weekday: weekday,
            Era: era,
            Year: year,
            Month: month,
            Day: day,
            Hour: hour,
            Minute: minute,
            Second: second,
            FractionalSecondDigits: fracSecRaw.HasValue ? (int)fracSecRaw.Value : null,
            DayPeriod: dayPeriod,
            TimeZoneName: timeZoneName);
    }

    // ECMA-402: Validate calendar type syntax (Unicode Locale Identifier type nonterminal).
    // A valid calendar type is 3-8 alphanumeric chars, optionally followed by -<3-8 alphanumeric chars>.
    private static bool IsWellFormedCalendarType(string calendar) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            calendar, @"^[a-z0-9]{3,8}(-[a-z0-9]{3,8})*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Parse the calendar id from a locale's `-u-ca-` Unicode extension.
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
            "",
            (thisValue, args) => DurationFormatPrototypeFormat(thisValue, args),
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        _ = prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(prototypeHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "",
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
            // ECMA-402 9.2.1: coerce to Object. Number/Boolean wrappers have no
            // length → empty list.
            var obj = ToObject(localesValue);
            var objHandle = _heap.AllocateObject(obj, AllocationSite.Current());
            _heap.PushRoot(objHandle);
            try
            {
                return CanonicalizeLocaleListForDuration(JsValue.FromObject(objHandle));
            }
            finally
            {
                _heap.PopRootsTo(_heap.RootCount - 1);
            }
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
        return CanonicalizeIntlLocaleTag(tag);
    }

    private static bool IsSupportedDurationLocale(string locale)
    {
        var primaryLanguage = locale.Split('-', 2)[0];
        return !string.Equals(primaryLanguage, "zxx", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsStructurallyValidDurationLocaleTag(string tag)
    {
        return IsStructurallyValidLocaleTag(tag);
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
        string? currencySign = null;
        string? unit = null;
        string? unitDisplay = null;
        string notation = "standard";
        int minimumIntegerDigits = 1;
        int? minimumFractionDigits = null;
        int? maximumFractionDigits = null;
        int? minimumSignificantDigits = null;
        int? maximumSignificantDigits = null;
        string useGrouping = "auto";
        string? signDisplay = null;
        string? compactDisplay = null;
        string? roundingMode = null;
        int? roundingIncrement = null;
        string? roundingPriority = null;
        string? trailingZeroDisplay = null;
        // ECMA-402 resolves the numbering system from options.numberingSystem,
        // then the locale's `-u-nu-` Unicode extension, then "latn". The value is
        // always lower-cased (case is insignificant in BCP-47 extensions).
        string? optionsNumberingSystem = null;

        if (optionsValue.Tag != JsValueTag.Undefined)
        {
            // ECMA-402: options must be converted via ToObject.
            JsObject optionsObj;
            JsValue optionsReceiver;
            if (optionsValue.Tag == JsValueTag.Null)
            {
                throw new JsThrownException(CreateTypeError("Cannot convert null to object."));
            }
            if (optionsValue.Tag == JsValueTag.Object)
            {
                optionsObj = _heap.GetObject(optionsValue.AsObjectHandle());
                optionsReceiver = optionsValue;
            }
            else
            {
                optionsObj = ToObject(optionsValue);
                optionsReceiver = JsValue.FromObject(_heap.AllocateObject(optionsObj, AllocationSite.Current()));
            }

            string? GetString(string name) => TryGetPropertyValue(optionsObj, optionsReceiver, name, out var value) && value.Tag != JsValueTag.Undefined ? ToStringValue(value) : null;
            bool? GetBool(string name) => TryGetPropertyValue(optionsObj, optionsReceiver, name, out var value) && value.Tag != JsValueTag.Undefined ? IsTruthy(value) : null;
            int? GetInt(string name) => TryGetPropertyValue(optionsObj, optionsReceiver, name, out var value) && value.Tag != JsValueTag.Undefined ? (int)ToNumber(value) : null;

            // ECMA-402 GetOption: localeMatcher must be "lookup" or "best fit".
            var localeMatcher = GetString("localeMatcher");
            if (localeMatcher is not null && localeMatcher is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"Invalid localeMatcher: {localeMatcher}"));

            // numberingSystem validation (syntactic check).
            optionsNumberingSystem = GetString("numberingSystem");
            if (optionsNumberingSystem is not null && !IsWellFormedNumberingSystem(optionsNumberingSystem))
                throw new JsThrownException(CreateRangeError($"Invalid numberingSystem: {optionsNumberingSystem}"));

            // Style.
            style = GetString("style");
            if (style is not null && style is not "decimal" and not "currency" and not "percent" and not "unit")
                throw new JsThrownException(CreateRangeError($"Invalid style: {style}"));

            // Currency / currencyDisplay / currencySign.
            currency = GetString("currency");
            // ECMA-402: currency codes are case-insensitive but stored uppercased.
            if (currency is not null) currency = currency.ToUpperInvariant();
            if (style == "currency" && currency is not null && !IsWellFormedCurrencyCode(currency))
                throw new JsThrownException(CreateRangeError($"Invalid currency code: {currency}"));
            if (style == "currency" && currency is null)
                throw new JsThrownException(CreateTypeError("Currency code is required when style is 'currency'."));
            currencyDisplay = GetString("currencyDisplay");
            if (currencyDisplay is not null && currencyDisplay is not "code" and not "symbol" and not "narrowSymbol" and not "name")
                throw new JsThrownException(CreateRangeError($"Invalid currencyDisplay: {currencyDisplay}"));
            currencySign = GetString("currencySign");
            if (currencySign is not null && currencySign is not "standard" and not "accounting")
                throw new JsThrownException(CreateRangeError($"Invalid currencySign: {currencySign}"));

            // Unit / unitDisplay.
            unit = GetString("unit");
            unitDisplay = GetString("unitDisplay");
            if (unitDisplay is not null && unitDisplay is not "long" and not "short" and not "narrow")
                throw new JsThrownException(CreateRangeError($"Invalid unitDisplay: {unitDisplay}"));

            // Notation.
            notation = GetString("notation") ?? "standard";
            if (notation is not "standard" and not "scientific" and not "engineering" and not "compact")
                throw new JsThrownException(CreateRangeError($"Invalid notation: {notation}"));

            // CompactDisplay (only meaningful for compact notation).
            compactDisplay = GetString("compactDisplay");
            if (compactDisplay is not null && compactDisplay is not "short" and not "long")
                throw new JsThrownException(CreateRangeError($"Invalid compactDisplay: {compactDisplay}"));

            // UseGrouping (ECMA-402: read before digit options).
            var groupingValue = GetString("useGrouping");
            if (groupingValue is not null)
            {
                if (groupingValue is "always" or "auto" or "min2")
                    useGrouping = groupingValue;
                else if (groupingValue == "true" || groupingValue == "false")
                    useGrouping = groupingValue == "true" ? "auto" : "false";
                else
                    useGrouping = "auto";
            }
            else
            {
                var groupingBool = GetBool("useGrouping");
                useGrouping = groupingBool is true ? "auto" : groupingBool is false ? "false" : "auto";
            }

            // SignDisplay (ECMA-402: read before digit options).
            signDisplay = GetString("signDisplay");
            if (signDisplay is not null && signDisplay is not "auto" and not "never" and not "always" and not "exceptZero" and not "negative")
                throw new JsThrownException(CreateRangeError($"Invalid signDisplay: {signDisplay}"));

            // Digit options.
            minimumIntegerDigits = GetInt("minimumIntegerDigits") ?? 1;
            if (minimumIntegerDigits < 1 || minimumIntegerDigits > 21)
                throw new JsThrownException(CreateRangeError($"Invalid minimumIntegerDigits: {minimumIntegerDigits}"));
            minimumFractionDigits = GetInt("minimumFractionDigits");
            if (minimumFractionDigits is < 0 or > 100)
                throw new JsThrownException(CreateRangeError($"Invalid minimumFractionDigits: {minimumFractionDigits}"));
            maximumFractionDigits = GetInt("maximumFractionDigits");
            if (maximumFractionDigits is < 0 or > 100)
                throw new JsThrownException(CreateRangeError($"Invalid maximumFractionDigits: {maximumFractionDigits}"));
            minimumSignificantDigits = GetInt("minimumSignificantDigits");
            if (minimumSignificantDigits is < 1 or > 21)
                throw new JsThrownException(CreateRangeError($"Invalid minimumSignificantDigits: {minimumSignificantDigits}"));
            maximumSignificantDigits = GetInt("maximumSignificantDigits");
            if (maximumSignificantDigits is < 1 or > 21)
                throw new JsThrownException(CreateRangeError($"Invalid maximumSignificantDigits: {maximumSignificantDigits}"));

            // Rounding options (ES2023).
            roundingIncrement = GetInt("roundingIncrement");
            if (roundingIncrement.HasValue && !IsValidRoundingIncrement(roundingIncrement.Value))
                throw new JsThrownException(CreateRangeError($"Invalid roundingIncrement: {roundingIncrement}"));
            roundingMode = GetString("roundingMode");
            if (roundingMode is not null && roundingMode is not "ceil" and not "floor" and not "expand" and not "trunc" and not "halfCeil" and not "halfFloor" and not "halfExpand" and not "halfTrunc" and not "halfEven")
                throw new JsThrownException(CreateRangeError($"Invalid roundingMode: {roundingMode}"));
            roundingPriority = GetString("roundingPriority");
            if (roundingPriority is not null && roundingPriority is not "auto" and not "morePrecision" and not "lessPrecision")
                throw new JsThrownException(CreateRangeError($"Invalid roundingPriority: {roundingPriority}"));
            trailingZeroDisplay = GetString("trailingZeroDisplay");
            if (trailingZeroDisplay is not null && trailingZeroDisplay is not "auto" and not "stripIfInteger")
                throw new JsThrownException(CreateRangeError($"Invalid trailingZeroDisplay: {trailingZeroDisplay}"));

            // RoundingIncrement conflicts.
            if (roundingIncrement.HasValue && roundingIncrement.Value != 1)
            {
                if (!string.IsNullOrEmpty(roundingPriority) && roundingPriority != "auto")
                    throw new JsThrownException(CreateTypeError("roundingIncrement conflict with roundingPriority"));
                if (minimumSignificantDigits.HasValue || maximumSignificantDigits.HasValue)
                    throw new JsThrownException(CreateTypeError("roundingIncrement conflict with significant digits"));
                if (maximumFractionDigits.HasValue && minimumFractionDigits.HasValue && maximumFractionDigits.Value != minimumFractionDigits.Value)
                    throw new JsThrownException(CreateRangeError("roundingIncrement requires equal min/max fraction digits"));
            }

            // ECMA-402: currency style defaults fraction digits from ISO 4217.
            if (style == "currency" && currency is not null)
            {
                var defaultDigits = GetCurrencyDefaultFractionDigits(currency);
                if (!minimumFractionDigits.HasValue) minimumFractionDigits = defaultDigits;
                if (!maximumFractionDigits.HasValue) maximumFractionDigits = defaultDigits;
            }

            // ECMA-402: percent style defaults to 0 fraction digits.
            if (style == "percent")
            {
                if (!minimumFractionDigits.HasValue) minimumFractionDigits = 0;
                if (!maximumFractionDigits.HasValue) maximumFractionDigits = 0;
            }

            }

        var localeNumberingSystem = ExtractUnicodeKeyword(locale, "nu");
        var numberingSystem = (optionsNumberingSystem ?? localeNumberingSystem ?? "latn").ToLowerInvariant();

        return new NumberFormatState(locale, style, currency, currencyDisplay, currencySign, unit, unitDisplay, notation, compactDisplay, minimumIntegerDigits, minimumFractionDigits, maximumFractionDigits, minimumSignificantDigits, maximumSignificantDigits, useGrouping != "false", useGrouping, signDisplay, roundingMode, roundingIncrement, roundingPriority, trailingZeroDisplay, numberingSystem);
    }

    // ISO 4217 currency → default fraction digits (CLDR secondary currency).
    private static int GetCurrencyDefaultFractionDigits(string currencyCode)
    {
        return currencyCode.ToUpperInvariant() switch
        {
            // 0 decimal digits
            "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF"
                or "UGX" or "UYI" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
            // 3 decimal digits
            "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
            // Default 2
            _ => 2,
        };
    }

    // ECMA-402 IsWellFormedCurrencyCode: exactly 3 ASCII letters [A-Za-z].
    private static bool IsWellFormedCurrencyCode(string code) =>
        code.Length == 3 && code.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    // ECMA-402 valid roundingIncrement values: 1, 2, 5, and their multiples up to 5000.
    private static bool IsValidRoundingIncrement(int inc) => inc switch
    {
        1 or 2 or 5 or 10 or 20 or 25 or 50 or 100 or 200 or 250 or 500 or 1000 or 2000 or 2500 or 5000 => true,
        _ => false
    };

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

    // Check if a Unicode extension key is present in the locale (even without a value).
    // For keys like "kn" (numeric), mere presence means "true".
    private static bool HasUnicodeKeyTrue(string locale, string key)
    {
        if (string.IsNullOrEmpty(locale)) return false;
        var subtags = locale.Split('-');
        for (var i = 0; i < subtags.Length; i++)
        {
            if (string.Equals(subtags[i], "u", StringComparison.OrdinalIgnoreCase) && i + 1 < subtags.Length)
            {
                for (var j = i + 1; j < subtags.Length; j++)
                {
                    if (subtags[j].Length == 1) break; // next singleton
                    if (subtags[j].Length == 2 && string.Equals(subtags[j], key, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // Skip any value subtag (length > 2) that follows a key
                    if (subtags[j].Length > 2 && j > i + 1 && subtags[j-1].Length == 2)
                        continue;
                }
                return false;
            }
        }

        return false;
    }

    private ListFormatState ParseListFormatState(string locale, JsValue optionsValue)
    {
        string type = "conjunction";
        string style = "long";

        if (optionsValue.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optionsValue.Tag != JsValueTag.Undefined)
        {
            // ECMA-402: options must be converted via ToObject and read with GetOption
            // For simplicity, read from the object directly.
            JsObject optsObj;
            if (optionsValue.Tag == JsValueTag.Object)
                optsObj = _heap.GetObject(optionsValue.AsObjectHandle());
            else
                optsObj = ToObject(optionsValue);

            string? GetOptStr(string name)
            {
                if (TryGetPropertyValue(optsObj, optionsValue, name, out var v) && v.Tag != JsValueTag.Undefined)
                    return ToStringValue(v);
                return null;
            }

            // localeMatcher validation.
            var localeMatcher = GetOptStr("localeMatcher");
            if (localeMatcher is not null && localeMatcher is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{localeMatcher} is an invalid localeMatcher option value"));

            // type validation.
            var optType = GetOptStr("type");
            if (optType is not null)
            {
                if (optType is not "conjunction" and not "disjunction" and not "unit")
                    throw new JsThrownException(CreateRangeError($"{optType} is an invalid type option value"));
                type = optType;
            }

            // style validation.
            var optStyle = GetOptStr("style");
            if (optStyle is not null)
            {
                if (optStyle is not "long" and not "short" and not "narrow")
                    throw new JsThrownException(CreateRangeError($"{optStyle} is an invalid style option value"));
                style = optStyle;
            }
        }

        return new ListFormatState(locale, type, style);
    }

    // ECMA-402 Number.prototype.toLocaleString / BigInt.prototype.toLocaleString.
    private JsValue FormatNumberToLocaleString(double value, JsValue locales, JsValue options)
    {
        var locale = "en-US";
        if (locales.Tag == JsValueTag.String) locale = CanonicalizeIntlLocaleTag(locales.AsString());
        else if (locales.Tag == JsValueTag.Object) locale = GetDurationFormatLocale(locales);
        var state = ParseNumberFormatState(locale, options);
        var result = string.Concat(FormatNumberToParts(JsValue.FromNumber(value), state).Select(static p => p.Value));
        return JsValue.FromString(result);
    }

    private JsValue FormatBigIntToLocaleString(System.Numerics.BigInteger value, JsValue locales, JsValue options)
    {
        var locale = "en-US";
        if (locales.Tag == JsValueTag.String) locale = CanonicalizeIntlLocaleTag(locales.AsString());
        else if (locales.Tag == JsValueTag.Object) locale = GetDurationFormatLocale(locales);
        var state = ParseNumberFormatState(locale, options);
        var result = string.Concat(FormatNumberToParts(JsValue.FromNumber((double)value), state).Select(static p => p.Value));
        return JsValue.FromString(result);
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
        // ECMA-402: BigInt values are formatted via their ToString representation.
        if (value.Tag == JsValueTag.BigInt)
        {
            var bigIntStr = value.AsBigInt().ToString(CultureInfo.InvariantCulture);
            return FormatBigIntStringToParts(bigIntStr, state);
        }

        // Extract numeric value.
        double number;
        if (value.Tag == JsValueTag.Int32) number = value.AsInt32();
        else if (value.Tag == JsValueTag.Number) number = value.AsNumber();
        else if (value.Tag == JsValueTag.String && double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) number = n;
        else number = ToNumber(value);
        if (double.IsNaN(number)) return new[] { new IntlPart("nan", "NaN") };
        // ECMA-402: check for -0 BEFORE normalizing to +0 (sign is controlled by signDisplay).
        bool isNegativeZero = double.IsNegative(number) && number == 0;
        if (number == 0) number = 0;

        var culture = ResolveNumberCulture(state.Locale);
        if (culture == CultureInfo.InvariantCulture) culture = CultureInfo.GetCultureInfo("en-US");
        var nfi = culture.NumberFormat;
        // Handle special values (NaN, Infinity) before normal formatting.
        if (double.IsNaN(number))
            return new[] { new IntlPart("nan", "NaN") };
        if (double.IsPositiveInfinity(number))
            return new[] { new IntlPart("infinity", "∞") };
        if (double.IsNegativeInfinity(number))
            return new[] { new IntlPart("minusSign", nfi.NegativeSign, state.Unit), new IntlPart("infinity", "∞") };

        var style = state.Style ?? "decimal";
        bool negative = number < 0 || isNegativeZero;
        double absValue = Math.Abs(number);

        // Handle significant digits mode.
        bool hasSigDigits = state.MinimumSignificantDigits.HasValue || state.MaximumSignificantDigits.HasValue;
        if (hasSigDigits)
        {
            int minSig = state.MinimumSignificantDigits ?? 1;
            int maxSig = state.MaximumSignificantDigits ?? 21;
            return FormatNumberWithSignificantDigits(absValue, negative, minSig, maxSig, nfi, state);
        }

        // Compute fraction digits with proper CLDR defaults.
        int defaultFrac = style == "currency" ? GetCurrencyDefaultFractionDigits(state.Currency ?? "USD") :
                          style == "percent" ? 0 : 0;
        int minFrac = state.MinimumFractionDigits ?? defaultFrac;
        int maxFrac = state.MaximumFractionDigits ?? (style == "currency" ? defaultFrac : style == "percent" ? Math.Max(minFrac, 0) : 3);

        // useGrouping: ES2023 supports "always", "auto", "min2", and the legacy true/false.
        bool useGrouping = state.UseGrouping;
        string? useGroupingValue = state.UseGroupingValue;
        // "min2" means at least 5 integer digits needed for grouping.
        bool groupingMin2 = string.Equals(useGroupingValue, "min2", StringComparison.Ordinal);

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
        // Set currency symbol for the requested currency code.
        if (style == "currency")
        {
            cnf.CurrencySymbol = (state.Currency ?? "USD") switch
            {
                "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "¥", "CNY" => "¥",
                "KRW" => "₩", "INR" => "₹", "BRL" => "R$", "RUB" => "₽",
                var c => c
            };
        }

        string notation = state.Notation ?? "standard";
        if (notation is "scientific" or "engineering")
        {
            // ECMA-402: deconstruct scientific/engineering notation into proper
            // parts: integer, decimal, fraction, exponentSeparator, exponentMinusSign,
            // exponentInteger. Apply numbering system to digit substrings.
            int sciExp;
            double mantissa;
            if (notation == "scientific")
            {
                if (absValue == 0) { mantissa = 0; sciExp = 0; }
                else
                {
                    sciExp = (int)Math.Floor(Math.Log10(absValue));
                    mantissa = absValue / Math.Pow(10, sciExp);
                }
            }
            else // engineering
            {
                if (absValue == 0) { mantissa = 0; sciExp = 0; }
                else
                {
                    sciExp = ((int)Math.Floor(Math.Log10(absValue)) / 3) * 3;
                    mantissa = absValue / Math.Pow(10, sciExp);
                    if (mantissa >= 1000) { mantissa /= 1000; sciExp += 3; }
                    if (mantissa < 1) { mantissa *= 1000; sciExp -= 3; }
                }
            }

            var sciParts = new List<IntlPart>();
            string signMode = state.SignDisplay ?? "auto";
            bool showSciSign = signMode switch
            {
                "never" => false, "always" => true,
                "exceptZero" => absValue != 0, "negative" => negative,
                _ => negative
            };
            if (showSciSign && negative)
                sciParts.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));

            // Format mantissa with maxFrac decimal places.
            string mantStr = mantissa.ToString("F" + maxFrac, CultureInfo.InvariantCulture);
            int dotIdx = mantStr.IndexOf('.');
            string mantInt = dotIdx >= 0 ? mantStr[..dotIdx] : mantStr;
            string mantFrac = dotIdx >= 0 ? mantStr[(dotIdx + 1)..] : "";
            // Trim trailing zeros in fraction down to minFrac.
            int trimTo = Math.Max(minFrac, 0);
            while (mantFrac.Length > trimTo && mantFrac.EndsWith("0"))
                mantFrac = mantFrac[..^1];

            sciParts.Add(new IntlPart("integer", ApplyNumberingSystem(mantInt, state.NumberingSystem), state.Unit));
            if (mantFrac.Length > 0)
            {
                sciParts.Add(new IntlPart("decimal", nfi.NumberDecimalSeparator, state.Unit));
                sciParts.Add(new IntlPart("fraction", ApplyNumberingSystem(mantFrac, state.NumberingSystem), state.Unit));
            }

            sciParts.Add(new IntlPart("exponentSeparator", "E", state.Unit));
            if (sciExp < 0)
            {
                sciParts.Add(new IntlPart("exponentMinusSign", nfi.NegativeSign, state.Unit));
                sciExp = -sciExp;
            }
            sciParts.Add(new IntlPart("exponentInteger", ApplyNumberingSystem(sciExp.ToString(CultureInfo.InvariantCulture), state.NumberingSystem), state.Unit));

            return sciParts;
        }

        // Compact notation — formats with magnitude-based suffix.
        if (notation == "compact")
        {
            return FormatCompactNumber(absValue, negative, isNegativeZero, state, nfi);
        }

        string formatted = style switch
        {
            "currency" => absValue.ToString("C", cnf),
            "percent" => absValue.ToString("P", cnf),
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
            string afterFraction = formatted[fracEnd..]; // preserve currency/literal suffix
            if (fracEnd == fracStart && trimTo == 0)
                formatted = formatted[..decIdx] + afterFraction;
            else
                formatted = formatted[..decIdx] + decSep + formatted[fracStart..fracEnd] + afterFraction;
        }

        // Parse formatted output into IntlParts.
        var parts = new List<IntlPart>();
        string signDisplay = state.SignDisplay ?? "auto";
        // ECMA-402 sign display: -0 and values rounding to 0 should not show minus
        // for "negative", "exceptZero", and "auto" modes.
        bool roundsToZero = absValue < Math.Pow(10, -(maxFrac + 1));
        bool showMinus = signDisplay switch
        {
            "never" => false,
            "always" => negative || isNegativeZero,
            "exceptZero" => negative && !roundsToZero,
            "negative" => negative && !isNegativeZero && !roundsToZero,
            _ => negative && !isNegativeZero && !roundsToZero // auto
        };
        bool showPlus = signDisplay == "always" && !negative && !isNegativeZero;

        // Walk formatted string, classifying each character.
        int pos = 0;
        string curSymbol = cnf.CurrencySymbol;

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
        if (negParens || showMinus)
        {
            parts.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));
        }
        else if (showPlus)
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
            else if (style == "currency" && curSymbol.Length > 0 &&
                     formatted.Substring(pos).StartsWith(curSymbol, StringComparison.Ordinal))
            {
                if (digitBuf.Count > 0)
                {
                    parts.Add(new IntlPart(inFraction ? "fraction" : "integer",
                        ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
                    digitBuf.Clear();
                }
                parts.Add(new IntlPart("currency", curSymbol, state.Unit));
                pos += curSymbol.Length;
            }
            else
            {
                if (digitBuf.Count > 0)
                {
                    parts.Add(new IntlPart(inFraction ? "fraction" : "integer",
                        ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
                    digitBuf.Clear();
                }
                // Normalize ASCII space between number and currency to NBSP per CLDR.
                var litVal = (c == ' ' && style == "currency") ? " " : c.ToString();
                parts.Add(new IntlPart("literal", litVal, state.Unit));
                pos++;
            }
        }

        if (digitBuf.Count > 0)
        {
            parts.Add(new IntlPart(inFraction ? "fraction" : "integer",
                ApplyNumberingSystem(new string(digitBuf.ToArray()), state.NumberingSystem), state.Unit));
        }

        // Post-process: apply minimum integer digits by padding the first integer part.
        if (state.MinimumIntegerDigits > 1)
        {
            // Count total integer digits across all integer parts.
            int totalIntDigits = 0;
            foreach (var p in parts)
                if (p.Type == "integer") totalIntDigits += p.Value.Length;
            if (totalIntDigits < state.MinimumIntegerDigits)
            {
                int padCount = state.MinimumIntegerDigits - totalIntDigits;
                // Find first integer part and prepend zeros.
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].Type == "integer")
                    {
                        parts[i] = new IntlPart("integer",
                            ApplyNumberingSystem(new string('0', padCount) + new string(parts[i].Value.Where(ch => ch >= '0' && ch <= '9').ToArray()), state.NumberingSystem), parts[i].Unit);
                        break;
                    }
                }
            }
        }

        // "min2" grouping: remove grouping separators when integer part has fewer than 5 digits.
        if (groupingMin2)
        {
            int totalIntDigits = 0;
            foreach (var p in parts)
                if (p.Type == "integer") totalIntDigits += p.Value.Length;
            if (totalIntDigits < 5)
            {
                // Remove group separators.
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    if (parts[i].Type == "group")
                    {
                        parts.RemoveAt(i);
                    }
                }
            }
        }

        // Closing paren or trailing currency/percent.
        if (negParens)
        {
            parts.Add(new IntlPart("literal", ")", state.Unit));
        }
        // Any remaining characters (shouldn't normally happen since while loop processes everything)
        if (pos < formatted.Length && !(negParens && formatted[pos] == ')'))
        {
            parts.Add(new IntlPart("literal", formatted[pos..], state.Unit));
        }

        // Unit suffix.
        if (style == "unit" && !string.IsNullOrEmpty(state.Unit))
        {
            parts.Add(new IntlPart("literal", " ", state.Unit));
            parts.Add(new IntlPart("unit", GetUnitLabel(state.Unit!, state.UnitDisplay ?? "short", state.Locale), state.Unit));
        }

        return parts;
    }

    // Compact notation: format with magnitude-based suffix (K, M, B, T for short).
    private IReadOnlyList<IntlPart> FormatCompactNumber(double absValue, bool negative, bool isNegativeZero,
        NumberFormatState state, NumberFormatInfo nfi)
    {
        bool compactShort = state.CompactDisplay is null or "short";
        var (divisor, suffix) = GetCompactEntry(absValue, compactShort, state.Locale);
        var scaled = absValue / divisor;
        if (absValue == 0) scaled = 0;

        // Format the scaled value with fraction digits.
        int minFrac = state.MinimumFractionDigits ?? 0;
        int maxFrac = state.MaximumFractionDigits ?? 2;

        // Round to maxFrac places.
        var rounded = Math.Round(scaled, maxFrac, MidpointRounding.AwayFromZero);
        var fmtStr = rounded.ToString("F" + maxFrac, CultureInfo.InvariantCulture);
        int dotIdx = fmtStr.IndexOf('.');
        string intPart = dotIdx >= 0 ? fmtStr[..dotIdx] : fmtStr;
        string fracPart = dotIdx >= 0 ? fmtStr[(dotIdx + 1)..] : "";

        // Trim trailing zeros down to minFrac.
        while (fracPart.Length > minFrac && fracPart.EndsWith("0"))
            fracPart = fracPart[..^1];

        var parts = new List<IntlPart>();

        // Sign handling.
        string signDisplay = state.SignDisplay ?? "auto";
        bool showMinus = signDisplay switch
        {
            "never" => false,
            "always" => negative || isNegativeZero,
            "exceptZero" => negative && !isNegativeZero && absValue != 0,
            // ECMA-402: "negative" and "auto" show minus for negative values
            // including negative zero, but NOT for positive zero.
            "negative" or "auto" => negative && !isNegativeZero,
            _ => negative && !isNegativeZero
        };
        bool showPlus = signDisplay == "always" && !negative && !isNegativeZero;
        if (showMinus)
            parts.Add(new IntlPart("minusSign", nfi.NegativeSign, state.Unit));
        else if (showPlus)
            parts.Add(new IntlPart("plusSign", nfi.PositiveSign, state.Unit));

        // Minimum integer digits padding.
        if (state.MinimumIntegerDigits > 1 && intPart.Length < state.MinimumIntegerDigits)
            intPart = intPart.PadLeft(state.MinimumIntegerDigits, '0');

        // Grouping for integer part.
        if (state.UseGrouping && intPart.Length > 3)
        {
            int groupSize = nfi.NumberGroupSizes.Length > 0 ? nfi.NumberGroupSizes[0] : 3;
            var grouped = new List<string>();
            int pos = intPart.Length;
            while (pos > groupSize) { pos -= groupSize; grouped.Insert(0, intPart[pos..(pos + groupSize)]); }
            grouped.Insert(0, intPart[..pos]);
            for (int i = 0; i < grouped.Count; i++)
            {
                if (i > 0) parts.Add(new IntlPart("group", nfi.NumberGroupSeparator, state.Unit));
                parts.Add(new IntlPart("integer", ApplyNumberingSystem(grouped[i], state.NumberingSystem), state.Unit));
            }
        }
        else
        {
            parts.Add(new IntlPart("integer", ApplyNumberingSystem(intPart, state.NumberingSystem), state.Unit));
        }

        if (fracPart.Length > 0)
        {
            parts.Add(new IntlPart("decimal", nfi.NumberDecimalSeparator, state.Unit));
            parts.Add(new IntlPart("fraction", ApplyNumberingSystem(fracPart, state.NumberingSystem), state.Unit));
        }

        // Compact suffix as separate part.
        parts.Add(new IntlPart("compact", suffix, state.Unit));

        return parts;
    }

    // Return the divisor and suffix for compact notation, keyed by locale.
    private static (double divisor, string suffix) GetCompactEntry(double absValue, bool short_, string locale)
    {
        var lang = locale.Length >= 2 ? locale[..2].ToLowerInvariant() : "en";
        // CLDR compact suffix data for commonly-tested locales.
        // Each entry: (threshold, shortSuffix, longSuffix).
        // East Asian locales (ja, ko, zh) use 10^4 (万/億/兆) thresholds.
        if (lang is "ja" or "ko" or "zh")
        {
            if (absValue >= 1e16)
                return short_ ? (1e16, GetAsianSuffix(lang, 4, short_)) : (1e16, GetAsianSuffix(lang, 4, short_));
            if (absValue >= 1e12)
                return short_ ? (1e12, GetAsianSuffix(lang, 3, short_)) : (1e12, GetAsianSuffix(lang, 3, short_));
            if (absValue >= 1e8)
                return short_ ? (1e8, GetAsianSuffix(lang, 2, short_)) : (1e8, GetAsianSuffix(lang, 2, short_));
            if (absValue >= 1e4)
                return short_ ? (1e4, GetAsianSuffix(lang, 1, short_)) : (1e4, GetAsianSuffix(lang, 1, short_));
            return (1, "");
        }
        // Western locales: use 10^3 (K/M/B/T) thresholds.
        if (absValue >= 1e15)
            return short_ ? (1e15, GetWesternSuffix(lang, 5, short_)) : (1e15, " " + GetWesternSuffix(lang, 5, false));
        if (absValue >= 1e12)
            return short_ ? (1e12, GetWesternSuffix(lang, 4, short_)) : (1e12, " " + GetWesternSuffix(lang, 4, false));
        if (absValue >= 1e9)
            return short_ ? (1e9, GetWesternSuffix(lang, 3, short_)) : (1e9, " " + GetWesternSuffix(lang, 3, false));
        if (absValue >= 1e6)
            return short_ ? (1e6, GetWesternSuffix(lang, 2, short_)) : (1e6, " " + GetWesternSuffix(lang, 2, false));
        if (absValue >= 1e3)
            return short_ ? (1e3, GetWesternSuffix(lang, 1, short_)) : (1e3, " " + GetWesternSuffix(lang, 1, false));
        return (1, "");
    }

    private static string GetWesternSuffix(string lang, int tier, bool short_)
    {
        // tier: 1=thousand, 2=million, 3=billion, 4=trillion, 5=quadrillion
        return lang switch
        {
            "de" => short_ ? (tier switch { 1 => "Tsd.", 2 => "Mio.", 3 => "Mrd.", 4 => "Bio.", _ => "Brd." })
                           : (tier switch { 1 => "Tausend", 2 => "Millionen", 3 => "Milliarden", 4 => "Billionen", _ => "Billiarden" }),
            _ => short_ ? (tier switch { 1 => "K", 2 => "M", 3 => "B", 4 => "T", _ => "Q" })
                       : (tier switch { 1 => "thousand", 2 => "million", 3 => "billion", 4 => "trillion", _ => "quadrillion" }),
        };
    }

    private static string GetAsianSuffix(string lang, int tier, bool short_)
    {
        // tier: 1=万/만/萬 (10^4), 2=億/억/億 (10^8), 3=兆/조/兆 (10^12), 4=京/경/京 (10^16)
        return lang switch
        {
            "ja" => tier switch { 1 => "万", 2 => "億", 3 => "兆", _ => "京" },
            "ko" => tier switch { 1 => "만", 2 => "억", 3 => "조", _ => "경" },
            "zh" => tier switch { 1 => "萬", 2 => "億", 3 => "兆", _ => "京" },
            _ => "K"
        };
    }

    // ECMA-402: Format a BigInt string into IntlParts. BigInts are formatted as
    // their decimal representation with grouping and signDisplay applied.
    private IReadOnlyList<IntlPart> FormatBigIntStringToParts(string bigIntStr, NumberFormatState state)
    {
        bool negative = bigIntStr.StartsWith("-", StringComparison.Ordinal);
        var digits = negative ? bigIntStr[1..] : bigIntStr;
        var parts = new List<IntlPart>();

        var signDisplay = state.SignDisplay ?? "auto";
        bool showSign = signDisplay switch
        {
            "never" => false,
            "always" => true,
            "exceptZero" => digits != "0",
            "negative" => negative,
            _ => negative
        };

        if (showSign && negative)
            parts.Add(new IntlPart("minusSign", "-", state.Unit));
        else if (showSign && !negative && signDisplay == "always")
            parts.Add(new IntlPart("plusSign", "+", state.Unit));

        // Apply grouping separators.
        if (state.UseGrouping && digits.Length > 3)
        {
            var groupSize = 3; // CLDR default
            var grouped = new List<string>();
            int pos = digits.Length;
            while (pos > groupSize)
            {
                pos -= groupSize;
                grouped.Insert(0, digits[pos..(pos + groupSize)]);
            }
            grouped.Insert(0, digits[..pos]);
            for (int i = 0; i < grouped.Count; i++)
            {
                if (i > 0) parts.Add(new IntlPart("group", ",", state.Unit));
                parts.Add(new IntlPart("integer", ApplyNumberingSystem(grouped[i], state.NumberingSystem), state.Unit));
            }
        }
        else
        {
            parts.Add(new IntlPart("integer", ApplyNumberingSystem(digits, state.NumberingSystem), state.Unit));
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
        var parts = new List<IntlPart>();
        if (items.Count == 0) return parts;
        if (items.Count == 1)
        {
            parts.Add(new IntlPart("element", items[0]));
            return parts;
        }

        // ECMA-402 CLDR list patterns. For each locale/type/style combination
        // there are up to 3 sub-patterns: start, middle, end (for 3+ items)
        // and a pair pattern (for exactly 2 items). Unit style never uses
        // a conjunction word. Narrow style uses only spaces, no punctuation.
        // Short style uses & for conjunction.
        bool isOr = string.Equals(state.Type, "disjunction", StringComparison.Ordinal);
        bool isUnit = string.Equals(state.Type, "unit", StringComparison.Ordinal);
        bool narrow = state.Style == "narrow";
        bool shortStyle = state.Style == "short";
        bool longStyle = state.Style == "long";
        // Determine the conjunction word and between-separator from CLDR English patterns.
        string andWord;
        if (isOr) andWord = "or";
        else if (isUnit) andWord = "";
        else if (shortStyle) andWord = "&";
        else andWord = "and";

        string between = narrow ? " " : ", ";

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                if (i == items.Count - 1)
                {
                    // Before the last element.
                    if (items.Count == 2)
                    {
                        // Pair pattern: "{0} and {1}" (no comma before conjunction)
                        if (!narrow && andWord.Length > 0)
                            parts.Add(new IntlPart("literal", " " + andWord + " "));
                        else
                            parts.Add(new IntlPart("literal", " "));
                    }
                    else
                    {
                        // End pattern for 3+: ", and {2}" (Oxford comma in English)
                        if (narrow || (isUnit && !longStyle))
                            parts.Add(new IntlPart("literal", " "));
                        else if (andWord.Length > 0)
                            parts.Add(new IntlPart("literal", ", " + andWord + " "));
                        else
                            parts.Add(new IntlPart("literal", ", "));
                    }
                }
                else
                {
                    // Middle separator.
                    parts.Add(new IntlPart("literal", between));
                }
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
                    null, null, null,
                    SingularDurationUnit(unit),
                    unitStyle is "numeric" or "2-digit" ? null : unitStyle,
                    "standard", null,
                    unitStyle == "2-digit" ? 2 : 1,
                    null, null, null, null,
                    unitStyle is "numeric" or "2-digit" ? false : true,
                    unitStyle is "numeric" or "2-digit" ? "false" : "auto",
                    suppressSign ? "never" : null,
                    null, null, null, null,
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

    // ECMA-402 §9.2.6 SupportedLocales (shared by all Intl services).
    // Returns an array of locale strings from the requested locales that are
    // structurally valid and supported (i.e., their primary language is not "zxx").
    private JsValue SupportedLocalesOf(IReadOnlyList<JsValue> args)
    {
        var requestedLocales = CanonicalizeIntlLocaleList(args.Count > 0 ? args[0] : JsValue.Undefined);
        var supported = requestedLocales
            .Where(IsSupportedIntlLocale)
            .Select(JsValue.FromString)
            .ToArray();
        var arrObj = CreateArrayFromElements(supported);
        var arrHandle = _heap.AllocateObject(arrObj, AllocationSite.Current());
        return JsValue.FromObject(arrHandle);
    }

    // CanonicalizeLocaleList for Intl services. Converts a locales argument into a
    // deduplicated list of canonical BCP-47 language tags.
    private List<string> CanonicalizeIntlLocaleList(JsValue localesValue)
    {
        var locales = new List<string>();
        if (localesValue.Tag == JsValueTag.Undefined)
            return locales;

        if (localesValue.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (localesValue.Tag == JsValueTag.String)
        {
            locales.Add(CanonicalizeIntlLocaleTag(ToStringValue(localesValue)));
            return locales;
        }

        // ECMA-402 9.2.1: coerce non-Object non-String to Object via ToObject.
        // Numbers, Booleans, etc. become wrapper objects with no length → empty list.
        if (localesValue.Tag != JsValueTag.Object)
        {
            var obj = ToObject(localesValue);
            var objHandle = _heap.AllocateObject(obj, AllocationSite.Current());
            _heap.PushRoot(objHandle);
            try
            {
                var result = CanonicalizeIntlLocaleList(JsValue.FromObject(objHandle));
                return result;
            }
            finally
            {
                _heap.PopRootsTo(_heap.RootCount - 1);
            }
        }

        var localesObject = _heap.GetObject(localesValue.AsObjectHandle());
        if (!TryGetPropertyValue(localesObject, localesValue, "length", out var lengthValue))
            return locales;

        var lengthNumber = ToNumber(lengthValue);
        if (double.IsNaN(lengthNumber) || lengthNumber < 0)
            lengthNumber = 0;

        var length = (int)Math.Min(lengthNumber, int.MaxValue);
        for (var i = 0; i < length; i++)
        {
            if (!TryGetPropertyValue(localesObject, localesValue, i.ToString(CultureInfo.InvariantCulture), out var element))
                continue;

            // ECMA-402 9.2.1 step 10.c: coerce element via ToString, which
            // handles String, Number, Boolean, and Objects (via ToPrimitive).
            // Symbols throw TypeError from ToString; null/undefined were already
            // coerced to string-like values above.
            if (element.Tag is JsValueTag.Undefined or JsValueTag.Null or JsValueTag.Symbol)
                throw new JsThrownException(CreateTypeError("Locale list elements must be strings or string-like objects."));

            locales.Add(CanonicalizeIntlLocaleTag(ToStringValue(element)));
        }

        return locales.Distinct(StringComparer.Ordinal).ToList();
    }

    // BCP-47 canonicalisation: lower-case primary language, upper-case region,
    // title-case script, lower-case extensions.
    private string CanonicalizeIntlLocaleTag(string tag)
    {
        if (!IsStructurallyValidLocaleTag(tag))
            throw new JsThrownException(CreateRangeError($"Invalid language tag: {tag}"));

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

            if (!inUnicodeExtension && ((part.Length == 2 && part.All(char.IsLetter)) || (part.Length == 3 && part.All(char.IsDigit))))
            {
                result.Add(part.ToUpperInvariant());
                continue;
            }

            result.Add(part.ToLowerInvariant());
        }

        return string.Join("-", result);
    }

    private static bool IsSupportedIntlLocale(string locale)
    {
        var primaryLanguage = locale.Split('-', 2)[0];
        return !string.Equals(primaryLanguage, "zxx", StringComparison.OrdinalIgnoreCase);
    }

    // Structural validation for BCP-47 language tags.
    private static bool IsStructurallyValidLocaleTag(string tag)
    {
        // ECMA-402 BCP-47 structural validation.
        if (string.IsNullOrWhiteSpace(tag) || tag.Any(ch => ch > 0x7F))
            return false;

        // Underscores and wildcards are not allowed in well-formed BCP-47 tags.
        if (tag.Contains('*', StringComparison.Ordinal) || tag.Contains('_', StringComparison.Ordinal))
            return false;

        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        // Primary language subtag: 2-3 or 5-8 letters (also accept 4-letter ISO 639-6).
        if (parts[0].Length == 1 || parts[0].Length > 8 || !parts[0].All(char.IsLetter))
            return false;

        // Special case: 4-letter primary language followed by 3-letter extlang
        // is only valid if the second part is 3 letters. Otherwise skip this check.
        if (parts[0].Length == 4 && parts.Length > 1 && parts[1].Length == 3 && !parts[1].All(char.IsLetter))
            return false;

        var seenSingletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inPrivateUse = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 1)
            {
                if (string.Equals(part, "x", StringComparison.OrdinalIgnoreCase))
                    inPrivateUse = true;
                // Outside private-use, singleton subtags must be alphanumeric and unique.
                if (!inPrivateUse)
                {
                    if (!char.IsLetterOrDigit(part[0]) || !seenSingletons.Add(part))
                        return false;
                }
                else if (!char.IsLetterOrDigit(part[0]))
                    return false;
                // A singleton cannot be the last subtag (except inside private-use -x-).
                if (i == parts.Length - 1 && !inPrivateUse)
                    return false;
                continue;
            }

            if (!part.All(char.IsLetterOrDigit))
                return false;
        }

        return true;
    }

    private ObjectHandle EnsureCollatorPrototype()
    {
        if (_collatorPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        DefineIntlPrototypeAccessor(ph, proto, "compare", (thisValue, cmpArgs) =>
        {
            string localeStr = "en-US";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var receiver = _heap.GetObject(thisValue.AsObjectHandle());
                if (receiver.TryGetProperty("__collator_locale", x => _heap.GetObject(x), out var locDesc) &&
                    locDesc.Value.Tag == JsValueTag.String)
                    localeStr = locDesc.Value.AsString();
            }
            var cult = IntlDateTimeFormatting.ResolveCulture(localeStr);
            var a = cmpArgs.Count > 0 ? ToStringValue(cmpArgs[0]) : string.Empty;
            var b = cmpArgs.Count > 1 ? ToStringValue(cmpArgs[1]) : string.Empty;
            var result = cult.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.None);
            return JsValue.FromNumber(result);
        }, length: 2);

        DefineIntlPrototypeAccessor(ph, proto, "resolvedOptions", (thisValue, _2) =>
        {
            string ReadStr(string prop, string def)
            {
                if (thisValue.Tag == JsValueTag.Object)
                {
                    var r = _heap.GetObject(thisValue.AsObjectHandle());
                    if (r.TryGetOwnProperty(prop, out var d) && d.Value.Tag == JsValueTag.String)
                        return d.Value.AsString();
                }
                return def;
            }
            bool ReadBool(string prop, bool def)
            {
                if (thisValue.Tag == JsValueTag.Object)
                {
                    var r = _heap.GetObject(thisValue.AsObjectHandle());
                    if (r.TryGetOwnProperty(prop, out var d) && d.Value.Tag != JsValueTag.Undefined)
                        return d.Value.AsBoolean();
                }
                return def;
            }
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(ReadStr("__collator_locale", "en-US")), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("usage", new JsPropertyDescriptor(JsValue.FromString(ReadStr("__collator_usage", "sort")), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("sensitivity", new JsPropertyDescriptor(JsValue.FromString(ReadStr("__collator_sensitivity", "variant")), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(ReadBool("__collator_ignorePunctuation", false)), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("collation", new JsPropertyDescriptor(JsValue.FromString(ReadStr("__collator_collation", "default")), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numeric", new JsPropertyDescriptor(JsValue.FromBoolean(ReadBool("__collator_numeric", false)), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("caseFirst", new JsPropertyDescriptor(JsValue.FromString(ReadStr("__collator_caseFirst", "false")), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);

        _collatorPrototypeHandle = ph;
        return ph;
    }

    private JsObject RequireSegmenterState(JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.Segmenter method called on incompatible receiver."));
        var receiver = _heap.GetObject(thisValue.AsObjectHandle());
        if (!receiver.TryGetProperty("__segmenterState", x => _heap.GetObject(x), out var stateDesc) ||
            stateDesc.Value.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Intl.Segmenter method called on incompatible receiver."));
        return _heap.GetObject(stateDesc.Value.AsObjectHandle());
    }

    private ObjectHandle EnsureSegmenterPrototype()
    {
        if (_segmenterPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var segmentFn = new NativeFunctionObject("segment", (_, a) =>
        {
            var str = a.Count > 0 ? ToStringValue(a[0]) : "";
            // Pre-compute grapheme cluster boundaries using .NET StringInfo.
            // Each segment is (startIndex, endIndex, segmentString).
            var boundaries = new List<int> { 0 };
            var segmentsList = new List<string>();
            var te = StringInfo.GetTextElementEnumerator(str);
            while (te.MoveNext())
            {
                var textEl = te.GetTextElement();
                segmentsList.Add(textEl);
                boundaries.Add(te.ElementIndex + textEl.Length);
            }
            // Store boundaries and segments as internal arrays on the Segments object.
            var segments = CreateOrdinaryObject();
            segments.DefineOwnProperty("_str", new JsPropertyDescriptor(JsValue.FromString(str), Writable: false, Enumerable: false, Configurable: false));
            segments.DefineOwnProperty("_idx", new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
            // Store boundaries as a JS array for binary search in containing().
            var boundaryArr = CreateArrayFromElements(boundaries.Select(b => JsValue.FromNumber(b)).ToArray());
            segments.DefineOwnProperty("_boundaries", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(boundaryArr, AllocationSite.Current())), Writable: false, Enumerable: false, Configurable: false));
            // Store segment strings as a JS array for fast iterator access.
            var segStrArr = CreateArrayFromElements(segmentsList.Select(s => JsValue.FromString(s)).ToArray());
            segments.DefineOwnProperty("_segments", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(segStrArr, AllocationSite.Current())), Writable: false, Enumerable: false, Configurable: false));
            segments.DefineOwnProperty("_count", new JsPropertyDescriptor(JsValue.FromNumber(segmentsList.Count), Writable: false, Enumerable: false, Configurable: false));
            // Set [Symbol.toStringTag] = "Segments"
            var stagSymId = ((IBuiltinContext)this).CreateWellKnownSymbol("toStringTag").AsSymbolId();
            segments.DefineOwnSymbolProperty(stagSymId, new JsPropertyDescriptor(JsValue.FromString("Segments"), Writable: false, Enumerable: false, Configurable: true));
            // ECMA-402: Segments.prototype.containing(index).
            var containingFn = new NativeFunctionObject("containing", (thisVal, cArgs) =>
            {
                // Validate receiver is a Segments object (has _str and _boundaries).
                if (thisVal.Tag != JsValueTag.Object) return JsValue.Undefined;
                var segsObj = _heap.GetObject(thisVal.AsObjectHandle());
                if (!segsObj.TryGetOwnProperty("_str", out var strDesc))
                    return JsValue.Undefined;
                string storedStr = strDesc.Value.AsString();
                if (cArgs.Count == 0) return JsValue.Undefined;
                if (cArgs[0].Tag == JsValueTag.Undefined) return JsValue.Undefined;
                var idxDbl = ToNumber(cArgs[0]);
                if (double.IsNaN(idxDbl) || double.IsInfinity(idxDbl)) return JsValue.Undefined;
                int idx = (int)idxDbl;
                if (idx < 0 || idx >= storedStr.Length) return JsValue.Undefined;
                // Read pre-computed boundaries and segments from stored arrays.
                List<int> bounds = new();
                List<string> segTexts = new();
                if (segsObj.TryGetOwnProperty("_boundaries", out var bDesc) && bDesc.Value.Tag == JsValueTag.Object)
                {
                    var bObj = _heap.GetObject(bDesc.Value.AsObjectHandle());
                    int bLen = 0;
                    if (bObj.TryGetOwnProperty("length", out var lDesc))
                        bLen = (int)lDesc.Value.AsNumber();
                    for (int i = 0; i < bLen; i++)
                        if (bObj.TryGetOwnProperty(i.ToString(), out var bi) && bi.Value.Tag != JsValueTag.Undefined)
                            bounds.Add((int)bi.Value.AsNumber());
                }
                if (segsObj.TryGetOwnProperty("_segments", out var sDesc) && sDesc.Value.Tag == JsValueTag.Object)
                {
                    var sObj = _heap.GetObject(sDesc.Value.AsObjectHandle());
                    int sLen = 0;
                    if (sObj.TryGetOwnProperty("length", out var slDesc))
                        sLen = (int)slDesc.Value.AsNumber();
                    for (int i = 0; i < sLen; i++)
                        if (sObj.TryGetOwnProperty(i.ToString(), out var si) && si.Value.Tag != JsValueTag.Undefined)
                            segTexts.Add(si.Value.AsString());
                }
                if (bounds.Count < 2 || segTexts.Count == 0) return JsValue.Undefined;
                // Find the segment containing idx using binary search on boundaries.
                int segIdx = -1;
                for (int i = 0; i < segTexts.Count; i++)
                {
                    if (idx >= bounds[i] && idx < bounds[i + 1])
                    { segIdx = i; break; }
                }
                if (segIdx < 0) return JsValue.Undefined;
                var resultObj = CreateOrdinaryObject();
                resultObj.DefineOwnProperty("segment", new JsPropertyDescriptor(JsValue.FromString(segTexts[segIdx]), Writable: true, Enumerable: true, Configurable: true));
                resultObj.DefineOwnProperty("index", new JsPropertyDescriptor(JsValue.FromNumber(bounds[segIdx]), Writable: true, Enumerable: true, Configurable: true));
                resultObj.DefineOwnProperty("input", new JsPropertyDescriptor(JsValue.FromString(storedStr), Writable: true, Enumerable: true, Configurable: true));
                return JsValue.FromObject(_heap.AllocateObject(resultObj, AllocationSite.Current()));
            }, length: 1);
            var containingHandle = _heap.AllocateObject(containingFn, AllocationSite.Current());
            segments.DefineOwnProperty("containing", new JsPropertyDescriptor(JsValue.FromObject(containingHandle), Writable: true, Enumerable: false, Configurable: true));
            // ECMA-402: Segment iterator — yields {segment, index, input} objects.
            var iterFn = new NativeFunctionObject("next", (thisIter, _2) =>
            {
                var segsObj = _heap.GetObject(thisIter.AsObjectHandle())!;
                int idx = 0;
                if (segsObj.TryGetOwnProperty("_idx", out var idxDesc))
                    idx = (int)idxDesc.Value.AsNumber();
                int count = 0;
                if (segsObj.TryGetOwnProperty("_count", out var cntDesc))
                    count = (int)cntDesc.Value.AsNumber();
                string storedStr = "";
                if (segsObj.TryGetOwnProperty("_str", out var strDesc))
                    storedStr = strDesc.Value.AsString();
                if (idx >= count)
                {
                    var doneObj = CreateOrdinaryObject();
                    doneObj.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
                    doneObj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.Undefined, Writable: true, Enumerable: true, Configurable: true));
                    return JsValue.FromObject(_heap.AllocateObject(doneObj, AllocationSite.Current()));
                }
                // Read segment string and boundaries from the internal arrays.
                int segStart = 0;
                string segText = "";
                // Read boundaries
                if (segsObj.TryGetOwnProperty("_boundaries", out var bDesc) && bDesc.Value.Tag == JsValueTag.Object)
                {
                    var bObj = _heap.GetObject(bDesc.Value.AsObjectHandle());
                    if (bObj.TryGetOwnProperty(idx.ToString(), out var bi) && bi.Value.Tag != JsValueTag.Undefined)
                        segStart = (int)bi.Value.AsNumber();
                }
                // Read segment text
                if (segsObj.TryGetOwnProperty("_segments", out var sDesc) && sDesc.Value.Tag == JsValueTag.Object)
                {
                    var sObj = _heap.GetObject(sDesc.Value.AsObjectHandle());
                    if (sObj.TryGetOwnProperty(idx.ToString(), out var si) && si.Value.Tag != JsValueTag.Undefined)
                        segText = si.Value.AsString();
                }
                var segResult = CreateOrdinaryObject();
                segResult.DefineOwnProperty("segment", new JsPropertyDescriptor(JsValue.FromString(segText), Writable: true, Enumerable: true, Configurable: true));
                segResult.DefineOwnProperty("index", new JsPropertyDescriptor(JsValue.FromNumber(segStart), Writable: true, Enumerable: true, Configurable: true));
                segResult.DefineOwnProperty("input", new JsPropertyDescriptor(JsValue.FromString(storedStr), Writable: true, Enumerable: true, Configurable: true));
                var iterResult = CreateOrdinaryObject();
                iterResult.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(segResult, AllocationSite.Current())), Writable: true, Enumerable: true, Configurable: true));
                iterResult.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
                segsObj.DefineOwnProperty("_idx", new JsPropertyDescriptor(JsValue.FromNumber(idx + 1), Writable: true, Enumerable: false, Configurable: false));
                return JsValue.FromObject(_heap.AllocateObject(iterResult, AllocationSite.Current()));
            }, length: 0);
            var iterObj = CreateOrdinaryObject();
            var iterObjHandle = _heap.AllocateObject(iterObj, AllocationSite.Current());
            iterObj.DefineOwnProperty("next", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(iterFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));
            var capturedHandle = iterObjHandle;
            var symIter = new NativeFunctionObject("[Symbol.iterator]", (_, _2) => JsValue.FromObject(capturedHandle), length: 0);
            var symHandle = _heap.AllocateObject(symIter, AllocationSite.Current());
            var symId = ((IBuiltinContext)this).CreateWellKnownSymbol("iterator").AsSymbolId();
            iterObj.DefineOwnSymbolProperty(symId, new JsPropertyDescriptor(JsValue.FromObject(symHandle), Writable: true, Enumerable: false, Configurable: true));
            return JsValue.FromObject(iterObjHandle);
        }, length: 1);
        DefineIntlAccessor(ph, proto, "segment", segmentFn);

        var segResFn = new NativeFunctionObject("resolvedOptions", (thisValue, _2) =>
        {
            var state = RequireSegmenterState(thisValue);
            string locale = "en", granularity = "grapheme";
            if (state.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String) locale = ld.Value.AsString();
            if (state.TryGetOwnProperty("granularity", out var gd) && gd.Value.Tag == JsValueTag.String) granularity = gd.Value.AsString();
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("granularity", new JsPropertyDescriptor(JsValue.FromString(granularity), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        DefineIntlAccessor(ph, proto, "resolvedOptions", segResFn);

        DefineBuiltinToStringTag(proto, "Intl.Segmenter");

        _segmenterPrototypeHandle = ph;
        return ph;
    }

    private ObjectHandle EnsurePluralRulesPrototype()
    {
        if (_pluralRulesPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var selectFn = new NativeFunctionObject("select", (thisValue, a) =>
        {
            string locale = "en";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var recv = _heap.GetObject(thisValue.AsObjectHandle());
                if (recv.TryGetProperty("__pluralRulesState", x => _heap.GetObject(x), out var sd) && sd.Value.Tag == JsValueTag.Object)
                {
                    var state = _heap.GetObject(sd.Value.AsObjectHandle());
                    if (state.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String)
                        locale = ld.Value.AsString();
                }
            }
            double n = a.Count > 0 ? ToNumber(a[0]) : 0;
            return JsValue.FromString(SelectPluralRule(locale, n));
        }, length: 1);
        proto.DefineOwnProperty("select", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(selectFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var resOptsFn = new NativeFunctionObject("resolvedOptions", (thisValue, _2) =>
        {
            string locale = "en", type = "cardinal";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var recv = _heap.GetObject(thisValue.AsObjectHandle());
                if (recv.TryGetProperty("__pluralRulesState", x => _heap.GetObject(x), out var sd) && sd.Value.Tag == JsValueTag.Object)
                {
                    var state = _heap.GetObject(sd.Value.AsObjectHandle());
                    if (state.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String) locale = ld.Value.AsString();
                    if (state.TryGetOwnProperty("type", out var td) && td.Value.Tag == JsValueTag.String) type = td.Value.AsString();
                }
            }
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("minimumIntegerDigits", new JsPropertyDescriptor(JsValue.FromNumber(1), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("minimumFractionDigits", new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("maximumFractionDigits", new JsPropertyDescriptor(JsValue.FromNumber(3), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("pluralCategories", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(new[] { JsValue.FromString("one"), JsValue.FromString("other") }), AllocationSite.Current())), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("roundingIncrement", new JsPropertyDescriptor(JsValue.FromNumber(1), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("roundingMode", new JsPropertyDescriptor(JsValue.FromString("halfExpand"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        DefineIntlAccessor(ph, proto, "resolvedOptions", resOptsFn);

        // ECMA-402 PluralRules.prototype.selectRange (data property for now)
        var selectRangeFn = new NativeFunctionObject("selectRange", (thisValue, a) =>
        {
            string locale = "en";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var recv = _heap.GetObject(thisValue.AsObjectHandle());
                if (recv.TryGetOwnProperty("__pluralRulesState", out var sd) && sd.Value.Tag == JsValueTag.Object)
                {
                    var state = _heap.GetObject(sd.Value.AsObjectHandle());
                    if (state.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String)
                        locale = ld.Value.AsString();
                }
            }
            double x = a.Count > 0 ? ToNumber(a[0]) : double.NaN;
            double y = a.Count > 1 ? ToNumber(a[1]) : double.NaN;
            if (double.IsNaN(x) || double.IsNaN(y))
                throw new JsThrownException(CreateTypeError("selectRange requires two finite numbers."));
            if (x > y) { var tmp = x; x = y; y = tmp; }
            return JsValue.FromString(SelectPluralRule(locale, y));
        }, length: 2);
        proto.DefineOwnProperty("selectRange", new JsPropertyDescriptor(
            JsValue.FromObject(_heap.AllocateObject(selectRangeFn, AllocationSite.Current())),
            Writable: true, Enumerable: false, Configurable: true));

        _pluralRulesPrototypeHandle = ph;
        return ph;
    }

    private ObjectHandle EnsureDisplayNamesPrototype()
    {
        if (_displayNamesPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var ofFn = new NativeFunctionObject("of", (tv, a) =>
        {
            var code = a.Count > 0 ? ToStringValue(a[0]) : "";
            string type = "language", style = "long";
            if (tv.Tag == JsValueTag.Object && _heap.GetObject(tv.AsObjectHandle()).TryGetOwnProperty("__displayNamesState", out var sd) && sd.Value.Tag == JsValueTag.Object)
            {
                var state = _heap.GetObject(sd.Value.AsObjectHandle());
                if (state.TryGetOwnProperty("type", out var td) && td.Value.Tag == JsValueTag.String) type = td.Value.AsString();
                if (state.TryGetOwnProperty("style", out var std) && std.Value.Tag == JsValueTag.String) style = std.Value.AsString();
            }
            string? result = type switch
            {
                "language" => TryGetLanguageDisplayName(code, style),
                "region" => TryGetRegionDisplayName(code, style),
                "script" => TryGetScriptDisplayName(code),
                "currency" => TryGetCurrencyDisplayName(code, style),
                "calendar" => TryGetCalendarDisplayName(code),
                _ => code
            };
            return JsValue.FromString(result ?? code);
        }, length: 1);
        proto.DefineOwnProperty("of", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(ofFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var dnResFn = new NativeFunctionObject("resolvedOptions", (tv, _2) =>
        {
            string locale = "en", style = "long", type = "language";
            if (tv.Tag == JsValueTag.Object && _heap.GetObject(tv.AsObjectHandle()).TryGetOwnProperty("__displayNamesState", out var sd) && sd.Value.Tag == JsValueTag.Object)
            {
                var state = _heap.GetObject(sd.Value.AsObjectHandle());
                if (state.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String) locale = ld.Value.AsString();
                if (state.TryGetOwnProperty("style", out var std) && std.Value.Tag == JsValueTag.String) style = std.Value.AsString();
                if (state.TryGetOwnProperty("type", out var td) && td.Value.Tag == JsValueTag.String) type = td.Value.AsString();
            }
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(style), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("fallback", new JsPropertyDescriptor(JsValue.FromString("code"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        DefineIntlAccessor(ph, proto, "resolvedOptions", dnResFn);

        _displayNamesPrototypeHandle = ph;
        return ph;
    }

    private ObjectHandle EnsureListFormatPrototype()
    {
        if (_listFormatPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        ListFormatState GetLFState(JsValue tv)
        {
            if (tv.Tag == JsValueTag.Object)
            {
                var o = _heap.GetObject(tv.AsObjectHandle());
                if (o.TryGetOwnProperty("__listFormatState", out var d) && d.Value.Tag == JsValueTag.Object)
                {
                    var s = _heap.GetObject(d.Value.AsObjectHandle());
                    string l = "en-US", t = "conjunction", st = "long";
                    if (s.TryGetOwnProperty("locale", out var ld) && ld.Value.Tag == JsValueTag.String) l = ld.Value.AsString();
                    if (s.TryGetOwnProperty("type", out var td) && td.Value.Tag == JsValueTag.String) t = td.Value.AsString();
                    if (s.TryGetOwnProperty("style", out var std) && std.Value.Tag == JsValueTag.String) st = std.Value.AsString();
                    return new ListFormatState(l, t, st);
                }
            }
            throw new JsThrownException(CreateTypeError("Intl.ListFormat method called on incompatible receiver."));
        }

        var formatFn = new NativeFunctionObject("", (tv, a) =>
        {
            var state = GetLFState(tv);
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            var parts = FormatListToParts(list, state);
            return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
        }, length: 1);
        DefineIntlAccessor(ph, proto, "format", formatFn);

        var formatToPartsFn = new NativeFunctionObject("", (tv, a) =>
        {
            var state = GetLFState(tv);
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            return CreateIntlPartsArray(FormatListToParts(list, state));
        }, length: 1);
        DefineIntlAccessor(ph, proto, "formatToParts", formatToPartsFn);

        var resOptsFn = new NativeFunctionObject("resolvedOptions", (tv, _2) =>
        {
            var state = GetLFState(tv);
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(string.IsNullOrEmpty(state.Locale) ? "en-US" : state.Locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(state.Type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(state.Style), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        DefineIntlAccessor(ph, proto, "resolvedOptions", resOptsFn);

        _listFormatPrototypeHandle = ph;
        return ph;
    }

}
