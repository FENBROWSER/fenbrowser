using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A getter or setter found on the receiver or its chain is cached per
// instruction (AccessorStub) and called directly. Each case warms the site,
// then changes something the full [[Get]] / [[Set]] would have seen.
public sealed class AccessorStubTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Warm = @"
        class A { constructor() { this._v = 1; } get v() { return this._v; } set v(x) { this._v = x * 10; } }
        class B extends A {}
        var b = new B();
        function read(o) { return o.v; }
        function write(o, x) { o.v = x; }
        for (var i = 0; i < 50; i++) { read(b); write(b, i); }";

    [Fact]
    public void GettersAndSettersRunWithTheReceiverAsThis()
    {
        Assert.Equal("70,70", Run(Warm + " write(b, 7); read(b) + ',' + b._v;"));
    }

    [Fact]
    public void AGetterRedefinedInPlaceIsTheOneCalled()
    {
        Assert.Equal("redefined", Run(Warm + @"
            Object.defineProperty(A.prototype, 'v', { get: function () { return 'redefined'; }, configurable: true });
            read(b);"));
    }

    [Fact]
    public void AGetterReplacedByADataPropertyIsRead()
    {
        Assert.Equal("data", Run(Warm + @"
            Object.defineProperty(A.prototype, 'v', { value: 'data', writable: true, configurable: true });
            read(b);"));
    }

    [Fact]
    public void AnOwnPropertyShadowsTheInheritedAccessor()
    {
        Assert.Equal("own,own-set", Run(Warm + @"
            Object.defineProperty(b, 'v', { value: 'own', writable: true, configurable: true });
            var r = read(b);
            write(b, 'own-set');
            r + ',' + b.v;"));
    }

    [Fact]
    public void ASwappedPrototypeIsFollowed()
    {
        Assert.Equal("other", Run(Warm + @"
            Object.setPrototypeOf(B.prototype, { get v() { return 'other'; } });
            read(b);"));
    }

    [Fact]
    public void AThrowingGetterIsCaught()
    {
        Assert.Equal("caught:boom", Run(Warm + @"
            Object.defineProperty(A.prototype, 'v', { get: function () { throw new Error('boom'); }, configurable: true });
            var r;
            try { read(b); r = 'no error'; } catch (e) { r = 'caught:' + e.message; }
            r;"));
    }

    [Fact]
    public void AStrictWriteToAGetterOnlyPropertyStillThrows()
    {
        Assert.Equal("TypeError", Run(@"
            var o = { get g() { return 1; } };
            function w(x) { 'use strict'; x.g = 2; }
            var name;
            for (var i = 0; i < 20; i++) { try { w(o); } catch (e) { name = e.constructor.name; } }
            name;"));
    }

    [Fact]
    public void NativeGettersAreCalled()
    {
        Assert.Equal("3", Run(@"
            var m = new Map([[1, 1], [2, 2]]);
            function size(x) { return x.size; }
            for (var i = 0; i < 20; i++) size(m);
            m.set(3, 3);
            String(size(m));"));
    }
}
