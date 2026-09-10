using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 15.7.14 ClassDefinitionEvaluation: a class body runs in an
/// environment of its own holding an immutable binding for the class name -
/// a declaration's as much as a named expression's. Its methods, heritage
/// expression, static blocks and static fields see the class through that
/// binding, whatever happens to the name outside, and none of them may assign
/// to it. Every expected value here is what V8 returns, or what the test262
/// case named beside it asserts.
/// </summary>
public sealed class ClassNameBindingTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AMethodSeesTheClassAfterTheOuterBindingChanges()
    {
        // test262 language/statements/class/scope-name-lex-close.js
        Assert.Equal("true", RunString("class C { m() { return C; } } var cls = C; C = null; String(cls.prototype.m() === cls)"));
    }

    [Fact]
    public void AMethodMayNotAssignTheClassName()
    {
        // test262 language/statements/class/scope-name-lex-open-no-heritage.js
        Assert.Equal(
            "TypeError",
            RunString("(function () { class D { m() { D = null; } } var d = new D(); try { d.m(); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AHeritageExpressionSeesTheInnerBinding()
    {
        // test262 language/statements/class/scope-name-lex-open-heritage.js
        Assert.Equal(
            "true",
            RunString("(function () { var probeHeritage; class G extends (probeHeritage = function () { return G; }, Object) {} var cls = G; G = null; return String(probeHeritage() === cls); })()"));
    }

    [Fact]
    public void ANamedClassExpressionStillBindsItsName()
    {
        Assert.Equal("true", RunString("var K = class C2 { m() { return C2; } }; String(new K().m() === K)"));
    }

    [Fact]
    public void TheDeclarationStillBindsTheNameOutside()
    {
        Assert.Equal("function", RunString("class E {} typeof E"));
    }

    [Fact]
    public void ABlockScopedClassBindsBothWays()
    {
        Assert.Equal("true", RunString("(function () { { class F { m() { return F; } } return String(new F().m() === F); } })()"));
    }

    [Fact]
    public void AStaticBlockSeesTheClass()
    {
        Assert.Equal("true", RunString("(function () { var seen; class H { static { seen = H; } } return String(seen === H); })()"));
    }

    [Fact]
    public void AStaticFieldSeesTheClass()
    {
        Assert.Equal("true", RunString("(function () { class I { static self = I; } return String(I.self === I); })()"));
    }

    [Fact]
    public void ADeclarationIsStillInTheTemporalDeadZoneBeforeItRuns()
    {
        Assert.Equal(
            "ReferenceError",
            RunString("(function () { try { new J(); return 'no-throw'; } catch (e) { return e.constructor.name; } class J {} })()"));
    }
}
