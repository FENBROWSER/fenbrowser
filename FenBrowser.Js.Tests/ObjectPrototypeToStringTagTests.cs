using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 20.1.3.6 Object.prototype.toString: builtin-tag determination from
// internal slots plus the @@toStringTag override read via the real [[Get]].
public sealed class ObjectPrototypeToStringTagTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        return result.AsString();
    }

    [Fact]
    public void NullAndUndefinedHaveDedicatedTags()
    {
        Assert.Equal("[object Undefined]", Run("Object.prototype.toString.call(undefined);"));
        Assert.Equal("[object Null]", Run("Object.prototype.toString.call(null);"));
    }

    [Fact]
    public void PrimitivesBoxToTheirBuiltinTag()
    {
        Assert.Equal("[object Boolean]", Run("Object.prototype.toString.call(true);"));
        Assert.Equal("[object Number]", Run("Object.prototype.toString.call(42);"));
        Assert.Equal("[object String]", Run("Object.prototype.toString.call('hi');"));
    }

    [Fact]
    public void BigIntAndSymbolUsePrototypeToStringTag()
    {
        Assert.Equal("[object BigInt]", Run("Object.prototype.toString.call(10n);"));
        Assert.Equal("[object Symbol]", Run("Object.prototype.toString.call(Symbol('x'));"));
    }

    [Fact]
    public void ArrayAndFunctionAndErrorTags()
    {
        Assert.Equal("[object Array]", Run("Object.prototype.toString.call([]);"));
        Assert.Equal("[object Function]", Run("Object.prototype.toString.call(function(){});"));
        Assert.Equal("[object Error]", Run("Object.prototype.toString.call(new TypeError());"));
    }

    [Fact]
    public void ArgumentsObjectHasArgumentsTag()
    {
        Assert.Equal(
            "[object Arguments]",
            Run("function f(){ return Object.prototype.toString.call(arguments); } f(1,2);"));
    }

    [Fact]
    public void StringToStringTagOverridesBuiltinTag()
    {
        Assert.Equal(
            "[object test262]",
            Run("var o = {}; o[Symbol.toStringTag] = 'test262'; Object.prototype.toString.call(o);"));
    }

    [Fact]
    public void NonStringToStringTagIsIgnored()
    {
        Assert.Equal(
            "[object Array]",
            Run("var a = []; Object.defineProperty(a, Symbol.toStringTag, {value: 123}); Object.prototype.toString.call(a);"));
    }

    [Fact]
    public void ToStringTagGetterAbruptCompletionPropagates()
    {
        Assert.Throws<JsThrownException>(() => Run(
            "var o = Object.defineProperty({}, Symbol.toStringTag, {get: function(){ throw new TypeError(); }});" +
            "Object.prototype.toString.call(o);"));
    }

    [Fact]
    public void MapAndSetAndPromiseBuiltinTags()
    {
        Assert.Equal("[object Map]", Run("Object.prototype.toString.call(new Map());"));
        Assert.Equal("[object Set]", Run("Object.prototype.toString.call(new Set());"));
        Assert.Equal("[object Promise]", Run("Object.prototype.toString.call(Promise.resolve());"));
    }

    [Fact]
    public void CallableProxyReportsFunctionTag()
    {
        Assert.Equal(
            "[object Function]",
            Run("Object.prototype.toString.call(new Proxy(function(){}, {}));"));
        Assert.Equal(
            "[object Array]",
            Run("Object.prototype.toString.call(new Proxy([], {}));"));
    }
}
