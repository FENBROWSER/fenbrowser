using System;
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
