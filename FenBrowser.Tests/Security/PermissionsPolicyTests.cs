using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Security
{
    public class PermissionsPolicyTests
    {
        [Fact]
        public void Parse_EmptyHeader_AllowsNothing()
        {
            var policy = PermissionsPolicy.Parse("");
            Assert.Empty(policy.Allowlists);
        }

        [Fact]
        public void Parse_FeatureWithoutAllowlist_DisablesFeature()
        {
            var policy = PermissionsPolicy.Parse("geolocation");
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://a.example", "https://a.example"));
        }

        [Fact]
        public void Parse_StarAllowlist_AllowsAllOrigins()
        {
            var policy = PermissionsPolicy.Parse("fullscreen=*");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Fullscreen, "https://evil.example", "https://a.example"));
        }

        [Fact]
        public void Parse_SelfAllowlist_OnlyAllowsDocumentOrigin()
        {
            var policy = PermissionsPolicy.Parse("fullscreen=self");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Fullscreen, "https://a.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Fullscreen, "https://evil.example", "https://a.example"));
        }

        [Fact]
        public void Parse_OriginList_AllowsListedOriginsOnly()
        {
            var policy = PermissionsPolicy.Parse("geolocation=(self \"https://trusted.example\")");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://a.example", "https://a.example"));
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://trusted.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://evil.example", "https://a.example"));
        }

        [Fact]
        public void Parse_EmptyAllowlist_DisablesForEveryone()
        {
            var policy = PermissionsPolicy.Parse("camera=()");
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Camera, "https://a.example", "https://a.example"));
        }

        [Fact]
        public void Parse_MultipleFeatures_IndependentAllowlists()
        {
            var policy = PermissionsPolicy.Parse("camera=*, microphone=self, geolocation");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Camera, "https://evil.example", "https://a.example"));
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Microphone, "https://a.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Microphone, "https://evil.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://a.example", "https://a.example"));
        }

        [Fact]
        public void Parse_UnmentionedFeature_FallsBackToDefault()
        {
            var policy = PermissionsPolicy.Parse("fullscreen=*");
            // 'notifications' not mentioned: default allowlist is 'self'-style
            // (DefaultAllowsAll=false) so it is denied for cross-origin.
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Notifications, "https://a.example", "https://a.example"));
        }

        [Fact]
        public void Parse_NestedParens_SplitsTopLevelCommasOnly()
        {
            var policy = PermissionsPolicy.Parse("geolocation=(self \"https://x.example\"), camera=self");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://x.example", "https://a.example"));
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Camera, "https://a.example", "https://a.example"));
        }

        [Fact]
        public void IframeAllow_Attribute_ParsesFeatureFlags()
        {
            var allow = PermissionsPolicy.ParseIframeAllowAttribute("fullscreen; geolocation=none; camera=(self)");
            Assert.True(allow[PolicyControlledFeature.Fullscreen]);
            Assert.False(allow[PolicyControlledFeature.Geolocation]);
            Assert.True(allow[PolicyControlledFeature.Camera]);
        }

        [Fact]
        public void IsFeatureAllowedInFrame_HeaderAndAllowlistMustBothPermit()
        {
            var policy = PermissionsPolicy.Parse("fullscreen=*");
            Assert.True(policy.IsFeatureAllowedInFrame(
                PolicyControlledFeature.Fullscreen,
                "https://frame.example",
                "https://a.example",
                "fullscreen"));

            // Header allows, but iframe allow attribute does not mention fullscreen.
            Assert.False(policy.IsFeatureAllowedInFrame(
                PolicyControlledFeature.Fullscreen,
                "https://frame.example",
                "https://a.example",
                "autoplay"));

            // Header denies (feature not mentioned -> default deny).
            var restrictive = PermissionsPolicy.Parse("autoplay=*");
            Assert.False(restrictive.IsFeatureAllowedInFrame(
                PolicyControlledFeature.Fullscreen,
                "https://frame.example",
                "https://a.example",
                "fullscreen"));
        }

        [Fact]
        public void ParseLegacyFeaturePolicy_UsesSameSyntax()
        {
            var policy = PermissionsPolicy.ParseLegacyFeaturePolicy("fullscreen *; geolocation (self)");
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Fullscreen, "https://evil.example", "https://a.example"));
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://a.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Geolocation, "https://evil.example", "https://a.example"));
        }
    }
}
