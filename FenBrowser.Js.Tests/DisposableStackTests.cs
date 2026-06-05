using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DisposableStackTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DisposeRunsResourcesInReverseOrder()
    {
        Assert.True(Run("""
            var order = [];
            var stack = new DisposableStack();
            stack.defer(function () { order.push(1); });
            stack.defer(function () { order.push(2); });
            stack.dispose();
            order.length === 2 && order[0] === 2 && order[1] === 1;
            """).AsBoolean());
    }

    [Fact]
    public void UseInvokesSymbolDispose()
    {
        Assert.True(Run("""
            var disposed = false;
            var value = {
              [Symbol.dispose]: function () { disposed = true; }
            };
            var stack = new DisposableStack();
            stack.use(value);
            stack.dispose();
            disposed;
            """).AsBoolean());
    }

    [Fact]
    public void MoveTransfersResourcesAndDisposesOriginal()
    {
        Assert.True(Run("""
            var order = [];
            var stack = new DisposableStack();
            stack.defer(function () { order.push(1); });
            var moved = stack.move();
            var originalDisposed = stack.disposed;
            moved.dispose();
            originalDisposed === true && order.length === 1 && order[0] === 1;
            """).AsBoolean());
    }

    [Fact]
    public void DisposeWrapsMultipleErrorsInSuppressedError()
    {
        Assert.True(Run("""
            var e1 = new Error('1');
            var e2 = new Error('2');
            var stack = new DisposableStack();
            stack.defer(function () { throw e1; });
            stack.defer(function () { throw e2; });
            try {
              stack.dispose();
              false;
            } catch (e) {
              e instanceof SuppressedError &&
                e.error === e1 &&
                e.suppressed === e2;
            }
            """).AsBoolean());
    }
}
