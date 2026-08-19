using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineFallbackPromotionTests
    {
        [Theory]
        [InlineData("PromoteHiddenFallbackContent")]
        [InlineData("ForceGoogleChallengeBannerVisible")]
        [InlineData("RemoveGoogleAccessTroubleBanners")]
        [InlineData("NormalizeNoJsFallbackClasses")]
        [InlineData("RewriteWebPToJpg")]
        public void GenericRenderer_DoesNotContainSiteSpecificDomOrUrlInterventions(string methodName)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.Null(method);
        }
    }
}
