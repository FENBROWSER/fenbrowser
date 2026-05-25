using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class GeneratorYieldTests
{
    private static JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // ECMA-262 27.5.1.2 — first .next() starts execution.
    [Fact]
    public void FirstNextReturnsYieldedValue()
    {
        var code = @"
            function* g() { yield 42; }
            var gen = g();
            gen.next().value;
        ";
        Assert.Equal(42d, Run(code).AsNumber());
    }

    [Fact]
    public void FirstNextReturnsDoneFalse()
    {
        var code = @"
            function* g() { yield 'hello'; }
            var gen = g();
            gen.next().done === false;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 27.5.1.2 — resumption: second .next() continues from saved IP.
    [Fact]
    public void SecondNextResumesAfterYield()
    {
        var code = @"
            function* g() { yield 1; yield 2; }
            var gen = g();
            gen.next();       // first: 1
            gen.next().value; // second: 2
        ";
        Assert.Equal(2d, Run(code).AsNumber());
    }

    [Fact]
    public void MultiYieldSequenceProducesCorrectValues()
    {
        var code = @"
            function* g() { yield 'a'; yield 'b'; yield 'c'; }
            var gen = g();
            var v1 = gen.next().value;
            var v2 = gen.next().value;
            var v3 = gen.next().value;
            v1 + v2 + v3;
        ";
        Assert.Equal("abc", Run(code).AsString());
    }

    // ECMA-262 27.5.1.2 — sent value: .next(val) injects val as the yield result.
    [Fact]
    public void NextWithArgumentSetsYieldExpressionValue()
    {
        var code = @"
            function* g() { var received = yield 1; return received; }
            var gen = g();
            gen.next();              // start, yields 1
            gen.next('hello').value; // resume, sent value becomes yield result
        ";
        Assert.Equal("hello", Run(code).AsString());
    }

    [Fact]
    public void NextArgumentSeenBySecondYield()
    {
        var code = @"
            function* g() { var a = yield 10; var b = yield 20; return a + b; }
            var gen = g();
            gen.next();           // start
            gen.next(100);        // a = 100
            gen.next(200).value;  // b = 200, returns 300
        ";
        Assert.Equal(300d, Run(code).AsNumber());
    }

    // ECMA-262 27.5.1.2 — return inside generator produces {done: true}.
    [Fact]
    public void GeneratorReturnProducesDoneTrue()
    {
        var code = @"
            function* g() { yield 1; return 'final'; }
            var gen = g();
            gen.next();           // yield 1
            var r = gen.next();   // return 'final'
            r.done === true && r.value === 'final';
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 27.5.1.2 — falling off the end returns {value: undefined, done: true}.
    [Fact]
    public void GeneratorFalloffProducesUndefinedDone()
    {
        var code = @"
            function* g() { yield 42; }
            var gen = g();
            gen.next();           // yield 42
            var r = gen.next();   // fall off end
            r.done === true && r.value === undefined;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // Generator with parameters: first call passed to generator function, body sees them.
    [Fact]
    public void GeneratorParametersAvailableInBody()
    {
        var code = @"
            function* adder(a, b) { yield a + b; }
            var gen = adder(3, 4);
            gen.next().value;
        ";
        Assert.Equal(7d, Run(code).AsNumber());
    }

    // Completed generator returns {done: true} on further .next() calls.
    [Fact]
    public void CompletedGeneratorNextReturnsDoneTrue()
    {
        var code = @"
            function* g() { yield 'only'; }
            var gen = g();
            gen.next();           // consume
            gen.next().done;      // already completed
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 27.5.1.3 — Generator.prototype.return on a suspended generator.
    [Fact]
    public void GeneratorReturnOnSuspendedProducesDoneTrue()
    {
        var code = @"
            function* g() { yield 1; yield 2; }
            var gen = g();
            gen.next();              // yield 1
            var r = gen.return(99);  // inject return
            r.done === true && r.value === 99;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 27.5.1.3 — .return() on a completed generator returns {done: true}.
    [Fact]
    public void GeneratorReturnOnCompletedReturnsDoneTrue()
    {
        var code = @"
            function* g() { yield 'x'; }
            var gen = g();
            gen.next();              // consume
            gen.next();              // complete
            var r = gen.return(42);  // return on completed
            r.done === true;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 27.5.1.4 — Generator.prototype.throw injects an exception
    // that is caught by the nearest try/catch in the generator body.
    [Fact]
    public void GeneratorThrowInjectsExceptionCaughtByCatch()
    {
        var code = @"
            var observed = 'none';
            function* g() {
                try { yield 1; }
                catch (e) { observed = 'ok'; return 'caught'; }
            }
            var gen = g();
            gen.next();
            try { gen.throw('boom'); } catch (ex) { observed = 'top-error'; }
            observed;
        ";
        Assert.Equal("ok", Run(code).AsString());
    }

    // .throw() on completed generator propagates the exception.
    [Fact]
    public void GeneratorThrowOnCompletedThrows()
    {
        var code = @"
            function* g() { yield 'x'; }
            var gen = g();
            gen.next();          // yield 'x'
            gen.next();          // complete
            var threw = false;
            try { gen.throw('err'); }
            catch (e) { threw = true; }
            threw;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // .throw() uncaught inside the generator body propagates to the caller.
    [Fact]
    public void GeneratorThrowUncaughtPropagates()
    {
        var code = @"
            function* g() { yield 1; }
            var gen = g();
            gen.next();
            var threw = false;
            try { gen.throw('unhandled'); }
            catch (e) { threw = true; }
            threw;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 15.5 — function* declaration creates a generator.
    [Fact]
    public void GeneratorFunctionDeclarationCreatesGenerator()
    {
        var code = @"
            function* g() { yield 1; yield 2; }
            var gen = g();
            typeof gen === 'object' && typeof gen.next === 'function';
        ";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void GeneratorFunctionYieldsValues()
    {
        var code = @"
            function* g() { yield 'a'; yield 'b'; }
            var gen = g();
            gen.next().value + gen.next().value;
        ";
        Assert.Equal("ab", Run(code).AsString());
    }

    [Fact]
    public void GeneratorFunctionExpressionWorks()
    {
        var code = @"
            var g = function*() { yield 42; };
            var gen = g();
            gen.next().value;
        ";
        Assert.Equal(42d, Run(code).AsNumber());
    }

    // .return() on a suspended generator before any yield.
    [Fact]
    public void GeneratorReturnBeforeFirstYield()
    {
        var code = @"
            function* g() { yield 1; yield 2; }
            var gen = g();
            var r = gen.return('early');
            r.done === true && r.value === 'early';
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // ECMA-262 15.5.5 — yield* delegation: values from inner iterator are
    // yielded in sequence — generator-to-generator delegation.
    [Fact]
    public void YieldStarDelegatesValues()
    {
        var code = @"
            function* inner() { yield 1; yield 2; }
            function* outer() { yield* inner(); }
            var gen = outer();
            gen.next().value;
        ";
        Assert.Equal(1d, Run(code).AsNumber());
    }

    // yield* returns the completion value of the inner iterator.
    // After delegation completes, the generator continues execution.
    [Fact]
    public void YieldStarReturnsCompletionValue()
    {
        var code = @"
            function* inner() { yield 1; return 'done'; }
            function* outer() { var r = yield* inner(); return r; }
            var gen = outer();
            gen.next();              // yield 1 from inner
            gen.next().value;        // return 'done' (inner's return value)
        ";
        Assert.Equal("done", Run(code).AsString());
    }

    // yield* with multiple inner yields and outer yield — generator delegation.
    [Fact]
    public void YieldStarMixedWithOuterYields()
    {
        var code = @"
            function* inner() { yield 'a'; yield 'b'; }
            function* outer() { yield* inner(); yield 'c'; }
            var gen = outer();
            var v1 = gen.next().value;
            var v2 = gen.next().value;
            var v3 = gen.next().value;
            v1 + v2 + v3;
        ";
        Assert.Equal("abc", Run(code).AsString());
    }

    // ECMA-262 15.5.5 step 5.d — .return() on outer generator during yield*
    // forwards to the inner generator. The outer .return() produces the value.
    [Fact]
    public void YieldStarReturnForwardsToInner()
    {
        var code = @"
            function* inner() {
                try { yield 1; }
                finally { }
            }
            function* outer() { yield* inner(); }
            var gen = outer();
            gen.next();              // yield 1 from inner
            var r = gen.return(99);
            r.done === true;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    // .return() value propagates correctly through yield*.
    [Fact]
    public void YieldStarReturnValuePropagates()
    {
        var code = @"
            function* inner() { yield 1; }
            function* outer() { yield* inner(); yield 2; }
            var gen = outer();
            gen.next();               // yield 1 (from inner)
            gen.return(42).value;     // .return() value should be 42
        ";
        Assert.Equal(42d, Run(code).AsNumber());
    }

    // .throw() during yield* propagates when inner iterator has no .throw().
    [Fact]
    public void YieldStarThrowInjectsWhenInnerHasNoThrow()
    {
        var code = @"
            function* outer() {
                try { yield* [1, 2]; }
                catch (e) { return 'caught'; }
            }
            var gen = outer();
            gen.next();              // yield 1
            gen.throw('boom').value; // propagates through yield*, caught by outer
        ";
        Assert.Equal("caught", Run(code).AsString());
    }

    // Catch variable is accessible on resume after .throw().
    [Fact]
    public void GeneratorThrowCatchVariableAccessible()
    {
        var code = @"
            function* g() {
                try { yield 1; }
                catch (e) { return (e === 'boom') ? 'ok' : 'fail'; }
            }
            var gen = g();
            gen.next();
            gen.throw('boom').value;
        ";
        Assert.Equal("ok", Run(code).AsString());
    }

    // Catch variable properly scoped — does not leak to outer scope.
    [Fact]
    public void CatchVariableDoesNotLeakToGlobal()
    {
        var code = @"
            function* g() {
                try { yield 1; }
                catch (e) { }
            }
            var gen = g();
            gen.next();
            gen.throw('err');
            typeof globalThis.e === 'undefined';
        ";
        Assert.True(Run(code).AsBoolean());
    }
}
