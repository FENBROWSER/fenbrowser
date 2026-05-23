using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class WeakRefTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DerefReturnsTarget()
    {
        Assert.Equal(7d, Run("var t={x:7}; new WeakRef(t).deref().x;").AsNumber());
    }

    [Fact]
    public void DerefSameIdentity()
    {
        Assert.True(Run("var t={}; var r=new WeakRef(t); r.deref() === t;").AsBoolean());
    }

    [Fact]
    public void ConstructorRequiresObjectTarget()
    {
        Assert.Throws<JsThrownException>(() => Run("new WeakRef(5);"));
        Assert.Throws<JsThrownException>(() => Run("new WeakRef();"));
    }

    [Fact]
    public void ConstructorRequiresNew()
    {
        Assert.Throws<JsThrownException>(() => Run("WeakRef({});"));
    }

    [Fact]
    public void DerefOnNonWeakRefThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("WeakRef.prototype.deref.call({});"));
    }
}
