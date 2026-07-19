using System;
using System.Net;
using System.Net.Http;
using FenBrowser.Core;
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

        [Fact]
        public void ResourceManagers_UseInjectedBrowsingSessionJar()
        {
            var sharedJar = new BrowserCookieJar();
            using var firstClient = new HttpClient();
            using var secondClient = new HttpClient();
            var first = new ResourceManager(firstClient, isPrivate: false, sharedJar);
            var second = new ResourceManager(secondClient, isPrivate: false, sharedJar);

            first.CookieJar.SetDocumentCookie(FirstParty, "shared_session=present; Path=/", FirstParty);

            Assert.Same(first.CookieJar, second.CookieJar);
            Assert.Contains(
                "shared_session=present",
                second.CookieJar.GetRequestCookieHeader(FirstParty, FirstParty));
        }

        [Fact]
        public void ResourceManagers_DefaultToIsolatedCookieJars()
        {
            using var firstClient = new HttpClient();
            using var secondClient = new HttpClient();
            var first = new ResourceManager(firstClient, isPrivate: true);
            var second = new ResourceManager(secondClient, isPrivate: true);

            first.CookieJar.SetDocumentCookie(FirstParty, "private_session=present; Path=/", FirstParty);

            Assert.NotSame(first.CookieJar, second.CookieJar);
            Assert.DoesNotContain(
                "private_session=present",
                second.CookieJar.GetRequestCookieHeader(FirstParty, FirstParty));
        }
    }
}
