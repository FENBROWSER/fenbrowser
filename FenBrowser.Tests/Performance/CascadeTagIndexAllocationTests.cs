using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    public class CascadeTagIndexAllocationTests
    {
        [Fact]
        public void TagIndex_MatchesSelectorTagsWithoutCaseNormalization()
        {
            var stylesheet = new CssStylesheet();
            stylesheet.Rules.Add(CreateTagRule("DiV", "color", "red"));

            var styleSet = new StyleSet();
            styleSet.SetSingleSheet(stylesheet);
            var engine = new CascadeEngine(styleSet);

            var cascaded = engine.ComputeCascadedValues(new Element("div"));

            Assert.Equal("red", cascaded["color"].Value);
        }

        [Fact]
        public void TagIndex_DoesNotAllocateNormalizedSelectorNames()
        {
            const int ruleCount = 512;
            var warmupSet = new StyleSet();
            var warmupSheet = new CssStylesheet();
            warmupSheet.Rules.Add(CreateTagRule("warmup-element", "color", "red"));
            warmupSet.SetSingleSheet(warmupSheet);
            new CascadeEngine(warmupSet).HasPseudoRules("before");

            var stylesheet = new CssStylesheet();
            for (var index = 0; index < ruleCount; index++)
            {
                stylesheet.Rules.Add(CreateTagRule($"benchmark-element-{index}", "color", "red"));
            }

            var styleSet = new StyleSet();
            styleSet.SetSingleSheet(stylesheet);
            var engine = new CascadeEngine(styleSet);

            var before = GC.GetAllocatedBytesForCurrentThread();
            engine.HasPseudoRules("before");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(
                allocated <= 94_000,
                $"Tag index construction allocated {allocated:N0} bytes; expected at most 94,000.");
        }

        private static CssStyleRule CreateTagRule(string tagName, string property, string value)
        {
            var segment = new SelectorSegment { TagName = tagName };
            var chain = new SelectorChain();
            chain.Segments.Add(segment);

            var rule = new CssStyleRule
            {
                Selector = new CssSelector
                {
                    Raw = tagName,
                    Chains = new List<SelectorChain> { chain }
                }
            };
            rule.Declarations.Add(new CssDeclaration { Property = property, Value = value });
            return rule;
        }
    }
}
