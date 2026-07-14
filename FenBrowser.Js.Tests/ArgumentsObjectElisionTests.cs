using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArgumentsObjectElisionTests
{
    [Fact]
    public void CompilerElidesArgumentsObjectWhenFunctionCannotObserveIt()
    {
        var function = CompileNested("(function add(a, b) { return a + b; })(2, 3);");

        Assert.False(function.HasOwnArgumentsObject);
        Assert.Equal(5, Execute("(function add(a, b) { return a + b; })(2, 3);"));
    }

    [Fact]
    public void CompilerRetainsArgumentsObjectForDirectReference()
    {
        var function = CompileNested("(function f() { return arguments.length; })(1, 2, 3);");

        Assert.True(function.HasOwnArgumentsObject);
        Assert.Equal(3, Execute("(function f() { return arguments.length; })(1, 2, 3);"));
    }

    [Fact]
    public void CompilerRetainsArgumentsObjectForDirectEval()
    {
        var function = CompileNested("(function f() { return eval('arguments[0]'); })(7);");

        Assert.True(function.HasOwnArgumentsObject);
        Assert.Equal(7, Execute("(function f() { return eval('arguments[0]'); })(7);"));
    }

    [Fact]
    public void CompilerRetainsOuterArgumentsObjectCapturedByArrow()
    {
        var function = CompileNested("(function f() { return (() => arguments[0])(); })(9);");

        Assert.True(function.HasOwnArgumentsObject);
        Assert.Equal(9, Execute("(function f() { return (() => arguments[0])(); })(9);"));
    }

    [Fact]
    public void NestedOrdinaryFunctionOwnsItsArgumentsWithoutRetainingOuterObject()
    {
        var outer = CompileNested("(function outer() { function inner() { return arguments.length; } return inner(1, 2); })();");
        var inner = Assert.Single(outer.NestedFunctions);

        Assert.False(outer.HasOwnArgumentsObject);
        Assert.True(inner.HasOwnArgumentsObject);
        Assert.Equal(2, Execute("(function outer() { function inner() { return arguments.length; } return inner(1, 2); })();"));
    }

    private static BytecodeFunction CompileNested(string source)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        return Assert.Single(script.NestedFunctions);
    }

    private static double Execute(string source)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        return new BytecodeInterpreter().Execute(script).AsNumber();
    }
}
