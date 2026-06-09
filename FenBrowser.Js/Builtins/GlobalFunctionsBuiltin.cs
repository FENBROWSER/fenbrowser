using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 19.2 — Function Properties of the Global Object.
//
// isNaN, isFinite, encodeURI, encodeURIComponent, decodeURI, decodeURIComponent,
// parseInt, parseFloat.
//
// parseInt and parseFloat use the context's GetParseIntFunction/GetParseFloatFunction
// to return the SAME function object used by Number.parseInt/Number.parseFloat
// (ECMA-262 21.1.2.13 / 21.1.2.14).
[EcmaSpecReference(
    "19.2",
    AbstractOperation = "GlobalFunctionProperties",
    Url = "https://tc39.es/ecma262/#sec-function-properties-of-the-global-object")]
public sealed class GlobalFunctionsBuiltin : IBuiltinModule
{
    public string Name => "GlobalFunctions";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindings = new List<BuiltinBinding>(8);

        // 19.2.5 parseInt(string, radix) — same object as Number.parseInt
        bindings.Add(BuiltinBinding.NonEnumerable("parseInt", JsValue.FromObject(context.GetParseIntFunction())));

        // 19.2.4 parseFloat(string) — same object as Number.parseFloat
        bindings.Add(BuiltinBinding.NonEnumerable("parseFloat", JsValue.FromObject(context.GetParseFloatFunction())));

        // 19.2.3 isNaN(number)
        AddFunction(context, bindings, "isNaN", (ctx, _, args) =>
        {
            var n = args.Count > 0 ? ctx.ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(double.IsNaN(n));
        }, length: 1);

        // 19.2.2 isFinite(number)
        AddFunction(context, bindings, "isFinite", (ctx, _, args) =>
        {
            var n = args.Count > 0 ? ctx.ToNumber(args[0]) : double.NaN;
            return JsValue.FromBoolean(!double.IsNaN(n) && !double.IsInfinity(n));
        }, length: 1);

