using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectFromEntriesTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void BuildsObjectFromEntriesArray()
    {
        Assert.Equal(1, Run("var o = Object.fromEntries([['a',1],['b',2]]); o.a;").AsNumber());
        Assert.Equal(2, Run("var o = Object.fromEntries([['a',1],['b',2]]); o.b;").AsNumber());
    }

    [Fact]
    public void EmptyArrayProducesEmptyObject()
    {
        Assert.Equal(JsValueTag.Undefined, Run("var o = Object.fromEntries([]); o.anything;").Tag);
    }

    [Fact]
    public void LastDuplicateKeyWins()
    {
        Assert.Equal(99, Run("Object.fromEntries([['k',1],['k',99]]).k;").AsNumber());
    }

    [Fact]
    public void RoundTripsWithObjectEntries()
    {
        Assert.Equal(7, Run("Object.fromEntries(Object.entries({x:7,y:8})).x;").AsNumber());
        Assert.Equal(8, Run("Object.fromEntries(Object.entries({x:7,y:8})).y;").AsNumber());
    }

    [Fact]
    public void NonArrayIterableThrowsTypeError()
    {
        // Full iterator protocol pending; non-Array iterables surface as TypeError.
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries({});"));
    }

    [Fact]
    public void NonObjectArgumentThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries(42);"));
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries();"));
    }

    [Fact]
    public void NonEntryEntriesThrowTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries([42]);"));
        Assert.Throws<JsThrownException>(() => Run("Object.fromEntries(['abc']);"));
    }
}
