using FenBrowser.Js.Bytecode;
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
    public void NonCallableThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask(5);"));
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask();"));
        Assert.Throws<JsThrownException>(() => Run("queueMicrotask({});"));
    }
}
