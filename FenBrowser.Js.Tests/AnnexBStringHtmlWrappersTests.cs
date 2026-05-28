using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Annex B B.2.2 — String.prototype HTML wrapper methods (audit gap §4.1).
// Spec is purely lexical: each wraps `this` in an HTML tag, escaping `"` in
// attribute values.
public class AnnexBStringHtmlWrappersTests
{
    private static string Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact] public void Anchor()    => Assert.Equal("<a name=\"x\">hi</a>", Run("'hi'.anchor('x');"));
    [Fact] public void Link()      => Assert.Equal("<a href=\"u\">hi</a>", Run("'hi'.link('u');"));
    [Fact] public void Fontcolor() => Assert.Equal("<font color=\"red\">hi</font>", Run("'hi'.fontcolor('red');"));
    [Fact] public void Fontsize()  => Assert.Equal("<font size=\"3\">hi</font>", Run("'hi'.fontsize(3);"));
    [Fact] public void Big()       => Assert.Equal("<big>hi</big>", Run("'hi'.big();"));
    [Fact] public void Blink()     => Assert.Equal("<blink>hi</blink>", Run("'hi'.blink();"));
    [Fact] public void Bold()      => Assert.Equal("<b>hi</b>", Run("'hi'.bold();"));
    [Fact] public void Fixed()     => Assert.Equal("<tt>hi</tt>", Run("'hi'.fixed();"));
    [Fact] public void Italics()   => Assert.Equal("<i>hi</i>", Run("'hi'.italics();"));
    [Fact] public void Small()     => Assert.Equal("<small>hi</small>", Run("'hi'.small();"));
    [Fact] public void Strike()    => Assert.Equal("<strike>hi</strike>", Run("'hi'.strike();"));
    [Fact] public void Sub()       => Assert.Equal("<sub>hi</sub>", Run("'hi'.sub();"));
    [Fact] public void Sup()       => Assert.Equal("<sup>hi</sup>", Run("'hi'.sup();"));

    [Fact] public void AnchorEscapesDoubleQuote()
        => Assert.Equal("<a name=\"&quot;\">x</a>", Run("'x'.anchor('\"');"));
}
