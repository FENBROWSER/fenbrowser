using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class QueueMicrotaskTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void GlobalIsCallable()
    {
        Assert.Equal("function", Run("typeof queueMicrotask;").AsString());
    }

    [Fact]
    public void CallableArgumentAccepted()
    {
        // Returns undefined per HTML "queue a microtask" step 5.
        Assert.Equal(JsValueTag.Undefined, Run("queueMicrotask(function(){});").Tag);
    }

    [Fact]
    public void CallbackDoesNotRunBeforeSyncCode()
    {
        // The callback is deferred until the microtask checkpoint at the end of
        // the script - so x is still 0 right after the queueMicrotask call.
        Assert.Equal(0d, Run("var x=0; queueMicrotask(function(){x=7;}); x;").AsNumber());
    }

    [Fact]
    public void CallbacksShareOneFifoQueueWithPromiseJobs()
    {
        // HTML 8.1.7.3: queueMicrotask and promise reactions append to the same
        // microtask queue, so they run in the order they were queued.
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(new BytecodeCompiler().CompileScript(new SourceText(
            "var log = []; var p = Promise.resolve();" +
            "p.then(function(){ log.push('J1'); });" +
            "queueMicrotask(function(){ log.push('M1'); });" +
            "p.then(function(){ log.push('J2'); queueMicrotask(function(){ log.push('M2'); }); });" +
            "queueMicrotask(function(){ log.push('M3'); });")));

        Assert.Equal("J1,M1,J2,M3,M2", interpreter.Execute(
            new BytecodeCompiler().CompileScript(new SourceText("log.join();"))).AsString());
    }

    [Fact]
    public void NonCallableThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask(5);"));
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask();"));
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask({});"));
    }

    [Fact]
    public void PumpObserverReceivesCallbackAndRethrowsOriginalException()
    {
        var interpreter = new BytecodeInterpreter();
        var expectedException = new InvalidOperationException("microtask-observer-fixture");
        var callback = interpreter.AllocateNativeFunction(
            "observedMicrotask",
            (_, _) => throw expectedException);
        ((IBuiltinContext)interpreter).EnqueueMicrotask(callback);

        var observedCallback = JsValue.Undefined;
        Exception? observedException = null;
        var actualException = Assert.Throws<InvalidOperationException>(() =>
            interpreter.PumpMicrotasks((currentCallback, exception) =>
            {
                observedCallback = currentCallback;
                observedException = exception;
            }));

        Assert.Equal(callback, observedCallback);
        Assert.Same(expectedException, observedException);
        Assert.Same(expectedException, actualException);
    }
}
