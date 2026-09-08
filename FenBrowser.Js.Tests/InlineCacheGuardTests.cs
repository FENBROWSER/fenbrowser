using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Compiled code runs a cache program's guards itself rather than calling
// something that performs them. Every guard has to fail closed to the same miss
// helper the interpreter uses, so what these pin is the set of receivers the
// inline guard must decline: a shape it does not cover, an accessor, a proxy, a
// slot that stopped being writable, and a property that was deleted.
public class InlineCacheGuardTests
{
    private const int TierUp = 2000;

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AWarmedLoadSite_StillReadsASecondShape()
    {
        var v = Run($@"
            function read(o) {{ return o.x; }}
            var first = {{ x: 1 }};
            for (var i = 0; i < {TierUp}; i++) read(first);
            var second = {{ other: 0, x: 2 }};
            read(first) + ',' + read(second) + ',' + read({{ x: 3 }});
        ");
        Assert.Equal("1,2,3", v);
    }

    [Fact]
    public void AWarmedLoadSite_StillRunsAnAccessor()
    {
        var v = Run($@"
            function read(o) {{ return o.x; }}
            var plain = {{ x: 1 }};
            for (var i = 0; i < {TierUp}; i++) read(plain);
            var calls = 0;
            var accessor = {{ get x() {{ calls++; return 9; }} }};
            read(accessor) + ',' + calls;
        ");
        Assert.Equal("9,1", v);
    }

    [Fact]
    public void AWarmedLoadSite_StillRoutesThroughAProxyTrap()
    {
        var v = Run($@"
            function read(o) {{ return o.x; }}
            var plain = {{ x: 1 }};
            for (var i = 0; i < {TierUp}; i++) read(plain);
            var trapped = 0;
            var proxy = new Proxy(plain, {{ get: function (t, k) {{ trapped++; return 'trap:' + k; }} }});
            read(proxy) + ',' + trapped;
        ");
        Assert.Equal("trap:x,1", v);
    }

    [Fact]
    public void AWarmedLoadSite_NoticesTheSlotBeingDeleted()
    {
        var v = Run($@"
            function read(o) {{ return o.x; }}
            var o = {{ x: 1 }};
            for (var i = 0; i < {TierUp}; i++) read(o);
            delete o.x;
            String(read(o));
        ");
        Assert.Equal("undefined", v);
    }

    [Fact]
    public void AWarmedLoadSite_ReadsThroughToThePrototype()
    {
        var v = Run($@"
            function read(o) {{ return o.x; }}
            var own = {{ x: 1 }};
            for (var i = 0; i < {TierUp}; i++) read(own);
            var inherited = Object.create({{ x: 'from-proto' }});
            String(read(inherited));
        ");
        Assert.Equal("from-proto", v);
    }

    [Fact]
    public void AWarmedStoreSite_RefusesANonWritableSlot()
    {
        var v = Run($@"
            function write(o, v) {{ o.x = v; }}
            var open = {{ x: 0 }};
            for (var i = 0; i < {TierUp}; i++) write(open, i);
            var locked = {{}};
            Object.defineProperty(locked, 'x', {{ value: 'kept', writable: false, configurable: true }});
            write(locked, 'ignored');
            open.x + ',' + locked.x;
        ");
        Assert.Equal((TierUp - 1) + ",kept", v);
    }

    [Fact]
    public void AWarmedStoreSite_StillRunsASetter()
    {
        var v = Run($@"
            function write(o, v) {{ o.x = v; }}
            var plain = {{ x: 0 }};
            for (var i = 0; i < {TierUp}; i++) write(plain, i);
            var seen = null;
            var accessor = {{ set x(v) {{ seen = v; }} }};
            write(accessor, 'through-setter');
            String(seen);
        ");
        Assert.Equal("through-setter", v);
    }

    [Fact]
    public void AWarmedStoreSite_StillRoutesThroughAProxyTrap()
    {
        var v = Run($@"
            function write(o, v) {{ o.x = v; }}
            var plain = {{ x: 0 }};
            for (var i = 0; i < {TierUp}; i++) write(plain, i);
            var seen = null;
            var proxy = new Proxy({{}}, {{ set: function (t, k, v) {{ seen = k + '=' + v; return true; }} }});
            write(proxy, 7);
            String(seen);
        ");
        Assert.Equal("x=7", v);
    }

    [Fact]
    public void AStoredObject_StaysReachableThroughTheWrittenSlot()
    {
        // The compiled store writes the slot itself, so it owes the same write
        // barrier the helper took; without it the collector loses the value.
        var v = Run($@"
            function write(o, v) {{ o.held = v; }}
            var box = {{ held: null }};
            for (var i = 0; i < {TierUp}; i++) write(box, {{ n: i }});
            var churn = null;
            for (var j = 0; j < 20000; j++) churn = {{ pad: j }};
            String(box.held.n + (churn.pad > 0 ? 0 : 0));
        ");
        Assert.Equal((TierUp - 1).ToString(), v);
    }

    [Fact]
    public void AssigningAFunctionPrototype_KeepsItsBookkeeping()
    {
        // "prototype" is the one key whose store carries work beyond the write,
        // and the compiled body decides that at compile time.
        var v = Run($@"
            function assign(f, p) {{ f.prototype = p; }}
            function Target() {{}}
            for (var i = 0; i < {TierUp}; i++) assign(Target, {{ tag: i }});
            var instance = new Target();
            Target.prototype.tag + ',' + (Object.getPrototypeOf(instance) === Target.prototype);
        ");
        Assert.Equal((TierUp - 1) + ",true", v);
    }
}
