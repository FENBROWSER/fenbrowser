using System.Reflection;
using FenBrowser.Core.Verification;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ContentVerifierStateTests
    {
        [Fact]
        public void AuthoritativeSourceRegistration_BlocksLaterSubresourceOverwrite()
        {
            ContentVerifier.ResetForNavigation("https://example.test/");

            try
            {
                ContentVerifier.RegisterSource("https://example.test/", 1200, 123, authoritative: true);
                ContentVerifier.RegisterSource("https://cdn.example.test/app.js", 98, 456);

                Assert.Equal("https://example.test/", GetPrivateField<string>("_lastUrl"));
                Assert.Equal(1200L, GetPrivateField<long>("_sourceLengthBytes"));
                Assert.Equal(123, GetPrivateField<int>("_sourceHash"));
            }
            finally
            {
                ContentVerifier.ResetForNavigation();
            }
        }

        [Fact]
        public void AuthoritativeRenderedRegistration_BlocksLaterProvisionalOverwrite()
        {
            ContentVerifier.ResetForNavigation("https://example.test/");

            try
            {
                ContentVerifier.RegisterRendered("https://example.test/", 52, 517, authoritative: true);
                ContentVerifier.RegisterRendered("https://example.test/", 520, 148345);

                Assert.Equal(52, GetPrivateField<int>("_domNodeCount"));
                Assert.Equal(517, GetPrivateField<int>("_renderedTextLength"));
            }
            finally
            {
                ContentVerifier.ResetForNavigation();
            }
        }

        private static T GetPrivateField<T>(string fieldName)
        {
            var field = typeof(ContentVerifier).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field!.GetValue(null)!;
        }
    }
}
