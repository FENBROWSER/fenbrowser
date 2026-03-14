using System;
using FenBrowser.Core.Network.Handlers;
using Xunit;

namespace FenBrowser.Tests.Core.Network
{
    public class TrackingPreventionHandlerTests
    {
        [Fact]
        public void IsTracker_DoesNotBlockSameSiteSubresource()
        {
            var page = new Uri("https://www.whatismybrowser.com/");
            var subresource = new Uri("https://cdn.whatismybrowser.com/prod-website/static/main/js/site.min.js");

            Assert.False(TrackingPreventionHandler.IsTracker(subresource, page));
        }

        [Fact]
        public void IsTracker_DoesNotTreatLogoPathAsLogBeacon()
        {
            var page = new Uri("https://www.whatismybrowser.com/");
            var logo = new Uri("https://cdn.whatismybrowser.com/prod-common/static/main/images/logo/favicon.ico");

            Assert.False(TrackingPreventionHandler.IsTracker(logo, page));
        }

        [Fact]
        public void IsTracker_DoesNotBlockSameOriginSubresource()
        {
            var page = new Uri("https://www.example.com/index.html");
            var subresource = new Uri("https://www.example.com/assets/app.js");

            Assert.False(TrackingPreventionHandler.IsTracker(subresource, page));
        }

        [Fact]
        public void IsTracker_StillBlocksKnownThirdPartyTracker()
        {
            var page = new Uri("https://www.example.com/");
            var tracker = new Uri("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js");

            Assert.True(TrackingPreventionHandler.IsTracker(tracker, page));
        }
    }
}
