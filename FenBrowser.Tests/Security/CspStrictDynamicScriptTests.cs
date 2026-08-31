using System;
using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Security
{
    // CSP3 6.6.3.4: a script element created and inserted by script is not
    // parser-inserted, so under 'strict-dynamic' the script that inserted it is what
    // authorizes it — the host allowlist is deliberately bypassed. Loaders whose whole
    // purpose is to inject their real bundle (reCAPTCHA's api.js) depend on this.
    public class CspStrictDynamicScriptTests
    {
        private static readonly Uri Origin = new("https://www.google.com/page");
        private static readonly Uri ScriptUri = new("https://www.gstatic.com/recaptcha/releases/abc/recaptcha__en.js");

        private static CspPolicy Parse(string header) => CspPolicy.Parse(header);

        [Fact]
        public void StrictDynamic_AllowsScriptInsertedScript_FromHostOutsideAllowlist()
        {
            var policy = Parse("script-src 'nonce-r4nd0m' 'strict-dynamic' https://example.com");

            Assert.True(policy.IsAllowed(
                "script-src",
                ScriptUri,
                nonce: null,
                origin: Origin,
                scriptProvenance: CspScriptProvenance.TrustedDynamic));
        }

        [Fact]
        public void StrictDynamic_StillBlocksScriptWithoutTrustedDynamicProvenance()
        {
            var policy = Parse("script-src 'nonce-r4nd0m' 'strict-dynamic' https://example.com");

            Assert.False(policy.IsAllowed(
                "script-src",
                ScriptUri,
                nonce: null,
                origin: Origin,
                scriptProvenance: CspScriptProvenance.Unknown));
        }

        // The security guard on the fix: TrustedDynamic must not become a blanket
        // bypass. Without 'strict-dynamic' the host allowlist still decides.
        [Fact]
        public void WithoutStrictDynamic_TrustedDynamicDoesNotBypassHostAllowlist()
        {
            var policy = Parse("script-src 'nonce-r4nd0m' https://example.com");

            Assert.False(policy.IsAllowed(
                "script-src",
                ScriptUri,
                nonce: null,
                origin: Origin,
                scriptProvenance: CspScriptProvenance.TrustedDynamic));
        }

        [Fact]
        public void WithoutStrictDynamic_TrustedDynamicStillHonoursAllowlistedHost()
        {
            var policy = Parse("script-src 'nonce-r4nd0m' https://www.gstatic.com");

            Assert.True(policy.IsAllowed(
                "script-src",
                ScriptUri,
                nonce: null,
                origin: Origin,
                scriptProvenance: CspScriptProvenance.TrustedDynamic));
        }
    }
}
