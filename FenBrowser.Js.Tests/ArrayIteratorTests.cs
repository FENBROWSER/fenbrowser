using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayIteratorTests
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
    public void ValuesIteratorYieldsValues()
    {
        Assert.Equal(1, RunNum("var it = [1,2,3].values(); it.next().value;"));
        Assert.Equal(2, RunNum("var it = [1,2,3].values(); it.next(); it.next().value;"));
    }

    [Fact]
    public void ValuesIteratorDoneAfterExhaust()
    {
        Assert.True(RunBool("var it = [1].values(); it.next(); it.next().done;"));
    }

    [Fact]
    public void KeysIteratorYieldsIndices()
    {
        Assert.Equal(0, RunNum("[1,2,3].keys().next().value;"));
        Assert.Equal(1, RunNum("var it = [1,2,3].keys(); it.next(); it.next().value;"));
    }

    [Fact]
    public void EntriesIteratorYieldsPairs()
    {
        Assert.Equal(0, RunNum("[10,20].entries().next().value[0];"));
        Assert.Equal(10, RunNum("[10,20].entries().next().value[1];"));
    }

    [Fact]
    public void IteratorDoneIsFalseBeforeExhaust()
    {
        Assert.False(RunBool("[1].values().next().done;"));
    }

    [Fact]
    public void EmptyArrayIteratorIsDoneImmediately()
    {
        Assert.True(RunBool("[].values().next().done;"));
        Assert.True(RunBool("[].keys().next().done;"));
        Assert.True(RunBool("[].entries().next().done;"));
    }

    [Fact]
    public void IteratorObservesLengthChangesDuringIteration()
    {
        // Spec mandates the iterator re-reads length on each call.
        Assert.Equal(1, RunNum("var a = [1]; var it = a.values(); var first = it.next().value; a.push(2); it.next().value === 2 ? first : 0;"));
    }
}
