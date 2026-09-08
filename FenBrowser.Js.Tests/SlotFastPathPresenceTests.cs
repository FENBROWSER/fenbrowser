using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The slot fast paths -- TryReadOwnSlot, TryWriteOwnSlot and the JIT's inline
// LoadVar/StoreVar guards -- no longer consult the presence array. They rely on
// an absent slot reading as a default Binding, which is uninitialized. These
// pin the cases where that equivalence has to hold, each driven far enough to
// run both interpreted and compiled.
public class SlotFastPathPresenceTests
{
    private const int TierUp = 2000;

    private static double Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void AbsentSlot_ResolvesToOuterScope()
    {
        // `outer` has no slot in inner's numbering, so inner's slot array reads
        // as absent and the lookup has to continue up the scope chain.
        var v = Run($@"
            var outer = 7;
            function inner(n) {{ var local = n; return local + outer; }}
            var acc = 0;
            for (var i = 0; i < {TierUp}; i++) acc = inner(i);
            acc;
        ");
        Assert.Equal(TierUp - 1 + 7.0, v);
    }

    [Fact]
    public void AbsentSlot_StoreReachesOuterScope()
    {
        var v = Run($@"
            var outer = 0;
            function bump(n) {{ var local = n; outer = local; return outer; }}
            for (var i = 0; i < {TierUp}; i++) bump(i);
            outer;
        ");
        Assert.Equal(TierUp - 1.0, v);
    }

    [Fact]
    public void DeadZoneSlot_StillThrowsReferenceError()
    {
        var v = Run($@"
            function tdz() {{
                try {{ return q; }} catch (e) {{ return e instanceof ReferenceError ? 1 : 0; }}
                let q = 5;
            }}
            var ok = 0;
            for (var i = 0; i < {TierUp}; i++) ok += tdz();
            ok;
        ");
        Assert.Equal((double)TierUp, v);
    }

    [Fact]
    public void ImmutableSlot_StillRejectsAssignment()
    {
        var v = Run($@"
            function constAssign() {{
                'use strict';
                const c = 1;
                try {{ c = 2; return 0; }} catch (e) {{ return e instanceof TypeError ? 1 : 0; }}
            }}
            var ok = 0;
            for (var i = 0; i < {TierUp}; i++) ok += constAssign();
            ok;
        ");
        Assert.Equal((double)TierUp, v);
    }

    [Fact]
    public void HoistedSlot_ReadsUndefinedBeforeAssignment()
    {
        var v = Run($@"
            function hoisted() {{ var seen = typeof later; var later = 1; return seen === 'undefined' ? 1 : 0; }}
            var ok = 0;
            for (var i = 0; i < {TierUp}; i++) ok += hoisted();
            ok;
        ");
        Assert.Equal((double)TierUp, v);
    }

    [Fact]
    public void ObjectValuedSlot_StoreKeepsWriteBarrier()
    {
        // The object-valued store leaves the inline guard for the slow path so
        // the remembered set still sees it; the value has to survive a GC.
        var v = Run($@"
            function hold(n) {{ var o = {{ v: n }}; var box = o; return box.v; }}
            var acc = 0;
            for (var i = 0; i < {TierUp}; i++) acc = hold(i);
            acc;
        ");
        Assert.Equal(TierUp - 1.0, v);
    }
}
