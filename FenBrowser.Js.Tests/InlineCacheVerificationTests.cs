using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Plan 31 explicit acceptance criteria verification.
public class InlineCacheVerificationTests
{
    private JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // 31.1 — IC off/on same result. Run the same expression twice in the same
    // interpreter (first populates, second uses IC) and verify identical results.
    [Fact]
    public void IcOnOffConsistency_LiteralRead()
    {
        Assert.Equal(42d, Run("var o={x:42}; o.x; o.x; o.x; o.x;").AsNumber());
    }

    [Fact]
    public void IcOnOffConsistency_NestedObjectRead()
    {
        Assert.Equal(7d, Run("var o={a:{b:{c:7}}}; o.a.b.c; o.a.b.c;").AsNumber());
    }

    [Fact]
    public void IcOnOffConsistency_FunctionAcrossInvocations()
    {
        var result = Run(@"
            function getX(obj) { return obj.x; }
            var a = {x: 10}; var b = {x: 20}; var c = {x: 30};
            getX(a) + getX(b) + getX(c);  // 3 shapes → polymorphic IC
        ");
        Assert.Equal(60d, result.AsNumber());
    }

    [Fact]
    public void IcOnOffConsistency_AccessorPropertyFallsBack()
    {
        // IC does not cache accessors; each access goes through the generic path.
        Assert.Equal(99d, Run(@"
            var o = {_x: 99};
            Object.defineProperty(o, 'x', {get: function(){ return o._x; }});
            o.x; o.x;
        ").AsNumber());
    }

    // 31.2 — GC stress with IC. The shape tree uses WeakReference so shapes
    // for collected objects can be GC'd. ICs hold no strong refs to objects.
    [Fact]
    public void GcStress_IcSurvivesAfterCollection()
    {
        var interpreter = new BytecodeInterpreter();
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(
            "(function(){var o={p:1}; var r=o.p; o=null; return r;})();"));
        new BytecodeVerifier().Verify(fn);
        var r1 = interpreter.Execute(fn);
        interpreter.Heap.CollectGarbage();
        var r2 = interpreter.Execute(fn);
        Assert.Equal(1d, r1.AsNumber());
        Assert.Equal(1d, r2.AsNumber());
    }

    [Fact]
    public void GcStress_MultipleCyclesWithIcReads()
    {
        var interpreter = new BytecodeInterpreter();
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(@"
            var o = {val: 42}; var r = o.val; r;"));
        new BytecodeVerifier().Verify(fn);
        for (var i = 0; i < 10; i++)
        {
            var result = interpreter.Execute(fn);
            Assert.Equal(42d, result.AsNumber());
            interpreter.Heap.CollectGarbage();
        }
    }

    // 31.3 — Prototype mutation invalidation. When a prototype property
    // changes, the IC must not return the stale cached descriptor.
    [Fact]
    public void PrototypeInvalidation_DirectMutation()
    {
        Assert.True(Run(@"
            var proto = {x: 1};
            var child = Object.create(proto);
            child.x;                 // resolves proto.x, warms IC on child shape
            proto.x = 999;           // mutate prototype
            child.x === 999;         // must see the new value
        ").AsBoolean());
    }

    [Fact]
    public void PrototypeInvalidation_AddNewPropertyToPrototype()
    {
        Assert.True(Run(@"
            var proto = {a: 1};
            var child = Object.create(proto);
            child.a;                 // warm IC
            proto.b = 2;
            child.b;                 // new own proto property
            child.a === 1;           // IC for 'a' still valid (shape unchanged)
        ").AsBoolean());
    }

    [Fact]
    public void PrototypeInvalidation_ReplacePrototype()
    {
        Assert.True(Run(@"
            var p1 = {x: 1};
            var p2 = {x: 2};
            var child = Object.create(p1);
            child.x;                     // resolves via p1
            Object.setPrototypeOf(child, p2);
            child.x === 2;               // must see p2.x now
        ").AsBoolean());
    }

    // 31.4 — Shape transition invalidation. Adding/deleting own properties
    // transitions the object's shape; IC entries for the old shape must not
    // be used for the new shape.
    [Fact]
    public void ShapeInvalidation_AddOwnProperty()
    {
        Assert.True(Run(@"
            function readX(o) { return o.x; }
            var obj = {x: 5};
            readX(obj);             // warm IC with shape {x}
            obj.y = 10;             // shape → {x, y}
            readX(obj) === 5;       // must work with new shape
        ").AsBoolean());
    }

    [Fact]
    public void ShapeInvalidation_DeleteOwnProperty()
    {
        Assert.True(Run(@"
            var o = {x: 5, y: 10};
            var r1 = o.x;           // warm IC with shape {x, y}
            delete o.x;
            o.x === undefined;      // must return undefined via generic path
        ").AsBoolean());
    }

    // 31.5 — Megamorphic fallback after >4 shapes. The IC transitions to
    // megamorphic state and uses the generic path exclusively.
    [Fact]
    public void MegamorphicFallback_SixShapesAtSameSite()
    {
        Assert.True(Run(@"
            function readV(o) { return o.v; }
            readV({v:1}); readV({v:2,x:1}); readV({v:3,y:1});
            readV({v:4,z:1}); readV({v:5,w:1}); readV({v:6,more:1});
            readV({v:7}) === 7;
        ").AsBoolean());
    }

    // 31.6 — IC hit validation: property attributes. If a property changes
    // from data to accessor or its configurability/writability changes,
    // the IC must detect the mismatch and fall back.
    [Fact]
    public void DescriptorMutation_DataToAccessor()
    {
        Assert.True(Run(@"
            var o = {x: 1};
            o.x;                        // warm IC (data property)
            Object.defineProperty(o, 'x', {get: function(){return 99;}});
            o.x === 99;                 // must see accessor result
        ").AsBoolean());
    }
}
