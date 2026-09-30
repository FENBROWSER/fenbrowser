using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Web Authentication with no authenticator attached. github.com's login loads its
/// whole passkey / "Continue with Google" / "Continue with Apple" block only when
/// navigator.credentials.create/get and PublicKeyCredential all exist.
/// </summary>
public sealed class FenJsWebAuthnTests
{
    [Fact]
    public async Task GitHubsFeatureDetection_SeesWebAuthn()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("true", engine.Evaluate(
            "String(!!(navigator.credentials && navigator.credentials.create && navigator.credentials.get && window.PublicKeyCredential))")?.ToString());
        Assert.Equal("TypeError", engine.Evaluate(
            "(function () { try { new PublicKeyCredential(); return 'constructed'; } catch (e) { return e.name; } })()")?.ToString());
    }

    // WebAuthn 5.1.7 / 5.1.8: nothing to offer, so both availability checks resolve false.
    [Fact]
    public async Task PlatformAuthenticatorAndConditionalUi_ReportUnavailable()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            Promise.all([
                PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable(),
                PublicKeyCredential.isConditionalMediationAvailable()
            ]).then(function (r) { globalThis.__result = r.join(','); });
            """);
        Assert.Equal("false,false", await WaitForAsync(engine, "globalThis.__result"));
    }

    // A ceremony with no usable authenticator ends in NotAllowedError, never a null credential.
    [Fact]
    public async Task PublicKeyCeremonies_RejectWithNotAllowedError()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            var challenge = new Uint8Array(32);
            globalThis.__results = [];
            function record(label, p) {
                p.then(function (v) { __results.push(label + ':resolved ' + v); },
                       function (e) { __results.push(label + ':' + e.name); });
            }
            record('get', navigator.credentials.get({ publicKey: { challenge: challenge, rpId: 'example.test' } }));
            record('create', navigator.credentials.create({ publicKey: {
                challenge: challenge, rp: { name: 'x' }, user: { id: challenge, name: 'u', displayName: 'u' },
                pubKeyCredParams: [{ type: 'public-key', alg: -7 }] } }));
            """);
        Assert.Equal("create:NotAllowedError,get:NotAllowedError",
            await WaitForAsync(engine, "__results.length === 2 ? __results.sort().join(',') : ''"));
    }

    // A conditional get() waits for an autofill pick; aborting its signal ends it.
    [Fact]
    public async Task ConditionalGet_StaysPendingUntilAborted()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            globalThis.__settled = 'pending';
            var controller = new AbortController();
            navigator.credentials.get({ mediation: 'conditional', signal: controller.signal, publicKey: { challenge: new Uint8Array(32) } })
                .then(function () { __settled = 'resolved'; }, function (e) { __settled = e.name; });
            """);
        await Task.Delay(100);
        Assert.Equal("pending", engine.Evaluate("globalThis.__settled")?.ToString());
        engine.Evaluate("controller.abort();");
        Assert.Equal("AbortError", await WaitForAsync(engine, "globalThis.__settled === 'pending' ? '' : globalThis.__settled"));
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(20);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
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
}
