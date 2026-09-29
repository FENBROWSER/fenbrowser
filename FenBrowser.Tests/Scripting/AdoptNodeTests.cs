using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// Document.adoptNode (DOM Standard §4.5, "adopt"): the node leaves its parent
    /// and it and its subtree become owned by the adopting document.
    /// </summary>
    public sealed class AdoptNodeTests
    {
        [Fact]
        public async Task AdoptNode_MovesASubtreeFromAnotherDocument()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                var other = document.implementation.createDocument('http://www.w3.org/2000/svg', 'svg');
                var g = other.createElementNS('http://www.w3.org/2000/svg', 'g');
                var child = other.createElementNS('http://www.w3.org/2000/svg', 'rect');
                g.appendChild(child);
                other.documentElement.appendChild(g);
                var adopted = document.adoptNode(g);
                [adopted === g, g.parentNode === null, other.documentElement.childNodes.length,
                 g.ownerDocument === document, child.ownerDocument === document].join('|');
                """);

            Assert.Equal("true|true|0|true|true", result?.ToString());
        }

        [Fact]
        public async Task AdoptNode_RemovesANodeOfTheSameDocumentFromItsParent()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                var g = document.getElementById('g');
                document.adoptNode(g);
                [g.parentNode === null, document.getElementById('g') === null, g.ownerDocument === document].join('|');
                """);

            Assert.Equal("true|true|true", result?.ToString());
        }

        [Fact]
        public async Task AdoptNode_RejectsDocumentsAndNonNodes()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                function name(f) { try { f(); return 'none'; } catch (e) { return e.name; } }
                [name(function () { document.adoptNode(document); }),
                 name(function () { document.adoptNode({}); })].join('|');
                """);

            Assert.Equal("NotSupportedError|TypeError", result?.ToString());
        }

        private static async Task<FenJsBrowserScriptEngine> CreateAsync()
        {
            var document = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg'><g id='g'/></svg>", "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/adopt.svg"));
            return engine;
        }
    }
}
