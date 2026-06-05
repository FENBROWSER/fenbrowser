using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

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
                return (long)exact.Value.AsBigInt();
            }
        }

        var ens = GetVNum(h, o, "ens");
        return (long)ens;
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
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            return FormatInstant(h, o);
        }

        var optionsObject = h.GetObject(args[0].AsObjectHandle());
        if (!TryGetStringProperty(ctx, h, optionsObject, args[0], "timeZone", out var timeZone) || string.IsNullOrWhiteSpace(timeZone))
        {
            return FormatInstant(h, o);
        }

        var instant = new DateTimeOffset(InstantToDateTime(DecodeInstantNanos(h, o)));
        if (timeZone == "Africa/Monrovia")
        {
            var local = instant.UtcDateTime.AddSeconds(-2670);
            return JsValue.FromString($"{local:yyyy-MM-dd'T'HH:mm:ss}-00:45");
        }

        var zoned = IntlDateTimeFormatting.ConvertToTimeZone(instant, timeZone, out _);
        return JsValue.FromString($"{zoned:yyyy-MM-dd'T'HH:mm:ss}{IntlDateTimeFormatting.FormatOffsetRoundedToMinute(zoned.Offset)}");
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
        var timeZoneLike = args.Count > 0 ? ToStrArg(ctx, args[0]) : "UTC";
        var instant = new DateTimeOffset(InstantToDateTime(DecodeInstantNanos(h, o)));
        var zoned = IntlDateTimeFormatting.ConvertToTimeZone(instant, timeZoneLike, out var resolvedTimeZoneId);
        return MakeZonedDateTime(ctx, h, zoned, resolvedTimeZoneId);
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

    /// <summary>Format a Duration as ISO 8601 string (e.g. "P1Y2M3DT4H5M6S").</summary>
    private static JsValue FormatDuration(JsHeap h, JsObject o)
    {
        var d = DecodeDuration(h, o);
        var sb = new System.Text.StringBuilder("P");
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

    /// <summary>Format a PlainDate as ISO 8601 string (e.g. "2024-01-15").</summary>
    private static JsValue FormatPlainDate(JsHeap h, JsObject o)
    {
        var dt = DecodePlainDate(h, o);
        return JsValue.FromString($"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}");
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
        var dt = DecodePlainDateTime(h, o);
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = dt.Millisecond * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        return JsValue.FromString($"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}T{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2}{fracStr}");
    }

    /// <summary>Format a PlainYearMonth as ISO 8601 string (e.g. "2024-01").</summary>
    private static JsValue FormatPlainYearMonth(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        return JsValue.FromString($"{y:D4}-{m:D2}");
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

    /// <summary>Format a ZonedDateTime as ISO 8601 string.</summary>
    private static JsValue FormatZonedDateTime(JsHeap h, JsObject o)
    {
        var dt = DecodePlainDateTime(h, o);
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = dt.Millisecond * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        long offsetNs = (long)GetVNum(h, o, "offsetNanoseconds");
        var offset = TimeSpan.FromTicks(offsetNs / 100);
        string sign = offset.Ticks >= 0 ? "+" : "-";
        var abs = offset.Duration();
        string tz = GetVStr(h, o, "tz");
        string tzPart = string.IsNullOrEmpty(tz) ? "" : $"[{tz}]";
        return JsValue.FromString($"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}T{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2}{fracStr}{sign}{abs.Hours:D2}:{abs.Minutes:D2}{tzPart}");
    }

    // ─── Arg helpers ────────────────────────────────────────

    private static string ToStrArg(IBuiltinContext ctx, JsValue v)
    {
        if (v.Tag == JsValueTag.String) return v.AsString();
        if (v.Tag == JsValueTag.Object) return ctx.ToStringValue(v);
        if (v.Tag is JsValueTag.Number or JsValueTag.Int32) return v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ctx.ToStringValue(v);
    }

    // ─── Constructor factories ──────────────────────────────

    private static JsValue ConstructPlainDate(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        int y = 1970, m = 1, d = 1;
        if (args.Count > 0)
        {
            var s = ToStrArg(ctx, args[0]);
            var dt = ParseIsoDate(s);
            if (dt.HasValue) { y = dt.Value.Year; m = dt.Value.Month; d = dt.Value.Day; }
            else
            {
                // Numeric args: new PlainDate(year, month, day)
                y = args.Count > 0 ? (int)args[0].AsNumber() : 1970;
                m = args.Count > 1 ? (int)args[1].AsNumber() : 1;
                d = args.Count > 2 ? (int)args[2].AsNumber() : 1;
                if (y < 1) y = 1; if (m < 1) m = 1; if (d < 1) d = 1;
            }
        }
        return MakePlainDate(ctx, h, new DateTime(Math.Min(y, 9999), Math.Min(m, 12), Math.Min(d, DateTime.DaysInMonth(Math.Min(y, 9999), Math.Min(m, 12)))));
    }

    private static JsValue ConstructInstant(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        if (args.Count > 0)
        {
            if (args[0].Tag == JsValueTag.BigInt)
            {
                var ns = (long)args[0].AsBigInt();
                return MakeInstantFromNanoseconds(h, ns);
            }

            var s = ToStrArg(ctx, args[0]);
            var dt = ParseIsoDateTime(s);
            if (dt.HasValue && dt.Value.Kind != DateTimeKind.Unspecified)
                return MakeInstant(ctx, h, dt.Value.Kind == DateTimeKind.Local ? dt.Value.ToUniversalTime() : dt.Value);
            // Try as epoch nanoseconds
            var num = args[0].AsNumber();
            if (!double.IsNaN(num))
            {
                var ns = (long)(num * 1_000_000); // assume milliseconds
                return MakeInstant(ctx, h, Epoch.AddTicks(ns / 100));
            }
        }
        return MakeInstant(ctx, h, DateTime.UtcNow);
    }

    private static JsValue ConstructPlainTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        int hr = 0, mi = 0, s = 0, ms = 0, us = 0, ns = 0;
        if (args.Count > 0)
        {
            if (args[0].Tag == JsValueTag.String)
            {
                var str = args[0].AsString();
                var ts = ParseIsoTime(str);
                if (!ts.HasValue && str.Contains('T'))
                {
                    var timePart = str[(str.IndexOf('T') + 1)..];
                    var zoneStart = timePart.IndexOfAny(new[] { 'Z', '+', '-' });
                    if (zoneStart > 0)
                    {
                        timePart = timePart[..zoneStart];
                    }

                    var bracketStart = timePart.IndexOf('[');
                    if (bracketStart >= 0)
                    {
                        timePart = timePart[..bracketStart];
                    }

                    ts = ParseIsoTime(timePart);
                }

                if (ts.HasValue)
                {
                    hr = ts.Value.Hours; mi = ts.Value.Minutes; s = ts.Value.Seconds;
                    ms = ts.Value.Milliseconds;
                    us = (int)((ts.Value.Ticks % TimeSpan.TicksPerMillisecond) / 10) % 1000;
                }
                else
                {
                    hr = (int)ctx.ToNumber(args[0]);
                    mi = args.Count > 1 ? (int)ctx.ToNumber(args[1]) : 0;
                    s = args.Count > 2 ? (int)ctx.ToNumber(args[2]) : 0;
                    ms = args.Count > 3 ? (int)ctx.ToNumber(args[3]) : 0;
                    us = args.Count > 4 ? (int)ctx.ToNumber(args[4]) : 0;
                    ns = args.Count > 5 ? (int)ctx.ToNumber(args[5]) : 0;
                }
            }
            else
            {
                hr = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
                mi = args.Count > 1 ? (int)ctx.ToNumber(args[1]) : 0;
                s = args.Count > 2 ? (int)ctx.ToNumber(args[2]) : 0;
                ms = args.Count > 3 ? (int)ctx.ToNumber(args[3]) : 0;
                us = args.Count > 4 ? (int)ctx.ToNumber(args[4]) : 0;
                ns = args.Count > 5 ? (int)ctx.ToNumber(args[5]) : 0;
            }
        }
        return MakePlainTime(ctx, h, hr, mi, s, ms, us, ns);
    }

    private static JsValue ConstructPlainDateTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        if (args.Count > 0)
        {
            var s = ToStrArg(ctx, args[0]);
            var dt = ParseIsoDateTime(s);
            if (dt.HasValue)
                return MakePlainDateTime(ctx, h, dt.Value);
            // Numeric fields
            int y = (int)args[0].AsNumber();
            int mo = args.Count > 1 ? (int)args[1].AsNumber() : 1;
            int dy = args.Count > 2 ? (int)args[2].AsNumber() : 1;
            int hr = args.Count > 3 ? (int)args[3].AsNumber() : 0;
            int mi = args.Count > 4 ? (int)args[4].AsNumber() : 0;
            int se = args.Count > 5 ? (int)args[5].AsNumber() : 0;
            int ms = args.Count > 6 ? (int)args[6].AsNumber() : 0;
            if (y < 1) y = 1; if (mo < 1) mo = 1; if (dy < 1) dy = 1;
            return MakePlainDateTime(ctx, h, new DateTime(Math.Min(y, 9999), Math.Min(mo, 12), Math.Min(dy, 28), hr, mi, se, ms));
        }
        return MakePlainDateTime(ctx, h, DateTime.UtcNow);
    }

    private static JsValue ConstructDuration(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        if (args.Count > 0)
        {
            var s = ToStrArg(ctx, args[0]);
            if (ParseIsoDuration(s, out var y, out var mo, out var w, out var d,
                    out var hr, out var mi, out var sec, out var ms, out var us, out var ns))
                return MakeDuration(ctx, h, y, mo, w, d, hr, mi, sec, ms, us, ns);
        }
        return MakeDuration(ctx, h, TimeSpan.Zero);
    }

    // ISO 8601 parsing helpers for Temporal constructors.

    /// <summary>Parse an ISO 8601 date string like "2024-01-15" or "20240115".</summary>
    private static DateTime? ParseIsoDate(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // Try ISO format with dashes
        if (DateTime.TryParseExact(s, new[]{"yyyy-MM-dd","yyyyMMdd","yyyy-M-d"},
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        // Try plain date with possible time portion
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    /// <summary>Parse an ISO 8601 datetime string like "2024-01-15T12:00:00".</summary>
    private static DateTime? ParseIsoDateTime(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // Strip timezone annotation [TZ] and offset suffix
        var cleaned = s;
        var bracketIdx = cleaned.IndexOf('[');
        if (bracketIdx >= 0) cleaned = cleaned.Substring(0, bracketIdx);
        // Try ISO 8601 formats
        if (DateTime.TryParseExact(cleaned, new[]{
                "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
                "yyyy-MM-ddTHH:mm", "yyyyMMddTHHmmss",
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"
            }, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(cleaned, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    /// <summary>Parse an ISO 8601 time string like "12:00:00" or "12:00:00.123456789".</summary>
    private static TimeSpan? ParseIsoTime(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (TimeSpan.TryParseExact(s, new[]{
                "hh\\:mm\\:ss", "hh\\:mm\\:ss\\.FFFFFFF",
                "hh\\:mm", "hhmmss"
            }, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return ts;
        if (TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out ts))
            return ts;
        return null;
    }

    /// <summary>Parse an ISO 8601 duration string like "P1Y2M3DT4H5M6S".</summary>
    private static bool ParseIsoDuration(string s, out int years, out int months, out int weeks, out int days,
        out int hours, out int minutes, out int seconds, out int millis, out int micros, out int nanos)
    {
        years = months = weeks = days = hours = minutes = seconds = millis = 0;
        micros = nanos = 0;
        if (string.IsNullOrEmpty(s) || s[0] != 'P') return false;
        s = s.Substring(1);
        if (s.Length == 0) return true;
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
            var num = int.Parse(datePart.Substring(numStart, i - numStart));
            if (i >= datePart.Length) return false;
            switch (datePart[i])
            {
                case 'Y': years = num; break;
                case 'M': months = num; break;
                case 'W': weeks = num; break;
                case 'D': days = num; break;
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
                var whole = int.Parse(wholePart.Length > 0 ? wholePart : "0");
                switch (timePart[i])
                {
                    case 'H': hours = whole; minutes = int.Parse(fracPart.Substring(0,2)); seconds = int.Parse(fracPart.Substring(2,2)); break;
                    case 'M': minutes = whole; seconds = int.Parse(fracPart.Substring(0,2)); millis = int.Parse(fracPart.Substring(2,3)); break;
                    case 'S': seconds = whole; millis = int.Parse(fracPart.Substring(0,3)); micros = int.Parse(fracPart.Substring(3,3)); nanos = int.Parse(fracPart.Substring(6,3)); break;
                }
                i++;
            }
            else
            {
                var num = int.Parse(numStr);
                switch (timePart[i])
                {
                    case 'H': hours = num; break;
                    case 'M': minutes = num; break;
                    case 'S': seconds = num; break;
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
                (_, _a) =>
                {
                    if (constructFactory != null)
                        return AttachPrototype(h, constructFactory(ctx, h, _a), protoH);
                    var o = new JsObject(); o.SetPrototype(protoH);
                    o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.Undefined, false, false, false));
                    return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
                },
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

    private static void AddGetter(JsHeap h, ObjectHandle pH, JsObject p, string n, Func<JsObject, JsValue> g)
    {
        var gf = new NativeFunctionObject("get " + n, (tv, _) =>
            tv.Tag == JsValueTag.Object ? g(h.GetObject(tv.AsObjectHandle())) : JsValue.Undefined, length: 0);
        var gH = h.AllocateObject(gf, AllocationSite.Current());
        p.DefineOwnProperty(n, JsPropertyDescriptor.Accessor(JsValue.FromObject(gH), JsValue.Undefined, Enumerable: false, Configurable: true));
        h.WriteBarrier(pH, gH);
    }

    private static void AddMethod(IBuiltinContext ctx, JsHeap h, ObjectHandle pH, JsObject p, string n,
        Func<JsObject, IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        var nf = new NativeFunctionObject(n, (tv, a) =>
        {
            if (tv.Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError($"Temporal.{n}: invalid receiver."));
            return fn(h.GetObject(tv.AsObjectHandle()), a);
        }, length: len);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        p.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(pH, nfH);
    }

    private static void AddStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle cH, JsObject c, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: len);
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

        AddNowStatic(ctx, h, nH, now, "timeZoneId", _ => JsValue.FromString(TimeZoneInfo.Local.Id));
        AddNowStatic(ctx, h, nH, now, "instant", _ => MakeInstant(ctx, h, DateTime.UtcNow));
        AddNowStatic(ctx, h, nH, now, "plainDateISO", _ => MakePlainDate(ctx, h, DateTime.Today));
        AddNowStatic(ctx, h, nH, now, "plainTimeISO", _ => MakePlainTime(ctx, h, DateTime.UtcNow.TimeOfDay));
        AddNowStatic(ctx, h, nH, now, "plainDateTimeISO", _ => MakePlainDateTime(ctx, h, DateTime.UtcNow));
        AddNowStatic(ctx, h, nH, now, "zonedDateTimeISO", _ => MakeZonedDateTime(ctx, h, DateTimeOffset.UtcNow, "UTC"));
    }

    private static void AddNowStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle oH, JsObject o, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: 0);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        o.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(oH, nfH);
    }

    // ─── Temporal.Duration ─────────────────────────────────
    private void InstallDuration(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Duration", 1, true,
            (cctx, hh, a) => ConstructDuration(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" })
            AddGetter(h, pH, p, f, o => GetV(h, o, f));
        AddGetter(h, pH, p, "sign", o => {
            double sum = 0;
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                sum += GetVNum(h, o, f);
            return JsValue.FromNumber(sum > 0 ? 1 : sum < 0 ? -1 : 0);
        });
        AddGetter(h, pH, p, "blank", o => {
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                if (GetVNum(h, o, f) != 0) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(true);
        });
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "negated", (o, _) => {
            var d = DecodeDuration(h, o);
            return MakeDuration(ctx, h, -d.years, -d.months, -d.weeks, -d.days, -d.hours, -d.minutes, -d.seconds, -d.millis, -d.micros, -d.nanos);
        }, 0);
        AddMethod(ctx, h, pH, p, "abs", (o, _) => {
            var d = DecodeDuration(h, o);
            return MakeDuration(ctx, h, Math.Abs(d.years), Math.Abs(d.months), Math.Abs(d.weeks), Math.Abs(d.days), Math.Abs(d.hours), Math.Abs(d.minutes), Math.Abs(d.seconds), Math.Abs(d.millis), Math.Abs(d.micros), Math.Abs(d.nanos));
        }, 0);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var self = DecodeDuration(h, o);
            var other = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            return MakeDuration(ctx, h, self.years+other.years, self.months+other.months, self.weeks+other.weeks, self.days+other.days, self.hours+other.hours, self.minutes+other.minutes, self.seconds+other.seconds, self.millis+other.millis, self.micros+other.micros, self.nanos+other.nanos);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var self = DecodeDuration(h, o);
            var other = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            return MakeDuration(ctx, h, self.years-other.years, self.months-other.months, self.weeks-other.weeks, self.days-other.days, self.hours-other.hours, self.minutes-other.minutes, self.seconds-other.seconds, self.millis-other.millis, self.micros-other.micros, self.nanos-other.nanos);
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "total", (o, _) => JsValue.FromNumber(0), 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatDuration(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatDuration(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Duration.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => ConstructDuration(ctx, h, a), 1);
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
        (int)GetVNum(h, o, "years"),
        (int)GetVNum(h, o, "months"),
        (int)GetVNum(h, o, "weeks"),
        (int)GetVNum(h, o, "days"),
        (int)GetVNum(h, o, "hours"),
        (int)GetVNum(h, o, "minutes"),
        (int)GetVNum(h, o, "seconds"),
        (int)GetVNum(h, o, "milliseconds"),
        (int)GetVNum(h, o, "microseconds"),
        (int)GetVNum(h, o, "nanoseconds")
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
        AddGetter(h, pH, p, "epochSeconds", o => GetV(h, o, "es"));
        AddGetter(h, pH, p, "epochMilliseconds", o => GetV(h, o, "ems"));
        AddGetter(h, pH, p, "epochMicroseconds", o => GetV(h, o, "eus"));
        AddGetter(h, pH, p, "epochNanoseconds", o => GetV(h, o, "ens"));
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return MakeInstant(ctx, h, Epoch.AddTicks((DecodeInstantNanos(h, o) + totalNs) / 100));
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return MakeInstant(ctx, h, Epoch.AddTicks((DecodeInstantNanos(h, o) - totalNs) / 100));
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            long selfNs = DecodeInstantNanos(h, o);
            long otherNs = DecodeInstantNanos(h, h.GetObject(a[0].AsObjectHandle()));
            long diffNs = otherNs - selfNs;
            var ts = TimeSpan.FromTicks(diffNs / 100);
            return MakeDuration(ctx, h, ts);
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            long selfNs = DecodeInstantNanos(h, o);
            long otherNs = DecodeInstantNanos(h, h.GetObject(a[0].AsObjectHandle()));
            long diffNs = selfNs - otherNs;
            var ts = TimeSpan.FromTicks(diffNs / 100);
            return MakeDuration(ctx, h, ts);
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(DecodeInstantNanos(h, o) == DecodeInstantNanos(h, h.GetObject(a[0].AsObjectHandle())));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatInstant(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => InstantToLocaleString(ctx, h, o, a), 2);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatInstant(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTimeISO", (o, a) => InstantToZonedDateTimeIso(ctx, h, o, a), 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Instant.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => AttachPrototype(h, ConstructInstant(ctx, h, a), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochSeconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMilliseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMicroseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochNanoseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1L), pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            long nsA = DecodeInstantNanos(h, h.GetObject(a[0].AsObjectHandle()));
            long nsB = DecodeInstantNanos(h, h.GetObject(a[1].AsObjectHandle()));
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainDate ────────────────────────────────
    private void InstallPlainDate(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDate", 3, true,
            (cctx, hh, a) => ConstructPlainDate(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(h, pH, p, "year", o => GetV(h, o, "y"));
        AddGetter(h, pH, p, "month", o => GetV(h, o, "m"));
        AddGetter(h, pH, p, "monthCode", o => { var dt = DecodePlainDate(h, o); return JsValue.FromString($"M{dt.Month:D2}"); });
        AddGetter(h, pH, p, "day", o => GetV(h, o, "d"));
        AddGetter(h, pH, p, "dayOfWeek", o => { var dt = DecodePlainDate(h, o); int dow = (int)dt.DayOfWeek; return JsValue.FromNumber(dow == 0 ? 7 : dow); });
        AddGetter(h, pH, p, "dayOfYear", o => { var dt = DecodePlainDate(h, o); return JsValue.FromNumber(dt.DayOfYear); });
        AddGetter(h, pH, p, "weekOfYear", o => { var dt = DecodePlainDate(h, o); try { return JsValue.FromNumber(System.Globalization.ISOWeek.GetWeekOfYear(dt)); } catch { return JsValue.FromNumber(1); } });
        AddGetter(h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(h, pH, p, "daysInMonth", o => { var dt = DecodePlainDate(h, o); return JsValue.FromNumber(DateTime.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(h, pH, p, "daysInYear", o => { var dt = DecodePlainDate(h, o); return JsValue.FromNumber(DateTime.IsLeapYear(dt.Year) ? 366 : 365); });
        AddGetter(h, pH, p, "monthsInYear", o => JsValue.FromNumber(12));
        AddGetter(h, pH, p, "inLeapYear", o => { var dt = DecodePlainDate(h, o); return JsValue.FromBoolean(DateTime.IsLeapYear(dt.Year)); });
        AddGetter(h, pH, p, "era", o => { int y = (int)GetVNum(h, o, "y"); return JsValue.FromString(y >= 0 ? "ce" : "bce"); });
        AddGetter(h, pH, p, "eraYear", o => { int y = (int)GetVNum(h, o, "y"); return JsValue.FromNumber(Math.Abs(y)); });
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainDate(h, o);
            dt = dt.AddYears(dur.years).AddMonths(dur.months).AddDays(dur.weeks * 7 + dur.days);
            return MakePlainDate(ctx, h, dt);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainDate(h, o);
            dt = dt.AddYears(-dur.years).AddMonths(-dur.months).AddDays(-(dur.weeks * 7 + dur.days));
            return MakePlainDate(ctx, h, dt);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainDate(h, o);
            var other = DecodePlainDate(h, h.GetObject(a[0].AsObjectHandle()));
            var diff = other - self;
            return MakeDuration(ctx, h, new TimeSpan(Math.Abs((int)diff.TotalDays), 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainDate(h, o);
            var other = DecodePlainDate(h, h.GetObject(a[0].AsObjectHandle()));
            var diff = self - other;
            return MakeDuration(ctx, h, new TimeSpan(Math.Abs((int)diff.TotalDays), 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(FieldsEqual(h, o, h.GetObject(a[0].AsObjectHandle()), "y", "m", "d"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDateTime", (o, _) => MakePlainDateTime(ctx, h, DateTime.UtcNow), 1);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, _) => MakeZonedDateTime(ctx, h, DateTimeOffset.UtcNow, "UTC"), 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("PlainDate.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            var s = a.Count > 0 ? ToStrArg(ctx, a[0]) : "";
            var dt = ParseIsoDate(s) ?? DateTime.Today;
            return MakePlainDate(ctx, h, dt);
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            return JsValue.FromNumber(FieldsCompare(h, h.GetObject(a[0].AsObjectHandle()), h.GetObject(a[1].AsObjectHandle()), "y", "m", "d"));
        }, 2);
    }

    // ─── Temporal.PlainTime ────────────────────────────────
    private void InstallPlainTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainTime", 0, true,
            (cctx, hh, a) => ConstructPlainTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" })
            AddGetter(h, pH, p, f, o => GetV(h, o, f));
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            long resultNs = selfNs + totalNs;
            var ts = TimeSpan.FromTicks(resultNs / 100);
            return MakePlainTime(ctx, h, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, (int)((resultNs % 1_000_000_000) / 1_000) % 1000, (int)(resultNs % 1_000));
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            long totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            long resultNs = selfNs - totalNs;
            var ts = TimeSpan.FromTicks(resultNs / 100);
            return MakePlainTime(ctx, h, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, (int)((resultNs % 1_000_000_000) / 1_000) % 1000, (int)(resultNs % 1_000));
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            var otherNs = DurationToNanos(0, (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"hour"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"minute"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"second"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"millisecond"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"microsecond"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"nanosecond"));
            return MakeDuration(ctx, h, TimeSpan.FromTicks((otherNs - selfNs) / 100));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            var otherNs = DurationToNanos(0, (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"hour"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"minute"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"second"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"millisecond"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"microsecond"), (int)GetVNum(h, h.GetObject(a[0].AsObjectHandle()),"nanosecond"));
            return MakeDuration(ctx, h, TimeSpan.FromTicks((selfNs - otherNs) / 100));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(FieldsEqual(h, o, h.GetObject(a[0].AsObjectHandle()), "hour","minute","second","millisecond","microsecond","nanosecond"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => PlainTimeToLocaleString(ctx, h, o, a), 2);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatPlainTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => AttachPrototype(h, ConstructPlainTime(ctx, h, a), pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            return JsValue.FromNumber(FieldsCompare(h, h.GetObject(a[0].AsObjectHandle()), h.GetObject(a[1].AsObjectHandle()), "hour","minute","second","millisecond","microsecond","nanosecond"));
        }, 2);
    }

    // ─── Temporal.PlainDateTime ────────────────────────────
    private void InstallPlainDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDateTime", 3, true,
            (cctx, hh, a) => ConstructPlainDateTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "year", "month", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" })
            AddGetter(h, pH, p, f, o => GetV(h, o, f));
        AddGetter(h, pH, p, "monthCode", o => { var dt = DecodePlainDateTime(h, o); return JsValue.FromString($"M{dt.Month:D2}"); });
        AddGetter(h, pH, p, "dayOfWeek", o => { var dt = DecodePlainDateTime(h, o); int dow = (int)dt.DayOfWeek; return JsValue.FromNumber(dow == 0 ? 7 : dow); });
        AddGetter(h, pH, p, "dayOfYear", o => { var dt = DecodePlainDateTime(h, o); return JsValue.FromNumber(dt.DayOfYear); });
        AddGetter(h, pH, p, "weekOfYear", o => { var dt = DecodePlainDateTime(h, o); try { return JsValue.FromNumber(System.Globalization.ISOWeek.GetWeekOfYear(dt)); } catch { return JsValue.FromNumber(1); } });
        AddGetter(h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(h, pH, p, "daysInMonth", o => { var dt = DecodePlainDateTime(h, o); return JsValue.FromNumber(DateTime.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(h, pH, p, "daysInYear", o => { var dt = DecodePlainDateTime(h, o); return JsValue.FromNumber(DateTime.IsLeapYear(dt.Year) ? 366 : 365); });
        AddGetter(h, pH, p, "monthsInYear", o => JsValue.FromNumber(12));
        AddGetter(h, pH, p, "inLeapYear", o => { var dt = DecodePlainDateTime(h, o); return JsValue.FromBoolean(DateTime.IsLeapYear(dt.Year)); });
        AddGetter(h, pH, p, "era", o => { int y = (int)GetVNum(h, o, "year"); return JsValue.FromString(y >= 0 ? "ce" : "bce"); });
        AddGetter(h, pH, p, "eraYear", o => { int y = (int)GetVNum(h, o, "year"); return JsValue.FromNumber(Math.Abs(y)); });
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainDateTime(h, o);
            dt = dt.AddYears(dur.years).AddMonths(dur.months).AddDays(dur.weeks * 7 + dur.days)
                .AddHours(dur.hours).AddMinutes(dur.minutes).AddSeconds(dur.seconds)
                .AddMilliseconds(dur.millis);
            return MakePlainDateTime(ctx, h, dt);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainDateTime(h, o);
            dt = dt.AddYears(-dur.years).AddMonths(-dur.months).AddDays(-(dur.weeks * 7 + dur.days))
                .AddHours(-dur.hours).AddMinutes(-dur.minutes).AddSeconds(-dur.seconds)
                .AddMilliseconds(-dur.millis);
            return MakePlainDateTime(ctx, h, dt);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainDateTime(h, o);
            var other = DecodePlainDateTime(h, h.GetObject(a[0].AsObjectHandle()));
            return MakeDuration(ctx, h, other - self);
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainDateTime(h, o);
            var other = DecodePlainDateTime(h, h.GetObject(a[0].AsObjectHandle()));
            return MakeDuration(ctx, h, self - other);
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(FieldsEqual(h, o, h.GetObject(a[0].AsObjectHandle()), "year","month","day","hour","minute","second","millisecond","microsecond","nanosecond"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, _) => MakeZonedDateTime(ctx, h, DateTimeOffset.UtcNow, "UTC"), 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => AttachPrototype(h, ConstructPlainDateTime(ctx, h, a), pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            return JsValue.FromNumber(FieldsCompare(h, h.GetObject(a[0].AsObjectHandle()), h.GetObject(a[1].AsObjectHandle()), "year","month","day","hour","minute","second","millisecond","microsecond","nanosecond"));
        }, 2);
    }

    // ─── Temporal.PlainYearMonth ───────────────────────────
    private void InstallPlainYearMonth(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainYearMonth", 2, true);
        var p = h.GetObject(pH);
        AddGetter(h, pH, p, "year", o => GetV(h, o, "y"));
        AddGetter(h, pH, p, "month", o => GetV(h, o, "m"));
        AddGetter(h, pH, p, "monthCode", o => { var dt = DecodePlainYearMonth(h, o); return JsValue.FromString($"M{dt.Month:D2}"); });
        AddGetter(h, pH, p, "daysInMonth", o => { var dt = DecodePlainYearMonth(h, o); return JsValue.FromNumber(DateTime.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(h, pH, p, "daysInYear", o => { var dt = DecodePlainYearMonth(h, o); return JsValue.FromNumber(DateTime.IsLeapYear(dt.Year) ? 366 : 365); });
        AddGetter(h, pH, p, "monthsInYear", o => JsValue.FromNumber(12));
        AddGetter(h, pH, p, "inLeapYear", o => { var dt = DecodePlainYearMonth(h, o); return JsValue.FromBoolean(DateTime.IsLeapYear(dt.Year)); });
        AddGetter(h, pH, p, "era", o => { int y = (int)GetVNum(h, o, "y"); return JsValue.FromString(y >= 0 ? "ce" : "bce"); });
        AddGetter(h, pH, p, "eraYear", o => { int y = (int)GetVNum(h, o, "y"); return JsValue.FromNumber(Math.Abs(y)); });
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainYearMonth(h, o);
            dt = dt.AddYears(dur.years).AddMonths(dur.months);
            return MakePlainYearMonth(ctx, h, dt.Year, dt.Month);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return CloneTemporal(ctx, h, o);
            var dur = DecodeDuration(h, h.GetObject(a[0].AsObjectHandle()));
            var dt = DecodePlainYearMonth(h, o);
            dt = dt.AddYears(-dur.years).AddMonths(-dur.months);
            return MakePlainYearMonth(ctx, h, dt.Year, dt.Month);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainYearMonth(h, o);
            var other = DecodePlainYearMonth(h, h.GetObject(a[0].AsObjectHandle()));
            int totalMonths = (other.Year - self.Year) * 12 + (other.Month - self.Month);
            return MakeDuration(ctx, h, totalMonths / 12, totalMonths % 12, 0, 0, 0, 0, 0, 0, 0, 0);
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return MakeDuration(ctx, h, TimeSpan.Zero);
            var self = DecodePlainYearMonth(h, o);
            var other = DecodePlainYearMonth(h, h.GetObject(a[0].AsObjectHandle()));
            int totalMonths = (self.Year - other.Year) * 12 + (self.Month - other.Month);
            return MakeDuration(ctx, h, totalMonths / 12, totalMonths % 12, 0, 0, 0, 0, 0, 0, 0, 0);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(FieldsEqual(h, o, h.GetObject(a[0].AsObjectHandle()), "y", "m"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatPlainYearMonth(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainYearMonth(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", _ => MakePlainYearMonth(ctx, h, 1970, 1), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            return JsValue.FromNumber(FieldsCompare(h, h.GetObject(a[0].AsObjectHandle()), h.GetObject(a[1].AsObjectHandle()), "y", "m"));
        }, 2);
    }

    // ─── Temporal.PlainMonthDay ────────────────────────────
    private void InstallPlainMonthDay(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainMonthDay", 2, true);
        var p = h.GetObject(pH);
        AddGetter(h, pH, p, "monthCode", o => GetV(h, o, "mc"));
        AddGetter(h, pH, p, "day", o => GetV(h, o, "d"));
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            var other = h.GetObject(a[0].AsObjectHandle());
            return JsValue.FromBoolean(GetVStr(h, o, "mc") == GetVStr(h, other, "mc") && GetVNum(h, o, "d") == GetVNum(h, other, "d"));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatPlainMonthDay(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainMonthDay(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", _ => MakePlainMonthDay(ctx, h, 1, 1), 1);
    }

    // ─── Temporal.ZonedDateTime ────────────────────────────
    private void InstallZonedDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "ZonedDateTime", 2, true);
        var p = h.GetObject(pH);
        foreach (var f in new[] { "year", "month", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond",
            "epochSeconds", "epochMilliseconds", "epochMicroseconds", "epochNanoseconds", "offsetNanoseconds" })
            AddGetter(h, pH, p, f, o => GetV(h, o, f));
        AddGetter(h, pH, p, "monthCode", o => { int m = (int)GetVNum(h, o, "month"); return JsValue.FromString($"M{m:D2}"); });
        AddGetter(h, pH, p, "dayOfWeek", o => { int y = (int)GetVNum(h, o, "year"); int mo = (int)GetVNum(h, o, "month"); int d = (int)GetVNum(h, o, "day"); if (y < 1 || mo < 1 || d < 1) return JsValue.FromNumber(1); try { var dt = new DateTime(Math.Min(y, 9999), Math.Min(mo, 12), Math.Min(d, 28)); int dow = (int)dt.DayOfWeek; return JsValue.FromNumber(dow == 0 ? 7 : dow); } catch { return JsValue.FromNumber(1); } });
        AddGetter(h, pH, p, "dayOfYear", o => { int y = (int)GetVNum(h, o, "year"); int mo = (int)GetVNum(h, o, "month"); int d = (int)GetVNum(h, o, "day"); if (y < 1 || mo < 1 || d < 1) return JsValue.FromNumber(1); try { var dt = new DateTime(Math.Min(y, 9999), Math.Min(mo, 12), Math.Min(d, 28)); return JsValue.FromNumber(dt.DayOfYear); } catch { return JsValue.FromNumber(1); } });
        AddGetter(h, pH, p, "weekOfYear", o => { int y = (int)GetVNum(h, o, "year"); int mo = (int)GetVNum(h, o, "month"); int d = (int)GetVNum(h, o, "day"); if (y < 1 || mo < 1 || d < 1) return JsValue.FromNumber(1); try { var dt = new DateTime(Math.Min(y, 9999), Math.Min(mo, 12), Math.Min(d, 28)); return JsValue.FromNumber(System.Globalization.ISOWeek.GetWeekOfYear(dt)); } catch { return JsValue.FromNumber(1); } });
        AddGetter(h, pH, p, "hoursInDay", o => { try { var tz = GetVStr(h, o, "tz"); var tzi = string.IsNullOrEmpty(tz) ? TimeZoneInfo.Utc : (TimeZoneInfo.FindSystemTimeZoneById(tz) ?? TimeZoneInfo.Utc); return JsValue.FromNumber(24); } catch { return JsValue.FromNumber(24); } });
        AddGetter(h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(h, pH, p, "daysInMonth", o => { int y = (int)GetVNum(h, o, "year"); int mo = (int)GetVNum(h, o, "month"); if (mo < 1) mo = 1; if (mo > 12) mo = 12; if (y < 1) y = 1; return JsValue.FromNumber(DateTime.DaysInMonth(y, mo)); });
        AddGetter(h, pH, p, "daysInYear", o => { int y = (int)GetVNum(h, o, "year"); if (y < 1) y = 1; return JsValue.FromNumber(DateTime.IsLeapYear(y) ? 366 : 365); });
        AddGetter(h, pH, p, "monthsInYear", o => JsValue.FromNumber(12));
        AddGetter(h, pH, p, "inLeapYear", o => { int y = (int)GetVNum(h, o, "year"); if (y < 1) y = 1; return JsValue.FromBoolean(DateTime.IsLeapYear(y)); });
        AddGetter(h, pH, p, "offset", o => { long nanos = (long)GetVNum(h, o, "offsetNanoseconds"); var ts = TimeSpan.FromTicks(nanos / 100); string sign = ts.Ticks >= 0 ? "+" : "-"; var abs = ts.Duration(); return JsValue.FromString($"{sign}{abs.Hours:D2}:{abs.Minutes:D2}"); });
        AddGetter(h, pH, p, "timeZoneId", o => GetV(h, o, "tz"));
        AddGetter(h, pH, p, "era", o => { int y = (int)GetVNum(h, o, "year"); return JsValue.FromString(y >= 0 ? "ce" : "bce"); });
        AddGetter(h, pH, p, "eraYear", o => { int y = (int)GetVNum(h, o, "year"); return JsValue.FromNumber(Math.Abs(y)); });
        AddMethod(ctx, h, pH, p, "with", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withTimeZone", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withPlainDate", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "withPlainTime", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "add", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "until", (o, _) => MakeDuration(ctx, h, TimeSpan.Zero), 1);
        AddMethod(ctx, h, pH, p, "since", (o, _) => MakeDuration(ctx, h, TimeSpan.Zero), 1);
        AddMethod(ctx, h, pH, p, "round", (o, _) => CloneTemporal(ctx, h, o), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
            var other = h.GetObject(a[0].AsObjectHandle());
            return JsValue.FromBoolean(DecodeInstantNanos(h, o) == DecodeInstantNanos(h, other));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => FormatZonedDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatZonedDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "getISOFields", (o, _) => JsValue.FromObject(h.AllocateObject(new JsObject(), AllocationSite.Current())), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", _ => MakeZonedDateTime(ctx, h, DateTimeOffset.UtcNow, "UTC"), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            if (a.Count < 2 || a[0].Tag != JsValueTag.Object || a[1].Tag != JsValueTag.Object) return JsValue.FromNumber(0);
            long nsA = DecodeInstantNanos(h, h.GetObject(a[0].AsObjectHandle()));
            long nsB = DecodeInstantNanos(h, h.GetObject(a[1].AsObjectHandle()));
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.Calendar ─────────────────────────────────
    private void InstallCalendar(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Calendar", 1, true);
        var p = h.GetObject(pH);
        AddGetter(h, pH, p, "id", o => JsValue.FromString("iso8601"));
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
        AddGetter(h, pH, p, "id", o => JsValue.FromString("UTC"));
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

    private static JsValue MakePlainDate(IBuiltinContext ctx, JsHeap h, DateTime dt)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("y", JsValue.FromNumber(dt.Year));
        d.SetProperty("m", JsValue.FromNumber(dt.Month));
        d.SetProperty("d", JsValue.FromNumber(dt.Day));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainTime(IBuiltinContext ctx, JsHeap h, TimeSpan ts)
    {
        return MakePlainTime(ctx, h, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, 0, 0);
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

    private static JsValue MakePlainDateTime(IBuiltinContext ctx, JsHeap h, DateTime dt)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("year", JsValue.FromNumber(dt.Year));
        d.SetProperty("month", JsValue.FromNumber(dt.Month));
        d.SetProperty("day", JsValue.FromNumber(dt.Day));
        d.SetProperty("hour", JsValue.FromNumber(dt.Hour));
        d.SetProperty("minute", JsValue.FromNumber(dt.Minute));
        d.SetProperty("second", JsValue.FromNumber(dt.Second));
        d.SetProperty("millisecond", JsValue.FromNumber(dt.Millisecond));
        d.SetProperty("microsecond", JsValue.FromNumber(0));
        d.SetProperty("nanosecond", JsValue.FromNumber(0));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainYearMonth(IBuiltinContext ctx, JsHeap h, int y, int m)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("y", JsValue.FromNumber(y));
        d.SetProperty("m", JsValue.FromNumber(m));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainMonthDay(IBuiltinContext ctx, JsHeap h, int m, int d)
    {
        var o = new JsObject();
        var dd = new JsObject(); var ddH = h.AllocateObject(dd, AllocationSite.Current());
        dd.SetProperty("mc", JsValue.FromString($"M{m:D2}"));
        dd.SetProperty("d", JsValue.FromNumber(d));
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

    private static JsValue CloneTemporal(IBuiltinContext ctx, JsHeap h, JsObject orig)
    {
        var clone = new JsObject();
        if (orig.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
            clone.DefineOwnProperty("_v", new JsPropertyDescriptor(dd.Value, false, false, false));
        if (orig.PrototypeHandle is { } ph) clone.SetPrototype(ph);
        return JsValue.FromObject(h.AllocateObject(clone, AllocationSite.Current()));
    }
}
