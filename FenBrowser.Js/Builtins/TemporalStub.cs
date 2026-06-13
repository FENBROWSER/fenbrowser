using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Temporal;

namespace FenBrowser.Js.Builtins;

// ECMA-262 Temporal proposal (ES2024+).
// Full implementation backed by .NET DateTime/DateTimeOffset/TimeSpan/TimeZoneInfo.
public sealed class TemporalStub : IBuiltinModule
{
    public string Name => "Temporal";

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;
        var temporal = new JsObject();
        temporal.SetPrototype(context.GetObjectPrototype());
        var tHandle = heap.AllocateObject(temporal, AllocationSite.Current());
        heap.PushRoot(tHandle);

        InstallNow(context, temporal, tHandle, heap);
        InstallDuration(context, temporal, tHandle, heap);
        InstallInstant(context, temporal, tHandle, heap);
        InstallPlainDate(context, temporal, tHandle, heap);
        InstallPlainTime(context, temporal, tHandle, heap);
        InstallPlainDateTime(context, temporal, tHandle, heap);
        InstallPlainYearMonth(context, temporal, tHandle, heap);
        InstallPlainMonthDay(context, temporal, tHandle, heap);
        InstallZonedDateTime(context, temporal, tHandle, heap);
        InstallCalendar(context, temporal, tHandle, heap);
        InstallTimeZone(context, temporal, tHandle, heap);
        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        _ = temporal.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("Temporal"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("Temporal", JsValue.FromObject(tHandle)) };
    }

    // ─── Helpers ───────────────────────────────────────────

    private static double GetVNum(JsHeap h, JsObject o, string k)
    {
        var v = GetV(h, o, k);
        return v.Tag is JsValueTag.Number or JsValueTag.Int32 ? v.AsNumber() : 0;
    }

    private static string GetVStr(JsHeap h, JsObject o, string k)
    {
        var v = GetV(h, o, k);
        return v.Tag == JsValueTag.String ? v.AsString() : "";
    }

    /// <summary>Safely apply years/months/days to a DateTime, clamping to valid range.</summary>
    private static DateTime SafeAddDate(DateTime dt, int years, int months, int days)
    {
        try { dt = dt.AddYears(years); } catch (ArgumentOutOfRangeException) { dt = years > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        try { dt = dt.AddMonths(months); } catch (ArgumentOutOfRangeException) { dt = months > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        try { dt = dt.AddDays(days); } catch (ArgumentOutOfRangeException) { dt = days > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        return dt;
    }

    /// <summary>Reconstruct a DateTime from a PlainDate _v object.</summary>
    private static DateTime DecodePlainDate(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        int d = (int)GetVNum(h, o, "d");
        y = Math.Max(1, Math.Min(9999, y));
        m = Math.Max(1, Math.Min(12, m));
        d = Math.Max(1, Math.Min(DateTime.DaysInMonth(y, m), d));
        return new DateTime(y, m, d);
    }

    /// <summary>Reconstruct a DateTime from a PlainDateTime _v object.</summary>
    private static DateTime DecodePlainDateTime(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "year");
        int mo = (int)GetVNum(h, o, "month");
        int d = (int)GetVNum(h, o, "day");
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int s = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        y = Math.Max(1, Math.Min(9999, y));
        mo = Math.Max(1, Math.Min(12, mo));
        d = Math.Max(1, Math.Min(DateTime.DaysInMonth(y, mo), d));
        hr = Math.Max(0, Math.Min(23, hr));
        mi = Math.Max(0, Math.Min(59, mi));
        s = Math.Max(0, Math.Min(59, s));
        ms = Math.Max(0, Math.Min(999, ms));
        return new DateTime(y, mo, d, hr, mi, s, ms);
    }

    /// <summary>Reconstruct a DateTime from a PlainYearMonth _v object (day defaults to 1).</summary>
    private static DateTime DecodePlainYearMonth(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        y = Math.Max(1, Math.Min(9999, y));
        m = Math.Max(1, Math.Min(12, m));
        return new DateTime(y, m, 1);
    }

    /// <summary>Reconstruct epoch nanoseconds from an Instant _v object.</summary>
    private static long DecodeInstantNanos(JsHeap h, JsObject o)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var value) && value.Value.Tag == JsValueTag.Object)
        {
            var slots = h.GetObject(value.Value.AsObjectHandle());
            if (slots.TryGetProperty("ensBig", x => h.GetObject(x), out var exact) && exact.Value.Tag == JsValueTag.BigInt)
            {
                return ToSafeLong(exact.Value.AsBigInt());
            }
        }

        var ens = GetVNum(h, o, "ens");
        if (double.IsNaN(ens) || double.IsInfinity(ens)) return 0L;
        return ToSafeLong(new System.Numerics.BigInteger(ens));
    }

    /// <summary>Compare fields of two _v objects. Returns -1, 0, or 1.</summary>
    private static int FieldsCompare(JsHeap h, JsObject a, JsObject b, params string[] fields)
    {
        foreach (var f in fields)
        {
            double va = GetVNum(h, a, f);
            double vb = GetVNum(h, b, f);
            if (va < vb) return -1;
            if (va > vb) return 1;
        }
        return 0;
    }

    /// <summary>True when all named fields are equal between two _v objects.</summary>
    private static bool FieldsEqual(JsHeap h, JsObject a, JsObject b, params string[] fields)
    {
        foreach (var f in fields)
        {
            if (GetVNum(h, a, f) != GetVNum(h, b, f)) return false;
        }
        return true;
    }

    /// <summary>Reconstruct a DateTime from Instant epoch nanos.</summary>
    private static DateTime InstantToDateTime(long nanos)
    {
        // Clamp to .NET DateTime range (1/1/0001 to 12/31/9999)
        const long minTicks = 0L;
        const long maxTicks = 3155378975999999999L;
        var ticks = Epoch.Ticks + (nanos / 100L);
        ticks = Math.Max(minTicks, Math.Min(maxTicks, ticks));
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>Format an Instant as ISO 8601 string (e.g. "2024-01-15T12:00:00Z").</summary>
    private static JsValue FormatInstant(JsHeap h, JsObject o)
    {
        long nanos = DecodeInstantNanos(h, o);
        var dt = InstantToDateTime(nanos);
        long subMilliNanos = nanos % 1_000_000;
        if (subMilliNanos < 0) subMilliNanos += 1_000_000;
        string frac = subMilliNanos == 0 ? "" : $".{subMilliNanos:D6}".TrimEnd('0');
        return JsValue.FromString($"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}T{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2}{frac}Z");
    }

    private static JsValue FormatInstant(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var opts = GetToStringOptions(ctx, h, args, 0);
        long epochNs = DecodeInstantNanos(h, o);
        long inc = PrecisionIncrementNs(opts);
        if (inc > 1) epochNs = RoundNsToIncrement(ctx, epochNs, inc, opts.RoundingMode);

        // timeZone option: render the wall clock in that zone with its offset.
        if (args.Count > 0 && args[0].Tag == JsValueTag.Object)
        {
            var optionsObject = h.GetObject(args[0].AsObjectHandle());
            if (ctx.TryGetPropertyValue(optionsObject, args[0], "timeZone", out var tzv) && tzv.Tag != JsValueTag.Undefined)
            {
                if (tzv.Tag != JsValueTag.String)
                    throw new JsThrownException(ctx.CreateTypeError("timeZone must be a string."));
                string ctz = CanonicalizeTimeZoneId(ctx, tzv.AsString());
                long offNs = TemporalTimeZones.GetOffsetNs(ctz, epochNs);
                var (zd, zt) = TemporalTimeZones.WallFromEpochNs(epochNs, offNs);
                return JsValue.FromString($"{FormatIsoYear(zd.Year)}-{zd.Month:D2}-{zd.Day:D2}T{zt.Hour:D2}:{zt.Minute:D2}" +
                    $"{FormatSecondsPart(zt.ToNanosecondsOfDay(), opts)}{TemporalTimeZones.FormatOffset(offNs)}");
            }
        }

        var (d, t) = TemporalTimeZones.WallFromEpochNs(epochNs, 0);
        return JsValue.FromString($"{FormatIsoYear(d.Year)}-{d.Month:D2}-{d.Day:D2}T{t.Hour:D2}:{t.Minute:D2}" +
            $"{FormatSecondsPart(t.ToNanosecondsOfDay(), opts)}Z");
    }

    private static JsValue InstantToLocaleString(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var options = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            IntlDateTimeFormatting.ValidateOptions(options);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var instant = new DateTimeOffset(InstantToDateTime(DecodeInstantNanos(h, o)));
        var result = IntlDateTimeFormatting.Format(instant, culture, options);
        return JsValue.FromString(result.Text);
    }

    private static JsValue InstantToZonedDateTimeIso(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        // sec-temporal.instant.prototype.tozoneddatetimeiso: the time zone
        // argument is required and must be a string (identifier or ISO string).
        if (args.Count == 0 || args[0].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("toZonedDateTimeISO requires a time zone string."));
        string tz = CanonicalizeTimeZoneId(ctx, args[0].AsString());
        return MakeZonedDateTimeNs(ctx, h, DecodeInstantNanos(h, o), tz, "iso8601");
    }

    private static JsValue PlainTimeToLocaleString(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var options = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            var result = IntlDateTimeFormatting.FormatPlainTime(
                (int)GetVNum(h, o, "hour"),
                (int)GetVNum(h, o, "minute"),
                (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"),
                (int)GetVNum(h, o, "microsecond"),
                (int)GetVNum(h, o, "nanosecond"),
                IntlDateTimeFormatting.ResolveCulture(locale),
                options);
            return JsValue.FromString(result.Text);
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrownException(ctx.CreateTypeError(ex.Message));
        }
    }

    /// <summary>Format a Duration as ISO 8601 string (e.g. "P1Y2M3DT4H5M6S", "-PT1H").</summary>
    private static JsValue FormatDuration(JsHeap h, JsObject o)
    {
        var d = DecodeDuration(h, o);
        bool negative = d.years < 0 || d.months < 0 || d.weeks < 0 || d.days < 0 || d.hours < 0
            || d.minutes < 0 || d.seconds < 0 || d.millis < 0 || d.micros < 0 || d.nanos < 0;
        var sb = new System.Text.StringBuilder(negative ? "-P" : "P");
        if (d.years != 0) sb.Append($"{Math.Abs(d.years)}Y");
        if (d.months != 0) sb.Append($"{Math.Abs(d.months)}M");
        if (d.weeks != 0) sb.Append($"{Math.Abs(d.weeks)}W");
        if (d.days != 0) sb.Append($"{Math.Abs(d.days)}D");
        if (d.hours == 0 && d.minutes == 0 && d.seconds == 0 && d.millis == 0 && d.micros == 0 && d.nanos == 0)
        {
            if (sb.Length == 1) sb.Append("T0S");
        }
        else
        {
            sb.Append('T');
            if (d.hours != 0) sb.Append($"{Math.Abs(d.hours)}H");
            if (d.minutes != 0) sb.Append($"{Math.Abs(d.minutes)}M");
            long frac = (long)Math.Abs(d.millis) * 1_000_000L + (long)Math.Abs(d.micros) * 1_000L + (long)Math.Abs(d.nanos);
            if (d.seconds != 0 || frac != 0)
            {
                sb.Append(Math.Abs(d.seconds));
                if (frac != 0) { sb.Append('.'); sb.Append(frac.ToString("D9").TrimEnd('0')); }
                sb.Append('S');
            }
        }
        return JsValue.FromString(sb.ToString());
    }

    /// <summary>
    /// Temporal.Duration.prototype.toString: format the duration applying the precision options
    /// (smallestUnit / fractionalSecondDigits / roundingMode). The fractional-seconds part is
    /// rounded to the requested precision, carrying any whole second into the seconds field.
    /// </summary>
    private static JsValue FormatDurationOpts(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        var opts = GetToStringOptions(ctx, h, a, 0);
        // Duration toString does not accept "minute" as smallestUnit.
        if (opts.SmallestUnit == "minute")
            throw new JsThrownException(ctx.CreateRangeError("'minute' is not a valid smallestUnit for Duration.toString."));

        var d = DecodeDuration(h, o);
        bool negative = d.years < 0 || d.months < 0 || d.weeks < 0 || d.days < 0 || d.hours < 0
            || d.minutes < 0 || d.seconds < 0 || d.millis < 0 || d.micros < 0 || d.nanos < 0;

        // Combine sub-second fields and the seconds field, then round the fraction to the precision.
        long secs = d.seconds;
        long fracNs = (long)d.millis * 1_000_000L + (long)d.micros * 1_000L + d.nanos;
        secs += fracNs / 1_000_000_000L; fracNs %= 1_000_000_000L;
        long inc = PrecisionIncrementNs(opts);
        if (inc > 1) fracNs = RoundNsToIncrement(ctx, fracNs, inc, opts.RoundingMode);
        secs += fracNs / 1_000_000_000L; fracNs %= 1_000_000_000L;

        int digits = opts.SmallestUnit switch
        {
            "second" => 0, "millisecond" => 3, "microsecond" => 6, "nanosecond" => 9, _ => opts.FractionalDigits,
        };
        long absFrac = Math.Abs(fracNs);
        string fracStr = digits switch
        {
            < 0 => absFrac == 0 ? "" : $".{absFrac:D9}".TrimEnd('0'),
            0 => "",
            _ => "." + $"{absFrac:D9}"[..digits],
        };

        var sb = new System.Text.StringBuilder(negative ? "-P" : "P");
        if (d.years != 0) sb.Append($"{Math.Abs(d.years)}Y");
        if (d.months != 0) sb.Append($"{Math.Abs(d.months)}M");
        if (d.weeks != 0) sb.Append($"{Math.Abs(d.weeks)}W");
        if (d.days != 0) sb.Append($"{Math.Abs(d.days)}D");
        bool emitSeconds = secs != 0 || fracNs != 0 || digits >= 0;
        bool emitTime = d.hours != 0 || d.minutes != 0 || emitSeconds;
        if (emitTime)
        {
            sb.Append('T');
            if (d.hours != 0) sb.Append($"{Math.Abs(d.hours)}H");
            if (d.minutes != 0) sb.Append($"{Math.Abs(d.minutes)}M");
            if (emitSeconds) sb.Append($"{Math.Abs(secs)}{fracStr}S");
        }
        else if (sb.Length == 1)
        {
            sb.Append("T0S");
        }
        return JsValue.FromString(sb.ToString());
    }

    /// <summary>Format a PlainDate as ISO 8601 string (e.g. "2024-01-15", extended years signed 6-digit).</summary>
    private static JsValue FormatPlainDate(JsHeap h, JsObject o)
    {
        var dt = DecodeIsoDate(h, o);
        return JsValue.FromString($"{FormatIsoYear(dt.Year)}-{dt.Month:D2}-{dt.Day:D2}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainTime as ISO 8601 string (e.g. "12:00:00.123").</summary>
    private static JsValue FormatPlainTime(JsHeap h, JsObject o)
    {
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int s = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = ms * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        return JsValue.FromString($"{hr:D2}:{mi:D2}:{s:D2}{fracStr}");
    }

    /// <summary>Format a PlainDateTime as ISO 8601 string (e.g. "2024-01-15T12:00:00").</summary>
    private static JsValue FormatPlainDateTime(JsHeap h, JsObject o)
    {
        var date = DecodeIsoDateLong(h, o);
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int se = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = ms * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        return JsValue.FromString($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}T{hr:D2}:{mi:D2}:{se:D2}{fracStr}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainYearMonth as ISO 8601 string (e.g. "2024-01").</summary>
    private static JsValue FormatPlainYearMonth(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        return JsValue.FromString($"{FormatIsoYear(y)}-{m:D2}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainMonthDay as ISO 8601 string (e.g. "01-15").</summary>
    private static JsValue FormatPlainMonthDay(JsHeap h, JsObject o)
    {
        var mc = GetVStr(h, o, "mc");
        int d = (int)GetVNum(h, o, "d");
        int m = 1;
        if (mc.StartsWith("M") && int.TryParse(mc.Substring(1), out var parsed))
            m = parsed;
        return JsValue.FromString($"{m:D2}-{d:D2}");
    }

    /// <summary>Format a ZonedDateTime as ISO 8601 string per the toString options.</summary>
    private static JsValue FormatZonedDateTime(IBuiltinContext ctx, JsHeap h, JsObject o, ToStringOptions opts)
    {
        string tz = GetVStr(h, o, "tz");
        long epochNs = DecodeInstantNanos(h, o);
        long inc = PrecisionIncrementNs(opts);
        if (inc > 1) epochNs = RoundNsToIncrement(ctx, epochNs, inc, opts.RoundingMode);
        long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNs);
        var (date, time) = TemporalTimeZones.WallFromEpochNs(epochNs, offsetNs);
        var sb = new System.Text.StringBuilder();
        sb.Append($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}T{time.Hour:D2}:{time.Minute:D2}");
        sb.Append(FormatSecondsPart(time.ToNanosecondsOfDay(), opts));
        if (opts.ShowOffset != "never") sb.Append(TemporalTimeZones.FormatOffset(offsetNs));
        if (opts.TimeZoneName != "never") sb.Append(opts.TimeZoneName == "critical" ? $"[!{tz}]" : $"[{tz}]");
        sb.Append(CalendarSuffix(h, o, opts));
        return JsValue.FromString(sb.ToString());
    }

    // ─── Arg helpers ────────────────────────────────────────

    private static string ToStrArg(IBuiltinContext ctx, JsValue v)
    {
        if (v.Tag == JsValueTag.String) return v.AsString();
        if (v.Tag == JsValueTag.Object) return ctx.ToStringValue(v);
        if (v.Tag is JsValueTag.Number or JsValueTag.Int32) return v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ctx.ToStringValue(v);
    }

    // ─── Spec validation helpers (backed by FenBrowser.Js.Temporal) ───

    /// <summary>ECMA-262 ToIntegerWithTruncation: RangeError on NaN/±Infinity.</summary>
    private static double ToIntegerWithTruncation(IBuiltinContext ctx, JsValue v)
    {
        var n = ctx.ToNumber(v);
        if (double.IsNaN(n) || double.IsInfinity(n))
            throw new JsThrownException(ctx.CreateRangeError("Value must be a finite number."));
        return Math.Truncate(n);
    }

    /// <summary>Required integer argument (undefined → NaN → RangeError per spec).</summary>
    private static double ArgInt(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
        => ToIntegerWithTruncation(ctx, i < a.Count ? a[i] : JsValue.Undefined);

    /// <summary>Optional integer argument with a default for undefined.</summary>
    private static double ArgIntOr(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i, double dflt)
        => i >= a.Count || a[i].Tag == JsValueTag.Undefined ? dflt : ToIntegerWithTruncation(ctx, a[i]);

    /// <summary>Canonicalize a calendar identifier; RangeError when unsupported.</summary>
    private static string CanonicalizeCalendarId(IBuiltinContext ctx, string id)
    {
        var canonical = TemporalCalendars.Canonicalize(id);
        if (canonical is not null)
            return canonical;

        // ParseTemporalCalendarString: any ISO date/time string also names a
        // calendar via its optional [u-ca] annotation, defaulting to iso8601.
        // Accept date-time, year-month, month-day and time-only productions.
        if (TemporalIsoParser.TryParseDateTime(id, out var parsed, out _)
            || TemporalIsoParser.TryParseYearMonth(id, out parsed, out _)
            || TemporalIsoParser.TryParseMonthDay(id, out parsed, out _)
            || TemporalIsoParser.TryParseTime(id, out parsed, out _))
        {
            if (parsed.Calendar is null)
                return "iso8601";
            canonical = TemporalCalendars.Canonicalize(parsed.Calendar);
            if (canonical is not null)
                return canonical;
        }

        throw new JsThrownException(ctx.CreateRangeError($"'{id}' is not a valid calendar identifier."));
    }

    /// <summary>ToTemporalCalendarIdentifier: a string, or a Temporal instance carrying a calendar.</summary>
    private static string ToCalendarIdentifier(IBuiltinContext ctx, JsHeap h, JsValue v)
    {
        if (v.Tag == JsValueTag.String)
            return CanonicalizeCalendarId(ctx, v.AsString());
        if (v.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(v.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "calendarId"))
            {
                var cid = GetVStr(h, obj, "calendarId");
                return string.IsNullOrEmpty(cid) ? "iso8601" : cid;
            }
        }

        throw new JsThrownException(ctx.CreateTypeError("calendar must be a string."));
    }

    /// <summary>Optional calendar argument: undefined → iso8601; non-string → TypeError.</summary>
    private static string CalendarArg(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
    {
        if (i >= a.Count || a[i].Tag == JsValueTag.Undefined) return "iso8601";
        if (a[i].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("calendar must be a string."));
        return CanonicalizeCalendarId(ctx, a[i].AsString());
    }

    /// <summary>
    /// Calendar from a parsed ISO string's [u-ca] annotation (null → iso8601). The annotation
    /// value must itself be a valid calendar identifier — it is never re-parsed as a date, so a
    /// date-shaped value such as "11111111" or "1111-11-11" is a RangeError, not iso8601.
    /// </summary>
    private static string CalendarFromAnnotation(IBuiltinContext ctx, string? annotation)
    {
        if (annotation is null) return "iso8601";
        var canonical = TemporalCalendars.Canonicalize(annotation);
        if (canonical is not null) return canonical;
        throw new JsThrownException(ctx.CreateRangeError($"'{annotation}' is not a valid calendar identifier."));
    }

    /// <summary>GetOptionsObject: options must be undefined or an object.</summary>
    private static void RequireOptionsObject(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
    {
        if (i < a.Count && a[i].Tag != JsValueTag.Undefined && a[i].Tag != JsValueTag.Object)
            throw new JsThrownException(ctx.CreateTypeError("options must be an object or undefined."));
    }

    /// <summary>ToTemporalOverflow: "constrain" (default) or "reject"; validates options shape.</summary>
    private static string GetOverflowOption(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i)
    {
        RequireOptionsObject(ctx, a, i);
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return "constrain";
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(obj, optionsValue, "overflow", out var v) || v.Tag == JsValueTag.Undefined)
            return "constrain";
        var s = ctx.ToStringValue(v);
        if (s is not ("constrain" or "reject"))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for overflow."));
        return s;
    }

    /// <summary>GetTemporalDisambiguationOption: options.disambiguation, validated.</summary>
    private static string GetDisambiguationOption(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i)
    {
        RequireOptionsObject(ctx, a, i);
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return "compatible";
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(obj, optionsValue, "disambiguation", out var v) || v.Tag == JsValueTag.Undefined)
            return "compatible";
        var s = ctx.ToStringValue(v);
        if (s is not ("compatible" or "earlier" or "later" or "reject"))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for disambiguation."));
        return s;
    }

    /// <summary>Read a property through the full Get protocol (triggers accessors); false when absent/undefined.</summary>
    private static bool TryGetField(IBuiltinContext ctx, JsHeap h, JsValue bagValue, string name, out JsValue value)
    {
        var obj = h.GetObject(bagValue.AsObjectHandle());
        if (ctx.TryGetPropertyValue(obj, bagValue, name, out value) && value.Tag != JsValueTag.Undefined)
            return true;
        value = JsValue.Undefined;
        return false;
    }

    /// <summary>Resolve the month from a field bag's month/monthCode (ISO semantics).</summary>
    private static double GetMonthFromFields(IBuiltinContext ctx, JsHeap h, JsValue bagValue)
    {
        // A field that is present but undefined is treated as absent (spec reads each
        // field with Get and falls back when the result is undefined).
        bool hasMonth = TryGetField(ctx, h, bagValue, "month", out var monthValue) && monthValue.Tag != JsValueTag.Undefined;
        bool hasCode = TryGetField(ctx, h, bagValue, "monthCode", out var codeValue) && codeValue.Tag != JsValueTag.Undefined;
        if (hasCode)
        {
            if (codeValue.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("monthCode must be a string."));
            var mc = codeValue.AsString();
            if (mc.Length != 3 || mc[0] != 'M' || !char.IsAsciiDigit(mc[1]) || !char.IsAsciiDigit(mc[2]))
                throw new JsThrownException(ctx.CreateRangeError($"'{mc}' is not a valid monthCode."));
            int m = (mc[1] - '0') * 10 + (mc[2] - '0');
            if (m < 1 || m > 12)
                throw new JsThrownException(ctx.CreateRangeError($"'{mc}' is not a valid monthCode for the iso8601 calendar."));
            if (hasMonth && ToIntegerWithTruncation(ctx, monthValue) != m)
                throw new JsThrownException(ctx.CreateRangeError("month and monthCode conflict."));
            return m;
        }

        if (!hasMonth)
            throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
        return ToIntegerWithTruncation(ctx, monthValue);
    }

    /// <summary>Calendar from a field bag ("calendar" property): TypeError/RangeError per ToTemporalCalendarIdentifier.</summary>
    private static string GetCalendarFromFields(IBuiltinContext ctx, JsHeap h, JsValue bagValue)
    {
        if (!TryGetField(ctx, h, bagValue, "calendar", out var calValue)) return "iso8601";
        return ToCalendarIdentifier(ctx, h, calValue);
    }

    /// <summary>RegulateISODate: constrain/reject then ISODateWithinLimits range-check.</summary>
    private static IsoDate RegulateIsoDate(IBuiltinContext ctx, double y, double m, double d, string overflow)
    {
        if (y is < -999_999 or > 999_999)
            throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
        int year = (int)y;
        int month;
        int day;
        if (overflow == "constrain")
        {
            month = (int)Math.Clamp(m, 1, 12);
            day = (int)Math.Clamp(d, 1, IsoMath.DaysInMonth(year, month));
        }
        else
        {
            if (m is < 1 or > 12 || d is < 1 or > 31 || !IsoMath.IsValidIsoDate(year, (int)m, (int)d))
                throw new JsThrownException(ctx.CreateRangeError("Invalid ISO date."));
            month = (int)m;
            day = (int)d;
        }

        if (!IsoMath.IsoDateWithinLimits(new IsoDate(year, month, day)))
            throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
        return new IsoDate(year, month, day);
    }

    /// <summary>RegulateISODate + CreateTemporalDate.</summary>
    private static JsValue MakePlainDateRegulated(IBuiltinContext ctx, JsHeap h, double y, double m, double d, string calendar, string overflow)
    {
        var date = RegulateIsoDate(ctx, y, m, d, overflow);
        return MakePlainDateYmd(ctx, h, date.Year, date.Month, date.Day, calendar);
    }

    // ─── Non-ISO calendar integration ───────────────────────────────────────

    /// <summary>The instance's calendar id, defaulting to iso8601.</summary>
    private static string CalId(JsHeap h, JsObject o)
    {
        var c = GetVStr(h, o, "calendarId");
        return string.IsNullOrEmpty(c) ? "iso8601" : c;
    }

    /// <summary>Calendar-relative field view of an ISO date, or null for iso8601 / unsupported calendars.</summary>
    private static CalendarFields? CalFields(string calId, IsoDate iso)
        => CalendarMath.Get(calId)?.ToFields(iso);

    /// <summary>era getter value for a non-ISO calendar (undefined for iso8601).</summary>
    private static JsValue EraValue(string calId, IsoDate iso)
    {
        var f = CalFields(calId, iso);
        return f is { Era: { } e } ? JsValue.FromString(e) : JsValue.Undefined;
    }

    private static JsValue EraYearValue(string calId, IsoDate iso)
    {
        var f = CalFields(calId, iso);
        return f is { EraYear: { } ey } ? JsValue.FromNumber(ey) : JsValue.Undefined;
    }

    /// <summary>
    /// CalendarResolveFields for a date bag in a non-ISO calendar: read era/eraYear/year,
    /// month/monthCode and day (in sorted key order), resolving to native (year, monthOrdinal, day).
    /// When <paramref name="baseFields"/> is given (the `with` path), absent fields fall back to it.
    /// </summary>
    private static (int Year, int Month, int Day) ResolveCalendarDateFields(
        IBuiltinContext ctx, JsHeap h, JsValue bag, CalendarSystem sys, CalendarFields? baseFields, bool requireDay)
    {
        // PrepareTemporalFields reads keys in sorted order: day, era, eraYear, month, monthCode, year.
        bool hasDay = TryGetField(ctx, h, bag, "day", out var dayV);
        bool hasEra = TryGetField(ctx, h, bag, "era", out var eraV);
        bool hasEraYear = TryGetField(ctx, h, bag, "eraYear", out var eraYearV);
        bool hasMonth = TryGetField(ctx, h, bag, "month", out var monthV);
        bool hasMonthCode = TryGetField(ctx, h, bag, "monthCode", out var mcV);
        bool hasYear = TryGetField(ctx, h, bag, "year", out var yearV);

        bool fresh = baseFields is null;

        // ── presence checks (TypeError), before any value validation (RangeError) ──
        // era and eraYear must be supplied together.
        if (hasEra != hasEraYear)
            throw new JsThrownException(ctx.CreateTypeError("era and eraYear must be provided together."));
        bool hasYearInfo = hasYear || (hasEra && hasEraYear);
        if (fresh && !hasYearInfo)
            throw new JsThrownException(ctx.CreateTypeError("year (or era and eraYear) is required."));
        if (fresh && !hasMonth && !hasMonthCode)
            throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
        if (fresh && requireDay && !hasDay)
            throw new JsThrownException(ctx.CreateTypeError("day is required."));

        // ── value resolution (RangeError) ──
        int year;
        if (hasEra && hasEraYear)
        {
            if (eraV.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("era must be a string."));
            int eraYear = ToSafeInt(ToIntegerWithTruncation(ctx, eraYearV));
            if (!sys.YearFromEra(eraV.AsString(), eraYear, out year))
                throw new JsThrownException(ctx.CreateRangeError($"'{eraV.AsString()}' is not a valid era for the {sys.Id} calendar."));
            if (hasYear) _ = ToIntegerWithTruncation(ctx, yearV); // observe coercion; era takes precedence
        }
        else if (hasYear)
        {
            year = ToSafeInt(ToIntegerWithTruncation(ctx, yearV));
        }
        else
        {
            year = baseFields!.Value.Year;
        }

        int monthOrdinal;
        if (hasMonthCode)
        {
            if (mcV.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("monthCode must be a string."));
            if (!sys.MonthFromCode(year, mcV.AsString(), out monthOrdinal, out bool exists))
                throw new JsThrownException(ctx.CreateRangeError($"'{mcV.AsString()}' is not a valid monthCode."));
            if (!exists)
                throw new JsThrownException(ctx.CreateRangeError($"'{mcV.AsString()}' is not a valid monthCode for the {sys.Id} calendar in this year."));
            if (hasMonth && ToSafeInt(ToIntegerWithTruncation(ctx, monthV)) != monthOrdinal)
                throw new JsThrownException(ctx.CreateRangeError("month and monthCode conflict."));
        }
        else if (hasMonth)
        {
            monthOrdinal = ToSafeInt(ToIntegerWithTruncation(ctx, monthV));
        }
        else
        {
            monthOrdinal = baseFields!.Value.Month;
        }

        int day = hasDay ? ToSafeInt(ToIntegerWithTruncation(ctx, dayV)) : (baseFields?.Day ?? 1);
        return (year, monthOrdinal, day);
    }

    /// <summary>
    /// Resolve a date bag to an ISO date for the given calendar (ISO or non-ISO). Fields are read
    /// first, then the overflow option (matching the spec's observable operation order).
    /// </summary>
    private static IsoDate ResolveDateBagToIso(IBuiltinContext ctx, JsHeap h, JsValue bag, string calendar,
        IReadOnlyList<JsValue> a, int optIdx, CalendarFields? baseFields = null, bool requireDay = true)
        => ResolveDateBagToIso(ctx, h, bag, calendar, a, optIdx, out _, baseFields, requireDay);

    private static IsoDate ResolveDateBagToIso(IBuiltinContext ctx, JsHeap h, JsValue bag, string calendar,
        IReadOnlyList<JsValue> a, int optIdx, out string overflow, CalendarFields? baseFields = null, bool requireDay = true)
    {
        var sys = CalendarMath.Get(calendar);
        if (sys is null)
        {
            // iso8601 (or a calendar we do not yet model): ISO field semantics.
            double y = baseFields?.Year ?? 0;
            bool hasYear = TryGetField(ctx, h, bag, "year", out var yv);
            if (hasYear) y = ToIntegerWithTruncation(ctx, yv);
            else if (baseFields is null) throw new JsThrownException(ctx.CreateTypeError("year is required."));
            bool hasMonth = TryGetField(ctx, h, bag, "month", out _) || TryGetField(ctx, h, bag, "monthCode", out _);
            double m;
            if (hasMonth) m = GetMonthFromFields(ctx, h, bag);
            else if (baseFields is { } bfm) m = bfm.Month;
            else throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
            bool hasDay = TryGetField(ctx, h, bag, "day", out var dv);
            if (!hasDay && requireDay && baseFields is null)
                throw new JsThrownException(ctx.CreateTypeError("day is required."));
            double d = hasDay ? ToIntegerWithTruncation(ctx, dv) : (baseFields?.Day ?? 1);
            overflow = GetOverflowOption(ctx, h, a, optIdx);
            return RegulateIsoDate(ctx, y, m, d, overflow);
        }

        var (year, monthOrdinal, day) = ResolveCalendarDateFields(ctx, h, bag, sys, baseFields, requireDay);
        overflow = GetOverflowOption(ctx, h, a, optIdx);
        if (!sys.TryResolveToIso(year, monthOrdinal, day, overflow, out var iso))
            throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported range or invalid for the calendar."));
        return iso;
    }

    /// <summary>The internal _v data object, when the value is one of our Temporal instances.</summary>
    private static bool TryGetInternalData(JsHeap h, JsObject o, out JsObject data)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            data = h.GetObject(dd.Value.AsObjectHandle());
            return true;
        }

        data = null!;
        return false;
    }

    /// <summary>ToTemporalDate: PlainDate/PlainDateTime/ZonedDateTime instance, ISO string, or property bag → ISO date + calendar.</summary>
    private static (IsoDate Date, string Calendar) ToTemporalDateRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO date string: {parseError}"));
            var cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            if (!IsoMath.IsoDateWithinLimits(date))
                throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
            return (date, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                    return (DecodeIsoDate(h, obj), GetVStr(h, obj, "calendarId"));
                if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                    return (DecodeIsoDateLong(h, obj), GetVStr(h, obj, "calendarId"));
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a date."));
            }

            string bagCal = GetCalendarFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double y = ToIntegerWithTruncation(ctx, yearValue);
            double m = GetMonthFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                throw new JsThrownException(ctx.CreateTypeError("day is required."));
            double d = ToIntegerWithTruncation(ctx, dayValue);
            return (RegulateIsoDate(ctx, y, m, d, "constrain"), bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal date."));
    }

    /// <summary>ToTemporalTime: PlainTime/PlainDateTime instance, ISO time string, or property bag → wall-clock time.</summary>
    private static IsoTime ToTemporalTimeRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO time string: {parseError}"));
            _ = CalendarFromAnnotation(ctx, parsed.Calendar);
            return parsed.Time;
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "hour"))
                    return new IsoTime((int)GetVNum(h, obj, "hour"), (int)GetVNum(h, obj, "minute"), (int)GetVNum(h, obj, "second"),
                        (int)GetVNum(h, obj, "millisecond"), (int)GetVNum(h, obj, "microsecond"), (int)GetVNum(h, obj, "nanosecond"));
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a time."));
            }

            string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerWithTruncation(ctx, fv);
                    any = true;
                }
            }

            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("At least one time field is required."));
            return new IsoTime(
                (int)Math.Clamp(values[0], 0, 23), (int)Math.Clamp(values[1], 0, 59), (int)Math.Clamp(values[2], 0, 59),
                (int)Math.Clamp(values[3], 0, 999), (int)Math.Clamp(values[4], 0, 999), (int)Math.Clamp(values[5], 0, 999));
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal time."));
    }

    /// <summary>ToTemporalDuration: Duration instance, ISO duration string, or property bag.</summary>
    private static (int years, int months, int weeks, int days, int hours, int minutes, int seconds, int millis, int micros, int nanos)
        ToTemporalDurationRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (ParseIsoDuration(s, out var y, out var mo, out var w, out var d,
                    out var hr, out var mi, out var sec, out var ms, out var us, out var ns))
                return (y, mo, w, d, hr, mi, sec, ms, us, ns);
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO duration string."));
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "years"))
                return DecodeDuration(h, obj);

            string[] fields = { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerIfIntegral(ctx, fv);
                    any = true;
                }
            }

            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("At least one duration field is required."));
            ValidateDuration(ctx, values);
            return (ToSafeInt(values[0]), ToSafeInt(values[1]), ToSafeInt(values[2]), ToSafeInt(values[3]), ToSafeInt(values[4]),
                ToSafeInt(values[5]), ToSafeInt(values[6]), ToSafeInt(values[7]), ToSafeInt(values[8]), ToSafeInt(values[9]));
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal duration."));
    }

    private static long DecodeTimeOfDayNs(JsHeap h, JsObject o)
        => new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond")).ToNanosecondsOfDay();

    /// <summary>ToTemporalDateTime: instance, ISO string, or property bag → ISO date + wall-clock time.</summary>
    private static (IsoDate Date, IsoTime Time) ToTemporalDateTimeRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO date-time string: {parseError}"));
            _ = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            if (!IsoMath.IsoDateWithinLimits(date))
                throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
            return (date, parsed.HasTime ? parsed.Time : IsoTime.Midnight);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                    return (DecodeIsoDateLong(h, obj), new IsoTime(
                        (int)GetVNum(h, obj, "hour"), (int)GetVNum(h, obj, "minute"), (int)GetVNum(h, obj, "second"),
                        (int)GetVNum(h, obj, "millisecond"), (int)GetVNum(h, obj, "microsecond"), (int)GetVNum(h, obj, "nanosecond")));
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                    return (DecodeIsoDate(h, obj), IsoTime.Midnight);
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a date-time."));
            }

            _ = GetCalendarFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double y = ToIntegerWithTruncation(ctx, yearValue);
            double m = GetMonthFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                throw new JsThrownException(ctx.CreateTypeError("day is required."));
            double d = ToIntegerWithTruncation(ctx, dayValue);
            var bagDate = RegulateIsoDate(ctx, y, m, d, "constrain");
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var tv = new double[timeFields.Length];
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, timeFields[fi], out var fv))
                    tv[fi] = ToIntegerWithTruncation(ctx, fv);
            }
            return (bagDate, new IsoTime(
                (int)Math.Clamp(tv[0], 0, 23), (int)Math.Clamp(tv[1], 0, 59), (int)Math.Clamp(tv[2], 0, 59),
                (int)Math.Clamp(tv[3], 0, 999), (int)Math.Clamp(tv[4], 0, 999), (int)Math.Clamp(tv[5], 0, 999)));
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal date-time."));
    }

    private const long NsPerDay = 86_400_000_000_000L;

    /// <summary>DifferenceISODateTime with largestUnit=day: days + balanced time, uniform sign.</summary>
    private static JsValue DifferencePlainDateTimes(IBuiltinContext ctx, JsHeap h, IsoDate d1, long t1Ns, IsoDate d2, long t2Ns)
    {
        long days = IsoMath.ToEpochDays(d2) - IsoMath.ToEpochDays(d1);
        long ns = t2Ns - t1Ns;
        if (days > 0 && ns < 0) { days--; ns += NsPerDay; }
        else if (days < 0 && ns > 0) { days++; ns -= NsPerDay; }
        int sign = ns < 0 ? -1 : 1;
        long absNs = Math.Abs(ns);
        return MakeDuration(ctx, h, 0, 0, 0, ToSafeInt(days),
            sign * (int)(absNs / 3_600_000_000_000L), sign * (int)(absNs / 60_000_000_000L % 60), sign * (int)(absNs / 1_000_000_000L % 60),
            sign * (int)(absNs / 1_000_000L % 1000), sign * (int)(absNs / 1_000L % 1000), sign * (int)(absNs % 1000));
    }

    /// <summary>ToTemporalInstant for method arguments: Instant/ZonedDateTime instance or ISO string → epoch ns.</summary>
    private static long ToInstantNs(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "ens") || HasOwn(h, data, "ensBig"))
                    return DecodeInstantNanos(h, obj);
                if (HasOwn(h, data, "epochNanoseconds"))
                    return ToSafeLong(new System.Numerics.BigInteger(GetVNum(h, obj, "epochNanoseconds")));
            }

            arg = JsValue.FromString(ctx.ToStringValue(arg));
        }

        if (arg.Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal instant."));
        var s = arg.AsString();
        if (!TemporalIsoParser.TryParseInstant(s, out var parsed, out var parseError))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for Instant: {parseError}"));
        // Temporal.Instant has no calendar: any [u-ca=...] annotation (even critical, even
        // an unknown value) is parsed and ignored, never validated.
        var epochDays = IsoMath.CivilToEpochDays(parsed.Year, parsed.Month, parsed.Day);
        var ns = new System.Numerics.BigInteger(epochDays) * NsPerDay
                 + parsed.Time.ToNanosecondsOfDay()
                 - (parsed.HasUtcDesignator ? 0L : parsed.OffsetNanoseconds);
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return ToSafeLong(ns);
    }

    /// <summary>ToTemporalYearMonth: instance, ISO string, or property bag.</summary>
    private static (int Year, int Month, string Calendar) ToTemporalYearMonthRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseYearMonth(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO year-month string: {parseError}"));
            var cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            return (parsed.Year, parsed.Month, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "y"))
                return ((int)GetVNum(h, obj, "y"), (int)GetVNum(h, obj, "m"), GetVStr(h, obj, "calendarId"));

            string bagCal = GetCalendarFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double y = ToIntegerWithTruncation(ctx, yearValue);
            double m = GetMonthFromFields(ctx, h, arg);
            if (y is < -999_999 or > 999_999)
                throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
            return ((int)y, (int)Math.Clamp(m, 1, 12), bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal year-month."));
    }

    /// <summary>ToIntegerIfIntegral: RangeError unless the value converts to a finite integer.</summary>
    private static double ToIntegerIfIntegral(IBuiltinContext ctx, JsValue v)
    {
        if (v.Tag == JsValueTag.Undefined) return 0;
        var n = ctx.ToNumber(v);
        if (!double.IsFinite(n) || n != Math.Truncate(n))
            throw new JsThrownException(ctx.CreateRangeError("Duration components must be finite integers."));
        return n + 0.0; // normalize -0 to +0
    }

    /// <summary>IsValidDuration sign check: all components share one sign.</summary>
    private static void ValidateDurationSigns(IBuiltinContext ctx, params double[] components)
    {
        int sign = 0;
        foreach (var c in components)
        {
            if (c == 0) continue;
            int s = c < 0 ? -1 : 1;
            if (sign == 0) sign = s;
            else if (s != sign)
                throw new JsThrownException(ctx.CreateRangeError("Mixed-sign durations are invalid."));
        }
    }

    /// <summary>
    /// IsValidDuration (Temporal spec): sign consistency plus the range limits —
    /// abs(years|months|weeks) &lt; 2^32 and the days-through-nanoseconds part, expressed
    /// in seconds, has abs &lt; 2^53. Components are in canonical order
    /// [years, months, weeks, days, hours, minutes, seconds, ms, µs, ns].
    /// </summary>
    private static void ValidateDuration(IBuiltinContext ctx, double[] v)
    {
        ValidateDurationSigns(ctx, v);
        const double twoTo32 = 4294967296.0;       // 2^32
        const double twoTo53 = 9007199254740992.0; // 2^53
        if (Math.Abs(v[0]) >= twoTo32 || Math.Abs(v[1]) >= twoTo32 || Math.Abs(v[2]) >= twoTo32)
            throw new JsThrownException(ctx.CreateRangeError("Duration years, months, or weeks out of range."));
        double seconds = v[3] * 86400.0 + v[4] * 3600.0 + v[5] * 60.0 + v[6]
            + v[7] / 1e3 + v[8] / 1e6 + v[9] / 1e9;
        if (!(Math.Abs(seconds) < twoTo53))
            throw new JsThrownException(ctx.CreateRangeError("Duration time fields out of range."));
    }

    /// <summary>Singular unit name; plurals accepted; RangeError on anything else.</summary>
    private static string NormalizeUnitName(IBuiltinContext ctx, string unit, bool allowAuto = false)
    {
        var s = unit switch
        {
            "years" => "year", "months" => "month", "weeks" => "week", "days" => "day",
            "hours" => "hour", "minutes" => "minute", "seconds" => "second",
            "milliseconds" => "millisecond", "microseconds" => "microsecond", "nanoseconds" => "nanosecond",
            _ => unit,
        };
        bool ok = s is "year" or "month" or "week" or "day" or "hour" or "minute" or "second"
            or "millisecond" or "microsecond" or "nanosecond" || (allowAuto && s == "auto");
        if (!ok)
            throw new JsThrownException(ctx.CreateRangeError($"'{unit}' is not a valid unit."));
        return s;
    }

    /// <summary>Map a unit name (singular or plural) to its singular form, or null if unknown.</summary>
    private static string? TryNormalizeUnit(string unit)
    {
        var s = unit switch
        {
            "years" => "year", "months" => "month", "weeks" => "week", "days" => "day",
            "hours" => "hour", "minutes" => "minute", "seconds" => "second",
            "milliseconds" => "millisecond", "microseconds" => "microsecond", "nanoseconds" => "nanosecond",
            _ => unit,
        };
        return s is "year" or "month" or "week" or "day" or "hour" or "minute" or "second"
            or "millisecond" or "microsecond" or "nanosecond" ? s : null;
    }

    // Allowed difference units per Temporal type (for since/until largestUnit/smallestUnit).
    private static readonly string[] TimeDiffUnits = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
    private static readonly string[] DateTimeDiffUnits = { "year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
    private static readonly string[] DateDiffUnits = { "year", "month", "week", "day" };
    private static readonly string[] YearMonthDiffUnits = { "year", "month" };

    /// <summary>Settings produced by <see cref="GetDifferenceSettings"/> for since()/until().</summary>
    private struct DiffSettings
    {
        public string Smallest;
        public string Largest;
        public int Increment;
        public string Mode;
    }

    /// <summary>Total ordering of all ten Temporal units; rank 0 = year (largest) … 9 = nanosecond.</summary>
    private static int DiffUnitRank(string unit) => unit switch
    {
        "year" => 0, "month" => 1, "week" => 2, "day" => 3, "hour" => 4, "minute" => 5,
        "second" => 6, "millisecond" => 7, "microsecond" => 8, "nanosecond" => 9, _ => 9,
    };

    /// <summary>MaximumTemporalDurationRoundingIncrement: dividend for a smallestUnit, or 0 if unbounded.</summary>
    private static long MaxDurationRoundingIncrement(string unit) => unit switch
    {
        "hour" => 24, "minute" => 60, "second" => 60,
        "millisecond" => 1000, "microsecond" => 1000, "nanosecond" => 1000,
        _ => 0, // year/month/week/day: no maximum
    };

    private const string ValidRoundingModes = "ceil floor expand trunc halfCeil halfFloor halfExpand halfTrunc halfEven";

    /// <summary>
    /// GetDifferenceSettings for since()/until(): reads largestUnit, roundingIncrement,
    /// roundingMode, smallestUnit (in that spec order) and fully validates each, resolving
    /// "auto" largestUnit and checking the largest≥smallest constraint and the increment maximum.
    /// </summary>
    private static DiffSettings GetDifferenceSettings(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i,
        string[] allowed, string fallbackSmallest, string defaultLargest)
    {
        RequireOptionsObject(ctx, a, i);
        var s = new DiffSettings { Smallest = fallbackSmallest, Largest = "auto", Increment = 1, Mode = "trunc" };
        if (i >= a.Count || a[i].Tag != JsValueTag.Object)
        {
            s.Largest = DiffUnitRank(defaultLargest) <= DiffUnitRank(fallbackSmallest) ? defaultLargest : fallbackSmallest;
            return s;
        }
        var opt = a[i];

        // 1. largestUnit (may be "auto").
        if (TryGetField(ctx, h, opt, "largestUnit", out var lv) && lv.Tag != JsValueTag.Undefined)
        {
            var ls = lv.Tag == JsValueTag.String ? lv.AsString() : ctx.ToStringValue(lv);
            if (ls != "auto")
            {
                var norm = TryNormalizeUnit(ls);
                if (norm is null || Array.IndexOf(allowed, norm) < 0)
                    throw new JsThrownException(ctx.CreateRangeError($"'{ls}' is not a valid value for largestUnit."));
                s.Largest = norm;
            }
        }

        // 2. roundingIncrement (ToTemporalRoundingIncrement: finite, integer-truncated, in [1,1e9]).
        if (TryGetField(ctx, h, opt, "roundingIncrement", out var iv) && iv.Tag != JsValueTag.Undefined)
        {
            double inc = ctx.ToNumber(iv);
            if (double.IsNaN(inc) || double.IsInfinity(inc))
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be a finite number."));
            double trunc = Math.Truncate(inc);
            if (trunc < 1 || trunc > 1_000_000_000)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            s.Increment = (int)trunc;
        }

        // 3. roundingMode (default trunc).
        if (TryGetField(ctx, h, opt, "roundingMode", out var mv) && mv.Tag != JsValueTag.Undefined)
        {
            var ms = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
            if (Array.IndexOf(ValidRoundingModes.Split(' '), ms) < 0)
                throw new JsThrownException(ctx.CreateRangeError($"'{ms}' is not a valid rounding mode."));
            s.Mode = ms;
        }

        // 4. smallestUnit (default fallbackSmallest).
        if (TryGetField(ctx, h, opt, "smallestUnit", out var sv) && sv.Tag != JsValueTag.Undefined)
        {
            var ss = sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv);
            var norm = TryNormalizeUnit(ss);
            if (norm is null || Array.IndexOf(allowed, norm) < 0)
                throw new JsThrownException(ctx.CreateRangeError($"'{ss}' is not a valid value for smallestUnit."));
            s.Smallest = norm;
        }

        // 5/6. Resolve "auto" largestUnit to the larger of defaultLargest and smallestUnit.
        if (s.Largest == "auto")
            s.Largest = DiffUnitRank(defaultLargest) <= DiffUnitRank(s.Smallest) ? defaultLargest : s.Smallest;

        // 7. largestUnit must not be smaller than smallestUnit.
        if (DiffUnitRank(s.Largest) > DiffUnitRank(s.Smallest))
            throw new JsThrownException(ctx.CreateRangeError("largestUnit cannot be smaller than smallestUnit."));

        // 8/9. ValidateTemporalRoundingIncrement against the smallestUnit's exclusive maximum.
        long max = MaxDurationRoundingIncrement(s.Smallest);
        if (max != 0 && (s.Increment >= max || max % s.Increment != 0))
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));
        return s;
    }

    /// <summary>
    /// Round a signed nanosecond difference to the increment of smallestUnit, then balance the
    /// result into a Duration whose largest field is <paramref name="largest"/> (time units only).
    /// </summary>
    private static JsValue MakeDiffDuration(IBuiltinContext ctx, JsHeap h, long diffNs, DiffSettings s)
    {
        long unitNs = UnitNs(s.Smallest);
        long incNs = unitNs * s.Increment;
        if (incNs > 1) diffNs = RoundNsToIncrement(ctx, diffNs, incNs, s.Mode);
        return MakeDurationFromNsBalanced(ctx, h, diffNs, s.Largest);
    }

    private static bool IsCalendarUnit(string? unit) => unit is "year" or "month" or "week";

    // Rank 0 = day (largest supported without relativeTo) … 6 = nanosecond.
    private static int UnitRank(string unit) => unit switch
    {
        "day" => 0, "hour" => 1, "minute" => 2, "second" => 3,
        "millisecond" => 4, "microsecond" => 5, _ => 6,
    };

    private static long UnitNs(string unit) => unit switch
    {
        "day" => NsPerDay, "hour" => 3_600_000_000_000L, "minute" => 60_000_000_000L,
        "second" => 1_000_000_000L, "millisecond" => 1_000_000L, "microsecond" => 1_000L, _ => 1L,
    };

    /// <summary>
    /// Temporal.Instant.prototype.round: validate smallestUnit (hour..nanosecond only),
    /// roundingIncrement (must divide its day-relative maximum), and roundingMode, then
    /// round the epoch nanoseconds to the increment.
    /// </summary>
    private static long RoundInstantNs(IBuiltinContext ctx, JsHeap h, long epochNs, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string smallest;
        double increment = 1;
        string mode = "halfExpand";
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv))
            {
                increment = ToIntegerWithTruncation(ctx, iv);
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }
            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv))
            {
                mode = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
                if (mode is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mode}' is not a valid rounding mode."));
            }
            if (!TryGetField(ctx, h, a[0], "smallestUnit", out var sv) || sv.Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateRangeError("smallestUnit is required."));
            smallest = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        // Instant supports only hour and smaller; calendar/day units are out of range.
        long maximum = smallest switch
        {
            "hour" => 24L,
            "minute" => 1440L,
            "second" => 86_400L,
            "millisecond" => 86_400_000L,
            "microsecond" => 86_400_000_000L,
            "nanosecond" => 86_400_000_000_000L,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{smallest}' is not a valid smallestUnit for Instant.round.")),
        };
        // ValidateTemporalRoundingIncrement with inclusive maximum.
        if (increment > maximum || maximum % (long)increment != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        long incrementNs = (long)increment * UnitNs(smallest);
        return RoundNsToIncrement(ctx, epochNs, incrementNs, mode);
    }

    /// <summary>
    /// Temporal.PlainTime.prototype.round: validate smallestUnit (hour..nanosecond),
    /// roundingIncrement (must divide its next-larger-unit maximum, exclusive), and
    /// roundingMode, then round the time-of-day nanoseconds to the increment.
    /// </summary>
    private static long RoundPlainTimeNs(IBuiltinContext ctx, JsHeap h, long timeNs, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string smallest;
        double increment = 1;
        string mode = "halfExpand";
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv))
            {
                increment = ToIntegerWithTruncation(ctx, iv);
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }
            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv))
            {
                mode = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
                if (mode is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mode}' is not a valid rounding mode."));
            }
            if (!TryGetField(ctx, h, a[0], "smallestUnit", out var sv) || sv.Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateRangeError("smallestUnit is required."));
            smallest = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        // PlainTime supports hour and smaller; day and calendar units are out of range.
        long maximum = smallest switch
        {
            "hour" => 24L,
            "minute" => 60L,
            "second" => 60L,
            "millisecond" => 1000L,
            "microsecond" => 1000L,
            "nanosecond" => 1000L,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{smallest}' is not a valid smallestUnit for PlainTime.round.")),
        };
        // ValidateTemporalRoundingIncrement with exclusive maximum.
        if (increment >= maximum || maximum % (long)increment != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        long incrementNs = (long)increment * UnitNs(smallest);
        return RoundNsToIncrement(ctx, timeNs, incrementNs, mode);
    }

    /// <summary>
    /// Temporal.PlainDateTime.prototype.round time component: validate smallestUnit
    /// (day..nanosecond), roundingIncrement, and roundingMode, then round the time-of-day
    /// nanoseconds. Returns the carry in whole days plus the rounded time-of-day ns.
    /// </summary>
    private static (long DayCarry, long TimeNs) RoundPlainDateTimeTime(IBuiltinContext ctx, JsHeap h, long timeNs, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string smallest;
        double increment = 1;
        string mode = "halfExpand";
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv))
            {
                increment = ToIntegerWithTruncation(ctx, iv);
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }
            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv))
            {
                mode = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
                if (mode is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mode}' is not a valid rounding mode."));
            }
            if (!TryGetField(ctx, h, a[0], "smallestUnit", out var sv) || sv.Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateRangeError("smallestUnit is required."));
            smallest = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        if (smallest == "day")
        {
            // day rounding requires an increment of exactly 1; round to the nearest midnight.
            if (increment != 1)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be 1 for day rounding."));
            long carry = RoundNsToIncrement(ctx, timeNs, NsPerDay, mode) / NsPerDay;
            return (carry, 0L);
        }

        long maximum = smallest switch
        {
            "hour" => 24L,
            "minute" => 60L,
            "second" => 60L,
            "millisecond" => 1000L,
            "microsecond" => 1000L,
            "nanosecond" => 1000L,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{smallest}' is not a valid smallestUnit for PlainDateTime.round.")),
        };
        if (increment >= maximum || maximum % (long)increment != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        long rounded = RoundNsToIncrement(ctx, timeNs, (long)increment * UnitNs(smallest), mode);
        long dayCarry = rounded / NsPerDay;
        long timeOfDay = rounded % NsPerDay;
        if (timeOfDay < 0) { timeOfDay += NsPerDay; dayCarry -= 1; }
        return (dayCarry, timeOfDay);
    }

    /// <summary>Total nanoseconds of the day/time portion (caller has excluded calendar units).</summary>
    private static long DurationDayTimeNs((int years, int months, int weeks, int days, int hours, int minutes, int seconds, int millis, int micros, int nanos) d)
        => DurationToNanos(d.days, d.hours, d.minutes, d.seconds, d.millis, d.micros, d.nanos);

    /// <summary>DefaultTemporalLargestUnit for day/time durations.</summary>
    private static string DefaultLargestUnit((int years, int months, int weeks, int days, int hours, int minutes, int seconds, int millis, int micros, int nanos) d)
        => d.days != 0 ? "day" : d.hours != 0 ? "hour" : d.minutes != 0 ? "minute" : d.seconds != 0 ? "second"
            : d.millis != 0 ? "millisecond" : d.micros != 0 ? "microsecond" : d.nanos != 0 ? "nanosecond" : "second";

    /// <summary>RoundNumberToIncrement over integer nanoseconds.</summary>
    private static long RoundNsToIncrement(IBuiltinContext ctx, long total, long increment, string mode)
    {
        if (increment <= 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be positive."));
        long t = total / increment;
        long r = total % increment;
        if (r == 0) return total;
        long lower = r > 0 ? t : t - 1;
        long upper = r > 0 ? t + 1 : t;
        long absR2 = Math.Abs(r) * 2;
        long nearer = absR2 < increment ? (r > 0 ? lower : upper) : (r > 0 ? upper : lower);
        long result = mode switch
        {
            "ceil" => upper,
            "floor" => lower,
            "trunc" => t,
            "expand" => total > 0 ? upper : lower,
            "halfCeil" => absR2 == increment ? upper : nearer,
            "halfFloor" => absR2 == increment ? lower : nearer,
            "halfTrunc" => absR2 == increment ? t : nearer,
            "halfEven" => absR2 == increment ? (lower % 2 == 0 ? lower : upper) : nearer,
            _ => absR2 == increment ? (total > 0 ? upper : lower) : nearer, // halfExpand (default)
        };
        return result * increment;
    }

    // ─── toString options (precision / calendarName / offset / timeZoneName) ───

    private sealed class ToStringOptions
    {
        public int FractionalDigits = -1;          // -1 = auto
        public string? SmallestUnit;               // minute..nanosecond, wins over digits
        public string RoundingMode = "trunc";
        public string CalendarName = "auto";       // auto|always|never|critical
        public string ShowOffset = "auto";         // auto|never
        public string TimeZoneName = "auto";       // auto|never|critical
    }

    /// <summary>Read toString options in alphabetical property order, each validated.</summary>
    private static ToStringOptions GetToStringOptions(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i)
    {
        RequireOptionsObject(ctx, a, i);
        var r = new ToStringOptions();
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return r;
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());

        if (ctx.TryGetPropertyValue(obj, optionsValue, "calendarName", out var cn) && cn.Tag != JsValueTag.Undefined)
        {
            var s = ctx.ToStringValue(cn);
            if (s is not ("auto" or "always" or "never" or "critical"))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for calendarName."));
            r.CalendarName = s;
        }

        if (ctx.TryGetPropertyValue(obj, optionsValue, "fractionalSecondDigits", out var fd) && fd.Tag != JsValueTag.Undefined)
        {
            if (fd.Tag is JsValueTag.Number or JsValueTag.Int32)
            {
                double n = fd.AsNumber();
                if (double.IsNaN(n) || double.IsInfinity(n) || Math.Floor(n) < 0 || Math.Floor(n) > 9)
                    throw new JsThrownException(ctx.CreateRangeError("fractionalSecondDigits is out of range."));
                r.FractionalDigits = (int)Math.Floor(n);
            }
            else
            {
                var s = ctx.ToStringValue(fd);
                if (s != "auto")
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for fractionalSecondDigits."));
            }
        }

        if (ctx.TryGetPropertyValue(obj, optionsValue, "offset", out var ofv) && ofv.Tag != JsValueTag.Undefined)
        {
            var s = ctx.ToStringValue(ofv);
            if (s is not ("auto" or "never"))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for offset."));
            r.ShowOffset = s;
        }

        if (ctx.TryGetPropertyValue(obj, optionsValue, "roundingMode", out var rm) && rm.Tag != JsValueTag.Undefined)
        {
            var s = ctx.ToStringValue(rm);
            if (s is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid rounding mode."));
            r.RoundingMode = s;
        }

        if (ctx.TryGetPropertyValue(obj, optionsValue, "smallestUnit", out var su) && su.Tag != JsValueTag.Undefined)
        {
            var s = NormalizeUnitName(ctx, su.Tag == JsValueTag.String ? su.AsString() : ctx.ToStringValue(su));
            if (s is not ("minute" or "second" or "millisecond" or "microsecond" or "nanosecond"))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid smallestUnit for toString."));
            r.SmallestUnit = s;
        }

        if (ctx.TryGetPropertyValue(obj, optionsValue, "timeZoneName", out var tzn) && tzn.Tag != JsValueTag.Undefined)
        {
            var s = ctx.ToStringValue(tzn);
            if (s is not ("auto" or "never" or "critical"))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for timeZoneName."));
            r.TimeZoneName = s;
        }

        return r;
    }

    /// <summary>ToSecondsStringPrecision rounding increment in ns (smallestUnit wins over digits).</summary>
    private static long PrecisionIncrementNs(ToStringOptions o)
        => o.SmallestUnit is { } u ? UnitNs(u)
            : o.FractionalDigits < 0 ? 1L : (long)Math.Pow(10, 9 - o.FractionalDigits);

    /// <summary>":SS[.fff…]" seconds part per the precision options ("" at minute precision).</summary>
    private static string FormatSecondsPart(long timeOfDayNs, ToStringOptions o)
    {
        if (o.SmallestUnit == "minute") return "";
        long seconds = timeOfDayNs / 1_000_000_000L % 60;
        long frac = timeOfDayNs % 1_000_000_000L;
        int digits = o.SmallestUnit switch
        {
            "second" => 0,
            "millisecond" => 3,
            "microsecond" => 6,
            "nanosecond" => 9,
            _ => o.FractionalDigits,
        };
        string fracStr = digits switch
        {
            < 0 => frac == 0 ? "" : $".{frac:D9}".TrimEnd('0'),
            0 => "",
            _ => "." + $"{frac:D9}"[..digits],
        };
        return $":{seconds:D2}{fracStr}";
    }

    /// <summary>Calendar annotation per the calendarName option.</summary>
    private static string CalendarSuffix(JsHeap h, JsObject o, ToStringOptions opts)
    {
        var cid = GetVStr(h, o, "calendarId");
        if (string.IsNullOrEmpty(cid)) cid = "iso8601";
        return opts.CalendarName switch
        {
            "never" => "",
            "always" => $"[u-ca={cid}]",
            "critical" => $"[!u-ca={cid}]",
            _ => cid == "iso8601" ? "" : $"[u-ca={cid}]",
        };
    }

    /// <summary>Balance signed nanoseconds into a Duration capped at largestUnit.</summary>
    private static JsValue MakeDurationBalancedNs(IBuiltinContext ctx, JsHeap h, long ns, string largestUnit)
    {
        int sign = ns < 0 ? -1 : 1;
        long n = Math.Abs(ns);
        int rank = UnitRank(largestUnit);
        long days = 0, hr = 0, mi = 0, se = 0, ms = 0, us = 0;
        if (rank <= 0) { days = n / NsPerDay; n %= NsPerDay; }
        if (rank <= 1) { hr = n / 3_600_000_000_000L; n %= 3_600_000_000_000L; }
        if (rank <= 2) { mi = n / 60_000_000_000L; n %= 60_000_000_000L; }
        if (rank <= 3) { se = n / 1_000_000_000L; n %= 1_000_000_000L; }
        if (rank <= 4) { ms = n / 1_000_000L; n %= 1_000_000L; }
        if (rank <= 5) { us = n / 1_000L; n %= 1_000L; }
        return MakeDuration(ctx, h, 0, 0, 0, sign * (int)days, sign * (int)hr, sign * (int)mi, sign * (int)se,
            sign * (int)ms, sign * (int)us, sign * (int)n);
    }

    /// <summary>AddDurationToDateTime: time-of-day arithmetic with day carry, then AddISODate.</summary>
    private static JsValue AddDurationToPlainDateTime(IBuiltinContext ctx, JsHeap h, JsObject t, JsObject o,
        IReadOnlyList<JsValue> a, ObjectHandle pH, int sign)
    {
        var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
        _ = GetOverflowOption(ctx, h, a, 1);
        var date = DecodeIsoDateLong(h, o);
        long total = DecodeTimeOfDayNs(h, o)
            + sign * DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
        long dayCarry = (long)Math.Floor(total / (double)NsPerDay);
        long timeNs = total - dayCarry * NsPerDay;
        var result = IsoMath.AddIsoDate(date, sign * dur.years, sign * dur.months, sign * dur.weeks,
            sign * (double)dur.days + dayCarry, constrainIntermediate: true, out var invalid);
        if (invalid || !IsoMath.IsoDateWithinLimits(result))
            throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
        return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, result.Year, result.Month, result.Day,
            (int)(timeNs / 3_600_000_000_000L), (int)(timeNs / 60_000_000_000L % 60), (int)(timeNs / 1_000_000_000L % 60),
            (int)(timeNs / 1_000_000L % 1000), (int)(timeNs / 1_000L % 1000), (int)(timeNs % 1000),
            GetVStr(h, o, "calendarId")), pH);
    }

    /// <summary>IsValidTime check → RangeError (all components integral and in range).</summary>
    private static void ValidateTime(IBuiltinContext ctx, double hr, double mi, double se, double ms, double us, double ns)
    {
        if (hr is < 0 or > 23 || mi is < 0 or > 59 || se is < 0 or > 59
            || ms is < 0 or > 999 || us is < 0 or > 999 || ns is < 0 or > 999)
            throw new JsThrownException(ctx.CreateRangeError("Invalid time component."));
    }

    /// <summary>Decode a PlainDate's stored fields as an IsoDate (no DateTime clamping).</summary>
    private static IsoDate DecodeIsoDate(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = Math.Max(1, (int)GetVNum(h, o, "m"));
        int d = Math.Max(1, (int)GetVNum(h, o, "d"));
        return new IsoDate(y, Math.Min(m, 12), Math.Min(d, IsoMath.DaysInMonth(y, Math.Min(m, 12))));
    }

    /// <summary>Decode a PlainDateTime/ZonedDateTime's stored date fields as an IsoDate.</summary>
    private static IsoDate DecodeIsoDateLong(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "year");
        int m = Math.Max(1, (int)GetVNum(h, o, "month"));
        int d = Math.Max(1, (int)GetVNum(h, o, "day"));
        return new IsoDate(y, Math.Min(m, 12), Math.Min(d, IsoMath.DaysInMonth(y, Math.Min(m, 12))));
    }

    /// <summary>Format an ISO year: 4 digits, or signed 6 digits outside 0000-9999.</summary>
    private static string FormatIsoYear(int y)
        => y is >= 0 and <= 9999 ? y.ToString("D4") : (y < 0 ? "-" : "+") + Math.Abs(y).ToString("D6");

    /// <summary>Calendar annotation suffix for toString ([u-ca=x] for non-ISO calendars).</summary>
    private static string CalendarSuffix(JsHeap h, JsObject o)
    {
        var cid = GetVStr(h, o, "calendarId");
        return string.IsNullOrEmpty(cid) || cid == "iso8601" ? "" : $"[u-ca={cid}]";
    }

    // ─── Constructor factories ──────────────────────────────

    private static JsValue ConstructPlainDate(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainDate(year, month, day [, calendar]) — numeric args
        // via ToIntegerWithTruncation, then IsValidISODate/limits reject.
        double y = ArgInt(ctx, args, 0);
        double m = ArgInt(ctx, args, 1);
        double d = ArgInt(ctx, args, 2);
        string cal = CalendarArg(ctx, args, 3);
        return MakePlainDateRegulated(ctx, h, y, m, d, cal, "reject");
    }

    private static readonly System.Numerics.BigInteger MaxInstantNs =
        System.Numerics.BigInteger.Parse("8640000000000000000000"); // nsMaxInstant = 8.64e21

    private static JsValue ConstructInstant(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.Instant(epochNanoseconds) — exactly one BigInt argument.
        if (args.Count == 0 || args[0].Tag != JsValueTag.BigInt)
            throw new JsThrownException(ctx.CreateTypeError("Instant constructor requires a BigInt epochNanoseconds argument."));
        var ns = args[0].AsBigInt();
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return MakeInstantFromNanoseconds(h, ToSafeLong(ns));
    }

    /// <summary>CreateTemporalInstant from a parsed ISO string (offset/Z applied), range-checked.</summary>
    private static JsValue MakeInstantFromParsed(IBuiltinContext ctx, JsHeap h, ParsedIsoString p)
    {
        var epochDays = IsoMath.CivilToEpochDays(p.Year, p.Month, p.Day);
        var ns = new System.Numerics.BigInteger(epochDays) * 86_400_000_000_000L
                 + p.Time.ToNanosecondsOfDay()
                 - (p.HasUtcDesignator ? 0L : p.OffsetNanoseconds);
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return MakeInstantFromNanoseconds(h, ToSafeLong(ns));
    }

    private static JsValue ConstructPlainTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainTime(h?, m?, s?, ms?, µs?, ns?) — each component
        // defaults to 0, ToIntegerWithTruncation, then IsValidTime reject.
        double hr = ArgIntOr(ctx, args, 0, 0);
        double mi = ArgIntOr(ctx, args, 1, 0);
        double se = ArgIntOr(ctx, args, 2, 0);
        double ms = ArgIntOr(ctx, args, 3, 0);
        double us = ArgIntOr(ctx, args, 4, 0);
        double ns = ArgIntOr(ctx, args, 5, 0);
        ValidateTime(ctx, hr, mi, se, ms, us, ns);
        return MakePlainTime(ctx, h, (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns);
    }

    private static JsValue ConstructPlainDateTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainDateTime(y, mo, d, h?, mi?, s?, ms?, µs?, ns? [, calendar])
        double y = ArgInt(ctx, args, 0);
        double mo = ArgInt(ctx, args, 1);
        double d = ArgInt(ctx, args, 2);
        double hr = ArgIntOr(ctx, args, 3, 0);
        double mi = ArgIntOr(ctx, args, 4, 0);
        double se = ArgIntOr(ctx, args, 5, 0);
        double ms = ArgIntOr(ctx, args, 6, 0);
        double us = ArgIntOr(ctx, args, 7, 0);
        double ns = ArgIntOr(ctx, args, 8, 0);
        string cal = CalendarArg(ctx, args, 9);
        if (y is < -999_999 or > 999_999 || mo is < 1 or > 12 || d is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)y, (int)mo, (int)d)
            || !IsoMath.IsoDateWithinLimits(new IsoDate((int)y, (int)mo, (int)d)))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO date."));
        ValidateTime(ctx, hr, mi, se, ms, us, ns);
        return MakePlainDateTimeParts(ctx, h, (int)y, (int)mo, (int)d, (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns, cal);
    }

    private static JsValue ConstructDuration(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.Duration(y?, mo?, w?, d?, h?, mi?, s?, ms?, µs?, ns?) —
        // ToIntegerIfIntegral each component, then IsValidDuration sign check.
        var v = new double[10];
        for (int i = 0; i < 10; i++)
            v[i] = ToIntegerIfIntegral(ctx, i < args.Count ? args[i] : JsValue.Undefined);
        ValidateDuration(ctx, v);
        return MakeDuration(ctx, h,
            ToSafeInt(v[0]), ToSafeInt(v[1]), ToSafeInt(v[2]), ToSafeInt(v[3]), ToSafeInt(v[4]),
            ToSafeInt(v[5]), ToSafeInt(v[6]), ToSafeInt(v[7]), ToSafeInt(v[8]), ToSafeInt(v[9]));
    }

    /// <summary>Temporal.Duration.from(arg) — handles string, Duration object (copy), and property bag.</summary>
    private static JsValue DurationFrom(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args, ObjectHandle protoH)
    {
        if (args.Count == 0) throw new JsThrownException(ctx.CreateTypeError("Duration.from requires at least 1 argument."));
        var arg = args[0];

        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (ParseIsoDuration(s, out var y, out var mo, out var w, out var d,
                    out var hr, out var mi, out var sec, out var ms, out var us, out var ns))
                return AttachPrototype(h, MakeDuration(ctx, h, y, mo, w, d, hr, mi, sec, ms, us, ns), protoH);
            throw new JsThrownException(ctx.CreateRangeError($"Invalid duration string: {s}"));
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            // If it's already a Duration instance, copy its fields
            if (IsTemporalInstance(h, arg, protoH))
            {
                var dur = DecodeDuration(h, obj);
                return AttachPrototype(h, MakeDuration(ctx, h, dur.years, dur.months, dur.weeks, dur.days,
                    dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos), protoH);
            }
            // Property bag with ToIntegerIfIntegral + sign validation.
            var r = ToTemporalDurationRecord(ctx, h, arg);
            return AttachPrototype(h, MakeDuration(ctx, h, r.years, r.months, r.weeks, r.days,
                r.hours, r.minutes, r.seconds, r.millis, r.micros, r.nanos), protoH);
        }

        throw new JsThrownException(ctx.CreateTypeError("Duration.from: argument must be a string, Duration, or property bag."));
    }

    /// <summary>Parse an ISO 8601 duration string like "P1Y2M3DT4H5M6S".</summary>
    private static bool ParseIsoDuration(string s, out int years, out int months, out int weeks, out int days,
        out int hours, out int minutes, out int seconds, out int millis, out int micros, out int nanos)
    {
        years = months = weeks = days = hours = minutes = seconds = millis = 0;
        micros = nanos = 0;
        if (string.IsNullOrEmpty(s)) return false;
        // Optional ASCII sign; duration designators are case-insensitive.
        int sign = 1;
        int start = 0;
        if (s[0] == '+') start = 1;
        else if (s[0] == '-') { sign = -1; start = 1; }
        if (start >= s.Length || (s[start] != 'P' && s[start] != 'p')) return false;
        s = s[(start + 1)..].ToUpperInvariant();
        if (s.Length == 0) return false;
        if (sign < 0)
        {
            // Parse the absolute value, then negate every component below.
            if (!ParseIsoDuration("P" + s, out years, out months, out weeks, out days,
                    out hours, out minutes, out seconds, out millis, out micros, out nanos))
                return false;
            years = -years; months = -months; weeks = -weeks; days = -days;
            hours = -hours; minutes = -minutes; seconds = -seconds;
            millis = -millis; micros = -micros; nanos = -nanos;
            return true;
        }

        // Safely parse a long (handles values larger than int range) then clamp
        long SafeLong(string str) => long.TryParse(str, out var v) ? v : 0L;
        int SafeInt(string str) => int.TryParse(str, out var v) ? v : 0;

        var timeIdx = s.IndexOf('T');
        var datePart = timeIdx >= 0 ? s.Substring(0, timeIdx) : s;
        var timePart = timeIdx >= 0 ? s.Substring(timeIdx + 1) : "";
        // Parse date part
        var i = 0;
        while (i < datePart.Length)
        {
            var numStart = i;
            while (i < datePart.Length && char.IsDigit(datePart[i])) i++;
            if (i == numStart) return false;
            var num = SafeLong(datePart.Substring(numStart, i - numStart));
            if (i >= datePart.Length) return false;
            switch (datePart[i])
            {
                case 'Y': years = ToSafeInt(num); break;
                case 'M': months = ToSafeInt(num); break;
                case 'W': weeks = ToSafeInt(num); break;
                case 'D': days = ToSafeInt(num); break;
                default: return false;
            }
            i++;
        }
        // Parse time part
        i = 0;
        while (i < timePart.Length)
        {
            int numStart = i;
            while (i < timePart.Length && (char.IsDigit(timePart[i]) || timePart[i] == '.')) i++;
            if (i == numStart) return false;
            var numStr = timePart.Substring(numStart, i - numStart);
            if (i >= timePart.Length) return false;
            if (numStr.Contains('.'))
            {
                var dotIdx = numStr.IndexOf('.');
                var wholePart = numStr.Substring(0, dotIdx);
                var fracPart = numStr.Substring(dotIdx + 1).PadRight(9, '0');
                var whole = SafeInt(wholePart.Length > 0 ? wholePart : "0");
                switch (timePart[i])
                {
                    case 'H': hours = whole; minutes = SafeInt(fracPart.Substring(0,2)); seconds = SafeInt(fracPart.Substring(2,2)); break;
                    case 'M': minutes = whole; seconds = SafeInt(fracPart.Substring(0,2)); millis = SafeInt(fracPart.Substring(2,3)); break;
                    case 'S': seconds = whole; millis = SafeInt(fracPart.Substring(0,3)); micros = SafeInt(fracPart.Substring(3,3)); nanos = SafeInt(fracPart.Substring(6,3)); break;
                }
                i++;
            }
            else
            {
                var num = SafeLong(numStr);
                switch (timePart[i])
                {
                    case 'H': hours = ToSafeInt(num); break;
                    case 'M': minutes = ToSafeInt(num); break;
                    case 'S': seconds = ToSafeInt(num); break;
                    default: return false;
                }
                i++;
            }
        }
        return true;
    }

    private static (ObjectHandle, ObjectHandle) MakeCtor(IBuiltinContext ctx, JsHeap h, JsObject parent, ObjectHandle pH,
        string name, int len, bool ctor, Func<IBuiltinContext, JsHeap, IReadOnlyList<JsValue>, JsValue>? constructFactory = null)
    {
        var proto = new JsObject();
        proto.SetPrototype(ctx.GetObjectPrototype());
        var protoH = h.AllocateObject(proto, AllocationSite.Current());
        h.PushRoot(protoH);

        NativeFunctionObject f;
        if (ctor)
        {
            f = new NativeFunctionObject(name,
                // Called as a plain function (NewTarget undefined): Temporal constructors require `new`.
                (_, _a) => throw new JsThrownException(ctx.CreateTypeError($"Temporal.{name} must be called with new.")),
                _a =>
                {
                    if (constructFactory != null)
                        return AttachPrototype(h, constructFactory(ctx, h, _a), protoH);
                    var o = new JsObject(); o.SetPrototype(protoH);
                    o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.Undefined, false, false, false));
                    return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
                }, length: len);
            var fnCH = ctx.MaterializeFunctionConstructor();
            var fnC = h.GetObject(fnCH);
            if (ctx.TryGetPropertyValue(fnC, JsValue.FromObject(fnCH), "prototype", out var fp) && fp.Tag == JsValueTag.Object)
                f.SetPrototype(fp.AsObjectHandle());
        }
        else
            f = new NativeFunctionObject(name, (_, _a) =>
                throw new JsThrownException(ctx.CreateTypeError($"{name} is not a constructor.")), length: len);

        f.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(protoH), false, false, false));
        var cH = h.AllocateObject(f, AllocationSite.Current()); h.PushRoot(cH); h.WriteBarrier(cH, protoH);
        proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(cH), true, false, true));
        // <Type>.prototype[@@toStringTag] = "Temporal.<Type>" (non-writable, non-enumerable, configurable).
        var toStringTag = ctx.CreateWellKnownSymbol("toStringTag");
        proto.DefineOwnSymbolProperty(toStringTag.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString($"Temporal.{name}"), Writable: false, Enumerable: false, Configurable: true));
        h.WriteBarrier(protoH, cH);
        parent.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(cH), true, false, true));
        h.WriteBarrier(pH, cH);
        return (cH, protoH);
    }

    private static JsValue AttachPrototype(JsHeap h, JsValue value, ObjectHandle protoHandle)
    {
        if (value.Tag == JsValueTag.Object)
        {
            h.GetObject(value.AsObjectHandle()).SetPrototype(protoHandle);
        }

        return value;
    }

    /// <summary>Check whether value is an instance of a Temporal type (its prototype chain contains protoH).</summary>
    private static bool IsTemporalInstance(JsHeap h, JsValue v, ObjectHandle protoH)
    {
        if (v.Tag != JsValueTag.Object) return false;
        var obj = h.GetObject(v.AsObjectHandle());
        for (int i = 0; i < 64; i++) // safety bound
        {
            var p = obj.PrototypeHandle;
            if (p is not { } ph) return false;
            if (ph.Equals(protoH)) return true;
            obj = h.GetObject(ph);
        }
        return false;
    }

    /// <summary>
    /// Built-in functions that are not constructors must have Function.prototype as their
    /// [[Prototype]] (ECMA-262 ch.17). Apply it to a freshly-created native function object.
    /// </summary>
    private static void ApplyFunctionPrototype(IBuiltinContext ctx, JsHeap h, NativeFunctionObject nf)
    {
        var fnCH = ctx.MaterializeFunctionConstructor();
        var fnC = h.GetObject(fnCH);
        if (ctx.TryGetPropertyValue(fnC, JsValue.FromObject(fnCH), "prototype", out var fp) && fp.Tag == JsValueTag.Object)
            nf.SetPrototype(fp.AsObjectHandle());
    }

    private static void AddGetter(IBuiltinContext ctx, JsHeap h, ObjectHandle pH, JsObject p, string n, Func<JsObject, JsValue> g)
    {
        var brandProto = pH;
        var gf = new NativeFunctionObject("get " + n, (tv, _) =>
            IsTemporalInstance(h, tv, brandProto)
                ? g(h.GetObject(tv.AsObjectHandle()))
                : throw new JsThrownException(ctx.CreateTypeError($"get {n}: receiver is not a valid Temporal instance.")), length: 0);
        ApplyFunctionPrototype(ctx, h, gf);
        var gH = h.AllocateObject(gf, AllocationSite.Current());
        p.DefineOwnProperty(n, JsPropertyDescriptor.Accessor(JsValue.FromObject(gH), JsValue.Undefined, Enumerable: false, Configurable: true));
        h.WriteBarrier(pH, gH);
    }

    private static void AddMethod(IBuiltinContext ctx, JsHeap h, ObjectHandle pH, JsObject p, string n,
        Func<JsObject, IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        // Capture protoH for brand checking
        var brandProto = pH;
        var nf = new NativeFunctionObject(n, (tv, a) =>
        {
            if (!IsTemporalInstance(h, tv, brandProto))
                throw new JsThrownException(ctx.CreateTypeError($"{n}: receiver is not a valid Temporal instance."));
            return fn(h.GetObject(tv.AsObjectHandle()), a);
        }, length: len);
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        p.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(pH, nfH);
    }

    private static void AddStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle cH, JsObject c, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: len);
        // Static functions must have Function.prototype as their [[Prototype]]
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        c.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(cH, nfH);
    }

    // Store a value on the internal _v data object
    private static void SetV(JsHeap h, JsObject o, string k, JsValue v)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            var data = h.GetObject(dd.Value.AsObjectHandle());
            data.SetProperty(k, v);
        }
    }

    private static JsValue GetV(JsHeap h, JsObject o, string k)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            var data = h.GetObject(dd.Value.AsObjectHandle());
            if (data.TryGetProperty(k, x => h.GetObject(x), out var vd)) return vd.Value;
        }
        return JsValue.Undefined;
    }

    /// <summary>Check if an object has an own property (not inherited, not undefined).</summary>
    private static bool HasOwn(JsHeap h, JsObject o, string name)
    {
        return o.TryGetProperty(name, x => h.GetObject(x), out var vd) && vd.Value.Tag != JsValueTag.Undefined;
    }

    /// <summary>Read a numeric own-property directly from a plain object (not from _v).</summary>
    private static double ReadOwnNum(JsHeap h, JsObject o, string name)
    {
        if (o.TryGetProperty(name, x => h.GetObject(x), out var vd))
            return vd.Value.AsNumber();
        return 0;
    }

    /// <summary>Read a string own-property directly from a plain object (not from _v).</summary>
    private static string ReadOwnStr(JsHeap h, JsObject o, string name)
    {
        if (o.TryGetProperty(name, x => h.GetObject(x), out var vd) && vd.Value.Tag == JsValueTag.String)
            return vd.Value.AsString();
        return "";
    }

    /// <summary>Convert a double to int safely, clamping to int range (prevents overflow crashes).</summary>
    private static int ToSafeInt(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        if (v > int.MaxValue) return int.MaxValue;
        if (v < int.MinValue) return int.MinValue;
        return (int)v;
    }

    /// <summary>Convert a BigInteger to long safely, clamping to long range.</summary>
    private static long ToSafeLong(System.Numerics.BigInteger bi)
    {
        // Clamp the negative side to MinValue+1 so Math.Abs on the result can't overflow.
        if (bi > long.MaxValue) return long.MaxValue;
        if (bi <= long.MinValue) return long.MinValue + 1;
        return (long)bi;
    }

    private static JsObject MakeData(JsHeap h)
    {
        var d = new JsObject();
        h.AllocateObject(d, AllocationSite.Current());
        return d;
    }

    // ─── Temporal.Now ──────────────────────────────────────
    private void InstallNow(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var now = new JsObject();
        var nH = h.AllocateObject(now, AllocationSite.Current()); h.PushRoot(nH);
        t.DefineOwnProperty("Now", new JsPropertyDescriptor(JsValue.FromObject(nH), true, false, true));
        h.WriteBarrier(tH, nH);
        // Temporal.Now[@@toStringTag] = "Temporal.Now".
        var nowTag = ctx.CreateWellKnownSymbol("toStringTag");
        now.DefineOwnSymbolProperty(nowTag.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString("Temporal.Now"), Writable: false, Enumerable: false, Configurable: true));

        AddNowStatic(ctx, h, nH, now, "timeZoneId", _ => JsValue.FromString(TimeZoneInfo.Local.Id));
        AddNowStatic(ctx, h, nH, now, "instant", _ => MakeInstant(ctx, h, DateTime.UtcNow));
        AddNowStatic(ctx, h, nH, now, "plainDateISO", _ => MakePlainDate(ctx, h, DateTime.Today));
        AddNowStatic(ctx, h, nH, now, "plainTimeISO", _ => MakePlainTime(ctx, h, DateTime.UtcNow.TimeOfDay));
        AddNowStatic(ctx, h, nH, now, "plainDateTimeISO", a => {
            var timeZone = NormalizeNowTimeZoneArg(ctx, a);
            _ = timeZone;
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTime(ctx, h, DateTime.UtcNow));
        });
        AddNowStatic(ctx, h, nH, now, "zonedDateTimeISO", a => {
            string tz = CanonicalizeTimeZoneId(ctx, NormalizeNowTimeZoneArg(ctx, a));
            long ns = (DateTime.UtcNow.Ticks - Epoch.Ticks) * 100L;
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNs(ctx, h, ns, tz, "iso8601"));
        });
    }

    private static void AddNowStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle oH, JsObject o, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: 0);
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        o.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(oH, nfH);
    }

    private static string NormalizeNowTimeZoneArg(IBuiltinContext ctx, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
        {
            return "UTC";
        }

        var raw = ToStrArg(ctx, args[0]);
        if (IsBareDateTime(raw) || HasSubMinuteOffset(raw))
        {
            throw new JsThrownException(ctx.CreateRangeError("Invalid time zone string."));
        }

        var extracted = IntlDateTimeFormatting.ExtractTimeZoneId(raw);
        return string.IsNullOrWhiteSpace(extracted) ? "UTC" : extracted;
    }

    private static bool IsBareDateTime(string value)
    {
        return value.Contains('T') &&
               !value.Contains('[') &&
               !value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
               !System.Text.RegularExpressions.Regex.IsMatch(value, @"[+\-]\d{2}:?\d{2}$");
    }

    private static bool HasSubMinuteOffset(string value)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"[+\-]\d{2}:\d{2}:\d{2}") ||
               System.Text.RegularExpressions.Regex.IsMatch(value, @"[+\-]\d{4}:\d{2}");
    }

    private static JsValue AttachTemporalPrototypeByName(IBuiltinContext ctx, JsHeap h, JsObject temporal, string ctorName, JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        if (!temporal.TryGetProperty(ctorName, x => h.GetObject(x), out var ctorDescriptor) || ctorDescriptor.Value.Tag != JsValueTag.Object)
        {
            return value;
        }

        var ctor = h.GetObject(ctorDescriptor.Value.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(ctor, ctorDescriptor.Value, "prototype", out var prototypeValue) || prototypeValue.Tag != JsValueTag.Object)
        {
            return value;
        }

        h.GetObject(value.AsObjectHandle()).SetPrototype(prototypeValue.AsObjectHandle());
        return value;
    }

    // ─── Temporal.Duration ─────────────────────────────────
    private void InstallDuration(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Duration", 1, true,
            (cctx, hh, a) => ConstructDuration(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "sign", o => {
            double sum = 0;
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                sum += GetVNum(h, o, f);
            return JsValue.FromNumber(sum > 0 ? 1 : sum < 0 ? -1 : 0);
        });
        AddGetter(ctx, h, pH, p, "blank", o => {
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                if (GetVNum(h, o, f) != 0) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(true);
        });
        AddMethod(ctx, h, pH, p, "with", (o, a) => AttachPrototype(h, DurationWith(ctx, h, o, a), pH), 1);
        AddMethod(ctx, h, pH, p, "negated", (o, _) => {
            var d = DecodeDuration(h, o);
            return AttachPrototype(h, MakeDuration(ctx, h, -d.years, -d.months, -d.weeks, -d.days, -d.hours, -d.minutes, -d.seconds, -d.millis, -d.micros, -d.nanos), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "abs", (o, _) => {
            var d = DecodeDuration(h, o);
            return AttachPrototype(h, MakeDuration(ctx, h, Math.Abs(d.years), Math.Abs(d.months), Math.Abs(d.weeks), Math.Abs(d.days), Math.Abs(d.hours), Math.Abs(d.minutes), Math.Abs(d.seconds), Math.Abs(d.millis), Math.Abs(d.micros), Math.Abs(d.nanos)), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var self = DecodeDuration(h, o);
            var other = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            // AddDurations: calendar units are not allowed without relativeTo.
            if (self.years != 0 || self.months != 0 || self.weeks != 0
                || other.years != 0 || other.months != 0 || other.weeks != 0)
                throw new JsThrownException(ctx.CreateRangeError("Duration.add does not support calendar units."));
            string largest = UnitRank(DefaultLargestUnit(self)) <= UnitRank(DefaultLargestUnit(other))
                ? DefaultLargestUnit(self) : DefaultLargestUnit(other);
            return AttachPrototype(h, MakeDurationBalancedNs(ctx, h, DurationDayTimeNs(self) + DurationDayTimeNs(other), largest), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var self = DecodeDuration(h, o);
            var other = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (self.years != 0 || self.months != 0 || self.weeks != 0
                || other.years != 0 || other.months != 0 || other.weeks != 0)
                throw new JsThrownException(ctx.CreateRangeError("Duration.subtract does not support calendar units."));
            string largest = UnitRank(DefaultLargestUnit(self)) <= UnitRank(DefaultLargestUnit(other))
                ? DefaultLargestUnit(self) : DefaultLargestUnit(other);
            return AttachPrototype(h, MakeDurationBalancedNs(ctx, h, DurationDayTimeNs(self) - DurationDayTimeNs(other), largest), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => AttachPrototype(h, DurationRound(ctx, h, o, a), pH), 1);
        AddMethod(ctx, h, pH, p, "total", (o, a) => DurationTotal(ctx, h, o, a), 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatDurationOpts(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatDuration(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Duration.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => DurationFrom(ctx, h, a, pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            double totalA = DurationTotalNs(h, h.GetObject(a[0].AsObjectHandle()));
            double totalB = DurationTotalNs(h, h.GetObject(a[1].AsObjectHandle()));
            return JsValue.FromNumber(totalA < totalB ? -1 : totalA > totalB ? 1 : 0);
        }, 2);
    }

    /// <summary>Decode all Duration fields from _v object.</summary>
    private static (int years, int months, int weeks, int days, int hours, int minutes, int seconds, int millis, int micros, int nanos)
        DecodeDuration(JsHeap h, JsObject o) => (
        ToSafeInt(GetVNum(h, o, "years")),
        ToSafeInt(GetVNum(h, o, "months")),
        ToSafeInt(GetVNum(h, o, "weeks")),
        ToSafeInt(GetVNum(h, o, "days")),
        ToSafeInt(GetVNum(h, o, "hours")),
        ToSafeInt(GetVNum(h, o, "minutes")),
        ToSafeInt(GetVNum(h, o, "seconds")),
        ToSafeInt(GetVNum(h, o, "milliseconds")),
        ToSafeInt(GetVNum(h, o, "microseconds")),
        ToSafeInt(GetVNum(h, o, "nanoseconds"))
    );

    /// <summary>Duration fields → total nanoseconds.</summary>
    private static long DurationToNanos(int days, int hours, int minutes, int seconds, int millis, int micros, int nanos) =>
        (long)days * 86_400_000_000_000L +
        (long)hours * 3_600_000_000_000L +
        (long)minutes * 60_000_000_000L +
        (long)seconds * 1_000_000_000L +
        (long)millis * 1_000_000L +
        (long)micros * 1_000L +
        (long)nanos;

    /// <summary>Approximate total nanoseconds for a Duration (1 day = 86 400 × 10⁹ ns).</summary>
    private static double DurationTotalNs(JsHeap h, JsObject o)
    {
        const double perDay = 86_400_000_000_000.0;
        double ns = GetVNum(h, o, "nanoseconds");
        ns += GetVNum(h, o, "microseconds") * 1_000;
        ns += GetVNum(h, o, "milliseconds") * 1_000_000;
        ns += GetVNum(h, o, "seconds") * 1_000_000_000;
        ns += GetVNum(h, o, "minutes") * 60_000_000_000;
        ns += GetVNum(h, o, "hours") * 3_600_000_000_000;
        ns += GetVNum(h, o, "days") * perDay;
        ns += GetVNum(h, o, "weeks") * 7 * perDay;
        ns += GetVNum(h, o, "months") * 30.436875 * perDay; // avg days/month
        ns += GetVNum(h, o, "years") * 365.2425 * perDay;   // avg days/year
        return ns;
    }

    // ─── Temporal.Instant ──────────────────────────────────
    private void InstallInstant(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Instant", 1, true,
            (cctx, hh, a) => ConstructInstant(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "epochSeconds", o => GetV(h, o, "es"));
        AddGetter(ctx, h, pH, p, "epochMilliseconds", o => GetV(h, o, "ems"));
        AddGetter(ctx, h, pH, p, "epochMicroseconds", o => GetV(h, o, "eus"));
        AddGetter(ctx, h, pH, p, "epochNanoseconds", o => GetV(h, o, "ensBig"));
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
                throw new JsThrownException(ctx.CreateRangeError("Instant arithmetic does not support calendar units."));
            long totalNs = DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanos(h, o) + totalNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
                throw new JsThrownException(ctx.CreateRangeError("Instant arithmetic does not support calendar units."));
            long totalNs = DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanos(h, o) - totalNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            long otherNs = ToInstantNs(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "second");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, otherNs - DecodeInstantNanos(h, o), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            long otherNs = ToInstantNs(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "second");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, DecodeInstantNanos(h, o) - otherNs, s));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) =>
            AttachPrototype(h, MakeInstantFromNanoseconds(h, RoundInstantNs(ctx, h, DecodeInstantNanos(h, o), a)), pH), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            long otherNs = ToInstantNs(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return JsValue.FromBoolean(DecodeInstantNanos(h, o) == otherNs);
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatInstant(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => InstantToLocaleString(ctx, h, o, a), 2);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatInstant(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTimeISO", (o, a) =>
            AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", InstantToZonedDateTimeIso(ctx, h, o, a)), 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Instant.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            // ToTemporalInstant: Instant/ZonedDateTime copy, else string parse;
            // numbers and other primitives are a TypeError.
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("Instant.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                    return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanos(h, obj)), pH);
                arg = JsValue.FromString(ctx.ToStringValue(arg));
            }
            if (arg.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("Instant.from requires a string or a Temporal instant-like object."));
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseInstant(s, out var parsed, out var parseError))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for Instant: {parseError}"));
            _ = CalendarFromAnnotation(ctx, parsed.Calendar);
            return AttachPrototype(h, MakeInstantFromParsed(ctx, h, parsed), pH);
        }, 1);
        AddStatic(ctx, h, cH, c, "fromEpochSeconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMilliseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMicroseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochNanoseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1L), pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            long nsA = ToInstantNs(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            long nsB = ToInstantNs(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainDate ────────────────────────────────
    private void InstallPlainDate(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDate", 3, true,
            (cctx, hh, a) => ConstructPlainDate(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDate(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => JsValue.FromNumber(IsoMath.DayOfYear(DecodeIsoDate(h, o))));
        AddGetter(ctx, h, pH, p, "weekOfYear", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDate(h, o)).Week));
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDate(h, o)).Year));
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDate(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDate(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("with: argument must be an object."));
            var bagValue = a[0];
            var cur = DecodeIsoDate(h, o);
            string cal = CalId(h, o);
            bool hasField = TryGetField(ctx, h, bagValue, "year", out _) || TryGetField(ctx, h, bagValue, "month", out _)
                || TryGetField(ctx, h, bagValue, "monthCode", out _) || TryGetField(ctx, h, bagValue, "day", out _)
                || TryGetField(ctx, h, bagValue, "era", out _) || TryGetField(ctx, h, bagValue, "eraYear", out _);
            if (!hasField)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var iso = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, CalFields(cal, cur) ?? new CalendarFields(null, null, cur.Year, cur.Month, $"M{cur.Month:D2}", cur.Day, 0, 0, 12, false));
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, iso.Year, iso.Month, iso.Day, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var cur = DecodeIsoDate(h, o);
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, cur.Year, cur.Month, cur.Day, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            var result = IsoMath.AddIsoDate(DecodeIsoDate(h, o), dur.years, dur.months, dur.weeks,
                dur.days + (dur.hours * 3600L + dur.minutes * 60L + dur.seconds) / 86_400.0, constrainIntermediate: true, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(result))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, result.Year, result.Month, result.Day, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            var result = IsoMath.AddIsoDate(DecodeIsoDate(h, o), -dur.years, -dur.months, -dur.weeks,
                -(dur.days + (dur.hours * 3600L + dur.minutes * 60L + dur.seconds) / 86_400.0), constrainIntermediate: true, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(result))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, result.Year, result.Month, result.Day, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (other, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateDiffUnits, "day", "day");
            long days = IsoMath.ToEpochDays(other) - IsoMath.ToEpochDays(DecodeIsoDate(h, o));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, 0, 0, 0, ToSafeInt(days), 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (other, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateDiffUnits, "day", "day");
            long days = IsoMath.ToEpochDays(DecodeIsoDate(h, o)) - IsoMath.ToEpochDays(other);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, 0, 0, 0, ToSafeInt(days), 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (other, otherCal) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var self = DecodeIsoDate(h, o);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(otherCal) ? "iso8601" : otherCal);
            return JsValue.FromBoolean(IsoMath.Compare(self, other) == 0 && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDateTime", (o, a) => {
            var d = DecodeIsoDate(h, o);
            var tm = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTimeParts(ctx, h,
                d.Year, d.Month, d.Day, tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond,
                GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainYearMonth", (o, _) => {
            var d = DecodeIsoDate(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainYearMonth", MakePlainYearMonth(ctx, h, d.Year, d.Month, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainMonthDay", (o, _) => {
            var d = DecodeIsoDate(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainMonthDay", MakePlainMonthDay(ctx, h, d.Month, d.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, a) => {
            // Argument: time zone string, or { timeZone, plainTime? }.
            var d = DecodeIsoDate(h, o);
            string tzRaw;
            var tm = IsoTime.Midnight;
            if (a.Count > 0 && a[0].Tag == JsValueTag.String)
            {
                tzRaw = a[0].AsString();
            }
            else if (a.Count > 0 && a[0].Tag == JsValueTag.Object)
            {
                if (!TryGetField(ctx, h, a[0], "timeZone", out var tzv) || tzv.Tag == JsValueTag.Undefined)
                    throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: timeZone is required."));
                if (tzv.Tag != JsValueTag.String)
                    throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: timeZone must be a string."));
                tzRaw = tzv.AsString();
                if (TryGetField(ctx, h, a[0], "plainTime", out var ptv) && ptv.Tag != JsValueTag.Undefined)
                    tm = ToTemporalTimeRecord(ctx, h, ptv);
            }
            else
            {
                throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: time zone is required."));
            }
            string tz = CanonicalizeTimeZoneId(ctx, tzRaw);
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNs(ctx, h,
                TemporalTimeZones.EpochNsFromWall(tz, d, tm), tz, GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0);
            var d = DecodeIsoDate(h, o);
            return JsValue.FromString($"{FormatIsoYear(d.Year)}-{d.Month:D2}-{d.Day:D2}{CalendarSuffix(h, o, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("PlainDate.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainDate.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalDate: AnnotatedDateTime without UTC designator.
                if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainDate: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, parsed.Day)))
                    throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
                return AttachPrototype(h, MakePlainDateYmd(ctx, h, parsed.Year, parsed.Month, parsed.Day, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int cy = ToSafeInt(GetVNum(h, obj, "y")), cm = ToSafeInt(GetVNum(h, obj, "m")), cd = ToSafeInt(GetVNum(h, obj, "d"));
                    string ccal = GetVStr(h, obj, "calendarId"); if (string.IsNullOrEmpty(ccal)) ccal = "iso8601";
                    return AttachPrototype(h, MakePlainDateYmd(ctx, h, cy, cm, cd, ccal), pH);
                }
                // ToTemporalDate step 2.b: a PlainDateTime instance yields its ISO date from
                // internal slots — never via observable getters.
                if (TryGetInternalData(h, obj, out var idata) && HasOwn(h, idata, "year") && HasOwn(h, idata, "day"))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    var iso = DecodeIsoDateLong(h, obj);
                    string ical = GetVStr(h, obj, "calendarId"); if (string.IsNullOrEmpty(ical)) ical = "iso8601";
                    return AttachPrototype(h, MakePlainDateYmd(ctx, h, iso.Year, iso.Month, iso.Day, ical), pH);
                }
                // Property bag: year (or era+eraYear) + (month|monthCode) + day required.
                string cal = GetCalendarFromFields(ctx, h, arg);
                var bagIso = ResolveDateBagToIso(ctx, h, arg, cal, a, 1);
                return AttachPrototype(h, MakePlainDateYmd(ctx, h, bagIso.Year, bagIso.Month, bagIso.Day, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainDate.from: argument must be a string, PlainDate, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (one, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (two, _) = ToTemporalDateRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(IsoMath.Compare(one, two));
        }, 2);
    }

    // ─── Temporal.PlainTime ────────────────────────────────
    private void InstallPlainTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainTime", 0, true,
            (cctx, hh, a) => ConstructPlainTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainTime.with: argument must be an object."));
            var bagValue = a[0];
            string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerWithTruncation(ctx, fv);
                    any = true;
                }
                else
                {
                    values[fi] = GetVNum(h, o, fields[fi]);
                }
            }
            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one time field is required."));
            var overflow = GetOverflowOption(ctx, h, a, 1);
            if (overflow == "reject")
                ValidateTime(ctx, values[0], values[1], values[2], values[3], values[4], values[5]);
            return AttachPrototype(h, MakePlainTime(ctx, h,
                (int)Math.Clamp(values[0], 0, 23), (int)Math.Clamp(values[1], 0, 59), (int)Math.Clamp(values[2], 0, 59),
                (int)Math.Clamp(values[3], 0, 999), (int)Math.Clamp(values[4], 0, 999), (int)Math.Clamp(values[5], 0, 999)), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            long resultNs = ((selfNs + totalNs) % 86_400_000_000_000L + 86_400_000_000_000L) % 86_400_000_000_000L;
            return AttachPrototype(h, MakePlainTimeFromDayNs(ctx, h, resultNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            long resultNs = ((selfNs - totalNs) % 86_400_000_000_000L + 86_400_000_000_000L) % 86_400_000_000_000L;
            return AttachPrototype(h, MakePlainTimeFromDayNs(ctx, h, resultNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "hour");
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, other.ToNanosecondsOfDay() - selfNs, s));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "hour");
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, selfNs - other.ToNanosecondsOfDay(), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => {
            long dayNs = RoundPlainTimeNs(ctx, h, DecodeTimeOfDayNs(h, o), a) % NsPerDay;
            if (dayNs < 0) dayNs += NsPerDay;
            int hr = (int)(dayNs / 3_600_000_000_000L);
            int mi = (int)(dayNs / 60_000_000_000L % 60);
            int se = (int)(dayNs / 1_000_000_000L % 60);
            int ms = (int)(dayNs / 1_000_000L % 1000);
            int us = (int)(dayNs / 1_000L % 1000);
            int ns = (int)(dayNs % 1000);
            return AttachPrototype(h, MakePlainTime(ctx, h, hr, mi, se, ms, us, ns), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return JsValue.FromBoolean(selfNs == other.ToNanosecondsOfDay());
        }, 1);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => PlainTimeToLocaleString(ctx, h, o, a), 2);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0);
            long dayNs = DecodeTimeOfDayNs(h, o);
            long inc = PrecisionIncrementNs(opts);
            if (inc > 1) dayNs = RoundNsToIncrement(ctx, dayNs, inc, opts.RoundingMode) % NsPerDay;
            return JsValue.FromString($"{dayNs / 3_600_000_000_000L:D2}:{dayNs / 60_000_000_000L % 60:D2}{FormatSecondsPart(dayNs, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainTime.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalTime: time string without UTC designator.
                if (!TemporalIsoParser.TryParseTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainTime: {parseError}"));
                _ = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                var tm = parsed.Time;
                return AttachPrototype(h, MakePlainTime(ctx, h, tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int hr = ToSafeInt(GetVNum(h, obj, "hour")), mi = ToSafeInt(GetVNum(h, obj, "minute")),
                        se = ToSafeInt(GetVNum(h, obj, "second")), ms = ToSafeInt(GetVNum(h, obj, "millisecond")),
                        us = ToSafeInt(GetVNum(h, obj, "microsecond")), ns = ToSafeInt(GetVNum(h, obj, "nanosecond"));
                    return AttachPrototype(h, MakePlainTime(ctx, h, hr, mi, se, ms, us, ns), pH);
                }
                // Property bag: at least one time field required; overflow regulates.
                string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
                var values = new double[fields.Length];
                bool any = false;
                for (int fi = 0; fi < fields.Length; fi++)
                {
                    if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                    {
                        values[fi] = ToIntegerWithTruncation(ctx, fv);
                        any = true;
                    }
                }
                if (!any)
                    throw new JsThrownException(ctx.CreateTypeError("PlainTime.from: at least one time field is required."));
                var overflow = GetOverflowOption(ctx, h, a, 1);
                if (overflow == "constrain")
                {
                    values[0] = Math.Clamp(values[0], 0, 23);
                    values[1] = Math.Clamp(values[1], 0, 59);
                    values[2] = Math.Clamp(values[2], 0, 59);
                    values[3] = Math.Clamp(values[3], 0, 999);
                    values[4] = Math.Clamp(values[4], 0, 999);
                    values[5] = Math.Clamp(values[5], 0, 999);
                }
                else
                {
                    ValidateTime(ctx, values[0], values[1], values[2], values[3], values[4], values[5]);
                }
                return AttachPrototype(h, MakePlainTime(ctx, h,
                    (int)values[0], (int)values[1], (int)values[2], (int)values[3], (int)values[4], (int)values[5]), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainTime.from: argument must be a string, PlainTime, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            long one = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined).ToNanosecondsOfDay();
            long two = ToTemporalTimeRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined).ToNanosecondsOfDay();
            return JsValue.FromNumber(one < two ? -1 : one > two ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainDateTime ────────────────────────────
    private void InstallPlainDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDateTime", 3, true,
            (cctx, hh, a) => ConstructPlainDateTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => JsValue.FromNumber(IsoMath.DayOfYear(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "weekOfYear", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDateLong(h, o)).Week));
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDateLong(h, o)).Year));
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("with: argument must be an object."));
            var bagValue = a[0];
            var curDate = DecodeIsoDateLong(h, o);
            string cal = CalId(h, o);
            bool hasDateField = TryGetField(ctx, h, bagValue, "year", out _) || TryGetField(ctx, h, bagValue, "month", out _)
                || TryGetField(ctx, h, bagValue, "monthCode", out _) || TryGetField(ctx, h, bagValue, "day", out _)
                || TryGetField(ctx, h, bagValue, "era", out _) || TryGetField(ctx, h, bagValue, "eraYear", out _);
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var timeValues = new double[timeFields.Length];
            bool anyTime = false;
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, timeFields[fi], out var fv))
                {
                    timeValues[fi] = ToIntegerWithTruncation(ctx, fv);
                    anyTime = true;
                }
                else
                {
                    timeValues[fi] = GetVNum(h, o, timeFields[fi]);
                }
            }
            if (!hasDateField && !anyTime)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var baseFields = CalFields(cal, curDate) ?? new CalendarFields(null, null, curDate.Year, curDate.Month, $"M{curDate.Month:D2}", curDate.Day, 0, 0, 12, false);
            var date = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, out var overflow, baseFields);
            if (overflow == "reject")
                ValidateTime(ctx, timeValues[0], timeValues[1], timeValues[2], timeValues[3], timeValues[4], timeValues[5]);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, date.Year, date.Month, date.Day,
                (int)Math.Clamp(timeValues[0], 0, 23), (int)Math.Clamp(timeValues[1], 0, 59), (int)Math.Clamp(timeValues[2], 0, 59),
                (int)Math.Clamp(timeValues[3], 0, 999), (int)Math.Clamp(timeValues[4], 0, 999), (int)Math.Clamp(timeValues[5], 0, 999),
                GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var cur = DecodeIsoDateLong(h, o);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, cur.Year, cur.Month, cur.Day,
                (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"), cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainTime", (o, a) => {
            var tm = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            var cur = DecodeIsoDateLong(h, o);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, cur.Year, cur.Month, cur.Day,
                tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => AddDurationToPlainDateTime(ctx, h, t, o, a, pH, 1), 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => AddDurationToPlainDateTime(ctx, h, t, o, a, pH, -1), 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "day");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                DifferencePlainDateTimes(ctx, h, DecodeIsoDateLong(h, o), DecodeTimeOfDayNs(h, o), otherDate, otherTime.ToNanosecondsOfDay()));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "day");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                DifferencePlainDateTimes(ctx, h, otherDate, otherTime.ToNanosecondsOfDay(), DecodeIsoDateLong(h, o), DecodeTimeOfDayNs(h, o)));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => {
            var date = DecodeIsoDateLong(h, o);
            long timeNs = DecodeTimeOfDayNs(h, o);
            var (dayCarry, roundedTimeNs) = RoundPlainDateTimeTime(ctx, h, timeNs, a);
            var resultDate = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + dayCarry);
            int hr = (int)(roundedTimeNs / 3_600_000_000_000L);
            int mi = (int)(roundedTimeNs / 60_000_000_000L % 60);
            int se = (int)(roundedTimeNs / 1_000_000_000L % 60);
            int ms = (int)(roundedTimeNs / 1_000_000L % 1000);
            int us = (int)(roundedTimeNs / 1_000L % 1000);
            int ns = (int)(roundedTimeNs % 1000);
            string cal = GetVStr(h, o, "calendarId"); if (string.IsNullOrEmpty(cal)) cal = "iso8601";
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, resultDate.Year, resultDate.Month, resultDate.Day,
                hr, mi, se, ms, us, ns, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return JsValue.FromBoolean(IsoMath.Compare(DecodeIsoDateLong(h, o), otherDate) == 0
                && DecodeTimeOfDayNs(h, o) == otherTime.ToNanosecondsOfDay());
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, _) => {
            var cur = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, cur.Year, cur.Month, cur.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainTime", (o, _) => AttachTemporalPrototypeByName(ctx, h, t, "PlainTime", MakePlainTime(ctx, h,
            (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"))), 0);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0);
            var date = DecodeIsoDateLong(h, o);
            long dayNs = DecodeTimeOfDayNs(h, o);
            long inc = PrecisionIncrementNs(opts);
            if (inc > 1)
            {
                dayNs = RoundNsToIncrement(ctx, dayNs, inc, opts.RoundingMode);
                long carry = dayNs / NsPerDay;
                if (carry != 0)
                {
                    date = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + carry);
                    dayNs -= carry * NsPerDay;
                }
            }
            return JsValue.FromString($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}" +
                $"T{dayNs / 3_600_000_000_000L:D2}:{dayNs / 60_000_000_000L % 60:D2}{FormatSecondsPart(dayNs, opts)}{CalendarSuffix(h, o, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, a) => {
            if (a.Count == 0 || a[0].Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime requires a time zone string."));
            _ = GetDisambiguationOption(ctx, h, a, 1);
            string tz = CanonicalizeTimeZoneId(ctx, a[0].AsString());
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNs(ctx, h,
                TemporalTimeZones.EpochNsFromWall(tz, DecodeIsoDateLong(h, o), time), tz, GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalDateTime: AnnotatedDateTime without UTC designator;
                // the time part is optional (midnight when absent).
                if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainDateTime: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, parsed.Day)))
                    throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
                var tm = parsed.HasTime ? parsed.Time : IsoTime.Midnight;
                return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, parsed.Year, parsed.Month, parsed.Day,
                    tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    return AttachPrototype(h, MakePlainDateTimeParts(ctx, h,
                        ToSafeInt(GetVNum(h, obj, "year")), ToSafeInt(GetVNum(h, obj, "month")), ToSafeInt(GetVNum(h, obj, "day")),
                        ToSafeInt(GetVNum(h, obj, "hour")), ToSafeInt(GetVNum(h, obj, "minute")), ToSafeInt(GetVNum(h, obj, "second")),
                        ToSafeInt(GetVNum(h, obj, "millisecond")), ToSafeInt(GetVNum(h, obj, "microsecond")), ToSafeInt(GetVNum(h, obj, "nanosecond")),
                        GetVStr(h, obj, "calendarId")), pH);
                }
                // Property bag: year (or era+eraYear) + (month|monthCode) + day; time fields optional.
                string cal = GetCalendarFromFields(ctx, h, arg);
                var calSys = CalendarMath.Get(cal);
                double y = 0, mo = 0, d = 0;
                int nativeYear = 0, nativeMonth = 0, nativeDay = 0;
                if (calSys is null)
                {
                    if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                        throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: year is required."));
                    y = ToIntegerWithTruncation(ctx, yearValue);
                    mo = GetMonthFromFields(ctx, h, arg);
                    if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                        throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: day is required."));
                    d = ToIntegerWithTruncation(ctx, dayValue);
                }
                else
                {
                    (nativeYear, nativeMonth, nativeDay) = ResolveCalendarDateFields(ctx, h, arg, calSys, null, requireDay: true);
                }
                double hr = TryGetField(ctx, h, arg, "hour", out var hv) ? ToIntegerWithTruncation(ctx, hv) : 0;
                double mi = TryGetField(ctx, h, arg, "minute", out var miv) ? ToIntegerWithTruncation(ctx, miv) : 0;
                double se = TryGetField(ctx, h, arg, "second", out var sev) ? ToIntegerWithTruncation(ctx, sev) : 0;
                double ms = TryGetField(ctx, h, arg, "millisecond", out var msv) ? ToIntegerWithTruncation(ctx, msv) : 0;
                double us = TryGetField(ctx, h, arg, "microsecond", out var usv) ? ToIntegerWithTruncation(ctx, usv) : 0;
                double ns = TryGetField(ctx, h, arg, "nanosecond", out var nsv) ? ToIntegerWithTruncation(ctx, nsv) : 0;
                var overflow = GetOverflowOption(ctx, h, a, 1);
                IsoDate date;
                if (calSys is null)
                {
                    if (y is < -999_999 or > 999_999)
                        throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
                    date = RegulateIsoDate(ctx, y, mo, d, overflow);
                }
                else
                {
                    if (!calSys.TryResolveToIso(nativeYear, nativeMonth, nativeDay, overflow, out date))
                        throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar or outside the supported range."));
                }
                if (overflow == "constrain")
                {
                    hr = Math.Clamp(hr, 0, 23); mi = Math.Clamp(mi, 0, 59); se = Math.Clamp(se, 0, 59);
                    ms = Math.Clamp(ms, 0, 999); us = Math.Clamp(us, 0, 999); ns = Math.Clamp(ns, 0, 999);
                }
                else
                {
                    ValidateTime(ctx, hr, mi, se, ms, us, ns);
                }
                return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, date.Year, date.Month, date.Day,
                    (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: argument must be a string, PlainDateTime, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (d1, t1) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (d2, t2) = ToTemporalDateTimeRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            int dateCmp = IsoMath.Compare(d1, d2);
            if (dateCmp != 0) return JsValue.FromNumber(dateCmp);
            long n1 = t1.ToNanosecondsOfDay(), n2 = t2.ToNanosecondsOfDay();
            return JsValue.FromNumber(n1 < n2 ? -1 : n1 > n2 ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainYearMonth ───────────────────────────
    private static JsValue ConstructPlainYearMonth(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainYearMonth(year, month [, calendar [, referenceISODay]])
        double y = ArgInt(ctx, args, 0);
        double m = ArgInt(ctx, args, 1);
        string cal = CalendarArg(ctx, args, 2);
        double refDay = ArgIntOr(ctx, args, 3, 1);
        if (y is < -999_999 or > 999_999 || m is < 1 or > 12 || refDay is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)y, (int)m, (int)refDay)
            || !IsoMath.IsoDateWithinLimits(new IsoDate((int)y, (int)m, (int)refDay)))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO year-month."));
        return MakePlainYearMonth(ctx, h, (int)y, (int)m, cal);
    }

    private void InstallPlainYearMonth(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainYearMonth", 2, true,
            (cctx, hh, a) => ConstructPlainYearMonth(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "year", o => GetV(h, o, "y"));
        AddGetter(ctx, h, pH, p, "month", o => GetV(h, o, "m"));
        AddGetter(ctx, h, pH, p, "monthCode", o => JsValue.FromString($"M{(int)GetVNum(h, o, "m"):D2}"));
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "daysInMonth", o => JsValue.FromNumber(IsoMath.DaysInMonth((int)GetVNum(h, o, "y"), Math.Clamp((int)GetVNum(h, o, "m"), 1, 12))));
        AddGetter(ctx, h, pH, p, "daysInYear", o => JsValue.FromNumber(IsoMath.DaysInYear((int)GetVNum(h, o, "y"))));
        AddGetter(ctx, h, pH, p, "monthsInYear", o => JsValue.FromNumber(12));
        AddGetter(ctx, h, pH, p, "inLeapYear", o => JsValue.FromBoolean(IsoMath.IsLeapYear((int)GetVNum(h, o, "y"))));
        // era/eraYear are undefined for the iso8601 calendar.
        AddGetter(ctx, h, pH, p, "era", _ => JsValue.Undefined);
        AddGetter(ctx, h, pH, p, "eraYear", _ => JsValue.Undefined);
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.with: argument must be an object."));
            var bagValue = a[0];
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out var yearValue);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _) || TryGetField(ctx, h, bagValue, "monthCode", out _);
            if (!hasYear && !hasMonth)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            double y = hasYear ? ToIntegerWithTruncation(ctx, yearValue) : GetVNum(h, o, "y");
            double m = hasMonth ? GetMonthFromFields(ctx, h, bagValue) : GetVNum(h, o, "m");
            var overflow = GetOverflowOption(ctx, h, a, 1);
            if (y is < -999_999 or > 999_999)
                throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
            if (overflow == "reject" && m is < 1 or > 12)
                throw new JsThrownException(ctx.CreateRangeError("Invalid ISO year-month."));
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, (int)y, (int)Math.Clamp(m, 1, 12), GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            var dt = DecodePlainYearMonth(h, o);
            dt = SafeAddDate(dt, dur.years, dur.months, 0);
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, dt.Year, dt.Month, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            var dt = DecodePlainYearMonth(h, o);
            dt = SafeAddDate(dt, -dur.years, -dur.months, 0);
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, dt.Year, dt.Month, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (oy, om, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, YearMonthDiffUnits, "month", "year");
            int totalMonths = (oy - (int)GetVNum(h, o, "y")) * 12 + (om - (int)GetVNum(h, o, "m"));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, totalMonths / 12, totalMonths % 12, 0, 0, 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (oy, om, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, YearMonthDiffUnits, "month", "year");
            int totalMonths = ((int)GetVNum(h, o, "y") - oy) * 12 + ((int)GetVNum(h, o, "m") - om);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, totalMonths / 12, totalMonths % 12, 0, 0, 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (oy, om, ocal) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(ocal) ? "iso8601" : ocal);
            return JsValue.FromBoolean((int)GetVNum(h, o, "y") == oy && (int)GetVNum(h, o, "m") == om && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, a) => {
            int day = 1;
            if (a.Count > 0 && a[0].Tag == JsValueTag.Object && TryGetField(ctx, h, a[0], "day", out var dv))
                day = ToSafeInt(ToIntegerWithTruncation(ctx, dv));
            int y = (int)GetVNum(h, o, "y");
            int m = Math.Clamp((int)GetVNum(h, o, "m"), 1, 12);
            day = Math.Clamp(day, 1, IsoMath.DaysInMonth(y, m));
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, y, m, day, GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0);
            int y = (int)GetVNum(h, o, "y");
            int m = (int)GetVNum(h, o, "m");
            string suffix = CalendarSuffix(h, o, opts);
            // With a calendar annotation the reference ISO day is included.
            string day = suffix.Length > 0 ? "-01" : "";
            return JsValue.FromString($"{FormatIsoYear(y)}-{m:D2}{day}{suffix}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainYearMonth(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                if (!TemporalIsoParser.TryParseYearMonth(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainYearMonth: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, 1)))
                    throw new JsThrownException(ctx.CreateRangeError("Year-month is outside the supported Temporal range."));
                return AttachPrototype(h, MakePlainYearMonth(ctx, h, parsed.Year, parsed.Month, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int y = ToSafeInt(GetVNum(h, obj, "y")), m = ToSafeInt(GetVNum(h, obj, "m"));
                    return AttachPrototype(h, MakePlainYearMonth(ctx, h, y, m, GetVStr(h, obj, "calendarId")), pH);
                }
                // Property bag: year + (month|monthCode) required.
                string cal = GetCalendarFromFields(ctx, h, arg);
                if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                    throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from: year is required."));
                double y2 = ToIntegerWithTruncation(ctx, yearValue);
                double m2 = GetMonthFromFields(ctx, h, arg);
                var overflow = GetOverflowOption(ctx, h, a, 1);
                if (y2 is < -999_999 or > 999_999)
                    throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
                int month2;
                if (overflow == "constrain")
                    month2 = (int)Math.Clamp(m2, 1, 12);
                else if (m2 is < 1 or > 12)
                    throw new JsThrownException(ctx.CreateRangeError("Invalid ISO year-month."));
                else
                    month2 = (int)m2;
                return AttachPrototype(h, MakePlainYearMonth(ctx, h, (int)y2, month2, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from: argument must be a string or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (y1, m1, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (y2, m2, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            int c1 = y1 != y2 ? (y1 < y2 ? -1 : 1) : m1 != m2 ? (m1 < m2 ? -1 : 1) : 0;
            return JsValue.FromNumber(c1);
        }, 2);
    }

    // ─── Temporal.PlainMonthDay ────────────────────────────
    private static JsValue ConstructPlainMonthDay(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainMonthDay(month, day [, calendar [, referenceISOYear]])
        double m = ArgInt(ctx, args, 0);
        double d = ArgInt(ctx, args, 1);
        string cal = CalendarArg(ctx, args, 2);
        double refYear = ArgIntOr(ctx, args, 3, 1972);
        if (refYear is < -999_999 or > 999_999 || m is < 1 or > 12 || d is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)refYear, (int)m, (int)d))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO month-day."));
        return MakePlainMonthDay(ctx, h, (int)m, (int)d, cal);
    }

    private void InstallPlainMonthDay(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainMonthDay", 2, true,
            (cctx, hh, a) => ConstructPlainMonthDay(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "monthCode", o => GetV(h, o, "mc"));
        AddGetter(ctx, h, pH, p, "day", o => GetV(h, o, "d"));
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.with: argument must be an object."));
            var bag = h.GetObject(a[0].AsObjectHandle());
            int m; var mc2 = ReadOwnStr(h, bag, "monthCode");
            if (!string.IsNullOrEmpty(mc2) && mc2.StartsWith("M") && int.TryParse(mc2.Substring(1), out var mp2)) m = mp2;
            else if (HasOwn(h, bag, "month")) m = ToSafeInt(ReadOwnNum(h, bag, "month"));
            else { var cmc = GetVStr(h, o, "mc"); m = (!string.IsNullOrEmpty(cmc) && cmc.StartsWith("M") && int.TryParse(cmc.Substring(1), out var cp)) ? cp : 1; }
            int d = HasOwn(h, bag, "day") ? ToSafeInt(ReadOwnNum(h, bag, "day")) : ToSafeInt(GetVNum(h, o, "d"));
            return MakePlainMonthDay(ctx, h, m, d);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            var other = h.GetObject(a[0].AsObjectHandle());
            return JsValue.FromBoolean(GetVStr(h, o, "mc") == GetVStr(h, other, "mc") && GetVNum(h, o, "d") == GetVNum(h, other, "d"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0);
            var mc = GetVStr(h, o, "mc");
            int d = (int)GetVNum(h, o, "d");
            int m = mc.StartsWith("M") && int.TryParse(mc.Substring(1), out var parsedMonth) ? parsedMonth : 1;
            string suffix = CalendarSuffix(h, o, opts);
            // With a calendar annotation the reference ISO year is included.
            string year = suffix.Length > 0 ? "1972-" : "";
            return JsValue.FromString($"{year}{m:D2}-{d:D2}{suffix}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainMonthDay(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                if (!TemporalIsoParser.TryParseMonthDay(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainMonthDay: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                return AttachPrototype(h, MakePlainMonthDay(ctx, h, parsed.Month, parsed.Day, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int d = ToSafeInt(GetVNum(h, obj, "d"));
                    var mc = GetVStr(h, obj, "mc");
                    int m = mc.Length == 3 && mc[0] == 'M' && int.TryParse(mc[1..], out var mp) ? mp : 1;
                    return AttachPrototype(h, MakePlainMonthDay(ctx, h, m, d, GetVStr(h, obj, "calendarId")), pH);
                }
                // Property bag: day + (monthCode | month-with-year) required.
                string cal = GetCalendarFromFields(ctx, h, arg);
                if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                    throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.from: day is required."));
                double d2 = ToIntegerWithTruncation(ctx, dayValue);
                bool hasCode = TryGetField(ctx, h, arg, "monthCode", out _);
                if (!hasCode && TryGetField(ctx, h, arg, "month", out _) && !TryGetField(ctx, h, arg, "year", out _))
                    throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.from: month requires year (or use monthCode)."));
                double m2 = GetMonthFromFields(ctx, h, arg);
                double refYear = TryGetField(ctx, h, arg, "year", out var yv) && !hasCode ? ToIntegerWithTruncation(ctx, yv) : 1972;
                var overflow = GetOverflowOption(ctx, h, a, 1);
                int month2;
                int day2;
                if (refYear is < -999_999 or > 999_999)
                    throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
                if (overflow == "constrain")
                {
                    month2 = (int)Math.Clamp(m2, 1, 12);
                    day2 = (int)Math.Clamp(d2, 1, IsoMath.DaysInMonth((int)refYear, month2));
                }
                else
                {
                    if (m2 is < 1 or > 12 || d2 is < 1 or > 31 || !IsoMath.IsValidIsoDate((int)refYear, (int)m2, (int)d2))
                        throw new JsThrownException(ctx.CreateRangeError("Invalid ISO month-day."));
                    month2 = (int)m2;
                    day2 = (int)d2;
                }
                return AttachPrototype(h, MakePlainMonthDay(ctx, h, month2, day2, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.from: argument must be a string or property bag."));
        }, 1);
    }

    // ─── Temporal.ZonedDateTime ────────────────────────────

    /// <summary>ToTemporalTimeZoneIdentifier: identifier or ISO string with [tz] → canonical form, or RangeError.</summary>
    private static string CanonicalizeTimeZoneId(IBuiltinContext ctx, string id)
    {
        if (TemporalTimeZones.TryCanonicalize(id, out var canonical, out _))
            return canonical;

        // An ISO date-time string also names a zone: its [tz] annotation,
        // or UTC for a 'Z' designator, or its numeric offset.
        if (TemporalIsoParser.TryParseDateTime(id, out var parsed, out _))
        {
            if (parsed.TimeZoneAnnotation is { } ann)
            {
                if (TemporalTimeZones.TryCanonicalize(ann, out canonical, out _))
                    return canonical;
            }
            else if (parsed.HasUtcDesignator)
            {
                return "UTC";
            }
            else if (parsed.HasOffset && !parsed.OffsetSubMinuteSyntax)
            {
                return TemporalTimeZones.FormatOffset(parsed.OffsetNanoseconds);
            }
        }

        throw new JsThrownException(ctx.CreateRangeError($"'{id}' is not a valid time zone."));
    }

    /// <summary>ToTemporalZonedDateTime: instance, ISO string with [tz], or property bag → epoch ns + zone + calendar.</summary>
    private static (long EpochNs, string Tz, string Calendar) ToTemporalZonedRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for ZonedDateTime: {parseError}"));
            if (parsed.TimeZoneAnnotation is null)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' has no time zone annotation; required for ZonedDateTime."));
            string tz = CanonicalizeTimeZoneId(ctx, parsed.TimeZoneAnnotation);
            string cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            if (!IsoMath.IsoDateWithinLimits(date))
                throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
            var time = parsed.HasTime ? parsed.Time : IsoTime.Midnight;
            long epochNs;
            if (parsed.HasUtcDesignator)
            {
                long days = Math.Clamp(IsoMath.ToEpochDays(date), -106_751L, 106_751L);
                epochNs = days * NsPerDay + time.ToNanosecondsOfDay();
            }
            else if (parsed.HasOffset)
            {
                long days = Math.Clamp(IsoMath.ToEpochDays(date), -106_751L, 106_751L);
                epochNs = days * NsPerDay + time.ToNanosecondsOfDay() - parsed.OffsetNanoseconds;
            }
            else
            {
                epochNs = TemporalTimeZones.EpochNsFromWall(tz, date, time);
            }

            return (epochNs, tz, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "tz"))
                return (DecodeInstantNanos(h, obj), GetVStr(h, obj, "tz"), GetVStr(h, obj, "calendarId") is { Length: > 0 } c ? c : "iso8601");

            // Property bag: timeZone required, plus a PlainDateTime-style bag.
            string bagCal = GetCalendarFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "timeZone", out var tzValue))
                throw new JsThrownException(ctx.CreateTypeError("timeZone is required."));
            if (tzValue.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("timeZone must be a string."));
            string bagTz = CanonicalizeTimeZoneId(ctx, tzValue.AsString());
            var bagSys = CalendarMath.Get(bagCal);
            IsoDate bagDate;
            if (bagSys is null)
            {
                if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                    throw new JsThrownException(ctx.CreateTypeError("year is required."));
                double y = ToIntegerWithTruncation(ctx, yearValue);
                double m = GetMonthFromFields(ctx, h, arg);
                if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                    throw new JsThrownException(ctx.CreateTypeError("day is required."));
                double d = ToIntegerWithTruncation(ctx, dayValue);
                bagDate = RegulateIsoDate(ctx, y, m, d, "constrain");
            }
            else
            {
                var (cy, cmo, cd) = ResolveCalendarDateFields(ctx, h, arg, bagSys, null, requireDay: true);
                if (!bagSys.TryResolveToIso(cy, cmo, cd, "constrain", out bagDate))
                    throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar or outside the supported range."));
            }
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var tv = new double[timeFields.Length];
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, timeFields[fi], out var fv))
                    tv[fi] = ToIntegerWithTruncation(ctx, fv);
            }

            var bagTime = new IsoTime(
                (int)Math.Clamp(tv[0], 0, 23), (int)Math.Clamp(tv[1], 0, 59), (int)Math.Clamp(tv[2], 0, 59),
                (int)Math.Clamp(tv[3], 0, 999), (int)Math.Clamp(tv[4], 0, 999), (int)Math.Clamp(tv[5], 0, 999));
            return (TemporalTimeZones.EpochNsFromWall(bagTz, bagDate, bagTime), bagTz, bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal.ZonedDateTime."));
    }

    private static JsValue ConstructZonedDateTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.ZonedDateTime(epochNanoseconds: BigInt, timeZone: string [, calendar])
        if (args.Count == 0 || args[0].Tag != JsValueTag.BigInt)
            throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime constructor requires a BigInt epochNanoseconds argument."));
        var ns = args[0].AsBigInt();
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("epochNanoseconds is outside the supported range."));
        if (args.Count < 2 || args[1].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime constructor requires a time zone string."));
        string tz = CanonicalizeTimeZoneId(ctx, args[1].AsString());
        string cal = CalendarArg(ctx, args, 2);
        return MakeZonedDateTimeNs(ctx, h, ToSafeLong(ns), tz, cal);
    }

    /// <summary>Decode a ZonedDateTime's wall-clock time-of-day in nanoseconds.</summary>
    private static long ZonedWallTimeNs(JsHeap h, JsObject o) => DecodeTimeOfDayNs(h, o);

    /// <summary>Epoch ns of midnight (start of day) for the instance's wall date.</summary>
    private static long ZonedStartOfDayNs(JsHeap h, JsObject o)
        => TemporalTimeZones.EpochNsFromWall(GetVStr(h, o, "tz"), DecodeIsoDateLong(h, o), IsoTime.Midnight);

    /// <summary>CreateTemporalZonedDateTime: epoch ns + zone + calendar, with the wall-clock fields cached in _v.</summary>
    private static JsValue MakeZonedDateTimeNs(IBuiltinContext ctx, JsHeap h, long epochNs, string tz, string calendarId)
    {
        long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNs);
        var (date, time) = TemporalTimeZones.WallFromEpochNs(epochNs, offsetNs);
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("year", JsValue.FromNumber(date.Year));
        d.SetProperty("month", JsValue.FromNumber(date.Month));
        d.SetProperty("day", JsValue.FromNumber(date.Day));
        d.SetProperty("hour", JsValue.FromNumber(time.Hour));
        d.SetProperty("minute", JsValue.FromNumber(time.Minute));
        d.SetProperty("second", JsValue.FromNumber(time.Second));
        d.SetProperty("millisecond", JsValue.FromNumber(time.Millisecond));
        d.SetProperty("microsecond", JsValue.FromNumber(time.Microsecond));
        d.SetProperty("nanosecond", JsValue.FromNumber(time.Nanosecond));
        d.SetProperty("epochSeconds", JsValue.FromNumber(Math.Floor(epochNs / 1_000_000_000.0)));
        d.SetProperty("epochMilliseconds", JsValue.FromNumber(Math.Floor(epochNs / 1_000_000.0)));
        d.SetProperty("epochMicroseconds", JsValue.FromNumber(Math.Floor(epochNs / 1_000.0)));
        d.SetProperty("ens", JsValue.FromNumber((double)epochNs));
        d.SetProperty("ensBig", JsValue.FromBigInt(epochNs));
        d.SetProperty("offsetNanoseconds", JsValue.FromNumber(offsetNs));
        d.SetProperty("tz", JsValue.FromString(tz));
        d.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private void InstallZonedDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "ZonedDateTime", 2, true,
            (cctx, hh, a) => ConstructZonedDateTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond",
            "epochSeconds", "epochMilliseconds", "epochMicroseconds", "offsetNanoseconds" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "epochNanoseconds", o => GetV(h, o, "ensBig"));
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => JsValue.FromNumber(IsoMath.DayOfYear(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "weekOfYear", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDateLong(h, o)).Week));
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => JsValue.FromNumber(IsoMath.WeekOfYear(DecodeIsoDateLong(h, o)).Year));
        AddGetter(ctx, h, pH, p, "hoursInDay", o => {
            var date = DecodeIsoDateLong(h, o);
            string tz = GetVStr(h, o, "tz");
            long start = TemporalTimeZones.EpochNsFromWall(tz, date, IsoTime.Midnight);
            long nextDays = IsoMath.ToEpochDays(date) + 1;
            long end = TemporalTimeZones.EpochNsFromWall(tz, IsoMath.EpochDaysToCivil(nextDays), IsoTime.Midnight);
            return JsValue.FromNumber((end - start) / 3_600_000_000_000.0);
        });
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "offset", o => JsValue.FromString(TemporalTimeZones.FormatOffset((long)GetVNum(h, o, "offsetNanoseconds"))));
        AddGetter(ctx, h, pH, p, "timeZoneId", o => GetV(h, o, "tz"));
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.with: argument must be an object."));
            var bagValue = a[0];
            var curDate = DecodeIsoDateLong(h, o);
            string cal = CalId(h, o);
            bool hasDateField = TryGetField(ctx, h, bagValue, "year", out _) || TryGetField(ctx, h, bagValue, "month", out _)
                || TryGetField(ctx, h, bagValue, "monthCode", out _) || TryGetField(ctx, h, bagValue, "day", out _)
                || TryGetField(ctx, h, bagValue, "era", out _) || TryGetField(ctx, h, bagValue, "eraYear", out _);
            bool hasOffset = TryGetField(ctx, h, bagValue, "offset", out _);
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var timeValues = new double[timeFields.Length];
            bool anyTime = false;
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, timeFields[fi], out var fv))
                {
                    timeValues[fi] = ToIntegerWithTruncation(ctx, fv);
                    anyTime = true;
                }
                else
                {
                    timeValues[fi] = GetVNum(h, o, timeFields[fi]);
                }
            }
            if (TryGetField(ctx, h, bagValue, "timeZone", out _) || TryGetField(ctx, h, bagValue, "calendar", out _))
                throw new JsThrownException(ctx.CreateTypeError("with: timeZone and calendar cannot be changed here; use withTimeZone/withCalendar."));
            if (!hasDateField && !anyTime && !hasOffset)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var baseFields = CalFields(cal, curDate) ?? new CalendarFields(null, null, curDate.Year, curDate.Month, $"M{curDate.Month:D2}", curDate.Day, 0, 0, 12, false);
            var date = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, out var overflow, baseFields);
            if (overflow == "reject")
                ValidateTime(ctx, timeValues[0], timeValues[1], timeValues[2], timeValues[3], timeValues[4], timeValues[5]);
            var time = new IsoTime(
                (int)Math.Clamp(timeValues[0], 0, 23), (int)Math.Clamp(timeValues[1], 0, 59), (int)Math.Clamp(timeValues[2], 0, 59),
                (int)Math.Clamp(timeValues[3], 0, 999), (int)Math.Clamp(timeValues[4], 0, 999), (int)Math.Clamp(timeValues[5], 0, 999));
            string tz = GetVStr(h, o, "tz");
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h,
                TemporalTimeZones.EpochNsFromWall(tz, date, time), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, DecodeInstantNanos(h, o), GetVStr(h, o, "tz"), cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withTimeZone", (o, a) => {
            if (a.Count == 0 || a[0].Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("withTimeZone: time zone must be a string."));
            var tz = CanonicalizeTimeZoneId(ctx, a[0].AsString());
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, DecodeInstantNanos(h, o), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainDate", (o, a) => {
            var (date, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            string tz = GetVStr(h, o, "tz");
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h,
                TemporalTimeZones.EpochNsFromWall(tz, date, time), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainTime", (o, a) => {
            var time = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            string tz = GetVStr(h, o, "tz");
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h,
                TemporalTimeZones.EpochNsFromWall(tz, DecodeIsoDateLong(h, o), time), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => AddDurationToZoned(ctx, h, o, a, pH, 1), 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => AddDurationToZoned(ctx, h, o, a, pH, -1), 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (otherNs, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "hour");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDurationFromNs(ctx, h, otherNs - DecodeInstantNanos(h, o)));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (otherNs, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "hour");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDurationFromNs(ctx, h, DecodeInstantNanos(h, o) - otherNs));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => ZonedRound(ctx, h, o, a, pH), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (otherNs, otherTz, otherCal) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(otherCal) ? "iso8601" : otherCal);
            bool tzEqual = string.Equals(GetVStr(h, o, "tz"), otherTz, StringComparison.OrdinalIgnoreCase);
            return JsValue.FromBoolean(DecodeInstantNanos(h, o) == otherNs && tzEqual && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "startOfDay", (o, _) =>
            AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, ZonedStartOfDayNs(h, o), GetVStr(h, o, "tz"), GetVStr(h, o, "calendarId")), pH), 0);
        AddMethod(ctx, h, pH, p, "getTimeZoneTransition", (o, a) => {
            // Direction is required: "next"/"previous" or { direction }.
            if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateTypeError("getTimeZoneTransition requires a direction."));
            string direction;
            if (a[0].Tag == JsValueTag.String) direction = a[0].AsString();
            else if (a[0].Tag == JsValueTag.Object)
            {
                if (!TryGetField(ctx, h, a[0], "direction", out var dv))
                    throw new JsThrownException(ctx.CreateRangeError("direction is required."));
                direction = dv.Tag == JsValueTag.String ? dv.AsString() : ctx.ToStringValue(dv);
            }
            else throw new JsThrownException(ctx.CreateTypeError("getTimeZoneTransition: invalid argument."));
            if (direction is not ("next" or "previous"))
                throw new JsThrownException(ctx.CreateRangeError($"'{direction}' is not a valid transition direction."));
            // No transition table is exposed; correct for UTC and offset zones.
            return JsValue.Null;
        }, 1);
        AddMethod(ctx, h, pH, p, "toInstant", (o, _) =>
            AttachTemporalPrototypeByName(ctx, h, t, "Instant", MakeInstantFromNanoseconds(h, DecodeInstantNanos(h, o))), 0);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, _) => {
            var d = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, d.Year, d.Month, d.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainTime", (o, _) => AttachTemporalPrototypeByName(ctx, h, t, "PlainTime", MakePlainTime(ctx, h,
            (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"))), 0);
        AddMethod(ctx, h, pH, p, "toPlainDateTime", (o, _) => {
            var d = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTimeParts(ctx, h, d.Year, d.Month, d.Day,
                (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"),
                GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatZonedDateTime(ctx, h, o, GetToStringOptions(ctx, h, a, 0)), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatZonedDateTime(ctx, h, o, new ToStringOptions()), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.from requires at least 1 argument."));
            RequireOptionsObject(ctx, a, 1);
            var (ns, tz, cal) = ToTemporalZonedRecord(ctx, h, a[0]);
            _ = GetOverflowOption(ctx, h, a, 1);
            _ = GetDisambiguationOption(ctx, h, a, 1);
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, ns, tz, cal), pH);
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (nsA, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (nsB, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    /// <summary>AddZonedDateTime: calendar units on the wall date (re-resolved in the zone), time units on the epoch.</summary>
    private static JsValue AddDurationToZoned(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a, ObjectHandle pH, int sign)
    {
        var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
        _ = GetOverflowOption(ctx, h, a, 1);
        string tz = GetVStr(h, o, "tz");
        long epochNs = DecodeInstantNanos(h, o);
        if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
        {
            var date = DecodeIsoDateLong(h, o);
            var newDate = IsoMath.AddIsoDate(date, sign * dur.years, sign * dur.months, sign * dur.weeks, sign * (double)dur.days,
                constrainIntermediate: true, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(newDate))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            epochNs = TemporalTimeZones.EpochNsFromWall(tz, newDate, time);
        }

        epochNs += sign * DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
        return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, epochNs, tz, GetVStr(h, o, "calendarId")), pH);
    }

    /// <summary>ZonedDateTime.prototype.round: round the wall time, re-resolve in the zone.</summary>
    private static JsValue ZonedRound(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a, ObjectHandle pH)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string? smallest = null;
        double increment = 1;
        string mode = "halfExpand";
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv))
            {
                increment = ToIntegerWithTruncation(ctx, iv);
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }
            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv))
            {
                mode = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
                if (mode is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mode}' is not a valid rounding mode."));
            }
            if (TryGetField(ctx, h, a[0], "smallestUnit", out var sv))
                smallest = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
            if (smallest is null)
                throw new JsThrownException(ctx.CreateRangeError("round requires smallestUnit."));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        if (IsCalendarUnit(smallest))
            throw new JsThrownException(ctx.CreateRangeError($"'{smallest}' is not a valid value for smallest unit."));
        if (smallest != "day")
        {
            long maxInc = smallest == "hour" ? 24 : smallest is "minute" or "second" ? 60 : 1000;
            if (increment >= maxInc || maxInc % (long)increment != 0)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly."));
        }
        else if (increment != 1)
        {
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be 1 for day."));
        }

        string tz = GetVStr(h, o, "tz");
        var date = DecodeIsoDateLong(h, o);
        long timeNs = ZonedWallTimeNs(h, o);
        if (smallest == "day")
        {
            // Round relative to the actual day length in the zone.
            long start = TemporalTimeZones.EpochNsFromWall(tz, date, IsoTime.Midnight);
            long end = TemporalTimeZones.EpochNsFromWall(tz, IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + 1), IsoTime.Midnight);
            long self = DecodeInstantNanos(h, o);
            long rounded = RoundNsToIncrement(ctx, self - start, end - start, mode) + start;
            return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h, rounded == start ? start : end, tz, GetVStr(h, o, "calendarId")), pH);
        }

        long roundedTime = RoundNsToIncrement(ctx, timeNs, (long)increment * UnitNs(smallest), mode);
        long dayCarry = roundedTime / NsPerDay;
        roundedTime -= dayCarry * NsPerDay;
        var newDate = dayCarry == 0 ? date : IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + dayCarry);
        var newTime = new IsoTime(
            (int)(roundedTime / 3_600_000_000_000L), (int)(roundedTime / 60_000_000_000L % 60), (int)(roundedTime / 1_000_000_000L % 60),
            (int)(roundedTime / 1_000_000L % 1000), (int)(roundedTime / 1_000L % 1000), (int)(roundedTime % 1000));
        return AttachPrototype(h, MakeZonedDateTimeNs(ctx, h,
            TemporalTimeZones.EpochNsFromWall(tz, newDate, newTime), tz, GetVStr(h, o, "calendarId")), pH);
    }

    // ─── Temporal.Calendar ─────────────────────────────────
    private void InstallCalendar(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Calendar", 1, true);
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "id", o => JsValue.FromString("iso8601"));
        AddMethod(ctx, h, pH, p, "toString", (o, _) => JsValue.FromString("iso8601"), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => JsValue.FromString("iso8601"), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", _ => MakeCalendar(ctx, h, "iso8601"), 1);
    }

    // ─── Temporal.TimeZone ─────────────────────────────────
    private void InstallTimeZone(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "TimeZone", 1, true);
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "id", o => JsValue.FromString("UTC"));
        AddMethod(ctx, h, pH, p, "getOffsetNanosecondsFor", (o, _) => JsValue.FromNumber(0), 1);
        AddMethod(ctx, h, pH, p, "getNextTransition", (o, _) => JsValue.Null, 1);
        AddMethod(ctx, h, pH, p, "getPreviousTransition", (o, _) => JsValue.Null, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => JsValue.FromString("UTC"), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => JsValue.FromString("UTC"), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", _ => MakeTimeZone(ctx, h, "UTC"), 1);
    }

    // ─── Factory methods ───────────────────────────────────
    private static JsValue MakeDuration(IBuiltinContext ctx, JsHeap h, TimeSpan ts)
    {
        return MakeDuration(ctx, h, 0, 0, 0, ts.Days, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, 0, 0);
    }

    private static JsValue MakeDuration(IBuiltinContext ctx, JsHeap h,
        int years, int months, int weeks, int days,
        int hours, int minutes, int seconds, int millis, int micros, int nanos)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("years", JsValue.FromNumber(years));
        d.SetProperty("months", JsValue.FromNumber(months));
        d.SetProperty("weeks", JsValue.FromNumber(weeks));
        d.SetProperty("days", JsValue.FromNumber(days));
        d.SetProperty("hours", JsValue.FromNumber(hours));
        d.SetProperty("minutes", JsValue.FromNumber(minutes));
        d.SetProperty("seconds", JsValue.FromNumber(seconds));
        d.SetProperty("milliseconds", JsValue.FromNumber(millis));
        d.SetProperty("microseconds", JsValue.FromNumber(micros));
        d.SetProperty("nanoseconds", JsValue.FromNumber(nanos));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeInstant(IBuiltinContext ctx, JsHeap h, DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Local) dt = dt.ToUniversalTime();
        var ns = (dt.Ticks - Epoch.Ticks) * 100L;
        return MakeInstantFromNanoseconds(h, ns);
    }

    private static JsValue MakeInstantFromNanoseconds(JsHeap h, long ns)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("es", JsValue.FromNumber((double)ns / 1_000_000_000));
        d.SetProperty("ems", JsValue.FromNumber((double)ns / 1_000_000));
        d.SetProperty("eus", JsValue.FromNumber((double)ns / 1_000));
        d.SetProperty("ens", JsValue.FromNumber((double)ns));
        d.SetProperty("ensBig", JsValue.FromBigInt(ns));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeInstantEpoch(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, long mul)
    {
        var v = a.Count > 0 ? ctx.ToNumber(a[0]) : 0;
        var ns = (long)(v * mul);
        var dt = Epoch.AddTicks(ns / 100L);
        return MakeInstant(ctx, h, dt);
    }

    private static JsValue MakePlainDate(IBuiltinContext ctx, JsHeap h, DateTime dt, string calendarId = "iso8601")
        => MakePlainDateYmd(ctx, h, dt.Year, dt.Month, dt.Day, calendarId);

    private static JsValue MakePlainDateYmd(IBuiltinContext ctx, JsHeap h, int y, int m, int d, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var data = new JsObject(); var dH = h.AllocateObject(data, AllocationSite.Current());
        data.SetProperty("y", JsValue.FromNumber(y));
        data.SetProperty("m", JsValue.FromNumber(m));
        data.SetProperty("d", JsValue.FromNumber(d));
        data.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainTime(IBuiltinContext ctx, JsHeap h, TimeSpan ts)
    {
        return MakePlainTime(ctx, h, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, 0, 0);
    }

    private static JsValue MakePlainTimeFromDayNs(IBuiltinContext ctx, JsHeap h, long dayNs)
    {
        return MakePlainTime(ctx, h,
            (int)(dayNs / 3_600_000_000_000L), (int)(dayNs / 60_000_000_000L % 60), (int)(dayNs / 1_000_000_000L % 60),
            (int)(dayNs / 1_000_000L % 1000), (int)(dayNs / 1_000L % 1000), (int)(dayNs % 1000));
    }

    /// <summary>
    /// Balance a signed nanosecond total into a time-only Duration whose largest populated field
    /// is <paramref name="largest"/> (one of hour, minute, second, millisecond, microsecond,
    /// nanosecond). Components are emitted as doubles so a large second/millisecond count that
    /// overflows int is preserved exactly.
    /// </summary>
    private static JsValue MakeDurationFromNsBalanced(IBuiltinContext ctx, JsHeap h, long totalNs, string largest)
    {
        if (totalNs == long.MinValue) totalNs = long.MinValue + 1;
        double sign = totalNs < 0 ? -1 : 1;
        long abs = Math.Abs(totalNs);
        double hours = 0, minutes = 0, seconds = 0, millis = 0, micros = 0, nanos = 0;
        switch (largest)
        {
            case "hour":
                hours = abs / 3_600_000_000_000L; abs %= 3_600_000_000_000L;
                minutes = abs / 60_000_000_000L; abs %= 60_000_000_000L;
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "minute":
                minutes = abs / 60_000_000_000L; abs %= 60_000_000_000L;
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "second":
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "millisecond":
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "microsecond":
                micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            default: // nanosecond
                nanos = abs;
                break;
        }
        return MakeDurationD(ctx, h, 0, 0, 0, 0,
            sign * hours, sign * minutes, sign * seconds, sign * millis, sign * micros, sign * nanos);
    }

    /// <summary>Build a Duration from double-valued fields (avoids int overflow on large totals).</summary>
    private static JsValue MakeDurationD(IBuiltinContext ctx, JsHeap h,
        double years, double months, double weeks, double days,
        double hours, double minutes, double seconds, double millis, double micros, double nanos)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("years", JsValue.FromNumber(years));
        d.SetProperty("months", JsValue.FromNumber(months));
        d.SetProperty("weeks", JsValue.FromNumber(weeks));
        d.SetProperty("days", JsValue.FromNumber(days));
        d.SetProperty("hours", JsValue.FromNumber(hours));
        d.SetProperty("minutes", JsValue.FromNumber(minutes));
        d.SetProperty("seconds", JsValue.FromNumber(seconds));
        d.SetProperty("milliseconds", JsValue.FromNumber(millis));
        d.SetProperty("microseconds", JsValue.FromNumber(micros));
        d.SetProperty("nanoseconds", JsValue.FromNumber(nanos));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    /// <summary>Balance a signed nanosecond total into an hours..nanoseconds Duration.</summary>
    private static JsValue MakeDurationFromNs(IBuiltinContext ctx, JsHeap h, long totalNs)
    {
        // Epoch differences are unchecked; a wrapped MinValue must not reach Math.Abs.
        if (totalNs == long.MinValue) totalNs = long.MinValue + 1;
        int sign = totalNs < 0 ? -1 : 1;
        long absNs = Math.Abs(totalNs);
        return MakeDuration(ctx, h, 0, 0, 0, 0,
            sign * (int)(absNs / 3_600_000_000_000L), sign * (int)(absNs / 60_000_000_000L % 60), sign * (int)(absNs / 1_000_000_000L % 60),
            sign * (int)(absNs / 1_000_000L % 1000), sign * (int)(absNs / 1_000L % 1000), sign * (int)(absNs % 1000));
    }

    private static JsValue MakePlainTime(IBuiltinContext ctx, JsHeap h,
        int hour, int minute, int second, int millisecond, int microsecond, int nanosecond)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("hour", JsValue.FromNumber(hour));
        d.SetProperty("minute", JsValue.FromNumber(minute));
        d.SetProperty("second", JsValue.FromNumber(second));
        d.SetProperty("millisecond", JsValue.FromNumber(millisecond));
        d.SetProperty("microsecond", JsValue.FromNumber(microsecond));
        d.SetProperty("nanosecond", JsValue.FromNumber(nanosecond));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainDateTime(IBuiltinContext ctx, JsHeap h, DateTime dt, string calendarId = "iso8601")
        => MakePlainDateTimeParts(ctx, h, dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, 0, 0, calendarId);

    private static JsValue MakePlainDateTimeParts(IBuiltinContext ctx, JsHeap h,
        int y, int mo, int d, int hr, int mi, int se, int ms, int us, int ns, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var data = new JsObject(); var dH = h.AllocateObject(data, AllocationSite.Current());
        data.SetProperty("year", JsValue.FromNumber(y));
        data.SetProperty("month", JsValue.FromNumber(mo));
        data.SetProperty("day", JsValue.FromNumber(d));
        data.SetProperty("hour", JsValue.FromNumber(hr));
        data.SetProperty("minute", JsValue.FromNumber(mi));
        data.SetProperty("second", JsValue.FromNumber(se));
        data.SetProperty("millisecond", JsValue.FromNumber(ms));
        data.SetProperty("microsecond", JsValue.FromNumber(us));
        data.SetProperty("nanosecond", JsValue.FromNumber(ns));
        data.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainYearMonth(IBuiltinContext ctx, JsHeap h, int y, int m, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("y", JsValue.FromNumber(y));
        d.SetProperty("m", JsValue.FromNumber(m));
        d.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainMonthDay(IBuiltinContext ctx, JsHeap h, int m, int d, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var dd = new JsObject(); var ddH = h.AllocateObject(dd, AllocationSite.Current());
        dd.SetProperty("mc", JsValue.FromString($"M{m:D2}"));
        dd.SetProperty("d", JsValue.FromNumber(d));
        dd.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(ddH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeZonedDateTime(IBuiltinContext ctx, JsHeap h, DateTimeOffset dto, string tz)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("year", JsValue.FromNumber(dto.Year)); d.SetProperty("month", JsValue.FromNumber(dto.Month));
        d.SetProperty("day", JsValue.FromNumber(dto.Day));
        d.SetProperty("hour", JsValue.FromNumber(dto.Hour)); d.SetProperty("minute", JsValue.FromNumber(dto.Minute));
        d.SetProperty("second", JsValue.FromNumber(dto.Second)); d.SetProperty("millisecond", JsValue.FromNumber(dto.Millisecond));
        d.SetProperty("microsecond", JsValue.FromNumber(0)); d.SetProperty("nanosecond", JsValue.FromNumber(0));
        var ns = (dto.UtcTicks - Epoch.Ticks) * 100L;
        d.SetProperty("epochSeconds", JsValue.FromNumber((double)ns / 1_000_000_000));
        d.SetProperty("epochMilliseconds", JsValue.FromNumber((double)ns / 1_000_000));
        d.SetProperty("epochMicroseconds", JsValue.FromNumber((double)ns / 1_000));
        d.SetProperty("epochNanoseconds", JsValue.FromNumber((double)ns));
        d.SetProperty("offsetNanoseconds", JsValue.FromNumber(dto.Offset.Ticks * 100L));
        d.SetProperty("tz", JsValue.FromString(tz));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        o.DefineOwnProperty("timeZoneId", new JsPropertyDescriptor(JsValue.FromString(tz), true, true, true));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeCalendar(IBuiltinContext ctx, JsHeap h, string id)
    {
        var o = new JsObject();
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(id), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeTimeZone(IBuiltinContext ctx, JsHeap h, string id)
    {
        var o = new JsObject();
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(id), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static IntlDateTimeFormatOptions ParseDateTimeFormatOptions(string locale, IBuiltinContext ctx, JsHeap h, JsValue optionsValue)
    {
        if (optionsValue.Tag != JsValueTag.Object)
        {
            return new IntlDateTimeFormatOptions(CalendarId: ParseCalendarId(locale));
        }

        var optionsObject = h.GetObject(optionsValue.AsObjectHandle());
        string? GetString(string name)
        {
            return TryGetStringProperty(ctx, h, optionsObject, optionsValue, name, out var value) ? value : null;
        }

        bool? GetBool(string name)
        {
            if (!optionsObject.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            var value = descriptor.Value;
            return value.Tag switch
            {
                JsValueTag.Boolean => value.AsBoolean(),
                _ => ctx.ToNumber(value) != 0 && !double.IsNaN(ctx.ToNumber(value)),
            };
        }

        int? GetInt(string name)
        {
            if (!optionsObject.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return (int)ctx.ToNumber(descriptor.Value);
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

    private static bool TryGetStringProperty(IBuiltinContext ctx, JsHeap h, JsObject obj, JsValue receiver, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
        {
            return false;
        }

        var property = descriptor.Value;
        value = property.Tag == JsValueTag.String ? property.AsString() : ctx.ToStringValue(property);
        return true;
    }

    /// <summary>Duration.prototype.with — merge duration-like object fields into a copy.</summary>
    private static JsValue DurationWith(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count < 1 || a[0].Tag != JsValueTag.Object)
            throw new JsThrownException(ctx.CreateTypeError("Duration.with: argument must be an object."));
        var bagValue = a[0];
        var dur = DecodeDuration(h, o);
        var current = new double[] { dur.years, dur.months, dur.weeks, dur.days, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos };
        string[] fields = { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" };
        bool any = false;
        for (int fi = 0; fi < fields.Length; fi++)
        {
            if (TryGetField(ctx, h, bagValue, fields[fi], out var fv))
            {
                current[fi] = ToIntegerIfIntegral(ctx, fv);
                any = true;
            }
        }

        if (!any)
            throw new JsThrownException(ctx.CreateTypeError("with: at least one duration field is required."));
        ValidateDurationSigns(ctx, current);
        return MakeDuration(ctx, h,
            ToSafeInt(current[0]), ToSafeInt(current[1]), ToSafeInt(current[2]), ToSafeInt(current[3]), ToSafeInt(current[4]),
            ToSafeInt(current[5]), ToSafeInt(current[6]), ToSafeInt(current[7]), ToSafeInt(current[8]), ToSafeInt(current[9]));
    }

    /// <summary>Duration.prototype.round for day/time units (relativeTo unsupported → RangeError on calendar units).</summary>
    private static JsValue DurationRound(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string? smallest = null;
        string? largest = null;
        double increment = 1;
        string mode = "halfExpand";
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            // Option reads in alphabetical order per GetRoundingIncrementOption et al.
            if (TryGetField(ctx, h, a[0], "largestUnit", out var lv))
                largest = NormalizeUnitName(ctx, lv.Tag == JsValueTag.String ? lv.AsString() : ctx.ToStringValue(lv), allowAuto: true);
            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv))
            {
                increment = ToIntegerWithTruncation(ctx, iv);
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }
            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv))
            {
                mode = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
                if (mode is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mode}' is not a valid rounding mode."));
            }
            if (TryGetField(ctx, h, a[0], "smallestUnit", out var sv))
                smallest = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
            if (smallest is null && (largest is null || largest == "auto"))
                throw new JsThrownException(ctx.CreateRangeError("round requires smallestUnit or largestUnit."));
            if (TryGetField(ctx, h, a[0], "relativeTo", out _))
                throw new JsThrownException(ctx.CreateRangeError("relativeTo is not supported."));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        var dur = DecodeDuration(h, o);
        if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || IsCalendarUnit(smallest) || (largest is not null && largest != "auto" && IsCalendarUnit(largest)))
            throw new JsThrownException(ctx.CreateRangeError("Calendar units require relativeTo (not supported)."));
        smallest ??= "nanosecond";
        string largestEff = largest is null or "auto"
            ? (UnitRank(DefaultLargestUnit(dur)) <= UnitRank(smallest) ? DefaultLargestUnit(dur) : smallest)
            : largest;
        if (UnitRank(smallest) < UnitRank(largestEff))
            throw new JsThrownException(ctx.CreateRangeError("smallestUnit is larger than largestUnit."));
        if (smallest != "day")
        {
            long maxInc = smallest == "hour" ? 24 : smallest is "minute" or "second" ? 60 : 1000;
            if (increment >= maxInc || maxInc % (long)increment != 0)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly."));
        }

        long rounded = RoundNsToIncrement(ctx, DurationDayTimeNs(dur), (long)increment * UnitNs(smallest), mode);
        return MakeDurationBalancedNs(ctx, h, rounded, largestEff);
    }

    /// <summary>Duration.prototype.total for day/time units.</summary>
    private static JsValue DurationTotal(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("total requires a unit or options argument."));
        string unit;
        if (a[0].Tag == JsValueTag.String)
        {
            unit = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            if (TryGetField(ctx, h, a[0], "relativeTo", out _))
                throw new JsThrownException(ctx.CreateRangeError("relativeTo is not supported."));
            if (!TryGetField(ctx, h, a[0], "unit", out var uv))
                throw new JsThrownException(ctx.CreateRangeError("total requires a unit."));
            unit = NormalizeUnitName(ctx, uv.Tag == JsValueTag.String ? uv.AsString() : ctx.ToStringValue(uv));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("total argument must be a string or options object."));
        }

        var dur = DecodeDuration(h, o);
        if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || IsCalendarUnit(unit))
            throw new JsThrownException(ctx.CreateRangeError("Calendar units require relativeTo (not supported)."));
        return JsValue.FromNumber(DurationDayTimeNs(dur) / (double)UnitNs(unit));
    }

    private static JsValue CloneTemporal(IBuiltinContext ctx, JsHeap h, JsObject orig)
    {
        var clone = new JsObject();
        if (orig.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
            clone.DefineOwnProperty("_v", new JsPropertyDescriptor(dd.Value, false, false, false));
        if (orig.PrototypeHandle is { } ph) clone.SetPrototype(ph);
        return JsValue.FromObject(h.AllocateObject(clone, AllocationSite.Current()));
    }
}
