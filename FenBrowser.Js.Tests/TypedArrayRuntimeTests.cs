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
}
