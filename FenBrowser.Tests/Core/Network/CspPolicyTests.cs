using System;
using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Core.Network
{
    public class CspPolicyTests
    {
        [Fact]
        public void Self_Allows_When_Origin_Matches()
        {
            var policy = CspPolicy.Parse("default-src 'self'");
            var origin = new Uri("https://example.com/app");
            var target = new Uri("https://example.com/script.js");

            Assert.True(policy.IsAllowed("script-src", target, origin: origin));
        }

        [Fact]
        public void Self_Blocks_When_Origin_Is_Not_Provided()
        {
            var policy = CspPolicy.Parse("default-src 'self'");
            var target = new Uri("https://example.com/script.js");

            Assert.False(policy.IsAllowed("script-src", target, origin: (Uri)null));
        }

        [Fact]
        public void Host_Wildcard_Allows_Subdomain()
        {
            var policy = CspPolicy.Parse("img-src https://*.example.com");
            var origin = new Uri("https://example.com/");
            var target = new Uri("https://cdn.example.com/logo.png");

            Assert.True(policy.IsAllowed("img-src", target, origin: origin));
        }

        [Fact]
        public void Self_Blocks_On_Port_Mismatch()
        {
            var policy = CspPolicy.Parse("connect-src 'self'");
            var origin = new Uri("https://example.com:8443/app");
            var target = new Uri("https://example.com/api");

            Assert.False(policy.IsAllowed("connect-src", target, origin: origin));
        }
    }
}
