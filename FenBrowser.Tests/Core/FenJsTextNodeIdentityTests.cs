using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Closure's goog.dom.setTextContent relies on a node with one text child
/// answering <c>node.lastChild == node.firstChild</c>; reCAPTCHA uses it to
/// swap the challenge button between "Skip" and "Verify".
/// </summary>
public sealed class FenJsTextNodeIdentityTests
{
    [Fact]
    public async Task TextNodeWrapperIsTheSameObjectFromEveryAccessor()
    {
        var engine = await CreateEngineAsync("<html><body><button id=\"b\">Skip</button></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var b = document.getElementById('b');
                var out = [];
                out.push('first===last:' + (b.firstChild === b.lastChild));
                out.push('first===childNodes0:' + (b.firstChild === b.childNodes[0]));
                out.push('first===first:' + (b.firstChild === b.firstChild));
                out.push('textContent in:' + ('textContent' in b));
                out.push('nodeType:' + b.firstChild.nodeType);
                return out.join(' ');
            })();
            """);

        Assert.Equal("first===last:true first===childNodes0:true first===first:true textContent in:true nodeType:3", result?.ToString());
    }

    [Fact]
    public async Task ClosureSetTextContentReplacesSingleTextChild()
    {
        var engine = await CreateEngineAsync("<html><body><button id=\"b\">Skip</button></body></html>");

        var result = engine.Evaluate("""
            (function () {
                function setTextContent(node, text) {
                    if ('textContent' in node) {
                        node.textContent = text;
                    } else if (node.nodeType == 3) {
                        node.data = String(text);
                    } else if (node.firstChild && node.firstChild.nodeType == 3) {
                        while (node.lastChild != node.firstChild) node.removeChild(node.lastChild);
                        node.firstChild.data = String(text);
                    } else {
                        while (node.firstChild) node.removeChild(node.firstChild);
                        node.appendChild(node.ownerDocument.createTextNode(String(text)));
                    }
                }
                var b = document.getElementById('b');
                setTextContent(b, 'Verify');
                return b.textContent + '|' + b.childNodes.length;
            })();
            """);

        Assert.Equal("Verify|1", result?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
