using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArraySliceConcatTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void SliceCopiesEntireArrayWhenNoArguments()
    {
        Assert.Equal(3, RunNum("var a = [1,2,3]; a.slice().length;"));
        Assert.Equal(1, RunNum("var a = [1,2,3]; a.slice()[0];"));
    }

    [Fact]
    public void SliceWithStart()
    {
        Assert.Equal(2, RunNum("var a = [1,2,3,4]; a.slice(2).length;"));
        Assert.Equal(3, RunNum("var a = [1,2,3,4]; a.slice(2)[0];"));
    }

    [Fact]
    public void SliceWithStartAndEnd()
    {
        Assert.Equal(2, RunNum("[1,2,3,4].slice(1,3).length;"));
        Assert.Equal(2, RunNum("[1,2,3,4].slice(1,3)[0];"));
        Assert.Equal(3, RunNum("[1,2,3,4].slice(1,3)[1];"));
    }

    [Fact]
    public void SliceWithNegativeIndices()
    {
        Assert.Equal(2, RunNum("[1,2,3,4].slice(-2).length;"));
        Assert.Equal(3, RunNum("[1,2,3,4].slice(-2)[0];"));
    }

    [Fact]
    public void SliceReturnsFreshArray()
    {
        Assert.Equal(1, RunNum("var a = [1,2]; (a.slice() === a) ? 0 : 1;"));
    }

    [Fact]
    public void SliceDoesNotMutate()
    {
        Assert.Equal(3, RunNum("var a = [1,2,3]; a.slice(0,1); a.length;"));
    }

    [Fact]
    public void ConcatArraysFlattensOneLevel()
    {
        Assert.Equal(4, RunNum("[1,2].concat([3,4]).length;"));
        Assert.Equal(3, RunNum("[1,2].concat([3,4])[2];"));
    }

    [Fact]
    public void ConcatNonArraysAppendsAsSingleElement()
    {
        Assert.Equal(3, RunNum("[1].concat(2, 3).length;"));
        Assert.Equal(2, RunNum("[1].concat(2, 3)[1];"));
    }

    [Fact]
    public void ConcatMixedSpreadsArraysButKeepsObjects()
    {
        // Array argument is spread; plain object is one element.
        Assert.Equal(3, RunNum("[1].concat([2], {x:3}).length;"));
    }

    [Fact]
    public void ConcatEmptyArrayCopies()
    {
        Assert.Equal(2, RunNum("var a = [1,2]; a.concat().length;"));
    }
}
