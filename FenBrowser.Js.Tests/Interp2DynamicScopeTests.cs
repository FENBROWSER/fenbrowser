using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Bodies whose scope changes shape while they run, on the register-window
// loop: `with` (ECMA-262 14.11), direct eval (19.2.1), a body variable
// environment separate from the parameters' (10.2.11 steps 28-30), `delete`
// of an identifier (13.5.1.2) and Annex B.3.3 block functions.
public sealed class Interp2DynamicScopeTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function f(o) { with (o) { return x; } }", true)]
    [InlineData("function f(s) { var a = 1; return eval(s); }", true)]
    [InlineData("function f(a, g = () => a) { var b = 2; return g(); }", true)]
    [InlineData("function f() { var x; return delete x; }", false)]
    [InlineData("function f() { { function g() {} } return g; }", false)]
    public void TheseBodiesRunOnTheRegisterWindow(string declaration, bool dynamicScope)
    {
        var layout = FrameLayout.For(Compile(declaration).NestedFunctions[0]);
        Assert.Equal(Interp2Bailout.None, layout.Bailout);
        Assert.Equal(dynamicScope, layout.DynamicScope);
    }

    [Theory]
    [InlineData("function f(o) { var x = 'local'; with (o) { return x; } } f({ x: 'object' });", "object")]
    [InlineData("function f(o) { var x = 'local'; with (o) { return x; } } f({});", "local")]
    [InlineData("function f(o) { with (o) { x = 2; } return o.x; } String(f({ x: 1 }));", "2")]
    [InlineData("function f(o) { var y = 0; with (o) { y = 5; } return y + ',' + ('y' in o); } f({});", "5,false")]
    // A property the object gains inside the body shadows from then on.
    [InlineData("function f(o) { var x = 'local'; with (o) { var r = x; o.x = 'late'; return r + ',' + x; } } f({});", "local,late")]
    // @@unscopables hides a property from the with.
    [InlineData("function f() { var x = 'local'; var o = { x: 'object', [Symbol.unscopables]: { x: true } }; with (o) { return x; } } f();", "local")]
    // A closure made inside the body keeps the object in its scope.
    [InlineData("function f(o) { with (o) { return function () { return x; }; } } var o = { x: 1 }; var g = f(o); o.x = 7; String(g());", "7")]
    // Leaving by break, return and throw all pop the object.
    [InlineData("function f(o) { var x = 'local'; for (;;) { with (o) { break; } } return x; } f({ x: 'object' });", "local")]
    [InlineData("function f(o) { var x = 'local'; try { with (o) { throw 0; } } catch (e) { return x; } } f({ x: 'object' });", "local")]
    public void WithResolvesNamesAgainstTheObjectFirst(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function f() { with (null) {} } try { f(); 'no error'; } catch (e) { e.constructor.name; }", "TypeError")]
    [InlineData("function f() { with (undefined) {} } try { f(); 'no error'; } catch (e) { e.constructor.name; }", "TypeError")]
    public void WithRejectsNullAndUndefined(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function f() { var a = 1; return eval('a + 1'); } String(f());", "2")]
    [InlineData("function f() { var a = 1; eval('a = 5'); return a; } String(f());", "5")]
    // A sloppy eval's var lands in the calling function and shadows the outer name.
    [InlineData("var x = 'global'; function f() { eval('var x = \"eval\"'); return x; } f() + ',' + x;", "eval,global")]
    [InlineData("function f() { { eval('var inner = 3'); } return inner; } String(f());", "3")]
    // A strict eval keeps its vars.
    [InlineData("function f() { eval('\"use strict\"; var kept = 1'); return typeof kept; } f();", "undefined")]
    [InlineData("function f() { 'use strict'; eval('var kept = 1'); return typeof kept; } f();", "undefined")]
    [InlineData("function f() { return eval('this.tag'); } f.call({ tag: 'receiver' });", "receiver")]
    [InlineData("function f() { return eval('arguments.length'); } String(f(1, 2, 3));", "3")]
    [InlineData("function f() { return eval('new.target') === f; } String(new f() instanceof f);", "true")]
    [InlineData("var f = (a) => eval('a * 2'); String(f(4));", "8")]
    // A closure made by eval code sees the caller's variables.
    [InlineData("function f() { var n = 1; var g = eval('(function () { return n; })'); n = 9; return g(); } String(f());", "9")]
    // eval inside a with sees the object.
    [InlineData("function f(o) { with (o) { return eval('x'); } } String(f({ x: 4 }));", "4")]
    // A function the eval declares is a var of the caller.
    [InlineData("function f() { eval('function made() { return 6; }'); return made(); } String(f());", "6")]
    public void DirectEvalRunsInTheCallersScope(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void AnIndirectEvalIsNotDirect()
    {
        Assert.Equal("global", Run(
            "var x = 'global'; function f() { var x = 'local'; var e = eval; return e('x'); } f();"));
    }

    [Theory]
    [InlineData("function f() { var x = 1; return String(delete x) + x; } f();", "false1")]
    [InlineData("function f() { eval('var x = 1'); var r = delete x; return r + ',' + typeof x; } f();", "true,undefined")]
    [InlineData("function f(o) { with (o) { return delete p; } } var o = { p: 1 }; f(o) + ',' + ('p' in o);", "true,false")]
    [InlineData("globalThis.gone = 1; function f() { return delete gone; } f() + ',' + typeof gone;", "true,undefined")]
    [InlineData("function f() { return delete neverDeclared; } String(f());", "true")]
    public void DeleteOfANameRemovesOnlyDeletableBindings(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    // The default value's closure sees the parameter, not the body's var of the same name.
    [InlineData("function f(a, g = () => a) { var a = 2; return a + ',' + g(); } f(1);", "2,1")]
    // A body var starts as the parameter's value.
    [InlineData("function f(a, g = () => a) { var a; return String(a); } f(3);", "3")]
    [InlineData("function f(a, g = () => typeof b) { var b = 1; return g(); } f();", "undefined")]
    public void TheBodyHasAVariableEnvironmentOfItsOwn(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function f() { { function g() { return 1; } } return g(); } String(f());", "1")]
    [InlineData("function f() { var before = typeof g; { function g() {} } return before + ',' + typeof g; } f();", "undefined,function")]
    [InlineData("function f() { { function g() { return 1; } g = 2; } return typeof g; } f();", "function")]
    public void AnnexBBlockFunctionsReachTheVar(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function* g(o) { with (o) { yield x; yield x + 1; } } [...g({ x: 1 })].join();", "1,2")]
    [InlineData("function* g() { var v = 1; yield eval('v'); eval('v = 5'); yield v; } [...g()].join();", "1,5")]
    public void GeneratorsSuspendInsideTheseScopes(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }
}
