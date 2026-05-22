using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SetTests
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

    [Fact]
    public void NewSetIsEmpty() => Assert.Equal(0, RunNum("new Set().size;"));

    [Fact]
    public void AddIncreasesSize()
    {
        Assert.Equal(3, RunNum("var s = new Set(); s.add(1); s.add(2); s.add(3); s.size;"));
    }

    [Fact]
    public void AddDuplicateNoOpsBySameValueZero()
    {
        Assert.Equal(1, RunNum("var s = new Set(); s.add(1); s.add(1); s.size;"));
        Assert.Equal(1, RunNum("var s = new Set(); s.add(0); s.add(-0); s.size;"));
        Assert.Equal(1, RunNum("var s = new Set(); s.add(NaN); s.add(NaN); s.size;"));
    }

    [Fact]
    public void AddReturnsTheSetForChaining()
    {
        Assert.Equal(2, RunNum("new Set().add(1).add(2).size;"));
    }

    [Fact]
    public void HasReturnsTrueForPresent() => Assert.True(RunBool("new Set().add(7).has(7);"));

    [Fact]
    public void HasReturnsFalseForAbsent() => Assert.False(RunBool("new Set().has(7);"));

    [Fact]
    public void DeleteRemovesAndReturnsTrue()
    {
        Assert.True(RunBool("var s = new Set(); s.add(1); s.delete(1);"));
        Assert.Equal(0, RunNum("var s = new Set(); s.add(1); s.delete(1); s.size;"));
    }

    [Fact]
    public void DeleteOfAbsentReturnsFalse() => Assert.False(RunBool("new Set().delete(99);"));

    [Fact]
    public void ClearEmptiesTheSet()
    {
        Assert.Equal(0, RunNum("var s = new Set(); s.add(1); s.add(2); s.clear(); s.size;"));
    }

    [Fact]
    public void ConstructorAcceptsInitialIterable()
    {
        Assert.Equal(3, RunNum("new Set([1,2,3]).size;"));
        Assert.True(RunBool("new Set([1,2,3]).has(2);"));
    }

    [Fact]
    public void ConstructorDeduplicates()
    {
        Assert.Equal(2, RunNum("new Set([1,1,2,2]).size;"));
    }

    [Fact]
    public void ForEachInvokesCallbackPerEntry()
    {
        Assert.Equal(6, RunNum("var s = new Set([1,2,3]); var t = 0; s.forEach(function(v){ t = t + v; }); t;"));
    }

    [Fact]
    public void SetCalledWithoutNewThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Set();"));
    }
}
