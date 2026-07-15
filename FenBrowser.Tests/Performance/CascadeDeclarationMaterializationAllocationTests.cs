using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CascadeDeclarationMaterializationAllocationTests
{
    private const int MatchingRuleCount = 64;
    private const int Iterations = 100;

    private readonly ITestOutputHelper _output;

    public CascadeDeclarationMaterializationAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RepeatedWinningProperty_HasBoundedCascadeAllocations()
    {
        var engine = CreateEngine();
        GC.KeepAlive(engine.ComputeCascadedValues(new Element("div")));

        Dictionary<string, CssDeclaration> cascaded = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            cascaded = engine.ComputeCascadedValues(new Element("div"));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine(
            $"{Iterations} cascades with {MatchingRuleCount} matching declarations allocated {allocated:N0} B.");

        Assert.NotNull(cascaded);
        Assert.Equal("rgb(63, 0, 0)", cascaded!["color"].Value);
        Assert.InRange(allocated, 1, 2_400_000);
    }

    [Fact]
    public void MaterializedWinners_PreserveCascadeShorthandsAndOwnership()
    {
        var importantColor = new CssDeclaration
        {
            Property = "COLOR",
            Value = " red ",
            IsImportant = true
        };
        var stylesheet = new CssStylesheet();
        stylesheet.Rules.Add(CreateRule(0, new CssDeclaration { Property = "margin", Value = "1px" }));
        stylesheet.Rules.Add(CreateRule(1, new CssDeclaration { Property = "margin-left", Value = "2px" }));
        stylesheet.Rules.Add(CreateRule(2, importantColor));
        stylesheet.Rules.Add(CreateRule(3, new CssDeclaration { Property = "color", Value = "blue" }));

        var styleSet = new StyleSet();
        styleSet.SetSingleSheet(stylesheet);
        var cascaded = new CascadeEngine(styleSet).ComputeCascadedValues(new Element("div"));

        Assert.Equal("1px", cascaded["margin-right"].Value);
        Assert.Equal("2px", cascaded["margin-left"].Value);
        Assert.Equal("red", cascaded["color"].Value);
        Assert.True(cascaded["color"].IsImportant);
        Assert.NotSame(importantColor, cascaded["color"]);
    }

    private static CascadeEngine CreateEngine()
    {
        var stylesheet = new CssStylesheet();
        for (var index = 0; index < MatchingRuleCount; index++)
        {
            stylesheet.Rules.Add(CreateRule(index, new CssDeclaration
            {
                Property = "color",
                Value = $"rgb({index}, 0, 0)"
            }));
        }

        var styleSet = new StyleSet();
        styleSet.SetSingleSheet(stylesheet);
        return new CascadeEngine(styleSet);
    }

    private static CssStyleRule CreateRule(int order, CssDeclaration declaration)
    {
        var segment = new SelectorSegment { TagName = "div" };
        var chain = new SelectorChain();
        chain.Segments.Add(segment);

        var rule = new CssStyleRule
        {
            Order = order,
            Selector = new CssSelector
            {
                Raw = "div",
                Chains = new List<SelectorChain> { chain }
            }
        };
        rule.Declarations.Add(declaration);
        return rule;
    }
}
