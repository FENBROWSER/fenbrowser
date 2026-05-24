using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("22.1", AbstractOperation = "String", Url = "https://tc39.es/ecma262/#sec-string-objects")]
public sealed class StringBuiltin : IBuiltinModule
{
    public string Name => "String";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var prototype = new StringObject(string.Empty);
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedCtx = context;
        var capturedProto = prototypeHandle;
        var constructor = new NativeFunctionObject(
            "String",
            (_, args) => JsValue.FromString(args.Count > 0 ? context.ToStringValue(args[0]) : string.Empty),
            args =>
            {
                var obj = new StringObject(args.Count > 0 ? context.ToStringValue(args[0]) : string.Empty);
                obj.SetPrototype(capturedProto);
                return JsValue.FromObject(heap.AllocateObject(obj, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        _ = protoObj.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toString", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv)));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "valueOf", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv)));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "charAt", CharAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "charCodeAt", CharCodeAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "codePointAt", CodePointAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "at", At, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "indexOf", IndexOf, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "lastIndexOf", LastIndexOf, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "includes", Includes, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "startsWith", StartsWith, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "endsWith", EndsWith, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "slice", Slice, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "substring", Substring, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "substr", Substr, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "concat", Concat, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "repeat", Repeat, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "padStart", PadStart, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "padEnd", PadEnd, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trim", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).Trim()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trimStart", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimStart()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trimEnd", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimEnd()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toUpperCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToUpperInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLowerCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToLowerInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLocaleUpperCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToUpperInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLocaleLowerCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToLowerInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "split", Split, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "replace", Replace, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "replaceAll", ReplaceAll, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "normalize", Normalize, length: 0);

        // String.fromCharCode
        context.DefineIntrinsicFunction(constructorHandle, constructor, "fromCharCode", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
                sb.Append((char)(ushort)MathHelpers.ToInt32(context.ToNumber(args[i])));
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // String.fromCodePoint
        context.DefineIntrinsicFunction(constructorHandle, constructor, "fromCodePoint", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var n = context.ToNumber(args[i]);
                if (double.IsNaN(n) || n < 0 || n > 0x10FFFF || Math.Floor(n) != n)
                    throw new JsThrownException(context.CreateRangeError("Invalid code point in String.fromCodePoint argument list."));
                sb.Append(char.ConvertFromUtf32((int)n));
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // String.raw
        context.DefineIntrinsicFunction(constructorHandle, constructor, "raw", (_, args) =>
        {
            if (args.Count == 0 || (args[0].Tag != JsValueTag.Object && args[0].Tag != JsValueTag.String))
                throw new JsThrownException(context.CreateTypeError("String.raw: template must be coercible to Object."));
            var template = args[0];
            var templateObj = heap.GetObject(template.AsObjectHandle());
            if (!context.TryGetPropertyValue(templateObj, template, "raw", out var rawValue) || rawValue.Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("String.raw: template.raw must be an object."));
            var rawObj = heap.GetObject(rawValue.AsObjectHandle());
            var rawLen = context.GetArrayLength(rawObj);
            if (rawLen == 0) return JsValue.FromString(string.Empty);
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < rawLen; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (context.TryGetPropertyValue(rawObj, rawValue, key, out var seg))
                    sb.Append(context.ToStringValue(seg));
                if (i + 1 == rawLen) break;
                if (i + 1 < args.Count)
                    sb.Append(context.ToStringValue(args[i + 1]));
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        return new[] { BuiltinBinding.NonEnumerable("String", JsValue.FromObject(constructorHandle)) };
    }

    private static string RequireString(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.String) return thisValue.AsString();
        if (thisValue.Tag == JsValueTag.Object && ctx.Heap.GetObject(thisValue.AsObjectHandle()) is StringObject so)
            return so.Value;
        throw new JsThrownException(ctx.CreateTypeError("String.prototype method called on incompatible receiver."));
    }

    private static int ClampIndex(IReadOnlyList<JsValue> args, int idx, int defaultValue, int length)
    {
        if (idx >= args.Count || args[idx].Tag == JsValueTag.Undefined)
            return Math.Clamp(defaultValue, 0, length);
        var raw = args[idx].Tag == JsValueTag.Int32 ? args[idx].AsInt32() : (int)args[idx].AsNumber();
        return Math.Clamp(raw, 0, length);
    }

    private static int WrapNegative(int value, int length) => value < 0 ? Math.Max(length + value, 0) : Math.Min(value, length);

    private delegate JsValue ProtoMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static void DefineProtoMethod(IBuiltinContext ctx, JsHeap heap, ObjectHandle protoHandle, JsObject proto, string name, ProtoMethod method, int length = 0)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(captured, thisValue, args), length: length);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(fnHandle, callHandle);
        proto.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, fnHandle);
    }

    private static JsValue CharAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        return pos < 0 || pos >= s.Length ? JsValue.FromString(string.Empty) : JsValue.FromString(s[pos].ToString());
    }

