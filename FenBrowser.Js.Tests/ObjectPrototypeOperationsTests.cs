using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectPrototypeOperationsTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void GetPrototypeOfReturnsObjectPrototype()
    {
        // The prototype of a plain object literal is %Object.prototype%, which has
        // the constructor pointing back at Object.
        Assert.Equal(JsValueTag.Object, Run("Object.getPrototypeOf({});").Tag);
    }

    [Fact]
    public void GetPrototypeOfNullThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.getPrototypeOf(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.getPrototypeOf(undefined);"));
    }

    [Fact]
    public void CreateWithNullPrototypeYieldsBareObject()
    {
        // Object.create(null) makes an object whose [[Prototype]] is null - so
        // looking up a property finds nothing and Object.getPrototypeOf returns null.
        Assert.Equal(JsValueTag.Null, Run("Object.getPrototypeOf(Object.create(null));").Tag);
    }

    [Fact]
    public void CreateWithProtoEstablishesChain()
    {
        Assert.Equal(42, Run("var p = {x:42}; var c = Object.create(p); c.x;").AsNumber());
    }

    [Fact]
    public void CreateWithNonObjectNonNullProtoThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.create(42);"));
        Assert.Throws<JsThrownException>(() => Run("Object.create('abc');"));
    }

    [Fact]
    public void SetPrototypeOfChangesProtoChain()
    {
        Assert.Equal(7, Run("var c = {}; Object.setPrototypeOf(c, {y:7}); c.y;").AsNumber());
    }

    [Fact]
    public void SetPrototypeOfToNullDetachesChain()
    {
        Assert.Equal(JsValueTag.Null, Run("var c = {}; Object.setPrototypeOf(c, null); Object.getPrototypeOf(c);").Tag);
    }

    [Fact]
    public void SetPrototypeOfReturnsObject()
    {
        Assert.Equal(1, Run("var c = {a:1}; Object.setPrototypeOf(c, null).a;").AsNumber());
    }

    [Fact]
    public void SetPrototypeOfWithBadProtoThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.setPrototypeOf({}, 42);"));
        Assert.Throws<JsThrownException>(() => Run("Object.setPrototypeOf({}, 'abc');"));
        Assert.Throws<JsThrownException>(() => Run("Object.setPrototypeOf(null, {});"));
    }
}
