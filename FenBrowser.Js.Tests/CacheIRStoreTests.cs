using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Behaviour of the property-store cache. A write that takes a shortcut is
/// observable in more ways than a read, so these drive the cache into every
/// state where a shortcut would be wrong.
/// </summary>
public sealed class CacheIRStoreTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    private static string RunString(string source) => Run(source).AsString();

    private static double RunNumber(string source) => Run(source).AsNumber();

    [Fact]
    public void WritesAnOwnDataPropertyRepeatedly()
    {
        Assert.Equal(
            "4",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "write(a, 1); write(a, 2); write(a, 3); write(a, 4);" +
                "String(a.x);"));
    }

    [Fact]
    public void StopsWritingThroughTheSlotOnceASetterIsInstalled()
    {
        Assert.Equal(
            "2",
            RunNumber(
                "function write(o, v) { o.x = v; }" +
                "var seen = 0;" +
                "var a = { x: 0 };" +
                "write(a, 1); write(a, 1);" +
                "Object.defineProperty(a, 'x', { set: function (v) { seen++; } });" +
                "write(a, 2); write(a, 3);" +
                "seen;").ToString());
    }

    [Fact]
    public void RefusesAfterThePropertyBecomesNonWritable()
    {
        Assert.Equal(
            "1",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "write(a, 1); write(a, 1);" +
                "Object.defineProperty(a, 'x', { writable: false });" +
                "write(a, 99);" +
                "String(a.x);"));
    }

    [Fact]
    public void ThrowsOnANonWritablePropertyInStrictMode()
    {
        Assert.Equal(
            "threw",
            RunString(
                "'use strict';" +
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "write(a, 1); write(a, 1);" +
                "Object.defineProperty(a, 'x', { writable: false });" +
                "var r = 'no-throw';" +
                "try { write(a, 2); } catch (e) { r = 'threw'; }" +
                "r;"));
    }

    [Fact]
    public void RoutesEveryWriteToAProxyThroughItsTrap()
    {
        Assert.Equal(
            5d,
            RunNumber(
                "function write(o, v) { o.x = v; }" +
                "var hits = 0;" +
                "var p = new Proxy({ x: 0 }, { set: function (t, k, v) { hits++; t[k] = v; return true; } });" +
                "write(p, 1); write(p, 2); write(p, 3); write(p, 4); write(p, 5);" +
                "hits;"));
    }

    [Fact]
    public void NeverShortCircuitsAnArrayLength()
    {
        // Writing length on an array may delete elements, so it can never be a
        // plain slot store however warm the site is.
        Assert.Equal(
            "2|0",
            RunString(
                "function setLength(o, v) { o.length = v; }" +
                "var plain = { length: 0 };" +
                "setLength(plain, 1); setLength(plain, 2);" +
                "var arr = [1, 2, 3, 4, 5];" +
                "setLength(arr, 0);" +
                "String(plain.length) + '|' + String(arr.length);"));
    }

    [Fact]
    public void KeepsShapesApartAcrossReceivers()
    {
        Assert.Equal(
            "9|8",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "var b = { p: 0, x: 0 };" +
                "write(a, 9); write(b, 8); write(a, 9); write(b, 8);" +
                "String(a.x) + '|' + String(b.x);"));
    }

    [Fact]
    public void CreatesRatherThanCachesAWriteToAMissingProperty()
    {
        Assert.Equal(
            "undefined|7",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = {};" +
                "var before = String(a.x);" +
                "write(a, 7);" +
                "before + '|' + String(a.x);"));
    }

    [Fact]
    public void WritesReachTheSetterOnThePrototype()
    {
        Assert.Equal(
            3d,
            RunNumber(
                "var taken = 0;" +
                "var proto = {};" +
                "Object.defineProperty(proto, 'x', { set: function (v) { taken++; }, configurable: true });" +
                "function write(o, v) { o.x = v; }" +
                "var a = Object.create(proto);" +
                "write(a, 1); write(a, 2); write(a, 3);" +
                "taken;"));
    }

    [Fact]
    public void MarksAPrototypeAssignmentOnAFunctionInstance()
    {
        // The compiled store path used to skip this bookkeeping entirely.
        Assert.Equal(
            "true",
            RunString(
                "function Widget() {}" +
                "function write(f, v) { f.prototype = v; }" +
                "var proto = { tag: 'custom' };" +
                "for (var i = 0; i < 900; i++) write(Widget, proto);" +
                "var w = new Widget();" +
                "String(Object.getPrototypeOf(w) === proto && w.tag === 'custom');"));
    }

    [Fact]
    public void CompiledStoreSiteStaysCorrectAcrossShapes()
    {
        Assert.Equal(
            "ok",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "var b = { p: 0, x: 0 };" +
                "var c = { q: 0, r: 0, x: 0 };" +
                "var os = [a, b, c];" +
                "for (var i = 0; i < 900; i++) {" +
                "  var o = os[i % 3];" +
                "  write(o, i);" +
                "  if (o.x !== i) { throw new Error('lost write at ' + i); }" +
                "}" +
                "'ok';"));
    }

    [Fact]
    public void CompiledStoreSiteHonoursALateSetter()
    {
        Assert.Equal(
            "300",
            RunString(
                "function write(o, v) { o.x = v; }" +
                "var a = { x: 0 };" +
                "for (var i = 0; i < 900; i++) write(a, i);" +
                "var seen = 0;" +
                "Object.defineProperty(a, 'x', { set: function (v) { seen++; } });" +
                "for (var i = 0; i < 300; i++) write(a, i);" +
                "String(seen);"));
    }

    [Fact]
    public void CompiledStoreSiteStillRoutesProxiesThroughTheirTrap()
    {
        Assert.Equal(
            300d,
            RunNumber(
                "function write(o, v) { o.x = v; }" +
                "var plain = { x: 0 };" +
                "for (var i = 0; i < 900; i++) write(plain, i);" +
                "var hits = 0;" +
                "var p = new Proxy({ x: 0 }, { set: function (t, k, v) { hits++; t[k] = v; return true; } });" +
                "for (var i = 0; i < 300; i++) write(p, i);" +
                "hits;"));
    }
}
