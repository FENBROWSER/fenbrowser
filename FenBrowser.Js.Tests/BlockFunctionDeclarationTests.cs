using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BlockFunctionDeclarationTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void FunctionDeclarationInsideForOfBlockIsInitialized()
    {
        Assert.True(RunBool("""
            var ok = false;
            for (let x of [1]) {
              function inside() { return x; }
              ok = typeof inside === 'function' && inside() === 1;
            }
            ok;
            """));
    }
}
