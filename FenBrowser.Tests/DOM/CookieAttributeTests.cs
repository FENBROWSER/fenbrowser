using System;
using FenBrowser.FenEngine.DOM;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    /// <summary>
    /// Regression tests for Storage-1 tranche: cookie attribute parsing.
    /// Tests InMemoryCookieStore's handling of path, domain, expires,
    /// max-age, secure, httponly, and samesite attributes.
    /// </summary>
    public class CookieAttributeTests
    {
        private static readonly Uri HttpsExample  = new Uri("https://example.com/");
        private static readonly Uri HttpExample   = new Uri("http://example.com/");
        private static readonly Uri HttpsAdmin    = new Uri("https://example.com/admin/dashboard");
        private static readonly Uri HttpsApi      = new Uri("https://example.com/api/data");

        // ------------------------------------------------------------------ basic name=value

        [Fact]
        public void SetGet_BasicNameValue_RoundTrips()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("foo=bar", HttpsExample);
            Assert.Contains("foo=bar", store.GetCookieString(HttpsExample));
        }

        [Fact]
        public void SetGet_MultipleValues_AllReturned()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("a=1", HttpsExample);
            store.SetCookie("b=2", HttpsExample);
            store.SetCookie("c=3", HttpsExample);
            var result = store.GetCookieString(HttpsExample);
            Assert.Contains("a=1", result);
            Assert.Contains("b=2", result);
            Assert.Contains("c=3", result);
        }

        [Fact]
        public void SetGet_OverwriteSameName_UpdatesValue()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("name=first", HttpsExample);
            store.SetCookie("name=second", HttpsExample);
            var result = store.GetCookieString(HttpsExample);
            Assert.Contains("name=second", result);
            Assert.DoesNotContain("name=first", result);
        }

        // ------------------------------------------------------------------ Max-Age

        [Fact]
        public void MaxAgeZero_DeletesCookie()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("session=abc", HttpsExample);
            Assert.True(store.Has("session", HttpsExample), "Cookie should exist before deletion");

            store.SetCookie("session=abc; Max-Age=0", HttpsExample);
            Assert.False(store.Has("session", HttpsExample), "Max-Age=0 must delete the cookie");
        }

        [Fact]
        public void MaxAgePositive_CookiePersists()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("token=xyz; Max-Age=3600", HttpsExample);
            Assert.Contains("token=xyz", store.GetCookieString(HttpsExample));
        }

        [Fact]
        public void MaxAgeNegative_DeletesCookie()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("del=me", HttpsExample);
            store.SetCookie("del=me; Max-Age=-1", HttpsExample);
            Assert.False(store.Has("del", HttpsExample));
        }

        // ------------------------------------------------------------------ Expires

        [Fact]
        public void ExpiresInFuture_CookiePersists()
        {
            var store = new InMemoryCookieStore();
            var future = DateTimeOffset.UtcNow.AddHours(1).ToString("R");
            store.SetCookie($"fut=1; Expires={future}", HttpsExample);
            Assert.Contains("fut=1", store.GetCookieString(HttpsExample));
        }

        [Fact]
        public void ExpiresInPast_CookieIsDeleted()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("old=value", HttpsExample);
            Assert.True(store.Has("old", HttpsExample));

            var past = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("R");
            store.SetCookie($"old=value; Expires={past}", HttpsExample);
            Assert.False(store.Has("old", HttpsExample));
        }

        // ------------------------------------------------------------------ Secure

        [Fact]
        public void SecureFlag_NotReturnedOnHttp()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("secret=s; Secure", HttpsExample);
            // HTTPS store then read from HTTP — must be filtered
            var result = store.GetCookieString(HttpExample);
            Assert.DoesNotContain("secret", result);
        }

        [Fact]
        public void SecureFlag_ReturnedOnHttps()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("secret=s; Secure", HttpsExample);
            var result = store.GetCookieString(HttpsExample);
            Assert.Contains("secret=s", result);
        }

        // ------------------------------------------------------------------ Path

        [Fact]
        public void PathMismatch_CookieNotReturned()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("admin=1; Path=/admin", HttpsExample);
            var result = store.GetCookieString(HttpsApi); // path=/api/data
            Assert.DoesNotContain("admin=1", result);
        }

        [Fact]
        public void PathMatch_CookieReturned()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("admin=1; Path=/admin", HttpsExample);
            var result = store.GetCookieString(HttpsAdmin); // path=/admin/dashboard
            Assert.Contains("admin=1", result);
        }

        [Fact]
        public void RootPath_MatchesEverything()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("global=1; Path=/", HttpsExample);
            Assert.Contains("global=1", store.GetCookieString(HttpsApi));
            Assert.Contains("global=1", store.GetCookieString(HttpsAdmin));
        }

        // ------------------------------------------------------------------ SameSite (parsed without error)

        [Fact]
        public void SameSiteStrict_ParsedAndCookieStored()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("pref=1; SameSite=Strict", HttpsExample);
            Assert.Contains("pref=1", store.GetCookieString(HttpsExample));
        }

        [Fact]
        public void SameSiteLax_ParsedAndCookieStored()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("pref=2; SameSite=Lax", HttpsExample);
            Assert.Contains("pref=2", store.GetCookieString(HttpsExample));
        }

        // ------------------------------------------------------------------ Has()

        [Fact]
        public void Has_ReturnsTrueForExistingCookie()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("x=1", HttpsExample);
            Assert.True(store.Has("x", HttpsExample));
        }

        [Fact]
        public void Has_ReturnsFalseForMissingCookie()
        {
            var store = new InMemoryCookieStore();
            Assert.False(store.Has("nonexistent", HttpsExample));
        }

        [Fact]
        public void Has_ReturnsFalseAfterMaxAgeZero()
        {
            var store = new InMemoryCookieStore();
            store.SetCookie("z=1", HttpsExample);
            store.SetCookie("z=1; Max-Age=0", HttpsExample);
            Assert.False(store.Has("z", HttpsExample));
        }
    }
}
