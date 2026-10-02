using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// WebIDL 3.7.6 puts interface attributes and operations on the interface prototype,
/// so a DOM node's own properties are only what script stored on it (and Document's
/// [LegacyUnforgeable] location). Reporting `hidden` as own made Polymer treat it as a
/// value set before upgrade and re-apply it as undefined over YouTube's hidden = true,
/// leaving the pre-warmed watch page painted over the home feed.
/// </summary>
public sealed class PlatformObjectOwnPropertyTests
{
    [Fact]
    public async Task InterfaceMembersAreNotOwn_ExpandosAre()
    {
        var engine = await StartAsync(
            """
            <script>
            var d = document.createElement('div');
            d.__data = { x: 1 };
            globalThis.__r = JSON.stringify({
                hidden: d.hasOwnProperty('hidden'),
                id: d.hasOwnProperty('id'),
                title: Object.hasOwn(d, 'title'),
                appendChild: d.hasOwnProperty('appendChild'),
                expando: d.hasOwnProperty('__data'),
                expandoHasOwn: Object.hasOwn(d, '__data'),
                missing: d.hasOwnProperty('nothingHere'),
                docLocation: document.hasOwnProperty('location'),
                docBody: document.hasOwnProperty('body')
            });
            </script>
            """);

        try
        {
            Assert.Equal(
                "{\"hidden\":false,\"id\":false,\"title\":false,\"appendChild\":false,\"expando\":true,\"expandoHasOwn\":true,\"missing\":false,\"docLocation\":true,\"docBody\":false}",
                engine.Evaluate("String(globalThis.__r)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task PolymerStylePropertyDefault_DoesNotOverwriteAValueSetAfterConstruction()
    {
        // Polymer captures own properties at construction as "instance values set before
        // upgrade" and replays them on first connect; a page's later hidden = true must win.
        var engine = await StartAsync(
            """
            <script>
            class Page extends HTMLElement {
                constructor() {
                    super();
                    this.__data = {};
                    if (!this.hasOwnProperty('hidden')) this.__data.hidden = false;
                    else this.__instance = this.hidden;
                }
                get hidden() { return this.__data.hidden; }
                set hidden(v) { this.__data.hidden = v; if (v) this.setAttribute('hidden', ''); else this.removeAttribute('hidden'); }
                connectedCallback() { if ('__instance' in this) this.hidden = this.__instance; }
            }
            customElements.define('x-page', Page);
            var p = document.createElement('x-page');
            p.hidden = true;
            document.body.appendChild(p);
            globalThis.__r = String(p.hidden) + '/' + p.hasAttribute('hidden');
            </script>
            """);

        try
        {
            Assert.Equal("true/true", engine.Evaluate("String(globalThis.__r)")?.ToString());
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
