using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class GeneratorYieldTests
{
    private JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void YieldExpressionCompilesAndReturnsValue()
    {
        Assert.Equal(42d, Run("function g() { yield 42; return 'done'; } var r = g(); r.value;").AsNumber());
    }

    [Fact]
    public void YieldReturnsDoneFalse()
    {
        Assert.True(Run("function g() { yield 'hello'; } var r = g(); r.done === false;").AsBoolean());
    }

    [Fact]
    public void YieldStarProducesDoneTrue()
    {
        Assert.True(Run("function g() { yield* [1,2]; } var r = g(); r.done;").AsBoolean());
    }

    [Fact]
    public void YieldWithoutOperandSeesUndefined()
    {
        Assert.True(Run("function g() { yield; } var r = g(); r.value === undefined;").AsBoolean());
    }
}
