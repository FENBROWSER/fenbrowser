using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A throw raised inside the engine's own helpers has to reach the handler in
/// the frame that caused it, exactly like one written as `throw` in source.
/// </summary>
public sealed class ThrowRoutingTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void AssigningANamedPropertyOfNullIsCatchable()
    {
        // ECMA-262 6.2.5.5 PutValue: ToObject on the base is a TypeError, and a
        // catchable one. It used to escape the frame entirely.
        Assert.Equal(
            "caught:true",
            Run("function f(a) { try { a.p = 1; return 'no-throw'; } " +
                "catch (e) { return 'caught:' + (e instanceof TypeError); } } f(null);").AsString());
    }

    [Fact]
    public void AssigningAnElementOfUndefinedIsCatchable()
    {
        Assert.Equal(
            "caught:true",
            Run("function f(a) { try { a[0] = 1; return 'no-throw'; } " +
                "catch (e) { return 'caught:' + (e instanceof TypeError); } } f(undefined);").AsString());
    }

    [Fact]
    public void TheMessageNamesTheKindAndTheProperty()
    {
        Assert.Equal(
            "Cannot set properties of null (setting 'p').",
            Run("function f(a) { try { a.p = 1; return ''; } catch (e) { return e.message; } } f(null);").AsString());
    }

    [Fact]
    public void ATypeErrorFromAnOperatorReachesTheHandler()
    {
        // Raised inside the multiplication helper, not by a `throw` statement.
        Assert.Equal(
            "tCz",
            Run("function f(x) { var out = ''; try { out += 't'; var v = x * 10n; out += 'N'; } " +
                "catch (e) { out += 'C'; } return out + 'z'; } f(3);").AsString());
    }

    [Fact]
    public void TheRestOfTheTryBlockDoesNotRun()
    {
        // The bug this guards: the handler ran but so did everything after the
        // throwing instruction.
        Assert.Equal(
            "a|C",
            Run("function f(o) { var out = 'a'; try { o.x.y = 1; out += '|N'; } " +
                "catch (e) { out += '|C'; } return out; } f({});").AsString());
    }

    [Fact]
    public void AnUncaughtOneStillLeavesTheFrame()
    {
        Assert.Throws<JsThrownException>(() => Run("function f(a) { a.p = 1; } f(null);"));
    }
}
