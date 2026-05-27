using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 20.2.1.1 Function constructor + Annex B.1.3 HTMLLikeComments.
// The Function constructor parameter source is parsed as
// FormalParameters[~Yield, ~Await] in a non-module goal, so Annex B
// HTML-like comments are admitted.
public class FunctionConstructorAnnexBTests
{
    private static (bool ok, string? errType, string? msg) Compile(string src)
    {
        try
        {
            var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
            new BytecodeInterpreter().Execute(fn);
            return (true, null, null);
        }
        catch (FenBrowser.Js.Interpreter.JsThrownException e)
        {
            return (false, "JsThrownException", e.Message);
        }
    }

    [Fact]
    public void FunctionCtor_HtmlOpenComment_InParams_IsAccepted()
    {
        // Function("<!--", "") — `<!--` is SingleLineHTMLOpenComment per
        // Annex B B.1.3, comments-out the rest of the line. Empty params.
        var r = Compile("var f = Function('<!--', ''); typeof f;");
        Assert.True(r.ok, $"expected accept; got {r.errType}: {r.msg}");
    }

    [Fact]
    public void FunctionCtor_HtmlCloseComment_AfterLineTerminator_IsAccepted()
    {
        // Function("\n-->", "") — `-->` after a LineTerminator is
        // SingleLineHTMLCloseComment. Empty params.
        var r = Compile("var f = Function('\\n-->', ''); typeof f;");
        Assert.True(r.ok, $"expected accept; got {r.errType}: {r.msg}");
    }

    [Fact]
    public void FunctionCtor_HtmlCloseComment_AtStartOfSource_ThrowsSyntaxError()
    {
        // Function("-->", "") — `-->` with no preceding LineTerminator is
        // NOT a SingleLineHTMLCloseComment; should throw SyntaxError, not
        // a raw host exception.
        var r = Compile("Function('-->', '');");
        Assert.False(r.ok);
        // Must be a JS-side throw (wrapped in JsThrownException), not an
        // unhandled JsParserException leaking past the runtime.
        Assert.Equal("JsThrownException", r.errType);
    }

    [Fact]
    public void FunctionCtor_NormalParameters_StillWork()
    {
        // Regression: regular identifier params + arithmetic body.
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText("var f = Function('a', 'b', 'return a + b;'); f(2, 3);"));
        var v = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5.0, v.AsNumber());
    }

    [Fact]
    public void FunctionCtor_InvalidIdentifierParam_ThrowsSyntaxError()
    {
        // Regression: junk param name still throws SyntaxError (wrapped),
        // never a raw host exception.
        var r = Compile("Function('1bad', '');");
        Assert.False(r.ok);
        Assert.Equal("JsThrownException", r.errType);
    }
}
