using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GlobalThisBindingTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void GlobalThisIsAnObject()
    {
        Assert.True(RunBool("typeof globalThis === 'object';"));
    }

    [Fact]
    public void PropertiesAssignedToGlobalThisRoundTripThroughIt()
    {
        // Reading the assigned property back through globalThis works today; reading
        // by bare identifier requires the global-object env-record routing that lands
        // with B.6.5.
        Assert.Equal(42, RunNum("globalThis.x = 42; globalThis.x;"));
    }

    [Fact]
    public void GlobalVarIsReachableThroughGlobalThis()
    {
        Assert.Equal(7, RunNum("var y = 7; globalThis.y;"));
    }
}
