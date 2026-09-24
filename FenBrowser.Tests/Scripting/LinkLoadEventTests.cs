using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// HTML 4.6.7 fetch and process the linked resource, for rel=stylesheet.
[Collection("Engine Tests")]
public sealed class LinkLoadEventTests : IDisposable
{
    public LinkLoadEventTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    // good.css is text/css, plain.txt is text/plain, anything else is a 404.
    private static Task<HttpResponseMessage> Serve(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        HttpResponseMessage response;
        if (path.EndsWith("good.css", StringComparison.Ordinal) || path.EndsWith("plain.txt", StringComparison.Ordinal))
        {
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("p{}", System.Text.Encoding.UTF8,
                    path.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/plain")
            };
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    private static FenJsBrowserScriptEngine Load(string html)
    {
        var baseUri = new Uri("https://links.test/page.html");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll,
            FetchHandler = Serve
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void ParserInsertedLinksReportBeforeTheWindowLoadEvent()
    {
        var engine = Load(@"<!doctype html><script>window.log = [];
            addEventListener('load', function () { log.push('window'); });</script>
            <link rel=stylesheet href='good.css' onload=""log.push('good')"" onerror=""log.push('good-error')"">
            <link rel=stylesheet href='missing.css' onload=""log.push('missing-load')"" onerror=""log.push('missing')"">
            <link rel=stylesheet href='plain.txt' onload=""log.push('plain-load')"" onerror=""log.push('plain')"">
            <link rel=stylesheet href='data:text/css,@import url(good.css);' onload=""log.push('import')"" onerror=""log.push('import-error')"">
            <link rel=stylesheet href='data:text/css,@import url(missing.css);' onload=""log.push('bad-import-load')"" onerror=""log.push('bad-import')"">");

        Assert.Equal("good,missing,plain,import,bad-import,window", engine.Evaluate("log.join(',')")?.ToString());
    }
}
