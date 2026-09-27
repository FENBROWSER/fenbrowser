using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// An about:blank child document resolves relative URLs against its creator's base URL
    /// (HTML "fallback base URL"), and document.open() gives it the entry document's URL
    /// (HTML 8.4.1 document open steps, step 13). Sites such as the w3schools "try it"
    /// editor write a whole page with relative media sources into a fresh iframe this way.
    /// </summary>
    public sealed class BlankFrameDocumentUrlTests
    {
        [Fact]
        public async Task ABlankFrameUsesItsCreatorsBaseUrl()
        {
            var engine = await Load("<script>var f = document.createElement('iframe'); document.body.appendChild(f); globalThis.__r = f.contentDocument.URL + '|' + f.contentDocument.baseURI;</script>");
            Assert.Equal("about:blank|https://parent.test/tags/tryit.asp", engine.Evaluate("globalThis.__r")?.ToString());
        }

        [Fact]
        public async Task DocumentOpenGivesTheFrameTheEntryDocumentsUrl()
        {
            var engine = await Load(
                "<script>var f = document.createElement('iframe'); document.body.appendChild(f);" +
                "var w = f.contentWindow; w.document.open(); w.document.write('<audio><source src=\"horse.ogg\"></audio>'); w.document.close();" +
                "globalThis.__r = w.document.URL + '|' + w.document.querySelector('source').src;</script>");
            Assert.Equal("https://parent.test/tags/tryit.asp|https://parent.test/tags/horse.ogg", engine.Evaluate("globalThis.__r")?.ToString());
        }

        private static async Task<FenJsBrowserScriptEngine> Load(string body)
        {
            var baseUri = new Uri("https://parent.test/tags/tryit.asp");
            var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return engine;
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
