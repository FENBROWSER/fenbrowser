using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Platform exceptions reach script as DOMException instances (WebIDL §3.14),
/// host objects inherit Object.prototype, and the document exposes DOM
/// traversal, collections and the XML factories.
/// </summary>
[Collection("Engine Tests")]
public sealed class FenJsDomExceptionAndTraversalTests
{
    private static async Task<string> RunAsync(string html, string script)
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html," + Uri.EscapeDataString(html)));
            var result = await browser.ExecuteScriptAsync("(function(){" + script + "})();");
            return result?.ToString() ?? string.Empty;
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task PlatformErrorsAreDomExceptionInstancesWithLegacyCodes()
    {
        var result = await RunAsync(
            "<p id='p'></p>",
            @"var out = [];
              try { document.createElement('<div>'); } catch (e) { out.push(e instanceof DOMException, e.name, e.code, e.code === DOMException.INVALID_CHARACTER_ERR, e.constructor === DOMException); }
              try { document.createCDATASection('x'); } catch (e) { out.push(e.name, e.code); }
              out.push(DOMException.HIERARCHY_REQUEST_ERR, DOMException.prototype.NOT_FOUND_ERR, Object.prototype.toString.call(new DOMException('m', 'AbortError')));
              return out.join('|');");

        Assert.Equal("true|InvalidCharacterError|5|true|true|NotSupportedError|9|3|8|[object DOMException]", result);
    }

    [Fact]
    public async Task HostObjectsInheritObjectPrototype()
    {
        var result = await RunAsync(
            "<p id='p'></p>",
            @"var w = document.createTreeWalker(document.body);
              var c = document.getElementsByTagName('p');
              return [typeof w.toString, w.hasOwnProperty === Object.prototype.hasOwnProperty, Object.prototype.toString.call(w), Object.prototype.toString.call(c)].join('|');");

        Assert.Equal("function|true|[object TreeWalker]|[object HTMLCollection]", result);
    }

    [Fact]
    public async Task NodeIteratorForwardsFilterExceptionsAndSkipsWhitespace()
    {
        var result = await RunAsync(
            "<div id='d'> <b>x</b> </div>",
            @"var out = [];
              var it = document.createNodeIterator(document.getElementById('d'), NodeFilter.SHOW_ALL, function (n) {
                  return n.nodeType === 3 && /^\s*$/.test(n.data) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
              });
              out.push(it.nextNode().id, it.nextNode().nodeName, it.nextNode().data, it.nextNode());
              var boom = {};
              var thrower = document.createNodeIterator(document.body, 0xFFFFFFFF, function () { throw boom; });
              try { thrower.nextNode(); } catch (e) { out.push(e === boom); }
              var lazy = document.createTreeWalker(document.body, 0xFFFFFFFF, {});
              try { lazy.firstChild(); out.push('no-throw'); } catch (e) { out.push(e instanceof TypeError); }
              return out.join('|');");

        Assert.Equal("d|B|x||true|true", result);
    }

    [Fact]
    public async Task DocumentExposesCollectionsDoctypeAndXmlFactories()
    {
        var result = await RunAsync(
            "<!DOCTYPE html><form name='f'><input></form><a href='#'>l</a>",
            @"var doctype = document.implementation.createDocumentType('html', 'pub', 'sys');
              var xml = document.implementation.createDocument(null, null, doctype);
              var pi = xml.createProcessingInstruction('t', 'd');
              xml.appendChild(pi);
              var cdata = xml.createCDATASection('c');
              return [document.forms.length, document.forms.f === document.forms[0], document.links.length,
                      document.doctype.name, Object.prototype.toString.call(document.doctype),
                      xml.doctype.publicId, xml.firstChild === doctype, pi.target, pi.nodeType, cdata.nodeName,
                      Object.prototype.toString.call(new Document()), typeof document.body.hasChildNodes,
                      document.doctype.hasChildNodes()].join('|');");

        Assert.Equal("1|true|1|html|[object DocumentType]|pub|true|t|7|#cdata-section|[object Document]|function|false", result);
    }
}
