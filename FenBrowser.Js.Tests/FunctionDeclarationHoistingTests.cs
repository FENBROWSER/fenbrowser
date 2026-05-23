using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class FunctionDeclarationHoistingTests
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
    public void FunctionDeclarationCanBeCalledBeforeSourcePosition()
    {
        Assert.Equal(9, RunNum("f(); function f(){ return 9; }"));
    }

    [Fact]
    public void HoistedFunctionDeclarationIsGlobalObjectProperty()
    {
        Assert.True(RunBool("function f(){ return 1; } globalThis.f === f;"));
    }

    [Fact]
    public void SiblingFunctionDeclarationsResolveThroughEnvironment()
    {
        Assert.Equal(6, RunNum("function a(){ return b(); } function b(){ return 6; } a();"));
    }

    [Fact]
    public void NestedFunctionDeclarationCanCallLaterPeer()
    {
        const string source = """
            function outer() {
                function caller() { return helper(21); }
                function helper(v) { return v * 2; }
                return caller();
            }
            outer();
            """;

        Assert.Equal(42, RunNum(source));
    }
}
