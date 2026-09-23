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
        public void Parse_UnmentionedFeature_KeepsItsOwnDefaultAllowlist()
        {
            var policy = PermissionsPolicy.Parse("fullscreen=*");
            // 'notifications' is not mentioned, so its own default allowlist applies:
            // 'self', which the document that sent the header is.
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.Notifications, "https://a.example", "https://a.example"));
            Assert.False(policy.IsFeatureAllowed(PolicyControlledFeature.Notifications, "https://evil.example", "https://a.example"));

            // Picture-in-Picture defaults to '*', so an unmentioned one reaches anybody.
            Assert.True(policy.IsFeatureAllowed(PolicyControlledFeature.PictureInPicture, "https://evil.example", "https://a.example"));
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
        public void ContainerAllow_DecidesOnlyTheFeaturesItNames()
        {
            const string parent = "https://a.example";
            Assert.False(PermissionsPolicy.EvaluateContainerAllow(
                "picture-in-picture 'none'", PolicyControlledFeature.PictureInPicture, parent, parent));
            Assert.True(PermissionsPolicy.EvaluateContainerAllow(
                "encrypted-media", PolicyControlledFeature.EncryptedMedia, "https://b.example", parent));
            Assert.False(PermissionsPolicy.EvaluateContainerAllow(
                "speaker-selection 'self'", PolicyControlledFeature.SpeakerSelection, "https://b.example", parent));

            // Not named: the feature's default allowlist decides, which the caller applies.
            Assert.Null(PermissionsPolicy.EvaluateContainerAllow(
                "autoplay", PolicyControlledFeature.PictureInPicture, parent, parent));
            Assert.Null(PermissionsPolicy.EvaluateContainerAllow(
                null, PolicyControlledFeature.EncryptedMedia, parent, parent));
        }

        [Fact]
        public void DefaultAllowlists_StarForPictureInPicture_SelfForMediaKeysAndSpeakers()
        {
            const string parent = "https://a.example";
            Assert.True(PermissionsPolicy.DefaultAllowlistAllows(PolicyControlledFeature.PictureInPicture, "https://b.example", parent));
            Assert.False(PermissionsPolicy.DefaultAllowlistAllows(PolicyControlledFeature.EncryptedMedia, "https://b.example", parent));
            Assert.False(PermissionsPolicy.DefaultAllowlistAllows(PolicyControlledFeature.SpeakerSelection, "https://b.example", parent));
            Assert.True(PermissionsPolicy.DefaultAllowlistAllows(PolicyControlledFeature.SpeakerSelection, parent, parent));
            Assert.True(PermissionsPolicy.Parse("encrypted-media=()").Allowlists.ContainsKey(PolicyControlledFeature.EncryptedMedia));
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
