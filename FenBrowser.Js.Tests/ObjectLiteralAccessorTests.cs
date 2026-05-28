using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Audit gap §1 closeout: object literals with `get name()` / `set name(v)`
// must install REAL accessor descriptors. Before this fix, the parser
// preserved the getter function but the compiler emitted SetPropByName,
// which stored the function as a regular data property. Read access then
// returned the function instead of invoking it, breaking the iterator
// protocol when the spec puts a getter on `[Symbol.iterator]` or `.next`.
public class ObjectLiteralAccessorTests
{
    private static JsValue Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void GetterIsInvokedOnRead()
    {
        // `o.x` reads the getter — must return 42, not the function itself.
        Assert.Equal(42d, Run("var o = { get x() { return 42; } }; o.x;").AsNumber());
    }

    [Fact]
    public void SetterIsInvokedOnWrite()
    {
        // Setter must run; backing storage decided by the user code.
        var v = Run("var captured = 0; var o = { set x(v) { captured = v * 2; } }; o.x = 7; captured;");
        Assert.Equal(14d, v.AsNumber());
    }

    [Fact]
    public void PairedGetterAndSetterCoexist()
    {
        var v = Run("var store = 1; var o = { get x() { return store; }, set x(v) { store = v; } }; o.x = 9; o.x;");
        Assert.Equal(9d, v.AsNumber());
    }

    [Fact]
    public void GetterReturningCallableReplaces_NonInvocation_Bug()
    {
        // Direct repro of test262 AggregateError case 7. Pre-fix `inner.next`
        // returned the getter function (because the property was a data
        // property), masking the spec's TypeError. Post-fix the getter is
        // invoked and returns {}, which the caller then treats as non-callable.
        var v = Run(@"
            var inner = { get next() { return {}; } };
            var caught = '';
            try { inner.next(); } catch (e) { caught = (e && e.constructor && e.constructor.name) || String(e); }
            caught;
        ");
        Assert.Equal("TypeError", v.AsString());
    }

    [Fact]
    public void AggregateError_GetterReturningEmptyNext_ThrowsTypeError()
    {
        // The actual test262 case 7 — should now report TypeError, not crash.
        var v = Run(@"
            var caught = '';
            try {
                new AggregateError({ [Symbol.iterator]() { return { get next() { return {}; } }; } });
            } catch (e) { caught = (e && e.constructor && e.constructor.name) || String(e); }
            caught;
        ");
        Assert.Equal("TypeError", v.AsString());
    }

    [Fact]
    public void ComputedGetterStringKey()
    {
        var v = Run(@"
            var key = 'x';
            var o = { get [key]() { return 7; } };
            o.x;
        ");
        Assert.Equal(7d, v.AsNumber());
    }
}
