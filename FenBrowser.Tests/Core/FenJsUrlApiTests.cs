using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsUrlApiTests
{
    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/dir/page");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    // WHATWG URL 6.1: the URL object's components are live accessors. Setting
    // one re-serializes the URL; Polymer's resolveUrl feature-detects exactly
    // `u.pathname = 'c%20d'` before it trusts the URL API at all.
    [Fact]
    public async Task SettersReserializeTheUrl()
    {
        var engine = await CreateEngineAsync();

        var result = engine.Evaluate("""
            (function () {
                var out = [];
                var u = new URL('b', 'http://a');
                u.pathname = 'c%20d';
                out.push(u.href);
                u.pathname = 'a b';
                out.push(u.pathname);
                u.search = 'x=1';
                u.hash = 'top';
                out.push(u.href);
                u.search = '';
                u.hash = '';
                out.push(u.href);
                u.hostname = 'example.org';
                u.port = '8080';
                out.push(u.host + ' ' + u.origin);
                u.protocol = 'https';
                out.push(u.href);
                u.href = 'https://other.test/p?q#f';
                out.push(u.host + u.pathname + u.search + u.hash);
                var opaque = new URL('mailto:someone@example.test');
                opaque.pathname = '/changed';
                out.push(opaque.href);
                return out.join('|');
            })();
            """);

        Assert.Equal(
            "http://a/c%20d|/a%20b|http://a/a%20b?x=1#top|http://a/a%20b|example.org:8080 http://example.org:8080|https://example.org:8080/a%20b|other.test/p?q#f|mailto:someone@example.test",
            result?.ToString());
    }

    // WHATWG URL 6.2: searchParams is a live view of the query in both directions.
    [Fact]
    public async Task SearchParamsStayInSyncWithSearch()
    {
        var engine = await CreateEngineAsync();

        var result = engine.Evaluate("""
            (function () {
                var u = new URL('https://wpt.fyi/results/?label=master');
                u.searchParams.append('q', 'a b');
                u.searchParams.set('label', 'experimental');
                var afterParams = u.href;
                u.search = '?sha=abc';
                var afterSearch = u.searchParams.get('sha') + ',' + u.searchParams.has('label');
                u.searchParams.delete('sha');
                return [afterParams, afterSearch, u.href].join('|');
            })();
            """);

        Assert.Equal(
            "https://wpt.fyi/results/?label=experimental&q=a+b|abc,false|https://wpt.fyi/results/",
            result?.ToString());
    }

    [Fact]
    public async Task InvalidUrlsThrowAndCanParseAnswersFalse()
    {
        var engine = await CreateEngineAsync();

        var result = engine.Evaluate("""
            (function () {
                var thrown = 'no';
                try { new URL('::'); } catch (e) { thrown = e instanceof TypeError ? 'TypeError' : e.name; }
                // 6.1 URL(url, base): no base means no document base either.
                var relative = 'no';
                try { new URL('components/x.js'); } catch (e) { relative = e instanceof TypeError ? 'TypeError' : e.name; }
                return [thrown, URL.canParse('::'), URL.canParse('b', 'http://a'), relative, URL.canParse('components/x.js')].join('|');
            })();
            """);

        Assert.Equal("TypeError|false|true|TypeError|false", result?.ToString());
    }

    // HTML 7.10 / 7.9: Location and History are interface objects on window even
    // though their instances are host singletons; Polymer's app-location declares
    // `location: { type: Location }` and reads the global.
    [Fact]
    public async Task LocationAndHistoryInterfaceObjectsExist()
    {
        var engine = await CreateEngineAsync();

        var result = engine.Evaluate("""
            (function () {
                return [
                    typeof Location, location instanceof Location, ({}) instanceof Location,
                    typeof History, history instanceof History
                ].join('|');
            })();
            """);

        Assert.Equal("function|true|false|function|true", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
