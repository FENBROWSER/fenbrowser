using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 NamedEvaluation: anonymous function/arrow definitions adopt the
// binding/assignment/property name they are bound to.
public sealed class NamedEvaluationTests
{
    private static string Name(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void VarBindingNamesAnonymousFunction()
        => Assert.Equal("f", Name("var f = function(){}; f.name;"));

    [Fact]
    public void LetAndConstBindingNameFunction()
    {
        Assert.Equal("g", Name("let g = function(){}; g.name;"));
        Assert.Equal("h", Name("const h = function(){}; h.name;"));
    }

    [Fact]
    public void VarBindingNamesArrow()
        => Assert.Equal("a", Name("var a = () => {}; a.name;"));

    [Fact]
    public void NamedFunctionExpressionKeepsItsOwnName()
        => Assert.Equal("inner", Name("var f = function inner(){}; f.name;"));

    [Fact]
    public void AssignmentToIdentifierNamesFunction()
        => Assert.Equal("x", Name("var x; x = function(){}; x.name;"));

    [Fact]
    public void MemberAssignmentDoesNotName()
        => Assert.Equal("", Name("var o = {}; o.m = function(){}; o.m.name;"));

    [Fact]
    public void ObjectLiteralDataPropertyNamesFunction()
        => Assert.Equal("m", Name("var o = { m: function(){} }; o.m.name;"));

    [Fact]
    public void ArrayDestructuringDefaultNamesFunction()
        => Assert.Equal("d", Name("var [d = function(){}] = []; d.name;"));

    [Fact]
    public void ObjectDestructuringDefaultNamesFunction()
        => Assert.Equal("p", Name("var {p = () => {}} = {}; p.name;"));

    [Fact]
    public void ObjectDestructuringRenamedTargetDefaultNamesFunction()
        => Assert.Equal("q", Name("var {x: q = function(){}} = {}; q.name;"));

    [Fact]
    public void ConditionalRhsIsNotNamed()
        // The RHS is a ConditionalExpression, not an anonymous function definition,
        // so NamedEvaluation does not apply.
        => Assert.Equal("", Name("var c = true ? function(){} : function(){}; c.name;"));
}
