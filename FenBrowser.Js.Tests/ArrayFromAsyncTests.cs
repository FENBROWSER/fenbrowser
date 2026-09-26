using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 23.1.2.2 Array.fromAsync. It used to run Array.from synchronously
// and resolve with the result, so an async mapfn's promise was stored instead of
// awaited, its rejection was never seen, and an endless iterator was drained in
// one native loop until memory ran out.
public sealed class ArrayFromAsyncTests
{
    // Execute drains the job queue before returning, so the second script
    // sees what the promise settled with.
    private static string Settle(string expression)
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile(
            "var out = 'pending';" +
            "(" + expression + ").then(v => { out = 'ok:' + (Array.isArray(v) ? '[' + v.join() + ']' : String(v)); }," +
            " e => { out = 'err:' + (e && e.constructor ? e.constructor.name : String(e)) + (e && e.message ? ':' + e.message : ''); });"));
        return interpreter.Execute(Compile("out;")).AsString();
    }

    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    [Theory]
    [InlineData("Array.fromAsync([1, 2, 3])", "ok:[1,2,3]")]
    [InlineData("Array.fromAsync([Promise.resolve(1), 2])", "ok:[1,2]")]
    [InlineData("Array.fromAsync(new Set([4, 5]), v => v * 2)", "ok:[8,10]")]
    [InlineData("Array.fromAsync([1, 2], async v => v + 1)", "ok:[2,3]")]
    [InlineData("Array.fromAsync({ length: 2, 0: 'a', 1: Promise.resolve('b') })", "ok:[a,b]")]
    [InlineData("Array.fromAsync({ async *[Symbol.asyncIterator]() { yield 1; yield 2; } })", "ok:[1,2]")]
    [InlineData("Array.fromAsync('ab')", "ok:[a,b]")]
    public void ItResolvesWithTheAwaitedValues(string expression, string expected)
    {
        Assert.Equal(expected, Settle(expression));
    }

    [Fact]
    public void AnAsyncMapfnRejectionClosesAnEndlessIteratorAndRejects()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile(
            "var closed = false, out = 'pending';" +
            "var it = { next() { return { value: 1, done: false }; }, return() { closed = true; return { done: true }; }," +
            " [Symbol.iterator]() { return this; } };" +
            "Array.fromAsync(it, async v => { throw new Error('boom'); })" +
            ".then(() => { out = 'resolved'; }, e => { out = e.message + ',' + closed; });"));
        Assert.Equal("boom,true", interpreter.Execute(Compile("out;")).AsString());
    }

    [Theory]
    [InlineData("Array.fromAsync([1], 5)", "err:TypeError")]
    [InlineData("Array.fromAsync(null)", "err:TypeError")]
    [InlineData("Array.fromAsync({ [Symbol.asyncIterator]: 1 })", "err:TypeError")]
    [InlineData("Array.fromAsync({ [Symbol.iterator]() { return { next() { throw new RangeError('n'); } }; } })", "err:RangeError:n")]
    public void AnAbruptStepRejectsRatherThanThrowing(string expression, string expected)
    {
        Assert.StartsWith(expected, Settle(expression));
    }

    [Fact]
    public void AConstructorThisBuildsTheResult()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile(
            "var out; class MyArray extends Array {}" +
            " Array.fromAsync.call(MyArray, [1, 2]).then(v => { out = (v instanceof MyArray) + ':' + v.length; });"));
        Assert.Equal("true:2", interpreter.Execute(Compile("out;")).AsString());
    }
}
