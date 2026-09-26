using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Error.prototype.stack (a host-defined property every engine provides): the
// active frames innermost first, as "    at name (file:line:column)", across
// register windows and compiled frames and through natives that call back
// into script.
public sealed class ErrorStackTests
{
    private static string[] StackLines(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString().Split('\n');
    }

    [Fact]
    public void EveryFrameIsListedInnermostFirst()
    {
        var lines = StackLines(
            "function a() { return b(); }\n" +
            "function b() { return [1].map(function c() { return new Error('x').stack; })[0]; }\n" +
            "a();");

        Assert.Equal("Error: x", lines[0]);
        Assert.StartsWith("    at c (", lines[1]);
        Assert.StartsWith("    at b (", lines[2]);
        Assert.StartsWith("    at a (", lines[3]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void FramesCarryTheLineAndColumnOfTheCall()
    {
        var lines = StackLines(
            "function inner() {\n" +
            "  return new Error('here').stack;\n" +
            "}\n" +
            "function outer() { return inner(); }\n" +
            "outer();");

        Assert.Equal("    at inner (<anonymous>:2:10)", lines[1]);
        Assert.Equal("    at outer (<anonymous>:4:27)", lines[2]);
        Assert.Equal("    at <anonymous>:5:1", lines[3]);
    }

    [Fact]
    public void AnEngineThrownErrorNamesTheFrameThatFailed()
    {
        var lines = StackLines("function f() { null.x; }\nvar s; try { f(); } catch (e) { s = e.stack; } s;");

        Assert.StartsWith("TypeError: ", lines[0]);
        Assert.Equal("    at f (<anonymous>:1:16)", lines[1]);
    }

    [Fact]
    public void InterpreterInternalsDoNotLeakIntoTheStack()
    {
        var stack = string.Join("\n", StackLines("function f() { return new Error('x').stack; } f();"));

        Assert.DoesNotContain("ip=", stack);
        Assert.DoesNotContain("op=", stack);
    }

    [Fact]
    public void DeepStacksAreCutAtTenFrames()
    {
        var lines = StackLines("function r(n) { return n === 0 ? new Error('deep').stack : r(n - 1); } r(50);");

        Assert.Equal(11, lines.Length);
    }
}
