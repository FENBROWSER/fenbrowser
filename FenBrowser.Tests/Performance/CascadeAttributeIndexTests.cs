using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class CascadeAttributeIndexTests
{
    [Fact]
    public void AttributeOnlyRules_AreExcludedFromUniversalCandidates()
    {
        var stylesheet = new CssStylesheet();
        for (var index = 0; index < 512; index++)
        {
            stylesheet.Rules.Add(CreateAttributeRule($"data-rule-{index}", index));
        }

        var styleSet = new StyleSet();
        styleSet.SetSingleSheet(stylesheet);
        var engine = new CascadeEngine(styleSet);

        Assert.Equal(1, engine.GetAttributeCandidateCount("data-rule-42"));
        Assert.Equal(0, engine.UniversalCandidateCount);

        var unmatched = engine.ComputeCascadedValues(new Element("div"));
        Assert.Empty(unmatched);

        var matchedElement = new Element("div");
        matchedElement.SetAttribute("data-rule-42", string.Empty);
        var matched = engine.ComputeCascadedValues(matchedElement);
        Assert.Equal("42", matched["z-index"].Value);
    }

    [Fact]
    public void AttributeIndex_PreservesSelectorListAndTagAttributeSemantics()
    {
        var attributeSegment = new SelectorSegment();
        attributeSegment.Attributes.Add(new AttributeSelector
        {
            Name = "data-active",
            Operator = "=",
            Value = "yes"
        });
        var attributeChain = new SelectorChain();
        attributeChain.Segments.Add(attributeSegment);

        var tagAttributeSegment = new SelectorSegment { TagName = "button" };
        tagAttributeSegment.Attributes.Add(new AttributeSelector { Name = "disabled" });
        var tagAttributeChain = new SelectorChain();
        tagAttributeChain.Segments.Add(tagAttributeSegment);

        var rule = new CssStyleRule
        {
            Selector = new CssSelector
            {
                Raw = "[data-active=yes], button[disabled]",
                Chains = new List<SelectorChain> { attributeChain, tagAttributeChain }
            }
        };
        rule.Declarations.Add(new CssDeclaration { Property = "opacity", Value = "0.5" });

        var stylesheet = new CssStylesheet();
        stylesheet.Rules.Add(rule);
        var styleSet = new StyleSet();
        styleSet.SetSingleSheet(stylesheet);
        var engine = new CascadeEngine(styleSet);

        var active = new Element("div");
        active.SetAttribute("data-active", "yes");
        Assert.Equal("0.5", engine.ComputeCascadedValues(active)["opacity"].Value);

        var disabled = new Element("button");
        disabled.SetAttribute("disabled", string.Empty);
        Assert.Equal("0.5", engine.ComputeCascadedValues(disabled)["opacity"].Value);
    }

    private static CssStyleRule CreateAttributeRule(string attributeName, int order)
    {
        var segment = new SelectorSegment();
        segment.Attributes.Add(new AttributeSelector { Name = attributeName });
        var chain = new SelectorChain();
        chain.Segments.Add(segment);
        var rule = new CssStyleRule
        {
            Order = order,
            Selector = new CssSelector
            {
                Raw = $"[{attributeName}]",
                Chains = new List<SelectorChain> { chain }
            }
        };
        rule.Declarations.Add(new CssDeclaration { Property = "z-index", Value = order.ToString() });
        return rule;
    }
}
