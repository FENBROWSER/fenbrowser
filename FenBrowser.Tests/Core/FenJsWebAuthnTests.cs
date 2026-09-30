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
[Collection("WebAuthnPlatform")]
public sealed class FenJsWebAuthnTests
{
    // With an authenticator (Windows Hello, or the broker's), get() runs a real ceremony
    // for this document's origin and resolves with a PublicKeyCredential whose buffers are
    // the authenticator's bytes.
    [Fact]
    public async Task Get_WithAnAuthenticator_ResolvesAPublicKeyCredential()
    {
        var fake = new WebAuthnClientTests.FakeAuthenticator();
        var previous = FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator;
        FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator = fake;
        try
        {
            var engine = await CreateEngineAsync();
            engine.Evaluate("""
                function hex(buffer) { return Array.from(new Uint8Array(buffer)).map(function (b) { return b.toString(16).padStart(2, '0'); }).join(''); }
                Promise.all([
                    PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable(),
                    navigator.credentials.get({ publicKey: {
                        challenge: new Uint8Array([1, 2, 3, 4]), rpId: 'example.test', userVerification: 'required',
                        allowCredentials: [{ type: 'public-key', id: new Uint8Array([9, 9]), transports: ['internal'] }] } })
                ]).then(function (r) {
                    var c = r[1];
                    var clientData = JSON.parse(new TextDecoder().decode(c.response.clientDataJSON));
                    globalThis.__result = [r[0], c instanceof PublicKeyCredential, c.type, c.id, hex(c.rawId),
                        hex(c.response.authenticatorData), hex(c.response.signature), hex(c.response.userHandle),
                        clientData.type, clientData.origin, clientData.challenge].join('|');
                }, function (e) { globalThis.__result = 'rejected ' + e.name + ' ' + e.message; });
                """);
            Assert.Equal(
                "true|true|public-key|AQID|010203|040506|070809|0a0b0c|webauthn.get|https://example.test|AQIDBA",
                await WaitForAsync(engine, "globalThis.__result"));
            Assert.Equal("https://example.test", FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnClient.SerializeOrigin(fake.LastGet!.Origin));
            Assert.Equal("example.test", fake.LastGet.RpId);
            Assert.Equal("required", fake.LastGet.UserVerification);
            Assert.Equal("CQk", Assert.Single(fake.LastGet.AllowCredentials).Id);
        }
        finally
        {
            FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator = previous;
        }
    }

    [Fact]
    public async Task Create_WithAnAuthenticator_PassesTheRegistrationAndMapsErrors()
    {
        var fake = new WebAuthnClientTests.FakeAuthenticator();
        var previous = FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator;
        FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator = fake;
        try
        {
            var engine = await CreateEngineAsync();
            engine.Evaluate("""
                globalThis.__results = [];
                var options = { publicKey: {
                    challenge: new Uint8Array([5]), rp: { id: 'example.test', name: 'Example' },
                    user: { id: new Uint8Array([7, 7]), name: 'u@example.test', displayName: 'U' },
                    pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
                    authenticatorSelection: { residentKey: 'required', userVerification: 'required' } } };
                navigator.credentials.create(options).then(function (c) {
                    __results.push('created ' + c.response.getTransports().join(',') + ' ' + c.authenticatorAttachment + ' ' + new Uint8Array(c.response.attestationObject).length);
                }, function (e) { __results.push('create rejected ' + e.name); });
                """);
            Assert.Equal("created internal platform 3", await WaitForAsync(engine, "__results.length === 1 ? __results[0] : ''"));
            Assert.Equal(new[] { -7, -257 }, fake.LastCreate!.Algorithms);
            Assert.Equal("Bwc", fake.LastCreate.UserId);
            Assert.Equal("required", fake.LastCreate.ResidentKey);

            fake.NextResult = FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnResult.Failure("InvalidStateError", "exists");
            engine.Evaluate("navigator.credentials.create(options).then(function () { __results.push('resolved'); }, function (e) { __results.push(e.name); });");
            Assert.Equal("InvalidStateError", await WaitForAsync(engine, "__results.length === 2 ? __results[1] : ''"));
        }
        finally
        {
            FenBrowser.FenEngine.WebAPIs.WebAuthn.WebAuthnPlatform.Authenticator = previous;
        }
    }

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
