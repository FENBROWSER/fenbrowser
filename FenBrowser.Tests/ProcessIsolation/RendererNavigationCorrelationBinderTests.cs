using FenBrowser.Core.Engine;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

// High-risk finding: the renderer child previously kept one mutable "current
// correlation" that every forwarded transition reused, so a navigation started
// inside the child (script location change, input activation) inherited the
// previous WebDriver correlation. The binder keys correlations by engine
// navigation id instead.
public class RendererNavigationCorrelationBinderTests
{
    [Fact]
    public void RequestedInNavigateWindow_BindsEnvelopeCorrelation()
    {
        var binder = new RendererNavigationCorrelationBinder();
        binder.BeginNavigation("webdriver-1");

        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Requested));
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Interactive));
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Complete));
    }

    [Fact]
    public void ScriptInitiatedNavigation_AfterEnvelopeCompleted_BindsNoCorrelation()
    {
        var binder = new RendererNavigationCorrelationBinder();

        binder.BeginNavigation("webdriver-1");
        binder.Observe(101, NavigationLifecyclePhase.Requested);
        binder.Observe(101, NavigationLifecyclePhase.Complete);
        binder.EndNavigation();

        // A later engine navigation started by the page itself must not inherit
        // the WebDriver correlation.
        Assert.Equal(string.Empty, binder.Observe(102, NavigationLifecyclePhase.Requested));
        Assert.Equal(string.Empty, binder.Observe(102, NavigationLifecyclePhase.Interactive));
        Assert.Equal(string.Empty, binder.Observe(102, NavigationLifecyclePhase.Complete));
    }

    [Fact]
    public void TerminalPhase_PrunesBindingSoItCannotBeReused()
    {
        var binder = new RendererNavigationCorrelationBinder();
        binder.BeginNavigation("webdriver-1");
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Requested));
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Complete));

        // A replayed/stale transition of the same engine navigation id must not
        // resurrect the consumed correlation.
        Assert.Equal(string.Empty, binder.Observe(101, NavigationLifecyclePhase.Interactive));
    }

    [Fact]
    public void InFlightNavigationKeepsBindingWhenNextEnvelopeBegins()
    {
        var binder = new RendererNavigationCorrelationBinder();

        binder.BeginNavigation("webdriver-1");
        binder.Observe(101, NavigationLifecyclePhase.Requested);
        binder.Observe(101, NavigationLifecyclePhase.Interactive);
        binder.EndNavigation();

        binder.BeginNavigation("webdriver-2");
        binder.Observe(102, NavigationLifecyclePhase.Requested);
        binder.EndNavigation();

        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Complete));
        Assert.Equal("webdriver-2", binder.Observe(102, NavigationLifecyclePhase.Complete));
    }

    [Fact]
    public void FailedNavigation_BindsThenPrunes()
    {
        var binder = new RendererNavigationCorrelationBinder();
        binder.BeginNavigation("webdriver-1");
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Requested));
        Assert.Equal("webdriver-1", binder.Observe(101, NavigationLifecyclePhase.Failed));
        Assert.Equal(string.Empty, binder.Observe(101, NavigationLifecyclePhase.Failed));
    }
}
