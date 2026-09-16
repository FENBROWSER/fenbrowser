using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Pseudo-element rules are indexed by their key segment, so an element only tests
/// the ones that could match it.
/// </summary>
/// <remarks>
/// The index used to be one flat list per pseudo-element, and the pass ran for every
/// element as soon as the document held a single <c>::before</c> rule anywhere. On
/// youtube.com that was 23,723 pseudo passes against 8,477 real ones and 973 ms of
/// the 1,395 ms the cascade spent matching selectors. Keying it dropped that to
/// 18 ms — but only if every key shape still finds its rule, which is what this
/// pins.
/// </remarks>
public sealed class PseudoElementRuleIndexTests
{
    private const string Markup =
        "<html><head><style>" +
        "#byid::before { content: 'ID'; }" +
        ".bycls::before { content: 'CLS'; }" +
        "p::before { content: 'TAG'; }" +
        "[data-k]::before { content: 'ATTR'; }" +
        ".a.b::after { content: 'MULTI'; }" +
        "div span::before { content: 'DESC'; }" +
        "li::marker { content: 'MARK'; }" +
        "</style></head><body>" +
        "<div id='byid'>1</div><div class='bycls'>2</div><p>3</p>" +
        "<div data-k='1'>4</div><div class='a b'>5</div>" +
        "<div><span>6</span></div><ul><li>7</li></ul>" +
        "</body></html>";

    private static async Task<(Document Document,
        System.Collections.Generic.Dictionary<Node, FenBrowser.Core.Css.CssComputed> Styles)> ComputeAsync()
    {
        var baseUri = new Uri("https://pseudo.test/");
        var document = new HtmlParser(Markup, baseUri).Parse();
        var root = document.Children.OfType<Element>()
            .First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1280, viewportHeight: 800);
        return (document, styles);
    }

    [Theory]
    // One row per way a pseudo rule's key segment can be indexed: by id, class,
    // tag, attribute, a multi-class key, a descendant chain, and ::marker.
    [InlineData("#byid", "before", "ID")]
    [InlineData(".bycls", "before", "CLS")]
    [InlineData("p", "before", "TAG")]
    [InlineData("[data-k]", "before", "ATTR")]
    [InlineData(".a.b", "after", "MULTI")]
    [InlineData("div span", "before", "DESC")]
    [InlineData("li", "marker", "MARK")]
    public async Task PseudoRule_IsFoundForEveryKeySegmentShape(
        string selector, string pseudo, string expectedContent)
    {
        var (document, styles) = await ComputeAsync();
        var element = Assert.IsType<Element>(document.QuerySelector(selector));

        var computed = styles[element];
        var pseudoStyle = pseudo switch
        {
            "before" => computed.Before,
            "after" => computed.After,
            "marker" => computed.Marker,
            _ => throw new ArgumentOutOfRangeException(nameof(pseudo), pseudo, null)
        };

        Assert.NotNull(pseudoStyle);
        Assert.Contains(expectedContent, pseudoStyle.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnElementTheRuleDoesNotName_GetsNoPseudoStyle()
    {
        // The narrowing must not go the other way either: an element with no
        // matching ::before rule should not pick one up from another element's.
        var (document, styles) = await ComputeAsync();
        var unrelated = Assert.IsType<Element>(document.QuerySelector("ul"));

        var before = styles[unrelated].Before;
        Assert.True(
            before == null || string.IsNullOrEmpty(before.Content),
            "an element named by no ::before rule must not inherit another element's");
    }
}
