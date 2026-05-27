using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Tier 5 #28 — JsRealm isolation. Verifies that an Isolated realm:
//   * has its own Symbol.for() registry (per-interpreter, already by
//     construction);
//   * has its own global state (var bindings, globalThis properties);
//   * bypasses the shared BytecodeCache during compile+execute so that
//     a cached entry from one realm does not flow into another.
public sealed class RealmIsolationTests
{
    [Fact]
    public void IsolatedRealms_DoNotShareGlobalState()
    {
        var realmA = new JsRealm { Isolated = true };
        var realmB = new JsRealm { Isolated = true };

        realmA.Run(interp =>
        {
            var compiler = new BytecodeCompiler();
            interp.Execute(compiler.CompileScript(new SourceText("var x = 'A';")));
            return 0;
        });

        var bX = realmB.Run(interp =>
        {
            var compiler = new BytecodeCompiler();
            return interp.Execute(compiler.CompileScript(new SourceText("globalThis.x;")));
        });

        Assert.Equal(JsValueTag.Undefined, bX.Tag);
    }

    [Fact]
    public void IsolatedRealm_ActivatesBytecodeCacheBypass()
    {
        var realm = new JsRealm { Isolated = true };
        Assert.False(BytecodeCache.IsBypassed);
        var observedInside = realm.Run(_ => BytecodeCache.IsBypassed);
        Assert.True(observedInside);
        Assert.False(BytecodeCache.IsBypassed);
    }

    [Fact]
    public void NonIsolatedRealm_DoesNotActivateBypass()
    {
        var realm = new JsRealm();
        var observedInside = realm.Run(_ => BytecodeCache.IsBypassed);
        Assert.False(observedInside);
    }

    [Fact]
    public void IsolatedRealms_HaveSeparateSymbolForRegistries()
    {
        var realmA = new JsRealm { Isolated = true };
        var realmB = new JsRealm { Isolated = true };

        var symA = realmA.Run(interp =>
        {
            var compiler = new BytecodeCompiler();
            return interp.Execute(compiler.CompileScript(new SourceText("Symbol.for('shared');")));
        });

        var symB = realmB.Run(interp =>
        {
            var compiler = new BytecodeCompiler();
            return interp.Execute(compiler.CompileScript(new SourceText("Symbol.for('shared');")));
        });

        Assert.NotEqual(symA.AsSymbolId(), symB.AsSymbolId());
    }
}
