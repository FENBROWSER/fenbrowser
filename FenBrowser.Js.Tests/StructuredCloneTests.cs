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
    [Fact]
    public void ArrayBufferBytesAreCopied()
    {
        Assert.Equal("7,4,false", Run(
            "var b=new ArrayBuffer(4); new Uint8Array(b)[0]=7; var c=structuredClone(b);" +
            "new Uint8Array(b)[0]=9; [new Uint8Array(c)[0], c.byteLength, c===b].join();").AsString());
    }

    [Fact]
    public void TransferMovesTheBytesAndDetachesTheOriginal()
    {
        Assert.Equal("0,true,5,4", Run(
            "var b=new ArrayBuffer(4); new Uint8Array(b)[0]=5;" +
            "var c=structuredClone({x:b}, {transfer:[b]}).x;" +
            "[b.byteLength, b.detached, new Uint8Array(c)[0], c.byteLength].join();").AsString());
    }

    [Fact]
    public void TransferringADetachedOrRepeatedBufferThrowsDataCloneError()
    {
        Assert.Equal("DataCloneError,DataCloneError,4", Run(
            "var r=[]; var b=new ArrayBuffer(4); var d=new ArrayBuffer(1); structuredClone(0,{transfer:[d]});" +
            "try{structuredClone(0,{transfer:[d]})}catch(e){r.push(e.name)}" +
            "try{structuredClone(0,{transfer:[b,b]})}catch(e){r.push(e.name)}" +
            "r.push(b.byteLength); r.join();").AsString());
    }
}
