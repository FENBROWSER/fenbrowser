using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 13.3.7.1 SuperCall: after BindThisValue the call itself runs
// InitializeInstanceElements, so a derived class's fields exist the moment
// super() returns - not at the end of the statement holding the call. Minified
// code writes `super(),this.#x=v` (TanStack Query's FocusManager does), and
// running the initializers after that statement reset #x to undefined.
public sealed class DerivedClassFieldInitializationTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void APrivateFieldAssignedAfterSuperInTheSameExpressionKeepsItsValue()
    {
        Assert.Equal("5", Run(@"
            class Base {}
            class K extends Base { #n; constructor() { super(), this.#n = 5; } get n() { return this.#n; } }
            String(new K().n);"));
    }

    [Fact]
    public void APublicFieldAssignedAfterSuperInTheSameExpressionKeepsItsValue()
    {
        Assert.Equal("6", Run(@"
            class Base {}
            class K extends Base { n = 1; constructor() { super(), this.n = 6; } }
            String(new K().n);"));
    }

    [Fact]
    public void FieldsAreInitializedFromASuperCallInsideABranch()
    {
        Assert.Equal("1,2", Run(@"
            class Base {}
            class K extends Base { a = 1; #p = 2; constructor(x) { if (x) { super(); } else { super(); } } get p() { return this.#p; } }
            var k = new K(true);
            k.a + ',' + k.p;"));
    }

    [Fact]
    public void FieldsAreInitializedOnceAndSeeTheBaseInstance()
    {
        Assert.Equal("1,9", Run(@"
            var count = 0;
            class Base { constructor(v) { this.b = v; } }
            class K extends Base { a = ++count; d = this.b + 1; constructor() { var r = super(8); } }
            var k = new K();
            count + ',' + k.d;"));
    }

    [Fact]
    public void FieldsAreInitializedWhenSuperIsCalledFromAnArrow()
    {
        Assert.Equal("1,7", Run(@"
            class Base { constructor(v) { this.b = v; } }
            class K extends Base { a = 1; constructor() { var f = () => super(7); f(); } }
            var k = new K();
            k.a + ',' + k.b;"));
    }

    [Fact]
    public void ASubscribableSubclassSeesTheSetupItStoredBesideSuper()
    {
        // The shape of TanStack Query's focus manager, as x.com ships it.
        Assert.Equal("function", Run(@"
            var g = class { constructor() { this.listeners = new Set, this.subscribe = this.subscribe.bind(this) }
                subscribe(e) { return this.listeners.add(e), this.onSubscribe(), () => {} } onSubscribe() {} };
            var seen;
            var m = new class extends g { #t; #n;
                constructor() { super(), this.#n = e => () => {} }
                onSubscribe() { this.#t || this.setEventListener(this.#n) }
                setEventListener(e) { seen = typeof e; this.#n = e, this.#t?.(), this.#t = e(e => {}) } };
            m.subscribe(() => {});
            seen;"));
    }
}
