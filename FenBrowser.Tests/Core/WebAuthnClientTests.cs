using System.Text;
using FenBrowser.FenEngine.WebAPIs.WebAuthn;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.ProcessIsolation.Fuzz;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The client-side rules a WebAuthn client enforces itself (WebAuthn L3 §5.1.3 / §5.1.4.1,
/// §5.8.1) and the broker's checks on a renderer's ceremony request.
/// </summary>
[Collection("WebAuthnPlatform")]
public sealed class WebAuthnClientTests
{
    [Theory]
    [InlineData("https://github.com", null, "github.com")]
    [InlineData("https://github.com", "github.com", "github.com")]
    [InlineData("https://gist.github.com", "github.com", "github.com")]
    [InlineData("https://accounts.google.com:443", "google.com", "google.com")]
    [InlineData("http://localhost:5000", null, "localhost")]
    public void RpId_IsTheHostOrARegistrableSuffix(string origin, string? requested, string expected)
    {
        Assert.True(WebAuthnClient.TryResolveRpId(origin, requested, out var rpId, out _));
        Assert.Equal(expected, rpId);
    }

    [Theory]
    [InlineData("https://github.com", "evil.com")]          // not a suffix
    [InlineData("https://github.com", "hub.com")]           // not on a label boundary
    [InlineData("https://gist.github.io", "github.io")]     // github.io is a public suffix
    [InlineData("https://example.co.uk", "co.uk")]          // public suffix
    [InlineData("https://github.com", "com")]               // public suffix
    [InlineData("http://github.com", null)]                 // not a secure context
    [InlineData("https://127.0.0.1", null)]                 // IP addresses are not valid domains
    [InlineData("null", null)]                              // opaque origin
    [InlineData("https://github.com", "github.com:443")]
    public void RpId_ThatIsNotTheOriginsDomain_IsASecurityError(string origin, string? requested)
    {
        Assert.False(WebAuthnClient.TryResolveRpId(origin, requested, out _, out var error));
        Assert.Equal("SecurityError", error);
    }

    [Fact]
    public void ClientData_IsSerialisedInTheSpecifiedMemberOrder()
    {
        var json = Encoding.UTF8.GetString(WebAuthnClient.BuildClientDataJson(
            "webauthn.get", "EgLSx2Qfomy_4frKHAFfnq5jhRrr7aHinqeR-yB-f7k", "https://GitHub.com:443/login"));
        Assert.Equal(
            "{\"type\":\"webauthn.get\",\"challenge\":\"EgLSx2Qfomy_4frKHAFfnq5jhRrr7aHinqeR-yB-f7k\",\"origin\":\"https://github.com\",\"crossOrigin\":false}",
            json);
    }

    [Fact]
    public void Base64Url_RoundTrips()
    {
        var bytes = new byte[] { 0xfb, 0xff, 0x00, 0x10, 0x7e };
        Assert.Equal("-_8AEH4", WebAuthnClient.ToBase64Url(bytes));
        Assert.Equal(bytes, WebAuthnClient.FromBase64Url("-_8AEH4"));
    }

    // A renderer may only run a ceremony for the document its tab committed.
    [Theory]
    [InlineData("https://github.com", "https://github.com/login", true)]
    [InlineData("https://evil.example", "https://github.com/login", false)]
    [InlineData("https://github.com", null, false)]
    public async Task Broker_RunsACeremonyOnlyForTheTabsCommittedOrigin(string claimedOrigin, string? committedUrl, bool runs)
    {
        var fake = new FakeAuthenticator();
        var previous = WebAuthnPlatform.Authenticator;
        WebAuthnPlatform.Authenticator = fake;
        try
        {
            var relay = new RendererWebAuthnRelay(1, _ => { }, () => committedUrl!);
            var reply = await relay.RunAsync(new RendererWebAuthnRequestPayload
            {
                Kind = "get",
                Get = new WebAuthnGetRequest { Origin = claimedOrigin, Challenge = "AAAA" },
            });

            Assert.Equal(runs, fake.Gets == 1);
            Assert.Equal(runs ? null : "NotAllowedError", reply.Result.ErrorName);
        }
        finally
        {
            WebAuthnPlatform.Authenticator = previous;
        }
    }

    // DoD: a new IPC message type is fuzzed for 10 000 iterations; the broker's RP ID
    // check must never accept a domain outside the claimed origin.
    [Fact]
    public void MutatedWebAuthnRequests_NeverCrashOrWidenTheRelyingParty()
    {
        var endpoint = new RendererIpcFuzzEndpoint();
        var mutator = new StructuredMutator(seed: 20260930);
        var seed = IpcFuzzHarness.WebAuthnSeed();

        Assert.True(endpoint.Fuzz(Encoding.UTF8.GetBytes(seed)), "the valid seed was rejected");
        for (var i = 0; i < 10_000; i++)
        {
            var input = mutator.MutateJson(seed);
            Assert.True(endpoint.Fuzz(input), $"iteration {i}: {System.Convert.ToBase64String(input)}");
        }
    }

    internal sealed class FakeAuthenticator : IWebAuthnAuthenticator
    {
        public int Gets;
        public WebAuthnGetRequest? LastGet;
        public WebAuthnCreateRequest? LastCreate;
        public WebAuthnResult? NextResult;
        public bool Uvpaa = true;

        public Task<bool> IsUserVerifyingPlatformAuthenticatorAvailableAsync() => Task.FromResult(Uvpaa);

        public Task<WebAuthnResult> GetAssertionAsync(WebAuthnGetRequest request, CancellationToken cancellationToken)
        {
            Gets++;
            LastGet = request;
            return Task.FromResult(NextResult ?? new WebAuthnResult
            {
                ClientDataJson = WebAuthnClient.ToBase64Url(WebAuthnClient.BuildClientDataJson("webauthn.get", request.Challenge, request.Origin)),
                CredentialId = "AQID",
                AuthenticatorData = "BAUG",
                Signature = "BwgJ",
                UserHandle = "CgsM",
            });
        }

        public Task<WebAuthnResult> MakeCredentialAsync(WebAuthnCreateRequest request, CancellationToken cancellationToken)
        {
            LastCreate = request;
            return Task.FromResult(NextResult ?? new WebAuthnResult
            {
                ClientDataJson = WebAuthnClient.ToBase64Url(WebAuthnClient.BuildClientDataJson("webauthn.create", request.Challenge, request.Origin)),
                CredentialId = "AQID",
                AuthenticatorData = "BAUG",
                AttestationObject = "DQ4P",
                Transports = new List<string> { "internal" },
                AuthenticatorAttachment = "platform",
            });
        }
    }
}
