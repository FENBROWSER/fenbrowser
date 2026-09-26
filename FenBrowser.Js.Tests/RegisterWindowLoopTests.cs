using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Programs that exercise the register-window loop's own machinery rather than
/// the language, and which a test262 slice would only catch by accident.
/// </summary>
/// <remarks>
/// Each expected value is the answer the old dispatch loop gave while the two
/// loops ran side by side; the old loop is gone, the answers are not.
/// </remarks>
public sealed class RegisterWindowLoopTests
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
    // A method that reaches for super.
    [InlineData("class A { m() { return 'a'; } } class B extends A { m() { return super.m() + 'b'; } }" +
                "new B().m();", "ab")]
    // Computed keys, spread and tagged templates.
    [InlineData("function f() { var k = 'a'; var o = { [k]: 1, b: 2 }; return o.a + o.b; } f();", "3")]
    [InlineData("function f() { var k = '__proto__'; var o = { [k]: 1 };" +
                "return Object.getPrototypeOf(o) === Object.prototype ? 'own:' + o.__proto__ : 'reparented'; } f();",
                "own:1")]
    [InlineData("function f() { var m = { ['x']() { return 7; } }; return m.x(); } f();", "7")]
    [InlineData("function f() { var a = [1, 2], b = [0, ...a, 3, ...a]; return b.join(','); } f();", "0,1,2,3,1,2")]
    [InlineData("function f() { function g(a, b, c) { return a + ':' + b + ':' + c; }" +
                "var args = [2, 3]; return g(1, ...args); } f();", "1:2:3")]
    [InlineData("function f() { var s = { a: 1, b: 2 }; var o = { ...s, c: 3 };" +
                "return Object.keys(o).join(',') + '=' + o.a + o.b + o.c; } f();", "a,b,c=123")]
    [InlineData("function f() { function t(strings, v) { return strings.length + '|' + strings[0] + '|' + v; }" +
                "return t`sep${9}end`; } f();", "2|sep|9")]

    // ECMA-262 13.2.5.5: an object literal creates its properties rather than
    // assigning them, so an inherited setter must not run and an own property
    // has to exist afterwards.
    [InlineData("function f() { Object.defineProperty(Object.prototype, 'z', " +
                "{ set: function () { throw new Error('setter ran'); }, " +
                "get: function () { return 'inherited'; }, configurable: true });" +
                "try { var o = { z: 5 };" +
                "return (Object.getOwnPropertyDescriptor(o, 'z') ? 'own' : 'inherited') + ':' + o.z; }" +
                "finally { delete Object.prototype.z; } } f();", "own:5")]

    // `super` resolves against the method's home object, which is the frame's
    // callee - so it needs no environment record on this loop.
    [InlineData("class A { get v() { return 7; } } class B extends A { m() { return super['v'] + 1; } }" +
                "String(new B().m());", "8")]
    [InlineData("var base = { greet() { return 'hi'; } };" +
                "var o = { __proto__: base, greet() { return super.greet() + '!'; } }; o.greet();", "hi!")]
    [InlineData("class A { who() { return this.name; } }" +
                "class B extends A { constructor() { super(); this.name = 'b'; } m() { return super.who(); } }" +
                "new B().m();", "b")]
    // An arrow carries no home object of its own; it finds the enclosing
    // method's.
    [InlineData("class A { m() { return 'a'; } }" +
                "class B extends A { m() { var f = () => super.m(); return f() + 'b'; } } new B().m();", "ab")]
    // A throw from `super[k]` - its ToPropertyKey, or a getter on the base -
    // has to reach the `try` around it rather than leave the loop.
    [InlineData("var bad = { toString: function () { throw new RangeError('key'); } };" +
                "var o = { m() { try { return super[bad]; } catch (e) { return 'caught:' + e.message; } } };" +
                "o.m();", "caught:key")]
    [InlineData("class A { get g() { throw new TypeError('getter'); } }" +
                "class B extends A { m() { try { return super.g; } catch (e) { return 'caught:' + e.message; } } }" +
                "new B().m();", "caught:getter")]

    // Accessors and methods, defined by the body that evaluates the class or the
    // literal - so each case runs inside f().
    [InlineData("function f() { var o = { get v() { return this._v; }, set v(x) { this._v = x * 2; } };" +
                "o.v = 4; return o.v + ':' + Object.getOwnPropertyDescriptor(o, 'v').get.name; } f();", "8:get v")]
    [InlineData("function f() { class C { get a() { return 1; } } var o = { get b() { return 2; } };" +
                "return Object.keys(o).join() + '|' + Object.getOwnPropertyDescriptor(C.prototype, 'a').enumerable; }" +
                "f();", "b|false")]
    [InlineData("function f() { class C { m() {} } var d = Object.getOwnPropertyDescriptor(C.prototype, 'm');" +
                "return d.enumerable + ':' + d.writable + ':' + C.prototype.m.name; } f();", "false:true:m")]
    [InlineData("function f() { var s = Symbol('tag'); class C { [s]() { return 1; } }" +
                "return C.prototype[s].name + ':' + new C()[s](); } f();", "[tag]:1")]
    [InlineData("function f() { var k = 'g'; class C { get [k]() { return 9; } }" +
                "var o = { set [k](v) {} };" +
                "return new C().g + ':' + Object.getOwnPropertyDescriptor(o, 'g').set.name; } f();", "9:set g")]
    [InlineData("function f() { class C { static s() { return 3; } get ['x' + 1]() { return 4; } }" +
                "return C.s() + new C().x1; } f();", "7")]
    // A computed key's toString is user code and runs exactly once.
    [InlineData("function f() { var n = 0; var k = { toString: function () { n++; return 'q'; } };" +
                "class C { [k]() {} } return n + ':' + typeof C.prototype.q; } f();", "1:function")]

    // A closure over a block-scoped binding. The enclosing body must not keep
    // that binding in a register nothing outside the frame can reach - the
    // closure resolves the name outwards and finds a ReferenceError, or an
    // unrelated binding of the same name that answers with the wrong value.
    [InlineData("function f() { var o = []; for (const v of ['a', 'b', 'c']) { o.push(function () { return v; }); }" +
                "return o.map(function (g) { return g(); }).join(','); } f();", "a,b,c")]
    [InlineData("function f() { var o = []; for (let i = 0; i < 3; i++) { o.push(function () { return i; }); }" +
                "return o.map(function (g) { return g(); }).join(','); } f();", "0,1,2")]
    [InlineData("function f() { var o = []; var xs = ['p', 'q'];" +
                "for (var k = 0; k < xs.length; k++) { const c = xs[k]; o.push(function () { return c; }); }" +
                "return o.map(function (g) { return g(); }).join(','); } f();", "p,q")]
    [InlineData("function f() { var o = []; for (const k in { a: 1, b: 2 }) { o.push(() => k); }" +
                "return o.map(function (g) { return g(); }).join(','); } f();", "a,b")]
    // An outer binding of the same name must not be what the closure finds.
    [InlineData("var v = 'outer';" +
                "function f() { var o = []; for (const v of ['inner']) { o.push(function () { return v; }); }" +
                "return o[0](); } f();", "inner")]

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

    // A generator suspends with its window intact: the frame lives on the
    // generator object between resumes, which is the machinery the loop needed
    // for a body that does not run to completion in one go.
    [InlineData("function* g() { yield 1; yield 2; } [...g()].join(',');", "1,2")]
    // The value .next() is given is what the yield evaluates to.
    [InlineData("function* g() { var x = yield 1; return x * 2; }" +
                "var it = g(); it.next(); String(it.next(21).value);", "42")]
    // Parameters are bound when the generator is made, not on the first next().
    [InlineData("var made = 0; function* g(a = ++made) { yield a; }" +
                "var it = g(); String(made) + ':' + String(it.next().value);", "1:1")]
    // Its own variables survive the suspension, and so does a closure over one.
    [InlineData("function* g() { var n = 0; var bump = () => ++n; yield bump(); yield bump(); }" +
                "[...g()].join(',');", "1,2")]
    // A let of its own is in the dead zone across a yield that precedes it.
    [InlineData("function* g() { yield typeof globalThis; let v = 2; yield v; } [...g()].join(',');",
                "object,2")]
    // .return() and .throw() at a suspension leave the body at once.
    [InlineData("function* g() { yield 1; yield 2; } var it = g(); it.next();" +
                "var r = it.return(9); String(r.value) + ':' + String(r.done);", "9:true")]
    [InlineData("function* g() { yield 1; } var it = g(); it.next();" +
                "function t() { try { it.throw(new Error('boom')); return 'no-throw'; }" +
                "catch (e) { return e.message; } } t();", "boom")]
    // ... including into one that has not started.
    [InlineData("function* g() { yield 1; } var it = g();" +
                "function t() { try { it.throw(new Error('early')); return 'no-throw'; }" +
                "catch (e) { return e.message; } } t();", "early")]
    // The receiver a generator was called with is still there after a resume.
    [InlineData("function* g() { yield this.v; yield this.v; }" +
                "var it = g.call({ v: 3 }); String(it.next().value) + ',' + String(it.next().value);", "3,3")]
    // Two generators from one function do not share a frame.
    [InlineData("function* g() { var n = 0; yield ++n; yield ++n; }" +
                "var a = g(), b = g(); String(a.next().value) + ',' + String(b.next().value) +" +
                "',' + String(a.next().value);", "1,1,2")]
    // A try open across a yield travels with the window, and .return() runs the
    // finally blocks that cover the yield - a catch never sees one.
    [InlineData("function* g() { try { yield 1; } finally { } } [...g()].join(',');", "1")]
    [InlineData("var log = []; function* g() { try { yield 1; } finally { log.push('fin'); } }" +
                "var it = g(); it.next(); it.return(7); log.join(',');", "fin")]
    [InlineData("function* g() { try { yield 1; } catch (e) { yield 'caught:' + e.message; } }" +
                "var it = g(); it.next(); it.throw(new Error('x')).value;", "caught:x")]
    [InlineData("function* g() { try { yield 1; } finally { yield 'fin'; } }" +
                "var it = g(); it.next(); var r = it.return(5); r.value + ':' + String(r.done);", "fin:false")]
    [InlineData("var log = []; function* g() { try { try { yield 1; } finally { log.push('inner'); } }" +
                "finally { log.push('outer'); } } var it = g(); it.next(); it.return(0); log.join(',');",
                "inner,outer")]
    [InlineData("function* g() { try { yield 1; } finally { return 9; } }" +
                "var it = g(); it.next(); var r = it.next(); String(r.value) + ':' + String(r.done);", "9:true")]
    // yield* over an array.
    [InlineData("function* g() { yield* [1, 2]; } [...g()].join(',');", "1,2")]
    public void RunsAsTheOldLoopDid(string source, string expected)
        => Assert.Equal(expected, Run(source));

    /// <summary>
    /// The same comparison for a body that suspends at an await. An async
    /// function's effects land after the script's completion value has been
    /// taken, so each case runs its program, lets the job queue drain, and then
    /// reads what the program left behind.
    /// </summary>
    /// <remarks>
    /// Every expectation here was taken from node before it was written down.
    /// </remarks>
    [Theory]
    // An async body runs to its first await synchronously and the rest of it
    // after the caller has carried on.
    [InlineData("var log = []; async function f() { log.push('a'); await 0; log.push('b'); } f(); log.push('c');",
                "log.join(',');", "a,c,b")]
    // What the body returns settles the promise the call handed back.
    [InlineData("var out; async function f() { return 7; } f().then(function (v) { out = v; });",
                "String(out);", "7")]
    [InlineData("var out; async function f() { await 0; return 'v'; } f().then(function (v) { out = 'then:' + v; });",
                "out;", "then:v")]
    // What the awaited promise settles with is what the await evaluates to.
    [InlineData("var out; async function f() { var v = await Promise.resolve(5); out = v * 2; } f();",
                "String(out);", "10")]
    [InlineData("var out; async function f() { var a = await 1, b = await 2; out = a + b; } f();",
                "String(out);", "3")]
    [InlineData("var out; async function f() { return await Promise.resolve(3); }" +
                "f().then(function (v) { out = v + 1; });", "String(out);", "4")]
    // A rejection rejects the promise, and a throw before any await does too.
    [InlineData("var out; async function f() { await Promise.reject(new Error('boom')); }" +
                "f().catch(function (e) { out = e.message; });", "out;", "boom")]
    [InlineData("var out; async function f() { throw new Error('sync'); }" +
                "f().catch(function (e) { out = e.message; });", "out;", "sync")]
    // A try open across an await comes back to the same handlers, so the
    // rejection arrives as a throw at the await itself.
    [InlineData("var out; async function f() { try { await Promise.reject('r'); } catch (e) { out = 'caught:' + e; } }" +
                "f();", "out;", "caught:r")]
    [InlineData("var log = []; async function f() { try { await 0; log.push('t'); } finally { log.push('f'); } } f();",
                "log.join(',');", "t,f")]
    [InlineData("var log = []; async function f() { try { try { await 0; throw 'x'; } finally { log.push('i'); } }" +
                "catch (e) { log.push('c' + e); } } f();", "log.join(',');", "i,cx")]
    // A finally that awaits still lets the return it is holding through.
    [InlineData("var out; async function f() { try { return 1; } finally { await 0; } }" +
                "f().then(function (v) { out = 'r' + v; });", "out;", "r1")]
    [InlineData("var out; async function f() { var s = 0; try { await 0; throw 'e'; } catch (x) { s = 1; }" +
                "finally { await 0; s += 10; } out = s; } f();", "String(out);", "11")]
    // The frame survives the suspension: its variables, a closure over one, its
    // receiver, its arguments object and a let still in the dead zone.
    [InlineData("var out; async function f() { var n = 1; await 0; n += 2; await 0; out = n; } f();",
                "String(out);", "3")]
    [InlineData("var out; async function f() { var n = 0; var bump = function () { return ++n; };" +
                "await 0; bump(); await 0; bump(); out = n; } f();", "String(out);", "2")]
    [InlineData("var out; async function f() { await 0; out = this.v; } f.call({ v: 3 });", "String(out);", "3")]
    [InlineData("var out; async function f() { await 0; out = arguments.length + ':' + arguments[1]; } f(7, 8, 9);",
                "out;", "3:8")]
    [InlineData("var out; async function f() { await 0; out = typeof globalThis; let v = 2; out += ',' + v; } f();",
                "out;", "object,2")]
    [InlineData("var out; async function f() { const c = 1; await 0; out = c; } f();", "String(out);", "1")]
    // Two activations of one body do not share a frame.
    [InlineData("var out = []; async function f(n) { await 0; out.push(n); await 0; out.push(n * 10); } f(1); f(2);",
                "out.join(',');", "1,2,10,20")]
    // An await inside a loop resumes back into the loop.
    [InlineData("var out; async function f() { var t = 0; for (var i = 0; i < 3; i++) { t += await i; } out = t; } f();",
                "String(out);", "3")]
    [InlineData("var out; async function f() { var r = []; for (var i = 0; i < 2; i++)" +
                "{ try { await i; r.push('t' + i); } catch (e) { r.push('c'); } } out = r.join(','); } f();",
                "out;", "t0,t1")]
    // A block binding a closure captures needs a fresh one per turn, across the
    // await as well as before it.
    [InlineData("var out; async function f() { var o = []; for (const v of ['a', 'b'])" +
                "{ await 0; o.push(function () { return v; }); }" +
                "out = o.map(function (g) { return g(); }).join(','); } f();", "out;", "a,b")]
    // The other shapes an async body comes in.
    [InlineData("var out; var f = async function () { await 0; out = 42; }; f();", "String(out);", "42")]
    [InlineData("var out; var o = { v: 5, m: async function () { await 0; out = this.v; } }; o.m();",
                "String(out);", "5")]
    [InlineData("var out; function outer() { var v = 9; async function inner() { await 0; out = v; } inner(); }" +
                "outer();", "String(out);", "9")]
    // Ordering against another async body and against a plain promise reaction,
    // which is what says the resumption is a job and not a call.
    [InlineData("var log = []; async function f() { log.push('f1'); await 0; log.push('f2'); }" +
                "async function g() { log.push('g1'); await f(); log.push('g2'); } g(); log.push('top');",
                "log.join(',');", "g1,f1,top,f2,g2")]
    [InlineData("var log = []; Promise.resolve().then(function () { log.push('p'); });" +
                "(async function () { log.push('a'); await 0; log.push('b'); })();", "log.join(',');", "a,p,b")]
    // An async generator is both machines at once - it suspends at a yield like
    // a generator and its results are promises - and it is run as
    // a generator whose results are wrapped in resolved promises. So an await
    // inside one has no activation to suspend into, and stays inline.
    [InlineData("var out; async function* g() { yield 1; yield 2; }" +
                "g().next().then(function (r) { out = r.value + ':' + r.done; });", "out;", "1:false")]
    [InlineData("var out; async function* g() { var v = await Promise.resolve(5); yield v * 2; }" +
                "g().next().then(function (r) { out = String(r.value); });", "out;", "10")]
    // Its own variables survive the suspension, as a sync generator's do, and
    // the value next() is given is what the yield evaluates to.
    [InlineData("var out; async function* g() { var n = 0; yield ++n; yield ++n; } var it = g(); var r = [];" +
                "it.next().then(function (a) { r.push(a.value); return it.next(); })" +
                ".then(function (b) { r.push(b.value); out = r.join(','); });", "out;", "1,2")]
    [InlineData("var out; async function* g() { var x = yield 1; out = 'got:' + x; } var it = g();" +
                "it.next().then(function () { return it.next('s'); });", "out;", "got:s")]
    [InlineData("var out; async function* g() { yield 1; } var it = g();" +
                "it.next().then(function () { return it.next(); })" +
                ".then(function (r) { out = String(r.value) + ':' + r.done; });", "out;", "undefined:true")]
    // A finally covering the yield runs when .return() ends the delegation.
    [InlineData("var out = []; async function* g() { try { yield 1; } finally { out.push('fin'); } } var it = g();" +
                "it.next().then(function () { return it.return(9); })" +
                ".then(function (r) { out.push(r.value + ':' + r.done); });", "out.join(',');", "fin,9:true")]
    // Its receiver and its parameters are bound when it is called.
    [InlineData("var out; async function* g() { yield this.v; }" +
                "g.call({ v: 7 }).next().then(function (a) { out = String(a.value); });", "out;", "7")]
    [InlineData("var out; async function* g(a, b) { yield a + b; }" +
                "g(3, 4).next().then(function (r) { out = String(r.value); });", "out;", "7")]
    // A throw before the first yield rejects the promise next() handed back.
    [InlineData("var out; async function* g() { throw new Error('ag'); }" +
                "g().next().catch(function (e) { out = e.message; });", "out;", "ag")]
    public void RunsAsTheOldLoopDidAfterTheJobQueueDrains(string source, string reader, string expected)
        => Assert.Equal(expected, RunThenRead(source, reader));

    /// <summary>
    /// Run a program, then read what it left behind. <c>Execute</c> drains the
    /// job queue before it returns, so the second script sees every await
    /// resumption the first one queued.
    /// </summary>
    private static string RunThenRead(string source, string reader)
    {
        var interpreter = new BytecodeInterpreter();
        Execute(interpreter, source);
        return Describe(interpreter, Execute(interpreter, reader));
    }

    private static Runtime.JsValue Execute(BytecodeInterpreter interpreter, string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return interpreter.Execute(function);
    }

    private static string Run(string source)
    {
        var interpreter = new BytecodeInterpreter();
        return Describe(interpreter, Execute(interpreter, source));
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