    private static JsValue CharCodeAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        return pos < 0 || pos >= s.Length ? JsValue.FromNumber(double.NaN) : JsValue.FromNumber(s[pos]);
    }

    private static JsValue CodePointAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length) return JsValue.Undefined;
        var high = s[pos];
        if (char.IsHighSurrogate(high) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
            return JsValue.FromNumber(char.ConvertToUtf32(high, s[pos + 1]));
        return JsValue.FromNumber(high);
    }

    private static JsValue At(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var raw = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        var idx = raw < 0 ? s.Length + raw : raw;
        return idx < 0 || idx >= s.Length ? JsValue.Undefined : JsValue.FromString(s[idx].ToString());
    }

    private static JsValue IndexOf(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromNumber(s.IndexOf(search, from, StringComparison.Ordinal));
    }

    private static JsValue LastIndexOf(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Min(s.Length, Math.Max(0, (int)ctx.ToNumber(args[1])) + search.Length) : s.Length;
        if (search.Length == 0) return JsValue.FromNumber(from);
        return JsValue.FromNumber(s[..from].LastIndexOf(search, StringComparison.Ordinal));
    }

    private static JsValue Includes(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromBoolean(s.IndexOf(search, from, StringComparison.Ordinal) >= 0);
    }

    private static JsValue StartsWith(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        if (from + search.Length > s.Length) return JsValue.FromBoolean(false);
        return JsValue.FromBoolean(s.AsSpan(from, search.Length).SequenceEqual(search.AsSpan()));
    }

    private static JsValue EndsWith(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var endPos = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : s.Length;
        var start = endPos - search.Length;
        if (start < 0) return JsValue.FromBoolean(false);
        return JsValue.FromBoolean(s.AsSpan(start, search.Length).SequenceEqual(search.AsSpan()));
    }

    private static JsValue Slice(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? WrapNegative((int)ctx.ToNumber(args[0]), len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? WrapNegative((int)ctx.ToNumber(args[1]), len) : len;
        return start >= end ? JsValue.FromString(string.Empty) : JsValue.FromString(s[start..end]);
    }

    private static JsValue Substring(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? Math.Clamp((int)ctx.ToNumber(args[0]), 0, len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, len) : len;
        if (start > end) (start, end) = (end, start);
        return JsValue.FromString(s[start..end]);
    }

    private static JsValue Substr(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (start < 0) start = Math.Max(0, len + start);
        start = Math.Min(start, len);
        var count = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, Math.Min(len - start, (int)ctx.ToNumber(args[1]))) : len - start;
        return JsValue.FromString(s.Substring(start, count));
    }

    private static JsValue Concat(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var sb = new System.Text.StringBuilder(RequireString(ctx, thisValue));
        for (var i = 0; i < args.Count; i++) sb.Append(ctx.ToStringValue(args[i]));
        return JsValue.FromString(sb.ToString());
    }

    private static JsValue Repeat(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var n = args.Count > 0 ? ctx.ToNumber(args[0]) : 0;
        if (double.IsNaN(n) || n < 0 || double.IsInfinity(n))
            throw new JsThrownException(ctx.CreateRangeError("Invalid repeat count."));
        var count = (int)n;
        if (count == 0 || s.Length == 0) return JsValue.FromString(string.Empty);
        var sb = new System.Text.StringBuilder(s.Length * count);
        for (var i = 0; i < count; i++) sb.Append(s);
        return JsValue.FromString(sb.ToString());
    }

    private static JsValue PadStart(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var targetLen = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (targetLen <= s.Length) return JsValue.FromString(s);
        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[1]) : " ";
        if (pad.Length == 0) return JsValue.FromString(s);
        return JsValue.FromString(BuildPadding(pad, targetLen - s.Length) + s);
    }

    private static JsValue PadEnd(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var targetLen = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (targetLen <= s.Length) return JsValue.FromString(s);
        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[1]) : " ";
        if (pad.Length == 0) return JsValue.FromString(s);
        return JsValue.FromString(s + BuildPadding(pad, targetLen - s.Length));
    }

    private static JsValue Split(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var limit = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? Math.Max(0, (int)ctx.ToNumber(args[1])) : int.MaxValue;
        var items = new List<JsValue>();
        if (limit == 0) return CreateArrayResult(ctx, items);
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined) { items.Add(JsValue.FromString(s)); return CreateArrayResult(ctx, items); }
        var sep = ctx.ToStringValue(args[0]);
        if (sep.Length == 0)
        {
            for (var i = 0; i < s.Length && items.Count < limit; i++)
                items.Add(JsValue.FromString(s[i].ToString()));
            return CreateArrayResult(ctx, items);
        }
        var start = 0;
        while (start <= s.Length && items.Count < limit)
        {
            var idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0) break;
            items.Add(JsValue.FromString(s[start..idx]));
            start = idx + sep.Length;
        }
        if (items.Count < limit)
            items.Add(JsValue.FromString(s[start..]));
        return CreateArrayResult(ctx, items);
    }

    private static JsValue Replace(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        if (args.Count < 2) return JsValue.FromString(s);
        var search = ctx.ToStringValue(args[0]);
        var idx = s.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return JsValue.FromString(s);
        var replacement = ResolveReplacement(ctx, args[1], s, idx, search);
        return JsValue.FromString(string.Concat(s[..idx], replacement, s[(idx + search.Length)..]));
    }

    private static JsValue ReplaceAll(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        if (args.Count < 2) return JsValue.FromString(s);
        var search = ctx.ToStringValue(args[0]);
        if (search.Length == 0) return JsValue.FromString(s);
        var replacement = args[1];
        var sb = new System.Text.StringBuilder();
        var start = 0;
        while (start <= s.Length)
        {
            var idx = s.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0) break;
            sb.Append(s[start..idx]);
            sb.Append(ResolveReplacement(ctx, replacement, s, idx, search));
            start = idx + search.Length;
        }
        sb.Append(s[start..]);
        return JsValue.FromString(sb.ToString());
    }

    private static string ResolveReplacement(IBuiltinContext ctx, JsValue replacement, string source, int matchStart, string matched)
    {
        if (replacement.Tag == JsValueTag.Object)
        {
            var obj = ctx.Heap.GetObject(replacement.AsObjectHandle());
            if (obj is JsFunctionObject or NativeFunctionObject)
            {
                var result = ctx.CallFunction(replacement,
                    new JsValue[] { JsValue.FromString(matched), JsValue.FromNumber(matchStart), JsValue.FromString(source) },
                    JsValue.Undefined);
                return ctx.ToStringValue(result);
            }
        }
        return ctx.ToStringValue(replacement);
    }

    private static JsValue Normalize(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var form = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[0]) : "NFC";
        return form switch
        {
            "NFC" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormC)),
            "NFD" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormD)),
            "NFKC" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormKC)),
            "NFKD" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormKD)),
            _ => throw new JsThrownException(ctx.CreateRangeError($"Invalid normalization form: {form}."))
        };
    }

    private static JsValue CreateArrayResult(IBuiltinContext ctx, List<JsValue> items)
    {
        var arr = new ArrayObject();
        arr.SetPrototype(ctx.GetArrayPrototype());
        for (var i = 0; i < items.Count; i++)
            arr.SetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), items[i]);
        _ = arr.DefineOwnProperty("length", new JsPropertyDescriptor(JsValue.FromNumber(items.Count), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(ctx.Heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private static string BuildPadding(string fill, int needed)
    {
        var sb = new System.Text.StringBuilder(needed);
        while (sb.Length < needed) { var remaining = needed - sb.Length; sb.Append(remaining >= fill.Length ? fill : fill[..remaining]); }
        return sb.ToString();
    }
}
