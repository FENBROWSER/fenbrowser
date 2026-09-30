using System;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// An over-budget image cache must not evict images the page is still drawing:
    /// pure LRU made a page whose images exceed the budget evict one to load another
    /// on every paint (github.com re-decoded 3-8 WebP images per frame).
    /// </summary>
    public class ImageCacheInUseEvictionTests
    {
        private const long Budget = 100L * 1024 * 1024;
        private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void RecentlyDrawnImage_IsKeptWhileOverBudget()
        {
            Assert.False(ImageLoader.IsEvictable(Now.AddSeconds(-1), Now, Budget + 50_000_000, Budget, 30, 500));
        }

        [Fact]
        public void ImageNotDrawnForAWhile_IsEvicted()
        {
            Assert.True(ImageLoader.IsEvictable(Now - ImageLoader.InUseWindow - TimeSpan.FromSeconds(1), Now, Budget + 1, Budget, 30, 500));
        }

        [Fact]
        public void PastTheHardCap_EvenInUseImagesGo()
        {
            long pastCap = Budget * ImageLoader.InUseOverrunFactor + 1;
            Assert.True(ImageLoader.IsEvictable(Now, Now, pastCap, Budget, 30, 500));
            Assert.True(ImageLoader.IsEvictable(Now, Now, Budget, Budget, 500 * ImageLoader.InUseOverrunFactor + 1, 500));
        }
    }
}
