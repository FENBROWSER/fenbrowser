using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public sealed class CascadeIndexInsertionTests
    {
        [Fact]
        public void AddToIndex_NewKeyHashesOnceAndPreservesRuleOrder()
        {
            var comparer = new CountingComparer();
            var index = new Dictionary<string, List<CssStyleRule>>(comparer)
            {
                ["seed"] = new List<CssStyleRule>()
            };
            var firstRule = new CssStyleRule();
            var secondRule = new CssStyleRule();

            comparer.Reset();
            CascadeEngine.AddToIndex(index, "target", firstRule);

            Assert.Equal(1, comparer.HashCalls);
            Assert.Same(firstRule, Assert.Single(index["target"]));

            comparer.Reset();
            CascadeEngine.AddToIndex(index, "TARGET", secondRule);

            Assert.Equal(1, comparer.HashCalls);
            Assert.Equal(2, index["target"].Count);
            Assert.Same(firstRule, index["target"][0]);
            Assert.Same(secondRule, index["target"][1]);
        }

        private sealed class CountingComparer : IEqualityComparer<string>
        {
            public int HashCalls { get; private set; }

            public bool Equals(string left, string right)
            {
                return StringComparer.OrdinalIgnoreCase.Equals(left, right);
            }

            public int GetHashCode(string value)
            {
                HashCalls++;
                return StringComparer.OrdinalIgnoreCase.GetHashCode(value);
            }

            public void Reset()
            {
                HashCalls = 0;
            }
        }
    }
}
