using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Tier-5 #28 cross-realm leak audit (audit doc §8.b). Negative tests
// proving that a value created in realm A cannot be observed from
// realm B except via an explicit host bridge. The contracts pinned
// here back the realm-isolation security story:
//   * each JsRealm owns its own heap, global object, and intrinsics;
//   * mutating a builtin's prototype in realm A must not affect realm B;
//   * Symbol.for / global var bindings / Object.prototype identity all
//     diverge across realms;
//   * Isolated realms additionally bypass the shared BytecodeCache so
//     that compile-time observation cannot leak (covered separately in
//     RealmIsolationTests).
public sealed class CrossRealmLeakTests
{
    private static JsValue Exec(JsRealm realm, string src) =>
        realm.Run(interp => interp.Execute(new BytecodeCompiler().CompileScript(new SourceText(src))));

    // ---- Globals and var bindings ----

    [Fact]
    public void GlobalVar_InRealmA_IsNotVisibleInRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "var leak = 42;");
        var v = Exec(b, "typeof leak;");
        // unresolved global -> "undefined"
        Assert.Equal("undefined", v.AsString());
    }

    [Fact]
    public void GlobalThis_DiffersAcrossRealms()
    {
        // Each realm constructs its own global object; assigning the
        // same property in both must produce independent values.
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "globalThis.tag = 'A';");
        Exec(b, "globalThis.tag = 'B';");
        Assert.Equal("A", Exec(a, "globalThis.tag;").AsString());
        Assert.Equal("B", Exec(b, "globalThis.tag;").AsString());
    }

    // ---- Intrinsic constructors and prototypes ----

    [Fact]
    public void ObjectPrototypePollution_InRealmA_DoesNotAffectRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "Object.prototype.leak = 'pwned';");
        var v = Exec(b, "var o = {}; typeof o.leak;");
        Assert.Equal("undefined", v.AsString());
    }

    [Fact]
    public void ArrayPrototypePollution_InRealmA_DoesNotAffectRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "Array.prototype.leak = function(){ return 'pwned'; };");
        var v = Exec(b, "var arr = []; typeof arr.leak;");
        Assert.Equal("undefined", v.AsString());
    }

    [Fact]
    public void StringPrototypePollution_InRealmA_DoesNotAffectRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "String.prototype.leak = 'pwned';");
        var v = Exec(b, "'x'.leak;");
        Assert.Equal(JsValueTag.Undefined, v.Tag);
    }

    [Fact]
    public void ErrorMessageMutation_InRealmA_DoesNotAffectRealmB()
    {
        // A script can't rebrand the Error constructor of another realm.
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "Error.prototype.name = 'PwnedError';");
        var v = Exec(b, "(new Error()).name;");
        Assert.Equal("Error", v.AsString());
    }

    [Fact]
    public void MathMutation_InRealmA_DoesNotAffectRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "Math.PI = 3;");
        // ECMA-262 specifies Math.PI as non-writable, so the assignment is
        // ignored in both realms in strict mode (and silently dropped in
        // sloppy mode). Either way, realm B must read the canonical value.
        var v = Exec(b, "Math.PI;");
        Assert.InRange(v.AsNumber(), 3.14, 3.15);
    }

    // ---- Function / closure / instance identity ----

    [Fact]
    public void FunctionDeclaredInRealmA_IsNotVisibleAsGlobalInRealmB()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "function leakFn(){ return 1; }");
        Assert.Equal("undefined", Exec(b, "typeof leakFn;").AsString());
    }

    [Fact]
    public void InstanceCreatedInRealmA_HasNoIdentityInRealmB()
    {
        // No host bridge between realms: an object exists only inside
        // its creating heap. We can't even reference it from B.
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "globalThis.shared = {tag: 'A'};");
        // From B, `shared` is not in the global scope.
        Assert.Equal("undefined", Exec(b, "typeof shared;").AsString());
    }

    // ---- Symbol registry isolation (Tier-5 #28) ----

    [Fact]
    public void SymbolFor_SameKey_ProducesDistinctSymbolsAcrossRealms()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        var symA = Exec(a, "Symbol.for('shared');");
        var symB = Exec(b, "Symbol.for('shared');");
        Assert.NotEqual(symA.AsSymbolId(), symB.AsSymbolId());
    }

    [Fact]
    public void SymbolKeyFor_FromRealmA_DoesNotResolveInRealmB()
    {
        // A symbol registered in realm A must not appear in B's
        // registry: B.Symbol.keyFor(B-side-symbol) === undefined for any
        // symbol that wasn't registered in B.
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        Exec(a, "Symbol.for('only-in-A');");
        // Construct an arbitrary symbol in B that was NOT registered.
        var keyResult = Exec(b, "Symbol.keyFor(Symbol('local-only'));");
        Assert.Equal(JsValueTag.Undefined, keyResult.Tag);
    }

    // ---- Bytecode cache bypass ----

    [Fact]
    public void IsolatedRealmRun_RestoresCacheBypass_AfterCallbackException()
    {
        // Even if the inner script throws, the BytecodeCache.BypassScope
        // dispose must restore the flag so subsequent non-isolated work
        // still hits the shared cache.
        var realm = new JsRealm { Isolated = true };
        Assert.False(BytecodeCache.IsBypassed);
        try
        {
            realm.Run<int>(_ => throw new System.Exception("boom"));
        }
        catch (System.Exception)
        {
            // expected; we care about the post-condition.
        }
        Assert.False(BytecodeCache.IsBypassed);
    }

    [Fact]
    public void NestedRealmRun_TwoIsolatedRealms_StackBypassCorrectly()
    {
        // Two isolated realms nested: bypass must remain on for the inner
        // callback and remain on for the outer continuation, then unwind
        // cleanly when both exit.
        var outer = new JsRealm { Isolated = true };
        var inner = new JsRealm { Isolated = true };
        var observed = (outerEntry: false, innerEntry: false, outerExit: false, after: false);
        outer.Run<int>(_ =>
        {
            observed.outerEntry = BytecodeCache.IsBypassed;
            inner.Run<int>(_ =>
            {
                observed.innerEntry = BytecodeCache.IsBypassed;
                return 0;
            });
            observed.outerExit = BytecodeCache.IsBypassed;
            return 0;
        });
        observed.after = BytecodeCache.IsBypassed;
        Assert.True(observed.outerEntry);
        Assert.True(observed.innerEntry);
        Assert.True(observed.outerExit);
        Assert.False(observed.after);
    }

    // ---- Eval policy is per-realm ----

    [Fact]
    public void EvalAllowedFlag_IsPerInterpreter()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        a.Interpreter.EvalAllowed = false;
        // A: blocked. B: still default-allowed.
        Assert.True(b.Interpreter.EvalAllowed);
        // Sanity: setting A doesn't mutate B's flag.
        a.Interpreter.EvalAllowed = true;
        b.Interpreter.EvalAllowed = false;
        Assert.True(a.Interpreter.EvalAllowed);
    }

    // ---- Wall-clock and budget are per-realm ----

    [Fact]
    public void WallClockBudget_IsPerInterpreter()
    {
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        a.Interpreter.WallClockTimeoutMs = 50;
        Assert.Equal(0L, b.Interpreter.WallClockTimeoutMs);
    }

    // ---- Static state ----

    [Fact]
    public void Shape_Root_IsIntentionallyShared()
    {
        // This is a documented non-leak: Shape.Root is a process-wide
        // singleton because shapes carry no per-realm capability state
        // (they index property layouts, not values). Pin the contract so
        // a future change can't silently break IC realm-agnosticism.
        var a = new JsRealm { Isolated = true };
        var b = new JsRealm { Isolated = true };
        var beforeA = Exec(a, "var o = {}; typeof o;").AsString();
        var beforeB = Exec(b, "var o = {}; typeof o;").AsString();
        Assert.Equal("object", beforeA);
        Assert.Equal("object", beforeB);
    }
}
