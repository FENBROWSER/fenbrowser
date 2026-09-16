using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §2.6.1 "Reflecting content attributes in IDL attributes": the table of
/// IDL attributes whose getter and setter are defined purely in terms of a
/// content attribute, so the host bridge needs no per-property code for them.
/// Only properties the bridge does not already special-case belong here; the
/// bridge consults the table after its explicit cases and before its
/// expando-property fallback.
/// </summary>
internal static class HtmlAttributeReflection
{
    internal enum Kind
    {
        /// <summary>DOMString: getter returns the attribute or "", setter sets it.</summary>
        String,

        /// <summary>USVString URL attribute: like <see cref="String"/> but resolved against the document base URL on read.</summary>
        Url,

        /// <summary>boolean: getter is attribute presence, setter adds "" or removes.</summary>
        Boolean,

        /// <summary>long: parsed per "rules for parsing integers", default when absent or invalid.</summary>
        Long,

        /// <summary>unsigned long limited to only non-negative numbers greater than zero (colspan, rowspan, span, size).</summary>
        PositiveLong,

        /// <summary>long limited to only non-negative numbers, -1 when absent (maxlength, minlength).</summary>
        NonNegativeLongOrMinusOne,

        /// <summary>unsigned long "clamped to the range [Min, Max]" (colspan, rowspan): out-of-range values clamp rather than fall back.</summary>
        ClampedLong,

        /// <summary>
        /// HTML §4.13.2 CORS settings attribute, reflected as a nullable DOMString
        /// (<c>crossOrigin</c>). Absent reflects as null - not "" - and any present
        /// value other than "use-credentials" reflects as "anonymous", since the
        /// attribute's invalid value default and its empty-string default are both
        /// the Anonymous state. Setting null removes the content attribute.
        /// </summary>
        CorsSetting,
    }

    internal readonly record struct Entry(string Attribute, Kind Kind, string[]? Tags, long Default = 0, long Min = 0, long Max = int.MaxValue);

    // Keyed by IDL property name (case-sensitive, as JS sees it). A null tag list
    // means the property is on HTMLElement itself.
    private static readonly Dictionary<string, Entry> Table = BuildTable();

