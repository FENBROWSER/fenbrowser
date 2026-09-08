using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 10.2.11 FunctionDeclarationInstantiation runs on every call, so the
/// entry path hoists a function's declared vars in one pass over its slots.
/// These pin the semantics that pass has to keep.
/// </summary>
public sealed class VarHoistingOnCallEntryTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    [Fact]
    public void RedeclaringAParameterAsVarKeepsTheArgument()
    {
        // The slot is already filled by parameter binding, so hoisting must
        // leave it alone rather than overwrite it with undefined.
        Assert.Equal(7d, Run("function f(a) { var a; return a; } f(7);").AsNumber());
    }

    [Fact]
    public void RedeclaringAParameterWithAnInitializerStillAssigns()
    {
        Assert.Equal(5d, Run("function f(a) { var a = 5; return a; } f(7);").AsNumber());
    }

    [Fact]
    public void LaterParametersAreUntouchedByHoisting()
    {
        Assert.Equal(
            "1|2|3",
            Run("function f(a, b, c) { var b; var a; var c; return [a, b, c].join('|'); } f(1, 2, 3);")
                .AsString());
    }

    [Fact]
    public void DeclaredVarsReadAsUndefinedBeforeTheirAssignment()
    {
        Assert.Equal(JsValueTag.Undefined, Run("function f() { return x; var x = 1; } f();").Tag);
    }

    [Fact]
    public void EachCallGetsFreshUndefinedVars()
    {
        // Slot storage is pooled between activations. A second call must not
        // see what the first one left in the slot.
        Assert.Equal(
            "undefined|undefined",
            Run(
                "function f() { var seen = x; x = 'set'; var x; return String(seen); }" +
                "f() + '|' + f();").AsString());
    }

    [Fact]
    public void ManyDeclaredVarsAllHoistToUndefined()
    {
        // Exercises the whole-array pass rather than a one-slot function.
        Assert.Equal(
            0d,
            Run(
                "function f() {" +
                "  var a, b, c, d, e, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u;" +
                "  var count = 0;" +
                "  var all = [a, b, c, d, e, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u];" +
                "  for (var idx = 0; idx < all.length; idx++) { if (all[idx] !== undefined) count++; }" +
                "  return count;" +
                "} f();").AsNumber());
    }

    [Fact]
    public void ArgumentsObjectSurvivesVarHoisting()
    {
        Assert.Equal(2d, Run("function f(a, b) { var q; return arguments.length; } f(1, 2);").AsNumber());
    }

    [Fact]
    public void HoistingDoesNotDisturbAnEnclosingScope()
    {
        Assert.Equal(
            "outer",
            Run("var x = 'outer'; function f() { var y; return 'inner'; } f(); x;").AsString());
    }

    [Fact]
    public void RecursiveCallsKeepTheirOwnVars()
    {
        Assert.Equal(
            6d,
            Run(
                "function fact(n) { var result; if (n <= 1) { result = 1; } else { result = n * fact(n - 1); }" +
                "  return result; } fact(3);").AsNumber());
    }
}
