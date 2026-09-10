using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 14.2.1 Block early errors: no LexicallyDeclaredName of a block may
/// also be one of its VarDeclaredNames, and a function declared in a block is
/// lexical there. Annex B.3.2.4 forgives sloppy code only duplicate function
/// declarations, never a var of the same name. Every expected value here is
/// what V8 reports for the same source.
/// </summary>
public sealed class BlockFunctionVarRedeclarationTests
{
    private static string Outcome(string source)
    {
        var escaped = source.Replace("\\", "\\\\").Replace("'", "\\'");
        var program = "var outcome; try { eval('" + escaped + "'); outcome = 'ok'; } catch (e) { outcome = e.constructor.name; } outcome;";
        var function = new BytecodeCompiler().CompileScript(new SourceText(program));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Theory]
    // test262 language/block-scope/syntax/redeclaration/var-redeclaration-attempt-after-function.js
    [InlineData("{ function f() {} var f; }")]
    [InlineData("{ var f; function f() {} }")]
    // test262 language/block-scope/syntax/redeclaration/inner-block-var-name-redeclaration-attempt-with-function.js
    [InlineData("{ { var f; } function f() {} }")]
    [InlineData("{ async function f() {} var f; }")]
    [InlineData("{ function* f() {} var f; }")]
    [InlineData("{ l: function f() {} var f; }")]
    [InlineData("'use strict'; { function f() {} var f; }")]
    [InlineData("function outer() { { function f() {} var f; } }")]
    public void AVarNamedLikeABlocksFunctionIsASyntaxError(string source)
    {
        Assert.Equal("SyntaxError", Outcome(source));
    }

    [Theory]
    [InlineData("function f() {} var f;")]
    [InlineData("{ function f() {} } var f;")]
    [InlineData("{ function f() {} function f() {} }")]
    [InlineData("for (var f;;) { function f() {} break; }")]
    [InlineData("{ if (1) function f() {} var f; }")]
    public void ThisIsNotABlockCollision(string source)
    {
        Assert.Equal("ok", Outcome(source));
    }
}
