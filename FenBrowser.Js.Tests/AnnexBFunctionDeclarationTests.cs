using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AnnexBFunctionDeclarationTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    [Fact]
    public void DirectEvalBlockFunctionMayUpdateParameterBinding()
    {
        Assert.True(Run(@"
            var init, after;
            (function (f) {
                eval('init = f; { function f() {} } after = f;');
            }(123));
            init === 123 && typeof after === 'function';
        ").AsBoolean());
    }

    [Fact]
    public void BlockFunctionNamedArgumentsDoesNotReplaceArgumentsObject()
    {
        Assert.True(Run(@"
            (function () {
                var before = arguments;
                {
                    function arguments() {}
                    if (typeof arguments !== 'function') return false;
                }
                return arguments === before;
            }());
        ").AsBoolean());
    }

    [Fact]
    public void DuplicateBlockFunctionUsesLastDeclaration()
    {
        Assert.Equal(2, Run(@"
            var result;
            { function value() { return 1; } function value() { return 2; } result = value(); }
            result;
        ").AsNumber());
    }

    [Fact]
    public void NestedBlockFunctionDoesNotReplaceEligibleOuterFunction()
    {
        Assert.Equal(1, Run(@"
            function invoke() {
                { function value() { return 1; } { function value() { return 2; } } }
                return value();
            }
            invoke();
        ").AsNumber());
    }

    [Fact]
    public void CatchVarInitializerUpdatesOnlyCatchBinding()
    {
        Assert.True(Run(@"
            var value = 'outer';
            try { throw 1; } catch (value) { var value = 'catch'; }
            value === 'outer';
        ").AsBoolean());
    }

    [Fact]
    public void LegacyOctalEscapeDecodesInsideTemplateSubstitution()
    {
        Assert.Equal(7, Run("`${'\\07'}`.charCodeAt(0);").AsNumber());
    }
}
