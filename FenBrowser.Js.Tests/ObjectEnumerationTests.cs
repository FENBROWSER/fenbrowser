using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectEnumerationTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void KeysReturnsOwnEnumerableInOrder()
    {
        Assert.Equal(3, Run("Object.keys({a:1, b:2, c:3}).length;").AsNumber());
        Assert.Equal("a", Run("Object.keys({a:1, b:2, c:3})[0];").AsString());
        Assert.Equal("b", Run("Object.keys({a:1, b:2, c:3})[1];").AsString());
        Assert.Equal("c", Run("Object.keys({a:1, b:2, c:3})[2];").AsString());
    }

    [Fact]
    public void ValuesReturnsOwnEnumerableInOrder()
    {
        Assert.Equal(3, Run("Object.values({a:1, b:2, c:3}).length;").AsNumber());
        Assert.Equal(1, Run("Object.values({a:1, b:2, c:3})[0];").AsNumber());
        Assert.Equal(2, Run("Object.values({a:1, b:2, c:3})[1];").AsNumber());
        Assert.Equal(3, Run("Object.values({a:1, b:2, c:3})[2];").AsNumber());
    }

    [Fact]
    public void EntriesReturnsTwoElementArrays()
    {
        Assert.Equal(2, Run("Object.entries({a:1, b:2}).length;").AsNumber());
        Assert.Equal("a", Run("Object.entries({a:1, b:2})[0][0];").AsString());
        Assert.Equal(1, Run("Object.entries({a:1, b:2})[0][1];").AsNumber());
        Assert.Equal("b", Run("Object.entries({a:1, b:2})[1][0];").AsString());
        Assert.Equal(2, Run("Object.entries({a:1, b:2})[1][1];").AsNumber());
        Assert.Equal(2, Run("Object.entries({a:1, b:2})[0].length;").AsNumber());
    }

    [Fact]
    public void EmptyObjectReturnsEmptyArrays()
    {
        Assert.Equal(0, Run("Object.keys({}).length;").AsNumber());
        Assert.Equal(0, Run("Object.values({}).length;").AsNumber());
        Assert.Equal(0, Run("Object.entries({}).length;").AsNumber());
    }

    [Fact]
    public void NullAndUndefinedThrowTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.keys(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.keys(undefined);"));
        Assert.Throws<JsThrownException>(() => Run("Object.keys();"));
        Assert.Throws<JsThrownException>(() => Run("Object.values(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.entries(null);"));
    }

    [Fact]
    public void PrimitivesReturnEmptyArray()
    {
        // Strict-spec primitives box, but until ToObject lands the empty-array
        // fallback keeps callers unsurprised.
        Assert.Equal(0, Run("Object.keys(42).length;").AsNumber());
        Assert.Equal(0, Run("Object.keys(true).length;").AsNumber());
    }
}
