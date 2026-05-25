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
}