    private static Dictionary<string, Entry> BuildTable()
    {
        var t = new Dictionary<string, Entry>(StringComparer.Ordinal);
        void Add(string property, string attribute, Kind kind, string[]? tags = null, long @default = 0, long min = 0, long max = int.MaxValue)
            => t[property] = new Entry(attribute, kind, tags, @default, min, max);

        string[] formControls = { "button", "input", "select", "textarea", "optgroup", "option", "fieldset", "link" };
        string[] media = { "audio", "video" };
        string[] tableCell = { "td", "th" };

        // HTMLElement
        Add("lang", "lang", Kind.String);
        Add("dir", "dir", Kind.String);
        Add("slot", "slot", Kind.String);
        Add("accessKey", "accesskey", Kind.String);
        Add("autofocus", "autofocus", Kind.Boolean);
        Add("inert", "inert", Kind.Boolean);
        Add("hidden", "hidden", Kind.Boolean);
        Add("translate", "translate", Kind.String);

        // Form controls (HTML §4.10)
        Add("disabled", "disabled", Kind.Boolean, formControls);
        Add("readOnly", "readonly", Kind.Boolean, new[] { "input", "textarea" });
        Add("required", "required", Kind.Boolean, new[] { "input", "select", "textarea" });
        Add("multiple", "multiple", Kind.Boolean, new[] { "input", "select" });
        Add("placeholder", "placeholder", Kind.String, new[] { "input", "textarea" });
        Add("pattern", "pattern", Kind.String, new[] { "input" });
        Add("min", "min", Kind.String, new[] { "input", "meter", "progress" });
        Add("max", "max", Kind.String, new[] { "input", "meter", "progress" });
        Add("step", "step", Kind.String, new[] { "input" });
        Add("accept", "accept", Kind.String, new[] { "input" });
        Add("alt", "alt", Kind.String, new[] { "input", "img", "area" });
        Add("autocomplete", "autocomplete", Kind.String, new[] { "input", "select", "textarea", "form" });
        Add("dirName", "dirname", Kind.String, new[] { "input", "textarea" });
        Add("inputMode", "inputmode", Kind.String);
        Add("enterKeyHint", "enterkeyhint", Kind.String);
        Add("maxLength", "maxlength", Kind.NonNegativeLongOrMinusOne, new[] { "input", "textarea" }, -1);
        Add("minLength", "minlength", Kind.NonNegativeLongOrMinusOne, new[] { "input", "textarea" }, -1);
        Add("size", "size", Kind.PositiveLong, new[] { "input" }, 20);
        Add("cols", "cols", Kind.PositiveLong, new[] { "textarea" }, 20);
        Add("rows", "rows", Kind.PositiveLong, new[] { "textarea" }, 2);
        Add("wrap", "wrap", Kind.String, new[] { "textarea" });
        Add("formAction", "formaction", Kind.Url, new[] { "button", "input" });
        Add("formEnctype", "formenctype", Kind.String, new[] { "button", "input" });
        Add("formMethod", "formmethod", Kind.String, new[] { "button", "input" });
        Add("formNoValidate", "formnovalidate", Kind.Boolean, new[] { "button", "input" });
        Add("formTarget", "formtarget", Kind.String, new[] { "button", "input" });
        Add("htmlFor", "for", Kind.String, new[] { "label", "output" });
        Add("label", "label", Kind.String, new[] { "optgroup", "option", "track" });
        Add("action", "action", Kind.Url, new[] { "form" });
        Add("method", "method", Kind.String, new[] { "form" });
        Add("enctype", "enctype", Kind.String, new[] { "form" });
        Add("encoding", "enctype", Kind.String, new[] { "form" });
        Add("acceptCharset", "accept-charset", Kind.String, new[] { "form" });
        Add("noValidate", "novalidate", Kind.Boolean, new[] { "form" });
        Add("target", "target", Kind.String, new[] { "a", "area", "form", "base" });

        // Links, images, embedded content
        Add("rel", "rel", Kind.String, new[] { "a", "area", "link", "form" });
        Add("hreflang", "hreflang", Kind.String, new[] { "a", "link" });
        // HTML §4.2.4: the destination a <link rel=preload> is fetching for, and
        // §4.13.2's CORS settings attribute. React sets both on every preload link
        // it creates for a chunk, so a bundle split into a hundred chunks assigns
        // them a hundred times before it renders anything.
        Add("as", "as", Kind.String, new[] { "link" });
        Add("crossOrigin", "crossorigin", Kind.CorsSetting, new[] { "link", "img", "script", "audio", "video" });
        Add("download", "download", Kind.String, new[] { "a", "area" });
        Add("ping", "ping", Kind.String, new[] { "a", "area" });
        Add("referrerPolicy", "referrerpolicy", Kind.String, new[] { "a", "area", "img", "iframe", "link", "script" });
        Add("media", "media", Kind.String, new[] { "link", "style", "source" });
        Add("coords", "coords", Kind.String, new[] { "area" });
        Add("shape", "shape", Kind.String, new[] { "area" });
        Add("useMap", "usemap", Kind.String, new[] { "img", "object" });
        Add("isMap", "ismap", Kind.Boolean, new[] { "img" });
        Add("srcset", "srcset", Kind.String, new[] { "img", "source" });
        Add("sizes", "sizes", Kind.String, new[] { "img", "source", "link" });
        Add("loading", "loading", Kind.String, new[] { "img", "iframe" });
        Add("decoding", "decoding", Kind.String, new[] { "img" });
        Add("allow", "allow", Kind.String, new[] { "iframe" });
        Add("allowFullscreen", "allowfullscreen", Kind.Boolean, new[] { "iframe" });
        Add("srcdoc", "srcdoc", Kind.String, new[] { "iframe" });
        Add("data", "data", Kind.Url, new[] { "object" });
        Add("poster", "poster", Kind.Url, new[] { "video" });
        Add("preload", "preload", Kind.String, media);
        Add("autoplay", "autoplay", Kind.Boolean, media);
        Add("loop", "loop", Kind.Boolean, media);
        Add("controls", "controls", Kind.Boolean, media);
        Add("defaultMuted", "muted", Kind.Boolean, media);
        Add("playsInline", "playsinline", Kind.Boolean, new[] { "video" });
        Add("kind", "kind", Kind.String, new[] { "track" });
        Add("srclang", "srclang", Kind.String, new[] { "track" });
        Add("default", "default", Kind.Boolean, new[] { "track" });
        Add("defer", "defer", Kind.Boolean, new[] { "script" });
        Add("noModule", "nomodule", Kind.Boolean, new[] { "script" });
        Add("cite", "cite", Kind.Url, new[] { "blockquote", "q", "ins", "del" });
        Add("dateTime", "datetime", Kind.String, new[] { "time", "ins", "del" });
        Add("open", "open", Kind.Boolean, new[] { "details" });
        Add("reversed", "reversed", Kind.Boolean, new[] { "ol" });
        Add("start", "start", Kind.Long, new[] { "ol" }, 1);
        Add("charset", "charset", Kind.String, new[] { "meta", "script", "a" });
        Add("scheme", "scheme", Kind.String, new[] { "meta" });
        Add("abbr", "abbr", Kind.String, new[] { "th" });
        Add("scope", "scope", Kind.String, new[] { "th" });
        Add("headers", "headers", Kind.String, tableCell);
        // HTML 4.9.11: colspan is clamped to [1, 1000], rowspan to [0, 65534].
        Add("colSpan", "colspan", Kind.ClampedLong, tableCell, 1, 1, 1000);
        Add("rowSpan", "rowspan", Kind.ClampedLong, tableCell, 1, 0, 65534);
        Add("span", "span", Kind.ClampedLong, new[] { "col", "colgroup" }, 1, 1, 1000);
        Add("summary", "summary", Kind.String, new[] { "table" });

        // Obsolete but reflected (HTML §16.3)
        Add("align", "align", Kind.String, new[] { "div", "p", "h1", "h2", "h3", "h4", "h5", "h6", "table", "tr", "td", "th", "thead", "tbody", "tfoot", "col", "colgroup", "caption", "img", "iframe", "input", "hr", "legend", "object", "embed", "applet" });
        Add("vAlign", "valign", Kind.String, new[] { "tr", "td", "th", "thead", "tbody", "tfoot", "col", "colgroup" });
        Add("bgColor", "bgcolor", Kind.String, new[] { "body", "table", "tr", "td", "th" });
        Add("border", "border", Kind.String, new[] { "table", "img", "object" });
        Add("cellPadding", "cellpadding", Kind.String, new[] { "table" });
        Add("cellSpacing", "cellspacing", Kind.String, new[] { "table" });
        Add("frame", "frame", Kind.String, new[] { "table" });
        Add("rules", "rules", Kind.String, new[] { "table" });
        Add("noWrap", "nowrap", Kind.Boolean, tableCell);
        Add("color", "color", Kind.String, new[] { "font", "hr" });
        Add("face", "face", Kind.String, new[] { "font" });
        Add("clear", "clear", Kind.String, new[] { "br" });
        Add("compact", "compact", Kind.Boolean, new[] { "ol", "ul", "dl", "menu", "dir" });
        Add("hspace", "hspace", Kind.Long, new[] { "img", "object", "embed" });
        Add("vspace", "vspace", Kind.Long, new[] { "img", "object", "embed" });
        Add("longDesc", "longdesc", Kind.Url, new[] { "img", "iframe" });
        Add("noShade", "noshade", Kind.Boolean, new[] { "hr" });
        Add("marginHeight", "marginheight", Kind.String, new[] { "iframe", "frame", "body" });
        Add("marginWidth", "marginwidth", Kind.String, new[] { "iframe", "frame", "body" });
        Add("frameBorder", "frameborder", Kind.String, new[] { "iframe", "frame" });
        Add("text", "text", Kind.String, new[] { "body" });
        Add("link", "link", Kind.String, new[] { "body" });
        Add("vLink", "vlink", Kind.String, new[] { "body" });
        Add("aLink", "alink", Kind.String, new[] { "body" });
        Add("background", "background", Kind.String, new[] { "body" });
        Add("chOff", "charoff", Kind.String, new[] { "tr", "td", "th", "col", "colgroup", "thead", "tbody", "tfoot" });
        Add("ch", "char", Kind.String, new[] { "tr", "td", "th", "col", "colgroup", "thead", "tbody", "tfoot" });
        Add("axis", "axis", Kind.String, tableCell);
        Add("event", "event", Kind.String, new[] { "script" });
        Add("version", "version", Kind.String, new[] { "html" });
        Add("profile", "profile", Kind.String, new[] { "head" });
        return t;
    }

