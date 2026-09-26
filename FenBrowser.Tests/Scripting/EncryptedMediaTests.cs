using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Encrypted Media Extensions as a page sees it: requesting access to Clear Key, creating
/// keys and a session, the license request message, and what a licence does to the key
/// statuses. The cases follow <c>encrypted-media/scripts/requestmediakeysystemaccess.js</c>
/// and <c>keystatuses.js</c>.
/// </summary>
public sealed class EncryptedMediaTests
{
    private const string VideoType = "video/mp4;codecs=\"avc1.4d401e\"";

    [Fact]
    public async Task TheClearKeyInterfacesArePresent()
    {
        var engine = await CreateEngineAsync();
        foreach (string name in new[]
        {
            "MediaKeys", "MediaKeySession", "MediaKeySystemAccess", "MediaKeyStatusMap",
            "MediaKeyMessageEvent", "MediaEncryptedEvent",
        })
        {
            Assert.Equal("function", engine.Evaluate($"typeof {name}")?.ToString());
        }

        Assert.Equal("function", engine.Evaluate("typeof navigator.requestMediaKeySystemAccess")?.ToString());
        Assert.Equal("2", engine.Evaluate("String(navigator.requestMediaKeySystemAccess.length)")?.ToString());
    }

    [Fact]
    public async Task AccessIsGrantedForClearKeyAndReportsWhatWasAgreed()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{" +
            "  initDataTypes: ['fakeidt', 'cenc']," +
            $"  videoCapabilities: [{{ contentType: 'video/fake' }}, {{ contentType: '{VideoType}' }}]" +
            "}]).then(function (access) {" +
            "  var c = access.getConfiguration();" +
            "  __out = access.keySystem + '|' + c.initDataTypes.join(',') + '|' + c.videoCapabilities.length" +
            "    + '|' + c.videoCapabilities[0].contentType;" +
            "}, function (e) { __out = 'rejected:' + e.name; });");

