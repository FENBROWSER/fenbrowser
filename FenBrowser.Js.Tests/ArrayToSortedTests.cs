using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayToSortedTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DefaultLexicographicSort()
    {
        Assert.Equal("1,11,2,3", Run("[3,1,11,2].toSorted().join(',');").AsString());
    }

    [Fact]
    public void NumericComparator()
    {
        Assert.Equal("1,2,3,11", Run("[3,1,11,2].toSorted(function(a,b){return a-b;}).join(',');").AsString());
    }

    [Fact]
    public void DoesNotMutateOriginal()
    {
        Assert.Equal("3,1,2", Run("var a=[3,1,2]; a.toSorted(); a.join(',');").AsString());
    }

    [Fact]
    public void UndefinedSortsLast()
    {
        // Array.prototype.join stringifies undefined as empty per ECMA-262 23.1.3.18.
        Assert.Equal("1,2,,", Run("[undefined,2,undefined,1].toSorted(function(a,b){return a-b;}).join(',');").AsString());
        Assert.Equal(4d, Run("[undefined,2,undefined,1].toSorted(function(a,b){return a-b;}).length;").AsNumber());
    }

    [Fact]
    public void ThrowsOnNonFunctionComparator()
    {
        Assert.Throws<JsThrownException>(() => Run("[1,2].toSorted(5);"));
    }
}
