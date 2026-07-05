using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Regex;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("22.2", AbstractOperation = "RegExp", Url = "https://tc39.es/ecma262/#sec-regexp-constructor")]
public sealed class RegExpBuiltin : IBuiltinModule
{
    public string Name => "RegExp";

    // Annex B B.2.4: legacy static match state for RegExp.$1..$9, input, etc.
    // Updated by the interpreter via UpdateLegacyState after each successful match.
    internal static string LastRegExpInput = string.Empty;
    // [0] = full match, [1..9] = captured groups $1..$9
    internal static readonly string[] LastCaptures = new string[] { "", "", "", "", "", "", "", "", "", "" };

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedCtx = context;
        var capturedProto = prototypeHandle;
        ObjectHandle constructorHandle = default;
        var functionPrototypeHandle = default(ObjectHandle);
        var hasFunctionPrototype = false;
        var constructor = new NativeFunctionObject(
            "RegExp",
            (_, args) => BuildRegExpCall(capturedCtx, capturedProto, constructorHandle, args),
            args => BuildRegExpConstruct(capturedCtx, capturedProto, args),
            length: 2);
        var functionConstructorHandle = context.MaterializeFunctionConstructor();
        var functionConstructor = heap.GetObject(functionConstructorHandle);
        if (context.TryGetPropertyValue(functionConstructor, JsValue.FromObject(functionConstructorHandle), "prototype", out var functionPrototypeValue) &&
            functionPrototypeValue.Tag == JsValueTag.Object)
        {
            functionPrototypeHandle = functionPrototypeValue.AsObjectHandle();
            constructor.SetPrototype(functionPrototypeHandle);
            hasFunctionPrototype = true;
        }

        constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);
        if (hasFunctionPrototype)
        {
            heap.WriteBarrier(constructorHandle, functionPrototypeHandle);
        }

        var protoObj = heap.GetObject(prototypeHandle);
        protoObj.DefineOwnProperty("constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        context.InstallRegExpPrototypeMethods(prototypeHandle, protoObj);

        var speciesSymbolId = context.CreateWellKnownSymbol("species").AsSymbolId();
        var speciesGetter = new NativeFunctionObject("get [Symbol.species]", (thisValue, _args) => thisValue, length: 0);
        var speciesGetterHandle = heap.AllocateObject(speciesGetter, AllocationSite.Current());
        constructor.DefineOwnSymbolProperty(
            speciesSymbolId,
            JsPropertyDescriptor.Accessor(
                JsValue.FromObject(speciesGetterHandle),
                JsValue.Undefined,
                Enumerable: false,
                Configurable: true));
        heap.WriteBarrier(constructorHandle, speciesGetterHandle);

        // ES2025 RegExp.escape
        context.DefineIntrinsicFunction(constructorHandle, constructor, "escape", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.String)
            {
                throw new JsThrownException(context.CreateTypeError("RegExp.escape: argument must be a string."));
            }

            return JsValue.FromString(RegExpEscape(args[0].AsString()));
        }, length: 1);

        // Annex B B.2.4: legacy static accessor properties ($1..$9, input, lastMatch, etc.)
        InstallLegacyAccessors(heap, constructorHandle, constructor, context);

        return new[] { BuiltinBinding.NonEnumerable("RegExp", JsValue.FromObject(constructorHandle)) };
    }

    private static void InstallLegacyAccessors(JsHeap heap, ObjectHandle ctorHandle, JsObject ctor, IBuiltinContext context)
    {
        var regExpValue = JsValue.FromObject(ctorHandle);

        // B.2.4 legacy static accessors. The getter/setter must check that thisValue
        // is the %RegExp% constructor; non-matching receivers get TypeError.
        void DefineGetter(string name, Func<string> valueProvider)
        {
            var getter = new NativeFunctionObject("get " + name, (thisValue, _2) =>
            {
                if (!thisValue.Equals(regExpValue))
                    throw new JsThrownException(context.CreateTypeError("RegExp." + name + " getter called on incompatible receiver."));
                return JsValue.FromString(valueProvider());
            }, length: 0);
            var gh = heap.AllocateObject(getter, AllocationSite.Current());
            _ = ctor.DefineOwnProperty(name, JsPropertyDescriptor.Accessor(
                JsValue.FromObject(gh), JsValue.Undefined, Enumerable: false, Configurable: true));
            heap.WriteBarrier(ctorHandle, gh);
        }

        void DefineGetterSetter(string name, Func<string> getProvider)
        {
            var getter = new NativeFunctionObject("get " + name, (thisValue, _2) =>
            {
                if (!thisValue.Equals(regExpValue))
                    throw new JsThrownException(context.CreateTypeError("RegExp." + name + " getter called on incompatible receiver."));
                return JsValue.FromString(getProvider());
            }, length: 0);
            var setter = new NativeFunctionObject("set " + name, (thisValue, args) =>
            {
                if (!thisValue.Equals(regExpValue))
                    throw new JsThrownException(context.CreateTypeError("RegExp." + name + " setter called on incompatible receiver."));
                LastRegExpInput = args.Count > 0 && args[0].Tag == JsValueTag.String ? args[0].AsString() : "undefined";
                return JsValue.Undefined;
            }, length: 1);
            var gh = heap.AllocateObject(getter, AllocationSite.Current());
            var sh = heap.AllocateObject(setter, AllocationSite.Current());
            _ = ctor.DefineOwnProperty(name, JsPropertyDescriptor.Accessor(
                JsValue.FromObject(gh), JsValue.FromObject(sh), Enumerable: false, Configurable: true));
            heap.WriteBarrier(ctorHandle, gh);
            heap.WriteBarrier(ctorHandle, sh);
        }

        // $1..$9
        for (int i = 1; i <= 9; i++) { var idx = i; DefineGetter("$" + i, () => LastCaptures[idx]); }
        // input / $_
        DefineGetterSetter("input", () => LastRegExpInput);
        DefineGetterSetter("$_", () => LastRegExpInput);
        // lastMatch / $&
        DefineGetter("lastMatch", () => LastCaptures[0]);
        DefineGetter("$&", () => LastCaptures[0]);
        // lastParen / $+
        DefineGetter("lastParen", () => { for (int i = 9; i >= 1; i--) if (LastCaptures[i].Length > 0) return LastCaptures[i]; return string.Empty; });
        DefineGetter("$+", () => { for (int i = 9; i >= 1; i--) if (LastCaptures[i].Length > 0) return LastCaptures[i]; return string.Empty; });
        // leftContext
        DefineGetter("leftContext", () => {
            if (string.IsNullOrEmpty(LastCaptures[0]) || string.IsNullOrEmpty(LastRegExpInput)) return string.Empty;
            var idx = LastRegExpInput.IndexOf(LastCaptures[0], StringComparison.Ordinal);
            return idx > 0 ? LastRegExpInput[..idx] : string.Empty;
        });
        DefineGetter("$`", () => {
            if (string.IsNullOrEmpty(LastCaptures[0]) || string.IsNullOrEmpty(LastRegExpInput)) return string.Empty;
            var idx = LastRegExpInput.IndexOf(LastCaptures[0], StringComparison.Ordinal);
            return idx > 0 ? LastRegExpInput[..idx] : string.Empty;
        });
        // rightContext
        DefineGetter("rightContext", () => {
            if (string.IsNullOrEmpty(LastCaptures[0]) || string.IsNullOrEmpty(LastRegExpInput)) return string.Empty;
            var idx = LastRegExpInput.IndexOf(LastCaptures[0], StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var end = idx + LastCaptures[0].Length;
            return end < LastRegExpInput.Length ? LastRegExpInput[end..] : string.Empty;
        });
        DefineGetter("$'", () => {
            if (string.IsNullOrEmpty(LastCaptures[0]) || string.IsNullOrEmpty(LastRegExpInput)) return string.Empty;
            var idx = LastRegExpInput.IndexOf(LastCaptures[0], StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var end = idx + LastCaptures[0].Length;
            return end < LastRegExpInput.Length ? LastRegExpInput[end..] : string.Empty;
        });
    }

    private static JsValue BuildRegExpCall(
        IBuiltinContext ctx,
        ObjectHandle protoHandle,
        ObjectHandle constructorHandle,
        IReadOnlyList<JsValue> args)
    {
        var patternArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Count > 1 ? args[1] : JsValue.Undefined;

        if (flagsArg.Tag == JsValueTag.Undefined &&
            ShouldReturnPatternOnCall(ctx, patternArg, constructorHandle))
        {
            return patternArg;
        }

        return BuildRegExpObject(ctx, protoHandle, patternArg, flagsArg);
    }

    private static JsValue BuildRegExpConstruct(
        IBuiltinContext ctx,
        ObjectHandle protoHandle,
        IReadOnlyList<JsValue> args)
    {
        var patternArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var flagsArg = args.Count > 1 ? args[1] : JsValue.Undefined;
        return BuildRegExpObject(ctx, protoHandle, patternArg, flagsArg);
    }

    private static JsValue BuildRegExpObject(
        IBuiltinContext ctx,
        ObjectHandle protoHandle,
        JsValue patternArg,
        JsValue flagsArg)
    {
        ResolvePatternAndFlags(ctx, patternArg, flagsArg, out var pattern, out var flags);

        string normalizedFlags;
        try
        {
            normalizedFlags = NormalizeRegExpFlags(flags);
        }
        catch (ArgumentException)
        {
            throw new JsThrownException(ctx.CreateSyntaxError("Invalid RegExp flags."));
        }

        var hasS = normalizedFlags.Contains('s', StringComparison.Ordinal);
        var hasU = normalizedFlags.Contains('u', StringComparison.Ordinal);
        var hasV = normalizedFlags.Contains('v', StringComparison.Ordinal);
        var options = (hasS || hasU || hasV)
            ? RegexOptions.CultureInvariant
            : RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (normalizedFlags.Contains('m', StringComparison.Ordinal))
        {
            options |= RegexOptions.Multiline;
        }

        if (hasS)
        {
            options |= RegexOptions.Singleline;
        }

        // ECMA-262 22.2.3.1: validate the pattern before creating the object.
        try
        {
            var parsedFlags = Regex.RegexFlags.Parse(normalizedFlags.AsSpan());
            Regex.RegExpCompiler.ValidatePatternEarlyErrors(pattern, parsedFlags);
        }
        catch (Regex.RegexSyntaxError ex)
        {
            throw new JsThrownException(ctx.CreateSyntaxError(ex.Message));
        }

        var executionPattern = Regex.RegExpCompiler.RewriteAnnexBNonUnicodePattern(
            pattern, Regex.RegexFlags.Parse(normalizedFlags.AsSpan()));
        var dotNetPattern = RewriteEcmaCharacterClassEscapes(executionPattern);
        var namedGroupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        dotNetPattern = Regex.RegExpCompiler.RewriteNamedGroupSyntaxForDotNet(dotNetPattern, namedGroupMap);
        if (hasU || hasV)
        {
            dotNetPattern = Regex.RegExpCompiler.RewriteUnicodeCodePointEscapes(dotNetPattern);
            dotNetPattern = Regex.RegExpCompiler.RewriteUnicodePropertyEscapesForDotNet(dotNetPattern);
        }
        dotNetPattern = Regex.RegExpCompiler.RewriteForwardBackreferences(dotNetPattern);

        BclRegex regex;
        // UnicodeSets (v-flag): set operations (--, &&, ~~), \q{...}, and
        // property-of-strings escapes are unsupported by .NET. Use neutral.
        if (hasV)
        {
            regex = new BclRegex("(?:)", options, TimeSpan.FromMilliseconds(250));
        }
        else
        {
            try
            {
                regex = new BclRegex(dotNetPattern, options, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                // .NET rejects some valid ECMAScript constructs (e.g. property names
                // it doesn't know after the \p{} rewrite). When the pattern uses
                // property escapes, fall back to a neutral BCL regex and let the
                // native program do the matching, mirroring RegExpCompiler.Compile.
                if (Regex.RegExpCompiler.TryCompileRangeNormalizedDotNetRegex(dotNetPattern, options, TimeSpan.FromMilliseconds(250), ex, out var rangeNormalizedRegex))
                {
                    regex = rangeNormalizedRegex;
                }
                else if (Regex.RegExpCompiler.ShouldUseNeutralDotNetFallback(pattern, dotNetPattern, ex))
                {
                    regex = new BclRegex("(?:)", options, TimeSpan.FromMilliseconds(250));
                }
                else
                {
                    throw new JsThrownException(ctx.CreateSyntaxError(ex.Message));
                }
            }
        }

        RegexProgram? nativeProgram;
        try
        {
            nativeProgram = Regex.RegExpCompiler.CompileNative(pattern, normalizedFlags);
        }
        catch (Regex.RegexSyntaxError ex)
        {
            throw new JsThrownException(ctx.CreateSyntaxError(ex.Message));
        }

        // Build reverse map: alias → original name for translating
        // .NET group names back to ECMAScript names during exec.
        Dictionary<string, string>? reverseMap = null;
        if (namedGroupMap.Count > 0)
        {
            reverseMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in namedGroupMap)
                reverseMap[kvp.Value] = kvp.Key;
        }

        var obj = new RegExpObject(pattern, normalizedFlags, regex, nativeProgram)
        {
            NamedGroupAliases = namedGroupMap.Count > 0 ? namedGroupMap : null,
            NamedGroupReverseMap = reverseMap
        };
        obj.SetPrototype(protoHandle);
        // source/flags and the individual flag booleans are accessor properties on
        // %RegExp.prototype% (installed by the interpreter's InstallRegExpFlagAccessors);
        // only lastIndex is an own data property of the instance (ECMA-262 22.2.7.1).
        obj.DefineOwnProperty("lastIndex", new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(ctx.Heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private static bool ShouldReturnPatternOnCall(IBuiltinContext ctx, JsValue patternArg, ObjectHandle constructorHandle)
    {
        if (patternArg.Tag != JsValueTag.Object)
        {
            return false;
        }

        if (!IsRegExpLike(ctx, patternArg))
        {
            return false;
        }

        var patternObject = ctx.Heap.GetObject(patternArg.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(patternObject, patternArg, "constructor", out var patternConstructor))
        {
            return false;
        }

        return patternConstructor.Tag == JsValueTag.Object &&
               patternConstructor.AsObjectHandle() == constructorHandle;
    }

    private static void ResolvePatternAndFlags(
        IBuiltinContext ctx,
        JsValue patternArg,
        JsValue flagsArg,
        out string pattern,
        out string flags)
    {
        if (patternArg.Tag == JsValueTag.Object && IsRegExpLike(ctx, patternArg))
        {
            var patternObject = ctx.Heap.GetObject(patternArg.AsObjectHandle());
            if (!ctx.TryGetPropertyValue(patternObject, patternArg, "source", out var sourceValue))
            {
                sourceValue = JsValue.FromString(string.Empty);
            }

            pattern = sourceValue.Tag == JsValueTag.Undefined ? string.Empty : ctx.ToStringValue(sourceValue);
            if (flagsArg.Tag == JsValueTag.Undefined)
            {
                if (!ctx.TryGetPropertyValue(patternObject, patternArg, "flags", out var inheritedFlags))
                {
                    inheritedFlags = JsValue.FromString(string.Empty);
                }

                flags = inheritedFlags.Tag == JsValueTag.Undefined ? string.Empty : ctx.ToStringValue(inheritedFlags);
                return;
            }
        }
        else
        {
            pattern = patternArg.Tag == JsValueTag.Undefined ? string.Empty : ctx.ToStringValue(patternArg);
        }

        flags = flagsArg.Tag == JsValueTag.Undefined ? string.Empty : ctx.ToStringValue(flagsArg);
    }

    private static bool IsRegExpLike(IBuiltinContext ctx, JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var objectValue = ctx.Heap.GetObject(value.AsObjectHandle());
        var matchSymbolId = ctx.CreateWellKnownSymbol("match").AsSymbolId();
        if (objectValue.TryGetSymbolProperty(matchSymbolId, h => ctx.Heap.GetObject(h), out var descriptor))
        {
            var matcher = descriptor.Value;
            if (descriptor.IsAccessor)
            {
                matcher = descriptor.Get.Tag == JsValueTag.Undefined
                    ? JsValue.Undefined
                    : ctx.CallFunction(descriptor.Get, Array.Empty<JsValue>(), value);
            }
            if (matcher.Tag != JsValueTag.Undefined)
            {
                return matcher.Tag switch
                {
                    JsValueTag.Boolean => matcher.AsBoolean(),
                    JsValueTag.Int32 => matcher.AsInt32() != 0,
                    JsValueTag.Number => matcher.AsNumber() != 0 && !double.IsNaN(matcher.AsNumber()),
                    JsValueTag.String => matcher.AsString().Length != 0,
                    JsValueTag.Null => false,
                    JsValueTag.Undefined => false,
                    _ => true
                };
            }
        }

        return objectValue is RegExpObject;
    }

    private static string RewriteEcmaCharacterClassEscapes(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        const string whiteSpaceClass = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        const string nonWhiteSpaceClass = @"[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        var rewritten = new StringBuilder(pattern.Length + 24);
        var inCharClass = false;
        var classStartPos = -1;
        var classContentStart = -1;
        var classNegated = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[i + 1];
                if (!inCharClass)
                {
                    switch (next)
                    {
                        case 'd':
                            rewritten.Append("[0-9]");
                            i++;
                            continue;
                        case 'D':
                            rewritten.Append("[^0-9]");
                            i++;
                            continue;
                        case 'w':
                            rewritten.Append("[A-Za-z0-9_]");
                            i++;
                            continue;
                        case 'W':
                            rewritten.Append("[^A-Za-z0-9_]");
                            i++;
                            continue;
                        case 's':
                            rewritten.Append(whiteSpaceClass);
                            i++;
                            continue;
                        case 'S':
                            rewritten.Append(nonWhiteSpaceClass);
                            i++;
                            continue;
                    }
                }

                rewritten.Append(ch);
                rewritten.Append(next);
                i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
                classStartPos = rewritten.Length;
                classNegated = false;
                rewritten.Append(ch);
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                {
                    classNegated = true;
                    rewritten.Append('^');
                    i++;
                }
                classContentStart = rewritten.Length;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                if (rewritten.Length == classContentStart)
                {
                    // ECMAScript Annex B: empty character class.
                    rewritten.Length = classStartPos;
                    if (classNegated)
                        rewritten.Append(@"[\s\S]");
                    else
                        rewritten.Append(@"[^\w\W]");
                }
                else
                {
                    rewritten.Append(ch);
                }
                continue;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    private static string NormalizeRegExpFlags(string flags)
    {
        var seen = new HashSet<char>();
        foreach (var c in flags)
        {
            switch (c)
            {
                case 'd':
                case 'g':
                case 'i':
                case 'm':
                case 's':
                case 'u':
                case 'v':
                case 'y':
                    if (!seen.Add(c))
                    {
                        throw new ArgumentException("Duplicate flag: " + c);
                    }
                    break;
                default:
                    throw new ArgumentException("Invalid flag: " + c);
            }
        }

        var canonical = new StringBuilder(flags.Length);
        foreach (var c in "dgimsuvy")
        {
            if (seen.Contains(c))
            {
                canonical.Append(c);
            }
        }

        return canonical.ToString();
    }

    // ECMA-262 §22.2.9 — RegExp.escape and EncodeForRegExpEscape (ES2025)

    /// <summary>Public entry point for RegExp.escape(string) — shared across builtin and interpreter.</summary>
    public static string RegExpEscapeString(string s) => RegExpEscape(s);

    private static string RegExpEscape(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        var first = true;
        for (var i = 0; i < s.Length;)
        {
            int codePoint;
            int advance;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                codePoint = char.ConvertToUtf32(s[i], s[i + 1]);
                advance = 2;
            }
            else
            {
                codePoint = s[i];
                advance = 1;
            }

            // Step 4.a of RegExp.escape: if escaped is empty and c is DecimalDigit or AsciiLetter
            if (first && IsDecimalDigitOrAsciiLetter(codePoint))
            {
                sb.Append('\\').Append('x').Append(codePoint.ToString("x2"));
                first = false;
                i += advance;
                continue;
            }

            first = false;
            EncodeForRegExpEscape(codePoint, sb);
            i += advance;
        }

        return sb.ToString();
    }

    private static bool IsDecimalDigitOrAsciiLetter(int cp)
    {
        return (cp >= '0' && cp <= '9') || (cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z');
    }

    /// <summary>
    /// EncodeForRegExpEscape(c) per ES2025 §22.2.9.1:
    /// 1. If c is a control character (Table 64) → \t \n \v \f \r
    /// 2. If SyntaxCharacter or '/' → \ + UTF16EncodeCodePoint(c)
    /// 3-5. If otherPunctuator, WhiteSpace, LineTerminator, or surrogate:
    ///      ≤ 0xFF → \xHH; else → each code unit via UnicodeEscape
    /// 6. Else → UTF16EncodeCodePoint(c) (literal)
    /// </summary>
    private static void EncodeForRegExpEscape(int codePoint, StringBuilder sb)
    {
        // Step 1: Control characters (Table 64)
        switch (codePoint)
        {
            case '\t': sb.Append("\\t"); return;
            case '\n': sb.Append("\\n"); return;
            case '\v': sb.Append("\\v"); return;
            case '\f': sb.Append("\\f"); return;
            case '\r': sb.Append("\\r"); return;
        }

        // Step 2: SyntaxCharacter or SOLIDUS → \ followed by UTF16EncodeCodePoint(c)
        if (IsRegExpSyntaxCharacter(codePoint) || codePoint == '/')
        {
            sb.Append('\\');
            AppendUtf16EncodedCodePoint(codePoint, sb);
            return;
        }

        // Steps 3-5: otherPunctuators, WhiteSpace, LineTerminator, surrogates
        if (IsOtherPunctuator(codePoint) ||
            IsRegExpWhiteSpace(codePoint) ||
            IsLineTerminator(codePoint) ||
            IsSurrogateCodePoint(codePoint))
        {
            if (codePoint <= 0xFF)
            {
                sb.Append("\\x");
                sb.Append(codePoint.ToString("x2"));
            }
            else
            {
                AppendEachCodeUnitAsUnicodeEscape(codePoint, sb);
            }
            return;
        }

        // Step 6: Return UTF16EncodeCodePoint(c) — literal
        AppendUtf16EncodedCodePoint(codePoint, sb);
    }

    private static bool IsRegExpSyntaxCharacter(int cp)
    {
        // ECMA-262 SyntaxCharacter: ^ $ \ . * + ? ( ) [ ] { } |
        if (cp > 0xFF) return false;
        var c = (char)cp;
        return c is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|';
    }

    private static bool IsOtherPunctuator(int cp)
    {
        // ",-=<>#&!%:;@~'`" + QUOTATION MARK (0x22)
        if (cp > 0xFF) return false;
        var c = (char)cp;
        return c is ',' or '-' or '=' or '<' or '>' or '#' or '&' or '!' or '%' or ':'
                or ';' or '@' or '~' or '\'' or '`' or '"';
    }

    /// <summary>
    /// WhiteSpace per ES2025 RegExp.escape:
    /// TAB(9), VT(11), FF(12), SPACE(20), NBSP(A0), ZWNBSP(FEFF),
    /// plus any code point with both Unicode White_Space property AND Space_Separator category
    /// (e.g. U+202F NARROW NO-BREAK SPACE, U+2000-U+200A en/em spaces, U+3000 IDEOGRAPHIC SPACE).
    /// </summary>
    internal static bool IsRegExpWhiteSpace(int cp)
    {
        // The four standalone whitespace chars
        if (cp is 0x0009 or 0x000B or 0x000C or 0xFEFF) return true;
        // USP = code points with both White_Space binary property AND Space_Separator general category.
        // This includes SPACE(0x20), NBSP(0xA0), and others like U+2000-U+200A, U+202F, U+205F, U+3000.
        // We use .NET's CharUnicodeInfo to check the SpaceSeparator category,
        // then additionally filter for known White_Space code points.
        if (cp is 0x0020 or 0x00A0) return true;
        if (cp >= 0x2000 && cp <= 0x200A) return true; // EN QUAD..HAIR SPACE
        if (cp is 0x202F or 0x205F or 0x3000) return true; // NARROW NO-BREAK, MEDIUM MATH, IDEOGRAPHIC
        return false;
    }

    private static bool IsLineTerminator(int cp)
    {
        // LineTerminator: LF(0x0A), CR(0x0D), LS(0x2028), PS(0x2029)
        return cp is 0x000A or 0x000D or 0x2028 or 0x2029;
    }

    private static bool IsSurrogateCodePoint(int cp)
    {
        return cp >= 0xD800 && cp <= 0xDFFF;
    }

    /// <summary>
    /// UTF16EncodeCodePoint(c): for BMP → the single char; for supplementary → surrogate pair.
    /// Used for literal output (step 6) and after backslash in step 2.
    /// </summary>
    private static void AppendUtf16EncodedCodePoint(int cp, StringBuilder sb)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append((char)cp);
        }
        else
        {
            sb.Append((char)(((cp - 0x10000) >> 10) + 0xD800));
            sb.Append((char)(((cp - 0x10000) & 0x3FF) + 0xDC00));
        }
    }

    /// <summary>
    /// For each code unit of UTF16EncodeCodePoint(cp), append UnicodeEscape(cu).
    /// UnicodeEscape: \u + 4-digit lowercase hex.
    /// </summary>
    private static void AppendEachCodeUnitAsUnicodeEscape(int cp, StringBuilder sb)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append("\\u");
            sb.Append(cp.ToString("x4"));
        }
        else
        {
            var high = ((cp - 0x10000) >> 10) + 0xD800;
            var low = ((cp - 0x10000) & 0x3FF) + 0xDC00;
            sb.Append("\\u");
            sb.Append(high.ToString("x4"));
            sb.Append("\\u");
            sb.Append(low.ToString("x4"));
        }
    }
}
