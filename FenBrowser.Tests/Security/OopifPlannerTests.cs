using FenBrowser.Core.Security.Oopif;
using Xunit;

namespace FenBrowser.Tests.Security
{
    public class OopifPlannerTests
    {
        [Fact]
        public void SiteLock_FromUrl_MultiLabelPublicSuffix_UsesRegistrableDomain()
        {
            var siteLock = SiteLock.FromUrl("https://a.b.example.co.uk/path");
            Assert.Equal("example.co.uk", siteLock.RegistrableDomain);
        }

        [Fact]
        public void ShouldIsolate_DifferentRegistrableDomainsUnderCoUk_RequiresNewProcess()
        {
            var policy = new OopifPolicy(SiteIsolationMode.CrossSiteIsolation);
            var currentFrame = new FrameNode
            {
                Url = "https://foo.example.co.uk/",
                SiteLock = SiteLock.FromUrl("https://foo.example.co.uk/")
            };

            var decision = policy.ShouldIsolate(currentFrame, "https://bar.other.co.uk/");
            Assert.True(decision.RequiresNewProcess);
        }

        [Fact]
        public void SiteLock_FromUrl_LocalhostAndIp_KeepHostAsRegistrableDomain()
        {
            var localhost = SiteLock.FromUrl("https://localhost:8443/");
            var ip = SiteLock.FromUrl("https://127.0.0.1/");

            Assert.Equal("localhost", localhost.RegistrableDomain);
            Assert.Equal("127.0.0.1", ip.RegistrableDomain);
        }
    }
}
