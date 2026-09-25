using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A read cached several prototypes up (ECMA-262 10.1.8.1 OrdinaryGet walks
// [[Prototype]] until it finds the name) must notice anything that would make
// the walk end elsewhere. Each case warms the site, then changes the chain.
public sealed class DeepPrototypeLoadTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Chain = @"
        class A { who() { return 'A'; } }
        class B extends A {}
        class C extends B {}
        var c = new C();
        function call(o) { return o.who(); }
        for (var i = 0; i < 100; i++) call(c);";

    [Fact]
    public void AMethodThreeLinksUpIsFound()
    {
        Assert.Equal("A,false", Run(Chain + " call(c) + ',' + c.hasOwnProperty('who');"));
    }

    [Fact]
    public void AMethodAddedToAnIntermediatePrototypeShadowsTheCachedOne()
    {
        Assert.Equal("B", Run(Chain + " B.prototype.who = function () { return 'B'; }; call(c);"));
    }

    [Fact]
    public void ARewiredChainIsFollowed()
    {
        Assert.Equal("D", Run(Chain + " Object.setPrototypeOf(B.prototype, { who: function () { return 'D'; } }); call(c);"));
    }

    [Fact]
    public void AHolderPropertyTurnedIntoAnAccessorRunsTheGetter()
    {
        Assert.Equal("getter", Run(Chain + @"
            Object.defineProperty(A.prototype, 'who', { get: function () { return function () { return 'getter'; }; }, configurable: true });
            call(c);"));
    }

    [Fact]
    public void ADeletedIntermediatePropertyUncoversTheHolder()
    {
        Assert.Equal("B,A", Run(@"
            class A { who() { return 'A'; } }
            class B extends A { who() { return 'B'; } }
            class C extends B {}
            var c = new C();
            function call(o) { return o.who(); }
            var first = '';
            for (var i = 0; i < 100; i++) first = call(c);
            delete B.prototype.who;
            first + ',' + call(c);"));
    }

    [Fact]
    public void HasOwnPropertyOnAClassInstanceIsFoundOnObjectPrototype()
    {
        Assert.Equal("true", Run(@"
            class K { constructor() { this.v = 1; } }
            var k = new K(), ok = true;
            for (var i = 0; i < 100; i++) ok = ok && k.hasOwnProperty('v') && !k.hasOwnProperty('w');
            String(ok);"));
    }
}
