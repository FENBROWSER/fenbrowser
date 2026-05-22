using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class WeakCollectionsTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    // WeakMap
    [Fact]
    public void WeakMapSetGet()
    {
        Assert.Equal(7, RunNum("var k = {}; var m = new WeakMap(); m.set(k, 7); m.get(k);"));
    }

    [Fact]
    public void WeakMapHas()
    {
        Assert.True(RunBool("var k = {}; var m = new WeakMap(); m.set(k, 1); m.has(k);"));
    }

    [Fact]
    public void WeakMapDelete()
    {
        Assert.True(RunBool("var k = {}; var m = new WeakMap(); m.set(k, 1); m.delete(k);"));
    }

    [Fact]
    public void WeakMapNonObjectKeyThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("new WeakMap().set('s', 1);"));
        Assert.Throws<JsThrownException>(() => RunNum("new WeakMap().set(42, 1);"));
    }

    [Fact]
    public void WeakMapGetMissingReturnsUndefined()
    {
        Assert.True(RunBool("new WeakMap().get({}) === undefined;"));
    }

    [Fact]
    public void WeakMapWithoutNewThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("WeakMap();"));
    }

    [Fact]
    public void WeakMapConstructorAcceptsEntries()
    {
        Assert.Equal(1, RunNum("var k = {}; var m = new WeakMap([[k, 1]]); m.get(k);"));
    }

    // WeakSet
    [Fact]
    public void WeakSetAddHas()
    {
        Assert.True(RunBool("var k = {}; var s = new WeakSet(); s.add(k); s.has(k);"));
    }

    [Fact]
    public void WeakSetDelete()
    {
        Assert.True(RunBool("var k = {}; var s = new WeakSet(); s.add(k); s.delete(k);"));
    }

    [Fact]
    public void WeakSetNonObjectThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("new WeakSet().add('x');"));
    }

    [Fact]
    public void WeakSetWithoutNewThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("WeakSet();"));
    }

    [Fact]
    public void WeakSetAddChains()
    {
        Assert.True(RunBool("var s = new WeakSet(); var a = {}, b = {}; s.add(a).add(b); s.has(b);"));
    }

    [Fact]
    public void WeakSetConstructorAcceptsArray()
    {
        Assert.True(RunBool("var a = {}, b = {}; var s = new WeakSet([a, b]); s.has(a);"));
    }
}
