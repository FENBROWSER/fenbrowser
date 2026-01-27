using Xunit;
using FenBrowser.Core.Cache;

namespace FenBrowser.Tests.Core
{
    public class ShardedCacheTests
    {
        [Fact]
        public void Put_Get_SamePartition_ReturnsValue()
        {
            var cache = new ShardedCache<string>(10);
            cache.Put("example.com", "http://resource.com/a.js", "content");

            bool found = cache.TryGet("example.com", "http://resource.com/a.js", out var val);
            Assert.True(found);
            Assert.Equal("content", val);
        }

        [Fact]
        public void Get_DifferentPartition_ReturnsFalse_DoubleCaches()
        {
            var cache = new ShardedCache<string>(10);
            cache.Put("siteA.com", "http://tracker.com/pixel.png", "A");

            // Site B should NOT see Site A's cache
            bool foundB = cache.TryGet("siteB.com", "http://tracker.com/pixel.png", out var valB);
            Assert.False(foundB);

            // Putting specifically for Site B
            cache.Put("siteB.com", "http://tracker.com/pixel.png", "B");
            
            // Both should exist independently
            cache.TryGet("siteA.com", "http://tracker.com/pixel.png", out var valA_check);
            cache.TryGet("siteB.com", "http://tracker.com/pixel.png", out var valB_check);
            
            Assert.Equal("A", valA_check);
            Assert.Equal("B", valB_check);
        }

        [Fact]
        public void LRU_Evicts_Global_Oldest()
        {
            var cache = new ShardedCache<string>(2); // Capacity 2
            
            cache.Put("p1", "url1", "1");
            cache.Put("p1", "url2", "2");
            // Cache is full: [url2, url1]

            cache.Put("p2", "url3", "3");
            // Should evict url1 (oldest globally)
            // Cache now: [url3, url2]

            Assert.False(cache.TryGet("p1", "url1", out _));
            Assert.True(cache.TryGet("p1", "url2", out _));
            Assert.True(cache.TryGet("p2", "url3", out _));
        }
    }
}
