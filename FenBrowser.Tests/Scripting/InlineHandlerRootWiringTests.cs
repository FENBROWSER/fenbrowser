using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// Event handler content attributes on the element the engine is handed as its
    /// DOM root. Only its descendants used to be wired, so the root's own handlers
    /// (&lt;svg onload&gt; in an SVG document, &lt;html onclick&gt;) never ran.
    /// </summary>
    public sealed class InlineHandlerRootWiringTests
    {
        [Fact]
        public async Task TheRootElementsHandlerAttributes_AreWired()
        {
            var document = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg' onclick=\"globalThis.__clicked = event.type\"><g/></svg>",
                "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/root.svg"));

            engine.DispatchEventForElement(document.DocumentElement, "click");

            Assert.Equal("click", engine.Evaluate("globalThis.__clicked")?.ToString());
        }
    }
}
