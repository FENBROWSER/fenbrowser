using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Audit JSRT-001/002/004/005 — GC tracing/rooting hygiene.
// Semantic results prove values survive collection; root-set accounting proves
// throw/await paths stop growing the permanent root stack.
public sealed class GcRootingHygieneTests
{
    private static JsValue Exec(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }

    // Runs the loop body once to settle boot-time cached roots, then requires the
    // SECOND identical pass to leave the explicit root stack unchanged.
    private static BytecodeInterpreter AssertSecondPassLeavesRootStackUnchanged(string loopSource)
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Heap.YoungAllocationsPerMinorGc = 8;

        Exec(interpreter, "globalThis.__warm = 1;");
        Exec(interpreter, loopSource);

        var before = interpreter.Heap.RootCount;
        Exec(interpreter, loopSource);
        var after = interpreter.Heap.RootCount;

        Assert.True(after <= before,
            $"Repeated pass grew the root stack from {before} to {after}; a throw/await path leaks pins.");
        return interpreter;
    }

    // --- JSRT-001: FunctionEnvironmentRecord internal slots are traced ---

    [Fact]
    public void CapturedReceiverBinding_SurvivesMajorCollection()
    {
        var interpreter = new BytecodeInterpreter();

        Exec(interpreter,
            "var get2; " +
            "var obj = { marker: 41, init: function () { var self = this; get2 = function () { return self.marker; }; } }; " +
            "obj.init(); obj = null;");

        interpreter.Heap.CollectGarbage();

        Assert.Equal(41d, Exec(interpreter, "get2();").AsNumber());
    }

    [Fact]
    public void LexicalThisThroughFunctionRecord_SurvivesMajorCollection()
    {
        var interpreter = new BytecodeInterpreter();

        Exec(interpreter,
            "var get; " +
            "var obj = { marker: 42, init: function () { get = () => this.marker; } }; " +
            "obj.init(); obj = null;");

        interpreter.Heap.CollectGarbage();

        Assert.Equal(42d, Exec(interpreter, "get();").AsNumber());
    }

    [Fact]
    public void NewTargetIsTracedThroughFunctionRecord()
    {
        // new.target is observable during construction; this exercises the
        // record's NewTarget slot through the trace path (frame + env both live).
        var interpreter = new BytecodeInterpreter();
        var result = Exec(interpreter,
            "function C() { globalThis.__nt = (new.target === undefined) ? 'no' : 'yes'; } " +
            "new C(); globalThis.__nt;");
        Assert.Equal("yes", result.AsString());
    }

    [Fact]
        public void FunctionEnvironmentInternalSlotsSurviveMajorCollection()
    {
        // A bound `this` captured only through a closure's environment chain
        // must survive a full collection (FunctionEnvironmentRecord internal
        // slots: this / function / newTarget / home are traced with the chain).
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var interpreter = new BytecodeInterpreter(heap);
        Exec(interpreter, """
            var __f;
            var __receiver = { tag: 42 };
            var __holder = { m: function () { var self = this; __f = function () { return self.tag; }; } };
            __holder.m.call(__receiver);
            """);

        heap.CollectGarbage();

        var tag = Exec(interpreter, "__f();");
        Assert.Equal(42, tag.AsNumber());
    }

    // --- JSRT-002: native-call pin windows release on thrown calls ---

    [Fact]
    public void ThrownNativeCallResultRemainsUsable_AfterAutomaticCollections()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Heap.YoungAllocationsPerMinorGc = 4;

        var result = Exec(interpreter,
            "var last = ''; " +
            "for (var i = 0; i < 100; i++) { " +
            "  try { [1].forEach(1); } catch (e) { last = (e && e.name) || '?'; } " +
            "} last;");
        Assert.Equal("TypeError", result.AsString());
    }

    [Fact]
    public void SecondPassOfCaughtNativeThrows_DoesNotGrowRootStack()
    {
        var interpreter = AssertSecondPassLeavesRootStackUnchanged(
            "var t = 0; for (var i = 0; i < 200; i++) { try { [1].forEach(1); } catch (e) { t++; } } globalThis.__t = t;");
        Assert.Equal(200d, Exec(interpreter, "globalThis.__t;").AsNumber());
    }

    // --- JSRT-005: ThrowOrHandle pins are scoped ---

    [Fact]
    public void CaughtThrowPayloadRemainsCorrect_UnderAggressiveCollection()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Heap.YoungAllocationsPerMinorGc = 4;

        var result = Exec(interpreter,
            "var sum = 0; " +
            "for (var i = 0; i < 400; i++) { try { throw { v: i }; } catch (e) { sum += e.v; } } " +
            "sum;");
        Assert.Equal(79800d, result.AsNumber());
    }

    [Fact]
    public void SecondPassOfCaughtScriptThrows_DoesNotGrowRootStack()
    {
        var interpreter = AssertSecondPassLeavesRootStackUnchanged(
            "var t = 0; for (var i = 0; i < 300; i++) { try { throw { payload: i }; } catch (e) { t += e.payload ? 1 : 1; } } globalThis.__u = t;");
        Assert.Equal(300d, Exec(interpreter, "globalThis.__u;").AsNumber());
    }

    [Fact]
    public void RepeatedEscapingThrows_DoNotGrowRootStack()
    {
        var interpreter = new BytecodeInterpreter();
        Exec(interpreter, "globalThis.__warm = 1;");
        var before = interpreter.Heap.RootCount;

        for (var i = 0; i < 100; i++)
        {
            Assert.Throws<JsThrownException>(() => Exec(interpreter, "throw { marker: 1 };"));
        }

        Assert.Equal(before, interpreter.Heap.RootCount);
    }

    // --- JSRT-004: async contexts do not pin forever ---

    [Fact]
    public void AwaitedResultsRemainCorrect()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Heap.YoungAllocationsPerMinorGc = 8;

        var result = Exec(interpreter,
            "var seen = []; " +
            "async function f(x) { var v = await x; seen.push(v); return v; } " +
            "f(Promise.resolve(7)); f(Promise.resolve(8)); " +
            "seen.join(',');");
        Assert.Equal("7,8", result.AsString());
    }

    [Fact]
    public void SecondPassOfAwaitingAsyncCalls_DoesNotGrowRootStack()
    {
        AssertSecondPassLeavesRootStackUnchanged(
            "async function f(x) { await x; return x + 1; } " +
            "for (var i = 0; i < 100; i++) { f(Promise.resolve(i)); } ");
    }

    [Fact]
    public void MajorCollectionMarksDeepObjectGraphsWithoutNativeRecursion()
    {
        const int depth = 20_000;
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var root = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(root);
        var current = root;

        for (var i = 0; i < depth; i++)
        {
            var child = heap.AllocateObject(new JsObject(), AllocationSite.Current());
            var owner = heap.GetObject(current);
            Assert.True(owner.DefineOwnProperty(
                "next",
                new JsPropertyDescriptor(
                    JsValue.FromObject(child),
                    Writable: true,
                    Enumerable: true,
                    Configurable: true)));
            current = child;
        }

        heap.CollectGarbage();

        heap.Validate(current);
    }

    private static (BytecodeInterpreter Interpreter, JsValue Result) Run(string source)
    {
        var interpreter = new BytecodeInterpreter();
        return (interpreter, Exec(interpreter, source));
    }
}
