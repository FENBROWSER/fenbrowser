using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayToReversedTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ReturnsFreshReversedArray()
    {
        Assert.Equal("3,2,1", Run("[1,2,3].toReversed().join(',');").AsString());
    }

    [Fact]
    public void DoesNotMutateOriginal()
    {
        Assert.Equal("1,2,3", Run("var a=[1,2,3]; a.toReversed(); a.join(',');").AsString());
    }

    [Fact]
    public void EmptyArray()
    {
        Assert.Equal(0d, Run("[].toReversed().length;").AsNumber());
    }

    [Fact]
    public void HolesBecomeUndefined()
    {
        // Spec reads via [[Get]] so a hole at index N reads the prototype chain;
        // here it surfaces as undefined and stays present in the result.
        Assert.Equal(3d, Run("var a=[1,,3]; a.toReversed().length;").AsNumber());
    }
}
