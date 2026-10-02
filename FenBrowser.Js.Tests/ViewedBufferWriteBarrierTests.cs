using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A typed array made from a length owns a backing ArrayBuffer that has no heap cell
/// until script first reads <c>.buffer</c> (or calls subarray). The view traces that
/// cell from then on, but nothing ran the write barrier for the new edge: a view
/// already promoted to the old generation is not rescanned by a minor collection, so
/// the buffer cell was swept while the view still named it, and the next
/// <c>.buffer</c> read failed with "Stale heap handle". YouTube's player hit it on
/// long-lived views over media data.
/// </summary>
public sealed class ViewedBufferWriteBarrierTests
{
    private static BytecodeInterpreter RunSetup(string source)
    {
        // Verifying after each collection traces every live cell, so a view left naming
        // a swept buffer fails at the collection that swept it.
        var interpreter = new BytecodeInterpreter(new FenBrowser.Js.Heap.JsHeap(verifyHeapAfterGc: true));
        var setup = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(setup);
        interpreter.Execute(setup);
        return interpreter;
    }

    private static double Probe(BytecodeInterpreter interpreter, string source)
    {
        var probe = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(probe);
        return interpreter.Execute(probe).AsNumber();
    }

    private static void PromoteEverything(BytecodeInterpreter interpreter)
    {
        for (var i = 0; i <= interpreter.Heap.PromotionThreshold; i++)
        {
            interpreter.Heap.MinorCollect();
        }
    }

    [Theory]
    [InlineData("u.buffer.byteLength")]
    [InlineData("u.subarray(1).length")]
    public void BufferFirstExposedFromAnOldView_SurvivesMinorCollections(string firstExposure)
    {
        var interpreter = RunSetup("globalThis.u = new Uint8Array(16); u[3] = 7;");
        PromoteEverything(interpreter);

        // The buffer's cell is created here, from a view that is already old. The
        // probe keeps no reference of its own to it.
        Probe(interpreter, firstExposure);
        // Cycle the pins on recent allocations and native return values, which would
        // otherwise keep the getter's result alive across the next collections.
        Probe(interpreter, "var n = 0; for (var i = 0; i < 4096; i++) n += [i].slice()[0]; n");
        interpreter.Heap.MinorCollect();
        interpreter.Heap.MinorCollect();

        Assert.Equal(23d, Probe(interpreter, "u.buffer.byteLength + new Uint8Array(u.buffer)[3]"));
    }
}
