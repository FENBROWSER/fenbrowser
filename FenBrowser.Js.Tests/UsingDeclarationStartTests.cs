using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 14.3.1: a using declaration starts only when a BindingIdentifier
/// follows `using` - or `await using` - with no line break anywhere in that
/// head; otherwise `using` is an identifier. And its bindings may not be named
/// let. Every expected value here is whether V8 parses the same source.
/// </summary>
public sealed class UsingDeclarationStartTests
{
    private static string Outcome(string source)
    {
        var escaped = source.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");
        var program = "var outcome; try { eval('" + escaped + "'); outcome = 'ok'; } catch (e) { outcome = e.constructor.name; } outcome;";
        var function = new BytecodeCompiler().CompileScript(new SourceText(program));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Theory]
    // test262 language/statements/await-using/syntax/await-using-declaring-let-split-across-two-lines.js
    [InlineData("(async function () { await using\n let = 'x'; var using, let; })")]
    [InlineData("(async function () { { await using\n x = null; } })")]
    [InlineData("(function () { { using\n x = null; } })")]
    [InlineData("(function () { { using [a] = [1]; } })")]
    [InlineData("(function () { { using x = null; } })")]
    [InlineData("(function () { { using x\n = null; } })")]
    [InlineData("(async function () { { await using x = null; } })")]
    public void ThisParses(string source)
    {
        Assert.Equal("ok", Outcome(source));
    }

    [Theory]
    [InlineData("(function () { { using let = null; } })")]
    [InlineData("(async function () { { await using let = null; } })")]
    [InlineData("(async function () { { await\n using x = null; } })")]
    [InlineData("(async function () { { await using [a] = [1]; } })")]
    public void ThisIsASyntaxError(string source)
    {
        Assert.Equal("SyntaxError", Outcome(source));
    }
}
