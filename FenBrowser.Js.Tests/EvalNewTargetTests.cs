using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 19.2.1.1 PerformEval steps 5-6: new.target in eval code is only
// allowed when the eval is direct and runs inside a function.
public sealed class EvalNewTargetTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("eval('new.target')")]
    [InlineData("(0, eval)('new.target')")]
    [InlineData("(() => eval('new.target'))()")]
    [InlineData("eval('() => new.target')")]
    [InlineData("(function () { return (0, eval)('new.target'); })()")]
    public void OutsideAFunctionItIsASyntaxError(string expression)
    {
        Assert.Equal("SyntaxError", Run(
            "var name = 'no error'; try { " + expression + "; } catch (e) { name = e.constructor.name; } name;"));
    }

    [Fact]
    public void InsideAFunctionOrANestedFunctionItIsAllowed()
    {
        Assert.Equal("true,undefined,undefined,undefined", Run(@"
            function F() { return eval('new.target'); }
            class C { x = eval('new.target'); }
            [String(new F() === F), String(F()), String(eval('(function () { return new.target; })')()), String(new C().x)].join();"));
    }
}
