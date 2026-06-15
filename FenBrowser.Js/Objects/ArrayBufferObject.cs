using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Objects;

// ECMA-262 25.1 — ArrayBuffer Objects.
// Wraps a byte[] backing store with [[ArrayBufferByteLength]] and
// [[ArrayBufferDetached]] internal slots. Views (TypedArray, DataView)
// reference the buffer and become detached when it detaches.
// ES2024: resizable ArrayBuffers via [[ArrayBufferMaxByteLength]] slot.
public sealed class ArrayBufferObject : JsObject
{
    private byte[] _data;
    private bool _isResizable;

    // ES2024: a buffer is resizable iff it was constructed with a maxByteLength
    // option — independent of whether the initial length already equals the max
    // (e.g. new ArrayBuffer(8, { maxByteLength: 8 }) is resizable and can shrink).
    public ArrayBufferObject(int byteLength, int maxByteLength = 0, bool resizable = false)
    {
        _data = new byte[byteLength];
        ByteLength = byteLength;
        _isResizable = resizable;
        MaxByteLength = resizable ? maxByteLength : byteLength;
    }

    internal ArrayBufferObject(byte[] data)
    {
        _data = data;
        ByteLength = data.Length;
        MaxByteLength = data.Length;
        _isResizable = false;
    }

    // 25.1.5.1 [[ArrayBufferByteLength]]
    public int ByteLength { get; private set; }

    // ES2024 [[ArrayBufferMaxByteLength]] — maximum size for resizable buffers.
    public int MaxByteLength { get; private set; }

    // ES2024: whether this buffer was created with maxByteLength (resizable).
    public bool IsResizable => _isResizable && !IsDetached;

    // True when this buffer was produced by the %SharedArrayBuffer% constructor.
    // Used by SharedArrayBuffer.prototype accessors/methods to distinguish
    // SharedArrayBuffer instances from plain ArrayBuffer instances.
    public bool IsSharedArrayBuffer { get; init; }

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
        MaxByteLength = 0;
        _isResizable = false;
    }

    // ES2024 25.1.5.X ResizeArrayBuffer(newByteLength)
    public void Resize(int newByteLength)
    {
        if (IsDetached)
            throw new InvalidOperationException("ArrayBuffer is detached.");
        if (newByteLength > MaxByteLength)
            throw new ArgumentOutOfRangeException(nameof(newByteLength), "New byte length exceeds maximum.");
        Array.Resize(ref _data, newByteLength);
        ByteLength = newByteLength;
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
