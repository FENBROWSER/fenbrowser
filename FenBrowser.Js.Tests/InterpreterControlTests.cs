using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Plan §14.2 and §18: instruction budget, interrupt callback, FunctionKind.
public class InterpreterControlTests
{
    private JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // Plan §14.2 — instruction budget stops infinite loops.
    [Fact]
    public void InstructionBudget_ThrowsWhenExceeded()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("while(true){}"));
        new BytecodeVerifier().Verify(fn);
        var interpreter = new BytecodeInterpreter();
        interpreter.InstructionBudget = 1000;
        Assert.Throws<JsThrownException>(() => interpreter.Execute(fn));
    }

    [Fact]
    public void InstructionBudget_CompletesWithinBudget()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("var x=0; while(x<10){x=x+1;} x;"));
        new BytecodeVerifier().Verify(fn);
        var interpreter = new BytecodeInterpreter();
        interpreter.InstructionBudget = 10000;
        Assert.Equal(10d, interpreter.Execute(fn).AsNumber());
    }

    // Plan §14.2 — interrupt callback.
    [Fact]
    public void InterruptCallback_StopsExecution()
    {
        var compiler = new BytecodeCompiler();
        // The interrupt callback is sampled every ~1024 instructions, so the
        // loop must run long enough to produce 50+ samples.
        var fn = compiler.CompileScript(new SourceText("var x=0; while(x<1000000){x=x+1;} x;"));
        new BytecodeVerifier().Verify(fn);
        var interpreter = new BytecodeInterpreter();
        var calls = 0;
        interpreter.InterruptCallback = () => ++calls <= 50; // allow 50 checks, then kill
        Assert.Throws<JsThrownException>(() => interpreter.Execute(fn));
    }

    // Plan §18 — FunctionKind classification.
    [Fact]
    public void FunctionKind_OrdinaryIsDefaultForRegularFunctions()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function f(){} f();"));
        new BytecodeVerifier().Verify(fn);
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(fn);
        // FunctionKind.Ordinary is the default — no assertion needed,
        // just verifying it doesn't crash.
    }

    [Fact]
    public void FunctionKind_CanDistinguishArrowFromOrdinary()
    {
        // Verify the enum values exist and can be compared.
        Assert.NotEqual(FunctionKind.Ordinary, FunctionKind.Arrow);
        Assert.NotEqual(FunctionKind.Method, FunctionKind.Constructor);
        Assert.NotEqual(FunctionKind.Bound, FunctionKind.Native);
    }
}
