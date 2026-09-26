using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 14.2.3 BlockDeclarationInstantiation creates a block's let binding
/// with CreateMutableBinding(dn, false): not deletable. A sloppy `delete x` on
/// one - or on a catch parameter, which is bound the same way - must return
/// false and leave the binding in place. Every expected value here is what V8
/// returns for the same source.
/// </summary>
/// <remarks>
/// The eval cases pin the other thing deletability was standing in for: a
/// direct eval's var may not share a name with a block's let (a SyntaxError),
/// except with an identifier catch parameter, which Annex B.3.4 allows - and
/// not with a destructured one.
/// </remarks>
public sealed class BlockBindingDeletabilityTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void DeletingACatchParameterKeepsIt()
    {
        Assert.Equal(
            "false/7",
            RunString(
                "function f() { try { throw 7; } catch (caught) {" +
                "  var r = delete caught; var a;" +
                "  try { a = caught; } catch (e) { a = 'threw:' + e.constructor.name; }" +
                "  return r + '/' + a; } } f();"));
    }

    [Fact]
    public void DeletingABlockLetKeepsIt()
    {
        Assert.Equal(
            "false/1",
            RunString(
                "function f() { { let b = 1; var r = delete b; var a;" +
                "  try { a = b; } catch (e) { a = 'threw:' + e.constructor.name; }" +
                "  return r + '/' + a; } } f();"));
    }

    [Fact]
    public void DeletingALoopLetKeepsItAcrossIterations()
    {
        Assert.Equal(
            "false:3,false:4,",
            RunString(
                "function f() { var out = '';" +
                "  for (let i = 3; i < 5; i++) { out += (delete i) + ':' + i + ','; }" +
                "  return out; } f();"));
    }

    [Fact]
    public void DeletingADestructuredCatchBindingKeepsIt()
    {
        Assert.Equal(
            "false/5",
            RunString(
                "function f() { try { throw { d: 5 }; } catch ({ d }) {" +
                "  var r = delete d; return r + '/' + d; } } f();"));
    }

    [Fact]
    public void DeletingABlockConstKeepsIt()
    {
        Assert.Equal(
            "false/2",
            RunString("function f() { { const c = 2; var r = delete c; return r + '/' + c; } } f();"));
    }

    [Fact]
    public void DeletingAParameterIsFalse()
    {
        Assert.Equal("false", RunString("function f(q) { return String(delete q); } f(1);"));
    }

    [Fact]
    public void DeletingAConfigurableGlobalPropertyStillWorks()
    {
        Assert.Equal(
            "true:undefined",
            RunString("function f() { globalThis.gp = 1; return (delete gp) + ':' + typeof gp; } f();"));
    }

    [Fact]
    public void EvalVarOverABlockLetIsASyntaxError()
    {
        Assert.Equal(
            "SyntaxError",
            RunString(
                "function f() { { let x = 1;" +
                "  try { eval('var x = 2'); return 'no-throw'; } catch (e) { return e.constructor.name; } } } f();"));
    }

    [Fact]
    public void EvalVarOverAnIdentifierCatchParameterIsAllowed()
    {
        // Annex B.3.4: the initializer assigns the catch parameter.
        Assert.Equal(
            "2",
            RunString("function f() { try { throw 1; } catch (e) { eval('var e = 2'); return String(e); } } f();"));
    }

    [Fact]
    public void EvalVarOverADestructuredCatchBindingIsASyntaxError()
    {
        Assert.Equal(
            "SyntaxError",
            RunString(
                "function f() { try { throw { p: 1 }; } catch ({ p }) {" +
                "  try { eval('var p = 2'); return 'no-throw:' + p; } catch (err) { return err.constructor.name; } } }" +
                "f();"));
    }

    [Fact]
    public void BlockAndCatchBindingsHoldUpPastTheTierUpThreshold()
    {
        // g loops enough to be compiled and then run compiled, so the JIT's own
        // scope entry - which takes the catch flag as an extra argument - is
        // what runs once it has been.
        Assert.Equal(
            "12497500",
            RunString(
                "function g(i) { for (var k = 0; k < 40; k++) { } try { throw i; } catch (e) { { let b = e; return b; } } }" +
                "var s = 0; for (var i = 0; i < 5000; i++) { s += g(i); } String(s);"));
    }
}
