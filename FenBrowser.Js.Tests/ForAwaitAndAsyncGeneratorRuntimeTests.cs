using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ForAwaitAndAsyncGeneratorRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

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
    public void ForAwait_IteratesAndAwaitsSyncIterableValues()
    {
        Assert.Equal(6d, RunThenRead(
            "var observed; async function f(){ var s = 0; for await (var x of [Promise.resolve(1), Promise.resolve(2), 3]) { s += x; } return s; } f().then(function(v){ observed = v; });",
            "observed;").AsNumber());
    }

    [Fact]
    public void ForAwaitOutsideAsyncIsRejectedAtCompileTime()
    {
        var compiler = new BytecodeCompiler();
        var ex = Assert.Throws<UnsupportedFeatureException>(() =>
            compiler.CompileScript(new SourceText("for await (var x of [1,2]) { x; }")));

        Assert.Equal("for-await-outside-async", ex.FeatureName);
        Assert.Equal(FeatureSupportLevel.ParserOnly, ex.Level);
    }

    [Fact]
    public void AsyncGenerator_NextReturnsPromise()
    {
        Assert.Equal("function", Run("async function* g(){ yield 1; } typeof g().next; typeof g().next().then;").AsString());
    }

    [Fact]
    public void AsyncGenerator_YieldsValuesThroughNextPromises()
    {
        Assert.Equal(1d, RunThenRead(
            "var observed; async function* g(){ yield 1; } g().next().then(function(r){ observed = r.value; });",
            "observed;").AsNumber());
    }
}
