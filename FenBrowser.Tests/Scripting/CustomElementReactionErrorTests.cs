using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML "invoke custom element reactions": an exception a reaction throws - the
/// upgrade constructor, connectedCallback - is caught and reported (§8.1.4.6), an
/// ErrorEvent at the window. A console line alone hid YouTube's ytd-app failing.
/// </summary>
public sealed class CustomElementReactionErrorTests
{
    [Fact]
    public async Task ExceptionsFromReactions_AreReportedAtTheWindow()
    {
        var engine = await StartAsync(
            """
            <script>
            var seen = [];
            window.addEventListener('error', function (e) { seen.push(e.message); });
            customElements.define('x-bad', class extends HTMLElement { connectedCallback() { throw new TypeError('in connected'); } });
            document.body.appendChild(document.createElement('x-bad'));
            customElements.define('x-bad2', class extends HTMLElement { constructor() { super(); throw new TypeError('in constructor'); } });
            var host = document.createElement('div');
            host.innerHTML = '<x-bad2></x-bad2>';
            document.body.appendChild(host);
            globalThis.__r = seen.join('|');
            </script>
            """);

        try
        {
            Assert.Equal(
                "Uncaught TypeError: in connected|Uncaught TypeError: in constructor",
                engine.Evaluate("String(globalThis.__r)")?.ToString());
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
