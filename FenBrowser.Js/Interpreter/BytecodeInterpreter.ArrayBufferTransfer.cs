using System;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ES2024 ArrayBuffer transfer surface (25.1.6.x):
//   ArrayBuffer.prototype.transfer([newLength])            — resizable result
//   ArrayBuffer.prototype.transferToFixedLength([newLength]) — fixed-length result
//   get ArrayBuffer.prototype.detached
//
// transfer moves the backing store into a fresh ArrayBuffer (resized as
// requested) and detaches the original. Installed from ArrayBufferBuiltin.
public sealed partial class BytecodeInterpreter
{
    void Builtins.IBuiltinContext.InstallArrayBufferTransfer()
    {
        var protoHandle = EnsureArrayBufferPrototype();
        var proto = _heap.GetObject(protoHandle);

        DefineNativePrototypeMethod(protoHandle, proto, "transfer",
            (thisValue, args) => ArrayBufferTransfer(thisValue, args, fixedLength: false), length: 0);
        DefineNativePrototypeMethod(protoHandle, proto, "transferToFixedLength",
            (thisValue, args) => ArrayBufferTransfer(thisValue, args, fixedLength: true), length: 0);

        if (!proto.TryGetOwnProperty("detached", out _))
        {
            var detachedGetter = new NativeFunctionObject("get detached", (thisValue, _) =>
            {
                var buf = RequireArrayBuffer(thisValue, "detached");
                return JsValue.FromBoolean(buf.IsDetached);
            }, length: 0);
            var detachedGetterHandle = _heap.AllocateObject(detachedGetter, AllocationSite.Current());
            _ = proto.DefineOwnProperty("detached", JsPropertyDescriptor.Accessor(
                JsValue.FromObject(detachedGetterHandle), JsValue.Undefined, Enumerable: false, Configurable: true));
            _heap.WriteBarrier(protoHandle, detachedGetterHandle);
        }
    }

    private ArrayBufferObject RequireArrayBuffer(JsValue thisValue, string method)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            _heap.GetObject(thisValue.AsObjectHandle()) is not ArrayBufferObject buf)
        {
            throw new JsThrownException(CreateTypeError($"ArrayBuffer.prototype.{method} called on a non-ArrayBuffer."));
        }

        return buf;
    }

    private JsValue ArrayBufferTransfer(JsValue thisValue, IReadOnlyList<JsValue> args, bool fixedLength)
    {
        var source = RequireArrayBuffer(thisValue, fixedLength ? "transferToFixedLength" : "transfer");
        if (source.IsDetached)
        {
            throw new JsThrownException(CreateTypeError("Cannot transfer a detached ArrayBuffer."));
        }

        var oldByteLength = source.ByteLength;
        var newByteLength = oldByteLength;
        if (args.Count > 0 && args[0].Tag != JsValueTag.Undefined)
        {
            var requested = (long)ToNumber(args[0]);
            if (requested < 0 || requested > int.MaxValue)
            {
                throw new JsThrownException(CreateRangeError("Invalid ArrayBuffer transfer length."));
            }

            newByteLength = (int)requested;
        }

        // A plain transfer preserves resizability (maxByteLength); the
        // fixed-length variant produces a non-resizable buffer.
        var resizable = !fixedLength && source.IsResizable;
        var result = new ArrayBufferObject(newByteLength, resizable ? source.MaxByteLength : 0, resizable);
        result.SetPrototype(EnsureArrayBufferPrototype());

        var copyLength = Math.Min(oldByteLength, newByteLength);
        if (copyLength > 0)
        {
            Array.Copy(source.Data, result.Data, copyLength);
        }

        source.Detach();
        return JsValue.FromObject(_heap.AllocateObject(result, AllocationSite.Current()));
    }
}
