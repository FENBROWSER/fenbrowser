using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class ObjectPoolCapacityTests
    {
        private sealed class PooledItem
        {
            public int ResetCount { get; set; }
        }

        [Fact]
        public void Return_BeyondCapacity_DropsExcessObjects()
        {
            var pool = new ObjectPool<PooledItem>(
                factory: () => new PooledItem(),
                reset: item => item.ResetCount++,
                maxRetained: 2);

            var first = new PooledItem();
            var second = new PooledItem();
            var third = new PooledItem();

            pool.Return(first);
            pool.Return(second);
            pool.Return(third);

            Assert.Equal(2, pool.RetainedCount);
            Assert.Equal(1, pool.DroppedReturns);
            Assert.Equal(1, first.ResetCount);
            Assert.Equal(1, second.ResetCount);
            Assert.Equal(1, third.ResetCount);
        }

        [Fact]
        public void Get_FromRetainedPool_DecrementsRetainedCount()
        {
            var pool = new ObjectPool<PooledItem>(maxRetained: 2);
            pool.Return(new PooledItem());
            pool.Return(new PooledItem());

            Assert.Equal(2, pool.RetainedCount);

            var itemA = pool.Get();
            var itemB = pool.Get();

            Assert.NotNull(itemA);
            Assert.NotNull(itemB);
            Assert.Equal(0, pool.RetainedCount);
        }
    }
}
