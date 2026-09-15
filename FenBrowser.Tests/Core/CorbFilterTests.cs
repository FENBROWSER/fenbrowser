using System.Text;
using FenBrowser.Core.Security.Corb;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// Cross-Origin Read Blocking protects HTML, XML and JSON responses fetched
    /// no-cors across origins. SVG is the XML type it deliberately leaves alone:
    /// an SVG is an image, and a page's cross-origin logos are all
    /// &lt;img src="logo.svg"&gt;. github.com's customer logos come from its CDN
    /// this way and were being zeroed.
    /// </summary>
    public class CorbFilterTests
    {
        private const string PageOrigin = "https://page.test";

        private static CorbFilterResult Evaluate(string contentType, string body, string nosniff = null)
        {
            return new CorbFilter().Evaluate(
                requestMode: "no-cors",
                requestOrigin: PageOrigin,
                responseUrl: "https://cdn.other.test/asset",
                contentType: contentType,
                contentTypeOptions: nosniff,
                responseBodyPrefix: Encoding.UTF8.GetBytes(body));
        }

        [Theory]
        [InlineData("image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0h1\"/></svg>")]
        [InlineData("image/svg+xml", "<?xml version=\"1.0\"?><!DOCTYPE svg><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
        [InlineData("text/plain", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- logo -->\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
        public void CrossOriginSvg_IsAllowed_EvenWithNosniff(string contentType, string body)
        {
            Assert.Equal(CorbVerdict.Allow, Evaluate(contentType, body).Verdict);
            Assert.Equal(CorbVerdict.Allow, Evaluate(contentType, body, nosniff: "nosniff").Verdict);
        }

        [Theory]
        [InlineData("text/html", "<!doctype html><html><body>secret</body></html>")]
        [InlineData("application/json", "{\"token\":\"secret\"}")]
        [InlineData("text/xml", "<?xml version=\"1.0\"?><account><balance>1</balance></account>")]
        [InlineData("application/rss+xml", "<?xml version=\"1.0\"?><rss/>")]
        public void CrossOriginProtectedTypes_StayBlocked(string contentType, string body)
        {
            Assert.Equal(CorbVerdict.Block, Evaluate(contentType, body).Verdict);
        }
    }
}
