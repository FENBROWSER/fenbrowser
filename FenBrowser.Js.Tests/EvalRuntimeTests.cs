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
        // ECMA-262 19.2.1.1 - direct eval creates 'var' in the calling environment.
        Assert.Equal(42, RunNum("function f(){ eval('var x = 42;'); return x; } f();"));
    }

    [Fact]
    public void IndirectEval_DoesNotUseCallerLexicalEnvironment()
    {
        Assert.Equal(1, RunNum("function f(){ var localOnly = 1; (0, eval)('__indirectEvalX = 2;'); return localOnly; } f();"));
    }

    [Fact]
    public void StrictDirectEval_DoesNotLeakVarToCaller()
    {
        Assert.Equal(
            "ReferenceError",
            Run("function f(){ 'use strict'; eval('var __strictLeakProbe = 1;'); let observed = 'ok'; try { __strictLeakProbe; observed = 'leaked'; } catch (e) { observed = e.name; } return observed; } f();").AsString());
    }

    [Fact]
    public void DirectEvalCall_IsTaggedInBytecode()
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText("function f(){ eval('1'); }"));
        var f = Assert.Single(script.NestedFunctions, n => n.Name == "f");
        Assert.Contains(f.Instructions, ins => ins.OpCode == OpCode.Call1 && ins.E == 1);
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

    [Fact]
    public void Eval_UsesConfiguredParserRecursionDepth()
    {
        var nestedExpression = new string('(', 12) + "1" + new string(')', 12);
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText($"try {{ eval('{nestedExpression}'); }} catch (e) {{ e.name; }}"));
        var interpreter = new BytecodeInterpreter
        {
            ParserMaxRecursionDepth = 8
        };

        Assert.Equal("SyntaxError", interpreter.Execute(fn).AsString());
    }
}
