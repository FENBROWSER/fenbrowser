using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class InlineCacheTests
{
    private JsValue Run(string source)
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // Plan §31: IC off/on produces same result.
    [Fact]
    public void IcProducesSameResultAsGenericPath()
    {
        // First access warms the IC, second uses it.
        Assert.Equal(42d, Run("var o = {x: 42}; o.x;").AsNumber());
        Assert.Equal(42d, Run("var o = {x: 42}; o.x; o.x;").AsNumber());
    }

    [Fact]
    public void IcHandlesMultipleShapes()
    {
        // Same property access site sees different shapes.
        Assert.True(Run(@"
            function readX(o) { return o.x; }
            var a = {x: 1};
            var b = {x: 2, y: 3};
            readX(a) === 1 && readX(b) === 2;
        ").AsBoolean());
    }

    [Fact]
    public void IcHandlesPropertyReadAcrossCalls()
    {
        Assert.Equal(99d, Run(@"
            function get(obj) { return obj.val; }
            var o = {val: 99};
            get(o);
            get(o);
        ").AsNumber());
    }

    // Plan §31: shape transition (adding new property) does not invalidate
    // existing ICs for UNCHANGED shapes.
    [Fact]
    public void IcSurvivesNewPropertyOnUnrelatedObject()
    {
        Assert.True(Run(@"
            var a = {x: 5};
            var b = {x: 10};
            var r1 = a.x;
            b.y = 20;       // shape change on b, not a
            var r2 = a.x;   // IC for a.x should still hit
            r1 === 5 && r2 === 5;
        ").AsBoolean());
    }

    // Plan §31: prototype mutation invalidates IC.
    [Fact]
    public void IcReadsUpdatedPrototypeProperty()
    {
        Assert.True(Run(@"
            var proto = {x: 1};
            var child = Object.create(proto);
            var r1 = child.x;       // reads from prototype, warms IC
            proto.x = 2;            // prototype mutation
            var r2 = child.x;       // must see updated value
            r1 === 1 && r2 === 2;
        ").AsBoolean());
    }

    // Plan §31: GC stress with ICs — objects survive collection while IC holds
    // a reference to their shape (shape is static, no strong ref needed).
    [Fact]
    public void IcSurvivesGcCycle()
    {
        var interpreter = new BytecodeInterpreter();
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("var o = {p: 123}; o.p; o.p; o.p;"));
        new BytecodeVerifier().Verify(fn);
        var result = interpreter.Execute(fn);
        interpreter.Heap.CollectGarbage();
        Assert.Equal(123d, result.AsNumber());
    }

    // Polymorphic: 4 distinct shapes at same access site.
    [Fact]
    public void IcHandlesPolymorphicSaturation()
    {
        Assert.True(Run(@"
            function read(o) { return o.v; }
            var a = {v: 1};
            var b = {v: 2, extra: 1};
            var c = {v: 3, other: 1};
            var d = {v: 4, more: 1};
            var e = {v: 5, another: 1};
            read(a) === 1 && read(b) === 2 && read(c) === 3 && read(d) === 4 && read(e) === 5;
        ").AsBoolean());
    }

    // Deleted property should not be found via IC stale cache.
    [Fact]
    public void IcDoesNotReturnDeletedProperty()
    {
        Assert.True(Run(@"
            var o = {x: 10, y: 20};
            var r1 = o.x;       // warm IC
            delete o.x;
            var r2 = o.x;       // must return undefined
            r1 === 10 && r2 === undefined;
        ").AsBoolean());
    }
}
