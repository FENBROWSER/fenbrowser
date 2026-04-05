using System;
using System.Net;
using System.Net.Http;
using FenBrowser.Core.Storage;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class BrowserCookieJarTests
    {
        private static readonly Uri FirstParty = new Uri("https://www.whatismybrowser.com/");
        private static readonly Uri ThirdParty = new Uri("https://webbrowsertests.com/detect/are-third-party-cookies-enabled-set-cookie");
        private static readonly Uri ThirdPartyCheck = new Uri("https://webbrowsertests.com/detect/are-third-party-cookies-enabled-check-cookie");

        [Fact]
        public void StoreResponseCookies_ReplaysThirdPartyCookie_WhenBlockingIsDisabled()
        {
            var jar = new BrowserCookieJar();
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, ThirdParty)
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "wimb_third_party=1; Path=/; Secure; SameSite=None");

            jar.StoreResponseCookies(response, FirstParty, blockThirdPartyCookies: false);

            var cookieHeader = jar.GetRequestCookieHeader(ThirdPartyCheck, FirstParty);

            Assert.Contains("wimb_third_party=1", cookieHeader);
        }

        [Fact]
        public void StoreResponseCookies_DropsThirdPartyCookie_WhenBlockingIsEnabled()
        {
            var jar = new BrowserCookieJar();
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, ThirdParty)
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "wimb_third_party=1; Path=/; Secure; SameSite=None");

            jar.StoreResponseCookies(response, FirstParty, blockThirdPartyCookies: true);

            var cookieHeader = jar.GetRequestCookieHeader(ThirdPartyCheck, FirstParty);

            Assert.True(string.IsNullOrWhiteSpace(cookieHeader));
        }

        [Fact]
        public void DocumentCookie_RoundTripsThroughSharedJar()
        {
            var jar = new BrowserCookieJar();

            jar.SetDocumentCookie(FirstParty, "session=abc; Path=/", FirstParty);

            Assert.Contains("session=abc", jar.GetDocumentCookieString(FirstParty, FirstParty));
            Assert.Contains("session=abc", jar.GetRequestCookieHeader(FirstParty, FirstParty));
        }
    }
}