    internal static bool TryGetEntry(Element element, string property, out Entry entry)
    {
        if (!Table.TryGetValue(property, out entry))
        {
            return false;
        }

        if (!IsHtmlElement(element))
        {
            return false;
        }

        if (entry.Tags == null)
        {
            return true;
        }

        var local = element.LocalName ?? string.Empty;
        foreach (var tag in entry.Tags)
        {
            if (string.Equals(tag, local, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHtmlElement(Element element)
        => element.NamespaceUri == null || string.Equals(element.NamespaceUri, Namespaces.Html, StringComparison.Ordinal);

    internal static JsValue Read(Element element, in Entry entry, Func<string, string> resolveUrl)
    {
        var raw = element.GetAttribute(entry.Attribute);
        switch (entry.Kind)
        {
            case Kind.Boolean:
                return JsValue.FromBoolean(raw != null);
            case Kind.String:
                return JsValue.FromString(raw ?? string.Empty);
            case Kind.CorsSetting:
                if (raw == null) return JsValue.Null;
                return JsValue.FromString(
                    string.Equals(raw, "use-credentials", StringComparison.OrdinalIgnoreCase)
                        ? "use-credentials"
                        : "anonymous");
            case Kind.Url:
                // HTML §2.6.1: a URL attribute reflects as "" when absent and as the
                // parsed-and-serialised URL otherwise (the raw value when parsing fails).
                if (raw == null) return JsValue.FromString(string.Empty);
                return JsValue.FromString(resolveUrl(raw) ?? raw);
            case Kind.Long:
                return JsValue.FromNumber(ParseInteger(raw, out var l) ? l : entry.Default);
            case Kind.PositiveLong:
                return JsValue.FromNumber(ParseInteger(raw, out var p) && p > 0 && p <= 2147483647 ? p : entry.Default);
            case Kind.NonNegativeLongOrMinusOne:
                return JsValue.FromNumber(ParseInteger(raw, out var n) && n >= 0 && n <= 2147483647 ? n : -1);
            case Kind.ClampedLong:
                // A value that fails to parse takes the default; one that parses
                // but is out of range clamps to the nearer bound.
                if (!ParseInteger(raw, out var c) || c < 0) return JsValue.FromNumber(entry.Default);
                return JsValue.FromNumber(Math.Min(entry.Max, Math.Max(entry.Min, c)));
            default:
                return JsValue.Undefined;
        }
    }

    internal static void Write(Element element, in Entry entry, JsValue value, Func<JsValue, string> toString, Func<JsValue, bool> toBoolean, Func<JsValue, double> toNumber)
    {
        switch (entry.Kind)
        {
            case Kind.Boolean:
                if (toBoolean(value)) element.SetAttribute(entry.Attribute, string.Empty);
                else element.RemoveAttribute(entry.Attribute);
                break;
            case Kind.String:
            case Kind.Url:
                element.SetAttribute(entry.Attribute, toString(value));
                break;
            case Kind.CorsSetting:
                // WebIDL: a nullable reflected DOMString removes the content
                // attribute when set to null. Undefined stringifies like any other
                // value, so only an explicit null removes.
                if (value.Tag == JsValueTag.Null) element.RemoveAttribute(entry.Attribute);
                else element.SetAttribute(entry.Attribute, toString(value));
                break;
            case Kind.Long:
                element.SetAttribute(entry.Attribute, ToInt32Clamped(toNumber(value)).ToString(CultureInfo.InvariantCulture));
                break;
            case Kind.PositiveLong:
            {
                // HTML §2.6.1: setting 0 (or a value out of range) throws IndexSizeError;
                // the bridge maps the DOMException below, so clamp to the default here.
                var n = ToInt32Clamped(toNumber(value));
                if (n <= 0) n = (int)Math.Max(1, entry.Default);
                element.SetAttribute(entry.Attribute, n.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case Kind.NonNegativeLongOrMinusOne:
            {
                var n = ToInt32Clamped(toNumber(value));
                if (n < 0) n = 0;
                element.SetAttribute(entry.Attribute, n.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case Kind.ClampedLong:
            {
                // WebIDL [Clamp]-style: the setter stores the clamped value.
                var raw = toNumber(value);
                var n = double.IsNaN(raw) ? entry.Default : (long)Math.Min(entry.Max, Math.Max(entry.Min, Math.Truncate(raw)));
                element.SetAttribute(entry.Attribute, n.ToString(CultureInfo.InvariantCulture));
                break;
            }
        }
    }

    private static int ToInt32Clamped(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        var truncated = Math.Truncate(value);
        if (truncated > int.MaxValue) return int.MaxValue;
        if (truncated < int.MinValue) return int.MinValue;
        return (int)truncated;
    }

    /// <summary>HTML §2.3.4.1 rules for parsing integers.</summary>
    private static bool ParseInteger(string? input, out long result)
    {
        result = 0;
        if (input == null) return false;
        var i = 0;
        while (i < input.Length && IsAsciiWhitespace(input[i])) i++;
        if (i >= input.Length) return false;
        var sign = 1;
        if (input[i] == '-') { sign = -1; i++; }
        else if (input[i] == '+') { i++; }
        if (i >= input.Length || !char.IsAsciiDigit(input[i])) return false;
        long value = 0;
        while (i < input.Length && char.IsAsciiDigit(input[i]))
        {
            value = value * 10 + (input[i] - '0');
            if (value > int.MaxValue) { value = int.MaxValue + 1L; }
            i++;
        }

        result = sign * value;
        return true;
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';
}
