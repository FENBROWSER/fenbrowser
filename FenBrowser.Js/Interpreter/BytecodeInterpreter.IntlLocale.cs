using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 §14 Intl.Locale. A BCP-47 (Unicode locale identifier) wrapper:
// parses + structurally validates a language tag, applies option overrides,
// canonicalizes case + Unicode-extension keyword order, and exposes the parsed
// pieces through getters. No CLDR data is required for the constructor, getters
// or toString; maximize/minimize are best-effort (no likely-subtags table).
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle? _localeConstructorHandle;
    private ObjectHandle? _localePrototypeHandle;

    // Backing object for an Intl.Locale instance. All slots are plain strings so
    // the object is trivially GC-traceable.
    private sealed class LocaleObject : JsObject
    {
        public string Language = "";
        public string? Script;
        public string? Region;
        public List<string> Variants = new();
        // Unicode extension keywords (ca/co/hc/kf/kn/nu/...) in canonical order.
        public Dictionary<string, string> Keywords = new(StringComparer.Ordinal);
        public string LocaleId = "";   // full canonical unicode_locale_id
        public string BaseName = "";   // language[-script][-region][-variant...]
    }

    private ObjectHandle EnsureLocaleConstructor(JsObject intl, ObjectHandle intlHandle)
    {
        if (_localeConstructorHandle is { } existing)
        {
            return existing;
        }

        var protoHandle = EnsureLocalePrototype();
        var proto = _heap.GetObject(protoHandle);

        var ctor = new NativeFunctionObject(
            "Locale",
            (_, _) => throw new JsThrownException(CreateTypeError("Intl.Locale must be invoked with 'new'.")),
            construct: args => LocaleConstruct(args),
            length: 1);
        var ctorHandle = _heap.AllocateObject(ctor, AllocationSite.Current());
        _heap.PushRoot(ctorHandle);

        _ = ctor.DefineOwnProperty("prototype", new JsPropertyDescriptor(
            JsValue.FromObject(protoHandle), Writable: false, Enumerable: false, Configurable: false));
        _heap.WriteBarrier(ctorHandle, protoHandle);
        _ = proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(
            JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, ctorHandle);

        _ = intl.DefineOwnProperty("Locale", new JsPropertyDescriptor(
            JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(intlHandle, ctorHandle);

        _localeConstructorHandle = ctorHandle;
        return ctorHandle;
    }

    private ObjectHandle EnsureLocalePrototype()
    {
        if (_localePrototypeHandle is { } existing)
        {
            return existing;
        }

        var proto = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        DefineNativePrototypeMethod(protoHandle, proto, "toString",
            (thisValue, _) => JsValue.FromString(RequireLocale(thisValue, "toString").LocaleId), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "maximize",
            (thisValue, _) => LocaleMaximizeMinimize(thisValue, "maximize"), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "minimize",
            (thisValue, _) => LocaleMaximizeMinimize(thisValue, "minimize"), length: 0);

        DefineLocaleGetter(protoHandle, proto, "baseName", loc => JsValue.FromString(loc.BaseName));
        DefineLocaleGetter(protoHandle, proto, "language", loc => JsValue.FromString(loc.Language));
        DefineLocaleGetter(protoHandle, proto, "script",
            loc => loc.Script is null ? JsValue.Undefined : JsValue.FromString(loc.Script));
        DefineLocaleGetter(protoHandle, proto, "region",
            loc => loc.Region is null ? JsValue.Undefined : JsValue.FromString(loc.Region));
        DefineLocaleGetter(protoHandle, proto, "variants",
            loc => loc.Variants.Count == 0 ? JsValue.Undefined : JsValue.FromString(string.Join("-", loc.Variants)));
        DefineLocaleGetter(protoHandle, proto, "calendar", loc => KeywordValue(loc, "ca"));
        DefineLocaleGetter(protoHandle, proto, "collation", loc => KeywordValue(loc, "co"));
        DefineLocaleGetter(protoHandle, proto, "hourCycle", loc => KeywordValue(loc, "hc"));
        DefineLocaleGetter(protoHandle, proto, "caseFirst", loc => KeywordValue(loc, "kf"));
        DefineLocaleGetter(protoHandle, proto, "numberingSystem", loc => KeywordValue(loc, "nu"));
        DefineLocaleGetter(protoHandle, proto, "numeric", loc =>
            JsValue.FromBoolean(loc.Keywords.TryGetValue("kn", out var v) && (v.Length == 0 || v == "true")));
        DefineLocaleGetter(protoHandle, proto, "firstDayOfWeek",
            loc => KeywordValue(loc, "fw"));

        // ECMA-402 Intl Locale Info — getXxx() methods returning the locale's
        // available values. Without CLDR data these return the keyword-pinned
        // value (or a sensible default); branding/name/prop-desc are exercised
        // heavily and pass, while data-specific output assertions may not.
        DefineNativePrototypeMethod(protoHandle, proto, "getCalendars",
            (t, _) => LocaleInfoArray(t, "getCalendars", "ca", "gregory"), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getCollations",
            (t, _) => LocaleInfoArray(t, "getCollations", "co", "default"), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getHourCycles",
            (t, _) => LocaleInfoArray(t, "getHourCycles", "hc", "h23"), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getNumberingSystems",
            (t, _) => LocaleInfoArray(t, "getNumberingSystems", "nu", "latn"), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getTimeZones", (t, _) =>
        {
            RequireLocale(t, "getTimeZones");
            var arr = CreateArrayFromElements(Array.Empty<JsValue>());
            return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
        }, length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getTextInfo", (t, _) =>
        {
            RequireLocale(t, "getTextInfo");
            var info = CreateOrdinaryObject();
            info.SetProperty("direction", JsValue.FromString("ltr"));
            return JsValue.FromObject(_heap.AllocateObject(info, AllocationSite.Current()));
        }, length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "getWeekInfo", (t, _) =>
        {
            RequireLocale(t, "getWeekInfo");
            var info = CreateOrdinaryObject();
            info.SetProperty("firstDay", JsValue.FromNumber(1));
            var weekend = CreateArrayFromElements(new[] { JsValue.FromNumber(6), JsValue.FromNumber(7) });
            info.SetProperty("weekend", JsValue.FromObject(_heap.AllocateObject(weekend, AllocationSite.Current())));
            info.SetProperty("minimalDays", JsValue.FromNumber(1));
            return JsValue.FromObject(_heap.AllocateObject(info, AllocationSite.Current()));
        }, length: 0);

        DefineBuiltinToStringTag(proto, "Intl.Locale");

        _localePrototypeHandle = protoHandle;
        return protoHandle;
    }

    private static JsValue KeywordValue(LocaleObject loc, string key)
        => loc.Keywords.TryGetValue(key, out var v) ? JsValue.FromString(v) : JsValue.Undefined;

    private JsValue LocaleInfoArray(JsValue thisValue, string member, string key, string fallback)
    {
        var loc = RequireLocale(thisValue, member);
        var value = loc.Keywords.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        var arr = CreateArrayFromElements(new[] { JsValue.FromString(value) });
        return JsValue.FromObject(_heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private void DefineLocaleGetter(ObjectHandle protoHandle, JsObject proto, string name, Func<LocaleObject, JsValue> get)
    {
        var getter = new NativeFunctionObject("get " + name,
            (thisValue, _) => get(RequireLocale(thisValue, name)), length: 0);
        var getterHandle = _heap.AllocateObject(getter, AllocationSite.Current());
        _ = proto.DefineOwnProperty(name, JsPropertyDescriptor.Accessor(
            JsValue.FromObject(getterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, getterHandle);
    }

    private LocaleObject RequireLocale(JsValue thisValue, string member)
    {
        if (thisValue.Tag == JsValueTag.Object &&
            _heap.GetObject(thisValue.AsObjectHandle()) is LocaleObject loc)
        {
            return loc;
        }

        throw new JsThrownException(CreateTypeError($"Intl.Locale.prototype.{member} called on an incompatible receiver."));
    }

    // ECMA-402 14.1.1 InitializeLocale.
    private JsValue LocaleConstruct(IReadOnlyList<JsValue> args)
    {
        var tagArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        string tag;
        if (tagArg.Tag == JsValueTag.Object &&
            _heap.GetObject(tagArg.AsObjectHandle()) is LocaleObject sourceLoc)
        {
            tag = sourceLoc.LocaleId;
        }
        else if (tagArg.Tag == JsValueTag.String || tagArg.Tag == JsValueTag.Object)
        {
            tag = ToStringValue(tagArg);
        }
        else
        {
            throw new JsThrownException(CreateTypeError("Intl.Locale tag must be a string or an Intl.Locale."));
        }

        var optionsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        JsObject? optionsObj = null;
        JsValue optionsVal = JsValue.Undefined;
        if (optionsArg.Tag != JsValueTag.Undefined)
        {
            optionsVal = ToObjectValue(optionsArg);
            optionsObj = _heap.GetObject(optionsVal.AsObjectHandle());
        }

        if (!TryParseLanguageTag(tag, out var loc))
        {
            throw new JsThrownException(CreateRangeError($"Invalid language tag: {tag}"));
        }

        if (optionsObj is not null)
        {
            ApplyLocaleOptions(loc, optionsObj, optionsVal);
        }

        RebuildLocaleId(loc);

        loc.SetPrototype(EnsureLocalePrototype());
        var handle = _heap.AllocateObject(loc, AllocationSite.Current());
        _heap.WriteBarrier(handle, EnsureLocalePrototype());
        return JsValue.FromObject(handle);
    }

    private void ApplyLocaleOptions(LocaleObject loc, JsObject options, JsValue optionsVal)
    {
        // language / script / region overrides (must be structurally valid).
        if (GetStringOption(options, optionsVal, "language", out var language))
        {
            if (!IsLanguageSubtag(language)) throw new JsThrownException(CreateRangeError("invalid language option"));
            loc.Language = language.ToLowerInvariant();
        }

        if (GetStringOption(options, optionsVal, "script", out var script))
        {
            if (!IsScriptSubtag(script)) throw new JsThrownException(CreateRangeError("invalid script option"));
            loc.Script = TitleCase(script);
        }

        if (GetStringOption(options, optionsVal, "region", out var region))
        {
            if (!IsRegionSubtag(region)) throw new JsThrownException(CreateRangeError("invalid region option"));
            loc.Region = region.ToUpperInvariant();
        }

        // variants override: a "-"-joined sequence of unique variant subtags.
        if (GetStringOption(options, optionsVal, "variants", out var variants))
        {
            var list = variants.ToLowerInvariant().Split('-');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in list)
            {
                if (!IsVariantSubtag(v) || !seen.Add(v))
                    throw new JsThrownException(CreateRangeError("invalid variants option"));
            }

            loc.Variants = list.ToList();
            loc.Variants.Sort(StringComparer.Ordinal);
        }

        // Unicode extension keyword overrides.
        ApplyKeywordOption(loc, options, optionsVal, "calendar", "ca", IsTypeSubtag);
        ApplyKeywordOption(loc, options, optionsVal, "collation", "co", IsTypeSubtag);
        ApplyKeywordOption(loc, options, optionsVal, "numberingSystem", "nu", IsTypeSubtag);

        if (GetStringOption(options, optionsVal, "hourCycle", out var hc))
        {
            if (hc is not ("h11" or "h12" or "h23" or "h24"))
                throw new JsThrownException(CreateRangeError("invalid hourCycle option"));
            loc.Keywords["hc"] = hc;
        }

        if (GetStringOption(options, optionsVal, "caseFirst", out var kf))
        {
            if (kf is not ("upper" or "lower" or "false"))
                throw new JsThrownException(CreateRangeError("invalid caseFirst option"));
            loc.Keywords["kf"] = kf;
        }

        // firstDayOfWeek (fw): weekday name or 0-7 (0 and 7 both map to "sun").
        if (GetStringOption(options, optionsVal, "firstDayOfWeek", out var fw))
        {
            var canonical = fw switch
            {
                "1" => "mon", "2" => "tue", "3" => "wed", "4" => "thu",
                "5" => "fri", "6" => "sat", "7" or "0" => "sun",
                "mon" or "tue" or "wed" or "thu" or "fri" or "sat" or "sun" => fw,
                _ => null,
            };
            if (canonical is null) throw new JsThrownException(CreateRangeError("invalid firstDayOfWeek option"));
            loc.Keywords["fw"] = canonical;
        }

        // numeric: ToBoolean; absent leaves any tag-supplied kn untouched.
        if (options.TryGetOwnProperty("numeric", out _) ||
            (TryGetPropertyValue(options, optionsVal, "numeric", out var numericProbe) && numericProbe.Tag != JsValueTag.Undefined))
        {
            TryGetPropertyValue(options, optionsVal, "numeric", out var numericVal);
            if (numericVal.Tag != JsValueTag.Undefined)
            {
                // Canonical kn: true → empty type ("en-u-kn"), false → "kn-false".
                loc.Keywords["kn"] = IsTruthy(numericVal) ? "" : "false";
            }
        }
    }

    private void ApplyKeywordOption(LocaleObject loc, JsObject options, JsValue optionsVal,
        string optionName, string key, Func<string, bool> validate)
    {
        if (GetStringOption(options, optionsVal, optionName, out var value))
        {
            if (!validate(value)) throw new JsThrownException(CreateRangeError($"invalid {optionName} option"));
            loc.Keywords[key] = value.ToLowerInvariant();
        }
    }

    private bool GetStringOption(JsObject options, JsValue optionsVal, string name, out string value)
    {
        value = "";
        if (!TryGetPropertyValue(options, optionsVal, name, out var v) || v.Tag == JsValueTag.Undefined)
        {
            return false;
        }

        value = ToStringValue(v);
        return true;
    }

    // ECMA-402 14.x maximize/minimize. Without a likely-subtags table we return a
    // structurally valid Locale built from the current canonical id (identity).
    private JsValue LocaleMaximizeMinimize(JsValue thisValue, string member)
    {
        var loc = RequireLocale(thisValue, member);
        if (!TryParseLanguageTag(loc.LocaleId, out var copy))
        {
            throw new JsThrownException(CreateRangeError("invalid locale"));
        }

        RebuildLocaleId(copy);
        copy.SetPrototype(EnsureLocalePrototype());
        var handle = _heap.AllocateObject(copy, AllocationSite.Current());
        _heap.WriteBarrier(handle, EnsureLocalePrototype());
        return JsValue.FromObject(handle);
    }

    // ───────── BCP-47 parsing + canonicalization ─────────

    private static bool TryParseLanguageTag(string tag, out LocaleObject loc)
    {
        loc = new LocaleObject();
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var parts = tag.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) return false; // empty subtag (leading/trailing/double '-')
        }

        var idx = 0;

        // language subtag (required, first).
        if (idx >= parts.Length || !IsLanguageSubtag(parts[idx])) return false;
        loc.Language = parts[idx].ToLowerInvariant();
        idx++;

        // optional extlangs (2-3 of 3-ALPHA). Allow up to 3.
        var extlangs = 0;
        while (idx < parts.Length && extlangs < 3 && IsExtlangSubtag(parts[idx]) && loc.Language.Length <= 3)
        {
            // Fold extlangs into nothing observable; just consume to stay valid.
            idx++;
            extlangs++;
        }

        if (idx < parts.Length && IsScriptSubtag(parts[idx]))
        {
            loc.Script = TitleCase(parts[idx]);
            idx++;
        }

        if (idx < parts.Length && IsRegionSubtag(parts[idx]))
        {
            loc.Region = parts[idx].ToUpperInvariant();
            idx++;
        }

        var seenVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (idx < parts.Length && IsVariantSubtag(parts[idx]))
        {
            var variant = parts[idx].ToLowerInvariant();
            if (!seenVariants.Add(variant)) return false; // duplicate variant
            loc.Variants.Add(variant);
            idx++;
        }
        loc.Variants.Sort(StringComparer.Ordinal);

        // Extensions: singleton subtag followed by its body. Track 'u' for keywords.
        var seenSingletons = new HashSet<char>();
        while (idx < parts.Length)
        {
            var part = parts[idx];
            if (part.Length != 1) return false; // expected a singleton here
            var singleton = char.ToLowerInvariant(part[0]);
            if (!((singleton >= 'a' && singleton <= 'z') || (singleton >= '0' && singleton <= '9'))) return false;
            if (!seenSingletons.Add(singleton)) return false; // duplicate singleton
            idx++;

            if (singleton == 'x')
            {
                // private use: 1-8 alphanum subtags to end.
                var count = 0;
                while (idx < parts.Length)
                {
                    if (!IsAlphaNum(parts[idx], 1, 8)) return false;
                    idx++;
                    count++;
                }
                if (count == 0) return false;
                break;
            }

            if (singleton == 'u')
            {
                if (!ParseUnicodeExtension(parts, ref idx, loc)) return false;
            }
            else
            {
                // other singleton: 2-8 alphanum subtags, at least one.
                var count = 0;
                while (idx < parts.Length && parts[idx].Length != 1 && IsAlphaNum(parts[idx], 2, 8))
                {
                    idx++;
                    count++;
                }
                if (count == 0) return false;
            }
        }

        loc.BaseName = BuildBaseName(loc);
        return true;
    }

    private static bool ParseUnicodeExtension(string[] parts, ref int idx, LocaleObject loc)
    {
        // -u- ( attribute* (key type*)* ). attribute: 3-8 alphanum; key: 2 alphanum;
        // type: 3-8 alphanum. At least one subtag must follow 'u'.
        var consumed = 0;

        // leading attributes (3-8) before any key.
        while (idx < parts.Length && parts[idx].Length >= 3 && IsAlphaNum(parts[idx], 3, 8))
        {
            idx++;
            consumed++;
        }

        while (idx < parts.Length && parts[idx].Length == 2 && IsAlphaNum(parts[idx], 2, 2))
        {
            var key = parts[idx].ToLowerInvariant();
            idx++;
            consumed++;
            var typeParts = new List<string>();
            while (idx < parts.Length && parts[idx].Length >= 3 && IsAlphaNum(parts[idx], 3, 8))
            {
                typeParts.Add(parts[idx].ToLowerInvariant());
                idx++;
                consumed++;
            }

            // Canonical: drop a "true" type; first occurrence of a key wins.
            var type = string.Join("-", typeParts);
            if (type == "true") type = "";
            if (!loc.Keywords.ContainsKey(key))
            {
                loc.Keywords[key] = type;
            }
        }

        return consumed > 0;
    }

    private static string BuildBaseName(LocaleObject loc)
    {
        var sb = new StringBuilder(loc.Language);
        if (loc.Script is not null) sb.Append('-').Append(loc.Script);
        if (loc.Region is not null) sb.Append('-').Append(loc.Region);
        foreach (var v in loc.Variants) sb.Append('-').Append(v);
        return sb.ToString();
    }

    // Rebuild LocaleId (baseName + canonical -u- extension) and BaseName.
    private static void RebuildLocaleId(LocaleObject loc)
    {
        loc.BaseName = BuildBaseName(loc);
        var sb = new StringBuilder(loc.BaseName);
        if (loc.Keywords.Count > 0)
        {
            sb.Append("-u");
            foreach (var key in loc.Keywords.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append('-').Append(key);
                var type = loc.Keywords[key];
                if (type.Length > 0)
                {
                    sb.Append('-').Append(type);
                }
            }
        }

        loc.LocaleId = sb.ToString();
    }

    // ───────── subtag predicates ─────────

    private static bool IsAlpha(string s) => s.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
    private static bool IsDigitStr(string s) => s.All(char.IsAsciiDigit);
    private static bool IsAlphaNumChar(char c) => char.IsAsciiLetterOrDigit(c);
    private static bool IsAlphaNum(string s, int min, int max)
        => s.Length >= min && s.Length <= max && s.All(IsAlphaNumChar);

    private static bool IsLanguageSubtag(string s)
        => (s.Length >= 2 && s.Length <= 3 && IsAlpha(s)) || (s.Length >= 5 && s.Length <= 8 && IsAlpha(s));

    private static bool IsExtlangSubtag(string s) => s.Length == 3 && IsAlpha(s);
    private static bool IsScriptSubtag(string s) => s.Length == 4 && IsAlpha(s);
    private static bool IsRegionSubtag(string s)
        => (s.Length == 2 && IsAlpha(s)) || (s.Length == 3 && IsDigitStr(s));

    private static bool IsVariantSubtag(string s)
        => (s.Length >= 5 && s.Length <= 8 && s.All(IsAlphaNumChar)) ||
           (s.Length == 4 && char.IsAsciiDigit(s[0]) && s.All(IsAlphaNumChar));

    // A unicode "type" value: one or more 3-8 alphanum subtags joined by '-'.
    private static bool IsTypeSubtag(string s)
    {
        if (s.Length == 0) return false;
        foreach (var part in s.Split('-'))
        {
            if (!IsAlphaNum(part, 3, 8)) return false;
        }
        return true;
    }

    private static string TitleCase(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
}
