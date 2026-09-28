using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A browser compiles every script of a page on one BytecodeCompiler. Per-program state
/// must not survive into the next program: the constant index of the `undefined` that
/// seeds an if/loop statement's completion value (ECMA-262 UpdateEmpty) was kept, so a
/// later script loaded a constant past the end of its own pool and failed verification.
/// w3schools' tryit page lost its resize handler this way.
/// </summary>
public sealed class BytecodeCompilerReuseTests
{
    [Fact]
    public void CompletionResetConstant_IsAllocatedPerProgram()
    {
        BytecodeCache.Enabled = false;
        try
        {
            var compiler = new BytecodeCompiler();

            // Five constants precede the first if statement, so `undefined` lands at 5.
            var first = compiler.CompileScript(new SourceText("var a = 'a', b = 'b', c = 'c', d = 'd', e = 'e'; if (a) {}"));
            new BytecodeVerifier().Verify(first);

            // One constant precedes it here; the pool never reaches index 5.
            var second = compiler.CompileScript(new SourceText("var q = 'q'; if (q) {}"));
            new BytecodeVerifier().Verify(second);

            Assert.Equal(FenBrowser.Js.Runtime.JsValueTag.Undefined, new BytecodeInterpreter().Execute(second).Tag);
        }
        finally
        {
            BytecodeCache.Enabled = true;
        }
    }
}
