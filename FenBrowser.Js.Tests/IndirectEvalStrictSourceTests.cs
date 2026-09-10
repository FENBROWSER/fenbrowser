using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 19.2.1.1 PerformEval for an indirect eval: it runs against the
/// global environment, but a strict source still gets a fresh declarative
/// variable environment, so its vars and functions stay inside it. A sloppy
/// source keeps putting them on the global object. Every expected value here
/// is what V8 returns.
/// </summary>
public sealed class IndirectEvalStrictSourceTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AStrictSourceVarDoesNotLeakOntoTheGlobal()
    {
        Assert.Equal(
            "undefined",
            RunString("(0, eval)('\"use strict\"; var indirectStrictVar = 1;'); typeof indirectStrictVar;"));
    }

    [Fact]
    public void AStrictSourceFunctionDoesNotLeakOntoTheGlobal()
    {
        Assert.Equal(
            "undefined",
            RunString("(0, eval)('\"use strict\"; function indirectStrictFn() {}'); typeof indirectStrictFn;"));
    }

    [Fact]
    public void ASloppySourceVarStillLandsOnTheGlobal()
    {
        Assert.Equal(
            "number:2",
            RunString("(0, eval)('var indirectSloppyVar = 2;'); typeof indirectSloppyVar + ':' + indirectSloppyVar;"));
    }

    [Fact]
    public void AStrictSourceKeepsItsCompletionValue()
    {
        Assert.Equal("9", RunString("String((0, eval)('\"use strict\"; var cv = 9; cv'));"));
    }

    [Fact]
    public void AStrictSourceVarDoesNotCollideWithAGlobalLet()
    {
        // test262 language/eval-code/indirect/var-env-global-lex-strict.js
        Assert.Equal(
            "no-throw",
            RunString("let x; (0, eval)('\"use strict\"; var x;'); 'no-throw';"));
    }
}
