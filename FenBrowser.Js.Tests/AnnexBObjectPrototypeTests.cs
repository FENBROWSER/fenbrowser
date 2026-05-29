using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 Annex B B.2.2 legacy Object.prototype accessors and methods.
public sealed class AnnexBObjectPrototypeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ProtoGetterReturnsPrototype()
    {
        // o.__proto__ on a plain object is %Object.prototype%, which has hasOwnProperty.
        Assert.Equal("function", Run("typeof ({}).__proto__.hasOwnProperty;").AsString());
    }

    [Fact]
    public void ProtoSetterReassignsPrototype()
    {
        Assert.Equal(42, Run("var p = {x:42}; var o = {}; o.__proto__ = p; o.x;").AsNumber());
    }

    [Fact]
    public void ProtoSetterIgnoresNonObjectNonNull()
    {
        // Setting __proto__ to a primitive is a no-op (returns undefined, leaves proto).
        Assert.Equal(7, Run("var p = {y:7}; var o = {}; o.__proto__ = p; o.__proto__ = 5; o.y;").AsNumber());
    }

    [Fact]
    public void DefineGetterInstallsAccessor()
    {
        Assert.Equal(99, Run("var o = {}; o.__defineGetter__('g', function(){ return 99; }); o.g;").AsNumber());
    }

    [Fact]
    public void DefineSetterInstallsAccessor()
    {
        Assert.Equal(5, Run("var o = {}; var seen; o.__defineSetter__('s', function(v){ seen = v; }); o.s = 5; seen;").AsNumber());
    }

    [Fact]
    public void DefineGetterPreservesExistingSetter()
    {
        Assert.Equal(
            3,
            Run("var o = {}; var seen; o.__defineSetter__('p', function(v){ seen = v; });" +
                "o.__defineGetter__('p', function(){ return 1; }); o.p = 3; seen;").AsNumber());
    }

    [Fact]
    public void DefineGetterThrowsOnNonCallable()
    {
        Assert.Throws<JsThrownException>(() => Run("({}).__defineGetter__('g', 5);"));
    }

    [Fact]
    public void LookupGetterFindsOwnAccessor()
    {
        Assert.Equal("function", Run("var o = {}; o.__defineGetter__('g', function(){}); typeof o.__lookupGetter__('g');").AsString());
    }

    [Fact]
    public void LookupGetterReturnsUndefinedForDataProperty()
    {
        Assert.Equal(JsValueTag.Undefined, Run("var o = {x:1}; o.__lookupGetter__('x');").Tag);
    }

    [Fact]
    public void LookupGetterWalksPrototypeChain()
    {
        Assert.Equal(
            "function",
            Run("var p = {}; p.__defineGetter__('g', function(){}); var o = Object.create(p); typeof o.__lookupGetter__('g');").AsString());
    }

    [Fact]
    public void LookupSetterFindsOwnAccessor()
    {
        Assert.Equal("function", Run("var o = {}; o.__defineSetter__('s', function(v){}); typeof o.__lookupSetter__('s');").AsString());
    }

    [Fact]
    public void LegacyMethodsThrowOnNullReceiver()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.prototype.__lookupGetter__.call(null, 'x');"));
        Assert.Throws<JsThrownException>(() => Run("Object.prototype.__defineGetter__.call(undefined, 'x', function(){});"));
    }
}
