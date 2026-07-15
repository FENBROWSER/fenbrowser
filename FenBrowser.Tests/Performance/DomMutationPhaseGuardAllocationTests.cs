using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class DomMutationPhaseGuardAllocationTests
{
    private readonly ITestOutputHelper _output;

    public DomMutationPhaseGuardAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AppendAndRemove_InIdleKeepPhaseGuardAllocationBounded()
    {
        const int iterations = 10_000;
        EngineContext.Reset();
        var parent = new Element("div");
        var child = new Element("span");

        parent.AppendChild(child);
        parent.RemoveChild(child);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            parent.AppendChild(child);
            parent.RemoveChild(child);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"{iterations:N0} append/remove pairs allocated {allocated:N0} B.");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ThreePhaseGuard_KeepsRepeatedChecksAllocationBounded()
    {
        const int iterations = 10_000;
        EngineContext.Reset();
        var context = EngineContext.Current;

        context.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            context.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"{iterations:N0} three-phase checks allocated {allocated:N0} B.");
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(EnginePhase.Measure)]
    [InlineData(EnginePhase.Layout)]
    [InlineData(EnginePhase.Paint)]
    public void DomMutation_RemainsForbiddenDuringRestrictedPhase(EnginePhase phase)
    {
        EngineContext.Reset();
        var parent = new Element("div");
        var child = new Element("span");

        try
        {
            EngineContext.Current.BeginPhase(phase);
            Assert.Throws<InvalidOperationException>(() => parent.AppendChild(child));
            Assert.Null(child.ParentNode);
            Assert.Throws<InvalidOperationException>(() => parent.SetAttribute("data-phase", "forbidden"));
            Assert.Null(parent.GetAttribute("data-phase"));
        }
        finally
        {
            EngineContext.Reset();
        }
    }
}
