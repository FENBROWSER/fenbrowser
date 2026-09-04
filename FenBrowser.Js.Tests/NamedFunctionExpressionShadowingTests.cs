using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 15.2.5: a named function expression binds its own name in a
/// dedicated funcEnv, and each call's environment is a child of it - so a
/// parameter or a body declaration of the same name shadows it legally.
/// Binding the name directly in the call environment turned those declarations
/// into "Cannot declare lexical binding" / "Assignment to constant variable",
/// which is what GitHub's minified ES modules hit.
/// </summary>
public class NamedFunctionExpressionShadowingTests
{
    private static double RunNumber(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void LetDeclarationShadowsFunctionExpressionName()
    {
        Assert.Equal(1d, RunNumber("var f = function b() { let b = 1; return b; }; f();"));
    }

    [Fact]
    public void ConstDeclarationShadowsFunctionExpressionName()
    {
        Assert.Equal(2d, RunNumber("var f = function b() { const b = 2; return b; }; f();"));
    }

    [Fact]
    public void InnerFunctionDeclarationShadowsFunctionExpressionName()
    {
        Assert.Equal(3d, RunNumber(
            "var f = function b() { function b() { return 3; } return b(); }; f();"));
    }

    [Fact]
    public void VarDeclarationShadowsFunctionExpressionName()
    {
        Assert.Equal(4d, RunNumber("var f = function b() { var b = 4; return b; }; f();"));
    }

    [Fact]
    public void ShadowingLetBindingIsAssignable()
    {
        Assert.Equal(6d, RunNumber("var f = function c() { let c = 5; c = 6; return c; }; f();"));
    }

    [Fact]
    public void ClassDeclarationShadowsFunctionExpressionName()
    {
        Assert.Equal("function", RunString(
            "var f = function b() { class b {} return typeof b; }; f();"));
    }

    [Fact]
    public void ParameterShadowsFunctionExpressionName()
    {
        Assert.Equal(11d, RunNumber("var f = function b(b) { return b; }; f(11);"));
    }

    // The name binding must still exist when nothing shadows it.
    [Fact]
    public void SelfReferenceStillResolvesForRecursion()
    {
        Assert.Equal(120d, RunNumber(
            "var f = function fact(n) { return n <= 1 ? 1 : n * fact(n - 1); }; f(5);"));
    }

    [Fact]
    public void UnshadowedNameIsVisibleInsideTheBody()
    {
        Assert.Equal("function", RunString("var f = function b() { return typeof b; }; f();"));
    }

    [Fact]
    public void UnshadowedNameRemainsImmutableInStrictMode()
    {
        Assert.Equal("TypeError", RunString(
            "var f = function b() { 'use strict';" +
            "  try { b = 1; return 'no-throw'; } catch (e) { return e.name; } };" +
            "f();"));
    }

    [Fact]
    public void NameIsNotVisibleOutsideTheExpression()
    {
        Assert.Equal("undefined", RunString(
            "var f = function b() { return 1; }; typeof b;"));
    }
}
