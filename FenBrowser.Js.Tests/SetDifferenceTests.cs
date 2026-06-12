using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SetDifferenceTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DifferenceKeepsOnlyLeftSide()
    {
        Assert.True(Run("var d=new Set([1,2,3]).difference(new Set([2,3,4])); d.size===1 && d.has(1);").AsBoolean());
    }

    [Fact]
    public void DifferenceEmptyWhenSubset()
    {
        Assert.Equal(0d, Run("new Set([1,2]).difference(new Set([1,2,3])).size;").AsNumber());
    }

    [Fact]
    public void SymmetricDifferenceUnionsExclusive()
    {
        Assert.Equal(3d, Run("new Set([1,2,3]).symmetricDifference(new Set([3,4])).size;").AsNumber());
        Assert.True(Run("var d=new Set([1,2,3]).symmetricDifference(new Set([3,4])); d.has(1)&&d.has(4)&&!d.has(3);").AsBoolean());
    }

    [Fact]
    public void DifferenceRejectsArray()
    {
        // ECMA-262 24.2.1.2 GetSetRecord: an Array is not Set-like, so difference throws.
        Assert.Throws<JsThrownException>(() => Run("new Set([1,2]).difference([2,3]);"));
    }
}
