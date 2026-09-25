using System.Text;
using System.Threading;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// An expression nested more deeply than the calling thread's stack allows is
// compiled again on a large-stack thread rather than failing: minified bundles
// nest that deeply, and not every embedder compiles on the browser's 256MB
// script thread.
public sealed class CompilerLargeStackRetryTests
{
    private static T OnSmallStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException("compile failed: " + failure);
        return result;
    }

    [Fact]
    public void ADeepLeftAssociativeChainCompilesFromASmallStack()
    {
        // The parser builds a binary chain in a loop; the compiler recurses into
        // its left operand once per link.
        var source = new StringBuilder("var x = 100000");
        for (var i = 0; i < 20000; i++) source.Append(" - 1");
        source.Append("; x;");

        var fn = OnSmallStack(() => new BytecodeCompiler().CompileScript(new SourceText(source.ToString())));
        Assert.Equal(80000.0, new BytecodeInterpreter().Execute(fn).AsNumber());
    }
}