        // 19.2.6.4 encodeURI(uri)
        AddFunction(context, bindings, "encodeURI", (ctx, _, args) =>
            JsValue.FromString(EncodeUri(ctx, args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined", encodeReserved: false)),
            length: 1);

        // 19.2.6.5 encodeURIComponent(uriComponent)
        AddFunction(context, bindings, "encodeURIComponent", (ctx, _, args) =>
            JsValue.FromString(EncodeUri(ctx, args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined", encodeReserved: true)),
            length: 1);

        // 19.2.6.2 decodeURI(encodedURI)
        AddFunction(context, bindings, "decodeURI", (ctx, _, args) =>
            JsValue.FromString(DecodeUri(ctx, args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined", preserveReserved: true)),
            length: 1);

        // 19.2.6.3 decodeURIComponent(encodedURIComponent)
        AddFunction(context, bindings, "decodeURIComponent", (ctx, _, args) =>
            JsValue.FromString(DecodeUri(ctx, args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined", preserveReserved: false)),
            length: 1);

        // Annex B B.2.1.1 escape(string) — legacy URI-ish encoder. Distinct from
        // encodeURIComponent: it uses %uXXXX for code points ≥ 256 and leaves
        // a smaller unreserved set untouched.
        AddFunction(context, bindings, "escape", (ctx, _, args) =>
            JsValue.FromString(LegacyEscape(args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined")),
            length: 1);

        // Annex B B.2.1.2 unescape(string) — inverse of escape; recognises both
        // %XX and %uXXXX. Unknown trailing % sequences pass through literally
        // per the spec.
        AddFunction(context, bindings, "unescape", (ctx, _, args) =>
            JsValue.FromString(LegacyUnescape(args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined")),
            length: 1);

        return bindings;
    }

    // Annex B B.2.1.1 escape. Unreserved set = ASCII letters, digits, and
    // `@`, `*`, `_`, `+`, `-`, `.`, `/`. UTF-16 code units are emitted as
    // %XX when ≤ 0xFF, else %uXXXX.
    internal static string LegacyEscape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = (int)ch;
            var unreserved =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '@' || c == '*' || c == '_' || c == '+' ||
                c == '-' || c == '.' || c == '/';
            if (unreserved)
            {
                _ = sb.Append(ch);
            }
            else if (c <= 0xFF)
            {
                _ = sb.Append('%').Append(c.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                _ = sb.Append("%u").Append(c.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    // Annex B B.2.1.2 unescape. Walks each code unit; `%uXXXX` and `%XX` are
    // decoded when the trailing characters form valid hex, otherwise the `%`
    // and following characters are emitted unchanged.
    internal static string LegacyUnescape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '%')
            {
                if (i + 5 < text.Length && text[i + 1] == 'u' &&
                    TryHex(text[i + 2], out var h1) && TryHex(text[i + 3], out var h2) &&
                    TryHex(text[i + 4], out var h3) && TryHex(text[i + 5], out var h4))
                {
                    _ = sb.Append((char)((h1 << 12) | (h2 << 8) | (h3 << 4) | h4));
                    i += 6;
                    continue;
                }
                if (i + 2 < text.Length &&
                    TryHex(text[i + 1], out var b1) && TryHex(text[i + 2], out var b2))
                {
                    _ = sb.Append((char)((b1 << 4) | b2));
                    i += 3;
                    continue;
                }
            }
            _ = sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryHex(char ch, out int value)
    {
        if (ch >= '0' && ch <= '9') { value = ch - '0'; return true; }
        if (ch >= 'A' && ch <= 'F') { value = ch - 'A' + 10; return true; }
        if (ch >= 'a' && ch <= 'f') { value = ch - 'a' + 10; return true; }
        value = 0;
        return false;
    }

    private static void AddFunction(
        IBuiltinContext context,
        List<BuiltinBinding> bindings,
        string name,
        Func<IBuiltinContext, JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length)
    {
        var captured = context;
        var fn = new NativeFunctionObject(name, (thisValue, args) => call(captured, thisValue, args), length: length);
        var handle = context.Heap.AllocateObject(fn, AllocationSite.Current());
        context.Heap.PushRoot(handle);
        bindings.Add(BuiltinBinding.NonEnumerable(name, JsValue.FromObject(handle)));
    }

    // ECMA-262 19.2.6.1.1 Encode(string, unescapedSet)
    internal static string EncodeUri(IBuiltinContext ctx, string text, bool encodeReserved)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            int codePoint;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    throw new JsThrownException(ctx.CreateUriError("URI malformed: lone high surrogate."));
                codePoint = char.ConvertToUtf32(ch, text[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                throw new JsThrownException(ctx.CreateUriError("URI malformed: lone low surrogate."));
            }
            else
            {
                codePoint = ch;
            }

            if (IsUriUnescaped(codePoint, encodeReserved))
            {
                _ = sb.Append((char)codePoint);
            }
            else
            {
                var buffer = codePoint <= 0x7F
                    ? new byte[] { (byte)codePoint }
                    : System.Text.Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint));
                foreach (var b in buffer)
                    _ = sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    // ECMA-262 19.2.6.1.2 Decode(string, reservedSet)
    internal static string DecodeUri(IBuiltinContext ctx, string text, bool preserveReserved)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch != '%')
            {
                _ = sb.Append(ch);
                i++;
                continue;
            }

            if (i + 2 >= text.Length)
                throw new JsThrownException(ctx.CreateUriError("URI malformed: truncated escape."));

            var b0 = DecodeHexByte(ctx, text, i);
            i += 3;
            if ((b0 & 0x80) == 0)
            {
                if (preserveReserved && IsUriReservedAscii((char)b0))
                    _ = sb.Append('%').Append(text[i - 2]).Append(text[i - 1]);
                else
                    _ = sb.Append((char)b0);
                continue;
            }

            int extraBytes;
            if ((b0 & 0xE0) == 0xC0) extraBytes = 1;
            else if ((b0 & 0xF0) == 0xE0) extraBytes = 2;
            else if ((b0 & 0xF8) == 0xF0) extraBytes = 3;
            else throw new JsThrownException(ctx.CreateUriError("URI malformed: bad UTF-8 leading byte."));

            var bytes = new byte[1 + extraBytes];
            bytes[0] = b0;
            for (var k = 1; k <= extraBytes; k++)
            {
                if (i >= text.Length || text[i] != '%' || i + 2 >= text.Length)
                    throw new JsThrownException(ctx.CreateUriError("URI malformed: truncated continuation."));
                var bk = DecodeHexByte(ctx, text, i);
                if ((bk & 0xC0) != 0x80)
                    throw new JsThrownException(ctx.CreateUriError("URI malformed: bad UTF-8 continuation."));
                bytes[k] = bk;
                i += 3;
            }

            // Manual UTF-8 decoding with strict validation (surrogates, overlong,
            // out-of-range). ECMA-262 19.2.6.3.
            var cp = DecodeUtf8CodePoint(ctx, bytes);
            _ = sb.Append(cp <= 0xFFFF
                ? ((char)cp).ToString()
                : char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    // ECMA-262 19.2.6.3: strict UTF-8 decoding that rejects surrogates,
    // overlong encodings, and code points beyond U+10FFFF.
    private static int DecodeUtf8CodePoint(IBuiltinContext ctx, byte[] bytes)
    {
        int cp;
        int expectedLen;
        if ((bytes[0] & 0x80) == 0)
        {
            cp = bytes[0];
            expectedLen = 1;
        }
        else if ((bytes[0] & 0xE0) == 0xC0)
        {
            cp = bytes[0] & 0x1F;
            expectedLen = 2;
        }
        else if ((bytes[0] & 0xF0) == 0xE0)
        {
            cp = bytes[0] & 0x0F;
            expectedLen = 3;
        }
        else if ((bytes[0] & 0xF8) == 0xF0)
        {
            cp = bytes[0] & 0x07;
            expectedLen = 4;
        }
        else
        {
            throw new JsThrownException(ctx.CreateUriError("URI malformed: bad UTF-8 leading byte."));
        }
        if (bytes.Length != expectedLen)
            throw new JsThrownException(ctx.CreateUriError("URI malformed: bad UTF-8 length."));
        for (var j = 1; j < expectedLen; j++)
        {
            if ((bytes[j] & 0xC0) != 0x80)
                throw new JsThrownException(ctx.CreateUriError("URI malformed: bad UTF-8 continuation."));
            cp = (cp << 6) | (bytes[j] & 0x3F);
        }
        // Overlong check
        if (expectedLen == 2 && cp < 0x80) throw new JsThrownException(ctx.CreateUriError("URI malformed: overlong encoding."));
        if (expectedLen == 3 && cp < 0x800) throw new JsThrownException(ctx.CreateUriError("URI malformed: overlong encoding."));
        if (expectedLen == 4 && cp < 0x10000) throw new JsThrownException(ctx.CreateUriError("URI malformed: overlong encoding."));
        // Surrogate pair range is invalid
        if (cp is >= 0xD800 and <= 0xDFFF)
            throw new JsThrownException(ctx.CreateUriError("URI malformed: decoded surrogate."));
        // Beyond Unicode range
        if (cp > 0x10FFFF)
            throw new JsThrownException(ctx.CreateUriError("URI malformed: out-of-range code point."));
        return cp;
    }

    private static byte DecodeHexByte(IBuiltinContext ctx, string text, int percentIndex)
    {
        var hi = HexDigit(ctx, text[percentIndex + 1]);
        var lo = HexDigit(ctx, text[percentIndex + 2]);
        return (byte)((hi << 4) | lo);
    }

    private static int HexDigit(IBuiltinContext ctx, char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        throw new JsThrownException(ctx.CreateUriError("URI malformed: invalid hex digit."));
    }

    private static bool IsUriUnescaped(int c, bool encodeReserved)
    {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            return true;
        switch (c)
        {
            case '-': case '_': case '.': case '!': case '~':
            case '*': case '\'': case '(': case ')':
                return true;
        }
        if (!encodeReserved)
        {
            switch (c)
            {
                case ';': case '/': case '?': case ':': case '@':
                case '&': case '=': case '+': case '$': case ',':
                case '#':
                    return true;
            }
        }
        return false;
    }

    private static bool IsUriReservedAscii(char c)
    {
        switch (c)
        {
            case ';': case '/': case '?': case ':': case '@':
            case '&': case '=': case '+': case '$': case ',':
            case '#':
                return true;
        }
        return false;
    }
}
