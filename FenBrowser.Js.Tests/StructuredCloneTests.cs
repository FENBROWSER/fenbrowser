using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StructuredCloneTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void PrimitivesUnchanged()
    {
        Assert.Equal(5d, Run("structuredClone(5);").AsNumber());
        Assert.Equal("hi", Run("structuredClone('hi');").AsString());
    }

    [Fact]
    public void ObjectGetsFreshIdentity()
    {
        Assert.False(Run("var o={a:1}; structuredClone(o) === o;").AsBoolean());
        Assert.Equal(1d, Run("structuredClone({a:1}).a;").AsNumber());
    }

    [Fact]
    public void NestedObjectsDeepCloned()
    {
        Assert.False(Run("var o={inner:{x:1}}; structuredClone(o).inner === o.inner;").AsBoolean());
        Assert.Equal(1d, Run("structuredClone({inner:{x:1}}).inner.x;").AsNumber());
    }

    [Fact]
    public void ArraysCloned()
    {
        Assert.Equal("1,2,3", Run("structuredClone([1,2,3]).join(',');").AsString());
        Assert.Equal(3d, Run("structuredClone([1,2,3]).length;").AsNumber());
    }

    [Fact]
    public void DateClonedPreservesTime()
    {
        Assert.Equal(123d, Run("structuredClone(new Date(123)).getTime();").AsNumber());
    }

    [Fact]
    public void CycleHandled()
    {
        // Cycles produce shared references in the clone, not infinite recursion.
        Assert.True(Run("var a={}; a.self=a; var c=structuredClone(a); c.self === c;").AsBoolean());
    }

    [Fact]
    public void FunctionThrowsDataCloneError()
    {
        Assert.Throws<JsThrownException>(() => Run("structuredClone(function(){});"));
    }
}
