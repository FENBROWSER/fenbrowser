using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Script and eval code on the register-window loop (ECMA-262 16.1.6
// ScriptEvaluation, 19.2.1.1 PerformEval): the frame has no callee and no
// record of its own, and every name resolves through the environment the
// declarations were instantiated into.
public sealed class Interp2ProgramCodeTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("var x = 1;")]
    [InlineData("let y = 1; { let z = 2; } eval('1');")]
    [InlineData("with ({}) {}")]
    public void ScriptCodeRunsOnTheRegisterWindow(string source)
    {
        var layout = FrameLayout.For(Compile(source));
        Assert.Equal(Interp2Bailout.None, layout.Bailout);
        Assert.True(layout.Eligible);
    }

    [Theory]
    // A var is a property of the global object, a let is not.
    [InlineData("var v = 1; let l = 2; String(globalThis.v) + ',' + String(globalThis.l) + ',' + l;", "1,undefined,2")]
    [InlineData("function f() { return 'f'; } typeof globalThis.f + f();", "functionf")]
    [InlineData("String(this === globalThis);", "true")]
    // The dead zone of a script's let.
    [InlineData("var r; try { early; } catch (e) { r = e.constructor.name; } let early = 1; r;", "ReferenceError")]
    [InlineData("const c = 1; var r; try { c = 2; } catch (e) { r = e.constructor.name; } r + c;", "TypeError1")]
    // A closure over a script's let sees later writes.
    [InlineData("let n = 1; function get() { return n; } n = 5; String(get());", "5")]
    // The completion value is the script's result.
    [InlineData("if (true) { 'then'; } else { 'else'; }", "then")]
    [InlineData("undeclaredGlobal = 3; String(globalThis.undeclaredGlobal);", "3")]
    public void ScriptCodeBindsThroughTheGlobalEnvironment(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void ScriptsShareTheGlobalLexicalEnvironment()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile("let shared = 'first'; var counter = 1;"));
        Assert.Equal("first,2", interpreter.Execute(Compile("counter++; shared + ',' + counter;")).AsString());
        var thrown = Assert.Throws<JsThrownException>(() => interpreter.Execute(Compile("let shared = 2;")));
        Assert.Contains("SyntaxError", thrown.Message);
    }

    [Theory]
    // eval code: its lets stay in it, a sloppy var reaches the caller.
    [InlineData("function f() { eval('let inner = 1; var outer = 2;'); return typeof inner + ',' + outer; } f();", "undefined,2")]
    [InlineData("eval('var fromEval = 1;'); String(globalThis.fromEval);", "1")]
    [InlineData("(0, eval)('var indirect = 1;'); String(globalThis.indirect);", "1")]
    [InlineData("String(eval('1 + 1; 3;'));", "3")]
    [InlineData("var o = { m() { return eval('this'); } }; String(o.m() === o);", "true")]
    // A throw inside eval code is caught by the caller's try.
    [InlineData("var r; try { eval('throw new TypeError(\"x\")'); } catch (e) { r = e.constructor.name; } r;", "TypeError")]
    // A try inside eval code handles its own throw.
    [InlineData("eval('var r; try { throw 1; } catch (e) { r = \"caught\"; } r;');", "caught")]
    // Annex B.3.3.3: a block function in sloppy eval code reaches the var scope.
    [InlineData("function f() { eval('{ function inBlock() { return 7; } }'); return inBlock(); } String(f());", "7")]
    public void EvalCodeRunsInItsOwnEnvironment(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }
}
