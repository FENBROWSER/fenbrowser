using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Provenance for GC roots and for the anonymous functions they point at.
///
/// A dangling entry in the root set fails validation from inside the mark
/// phase, and the error could only say which cell was missing — never which
/// root pointed at it, which is the thing actually broken. Minified page
/// bundles then made the cell itself unidentifiable too: their functions have
/// no Name and no recoverable SourceText, so every one of them printed as
/// "&lt;anonymous&gt;".
/// </summary>
public sealed class GcRootProvenanceTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static BytecodeFunction FirstNested(string source) =>
        Compile(source).NestedFunctions[0];

    [Fact]
    public void Signature_DistinguishesTwoAnonymousFunctions()
    {
        var first = FirstNested("(function (a) { return a.alpha + 1; });");
        var second = FirstNested("(function (a) { return a.beta + 2; });");

        // The premise the diagnostic used to rely on: neither has anything to
        // print but "<anonymous>".
        Assert.True(string.IsNullOrEmpty(first.Name));
        Assert.True(string.IsNullOrEmpty(second.Name));

        Assert.NotEqual(BytecodeFunctionSignature.Hash(first), BytecodeFunctionSignature.Hash(second));
        Assert.NotEqual(BytecodeFunctionSignature.Describe(first), BytecodeFunctionSignature.Describe(second));
    }

    [Fact]
    public void Signature_IsStableAcrossSeparateCompilations()
    {
        const string Source = "(function (a, b) { return a.gamma(b) + 'delta'; });";

        var first = FirstNested(Source);
        var second = FirstNested(Source);

        Assert.NotSame(first, second);
        Assert.Equal(BytecodeFunctionSignature.Hash(first), BytecodeFunctionSignature.Hash(second));
        Assert.Equal(BytecodeFunctionSignature.Describe(first), BytecodeFunctionSignature.Describe(second));
    }

    [Fact]
    public void Signature_CarriesIdentifyingShape()
    {
        var function = FirstNested("(function (widthArg) { return widthArg.offsetWidth + 'px-marker'; });");
        var description = BytecodeFunctionSignature.Describe(function);

        Assert.Contains("fn#", description, StringComparison.Ordinal);
        Assert.Contains("[anon]", description, StringComparison.Ordinal);
        Assert.Contains("offsetWidth", description, StringComparison.Ordinal);
        Assert.Contains("px-marker", description, StringComparison.Ordinal);
        Assert.Contains("widthArg", description, StringComparison.Ordinal);
        // Single line: these land in one log record.
        Assert.DoesNotContain('\n', description);
    }

    [Fact]
    public void Signature_IdentifiesShapeRatherThanSourceText()
    {
        // SourceText is null for every function the compiler could not recover
        // original text for, so the identity cannot rest on it. Two functions
        // whose text differs only in comments and spacing compile to the same
        // shape and must fingerprint alike.
        var spaced = FirstNested("(function (a) { return a.epsilon; });");
        var commented = FirstNested("(function (a) {  /* note */  return a.epsilon;  });");

        Assert.NotEqual(spaced.SourceText, commented.SourceText);
        Assert.Equal(BytecodeFunctionSignature.Hash(spaced), BytecodeFunctionSignature.Hash(commented));
        Assert.DoesNotContain("<anonymous>", BytecodeFunctionSignature.Describe(spaced), StringComparison.Ordinal);
    }

    [Fact]
    public void StaleRootHandle_NamesTheRootSlotThatHeldIt()
    {
        if (JsHeap.RootAuditEnabled)
        {
            // FEN_FENJS_GC_ROOT_AUDIT=1 deliberately downgrades this fatal to a
            // report so a live run can enumerate every bad root instead of
            // ending at the first. Nothing to assert about the fatal then.
            return;
        }

        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.PushRoot(handle);
        heap.FreeForTest(handle);

        var error = Assert.Throws<JsEngineFatalException>(() => heap.CollectGarbage());

        // Without this the message named only the missing cell, and the root
        // that pointed at it — the actual defect — went unreported.
        Assert.Contains("rootCtx=heap.roots[", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleNonRootHandle_SaysItWasNotReachedFromARootSlot()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(handle);

        var error = Assert.Throws<JsEngineFatalException>(() => heap.Validate(handle));

        Assert.Contains("rootCtx=<not-a-root-walk>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeHandleForDiagnostics_ReportsADeadHandleWithoutThrowing()
    {
        var heap = new JsHeap();
        var handle = heap.AllocateObject(new JsObject(), AllocationSite.Current());
        heap.FreeForTest(handle);

        var description = heap.DescribeHandleForDiagnostics(handle);

        Assert.Contains("cell=null", description, StringComparison.Ordinal);
        Assert.Contains($"idx={handle.Index}", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnPinRing_DoesNotAdmitAHandleThisHeapCannotResolve()
    {
        // The ring is a GC root, so an unresolvable handle stored in it kills
        // every later collection, long after the call that produced it
        // returned. A healthy run stores nothing it cannot resolve and reports
        // nothing.
        var interpreter = new BytecodeInterpreter();
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText("var f = function (x) { return { v: x }; }; for (var i = 0; i < 50; i++) f(i); 1;"));
        new BytecodeVerifier().Verify(fn);
        interpreter.Execute(fn);
        interpreter.Heap.CollectGarbage();

        Assert.Empty(interpreter.ReturnPinFailureReports);
    }
}
