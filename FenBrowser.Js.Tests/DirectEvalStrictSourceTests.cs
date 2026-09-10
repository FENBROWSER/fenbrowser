using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 19.2.1.1 PerformEval: a direct eval is strict when its caller is
/// strict or when its own source opens with "use strict". A strict eval gets a
/// fresh variable environment - its vars and functions stay inside it - and is
/// exempt from the sloppy var-versus-let conflict checks. Deciding on the
/// caller's strictness alone got both wrong for a sloppy caller evaluating a
/// strict source. Every expected value here is what V8 returns.
/// </summary>
public sealed class DirectEvalStrictSourceTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AStrictSourceVarOverABlockLetIsNotAConflict()
    {
        Assert.Equal(
            "no-throw",
            RunString("function f() { { let x; { eval('\"use strict\"; var x;'); } } return 'no-throw'; } f();"));
    }

    [Fact]
    public void AStrictSourceVarDoesNotLeakIntoTheCaller()
    {
        Assert.Equal(
            "undefined",
            RunString("function f() { eval('\"use strict\"; var leaked = 1;'); return typeof leaked; } f();"));
    }

    [Fact]
    public void AStrictSourceKeepsItsCompletionValueAndItsScope()
    {
        Assert.Equal(
            "5:undefined",
            RunString("function f() { var r = eval('\"use strict\"; var y = 5; y'); return r + ':' + typeof y; } f();"));
    }

    [Fact]
    public void AStrictSourceFunctionDoesNotLeakIntoTheCaller()
    {
        Assert.Equal(
            "undefined",
            RunString("function f() { eval('\"use strict\"; function g() { return 1; }'); return typeof g; } f();"));
    }

    [Fact]
    public void ASloppySourceVarStillLeaksIntoTheCaller()
    {
        Assert.Equal(
            "number:3",
            RunString("function f() { eval('var s = 3;'); return typeof s + ':' + s; } f();"));
    }

    [Fact]
    public void ASloppySourceVarOverABlockLetIsStillASyntaxError()
    {
        Assert.Equal(
            "SyntaxError",
            RunString(
                "function f() { { let z;" +
                "  try { eval('var z;'); return 'no-throw'; } catch (e) { return e.constructor.name; } } } f();"));
    }
}
