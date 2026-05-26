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

    // === Basic async function call ===

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

    // === await with fulfilled promises (fast path — no suspension) ===

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

    // === await with suspension (pending promises) ===

    [Fact]
    public void AwaitPendingPromiseResumesAfterMicrotask()
    {
        // queueMicrotask inside a promise executor creates a pending promise
        // that settles during the microtask checkpoint.
        Assert.Equal(88d, RunThenRead(
            "var observed; async function f(){ return await new Promise(function(resolve){ queueMicrotask(function(){ resolve(88); }); }); } f().then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    // === compile-time error ===

    [Fact]
    public void AwaitOutsideAsyncIsRejectedAtCompileTime()
    {
        var compiler = new BytecodeCompiler();
        var ex = Assert.Throws<UnsupportedFeatureException>(() =>
            compiler.CompileScript(new SourceText("await 1;")));

        Assert.Equal("await-outside-async", ex.FeatureName);
        Assert.Equal(FeatureSupportLevel.ParserOnly, ex.Level);
    }

    // === async method tests (class + object literal) ===

    [Fact]
    public void AsyncMethod_ReturnsPromise()
    {
        Assert.True(Run("class C{async foo(){return 42;}}var c=new C();typeof c.foo().then==='function';").AsBoolean());
    }

    [Fact]
    public void AsyncMethod_AwaitInside()
    {
        Assert.True(Run("class C{async foo(){var x=await 7;return x;}}var c=new C();typeof c.foo().then==='function';").AsBoolean());
    }

    [Fact]
    public void AsyncObjectLiteralMethod_ReturnsPromise()
    {
        Assert.True(Run("var obj={async fetch(){return 1;}};typeof obj.fetch().then==='function';").AsBoolean());
    }

    // === code before/after await executes correctly ===

    [Fact]
    public void CodeBeforeAwaitExecutes()
    {
        Assert.Equal(99d, Run("var side = 0; async function f() { side = 99; return await 42; } f(); side;").AsNumber());
    }

    [Fact]
    public void CodeAfterAwaitExecutes()
    {
        Assert.Equal(42d, Run("var side = 0; async function f() { var x = await 42; side = x; } f(); side;").AsNumber());
    }

    // === await inside try/catch ===

    [Fact]
    public void AwaitInsideTryCatchBody()
    {
        var result = Run(@"
            var captured = 'none';
            async function f() {
                try {
                    var x = await 42;
                    captured = 'ok-' + (typeof x);
                } catch (e) {
                    captured = 'err';
                }
            }
            f();
            captured;
        ");
        Assert.Equal("ok-number", result.AsString());
    }
}
