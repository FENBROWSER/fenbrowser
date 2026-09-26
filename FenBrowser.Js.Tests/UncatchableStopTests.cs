using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// An exhausted deadline, budget or interrupt is not a JavaScript exception:
// script cannot catch it, and neither may the native code between it and the
// host. Promise jobs, promise executors, async function bodies and iterator
// closing each caught every JsThrownException and turned it into a rejection
// or dropped it, so a runaway script inside a `.then` handler kept running
// after its deadline - the reaction's promise was rejected and the job loop
// moved on.
public sealed class UncatchableStopTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static void AssertStoppedByDeadline(string source)
    {
        var interpreter = new BytecodeInterpreter { WallClockTimeoutMs = 50 };
        var thrown = Assert.Throws<JsThrownException>(() => interpreter.Execute(Compile(source)));
        Assert.True(thrown.IsUncatchableByScript, "the stop was turned into an ordinary exception");
    }

    [Theory]
    // A promise reaction.
    [InlineData("var after = 0; Promise.resolve().then(() => { while (true) {} }).catch(() => { after = 1; });")]
    // A promise executor.
    [InlineData("new Promise(() => { while (true) {} }).catch(() => {});")]
    // An async function body, before and after its first await.
    [InlineData("(async () => { while (true) {} })().catch(() => {});")]
    [InlineData("(async () => { await null; while (true) {} })().catch(() => {});")]
    // A thenable's then, called from a promise job.
    [InlineData("Promise.resolve({ then() { while (true) {} } }).catch(() => {});")]
    // An iterator's return, called while closing on an error.
    [InlineData("var it = { [Symbol.iterator]() { return this; }, next() { return { value: 1, done: false }; }," +
                " return() { while (true) {} } }; try { for (var v of it) throw 0; } catch (e) {}")]
    // The same inside a function, whose for-of closes the iterator suppressing
    // errors from return() - but not a stop.
    [InlineData("var it = { [Symbol.iterator]() { return this; }, next() { return { value: 1, done: false }; }," +
                " return() { while (true) {} } }; function f() { try { for (var v of it) throw 0; } catch (e) {} } f();")]
    public void ADeadlineReachedInsideNativeMachineryStillStopsTheScript(string source)
    {
        AssertStoppedByDeadline(source);
    }
}
