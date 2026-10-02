using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Intl.Segmenter's segment and resolvedOptions are accessors whose getter returns an
/// implementation function captured in a CLR closure. The collector cannot see into a
/// closure, so the first minor collection swept the implementation while the getter
/// still handed it out ("Stale heap handle" on the next read).
/// </summary>
public sealed class IntlAccessorCapturedRootTests
{
    [Fact]
    public void SegmenterAccessors_SurviveAMinorCollection()
    {
        // Verifying after each collection traces every live cell, so a getter left
        // naming a swept function fails at the collection that swept it.
        var interpreter = new BytecodeInterpreter(new JsHeap(verifyHeapAfterGc: true));
        var bootstrap = new BytecodeCompiler().CompileScript(new SourceText("0"));
        new BytecodeVerifier().Verify(bootstrap);
        interpreter.Execute(bootstrap);
        interpreter.Heap.MinorCollect();
        interpreter.Heap.MinorCollect();

        var probe = new BytecodeCompiler().CompileScript(new SourceText(
            "var s = new Intl.Segmenter('en'); " +
            "typeof s.segment === 'function' && typeof s.resolvedOptions === 'function'"));
        new BytecodeVerifier().Verify(probe);

        Assert.True(interpreter.Execute(probe).AsBoolean());
    }
}
