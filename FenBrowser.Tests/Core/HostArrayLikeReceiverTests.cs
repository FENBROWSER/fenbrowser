using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// ECMA-262 23.1.3: the Array.prototype methods are generic, so a host array-like (a
/// NamedNodeMap, a NodeList) works as their receiver. WPT's test_serializer does exactly
/// this with Array.prototype.map.call(element.attributes, ...).
/// </summary>
public sealed class HostArrayLikeReceiverTests
{
    [Fact]
    public async Task ArrayMethodsAcceptHostArrayLikes()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body class=x id=y><b></b><i></i></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(navigate: _ => { }, post: (_, _) => { }, status: _ => { }, log: _ => { })) { Sandbox = SandboxPolicy.AllowAll };
        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            [
                Array.prototype.map.call(document.body.attributes, function (a) { return a.localName + '=' + a.value; }).join(','),
                Array.prototype.slice.call(document.body.attributes).length,
                Array.prototype.map.call(document.body.childNodes, function (n) { return n.localName; }).join(','),
                Array.prototype.some.call(document.body.childNodes, function (n) { return n.localName === 'i'; }),
                Array.prototype.indexOf.call(document.body.childNodes, document.body.lastChild),
                Array.prototype.reduce.call(document.body.attributes, function (acc, a) { return acc + a.localName; }, ''),
                Array.prototype.join.call(document.body.childNodes, '|').length > 0
            ].join(';')
            """)?.ToString();

        Assert.Equal("class=x,id=y;2;b,i;true;1;classid;true", result);
    }
}
