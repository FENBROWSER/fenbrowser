using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.1.6.3 ValidateAndApplyPropertyDescriptor: Object.defineProperty must
// reject (TypeError) disallowed changes to non-configurable properties and additions
// to non-extensible objects, while still permitting spec-allowed changes.
public sealed class DefinePropertyInvariantsTests
{
    private static string CtorName(string call)
    {
        var src = "var n = '<ok>'; try { " + call + " } catch (e) { n = e.constructor.name; } n;";
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static string Eval(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void CannotAddToNonExtensible()
        => Assert.Equal("TypeError",
            CtorName("var o = {}; Object.preventExtensions(o); Object.defineProperty(o, 'x', {value: 1});"));

    [Fact]
    public void CannotRedefineNonConfigurableValue()
        => Assert.Equal("TypeError",
            CtorName("var o = {}; Object.defineProperty(o, 'x', {value: 1}); Object.defineProperty(o, 'x', {value: 2});"));

    [Fact]
    public void CannotMakeNonConfigurableConfigurable()
        => Assert.Equal("TypeError",
            CtorName("var o = {}; Object.defineProperty(o, 'x', {value: 1}); Object.defineProperty(o, 'x', {configurable: true});"));

    [Fact]
    public void CannotFlipEnumerableOnNonConfigurable()
        => Assert.Equal("TypeError",
            CtorName("var o = {}; Object.defineProperty(o, 'x', {value: 1, enumerable: false}); Object.defineProperty(o, 'x', {enumerable: true});"));

    [Fact]
    public void CannotChangeDataToAccessorOnNonConfigurable()
        => Assert.Equal("TypeError",
            CtorName("var o = {}; Object.defineProperty(o, 'x', {value: 1}); Object.defineProperty(o, 'x', {get: function(){}});"));

    [Fact]
    public void CannotRedefineFrozenProperty()
        => Assert.Equal("TypeError",
            CtorName("var o = Object.freeze({a: 1}); Object.defineProperty(o, 'a', {value: 9});"));

    [Fact]
    public void ConfigurableRedefinitionSucceeds()
        => Assert.Equal("2",
            Eval("var o = {}; Object.defineProperty(o, 'x', {value: 1, configurable: true}); Object.defineProperty(o, 'x', {value: 2}); String(o.x);"));

    [Fact]
    public void WritableNonConfigurableValueChangeSucceeds()
        // A non-configurable but writable data property may still change value / become non-writable.
        => Assert.Equal("2",
            Eval("var o = {}; Object.defineProperty(o, 'x', {value: 1, writable: true}); Object.defineProperty(o, 'x', {value: 2}); String(o.x);"));

    [Fact]
    public void RedefiningWithSameValueOnNonConfigurableSucceeds()
        => Assert.Equal("<ok>",
            CtorName("var o = {}; Object.defineProperty(o, 'x', {value: 1}); Object.defineProperty(o, 'x', {value: 1});"));
}
