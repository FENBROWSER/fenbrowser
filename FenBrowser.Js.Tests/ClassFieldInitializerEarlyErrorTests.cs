using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 15.7.1 (Class Definitions, Static Semantics: Early Errors):
/// a FieldDefinition Initializer may not contain <c>arguments</c>,
/// <c>super</c> or <c>new.target</c> — but a nested non-arrow function
/// introduces its own scope, where all three become legal again.
/// </summary>
public sealed class ClassFieldInitializerEarlyErrorTests
{
    [Theory]
    [InlineData("class C { f = arguments; }")]
    [InlineData("class C { f = arguments.length; }")]
    [InlineData("class C { f = [arguments]; }")]
    [InlineData("class C { f = { x: arguments }; }")]
    [InlineData("class C { f = (0, arguments); }")]
    [InlineData("class C { f = () => arguments; }")]
    [InlineData("class C { f = x => y => arguments; }")]
    [InlineData("class C extends Base { f = new.target; }")]
    [InlineData("class C extends Base { f = super(); }")]
    public void RejectsForbiddenSyntaxDirectlyInFieldInitializer(string source)
    {
        Assert.Throws<JsParserException>(() => Compile(source));
    }

    // A function expression used as the initializer is a scope boundary, so the
    // enclosing field-initializer restriction must not leak into its body.
    // Regression: this rejected the whole script, which failed real-world bundles.
    [Theory]
    [InlineData("class C { f = function () { return arguments; }; }")]
    [InlineData("class C { f = function named() { return arguments; }; }")]
    [InlineData("class C { f = function () { return arguments.length; }; }")]
    [InlineData("class C { f = function () { if (1) { return arguments; } }; }")]
    [InlineData("class C { f = function () { try { return arguments; } catch (e) {} }; }")]
    [InlineData("class C { f = function () { for (;;) { return arguments; } }; }")]
    [InlineData("class C { f = function () { var a = { q: arguments }; return a; }; }")]
    [InlineData("class C { f = function* () { yield arguments; }; }")]
    [InlineData("class C { f = async function () { return arguments; }; }")]
    [InlineData("class C extends Base { f = function () { return new.target; }; }")]
    [InlineData("class C { f = { m() { return arguments; } }; }")]
    [InlineData("class C { f = class { m() { return arguments; } }; }")]
    [InlineData("class C { f = [function () { return arguments; }]; }")]
    [InlineData("class C { f = (function () { return arguments; })(); }")]
    public void AllowsForbiddenSyntaxInsideNestedFunctionScope(string source)
    {
        Assert.NotNull(Compile(source));
    }

    // Methods are their own scope: the restriction never applied to them.
    [Theory]
    [InlineData("class C { m() { return arguments; } }")]
    [InlineData("class C { static m() { return arguments; } }")]
    [InlineData("class C extends Base { m() { return new.target; } }")]
    public void AllowsForbiddenSyntaxInsideMethods(string source)
    {
        Assert.NotNull(Compile(source));
    }

    // The class-field early errors live in AstValidator, which runs from the
    // compiler rather than the parser, so drive the full compile.
    private static object Compile(string source) =>
        new BytecodeCompiler().CompileScript(new SourceText(source));
}
