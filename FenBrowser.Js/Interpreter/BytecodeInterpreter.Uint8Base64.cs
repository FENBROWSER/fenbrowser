using System;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Uint8Array base64 / hex methods (proposal-arraybuffer-base64, ES2025).
//   static  Uint8Array.fromBase64(string, options)
//   static  Uint8Array.fromHex(string)
//   proto   Uint8Array.prototype.toBase64(options)
//   proto   Uint8Array.prototype.toHex()
//   proto   Uint8Array.prototype.setFromBase64(string, options) -> { read, written }
//   proto   Uint8Array.prototype.setFromHex(string)             -> { read, written }
//
// These live only on Uint8Array, so they are installed after the typed-array
// constructors are materialized rather than on the shared %TypedArray% proto.
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle _uint8ArrayPrototypeHandle;

    private enum LastChunkHandling
    {
        Loose,
        Strict,
        StopBeforePartial,
    }

    void Builtins.IBuiltinContext.InstallUint8ArrayBase64Hex()
    {
        if (_typedArrayConstructors is null)
        {
            return;
        }

        ObjectHandle ctorHandle = default;
        var found = false;
        foreach (var binding in _typedArrayConstructors)
        {
            if (binding.Name == "Uint8Array" && binding.Value.Tag == JsValueTag.Object)
            {
                ctorHandle = binding.Value.AsObjectHandle();
                found = true;
                break;
            }
        }

        if (!found)
        {
            return;
        }

        var ctor = _heap.GetObject(ctorHandle);
        if (!ctor.TryGetOwnProperty("prototype", out var protoDesc) || protoDesc.Value.Tag != JsValueTag.Object)
        {
            return;
        }

        var protoHandle = protoDesc.Value.AsObjectHandle();
        var proto = _heap.GetObject(protoHandle);
        _uint8ArrayPrototypeHandle = protoHandle;

        DefineIntrinsicFunction(ctorHandle, ctor, "fromBase64", (_, args) => Uint8ArrayFromBase64(args), length: 1);
        DefineIntrinsicFunction(ctorHandle, ctor, "fromHex", (_, args) => Uint8ArrayFromHex(args), length: 1);

        DefineNativePrototypeMethod(protoHandle, proto, "toBase64", (thisValue, args) => Uint8ArrayToBase64(thisValue, args), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "toHex", (thisValue, _) => Uint8ArrayToHex(thisValue), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "setFromBase64", (thisValue, args) => Uint8ArraySetFromBase64(thisValue, args), length: 1);
        DefineNativePrototypeMethod(protoHandle, proto, "setFromHex", (thisValue, args) => Uint8ArraySetFromHex(thisValue, args), length: 1);
    }

    // Validates the receiver is a Uint8Array. Detachment is intentionally NOT
    // checked here: the spec validates the buffer only after reading the options
    // object (whose getters can detach it), so callers run ThrowIfDetached after
    // option parsing.
    private Uint8Array RequireUint8Array(JsValue thisValue, string method)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not Uint8Array u8)
        {
            throw new JsThrownException(CreateTypeError($"Uint8Array.prototype.{method} requires a Uint8Array receiver."));
        }

        return u8;
    }

    private void ThrowIfDetached(Uint8Array u8, string method)
    {
        if (u8.IsViewDetached)
        {
            throw new JsThrownException(CreateTypeError($"Uint8Array.prototype.{method} called on a detached buffer."));
        }
    }

    private JsValue MakeUint8Array(byte[] data)
    {
        var buffer = new ArrayBufferObject(data.Length);
        buffer.SetPrototype(EnsureArrayBufferPrototype());
        Array.Copy(data, buffer.Data, data.Length);
        var view = new Uint8Array(buffer, 0, data.Length);
        view.SetPrototype(_uint8ArrayPrototypeHandle);
        return JsValue.FromObject(_heap.AllocateObject(view, AllocationSite.Current()));
    }

    private JsValue MakeReadWrittenResult(int read, int written)
    {
        var result = CreateOrdinaryObject();
        result.SetPrototype(EnsureObjectPrototype());
        _ = result.DefineOwnProperty("read", new JsPropertyDescriptor(JsValue.FromNumber(read), Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty("written", new JsPropertyDescriptor(JsValue.FromNumber(written), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(_heap.AllocateObject(result, AllocationSite.Current()));
    }

    // --- option parsing -----------------------------------------------------

    // 1.1.x GetOptionsObject: undefined -> none; Object -> use; else TypeError.
    private JsObject? GetBase64Options(JsValue options, string method)
    {
        if (options.Tag == JsValueTag.Undefined)
        {
            return null;
        }

        if (options.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError($"{method}: options must be an object or undefined."));
        }

        return _heap.GetObject(options.AsObjectHandle());
    }

    // A string-valued option must be undefined or a primitive String (it is not
    // coerced — see fromBase64/option-coercion). Validates against the allowed set.
    private string GetStringOption(JsObject? opts, JsValue optsValue, string name, string defaultValue, string[] allowed, string method)
    {
        if (opts is null)
        {
            return defaultValue;
        }

        if (!TryGetPropertyValue(opts, optsValue, name, out var value) || value.Tag == JsValueTag.Undefined)
        {
            return defaultValue;
        }

        if (value.Tag != JsValueTag.String)
        {
            throw new JsThrownException(CreateTypeError($"{method}: '{name}' must be a string."));
        }

        var s = value.AsString();
        if (Array.IndexOf(allowed, s) < 0)
        {
            throw new JsThrownException(CreateTypeError($"{method}: '{name}' has an invalid value."));
        }

        return s;
    }

    private bool GetBooleanOption(JsObject? opts, JsValue optsValue, string name)
    {
        if (opts is null)
        {
            return false;
        }

        if (!TryGetPropertyValue(opts, optsValue, name, out var value))
        {
            return false;
        }

        return IsTruthy(value);
    }

    private LastChunkHandling GetLastChunkHandling(JsObject? opts, JsValue optsValue, string method)
    {
        var s = GetStringOption(opts, optsValue, "lastChunkHandling", "loose",
            new[] { "loose", "strict", "stop-before-partial" }, method);
        return s switch
        {
            "strict" => LastChunkHandling.Strict,
            "stop-before-partial" => LastChunkHandling.StopBeforePartial,
            _ => LastChunkHandling.Loose,
        };
    }

    // --- base64 ------------------------------------------------------------

    private static bool IsAsciiWhitespace(char c)
        => c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static int Base64Value(char c, bool url)
    {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (!url)
        {
            if (c == '+') return 62;
            if (c == '/') return 63;
        }
        else
        {
            if (c == '-') return 62;
            if (c == '_') return 63;
        }

        return -1;
    }

    // Core base64 decoder. Writes decoded bytes to sink, never exceeding
    // capacity bytes; a chunk that would not fully fit is not consumed. Returns
    // the number of input characters consumed (the "read" count). Throws a JS
    // SyntaxError for malformed input — bytes already handed to sink stay.
    private int DecodeBase64(string s, bool url, LastChunkHandling lch, int capacity, Action<byte> sink)
    {
        var written = 0;
        var read = 0;
        var chunk = new int[4];
        var chunkLen = 0;
        var i = 0;
        var n = s.Length;

        while (true)
        {
            // Once the target is full, decoding stops successfully — any
            // remaining characters (even illegal ones) are simply not read.
            if (written >= capacity)
            {
                return read;
            }

            while (i < n && IsAsciiWhitespace(s[i])) i++;
            if (i == n) break;

            var c = s[i];
            if (c == '=')
            {
                if (chunkLen < 2)
                {
                    throw new JsThrownException(CreateSyntaxError("Invalid base64: unexpected padding."));
                }

                i++; // consume first '='
                var paddingComplete = chunkLen == 3;
                if (chunkLen == 2)
                {
                    while (i < n && IsAsciiWhitespace(s[i])) i++;
                    if (i < n && s[i] == '=')
                    {
                        i++;
                        paddingComplete = true;
                    }
                }

                if (!paddingComplete)
                {
                    if (lch == LastChunkHandling.StopBeforePartial)
                    {
                        return read;
                    }

                    throw new JsThrownException(CreateSyntaxError("Invalid base64: incomplete padding."));
                }

                while (i < n && IsAsciiWhitespace(s[i])) i++;
                if (i != n)
                {
                    throw new JsThrownException(CreateSyntaxError("Invalid base64: characters after padding."));
                }

                // If the final chunk does not fit the remaining capacity it is
                // left unconsumed: report the boundary before it (read), not the
                // input end (i).
                var paddedEmitted = EmitPartialBase64(chunk, chunkLen, lch, capacity, ref written, sink);
                return paddedEmitted ? i : read;
            }

            var v = Base64Value(c, url);
            if (v < 0)
            {
                throw new JsThrownException(CreateSyntaxError("Invalid base64: illegal character."));
            }

            chunk[chunkLen++] = v;
            i++;
            if (chunkLen == 4)
            {
                if (written + 3 > capacity)
                {
                    return read; // stop before a full chunk that does not fit
                }

                sink((byte)((chunk[0] << 2) | (chunk[1] >> 4)));
                sink((byte)(((chunk[1] & 0x0F) << 4) | (chunk[2] >> 2)));
                sink((byte)(((chunk[2] & 0x03) << 6) | chunk[3]));
                written += 3;
                chunkLen = 0;
                read = i;
            }
        }

        if (chunkLen > 0)
        {
            if (lch == LastChunkHandling.StopBeforePartial)
            {
                return read;
            }

            if (lch == LastChunkHandling.Strict)
            {
                throw new JsThrownException(CreateSyntaxError("Invalid base64: missing padding."));
            }

            if (chunkLen == 1)
            {
                throw new JsThrownException(CreateSyntaxError("Invalid base64: lone character."));
            }

            var emitted = EmitPartialBase64(chunk, chunkLen, lch, capacity, ref written, sink);
            if (emitted)
            {
                read = n;
            }
        }
        else
        {
            read = i;
        }

        return read;
    }

    // Emits the 1 or 2 bytes of a final partial chunk (chunkLen 2 or 3). Honors
    // the capacity (no partial write of the chunk) and, under Strict, requires
    // the discarded low bits to be zero. Returns whether the bytes were written.
    private bool EmitPartialBase64(int[] chunk, int chunkLen, LastChunkHandling lch, int capacity, ref int written, Action<byte> sink)
    {
        var nbytes = chunkLen - 1;
        if (written + nbytes > capacity)
        {
            return false;
        }

        if (lch == LastChunkHandling.Strict)
        {
            var extraBits = chunkLen == 2 ? (chunk[1] & 0x0F) : (chunk[2] & 0x03);
            if (extraBits != 0)
            {
                throw new JsThrownException(CreateSyntaxError("Invalid base64: non-zero padding bits."));
            }
        }

        sink((byte)((chunk[0] << 2) | (chunk[1] >> 4)));
        if (chunkLen == 3)
        {
            sink((byte)(((chunk[1] & 0x0F) << 4) | (chunk[2] >> 2)));
        }

        written += nbytes;
        return true;
    }

    private JsValue Uint8ArrayFromBase64(IReadOnlyList<JsValue> args)
    {
        var input = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (input.Tag != JsValueTag.String)
        {
            throw new JsThrownException(CreateTypeError("Uint8Array.fromBase64 requires a string."));
        }

        var optsValue = args.Count > 1 ? args[1] : JsValue.Undefined;
        var opts = GetBase64Options(optsValue, "Uint8Array.fromBase64");
        var url = GetStringOption(opts, optsValue, "alphabet", "base64", new[] { "base64", "base64url" }, "Uint8Array.fromBase64") == "base64url";
        var lch = GetLastChunkHandling(opts, optsValue, "Uint8Array.fromBase64");

        var bytes = new System.Collections.Generic.List<byte>();
        _ = DecodeBase64(input.AsString(), url, lch, int.MaxValue, b => bytes.Add(b));
        return MakeUint8Array(bytes.ToArray());
    }

    private JsValue Uint8ArraySetFromBase64(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var self = RequireUint8Array(thisValue, "setFromBase64");
        var input = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (input.Tag != JsValueTag.String)
        {
            throw new JsThrownException(CreateTypeError("Uint8Array.prototype.setFromBase64 requires a string."));
        }

        var optsValue = args.Count > 1 ? args[1] : JsValue.Undefined;
        var opts = GetBase64Options(optsValue, "Uint8Array.prototype.setFromBase64");
        var url = GetStringOption(opts, optsValue, "alphabet", "base64", new[] { "base64", "base64url" }, "Uint8Array.prototype.setFromBase64") == "base64url";
        var lch = GetLastChunkHandling(opts, optsValue, "Uint8Array.prototype.setFromBase64");

        ThrowIfDetached(self, "setFromBase64");
        var written = 0;
        var raw = self.Buffer.Data;
        var baseOffset = self.ByteOffset;
        var read = DecodeBase64(input.AsString(), url, lch, self.Length, b =>
        {
            raw[baseOffset + written] = b;
            written++;
        });
        return MakeReadWrittenResult(read, written);
    }

    private JsValue Uint8ArrayToBase64(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var self = RequireUint8Array(thisValue, "toBase64");
        var optsValue = args.Count > 0 ? args[0] : JsValue.Undefined;
        var opts = GetBase64Options(optsValue, "Uint8Array.prototype.toBase64");
        var url = GetStringOption(opts, optsValue, "alphabet", "base64", new[] { "base64", "base64url" }, "Uint8Array.prototype.toBase64") == "base64url";
        var omitPadding = GetBooleanOption(opts, optsValue, "omitPadding");

        ThrowIfDetached(self, "toBase64");
        var alphabet = url
            ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
            : "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        var raw = self.Buffer.Data;
        var offset = self.ByteOffset;
        var length = self.Length;
        var sb = new System.Text.StringBuilder((length + 2) / 3 * 4);
        var i = 0;
        for (; i + 3 <= length; i += 3)
        {
            var b0 = raw[offset + i];
            var b1 = raw[offset + i + 1];
            var b2 = raw[offset + i + 2];
            _ = sb.Append(alphabet[b0 >> 2]);
            _ = sb.Append(alphabet[((b0 & 0x03) << 4) | (b1 >> 4)]);
            _ = sb.Append(alphabet[((b1 & 0x0F) << 2) | (b2 >> 6)]);
            _ = sb.Append(alphabet[b2 & 0x3F]);
        }

        var rem = length - i;
        if (rem == 1)
        {
            var b0 = raw[offset + i];
            _ = sb.Append(alphabet[b0 >> 2]);
            _ = sb.Append(alphabet[(b0 & 0x03) << 4]);
            if (!omitPadding) _ = sb.Append("==");
        }
        else if (rem == 2)
        {
            var b0 = raw[offset + i];
            var b1 = raw[offset + i + 1];
            _ = sb.Append(alphabet[b0 >> 2]);
            _ = sb.Append(alphabet[((b0 & 0x03) << 4) | (b1 >> 4)]);
            _ = sb.Append(alphabet[(b1 & 0x0F) << 2]);
            if (!omitPadding) _ = sb.Append('=');
        }

        return JsValue.FromString(sb.ToString());
    }

    // --- hex ---------------------------------------------------------------

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    // Core hex decoder, mirroring DecodeBase64's capacity/partial contract.
    private int DecodeHex(string s, int capacity, Action<byte> sink)
    {
        if ((s.Length & 1) == 1)
        {
            throw new JsThrownException(CreateSyntaxError("Invalid hex: odd length."));
        }

        var written = 0;
        var read = 0;
        for (var i = 0; i + 1 < s.Length; i += 2)
        {
            if (written >= capacity)
            {
                return read; // target full
            }

            var hi = HexValue(s[i]);
            var lo = HexValue(s[i + 1]);
            if (hi < 0 || lo < 0)
            {
                throw new JsThrownException(CreateSyntaxError("Invalid hex: illegal character."));
            }

            sink((byte)((hi << 4) | lo));
            written++;
            read = i + 2;
        }

        return read;
    }

    private JsValue Uint8ArrayFromHex(IReadOnlyList<JsValue> args)
    {
        var input = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (input.Tag != JsValueTag.String)
        {
            throw new JsThrownException(CreateTypeError("Uint8Array.fromHex requires a string."));
        }

        var bytes = new System.Collections.Generic.List<byte>();
        _ = DecodeHex(input.AsString(), int.MaxValue, b => bytes.Add(b));
        return MakeUint8Array(bytes.ToArray());
    }

    private JsValue Uint8ArraySetFromHex(JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var self = RequireUint8Array(thisValue, "setFromHex");
        var input = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (input.Tag != JsValueTag.String)
        {
            throw new JsThrownException(CreateTypeError("Uint8Array.prototype.setFromHex requires a string."));
        }

        ThrowIfDetached(self, "setFromHex");
        var written = 0;
        var raw = self.Buffer.Data;
        var baseOffset = self.ByteOffset;
        var read = DecodeHex(input.AsString(), self.Length, b =>
        {
            raw[baseOffset + written] = b;
            written++;
        });
        return MakeReadWrittenResult(read, written);
    }

    private JsValue Uint8ArrayToHex(JsValue thisValue)
    {
        var self = RequireUint8Array(thisValue, "toHex");
        ThrowIfDetached(self, "toHex");
        var raw = self.Buffer.Data;
        var offset = self.ByteOffset;
        var length = self.Length;
        const string hex = "0123456789abcdef";
        var sb = new System.Text.StringBuilder(length * 2);
        for (var i = 0; i < length; i++)
        {
            var b = raw[offset + i];
            _ = sb.Append(hex[b >> 4]);
            _ = sb.Append(hex[b & 0x0F]);
        }

        return JsValue.FromString(sb.ToString());
    }
}
