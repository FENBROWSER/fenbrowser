using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// An add-property store replays a [[Set]] that created a property (ECMA-262
// 10.1.9.2 OrdinarySetWithOwnDescriptor) only while the prototype chain cannot
// have gained a setter or a read-only property of that name. Every case warms
// the stub first, then changes something the full [[Set]] would have seen.
public sealed class AddPropertyStubTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Warm = "function P(v) { this.x = v; this.y = v; } for (var i = 0; i < 100; i++) new P(i);";

    [Fact]
    public void AddedPropertiesKeepOrderAndAttributes()
    {
        Assert.Equal("x,y|true,true,true", Run(Warm + @"
            var p = new P(1);
            var d = Object.getOwnPropertyDescriptor(p, 'y');
            Object.keys(p).join() + '|' + [d.writable, d.enumerable, d.configurable].join();"));
    }

    [Fact]
    public void ASetterAddedToThePrototypeRuns()
    {
        Assert.Equal("set:7,false", Run(Warm + @"
            var seen = '';
            Object.defineProperty(P.prototype, 'y', { set: function (v) { seen = 'set:' + v; }, configurable: true });
            var p = new P(7);
            seen + ',' + p.hasOwnProperty('y');"));
    }

    [Fact]
    public void ASetterAddedToObjectPrototypeRuns()
    {
        Assert.Equal("set:3", Run(Warm + @"
            var seen = '';
            Object.defineProperty(Object.prototype, 'x', { set: function (v) { seen = 'set:' + v; }, configurable: true });
            new P(3);
            delete Object.prototype.x;
            seen;"));
    }

    [Fact]
    public void AReadOnlyInheritedPropertyBlocksTheAdd()
    {
        Assert.Equal("TypeError,false", Run(@"
            function S(v) { 'use strict'; this.x = v; }
            for (var i = 0; i < 100; i++) new S(i);
            Object.defineProperty(S.prototype, 'x', { value: 0, writable: false, configurable: true });
            var name;
            try { new S(1); name = 'no error'; } catch (e) { name = e.constructor.name; }
            var sloppy = {};
            function Q(v) { this.x = v; }
            for (var j = 0; j < 100; j++) new Q(j);
            Object.defineProperty(Q.prototype, 'x', { value: 0, writable: false, configurable: true });
            name + ',' + new Q(1).hasOwnProperty('x');"));
    }

    [Fact]
    public void ANonExtensibleReceiverRefusesTheAdd()
    {
        Assert.Equal("TypeError", Run(@"
            function add(o) { 'use strict'; o.z = 1; }
            for (var i = 0; i < 100; i++) add({});
            var sealed = Object.preventExtensions({});
            var name;
            try { add(sealed); name = 'no error'; } catch (e) { name = e.constructor.name; }
            name;"));
    }

    [Fact]
    public void ASwappedPrototypeWithASetterIsSeen()
    {
        Assert.Equal("proto:5", Run(@"
            function add(o, v) { o.w = v; }
            for (var i = 0; i < 100; i++) add({}, i);
            var seen = '';
            var target = Object.create({ set w(v) { seen = 'proto:' + v; } });
            add(target, 5);
            seen;"));
    }

    [Fact]
    public void AReAddedPrototypePropertyInADeletedSlotIsSeen()
    {
        // Re-adding a deleted property reuses its slot without a new shape, so a
        // prototype that ever had the name must never be trusted by a stub.
        Assert.Equal("reused:9", Run(@"
            function R(v) { this.q = v; }
            R.prototype.q = 0;
            delete R.prototype.q;
            for (var i = 0; i < 100; i++) new R(i);
            var seen = '';
            Object.defineProperty(R.prototype, 'q', { set: function (v) { seen = 'reused:' + v; }, configurable: true });
            new R(9);
            seen;"));
    }
}
