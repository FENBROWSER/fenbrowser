using System.Text.RegularExpressions;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
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
        var constructor = new NativeFunctionObject(
            "RegExp",
            (_, args) => BuildRegExp(capturedCtx, capturedProto, args),
            args => BuildRegExp(capturedCtx, capturedProto, args),
            length: 2);
        constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        protoObj.DefineOwnProperty("constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        context.InstallRegExpPrototypeMethods(prototypeHandle, protoObj);

        // ES2025 RegExp.escape
        context.DefineIntrinsicFunction(constructorHandle, constructor, "escape", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.String)
                throw new JsThrownException(context.CreateTypeError("RegExp.escape: argument must be a string."));
            return JsValue.FromString(RegExpEscape(args[0].AsString()));
        }, length: 1);

        return new[] { BuiltinBinding.NonEnumerable("RegExp", JsValue.FromObject(constructorHandle)) };
    }

    private static JsValue BuildRegExp(IBuiltinContext ctx, ObjectHandle protoHandle, IReadOnlyList<JsValue> args)
    {
        var pattern = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[0]) : string.Empty;
        var flags = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[1]) : string.Empty;
        string normalizedFlags;
        try { normalizedFlags = NormalizeRegExpFlags(flags); }
        catch (ArgumentException) { throw new JsThrownException(ctx.CreateSyntaxError("Invalid RegExp flags.")); }
        // ECMA-262 22.2.4 flags → RegexOptions mapping.
        // ECMAScript mode is the default; dotAll (s) conflicts with it and
        // Unicode (u) restricts \w/\d to ASCII in ECMAScript mode, so both
        // remove the ECMAScript option to get fuller Unicode behaviour.
        bool hasS = normalizedFlags.Contains('s', StringComparison.Ordinal);
        bool hasU = normalizedFlags.Contains('u', StringComparison.Ordinal);
        var options = (hasS || hasU)
            ? RegexOptions.CultureInvariant
            : RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (normalizedFlags.Contains('i', StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
        if (normalizedFlags.Contains('m', StringComparison.Ordinal)) options |= RegexOptions.Multiline;
        if (hasS) options |= RegexOptions.Singleline;

        Regex regex;
        try { regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException ex) { throw new JsThrownException(ctx.CreateSyntaxError(ex.Message)); }

        var obj = new RegExpObject(pattern, normalizedFlags, regex);
        obj.SetPrototype(protoHandle);
        obj.DefineOwnProperty("source", new JsPropertyDescriptor(JsValue.FromString(pattern), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("global", new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('g', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("ignoreCase", new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('i', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("multiline", new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('m', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("dotAll", new JsPropertyDescriptor(JsValue.FromBoolean(hasS), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("unicode", new JsPropertyDescriptor(JsValue.FromBoolean(hasU), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("sticky", new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('y', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("hasIndices", new JsPropertyDescriptor(JsValue.FromBoolean(normalizedFlags.Contains('d', StringComparison.Ordinal)), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("flags", new JsPropertyDescriptor(JsValue.FromString(normalizedFlags), Writable: false, Enumerable: false, Configurable: true));
        obj.DefineOwnProperty("lastIndex", new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(ctx.Heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private static string NormalizeRegExpFlags(string flags)
    {
        var sb = new System.Text.StringBuilder(flags.Length);
        var seenG = false; var seenI = false; var seenM = false;
        var seenS = false; var seenU = false; var seenY = false; var seenD = false;
        foreach (var c in flags)
        {
            switch (c)
            {
                case 'g': if (!seenG) { seenG = true; sb.Append(c); } break;
                case 'i': if (!seenI) { seenI = true; sb.Append(c); } break;
                case 'm': if (!seenM) { seenM = true; sb.Append(c); } break;
                case 's': if (!seenS) { seenS = true; sb.Append(c); } break;
                case 'u': if (!seenU) { seenU = true; sb.Append(c); } break;
                case 'y': if (!seenY) { seenY = true; sb.Append(c); } break;
                case 'd': if (!seenD) { seenD = true; sb.Append(c); } break;
                default: throw new ArgumentException("Invalid flag: " + c);
            }
        }
        return sb.ToString();
    }

    private static string RegExpEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
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
            if ("^$\\.*+?()[]{}|/".IndexOf(c) >= 0) sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
