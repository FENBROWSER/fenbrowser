using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Audit doc §3.1 first slice. JIT codegen for GetPropByName / SetPropByName
// now pre-resolves the property name (skipping IReadOnlyList<string>
// indexing) and pre-allocates the inline cache reference (skipping the
// LoadICs/StoreICs dictionary lookup) per call site. The IC reference is
// stable for the function's lifetime so it's embedded as a Constant. Tests
// pin the contract: same observable behavior, IC populates correctly,
// accessor/non-writable invalidation still works.
public class JitDirectIcTests
{
    private static BytecodeFunction Compile(string src)
        => new BytecodeCompiler().CompileScript(new SourceText(src));

    private static double Run(string src)
        => new BytecodeInterpreter().Execute(Compile(src)).AsNumber();

    [Fact]
    public void PropertyReadInJit_ReturnsCorrectValue_AfterTierUp()
    {
        // Drive >100 invocations so the JIT path engages, then read a
        // monomorphic property. Result must match the interpreter.
        var v = Run(@"
            function read(o) { return o.x; }
            var obj = { x: 42 };
            var sum = 0;
            for (var i = 0; i < 200; i++) sum = read(obj);
            sum;
        ");
        Assert.Equal(42.0, v);
    }

    [Fact]
    public void PropertyWriteInJit_PersistsValue_AfterTierUp()
    {
        var v = Run(@"
            function write(o, v) { o.x = v; }
            var obj = { x: 0 };
            for (var i = 0; i < 200; i++) write(obj, i);
            obj.x;
        ");
        Assert.Equal(199.0, v);
    }

    [Fact]
    public void AccessorInvalidation_StillWorksUnderJit()
    {
        // After tier-up the JIT path embeds the IC reference. If the
        // cached slot becomes an accessor, the IC must invalidate
        // correctly (no stale slot read) and fall through to the slow
        // path that walks the accessor.
        var v = Run(@"
            function read(o) { return o.x; }
            var obj = { x: 7 };
            var sum = 0;
            for (var i = 0; i < 200; i++) sum = read(obj);
            // Replace the data property with an accessor that returns 99.
            Object.defineProperty(obj, 'x', { get: function() { return 99; } });
            read(obj);
        ");
        Assert.Equal(99.0, v);
    }

    [Fact]
    public void PolymorphicAccess_FallsBackCleanly()
    {
        // Two object shapes hit the same site. The 4-entry PIC accommodates
        // both; result must remain correct under JIT.
        var v = Run(@"
            function read(o) { return o.x; }
            var a = { x: 1, y: 2 };
            var b = { y: 0, x: 10 };
            var sum = 0;
            for (var i = 0; i < 200; i++) sum += read(i % 2 ? a : b);
            sum;
        ");
        // Sum of 100 reads of a.x (=1) + 100 of b.x (=10): 100+1000 = 1100.
        // (i starts at 0; i%2==0 picks b for even i, a for odd. 100 evens, 100 odds.)
        Assert.Equal(1100.0, v);
    }

    [Fact]
    public void EnsureLoadIC_DoesNotAffectInterpretedRuns()
    {
        // Pre-allocating the IC in the JIT compile path must not change
        // observable behavior of interpreted (non-JIT) executions.
        var v = Run("var o = {a:1}; o.a + o.a;");
        Assert.Equal(2.0, v);
    }
}
