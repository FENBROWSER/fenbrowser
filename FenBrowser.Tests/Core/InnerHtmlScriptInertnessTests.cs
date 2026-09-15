using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML "parsing HTML fragments" (13.4) marks scripts created by innerHTML and
/// insertAdjacentHTML as "already started", so they never run. bing.com's sj_appHTML
/// parses server markup through innerHTML and recreates each script element itself;
/// executing them during the parse as well ran every inline script twice.
/// </summary>
public sealed class InnerHtmlScriptInertnessTests
{
    [Fact]
    public void PageScriptEngine_DoesNotExecuteScriptsInsertedThroughInnerHtml()
    {
        using var engine = new CustomHtmlEngine();
        var setup = typeof(CustomHtmlEngine).GetMethod(
            "SetupJavaScriptEngine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setup);

        var scriptEngine = setup.Invoke(engine, new object[]
        {
            new Uri("https://example.test/"),
            new Action<Uri>(_ => { }),
            true,
            new Func<Uri, Task<string>>(_ => Task.FromResult(string.Empty)),
            1280d,
            800d
        });

        Assert.NotNull(scriptEngine);
        var flag = scriptEngine.GetType().GetProperty("ExecuteInlineScriptsOnInnerHTML");
        Assert.NotNull(flag);
        Assert.False((bool)flag.GetValue(scriptEngine));
    }
}
