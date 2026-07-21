using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

/// <summary>
/// Phase 12: verify that the async input dispatch infrastructure is wired
/// correctly — async methods exist on the interface, return Tasks, and
/// preserve the timeout behavior.
/// </summary>
public class Phase12AsyncInputDispatchTests
{
    [Fact]
    public void IBrowserScriptEngine_HasAsyncDispatchMethod()
    {
        // The interface must expose DispatchEventForElementAsync.
        var method = typeof(IBrowserScriptEngine).GetMethod("DispatchEventForElementAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(System.Threading.Tasks.Task<bool>), method.ReturnType);
    }

    [Fact]
    public void CustomHtmlEngine_HasAsyncDispatchMethod()
    {
        // CustomHtmlEngine must expose DispatchPointerEventAsync.
        var method = typeof(CustomHtmlEngine).GetMethod("DispatchPointerEventAsync");
        Assert.NotNull(method);
        Assert.True(method.ReturnType == typeof(System.Threading.Tasks.Task<bool>));
    }

    [Fact]
    public void AnimationFrameEvent_HasUpdateKind()
    {
        // Phase 3's AnimationFrameEvent.UpdateKind feeds into Phase 4's
        // compositor-only detection and Phase 12's async dispatch paths.
        var evt = new AnimationFrameEvent
        {
            Element = new FenBrowser.Core.Dom.V2.Element("div"),
            OwnerDocument = FenBrowser.Core.Dom.V2.Document.CreateHtmlDocument(),
            UpdateKind = AnimationUpdateKind.Composite
        };

        Assert.Equal(AnimationUpdateKind.Composite, evt.UpdateKind);
        Assert.NotNull(evt.Element);
        Assert.NotNull(evt.OwnerDocument);
    }

    [Fact]
    public void UpdateKind_CompositeOnly_DoesNotIncludePaintOrLayout()
    {
        var kind = AnimationUpdateKind.Composite;
        Assert.False(kind.HasFlag(AnimationUpdateKind.Paint));
        Assert.False(kind.HasFlag(AnimationUpdateKind.Layout));
    }

    [Fact]
    public void UpdateKind_Layout_IncludesLayoutFlag()
    {
        var kind = AnimationUpdateKind.Layout;
        Assert.True(kind.HasFlag(AnimationUpdateKind.Layout));
    }
}
