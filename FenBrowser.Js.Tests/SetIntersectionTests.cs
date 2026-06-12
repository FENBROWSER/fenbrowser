using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SetIntersectionTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void KeepsCommonEntries()
    {
        Assert.Equal(1d, Run("new Set([1,2,3]).intersection(new Set([3,4])).size;").AsNumber());
        Assert.True(Run("new Set([1,2,3]).intersection(new Set([3,4])).has(3);").AsBoolean());
    }

    [Fact]
    public void EmptyWhenDisjoint()
    {
        Assert.Equal(0d, Run("new Set([1,2]).intersection(new Set([3,4])).size;").AsNumber());
    }

    [Fact]
    public void RejectsArray()
    {
        // ECMA-262 24.2.1.2 GetSetRecord: an Array is not Set-like, so intersection throws.
        Assert.Throws<JsThrownException>(() => Run("new Set([1,2,3]).intersection([2,3,4,5]);"));
    }

    [Fact]
    public void IsFreshSet()
    {
        Assert.False(Run("var a=new Set([1]); a.intersection(new Set([1])) === a;").AsBoolean());
    }
}
