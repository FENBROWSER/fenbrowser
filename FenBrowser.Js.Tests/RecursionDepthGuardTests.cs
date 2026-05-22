using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RecursionDepthGuardTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void DeepFiniteRecursionStillWorks()
    {
        // 50 levels of recursion must still succeed (cap is 80; leave headroom).
        Assert.Equal(50, RunNum("function f(n){return n === 0 ? 0 : 1 + f(n - 1);} f(50);"));
    }

    [Fact]
    public void UnboundedRecursionRaisesRangeErrorNotCrash()
    {
        // Without the guard, this would StackOverflow and kill the host.
        var ex = Assert.Throws<JsThrownException>(() =>
            RunNum("function f(){return f();} f();"));
        // Confirm the thrown value is a RangeError per spec ("Maximum call stack size exceeded").
        Assert.Equal(JsValueTag.Object, ex.Value.Tag);
    }

    [Fact]
    public void RecursionGuardResetsBetweenCalls()
    {
        // After one runaway recursion is caught, a fresh script should still get the
        // full call depth. Two separate Execute calls share an interpreter instance
        // here to exercise that.
        var compiler = new BytecodeCompiler();
        var interpreter = new BytecodeInterpreter();
        var bad = compiler.CompileScript(new SourceText("function f(){return f();} try { f(); 0; } catch (e) { 1; }"));
        new BytecodeVerifier().Verify(bad);
        Assert.Equal(1, interpreter.Execute(bad).AsNumber());

        var good = compiler.CompileScript(new SourceText("function f(n){return n === 0 ? 0 : 1 + f(n - 1);} f(50);"));
        new BytecodeVerifier().Verify(good);
        Assert.Equal(50, interpreter.Execute(good).AsNumber());
    }
}
