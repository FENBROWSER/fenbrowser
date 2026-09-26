using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// On-stack replacement from the register-window loop: a frame that loops long
// enough finishes in its compiled code from the loop header, carrying its
// registers and variables across. Each case compiles the looping body up front
// so the hand-off does not depend on when a background compile lands.
public sealed class Interp2OsrTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    /// <summary>Runs the script with <paramref name="compiled"/> compiled first; false when the JIT is off or declines it.</summary>
    private static (string Result, long OsrEntries, bool Compiled) Run(string source, Func<BytecodeFunction, BytecodeFunction> compiled)
    {
        var script = Compile(source);
        var target = compiled(script);
        var hasCode = false;
#if !PUBLISH_AOT
        if (JitCompiler.Enabled)
        {
            target.JitCompileAttempted = true;
            target.JitDelegate = JitCompiler.TryCompile(target);
            hasCode = target.JitDelegate is not null && target.OsrEntryPoints is { Count: > 0 };
        }
#endif
        var interpreter = new BytecodeInterpreter();
        var result = interpreter.Execute(script).AsString();
        return (result, interpreter.Interp2State?.OsrEntries ?? 0, hasCode);
    }

    [Theory]
    // A body called once, from the top level and from another function.
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i * 3) | 0; return t; } String(f(100000));",
                "2114948112")]
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i * 3) | 0; return t; }" +
                " function g() { return 'r' + f(100000); } g();", "r2114948112")]
    // A function-level let and a const still in its dead zone cross over.
    [InlineData("function f(n) { let t = 0; for (var i = 0; i < n; i++) t = (t + i) | 0; const k = t; return String(k); } f(100000);",
                "704982704")]
    // The arguments object and a parameter written in the loop.
    [InlineData("function f(n) { for (var i = 0; i < 100000; i++) n = (n + 1) | 0; return n + ',' + arguments.length; } f(1);",
                "100001,1")]
    public void AHotLoopFinishesInCompiledCode(string source, string expected)
    {
        var (result, osrEntries, compiled) = Run(source, script => script.NestedFunctions[0]);
        Assert.Equal(expected, result);
        if (compiled)
        {
            Assert.True(osrEntries > 0, "the frame was never handed to compiled code");
        }
    }

#if !PUBLISH_AOT
    [Fact]
    public void ASimpleHotLoopIsHandedOver()
    {
        if (!JitCompiler.Enabled)
        {
            return;
        }

        var (result, osrEntries, compiled) = Run(
            "function f(n) { var t = 0; for (var i = 0; i < n; i++) t = (t + i * 3) | 0; return t; } String(f(100000));",
            script => script.NestedFunctions[0]);
        Assert.Equal("2114948112", result);
        Assert.True(compiled, "the JIT declined the body");
        Assert.Equal(1, osrEntries);
    }
#endif

    [Fact]
    public void AScriptsTopLevelLoopFinishesInCompiledCode()
    {
        var (result, _, _) = Run(
            "var t = 0; for (var i = 0; i < 100000; i++) t = (t + i * 3) | 0; String(t) + ',' + i;",
            script => script);
        Assert.Equal("2114948112,100000", result);
    }

    [Theory]
    // A closure made before the loop shares the variables the loop writes.
    [InlineData("function f(n) { var t = 0; var read = function () { return t; }; for (var i = 0; i < n; i++) t = (t + 1) | 0;" +
                " return String(read()); } f(100000);", "100000")]
    // Inside a try the frame has a handler, and stays where it is.
    [InlineData("function f(n) { var t = 0; try { for (var i = 0; i < n; i++) t = (t + 1) | 0; } catch (e) {} return String(t); } f(100000);",
                "100000")]
    // A throw from compiled code reaches the caller's catch.
    [InlineData("function f(n) { var t = 0; for (var i = 0; i < n; i++) { t = (t + 1) | 0; if (i === 90000) throw t; } }" +
                " var r; try { f(100000); } catch (e) { r = e; } String(r);", "90001")]
    public void StateCarriedAcrossTheHandOffStaysCorrect(string source, string expected)
    {
        var (result, _, _) = Run(source, script => script.NestedFunctions[0]);
        Assert.Equal(expected, result);
    }
}
