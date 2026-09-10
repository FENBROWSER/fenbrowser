using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 10.2.1.3 FunctionDeclarationInstantiation steps 28 and 30: a
/// function whose parameters have expressions runs its body in an environment
/// of its own. A closure made in a default value sees the parameters and the
/// scope outside them, never the body's vars; each body var starts as the
/// same-named parameter's value. Every expected value here is what V8 returns,
/// or what the test262 case named beside it asserts.
/// </summary>
public sealed class FunctionBodyScopeTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void AParameterClosureSeesTheScopeOutsideNotTheBodysVar()
    {
        // test262 language/expressions/function/scope-paramsbody-var-open.js
        Assert.Equal(
            "outside:inside",
            RunString(
                "var x = 'outside'; var probeParams, probeBody;" +
                "(function (_ = probeParams = function () { return x; }) { var x = 'inside'; probeBody = function () { return x; }; }());" +
                "probeParams() + ':' + probeBody();"));
    }

    [Fact]
    public void ABodyVarNamedLikeAParameterIsASeparateBinding()
    {
        Assert.Equal("2:1", RunString("(function () { function h(a, q = () => a) { var a = 2; return a + ':' + q(); } return h(1); })()"));
    }

    [Fact]
    public void AnArrowBodyVarNamedLikeAParameterIsASeparateBinding()
    {
        Assert.Equal("2:1", RunString("(function () { const h = (a, q = () => a) => { var a = 2; return a + ':' + q(); }; return h(1); })()"));
    }

    [Fact]
    public void ABodyVarStartsAsTheParametersValue()
    {
        Assert.Equal("1:1", RunString("(function () { function h(a, q = () => a) { var a; return a + ':' + q(); } return h(1); })()"));
    }

    [Fact]
    public void AParameterEvalVarIsShadowedByTheBodysLet()
    {
        Assert.Equal(
            "2:1",
            RunString("(function () { function g(p = eval('var yy = 1'), q = () => yy) { let yy = 2; return yy + ':' + q(); } return g(); })()"));
    }

    [Fact]
    public void AnArrowParameterEvalVarIsShadowedByTheBodysVar()
    {
        Assert.Equal(
            "2:1",
            RunString("(function () { const g = (p = eval('var vv = 1'), q = () => vv) => { var vv = 2; return vv + ':' + q(); }; return g(); })()"));
    }

    [Fact]
    public void ADestructuredParametersBodyVarStartsAsItsValue()
    {
        Assert.Equal(
            "1:2:1",
            RunString("(function () { function h({a}, q = () => a) { var a; var first = a; a = 2; return first + ':' + a + ':' + q(); } return h({a: 1}); })()"));
    }

    [Fact]
    public void AGeneratorBodyHasItsOwnScopeToo()
    {
        Assert.Equal("2:1", RunString("(function () { function* g(a, q = () => a) { var a = 2; yield a + ':' + q(); } return g(1).next().value; })()"));
    }

    [Fact]
    public void ABodyEvalVarIsHiddenFromAParameterClosure()
    {
        Assert.Equal(
            "undefined:number",
            RunString("(function () { function f(a = () => typeof z) { eval('var z = 1'); return a() + ':' + typeof z; } return f(); })()"));
    }

    [Fact]
    public void ABodyBlockFunctionIsHiddenFromAParameterClosure()
    {
        Assert.Equal(
            "undefined:function",
            RunString("(function () { function f(a = () => typeof bf) { { function bf() {} } return a() + ':' + typeof bf; } return f(); })()"));
    }

    [Fact]
    public void ABodyVarNamedArgumentsStartsAsTheArgumentsObject()
    {
        Assert.Equal(
            "object:true",
            RunString("(function () { function f(a = () => arguments) { var arguments; return typeof arguments + ':' + (arguments === a()); } return f(); })()"));
    }

    [Fact]
    public void ABodyFunctionShadowsTheParameterForTheBodyOnly()
    {
        Assert.Equal(
            "function:1",
            RunString("(function () { function f(a, q = () => a) { function a() {} return typeof a + ':' + q(); } return f(1); })()"));
    }

    [Fact]
    public void AnArrowParameterEvalIgnoresTheBodysLet()
    {
        // test262 language/eval-code/direct/arrow-fn-body-cntns-arguments-lex-bind-arrow-func-declare-arguments-assign.js
        Assert.Equal(
            "local",
            RunString("(() => { const f = (p = eval(\"var arguments = 1\")) => { let arguments = 'local'; return arguments; }; return f(); })()"));
    }
}
