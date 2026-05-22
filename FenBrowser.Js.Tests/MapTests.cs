using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MapTests
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

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact] public void NewMapIsEmpty() => Assert.Equal(0, RunNum("new Map().size;"));

    [Fact]
    public void SetGet()
    {
        Assert.Equal(7, RunNum("var m = new Map(); m.set('k', 7); m.get('k');"));
    }

    [Fact]
    public void SetReturnsTheMapForChaining()
    {
        Assert.Equal(2, RunNum("new Map().set('a',1).set('b',2).size;"));
    }

    [Fact]
    public void HasReflectsInsertions()
    {
        Assert.True(RunBool("new Map().set('a', 1).has('a');"));
        Assert.False(RunBool("new Map().has('a');"));
    }

    [Fact]
    public void DeleteRemovesAndReturnsTrue()
    {
        Assert.True(RunBool("var m = new Map().set('a',1); m.delete('a');"));
        Assert.Equal(0, RunNum("var m = new Map().set('a',1); m.delete('a'); m.size;"));
    }

    [Fact]
    public void DeleteOfAbsentReturnsFalse() => Assert.False(RunBool("new Map().delete('x');"));

    [Fact]
    public void ClearEmpties()
    {
        Assert.Equal(0, RunNum("var m = new Map().set('a',1).set('b',2); m.clear(); m.size;"));
    }

    [Fact]
    public void SameValueZeroKeysCollapseNaN()
    {
        Assert.Equal(1, RunNum("var m = new Map(); m.set(NaN, 1); m.set(NaN, 2); m.size;"));
        Assert.Equal(2, RunNum("var m = new Map(); m.set(NaN, 1); m.set(NaN, 2); m.get(NaN);"));
    }

    [Fact]
    public void SameValueZeroKeysCollapsePlusMinusZero()
    {
        Assert.Equal(1, RunNum("var m = new Map(); m.set(0, 1); m.set(-0, 2); m.size;"));
    }

    [Fact]
    public void ObjectKeyIdentityNotEquality()
    {
        // Two distinct objects with the same shape are distinct keys.
        Assert.Equal(2, RunNum("var m = new Map(); m.set({a:1}, 1); m.set({a:1}, 2); m.size;"));
    }

    [Fact]
    public void ConstructorAcceptsArrayOfEntries()
    {
        Assert.Equal(2, RunNum("new Map([['a',1],['b',2]]).size;"));
        Assert.Equal(1, RunNum("new Map([['a',1],['b',2]]).get('a');"));
    }

    [Fact]
    public void ForEachReceivesValueKeyMap()
    {
        Assert.Equal("a1b2", RunStr("var r = ''; new Map([['a',1],['b',2]]).forEach(function(v,k){ r = r + k + v; }); r;"));
    }

    [Fact]
    public void GetOfMissingReturnsUndefined()
    {
        Assert.True(RunBool("new Map().get('missing') === undefined;"));
    }

    [Fact]
    public void MapCalledWithoutNewThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Map();"));
    }
}