        Assert.Equal($"org.w3.clearkey|cenc|1|{VideoType}", await WaitForAsync(engine, "__out"));
    }

    [Theory]
    // The key system name is matched exactly, and an empty one is a TypeError before
    // anything is asked of the implementation.
    [InlineData("''", "TypeError")]
    [InlineData("'com.example.unsupported'", "NotSupportedError")]
    [InlineData("'org.w3.clearkey.'", "NotSupportedError")]
    [InlineData("'ORG.W3.CLEARKEY'", "NotSupportedError")]
    [InlineData("'webkit-org.w3.clearkey'", "NotSupportedError")]
    public async Task AnUnsupportedKeySystemIsRefused(string keySystem, string error)
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            $"navigator.requestMediaKeySystemAccess({keySystem}, [{{ videoCapabilities: [{{ contentType: '{VideoType}' }}] }}])" +
            ".then(function () { __out = 'resolved'; }, function (e) { __out = e.name; });");

        Assert.Equal(error, await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task AnEmptyConfigurationListIsATypeError()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [])" +
            ".then(function () { __out = 'resolved'; }, function (e) { __out = e.name; });");

        Assert.Equal("TypeError", await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task AnEmptyConfigurationIsNotSupported()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{}])" +
            ".then(function () { __out = 'resolved'; }, function (e) { __out = e.name; });");

        Assert.Equal("NotSupportedError", await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task ASessionAsksForALicenceAndTakesTheKeysItIsGiven()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "globalThis.__steps = [];" +
            "var keyIdB64 = 'rRP56ivmmLh19QSo48zqZA';" +
            "var initData = new TextEncoder().encode(JSON.stringify({ kids: [keyIdB64] }));" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{" +
            "  initDataTypes: ['keyids']," +
            $"  videoCapabilities: [{{ contentType: '{VideoType}' }}]" +
            "}]).then(function (access) {" +
            "  return access.createMediaKeys();" +
            "}).then(function (keys) {" +
            "  var session = keys.createSession();" +
            "  globalThis.__session = session;" +
            "  __steps.push('sessionId:' + (session.sessionId === '' ? 'empty' : 'set'));" +
            "  __steps.push('statuses:' + session.keyStatuses.size);" +
            "  session.addEventListener('message', function (ev) {" +
            "    __steps.push('message:' + ev.messageType);" +
            "    __steps.push('kids:' + JSON.parse(new TextDecoder().decode(ev.message)).kids.join(','));" +
            "    session.addEventListener('keystatuseschange', function () {" +
            "      var out = [];" +
            "      session.keyStatuses.forEach(function (status) { out.push(status); });" +
            "      __steps.push('changed:' + out.join(','));" +
            "      __out = __steps.join('|');" +
            "    });" +
            "    session.update(new TextEncoder().encode(JSON.stringify({ keys: [{" +
            "      kty: 'oct', kid: keyIdB64, k: 'vn34o2Z6ao_VZNDtgTOalQ' }] })));" +
            "  });" +
            "  return session.generateRequest('keyids', initData).then(function () {" +
            "    __steps.push('requested:' + (session.sessionId !== '' ? 'identified' : 'anonymous'));" +
            "  });" +
            "}).catch(function (e) { __out = 'failed:' + e.name + ':' + e.message; });");

        string result = await WaitForAsync(engine, "__out");
        Assert.Equal(
            "sessionId:empty|statuses:0|requested:identified|message:license-request|kids:rRP56ivmmLh19QSo48zqZA|changed:usable",
            result);
    }

    [Fact]
    public async Task AnInvalidLicenceRejectsUpdateWithATypeError()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "var initData = new TextEncoder().encode(JSON.stringify({ kids: ['rRP56ivmmLh19QSo48zqZA'] }));" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{" +
            "  initDataTypes: ['keyids']," +
            $"  videoCapabilities: [{{ contentType: '{VideoType}' }}]" +
            "}]).then(function (access) { return access.createMediaKeys(); })" +
            ".then(function (keys) {" +
            "  var session = keys.createSession();" +
            "  session.addEventListener('message', function () {" +
            "    session.update(new Uint8Array([0, 17, 34, 51])).then(" +
            "      function () { __out = 'resolved'; }," +
            "      function (e) { __out = e.name; });" +
            "  });" +
            "  return session.generateRequest('keyids', initData);" +
            "}).catch(function (e) { __out = 'failed:' + e.name; });");

        Assert.Equal("TypeError", await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task ClosingASessionEmptiesItsStatusesAndSettlesItsClosedPromise()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "var initData = new TextEncoder().encode(JSON.stringify({ kids: ['rRP56ivmmLh19QSo48zqZA'] }));" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{" +
            "  initDataTypes: ['keyids']," +
            $"  videoCapabilities: [{{ contentType: '{VideoType}' }}]" +
            "}]).then(function (access) { return access.createMediaKeys(); })" +
            ".then(function (keys) {" +
            "  var session = keys.createSession();" +
            "  session.closed.then(function () { __out = 'closed:' + session.keyStatuses.size; });" +
            "  return session.generateRequest('keyids', initData).then(function () { return session.close(); });" +
            "}).catch(function (e) { __out = 'failed:' + e.name; });");

        Assert.Equal("closed:0", await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task AMediaElementTakesAndReportsItsMediaKeys()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "var video = document.createElement('video');" +
            "document.body.appendChild(video);" +
            "var before = video.mediaKeys === null ? 'none' : 'some';" +
            "navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{" +
            $"  videoCapabilities: [{{ contentType: '{VideoType}' }}]" +
            "}]).then(function (access) { return access.createMediaKeys(); })" +
            ".then(function (keys) {" +
            "  return video.setMediaKeys(keys).then(function () {" +
            "    return video.setMediaKeys(null).then(function () {" +
            "      __out = before + '|' + (video.mediaKeys === null ? 'cleared' : 'kept');" +
            "    });" +
            "  });" +
            "}).catch(function (e) { __out = 'failed:' + e.name + ':' + e.message; });");

        Assert.Equal("none|cleared", await WaitForAsync(engine, "__out"));
    }

    [Fact]
    public async Task SetMediaKeysRefusesSomethingThatIsNotMediaKeys()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__out = '';" +
            "var video = document.createElement('video');" +
            "video.setMediaKeys({}).then(function () { __out = 'resolved'; }, function (e) { __out = e.name; });");

        Assert.Equal("TypeError", await WaitForAsync(engine, "__out"));
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }
}
