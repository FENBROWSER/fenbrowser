using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Storage;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class StoragePartitioningTests
    {
        [Fact]
        public void PartitionedKeyValueStorage_IsolatesValuesByOriginAndPartition()
        {
            var storage = new PartitionedKeyValueStorage();
            var origin = "https://example.com";
            var partitionA = StoragePartitionKey.FirstParty("https://site-a.test");
            var partitionB = StoragePartitionKey.FirstParty("https://site-b.test");

            storage.SetItem(origin, partitionA, "token", "alpha");
            storage.SetItem(origin, partitionB, "token", "beta");

            Assert.Equal("alpha", storage.GetItem(origin, partitionA, "token"));
            Assert.Equal("beta", storage.GetItem(origin, partitionB, "token"));
        }

        [Fact]
        public void StorageService_ClearPartition_RemovesOnlyTargetPartitionData()
        {
            var service = new StorageService();
            var partitionA = StoragePartitionKey.FirstParty("https://site-a.test");
            var partitionB = StoragePartitionKey.FirstParty("https://site-b.test");
            var origin = "https://app.example";

            service.LocalStorage.SetItem(origin, partitionA, "key", "A");
            service.LocalStorage.SetItem(origin, partitionB, "key", "B");

            service.SessionStorage.SetItem(origin, partitionA, "session", "A");
            service.SessionStorage.SetItem(origin, partitionB, "session", "B");

            service.Cookies.Set(new Cookie
            {
                Name = "sid",
                Value = "A",
                Domain = "app.example",
                Path = "/"
            }, partitionA);
            service.Cookies.Set(new Cookie
            {
                Name = "sid",
                Value = "B",
                Domain = "app.example",
                Path = "/"
            }, partitionB);

            service.HttpCache.Put(
                partitionA,
                "https://app.example/data",
                new HttpCacheEntry
                {
                    Url = "https://app.example/data",
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, string>(),
                    Body = new byte[] { 1, 2, 3 }
                });
            service.HttpCache.Put(
                partitionB,
                "https://app.example/data",
                new HttpCacheEntry
                {
                    Url = "https://app.example/data",
                    StatusCode = 200,
                    ResponseHeaders = new Dictionary<string, string>(),
                    Body = new byte[] { 4, 5, 6 }
                });

            service.ClearPartition(partitionA);

            Assert.Null(service.LocalStorage.GetItem(origin, partitionA, "key"));
            Assert.Equal("B", service.LocalStorage.GetItem(origin, partitionB, "key"));

            Assert.Null(service.SessionStorage.GetItem(origin, partitionA, "session"));
            Assert.Equal("B", service.SessionStorage.GetItem(origin, partitionB, "session"));

            Assert.Empty(service.Cookies.GetForUrl("https://app.example/", partitionA));
            Assert.Single(service.Cookies.GetForUrl("https://app.example/", partitionB));
            Assert.Equal("B", service.Cookies.GetForUrl("https://app.example/", partitionB)[0].Value);

            Assert.Null(service.HttpCache.Get(partitionA, "https://app.example/data"));
            Assert.NotNull(service.HttpCache.Get(partitionB, "https://app.example/data"));
        }

        [Fact]
        public void CookieStore_DomainCookie_MatchesSubdomain()
        {
            var service = new StorageService();
            service.Cookies.Set(new Cookie
            {
                Name = "sid",
                Value = "domain",
                Domain = "example.test",
                Path = "/",
                HostOnly = false
            });

            var cookies = service.Cookies.GetForUrl(
                "https://www.example.test/search",
                StoragePartitionKey.FirstParty("https://www.example.test"));

            var cookie = Assert.Single(cookies);
            Assert.Equal("sid", cookie.Name);
            Assert.Equal("domain", cookie.Value);
        }

        [Fact]
        public void CookieStore_HostOnlyCookie_DoesNotMatchSubdomain()
        {
            var service = new StorageService();
            service.Cookies.Set(new Cookie
            {
                Name = "sid",
                Value = "host",
                Domain = "example.test",
                Path = "/",
                HostOnly = true
            });

            Assert.Empty(service.Cookies.GetForUrl(
                "https://www.example.test/search",
                StoragePartitionKey.FirstParty("https://www.example.test")));

            var sameHostCookies = service.Cookies.GetForUrl(
                "https://example.test/search",
                StoragePartitionKey.FirstParty("https://example.test"));

            Assert.Single(sameHostCookies);
        }

        [Fact]
        public void CookieStore_DomainCapacity_EvictsOldestCookie()
        {
            var store = new PartitionedCookieStore();
            var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            for (var i = 0; i <= 180; i++)
            {
                store.Set(new Cookie
                {
                    Name = $"cookie-{i}",
                    Value = i.ToString(),
                    Domain = "capacity.test",
                    CreationTime = created.AddSeconds(i)
                });
            }

            var cookies = store.GetForUrl(
                "https://capacity.test/",
                StoragePartitionKey.FirstParty("https://capacity.test"));

            Assert.Equal(180, cookies.Count);
            Assert.DoesNotContain(cookies, cookie => cookie.Name == "cookie-0");
            Assert.Contains(cookies, cookie => cookie.Name == "cookie-180");
        }

        [Fact]
        public async Task CookieStore_ConcurrentDomainMutations_RemainIsolated()
        {
            var store = new PartitionedCookieStore();
            const int domainCount = 32;
            const int cookiesPerDomain = 64;

            await Task.WhenAll(Enumerable.Range(0, domainCount).Select(domain => Task.Run(() =>
            {
                for (var cookie = 0; cookie < cookiesPerDomain; cookie++)
                {
                    store.Set(new Cookie
                    {
                        Name = $"cookie-{cookie}",
                        Value = $"{domain}:{cookie}",
                        Domain = $"domain-{domain}.test"
                    });
                }
            })));

            for (var domain = 0; domain < domainCount; domain++)
            {
                var host = $"domain-{domain}.test";
                var cookies = store.GetForUrl(
                    $"https://{host}/",
                    StoragePartitionKey.FirstParty($"https://{host}"));
                Assert.Equal(cookiesPerDomain, cookies.Count);
                Assert.All(cookies, cookie => Assert.StartsWith($"{domain}:", cookie.Value));
            }
        }
    }
}
