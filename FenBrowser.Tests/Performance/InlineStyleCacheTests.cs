using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public class InlineStyleCacheTests
    {
        [Fact]
        public void RepeatedInlineStyleText_IsParsedOncePerCascadeEngine()
        {
            var engine = new CascadeEngine(new StyleSet());
            var first = new Element("div");
            var second = new Element("section");
            const string style = "width: 120px; color: rgb(1, 2, 3)";
            first.SetAttribute("style", style);
            second.SetAttribute("style", style);

            var firstValues = engine.ComputeCascadedValues(first);
            var secondValues = engine.ComputeCascadedValues(second);
            var statistics = engine.GetInlineStyleCacheStatistics();

            Assert.Equal("120px", firstValues["width"].Value);
            Assert.Equal("120px", secondValues["width"].Value);
            Assert.Equal(1, statistics.Misses);
            Assert.Equal(1, statistics.Hits);
            Assert.Equal(1, statistics.Entries);
            Assert.Equal(0, statistics.Evictions);
        }

        [Fact]
        public void ChangedInlineStyleText_UsesNewParsedDeclarations()
        {
            var engine = new CascadeEngine(new StyleSet());
            var element = new Element("div");
            element.SetAttribute("style", "width: 120px");
            var original = engine.ComputeCascadedValues(element);

            element.SetAttribute("style", "width: 240px");
            var changed = engine.ComputeCascadedValues(element);
            var statistics = engine.GetInlineStyleCacheStatistics();

            Assert.Equal("120px", original["width"].Value);
            Assert.Equal("240px", changed["width"].Value);
            Assert.Equal(2, statistics.Misses);
            Assert.Equal(0, statistics.Hits);
        }

        [Fact]
        public void UniqueInlineStyles_AreBoundedAndEvictOldestEntry()
        {
            var engine = new CascadeEngine(new StyleSet());
            for (var index = 0; index < 257; index++)
            {
                var element = new Element("div");
                element.SetAttribute("style", $"width: {index}px");
                engine.ComputeCascadedValues(element);
            }

            var statistics = engine.GetInlineStyleCacheStatistics();

            Assert.Equal(257, statistics.Misses);
            Assert.Equal(1, statistics.Evictions);
            Assert.Equal(256, statistics.Entries);
            Assert.Equal(256, statistics.Capacity);
        }
    }
}
