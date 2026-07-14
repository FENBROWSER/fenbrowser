using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTokenPoolTests
    {
        [Fact]
        public void Constructor_DoesNotAllocateTokenObjectsBeforeFirstRent()
        {
            var pool = new HtmlTokenPool();

            Assert.Equal(0, pool.TotalRented);
            Assert.Equal(0, pool.TotalAllocated);
            Assert.Equal(0, pool.ReuseRatio);
        }

        [Fact]
        public void ResetAll_ReusesLazilyCreatedSlotsAndResetsObservableState()
        {
            var pool = new HtmlTokenPool();
            var start = pool.RentStartTag();
            start.TagName = "div";
            start.AddAttribute("class", "before-reset");
            var character = pool.RentCharacter('a');

            Assert.Equal(2, pool.TotalAllocated);
            pool.ResetAll();

            var reusedStart = pool.RentStartTag();
            var reusedCharacter = pool.RentCharacter('b');

            Assert.Same(start, reusedStart);
            Assert.Same(character, reusedCharacter);
            Assert.Null(reusedStart.TagName);
            Assert.Empty(reusedStart.Attributes);
            Assert.Equal("b", reusedCharacter.Data);
            Assert.Equal(2, pool.TotalAllocated);
            Assert.Equal(4, pool.TotalRented);
            Assert.Equal(0.5, pool.ReuseRatio);
        }
    }
}
