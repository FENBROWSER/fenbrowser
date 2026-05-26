using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class EvalRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void DirectEval_CreatesVarInCallerScope()
    {
        // ECMA-262 19.2.1.1 — direct eval creates 'var' in the calling environment.
        Assert.Equal(42, RunNum("function f(){ eval('var x = 42;'); return x; } f();"));
    }

    [Fact]
    public void Eval_NonStringReturnsInput()
    {
        Assert.Equal(42, RunNum("eval(42);"));
    }

    [Fact]
    public void Eval_NoArgsReturnsUndefined()
    {
        Assert.Equal(JsValueTag.Undefined, Run("eval();").Tag);
    }
}
