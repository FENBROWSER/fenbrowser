using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7.2: only width, height and the insets resolve to used values. Reading
/// another property from getComputedStyle must not lay the document out; it used to
/// force a full layout on every call, thousands of times per YouTube load.
/// </summary>
public sealed class ComputedStyleLazyLayoutTests
{
    [Fact]
    public async Task OnlyLayoutDependentProperties_AskForLayout()
    {
        var engine = await StartAsync("<div id='d' style='display:block;width:50%'>x</div>");
        var layoutRequests = 0;
        engine.LayoutBoxResolver = _ =>
        {
            layoutRequests++;
            return null;
        };

        try
        {
            engine.Evaluate("var cs = getComputedStyle(document.getElementById('d')); globalThis.__d = cs.display + '|' + cs.getPropertyValue('display');");
            Assert.Equal(0, layoutRequests);
            Assert.Equal("block|block", engine.Evaluate("String(globalThis.__d)")?.ToString());

            engine.Evaluate("globalThis.__w = cs.width;");
            Assert.True(layoutRequests > 0);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<FenJsBrowserScriptEngine> StartAsync(string body)
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
