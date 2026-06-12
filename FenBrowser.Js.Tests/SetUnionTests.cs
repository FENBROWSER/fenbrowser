using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SetUnionTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void UnionMergesEntries()
    {
        Assert.Equal(4d, Run("new Set([1,2,3]).union(new Set([3,4])).size;").AsNumber());
    }

    [Fact]
    public void UnionRejectsArray()
    {
        // ECMA-262 24.2.1.2 GetSetRecord: a plain Array is not Set-like (its `size`
        // is undefined → NaN, no callable `has`/`keys`), so union must throw.
        Assert.Throws<JsThrownException>(() => Run("new Set([1,2]).union([2,3]);"));
    }

    [Fact]
    public void UnionIsFreshSet()
    {
        Assert.False(Run("var a=new Set([1]); a.union(new Set([2])) === a;").AsBoolean());
    }

    [Fact]
    public void UnionPreservesOriginal()
    {
        Assert.Equal(1d, Run("var a=new Set([1]); a.union(new Set([2])); a.size;").AsNumber());
    }

    [Fact]
    public void UnionNonIterableThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("new Set([1]).union(undefined);"));
    }
}
