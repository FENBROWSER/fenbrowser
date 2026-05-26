using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Objects;

// ECMA-262 25.1 — ArrayBuffer Objects.
// Wraps a byte[] backing store with [[ArrayBufferByteLength]] and
// [[ArrayBufferDetached]] internal slots. Views (TypedArray, DataView)
// reference the buffer and become detached when it detaches.
public sealed class ArrayBufferObject : JsObject
{
    private byte[] _data;

    public ArrayBufferObject(int byteLength)
    {
        _data = new byte[byteLength];
        ByteLength = byteLength;
    }

    internal ArrayBufferObject(byte[] data)
    {
        _data = data;
        ByteLength = data.Length;
    }

    // 25.1.5.1 [[ArrayBufferByteLength]]
    public int ByteLength { get; private set; }

    // 25.1.5.2 [[ArrayBufferData]] — raw byte access.
    public byte[] Data
    {
        get
        {
            if (IsDetached)
                throw new InvalidOperationException("ArrayBuffer is detached.");
            return _data;
        }
    }

    // 25.1.5.3 [[ArrayBufferDetached]]
    public bool IsDetached { get; private set; }

    // 25.1.5.4 DetachArrayBuffer()
    public void Detach()
    {
        IsDetached = true;
        _data = Array.Empty<byte>();
        ByteLength = 0;
    }

    // 25.1.5.5 CloneArrayBuffer(src, srcByteOffset, srcLength)
    public ArrayBufferObject Clone(int byteOffset, int byteLength)
    {
        if (IsDetached)
            throw new InvalidOperationException("ArrayBuffer is detached.");
        var copy = new byte[byteLength];
        Array.Copy(_data, byteOffset, copy, 0, byteLength);
        return new ArrayBufferObject(copy);
    }

    // 25.1.5.6 RawBytesToNumeric / 25.1.5.7 GetValueFromBuffer
    // and 25.1.5.8 SetValueInBuffer are in TypedArrayObject / DataViewObject.
}
