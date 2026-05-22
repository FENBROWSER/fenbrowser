using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArraySortTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void DefaultComparatorIsLexicographic()
    {
        // 10 < 2 when compared as strings ("10" < "2"), confirming default-string
        // semantics matches the spec.
        Assert.Equal("1,10,2", RunStr("[2,10,1].sort().join(',');"));
    }

    [Fact]
    public void CustomComparator()
    {
        Assert.Equal("1,2,10", RunStr("[2,10,1].sort(function(a,b){return a-b;}).join(',');"));
    }

    [Fact]
    public void DescendingComparator()
    {
        Assert.Equal("10,2,1", RunStr("[2,10,1].sort(function(a,b){return b-a;}).join(',');"));
    }

    [Fact]
    public void SortReturnsReceiver()
    {
        Assert.Equal(1, RunNum("var a = [3,1,2]; (a.sort() === a) ? 1 : 0;"));
    }

    [Fact]
    public void SortIsInPlace()
    {
        Assert.Equal("1,2,3", RunStr("var a = [3,1,2]; a.sort(function(x,y){return x-y;}); a.join(',');"));
    }

    [Fact]
    public void EmptyArraySortsToEmpty()
    {
        Assert.Equal(0, RunNum("[].sort().length;"));
    }

    [Fact]
    public void SingleElementUnchanged()
    {
        Assert.Equal(7, RunNum("[7].sort()[0];"));
    }

    [Fact]
    public void UndefinedElementsMoveToEnd()
    {
        // Sorted as [1, 2, undefined]; join renders undefined to an empty string.
        Assert.Equal("1,2,", RunStr("[undefined, 2, 1].sort(function(a,b){return a-b;}).join(',');"));
        Assert.Equal("undefined", RunStr("var a = [undefined, 2, 1]; a.sort(function(x,y){return x-y;}); typeof a[2];"));
    }

    [Fact]
    public void NonFunctionComparatorThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("[1,2].sort(42);"));
    }
}
