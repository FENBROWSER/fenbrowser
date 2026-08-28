using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringRawTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void InterleavesRawAndSubstitutions()
    {
        Assert.Equal("a1b2c", Run("String.raw({raw:['a','b','c']}, 1, 2);").AsString());
    }

    [Fact]
    public void SubstitutionsCoerced()
    {
        Assert.Equal("xtruey", Run("String.raw({raw:['x','y']}, true);").AsString());
    }

    [Fact]
    public void FewerSubstitutionsThanGapsLeavesGapsEmpty()
    {
        Assert.Equal("ab", Run("String.raw({raw:['a','b']});").AsString());
    }

    [Fact]
    public void EmptyRawReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Run("String.raw({raw:[]});").AsString());
    }

    [Fact]
    public void MissingTemplateThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("String.raw();"));
    }

    // ToObject(template.raw) boxes every non-null/undefined primitive; a Number
    // wrapper has no length, so the result is the empty string.
    [Fact]
    public void PrimitiveNumberRawReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Run("String.raw({raw: 5});").AsString());
    }

    [Fact]
    public void PrimitiveStringRawYieldsItsCharacters()
    {
        Assert.Equal("ab", Run("String.raw({raw: 'ab'});").AsString());
    }

    [Fact]
    public void PrimitiveBooleanRawReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Run("String.raw({raw: true});").AsString());
    }

    [Fact]
    public void PrimitiveTemplateObservesWrapperPrototypeRawGetter()
    {
        Assert.Equal("n", Run(
            "Object.defineProperty(Number.prototype, 'raw', { get: function () { return { 0: 'n', length: 1 }; }, configurable: true });" +
            "String.raw(5);").AsString());
    }

    [Fact]
    public void PrimitiveRawObservesWrapperPrototypeLengthGetter()
    {
        // The Number wrapper's length resolves through Number.prototype, so the
        // getter runs once and supplies the literal-segment count.
        Assert.Equal("true:undefined", Run(
            "var calls = 0;" +
            "Object.defineProperty(Number.prototype, 'length', { get: function () { calls++; return 1; }, configurable: true });" +
            "var out = String.raw({raw: 7});" +
            "String(calls === 1) + ':' + out;").AsString());
    }

    [Fact]
    public void NullRawThrowsTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw({raw: null}); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }

    [Fact]
    public void UndefinedRawThrowsTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw({raw: undefined}); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }

    // JSLIB-003: a primitive template must flow through ToObject/Get and surface
    // as a catchable JS TypeError — never a CLR InvalidOperationException from
    // AsObjectHandle().
    [Fact]
    public void PrimitiveTemplateViaCallThrowsCatchableTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw.call({}, 'abc'); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }

    [Fact]
    public void PrimitiveTemplateDirectCallThrowsTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw('abc'); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }

    [Fact]
    public void NullTemplateThrowsTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw(null); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }

    [Fact]
    public void AccessorRawIsObserved()
    {
        Assert.Equal("x", Run("String.raw({ get raw() { return { 0: 'x', length: 1 }; } });").AsString());
    }

    [Fact]
    public void ProxyRawIsObserved()
    {
        Assert.Equal("p", Run("var t = new Proxy({}, { get: function (o, k) { return k === 'raw' ? { 0: 'p', length: 1 } : undefined; } }); String.raw(t);").AsString());
    }

    [Fact]
    public void ArrayLikeRawObjectInterleavesSubstitutions()
    {
        Assert.Equal("axb", Run("String.raw({ raw: { 0: 'a', 1: 'b', length: 2 } }, 'x');").AsString());
    }

    [Fact]
    public void ThrowingRawGetterPropagatesUserError()
    {
        Assert.True(Run("var o = {}; Object.defineProperty(o, 'raw', { get: function () { throw new RangeError('gone'); } }); var marker = false; try { String.raw(o); } catch (e) { marker = e instanceof RangeError; } marker;").AsBoolean());
    }

    [Fact]
    public void SymbolSegmentThrowsTypeError()
    {
        Assert.True(Run("var threw = false; try { String.raw({ raw: { length: 1, 0: Symbol('s') } }); } catch (e) { threw = e instanceof TypeError; } threw;").AsBoolean());
    }
}
