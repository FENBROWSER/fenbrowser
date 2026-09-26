using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 15.5.5 yield* and 14.7.5 for-await-of on the register-window loop.
// yield* is one delegation step per resume: the generator re-runs the
// instruction, forwarding the completion it was resumed with to the delegate.
[Collection(nameof(Interpreter2ParityTests))]
public sealed class Interp2GeneratorDelegationTests : IDisposable
{
    private readonly bool _previousEngine = Interp2Options.Enabled;

    public Interp2GeneratorDelegationTests() => Interp2Options.Enabled = true;

    public void Dispose() => Interp2Options.Enabled = _previousEngine;

    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function* g() { yield* [1, 2]; }")]
    [InlineData("async function f(xs) { for await (const x of xs) {} }")]
    public void TheseBodiesRunOnTheRegisterWindow(string declaration)
    {
        Assert.Equal(Interp2Bailout.None, FrameLayout.For(Compile(declaration).NestedFunctions[0]).Bailout);
    }

    [Theory]
    [InlineData("function* g() { yield* [1, 2]; yield 3; } [...g()].join();", "1,2,3")]
    [InlineData("function* inner() { yield 1; return 'r'; } function* outer() { var v = yield* inner(); yield v; }" +
                " [...outer()].join();", "1,r")]
    // A value passed to next() reaches the delegate.
    [InlineData("function* inner() { var a = yield 1; var b = yield a + 1; return b; } function* outer() { return yield* inner(); }" +
                " var it = outer(); var r = [it.next().value, it.next(10).value, it.next(20).value].join(); r;", "1,11,20")]
    // throw() goes to the delegate, which may catch it and carry on.
    [InlineData("function* inner() { try { yield 1; } catch (e) { yield 'caught ' + e; } } function* outer() { yield* inner(); }" +
                " var it = outer(); it.next(); it.throw('x').value;", "caught x")]
    // return() runs the delegate's finally and completes the outer generator.
    [InlineData("var log = []; function* inner() { try { yield 1; } finally { log.push('inner'); } }" +
                " function* outer() { try { yield* inner(); } finally { log.push('outer'); } }" +
                " var it = outer(); it.next(); var r = it.return(7); log.join() + ':' + r.value + ':' + r.done;", "inner,outer:7:true")]
    // Nested delegation.
    [InlineData("function* a() { yield 1; } function* b() { yield* a(); yield 2; } function* c() { yield* b(); yield 3; }" +
                " [...c()].join();", "1,2,3")]
    public void DelegationForwardsEveryCompletion(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    // A delegate without throw() is closed, then a TypeError is raised.
    [InlineData("var closed = false; var it = { [Symbol.iterator]() { return this; }, next() { return { value: 1, done: false }; }," +
                " return() { closed = true; return {}; } }; function* g() { yield* it; } var gen = g(); gen.next();" +
                " var name; try { gen.throw(new Error('e')); } catch (e) { name = e.constructor.name; } name + ',' + closed;", "TypeError,true")]
    // A delegate without return() just lets the outer generator finish.
    [InlineData("var it = { [Symbol.iterator]() { return this; }, next() { return { value: 1, done: false }; } };" +
                " function* g() { yield* it; } var gen = g(); gen.next(); var r = gen.return(5); r.value + ',' + r.done;", "5,true")]
    // The delegate's result object is yielded as it is, not rebuilt.
    [InlineData("var result = { value: 1, done: false }; var it = { [Symbol.iterator]() { return this; }, next() { return result; } };" +
                " function* g() { yield* it; } String(g().next() === result);", "true")]
    // done is read with [[Get]] and ToBoolean; next is read once and called with one argument.
    [InlineData("var gets = 0, argc = []; var n = 0; var it = { [Symbol.iterator]() { return this; }," +
                " get next() { gets++; return function () { argc.push(arguments.length); n++;" +
                " return { get done() { return n > 2 ? 1 : 0; }, value: n }; }; } };" +
                " function* g() { return yield* it; } var gen = g(); gen.next(); gen.next(); var last = gen.next();" +
                " gets + ':' + argc.join() + ':' + last.value + ':' + last.done;", "1:1,1,1:3:true")]
    public void TheDelegateIsDrivenAsTheSpecificationSays(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("async function* src() { yield 1; yield 2; } async function f() { var s = []; for await (const x of src()) s.push(x); out = s.join(); } f();", "1,2")]
    [InlineData("async function f() { var s = []; for await (const x of [Promise.resolve(3), 4]) s.push(x); out = s.join(); } f();", "3,4")]
    [InlineData("var closed = false; async function* src() { try { yield 1; yield 2; } finally { closed = true; } }" +
                " async function f() { for await (const x of src()) break; out = String(closed); } f();", "true")]
    public void ForAwaitDrivesAsyncAndSyncIterables(string source, string expected)
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile("var out = 'pending'; " + source));
        Assert.Equal(expected, interpreter.Execute(Compile("out;")).AsString());
    }
}
