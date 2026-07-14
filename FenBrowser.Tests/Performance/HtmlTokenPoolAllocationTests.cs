using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class HtmlTokenPoolAllocationTests
    {
        [Fact]
        public void Constructor_DoesNotAllocateMaximumSlotTables()
        {
            var warmup = new HtmlTokenPool();
            GC.KeepAlive(warmup);

            HtmlTokenPool pool = null;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                pool = new HtmlTokenPool();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            GC.KeepAlive(pool);
            Assert.InRange(allocated, 1, 26_000);
        }

        [Fact]
        public void StartTagRing_GrowsLazilyAndWrapsAtExistingLimit()
        {
            const int startTagPoolSize = 4096;
            var pool = new HtmlTokenPool();
            var first = pool.RentStartTag();

            for (var index = 1; index < startTagPoolSize; index++)
            {
                pool.RentStartTag();
            }

            var wrapped = pool.RentStartTag();

            Assert.Same(first, wrapped);
            Assert.Equal(startTagPoolSize, pool.TotalAllocated);
            Assert.Equal(startTagPoolSize + 1, pool.TotalRented);
        }
    }
}
