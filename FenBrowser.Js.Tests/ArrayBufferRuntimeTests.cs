using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayBufferRuntimeTests
{
    private static JsValue Eval(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ConstructorAllocatesBuffer()
    {
        var buf = new ArrayBufferObject(16);
        Assert.Equal(16, buf.ByteLength);
        Assert.False(buf.IsDetached);
        Assert.Equal(16, buf.Data.Length);
    }

    [Fact]
    public void DetachZeroesBuffer()
    {
        var buf = new ArrayBufferObject(8);
        buf.Data[0] = 0xFF;
        buf.Detach();
        Assert.True(buf.IsDetached);
        Assert.Equal(0, buf.ByteLength);
    }

    [Fact]
    public void CloneCreatesIndependentCopy()
    {
        var buf = new ArrayBufferObject(8);
        buf.Data[0] = 0x42;
        var clone = buf.Clone(0, 8);
        Assert.Equal(0x42, clone.Data[0]);
        clone.Data[0] = 0x99;
        Assert.Equal(0x42, buf.Data[0]);
    }

    // ECMA-262 25.1.3.1 step 4 + 6.2.6.1 CreateByteDataBlock: byteLength must be
    // a non-negative integer ≤ the implementation-defined limit; otherwise RangeError.
    // Backing store is a managed byte[] so the limit is int.MaxValue.
    // Audit gap §1 crashes #3 (allocation-limit.js) and #4 (data-allocation-after-object-creation.js).
    private static bool CaughtRangeError(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var caught = '';
            try { " + src + @" }
            catch (e) { caught = (e && e.constructor && e.constructor.name) || ''; }
            caught;"));
        return new BytecodeInterpreter().Execute(fn).AsString() == "RangeError";
    }

    [Fact]
    public void Constructor_SevenPiB_ThrowsRangeError()
        => Assert.True(CaughtRangeError("new ArrayBuffer(7 * 1125899906842624);"));

    [Fact]
    public void Constructor_Pow2_53Minus1_ThrowsRangeError()
        => Assert.True(CaughtRangeError("new ArrayBuffer(9007199254740991);"));

    [Fact]
    public void Constructor_JustOverInt32Max_ThrowsRangeError()
        => Assert.True(CaughtRangeError("new ArrayBuffer(2147483648);"));

    [Fact]
    public void Constructor_NegativeLength_ThrowsRangeError()
        => Assert.True(CaughtRangeError("new ArrayBuffer(-1);"));

    [Fact]
    public void ResizableBufferReportsMaxByteLengthAndCanResize()
    {
        var result = Eval(@"
            var buf = new ArrayBuffer(4, { maxByteLength: 8 });
            var before = buf.resizable === true && buf.maxByteLength === 8 && buf.byteLength === 4;
            buf.resize(6);
            before && buf.byteLength === 6 && buf.maxByteLength === 8;
        ");
        Assert.True(result.AsBoolean());
    }
}

public sealed class TypedArrayObjectTests
{
    [Fact]
    public void Uint8Array_GetSetElement()
    {
        var buf = new ArrayBufferObject(4);
        var view = new ConcreteTypedArray(buf, 0, 4, TypedArrayElementType.Uint8, 1);
        view.SetElement(0, JsValue.FromNumber(42));
        Assert.Equal(42d, view.GetElement(0).AsNumber());
    }

    [Fact]
    public void Int32Array_GetSetElement()
    {
        var buf = new ArrayBufferObject(8);
        var view = new ConcreteTypedArray(buf, 0, 8, TypedArrayElementType.Int32, 4);
        view.SetElement(0, JsValue.FromNumber(-12345));
        Assert.Equal(-12345d, view.GetElement(0).AsNumber());
    }

    [Fact]
    public void Float64Array_GetSetElement()
    {
        var buf = new ArrayBufferObject(8);
        var view = new ConcreteTypedArray(buf, 0, 8, TypedArrayElementType.Float64, 8);
        view.SetElement(0, JsValue.FromNumber(3.14159));
        Assert.Equal(3.14159, view.GetElement(0).AsNumber());
    }

    [Fact]
    public void Uint8ClampedArray_ClampsValues()
    {
        var buf = new ArrayBufferObject(4);
        var view = new ConcreteTypedArray(buf, 0, 4, TypedArrayElementType.Uint8Clamped, 1);
        view.SetElement(0, JsValue.FromNumber(300));
        Assert.Equal(255d, view.GetElement(0).AsNumber());
        view.SetElement(1, JsValue.FromNumber(-10));
        Assert.Equal(0d, view.GetElement(1).AsNumber());
    }

    [Fact]
    public void OutOfBoundsGetReturnsUndefined()
    {
        var buf = new ArrayBufferObject(4);
        var view = new ConcreteTypedArray(buf, 0, 4, TypedArrayElementType.Uint8, 1);
        Assert.True(view.GetElement(10).Tag == JsValueTag.Undefined);
    }

    [Fact]
    public void LengthComputedCorrectly()
    {
        var buf = new ArrayBufferObject(16);
        var view = new ConcreteTypedArray(buf, 0, 16, TypedArrayElementType.Float64, 8);
        Assert.Equal(2, view.Length);
    }

    // Concrete test implementation
    private sealed class ConcreteTypedArray : TypedArrayObject
    {
        private readonly TypedArrayElementType _elementType;
        private readonly int _elementSize;
        public ConcreteTypedArray(ArrayBufferObject buffer, int byteOffset, int byteLength, TypedArrayElementType elementType, int elementSize)
            : base(buffer, byteOffset, byteLength)
        {
            _elementType = elementType;
            _elementSize = elementSize;
        }
        public override TypedArrayElementType ElementType => _elementType;
        public override int ElementSize => _elementSize;
    }
}
