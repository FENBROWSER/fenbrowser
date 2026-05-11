using System;
using System.Threading;
using FenBrowser.FenEngine.Performance;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public class SmartCacheSystemTests
    {
        public SmartCacheSystemTests()
        {
            SmartCacheSystem.Initialize();
        }
        
        [Fact]
        public void Cache_SetAndGet_Works()
        {
            SmartCacheSystem.ClearAll();
            SmartCacheSystem.LayoutCache.Set("key1", "value1");
            
            bool found = SmartCacheSystem.LayoutCache.TryGet<string>("key1", out var value);
            
            Assert.True(found);
            Assert.Equal("value1", value);
        }
        
        [Fact]
        public void Cache_Miss_ReturnsFalse()
        {
            SmartCacheSystem.ClearAll();
            bool found = SmartCacheSystem.LayoutCache.TryGet<string>("nonexistent", out var value);
            
            Assert.False(found);
            Assert.Null(value);
        }
        
        [Fact]
        public void Cache_HitRate_CalculatedCorrectly()
        {
            SmartCacheSystem.ClearAll();
            // Warm cache with hits
            for (int i = 0; i < 10; i++)
            {
                SmartCacheSystem.LayoutCache.Set($"hit{i}", i);
                SmartCacheSystem.LayoutCache.TryGet<int>($"hit{i}", out _);
            }
            
            // Create misses
            for (int i = 0; i < 5; i++)
            {
                SmartCacheSystem.LayoutCache.TryGet<int>($"miss{i}", out _);
            }
            
            var stats = SmartCacheSystem.GetStats();
            
            Assert.Equal(10, stats.LayoutHits);
            Assert.Equal(5, stats.LayoutMisses);
            Assert.Equal(0.667, Math.Round(stats.HitRate, 3));
        }
        
        [Fact]
        public void Cache_Clear_RemovesAll()
        {
            SmartCacheSystem.ClearAll();
            SmartCacheSystem.LayoutCache.Set("key1", "value1");
            SmartCacheSystem.StyleCache.Set("key2", "value2");
            
            SmartCacheSystem.ClearAll();
            
            Assert.False(SmartCacheSystem.LayoutCache.TryGet<string>("key1", out _));
            Assert.False(SmartCacheSystem.StyleCache.TryGet<string>("key2", out _));
        }
        
        [Fact]
        public void Cache_LruEviction_Works()
        {
            var cache = new CacheEngine("test", maxSize: 3, loadFactor: 1.0);

            for (int i = 0; i < 4; i++)
            {
                cache.Set($"key{i}", i);
            }
            
            bool found = cache.TryGet<int>("key0", out _);
            
            Assert.False(found);
        }
    }
}
