using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// HTML 7.2.2 indexed access on the Window: window[n] is the n-th child navigable's
    /// WindowProxy from the moment the iframe is in the document (4.8.5), before its
    /// document has loaded, and it is the same object as frames[n] and the iframe's
    /// contentWindow, then and after the load.
    /// </summary>
    public sealed class WindowIndexedFrameAccessTests
    {
        [Fact]
        public async Task ParentReachesTheFrameWindowBeforeItLoads()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body><iframe id='child' src='https://parent.test/child'></iframe>" +
                "<script>globalThis.__before = typeof window[0] + ':' + (window[0] === document.getElementById('child').contentWindow) + ':' + (window[0] === window.frames[0]) + ':' + window.length;</script></body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.Equal("object:true:true:1", engine.Evaluate("globalThis.__before")?.ToString());

            var frameUri = new Uri("https://parent.test/child");
            var frameDocument = new HtmlParser("<html><body id='child-body'></body></html>", frameUri).Parse();
            Assert.IsType<Element>(document.GetElementById("child")).AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            Assert.Equal(
                "true:true:https://parent.test/child",
                engine.Evaluate("String(window[0] === window.frames[0] && window[0] === document.getElementById('child').contentWindow) + ':' + (window[0].document === document.getElementById('child').contentDocument) + ':' + window[0].document.URL")?.ToString());
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
