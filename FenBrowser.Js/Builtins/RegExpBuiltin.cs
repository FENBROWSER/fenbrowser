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

        return new[] { BuiltinBinding.NonEnumerable("RegExp", JsValue.FromObject(constructorHandle)) };
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
            Regex.RegExpCompiler.ValidatePattern(pattern, normalizedFlags);
        }
        catch (Regex.RegexSyntaxError ex)
        {
            throw new JsThrownException(ctx.CreateSyntaxError(ex.Message));
        }

        var dotNetPattern = RewriteEcmaCharacterClassEscapes(pattern);
        if (hasU || hasV)
        {
            dotNetPattern = Regex.RegExpCompiler.RewriteUnicodeCodePointEscapes(dotNetPattern);
            dotNetPattern = Regex.RegExpCompiler.RewriteUnicodePropertyEscapesForDotNet(dotNetPattern);
        }

        BclRegex regex;
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
            if (Regex.RegExpCompiler.ContainsUnicodePropertyEscape(pattern))
            {
                regex = new BclRegex("(?:)", options, TimeSpan.FromMilliseconds(250));
            }
            else
            {
                throw new JsThrownException(ctx.CreateSyntaxError(ex.Message));
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

        var obj = new RegExpObject(pattern, normalizedFlags, regex, nativeProgram);
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
            var matcher = descriptor.IsAccessor ? JsValue.Undefined : descriptor.Value;
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
                rewritten.Append(ch);
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                rewritten.Append(ch);
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

    private static string RegExpEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i == 0 && ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            {
                sb.Append('\\').Append('x').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            switch (c)
            {
                case '\t': sb.Append("\\t"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\v': sb.Append("\\v"); continue;
                case '\f': sb.Append("\\f"); continue;
                case '\r': sb.Append("\\r"); continue;
            }

            if ("^$\\.*+?()[]{}|/".IndexOf(c) >= 0)
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
