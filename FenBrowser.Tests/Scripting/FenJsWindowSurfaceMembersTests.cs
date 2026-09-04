using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Window-level members real sites read that the bridge did not expose.
/// Expected values were taken from Chrome 152 rather than from memory:
/// location.port is "" on a default port, history.scrollRestoration is "auto",
/// and navigator.doNotTrack is null (not "0") when the preference is off.
/// </summary>
public sealed class FenJsWindowSurfaceMembersTests
{
    [Fact]
    public async Task LocationPort_IsEmptyOnDefaultPort()
    {
        var result = await EvaluateAsync(
            "typeof location.port + '|' + JSON.stringify(location.port);",
            new Uri("https://fixture.test/page.html"));

        Assert.Equal("string|\"\"", result);
    }

    [Fact]
    public async Task LocationPort_ReportsExplicitNonDefaultPort()
    {
        var result = await EvaluateAsync(
            "location.port;",
            new Uri("https://fixture.test:8443/page.html"));

        Assert.Equal("8443", result);
    }

    [Fact]
    public async Task LocationPort_IsEmptyWhenPortMatchesSchemeDefault()
    {
        var result = await EvaluateAsync(
            "JSON.stringify(location.port);",
            new Uri("https://fixture.test:443/page.html"));

        Assert.Equal("\"\"", result);
    }

    [Fact]
    public async Task HistoryScrollRestoration_DefaultsToAuto()
    {
        var result = await EvaluateAsync("history.scrollRestoration;");

        Assert.Equal("auto", result);
    }

    [Fact]
    public async Task HistoryScrollRestoration_AcceptsManual()
    {
        var result = await EvaluateAsync(
            "history.scrollRestoration = 'manual'; history.scrollRestoration;");

        Assert.Equal("manual", result);
    }

    [Fact]
    public async Task HistoryScrollRestoration_IgnoresValuesOutsideTheEnum()
    {
        var result = await EvaluateAsync(
            "history.scrollRestoration = 'manual';" +
            "history.scrollRestoration = 'nonsense';" +
            "history.scrollRestoration;");

        Assert.Equal("manual", result);
    }

    [Fact]
    public async Task NavigatorDoNotTrack_IsNullWhenThePreferenceIsOff()
    {
        var result = await EvaluateAsync(
            "String(navigator.doNotTrack) + '|' + (navigator.doNotTrack === null);");

        Assert.Equal("null|true", result);
    }

    [Fact]
    public async Task NavigatorDoNotTrack_IsPresentAsAMember()
    {
        var result = await EvaluateAsync("'doNotTrack' in navigator;");

        Assert.Equal("True", result, ignoreCase: true);
    }

    private static async Task<string> EvaluateAsync(string script, Uri baseUri = null)
    {
        baseUri ??= new Uri("https://fixture.test/window-surface.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='host'></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return engine.Evaluate(script)?.ToString();
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
