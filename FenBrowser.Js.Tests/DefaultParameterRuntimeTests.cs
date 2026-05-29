using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.2.1.3 (FunctionDeclarationInstantiation) / IteratorBindingInitialization:
// a parameter's Initializer is evaluated only when the corresponding argument is
// undefined, in left-to-right order, and later defaults may reference earlier ones.
public sealed class DefaultParameterRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void DefaultAppliesWhenArgumentMissing()
    {
        Assert.Equal(6, RunNum("function f(a, b = 5) { return a + b; } f(1);"));
    }

    [Fact]
    public void PassedArgumentOverridesDefault()
    {
        Assert.Equal(3, RunNum("function f(a, b = 5) { return a + b; } f(1, 2);"));
    }

    [Fact]
    public void ExplicitUndefinedTriggersDefault()
    {
        Assert.Equal(9, RunNum("function f(a = 9) { return a; } f(undefined);"));
    }

    [Fact]
    public void NullDoesNotTriggerDefault()
    {
        Assert.Equal(1, RunNum("function f(a = 9) { return a === null ? 1 : 0; } f(null);"));
    }

    [Fact]
    public void DefaultCanReferenceEarlierParameter()
    {
        Assert.Equal(2, RunNum("function f(a, b = a) { return b; } f(2);"));
    }

    [Fact]
    public void MethodDefinitionDefaultApplies()
    {
        Assert.Equal(13, RunNum("var o = { m(x, y = 10) { return x + y; } }; o.m(3);"));
    }

    [Fact]
    public void ArrowFunctionDefaultApplies()
    {
        Assert.Equal(7, RunNum("var g = (a, b = 4) => a + b; g(3);"));
    }

    [Fact]
    public void GeneratorMethodDefaultApplies()
    {
        Assert.Equal(5, RunNum("function* g(a = 5) { yield a; } g().next().value;"));
    }

    [Fact]
    public void DestructuringParameterDefaultApplies()
    {
        Assert.Equal(1, RunNum("function f({ x } = { x: 1 }) { return x; } f();"));
    }
}
