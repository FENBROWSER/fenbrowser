using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PromiseRuntimeTests
{
    private static (JsValue Result, BytecodeInterpreter Interpreter) Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        var interpreter = new BytecodeInterpreter();
        return (interpreter.Execute(fn), interpreter);
    }

    // The script's final-expression value is evaluated before the microtask
    // drain, so tests that observe a Promise side effect must run a follow-up
    // script (in the same interpreter) to read the post-drain global state.
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
    public void PromiseGlobalIsCallable()
    {
        Assert.Equal("function", Run("typeof Promise;").Result.AsString());
    }

    [Fact]
    public void ConstructorRequiresExecutor()
    {
        Assert.Throws<JsThrownException>(() => Run("new Promise();"));
    }

    [Fact]
    public void ConstructorRequiresCallableExecutor()
    {
        Assert.Throws<JsThrownException>(() => Run("new Promise(5);"));
    }

    [Fact]
    public void ResolveStaticFulfillsImmediately()
    {
        Assert.Equal(42d, RunThenRead(
            "var observed; Promise.resolve(42).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void RejectStaticRoutesToCatch()
    {
        Assert.Equal("boom", RunThenRead(
            "var observed; Promise.reject('boom').catch(function(r){ observed = r; });",
            "observed;").AsString());
    }

    [Fact]
    public void ConstructorResolveSurfacesValueOnMicrotask()
    {
        Assert.Equal(7d, RunThenRead(
            "var observed = 0; new Promise(function(resolve){ resolve(7); }).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void HandlerReturnValueChainsToNextThen()
    {
        Assert.Equal(11d, RunThenRead(
            "var observed; Promise.resolve(5).then(function(v){ return v + 6; }).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void ThrowInHandlerRoutesToCatch()
    {
        Assert.Equal("nope", RunThenRead(
            "var observed; Promise.resolve(1).then(function(){ throw 'nope'; }).catch(function(r){ observed = r; });",
            "observed;").AsString());
    }

    [Fact]
    public void FinallyRunsAndPropagatesValue()
    {
        Assert.Equal(9d, RunThenRead(
            "var observed; Promise.resolve(9).finally(function(){}).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void FinallyRunsAndPropagatesRejection()
    {
        Assert.Equal("err", RunThenRead(
            "var observed; Promise.reject('err').finally(function(){}).catch(function(r){ observed = r; });",
            "observed;").AsString());
    }

    [Fact]
    public void ResolvingWithThenableChainsThrough()
    {
        Assert.Equal(99d, RunThenRead(
            "var observed; var t = { then: function(r){ r(99); } }; Promise.resolve(t).then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void SelfResolutionBecomesTypeError()
    {
        Assert.Equal("TypeError", RunThenRead(
            "var observed; var resolver; var q = new Promise(function(r){ resolver = r; }); resolver(q); q.catch(function(e){ observed = e.name; });",
            "observed;").AsString());
    }

    [Fact]
    public void MicrotaskCheckpointDrainsChainedReactions()
    {
        Assert.Equal(3d, RunThenRead(
            "var c = 0; Promise.resolve().then(function(){ c++; }).then(function(){ c++; }).then(function(){ c++; });",
            "c;").AsNumber());
    }

    [Fact]
    public void JobQueueEmptiesAfterTopLevelExecute()
    {
        var (_, interpreter) = Run("Promise.resolve(1).then(function(){}).then(function(){});");
        Assert.Equal(0, interpreter.PromiseJobQueueDepthForTest);
    }
}
