using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// The register-window loop must produce what the dispatch loop produces.
/// </summary>
/// <remarks>
/// <para>
/// test262 is where that is checked at scale, through
/// <c>scripts/interp2_ab.sh</c>, and it is where the interesting failures have
/// come from. These are the cases worth pinning here as well: the ones that
/// exercise the new loop's own machinery rather than the language, and which a
/// slice would only catch by accident.
/// </para>
/// <para>
/// The programs run twice in the same process, once on each loop, and the two
/// answers are compared. Flipping the engine per test rather than per process
/// means the default <c>dotnet test</c> run covers both, with no environment
/// variable to remember.
/// </para>
/// </remarks>
[Collection(nameof(Interpreter2ParityTests))]
[CollectionDefinition(nameof(Interpreter2ParityTests), DisableParallelization = true)]
public sealed class Interpreter2ParityTests
{
    [Theory]
    // Where a frame's variables live: registers, and the window's clear.
    [InlineData("function f(a, b) { var c = a + b; return c; } f(2, 3);", "5")]
    [InlineData("function f(a) { var u; return String(u) + ':' + a; } f(1);", "undefined:1")]
    // Sloppy mode lets formals repeat a name; the last one wins even unsupplied.
    [InlineData("function f(x, a, b, x) { return x; } String(f(1, 2));", "undefined")]
    [InlineData("function f(x, a, b, x) { return x; } String(f(1, 2, 3, 4));", "4")]
    // A closure that reads nothing of the enclosing frame keeps it in registers.
    [InlineData("function f() { var n = 1; var g = function () { return 7; }; return g() + n; } f();", "8")]
    // A closure that does read one moves that variable, and only that one.
    [InlineData("function counter() { var n = 0; var other = 5; return function () { return ++n + other; } }" +
                "var c = counter(); String(c()) + ',' + String(c());", "6,7")]
    // Two activations must not share a captured binding.
    [InlineData("function counter() { var n = 0; return function () { return ++n; } }" +
                "var a = counter(), b = counter(); String(a()) + ',' + String(a()) + ',' + String(b());", "1,2,1")]
    // A capture two levels down still reaches through.
    [InlineData("function o() { var v = 7; function m() { function i() { return v; } return i(); } return m(); } o();", "7")]
    // Writing a captured variable from the closure is visible to the frame.
    [InlineData("function f() { var n = 1; var g = function () { n = 9; }; g(); return n; } f();", "9")]
    // Arrows have no receiver of their own and read the enclosing one.
    [InlineData("var o = { v: 3, m: function () { var a = () => this.v; return a(); } }; o.m();", "3")]
    [InlineData("var o = { v: 3, m: function () { var a = () => () => this.v; return a()(); } }; o.m();", "3")]
    // An ordinary nested function does not; it gets its own.
    [InlineData("var o = { m: function () { var f = function () { return typeof this; }; return f(); } }; o.m();",
                "object")]
    // The scope-chain cache must notice the global it resolved to changing.
    [InlineData("var g = 1; function r() { return g; } var a = r(); g = 2; String(a) + ',' + String(r());", "1,2")]
    // ... and must not answer for a name that is not there.
    [InlineData("function r() { try { return missingGlobalName; } catch (e) { return e.constructor.name; } } r();",
                "ReferenceError")]
    // A free identifier two frames out, read repeatedly through the cache.
    [InlineData("function outer() { var v = 4; function inner() { var t = 0; for (var i = 0; i < 3; i++) t += v;" +
                " return t; } return inner(); } outer();", "12")]
    // The value stack has to survive a call chain deeper than its initial size.
    [InlineData("function rec(n) { return n === 0 ? 0 : 1 + rec(n - 1); } rec(15);", "15")]
    // A getter re-enters the loop between the read and the store of its result.
    [InlineData("var o = { get p() { return side(); } }; function side() { return 11; } o.p;", "11")]
    // typeof on an identifier answers for one that resolves nowhere.
    [InlineData("function f() { return typeof notDeclaredAnywhere; } f();", "undefined")]
    [InlineData("function f() { var declared = 1; return typeof declared; } f();", "number")]
    // new, throw and property writes.
    [InlineData("function P(v) { this.v = v; } function f() { return new P(6).v; } f();", "6")]
    [InlineData("function f() { throw new TypeError('x'); } try { f(); } catch (e) { e.constructor.name }",
                "TypeError")]
    [InlineData("function f() { var a = []; a[0] = 1; a[1] = 2; a.n = 3; return a[0] + a[1] + a.n; } f();", "6")]
    // try / catch / finally, and the block scope a catch binding needs.
    [InlineData("function f() { try { throw 1; } catch (e) { return 'c' + e; } } f();", "c1")]
    [InlineData("function f() { var s = ''; try { s += 'a'; } finally { s += 'b'; } return s; } f();", "ab")]
    [InlineData("function f() { var s = ''; try { throw 1; } catch (e) { s += 'c'; } finally { s += 'f'; }" +
                " return s; } f();", "cf")]
    // An exception crossing a finally with no catch is re-raised when it ends.
    [InlineData("var s = ''; function f() { try { throw 'x'; } finally { s += 'f'; } }" +
                "try { f(); } catch (e) { s += e; } s;", "fx")]
    // A throw two frames down finds the handler two frames up.
    [InlineData("function deep() { throw 'd'; } function mid() { deep(); }" +
                "function f() { try { mid(); } catch (e) { return 'caught:' + e; } } f();", "caught:d")]
    // A throw out of a native callback still finds it.
    [InlineData("function f() { try { [1].forEach(function () { throw 'n'; }); } catch (e) { return e; } } f();",
                "n")]
    // Nested try blocks unwind one level at a time.
    [InlineData("function f() { var s = ''; try { try { throw 1; } finally { s += 'i'; } }" +
                " catch (e) { s += 'o'; } return s; } f();", "io")]
    // A catch binding is a block scope, and a return from inside it still works.
    [InlineData("function f() { try { throw 5; } catch (e) { var d = e * 2; return d; } } f();", "10")]
    // The catch binding must not leak past the block.
    [InlineData("var e = 'outer'; function f() { try { throw 'inner'; } catch (e) {} return e; } f();", "outer")]
    // A handler left open by a return must not catch the caller's throw.
    [InlineData("function g() { try { return 1; } catch (e) { return 'wrong'; } }" +
                "function f() { g(); throw 'up'; } try { f(); } catch (e) { e }", "up")]
    // const inside a block: initialising is allowed, assigning is not.
    [InlineData("function f() { { const c = 1; return c; } } f();", "1")]
    [InlineData("function f() { try { { const c = 1; c = 2; } } catch (e) { return e.constructor.name; } } f();",
                "TypeError")]
    // A named function expression can call itself by its own name.
    [InlineData("var f = function fact(n) { return n <= 1 ? 1 : n * fact(n - 1); }; f(5);", "120")]
    // The name is not visible outside it.
    [InlineData("var f = function named() { return 1; }; typeof named;", "undefined")]
    // Assigning to it is dropped in sloppy code and a TypeError in strict code.
    [InlineData("var f = function n() { n = 1; return typeof n; }; f();", "function")]
    [InlineData("var f = function n() { 'use strict'; try { n = 1; } catch (e) " +
                "{ return e.constructor.name; } return 'no-throw'; }; f();", "TypeError")]
    // A parameter of the same name shadows it.
    [InlineData("var f = function n(n) { return n; }; f(7);", "7")]
    // A closure inside it can read it.
    [InlineData("var f = function outer() { var g = function () { return typeof outer; }; return g(); }; f();",
                "function")]
    // Regex literals.
    [InlineData("function f() { return /a(b+)c/.exec('xabbbc')[1]; } f();", "bbb")]
    [InlineData("function f() { var r = /x/g; return r.source + ':' + r.flags; } f();", "x:g")]
    // A method is an ordinary body with a home object, and the home object only
    // matters to `super` - which the loop still declines.
    [InlineData("var o = { v: 4, m() { return this.v + 1; } }; o.m();", "5")]
    [InlineData("class C { m(a) { return a * 2; } } new C().m(21);", "42")]
    [InlineData("var o = { m() { var f = () => this; return f() === o; } }; String(o.m());", "true")]
    [InlineData("var o = { m() { return arguments.length + ':' + arguments[1]; } }; o.m(7, 8, 9);", "3:8")]
    [InlineData("class C { static m() { return this === C; } } String(C.m());", "true")]
    // A method that reaches for super stays on the old loop, and still works.
    [InlineData("class A { m() { return 'a'; } } class B extends A { m() { return super.m() + 'b'; } }" +
                "new B().m();", "ab")]
    // Private fields: the brand is the whole of the access control.
    [InlineData("class C { #x = 5; read() { return this.#x; } } new C().read();", "5")]
    [InlineData("class C { #x = 1; bump() { this.#x += 2; return this.#x; } } new C().bump();", "3")]
    [InlineData("class C { #x = 1; static peek(o) { return o.#x; } } C.peek(new C());", "1")]
    // Reading one off an object of another class is a TypeError, not undefined.
    [InlineData("class C { #x = 1; read(o) { return this.#x; } }" +
                "class D { #x = 2; }" +
                "function t() { try { return C.prototype.read.call(new D()); }" +
                "catch (e) { return e.constructor.name; } } String(t());", "TypeError")]
    [InlineData("class C { #x = 1; read(o) { return o.#x; } }" +
                "function t() { try { return new C().read({}); }" +
                "catch (e) { return e.constructor.name; } } String(t());", "TypeError")]
    [InlineData("class C { #x = 1; read(o) { return o.#x; } }" +
                "function t() { try { return new C().read(7); }" +
                "catch (e) { return e.constructor.name; } } String(t());", "TypeError")]
    public void BothLoopsAgree(string source, string expected)
    {
        var onOldLoop = RunOn(engine2: false, source);
        var onNewLoop = RunOn(engine2: true, source);

        Assert.Equal(expected, onOldLoop);
        Assert.Equal(onOldLoop, onNewLoop);
    }

    private static string RunOn(bool engine2, string source)
    {
        var previous = Interp2Options.Enabled;
        Interp2Options.Enabled = engine2;
        try
        {
            var function = new BytecodeCompiler().CompileScript(new SourceText(source));
            new BytecodeVerifier().Verify(function);
            var interpreter = new BytecodeInterpreter();
            return Describe(interpreter, interpreter.Execute(function));
        }
        finally
        {
            Interp2Options.Enabled = previous;
        }
    }

    private static string Describe(BytecodeInterpreter interpreter, Runtime.JsValue value) => value.Tag switch
    {
        Runtime.JsValueTag.Undefined => "undefined",
        Runtime.JsValueTag.Null => "null",
        Runtime.JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
        Runtime.JsValueTag.Int32 or Runtime.JsValueTag.Number =>
            value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
        Runtime.JsValueTag.String => value.AsString(),
        _ => value.Tag.ToString(),
    };
}
