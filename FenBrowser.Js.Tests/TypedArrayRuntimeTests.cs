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
}
