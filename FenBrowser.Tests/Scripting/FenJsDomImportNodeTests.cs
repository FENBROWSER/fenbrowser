using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsDomImportNodeTests
    {
        [Fact]
        public async Task Document_ExposesInheritedNodeChildTraversal()
        {
            var baseUri = new Uri("https://fen.test/document-node");
            var document = new HtmlParser(
                "<!doctype html><html><body><main>content</main></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                [
                    document.firstChild.nodeType,
                    document.lastChild.nodeName,
                    document.childNodes.length,
                    document.previousSibling === null,
                    document.nextSibling === null
                ].join('|')
                """);

            Assert.Equal("10|HTML|2|true|true", result?.ToString());
        }

        [Fact]
        public async Task TopLevelFenJsWork_UsesFreshInstructionBudget()
        {
            var baseUri = new Uri("https://fen.test/execution-budget");
            var document = new HtmlParser(
                "<html><body></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var interpreter = typeof(FenJsBrowserScriptEngine)
                .GetField("_interpreter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(engine);
            Assert.NotNull(interpreter);

            var instructionCount = interpreter.GetType()
                .GetField("_instructionCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(instructionCount);
            instructionCount.SetValue(interpreter, 100_000_000);

            var result = engine.Evaluate("'alive'");

            Assert.Equal("alive", result?.ToString());
        }

        [Fact]
        public async Task PerformanceNow_AdvancesDuringScriptExecution()
        {
            var baseUri = new Uri("https://fen.test/performance-now");
            var document = new HtmlParser(
                "<html><body></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var start = performance.now();
                    var iterations = 0;
                    while (performance.now() - start < 10 && iterations < 1000000) {
                        iterations++;
                    }
                    return [
                        String(performance.now() - start >= 10),
                        String(iterations < 1000000)
                    ].join('|');
                })();
                """);

            Assert.Equal("true|true", result?.ToString());
        }

        [Fact]
        public async Task Element_ExposesParentNodeElementTraversal()
        {
            var baseUri = new Uri("https://fen.test/element-traversal");
            var document = new HtmlParser(
                "<html><body><div id='root'><span id='first'></span>text<i id='last'></i></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var root = document.getElementById('root');
                    return [
                        root.firstElementChild.id,
                        root.lastElementChild.id,
                        root.childElementCount
                    ].join('|');
                })();
                """);

            Assert.Equal("first|last|2", result?.ToString());
        }

        [Fact]
        public async Task DocumentImportNode_ClonesTemplateFragmentIntoTargetDocument()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser(
                "<html><body><template id='tpl'><section id='card'><span>Video</span></section></template></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var target = document.implementation.createHTMLDocument('target');
                    var content = document.getElementById('tpl').content;
                    var imported = target.importNode(content, true);
                    imported.__noInsertionPoint = true;

                    if (content.firstChild.ownerDocument !== document) {
                        return 'source-owner';
                    }

                    var ownerBeforeInsert = imported.ownerDocument === target;
                    target.body.appendChild(imported);

                    return [
                        typeof target.importNode,
                        String(ownerBeforeInsert),
                        String(imported.__noInsertionPoint),
                        target.body.textContent,
                        String(target.querySelectorAll('section').length),
                        String(target.getElementById('card').ownerDocument === target)
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|true|Video|1|true", result?.ToString());
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
