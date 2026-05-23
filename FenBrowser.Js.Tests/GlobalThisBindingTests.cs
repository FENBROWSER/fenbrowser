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
        Assert.Equal(42, RunNum("globalThis.x = 42; globalThis.x;"));
    }

    [Fact]
    public void PropertiesAssignedToGlobalThisResolveAsBareGlobalBindings()
    {
        Assert.Equal(42, RunNum("globalThis.x = 42; x;"));
    }

    [Fact]
    public void GlobalVarIsReachableThroughGlobalThis()
    {
        Assert.Equal(7, RunNum("var y = 7; globalThis.y;"));
    }

    [Fact]
    public void BareGlobalAssignmentIsReachableThroughGlobalThis()
    {
        Assert.Equal(11, RunNum("z = 11; globalThis.z;"));
    }

    [Fact]
    public void StandardGlobalsAreGlobalThisProperties()
    {
        Assert.True(RunBool("globalThis.globalThis === globalThis;"));
        Assert.True(RunBool("Object === globalThis.Object;"));
        Assert.True(RunBool("parseInt === globalThis.parseInt;"));
    }

    [Fact]
    public void FunctionBodiesResolveGlobalObjectBindings()
    {
        Assert.Equal(5, RunNum("globalThis.shared = 5; function read() { return shared; } read();"));
    }
}
