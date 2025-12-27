using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Domains.DTOs;

public class GetComputedStyleResponse
{
    [JsonPropertyName("computedStyle")]
    public List<CssPropertyDto>? ComputedStyle { get; set; }
}

public class CssPropertyDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public class GetMatchedStylesResponse
{
    [JsonPropertyName("inlineStyle")]
    public CssStyleDto? InlineStyle { get; set; }

    [JsonPropertyName("matchedCSSRules")]
    public List<MatchedRuleDto>? MatchedCSSRules { get; set; }

    [JsonPropertyName("inherited")]
    public List<InheritedStyleEntryDto>? Inherited { get; set; }
}

public class CssStyleDto
{
    [JsonPropertyName("styleId")]
    public CssStyleIdDto? StyleId { get; set; }

    [JsonPropertyName("cssProperties")]
    public List<CssPropertyDto>? CssProperties { get; set; }

    [JsonPropertyName("shorthandEntries")]
    public List<ShorthandEntryDto>? ShorthandEntries { get; set; }

    [JsonPropertyName("cssText")]
    public string? CssText { get; set; }

    [JsonPropertyName("range")]
    public SourceRangeDto? Range { get; set; }
}

public class CssStyleIdDto
{
    [JsonPropertyName("styleSheetId")]
    public string StyleSheetId { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }
}

public class MatchedRuleDto
{
    [JsonPropertyName("rule")]
    public CssRuleDto? Rule { get; set; }

    [JsonPropertyName("matchingSelectors")]
    public List<int>? MatchingSelectors { get; set; }
}

public class CssRuleDto
{
    [JsonPropertyName("styleSheetId")]
    public string? StyleSheetId { get; set; }

    [JsonPropertyName("selectorList")]
    public SelectorListDto? SelectorList { get; set; }

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "regular"; // regular, user-agent, etc.

    [JsonPropertyName("style")]
    public CssStyleDto? Style { get; set; }
}

public class SelectorListDto
{
    [JsonPropertyName("selectors")]
    public List<ValueDto>? Selectors { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class ValueDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class InheritedStyleEntryDto
{
    [JsonPropertyName("inlineStyle")]
    public CssStyleDto? InlineStyle { get; set; }

    [JsonPropertyName("matchedCSSRules")]
    public List<MatchedRuleDto>? MatchedCSSRules { get; set; }
}

public class ShorthandEntryDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public class SourceRangeDto
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }
    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}
