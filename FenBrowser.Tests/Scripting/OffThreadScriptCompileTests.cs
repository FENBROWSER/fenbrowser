using System;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// Classic scripts of 64K characters or more are compiled on a thread of their
    /// own before the script thread runs them. These pin that the page cannot tell:
    /// order, currentScript, source text, and a broken script failing alone stay as they were.
    /// </summary>
    public sealed class OffThreadScriptCompileTests
    {

        // A body well past the off-thread threshold, ending in `tail`.
        private static string LargeScript(string tail)
        {
            var builder = new StringBuilder();
            for (var i = 0; builder.Length < 96 * 1024; i++)
            {
                builder.Append("function filler").Append(i).Append("(a, b) { return a * ").Append(i).Append(" + b; }\n");
            }

            return builder.Append(tail).ToString();
        }

        [Fact]
        public async Task LargeBlockingScript_RunsInDocumentOrderWithCurrentScriptAndSource()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><script>globalThis.__order = ['before'];</script>" +
                "<script src=\"/big.js\"></script>" +
                "<script>globalThis.__order.push('after');</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            engine.AllowExternalScripts = true;
            var big = LargeScript(
                "globalThis.__order.push('big'); globalThis.__src = document.currentScript.src; " +
                "globalThis.__fillerSource = String(filler7); globalThis.__fillerValue = filler7(2, 3);");
            engine.ExternalScriptFetcher = (_, _) => Task.FromResult(big);

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("before,big,after", engine.Evaluate("globalThis.__order.join(',')")?.ToString());
            Assert.Equal("https://example.com/big.js", engine.Evaluate("globalThis.__src")?.ToString());
            Assert.Equal("function filler7(a, b) { return a * 7 + b; }", engine.Evaluate("globalThis.__fillerSource")?.ToString());
            Assert.Equal("17", engine.Evaluate("String(globalThis.__fillerValue)")?.ToString());
        }

        [Fact]
        public async Task LargeScriptWithSyntaxError_DoesNotRunAndTheNextScriptStillRuns()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><script src=\"/broken.js\"></script>" +
                "<script>globalThis.__after = true;</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            engine.AllowExternalScripts = true;
            var broken = LargeScript("globalThis.__brokenRan = true; var = ;");
            engine.ExternalScriptFetcher = (_, _) => Task.FromResult(broken);

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__after === true"));
            Assert.Equal(true, engine.Evaluate("globalThis.__brokenRan === undefined"));
        }

        [Fact]
        public async Task LargeDynamicScript_ExecutesBeforeItsLoadEvent()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='state'>pending</div><script>" +
                "document.addEventListener('DOMContentLoaded', function () {" +
                " var s = document.createElement('script'); s.src = '/chunk.js';" +
                " s.onload = function () { globalThis.__ranBeforeLoad = globalThis.__chunkRan === true; document.getElementById('state').textContent = 'loaded'; };" +
                " document.body.appendChild(s); });</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            engine.AllowExternalScripts = true;
            var chunk = LargeScript("globalThis.__chunkRan = true;");
            engine.ExternalScriptFetcher = (_, _) => Task.FromResult(chunk);

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            for (var i = 0; i < 200 && engine.Evaluate("document.getElementById('state').textContent")?.ToString() != "loaded"; i++)
            {
                await Task.Delay(25);
            }

            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__ranBeforeLoad"));
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
