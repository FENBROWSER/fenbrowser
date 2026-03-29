using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class SecurityChecksTests
    {
        [Fact]
        public void Origin_FromUri_NormalizesSchemeHostAndDefaultPort()
        {
            var origin = Origin.FromUri(new Uri("https://Example.com/path?q=1"));

            Assert.False(origin.IsOpaque);
            Assert.Equal("https", origin.Scheme);
            Assert.Equal("example.com", origin.Host);
            Assert.Equal(443, origin.Port);
            Assert.Equal("https://example.com", origin.ToString());
        }

        [Fact]
        public void OpaqueOrigins_AreNeverSameOrigin()
        {
            var first = Origin.Opaque();
            var second = Origin.Opaque();

            Assert.False(first.IsSameOrigin(first));
            Assert.False(first.IsSameOrigin(second));
        }

        [Fact]
        public void EnforceSameOrigin_ThrowsSecurityErrorForCrossOriginAccess()
        {
            var accessor = new Document
            {
                Origin = Origin.FromUri(new Uri("https://a.example/"))
            };
            var target = new Document
            {
                Origin = Origin.FromUri(new Uri("https://b.example/"))
            };

            var ex = Assert.Throws<DomException>(() => SecurityChecks.EnforceSameOrigin(accessor, target, "window.location"));

            Assert.Equal("SecurityError", ex.Name);
            Assert.Contains("cross-origin frame", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidatePostMessageOrigin_AcceptsWildcardAndExactOriginOnly()
        {
            var actual = Origin.FromUri(new Uri("https://example.com/app"));

            Assert.True(SecurityChecks.ValidatePostMessageOrigin("*", actual));
            Assert.True(SecurityChecks.ValidatePostMessageOrigin("https://example.com", actual));
            Assert.False(SecurityChecks.ValidatePostMessageOrigin("https://other.example", actual));
            Assert.False(SecurityChecks.ValidatePostMessageOrigin("not-a-uri", actual));
        }

        [Theory]
        [InlineData("Strict", true, false, "POST", true)]
        [InlineData("Strict", false, true, "GET", false)]
        [InlineData("Lax", false, true, "GET", true)]
        [InlineData("Lax", false, true, "POST", false)]
        [InlineData("None", false, false, "POST", true)]
        [InlineData(null, false, true, "GET", true)]
        public void ShouldSendCookie_EnforcesSameSiteRules(
            string? sameSiteAttribute,
            bool isSameOriginRequest,
            bool isTopLevelNavigation,
            string requestMethod,
            bool expected)
        {
            var allowed = SecurityChecks.ShouldSendCookie(
                sameSiteAttribute,
                isSameOriginRequest,
                isTopLevelNavigation,
                requestMethod);

            Assert.Equal(expected, allowed);
        }

        [Fact]
        public void ValidateCorsPreflight_RequiresMatchingOriginMethodAndHeaders()
        {
            var requestOrigin = Origin.FromUri(new Uri("https://app.example/"));

            Assert.True(SecurityChecks.ValidateCorsPreflight(
                "https://app.example",
                "GET, POST",
                "X-Test, Content-Type",
                requestOrigin,
                "POST",
                new[] { "X-Test" }));

            Assert.False(SecurityChecks.ValidateCorsPreflight(
                "https://other.example",
                "GET, POST",
                "X-Test, Content-Type",
                requestOrigin,
                "POST",
                new[] { "X-Test" }));

            Assert.False(SecurityChecks.ValidateCorsPreflight(
                "https://app.example",
                "GET",
                "X-Test, Content-Type",
                requestOrigin,
                "POST",
                new[] { "X-Test" }));

            Assert.False(SecurityChecks.ValidateCorsPreflight(
                "https://app.example",
                "GET, POST",
                "Content-Type",
                requestOrigin,
                "POST",
                new[] { "X-Test" }));
        }
    }
}
