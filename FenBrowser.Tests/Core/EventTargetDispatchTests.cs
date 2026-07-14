using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class EventTargetDispatchTests
{
    [Fact]
    public void DispatchWithoutListenersLeavesObservableStateConsistent()
    {
        var target = new Element("button");
        var evt = new Event("click", new EventInit { Bubbles = true, Composed = true });

        Assert.True(target.DispatchEvent(evt));
        Assert.Same(target, evt.Target);
        Assert.Null(evt.CurrentTarget);
        Assert.Equal(EventPhase.None, evt.EventPhase);
        Assert.Empty(evt.ComposedPath());
    }

    [Fact]
    public void DispatchPreservesCaptureTargetAndBubbleOrder()
    {
        var root = new Element("main");
        var parent = new Element("div");
        var target = new Element("button");
        root.AppendChild(parent);
        parent.AppendChild(target);
        var calls = new List<string>();

        root.AddEventListener("click", evt => calls.Add($"root-{evt.EventPhase}"), capture: true);
        target.AddEventListener("click", evt => calls.Add($"target-{evt.EventPhase}"));
        parent.AddEventListener("click", evt => calls.Add($"parent-{evt.EventPhase}"));

        Assert.True(target.DispatchEvent(new Event("click", new EventInit { Bubbles = true })));
        Assert.Equal(
            ["root-Capturing", "target-AtTarget", "parent-Bubbling"],
            calls);
    }

    [Fact]
    public void OnceAndPassiveOptionsRetainDomSemantics()
    {
        var target = new Element("button");
        var onceCalls = 0;
        target.AddEventListener(
            "once",
            _ => onceCalls++,
            new AddEventListenerOptions { Once = true });

        Assert.True(target.DispatchEvent(new Event("once")));
        Assert.True(target.DispatchEvent(new Event("once")));
        Assert.Equal(1, onceCalls);

        target.AddEventListener(
            "passive",
            evt => evt.PreventDefault(),
            new AddEventListenerOptions { Passive = true });
        var passiveEvent = new Event("passive", new EventInit { Cancelable = true });

        Assert.True(target.DispatchEvent(passiveEvent));
        Assert.False(passiveEvent.DefaultPrevented);
    }

    [Fact]
    public void RemovingListenerDuringDispatchPreventsItsLaterInvocation()
    {
        var target = new Element("button");
        var secondCalls = 0;
        EventListener second = _ => secondCalls++;
        target.AddEventListener("click", _ => target.RemoveEventListener("click", second));
        target.AddEventListener("click", second);

        Assert.True(target.DispatchEvent(new Event("click")));
        Assert.Equal(0, secondCalls);
    }
}
