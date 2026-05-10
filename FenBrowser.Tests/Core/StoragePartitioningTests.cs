using System;
using System.Collections.Generic;
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
    }
}
