using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsReflectedHtmlElementPropertyTests
{
    [Fact]
    public async Task GoogleChallengeProperties_ReflectAttributesAndStandardObjects()
    {
        var result = await EvaluateAsync(
            "var iframe=document.createElement('iframe');" +
            "iframe.sandbox='allow-scripts allow-forms';iframe.scrolling='no';iframe.title='challenge';" +
            "var meta=document.createElement('meta');meta.httpEquiv='content-security-policy';" +
            "var script=document.createElement('script');" +
            "var asyncBefore=script.async;script.async=true;var asyncSet=script.hasAttribute('async');" +
            "script.async=false;var asyncCleared=!script.hasAttribute('async');" +
            "script.type='module';script.charset='utf-8';script.crossOrigin='anonymous';script.integrity='sha256-test';" +
            "var crossSet=script.getAttribute('crossorigin');script.crossOrigin=null;" +
            "var input=document.createElement('input');document.body.appendChild(input);input.focus();" +
            "[iframe.getAttribute('sandbox'),iframe.scrolling,iframe.title," +
            "meta.getAttribute('http-equiv'),meta.httpEquiv," +
            "asyncBefore,asyncSet,asyncCleared,script.type,script.charset,crossSet," +
            "script.crossOrigin===null,script.integrity,document.activeElement===input," +
            "typeof location.toString,location.toString(),String(location)].join('|');");

        Assert.Equal(
            "allow-scripts allow-forms|no|challenge|content-security-policy|content-security-policy|" +
            "false|true|true|module|utf-8|anonymous|true|sha256-test|true|function|" +
            "https://fixture.test/challenge.html|https://fixture.test/challenge.html",
            result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/challenge.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser("<html><head></head><body></body></html>", baseUri).Parse();
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
