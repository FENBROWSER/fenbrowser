using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TypedArrayRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ArrayBufferConstructorExists()
    {
        Assert.Equal("function", Run("typeof ArrayBuffer;").AsString());
    }

    [Fact]
    public void ArrayBufferConstructorAllocates()
    {
        var result = Run("new ArrayBuffer(16).byteLength;");
        Assert.Equal(16d, result.AsNumber());
    }

    [Fact]
    public void ArrayBufferIsView()
    {
        Assert.False(Run("ArrayBuffer.isView([]);").AsBoolean());
    }

    [Fact]
    public void ArrayBufferSlice()
    {
        var result = Run("var buf = new ArrayBuffer(8); buf.slice(2, 5).byteLength;");
        Assert.Equal(3d, result.AsNumber());
    }

    [Fact]
    public void DataViewConstructorExists()
    {
        Assert.Equal("function", Run("typeof DataView;").AsString());
    }

    [Fact]
    public void DataViewGetSetInt32()
    {
        var result = Run(@"
            var buf = new ArrayBuffer(8);
            var view = new DataView(buf);
            view.setInt32(0, 12345, true);
            view.getInt32(0, true);
        ");
        Assert.Equal(12345d, result.AsNumber());
    }

    [Fact]
    public void DataViewGetSetFloat64()
    {
        var result = Run(@"
            var buf = new ArrayBuffer(8);
            var view = new DataView(buf);
            view.setFloat64(0, 3.14, true);
            view.getFloat64(0, true);
        ");
        Assert.Equal(3.14, result.AsNumber());
    }

    [Fact]
    public void DataViewGetters()
    {
        var result = Run(@"
            var buf = new ArrayBuffer(16);
            var view = new DataView(buf, 4, 8);
            view.byteLength + view.byteOffset;
        ");
        Assert.Equal(12d, result.AsNumber());
    }

    [Fact]
    public void DataViewGetOutOfRangeThrowsRangeError()
    {
        var result = Run(@"
            var ok = false;
            var view = new DataView(new ArrayBuffer(4));
            try { view.getInt32(2, true); }
            catch (e) { ok = e instanceof RangeError; }
            ok;
        ");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DataViewSetOutOfRangeThrowsRangeError()
    {
        var result = Run(@"
            var ok = false;
            var view = new DataView(new ArrayBuffer(4));
            try { view.setUint32(2, 1, true); }
            catch (e) { ok = e instanceof RangeError; }
            ok;
        ");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TypedArrayInfinityLengthThrowsRangeError()
    {
        // ECMA-262 23.2.5.1 step 3 → AllocateTypedArray routes the length through
        // ToIndex, which throws RangeError for Infinity. Previously the (int) cast
        // overflowed and crashed the host buffer allocation.
        var result = Run(@"
            var ok = false;
            try { new Int8Array(Infinity); }
            catch (e) { ok = e instanceof RangeError; }
            ok;
        ");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TypedArrayExcessiveArrayLikeLengthThrowsRangeError()
    {
        // ECMA-262 23.2.5.1 step 6 (object/array-like path) → AllocateTypedArrayBuffer
        // applies the byte-size limit; length 2^53 must surface as a RangeError rather
        // than overflowing the int length*elementSize multiply.
        var result = Run(@"
            var ok = false;
            try { new Int16Array({ length: Math.pow(2, 53) }); }
            catch (e) { ok = e instanceof RangeError; }
            ok;
        ");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Uint8ArrayConstructorExists()
    {
        Assert.Equal("function", Run("typeof Uint8Array;").AsString());
    }

    [Fact]
    public void Uint8ArrayByLength()
    {
        var result = Run("new Uint8Array(4).length;");
        Assert.Equal(4d, result.AsNumber());
    }

    [Fact]
    public void Float64ArrayByLength()
    {
        var result = Run("new Float64Array(3).length;");
        Assert.Equal(3d, result.AsNumber());
    }

    [Fact]
    public void TypedArrayGetters()
    {
        var result = Run(@"
            var arr = new Uint8Array(5);
            arr.length + arr.byteLength + arr.byteOffset;
        ");
        Assert.Equal(10d, result.AsNumber()); // 5 + 5 + 0
    }

    [Fact]
    public void TypedArrayBytesPerElement()
    {
        Assert.Equal(4d, Run("Int32Array.BYTES_PER_ELEMENT;").AsNumber());
        Assert.Equal(8d, Run("Float64Array.BYTES_PER_ELEMENT;").AsNumber());
        Assert.Equal(1d, Run("Uint8Array.BYTES_PER_ELEMENT;").AsNumber());
    }

    [Fact]
    public void AllTypedArrayConstructorsExist()
    {
        var names = new[] { "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array", "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array" };
        foreach (var name in names)
        {
            var result = Run($"typeof {name};");
            Assert.Equal("function", result.AsString());
        }
    }

    [Fact]
    public void TypedArrayPrototypesShareCommonParent()
    {
        Assert.True(Run(@"
            var a = Object.getPrototypeOf(Uint8Array.prototype);
            var b = Object.getPrototypeOf(Int16Array.prototype);
            a === b;
        ").AsBoolean());
    }

    [Fact]
    public void TypedArrayCommonMethodsExist()
    {
        Assert.True(Run(@"
            var p = Uint8Array.prototype;
            typeof p.at === 'function' &&
            typeof p.copyWithin === 'function' &&
            typeof p.entries === 'function' &&
            typeof p.every === 'function' &&
            typeof p.fill === 'function' &&
            typeof p.filter === 'function' &&
            typeof p.find === 'function' &&
            typeof p.findIndex === 'function' &&
            typeof p.findLast === 'function' &&
            typeof p.findLastIndex === 'function' &&
            typeof p.forEach === 'function' &&
            typeof p.includes === 'function' &&
            typeof p.indexOf === 'function' &&
            typeof p.join === 'function' &&
            typeof p.keys === 'function' &&
            typeof p.lastIndexOf === 'function' &&
            typeof p.map === 'function' &&
            typeof p.reduce === 'function' &&
            typeof p.reduceRight === 'function' &&
            typeof p.reverse === 'function' &&
            typeof p.some === 'function' &&
            typeof p.sort === 'function' &&
            typeof p.subarray === 'function' &&
            typeof p.values === 'function' &&
            typeof p.with === 'function';
        ").AsBoolean());
    }

    [Fact]
    public void TypedArrayMapReduceAndIteratorsWork()
    {
        Assert.Equal(11d, Run(@"
            var a = new Uint8Array(4);
            a.fill(1);
            var b = a.map(function(x, i) { return x + i; });
            var sum = b.reduce(function(acc, x) { return acc + x; }, 0);
            var it = a.values();
            var first = it.next().value;
            sum + first;
        ").AsNumber());
    }
}
