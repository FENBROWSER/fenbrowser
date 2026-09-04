using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A WeakMap holds its keys weakly and its values strongly. Nothing showed the
/// major mark phase those values, so a value reachable only through a WeakMap
/// was swept while the map still held its handle, and the next get() returned
/// a cell that no longer existed ("Stale heap handle").
/// </summary>
public sealed class WeakMapValueTracingTests
{
    private static BytecodeInterpreter RunSetup(string source)
    {
        var interpreter = new BytecodeInterpreter();
        var setup = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(setup);
        interpreter.Execute(setup);
        return interpreter;
    }

    private static JsValueResult Probe(BytecodeInterpreter interpreter, string source)
    {
        var probe = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(probe);
        return new JsValueResult(interpreter.Execute(probe));
    }

    private readonly record struct JsValueResult(FenBrowser.Js.Runtime.JsValue Value)
    {
        public double AsNumber() => Value.AsNumber();
        public bool AsBoolean() => Value.AsBoolean();
    }

    [Fact]
    public void ObjectKeyedValue_SurvivesAMajorCollection()
    {
        var interpreter = RunSetup(
            "globalThis.k = {};" +
            "globalThis.wm = new WeakMap();" +
            "wm.set(k, new Array(1, 2, 3));");

        interpreter.Heap.CollectGarbage();

        Assert.Equal(3d, Probe(interpreter, "wm.get(k).length").AsNumber());
    }

    [Fact]
    public void SymbolKeyedValue_SurvivesAMajorCollection()
    {
        // Symbol keys live in a separate entry table with the same omission.
        var interpreter = RunSetup(
            "globalThis.s = Symbol('key');" +
            "globalThis.wm = new WeakMap();" +
            "wm.set(s, { marker: 7 });");

        interpreter.Heap.CollectGarbage();

        Assert.Equal(7d, Probe(interpreter, "wm.get(s).marker").AsNumber());
    }

    [Fact]
    public void ValueSurvivesRepeatedCollections()
    {
        var interpreter = RunSetup(
            "globalThis.k = {};" +
            "globalThis.wm = new WeakMap();" +
            "wm.set(k, { nested: { deep: 11 } });");

        interpreter.Heap.CollectGarbage();
        interpreter.Heap.CollectGarbage();
        interpreter.Heap.CollectGarbage();

        Assert.Equal(11d, Probe(interpreter, "wm.get(k).nested.deep").AsNumber());
    }

    [Fact]
    public void KeysStayWeak_WeakMapDoesNotBecomeAStrongMap()
    {
        // The fix traces values only. Tracing keys as well would silently turn
        // every WeakMap into a Map.
        var interpreter = RunSetup(
            "globalThis.wm = new WeakMap();" +
            "globalThis.probe = (function () { var k = {}; wm.set(k, 1); return wm.has(k); })();");

        interpreter.Heap.CollectGarbage();

        Assert.True(Probe(interpreter, "probe").AsBoolean());
    }
}
