using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A sloppy direct eval may declare `arguments` in a function body, where it
/// shares the arguments binding as V8 lets it; only an eval in a non-arrow
/// function's parameter list may not, which is what test262 asserts. Every
/// expected value here is what V8 returns, or what the test262 case named
/// beside it asserts.
/// </summary>
public sealed class DirectEvalArgumentsDeclarationTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void ABodyEvalMayDeclareArguments()
    {
        Assert.Equal(
            "no-throw:1",
            RunString("(function f() { try { eval('var arguments = 1'); return 'no-throw:' + arguments; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void ABodyEvalMayDeclareArgumentsBesideAParameter()
    {
        Assert.Equal(
            "no-throw:1",
            RunString("(function f(a) { try { eval('var arguments = 1'); return 'no-throw:' + arguments; } catch (e) { return e.constructor.name; } })(0)"));
    }

    [Fact]
    public void ABodyEvalMayDeclareAFunctionNamedArguments()
    {
        Assert.Equal(
            "no-throw:function",
            RunString("(function f() { try { eval('function arguments() {}'); return 'no-throw:' + typeof arguments; } catch (e) { return e.constructor.name; } })()"));
    }

    [Fact]
    public void AParameterEvalMayNotDeclareArguments()
    {
        // test262 language/eval-code/direct/func-decl-no-pre-existing-arguments-bindings-are-present-declare-arguments.js
        Assert.Equal(
            "SyntaxError",
            RunString("(function () { function f(p = eval('var arguments')) {} try { f(); return 'no-throw'; } catch (e) { return e.constructor.name; } })()"));
    }
}
