using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AsyncFunctionRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static JsValue RunThenRead(string side, string read)
    {
        var interpreter = new BytecodeInterpreter();

        var sideFn = new BytecodeCompiler().CompileScript(new SourceText(side));
        new BytecodeVerifier().Verify(sideFn);
        _ = interpreter.Execute(sideFn);

        var readFn = new BytecodeCompiler().CompileScript(new SourceText(read));
        new BytecodeVerifier().Verify(readFn);
        return interpreter.Execute(readFn);
    }

    [Fact]
    public void AsyncFunctionReturnsPromiseObject()
    {
        Assert.Equal("object", Run("async function f(){ return 1; } typeof f();").AsString());
    }

    [Fact]
    public void AsyncFunctionResolvesReturnValue()
    {
        Assert.Equal(7d, RunThenRead(
            "var observed; async function f(){ return 7; } f().then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void AsyncFunctionThrowBecomesRejectedPromise()
    {
        Assert.Equal("boom", RunThenRead(
            "var observed; async function f(){ throw 'boom'; } f().catch(function(e){ observed = e; });",
            "observed;").AsString());
    }

    [Fact]
    public void AwaitPrimitiveResolvesInsideAsyncFunction()
    {
        Assert.Equal(3d, RunThenRead(
            "var observed; async function f(){ return await 3; } f().then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void AwaitPromiseResolvePathWorks()
    {
        Assert.Equal(9d, RunThenRead(
            "var observed; async function f(){ return await Promise.resolve(9); } f().then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void AwaitRejectedPromiseRoutesToCatch()
    {
        Assert.Equal("x", RunThenRead(
            "var observed; async function f(){ await Promise.reject('x'); return 1; } f().catch(function(e){ observed = e; });",
            "observed;").AsString());
    }

    [Fact]
    public void AwaitOutsideAsyncIsRejectedAtCompileTime()
    {
        var compiler = new BytecodeCompiler();
        var ex = Assert.Throws<UnsupportedFeatureException>(() =>
            compiler.CompileScript(new SourceText("await 1;")));

        Assert.Equal("await-outside-async", ex.FeatureName);
        Assert.Equal(FeatureSupportLevel.ParserOnly, ex.Level);
    }
}
