using FenBrowser.Core.Dom;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.Core.Css;
using System.Collections.Generic;
using System.Text.Json;
using FenBrowser.DevTools.Domains.DTOs;
using System.Threading.Tasks;
using System.Linq;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// CSS Domain handler.
/// Handles CSS inspection: getComputedStyleForNode, getMatchedStylesForNode, etc.
/// </summary>
public class CSSDomain : IProtocolHandler
{
    private readonly INodeRegistry _registry;
    private readonly Func<Node, CssComputed?> _getComputedStyle;
    private readonly Func<Node, List<CssLoader.MatchedRule>>? _getMatchedRules;

    public string Domain => "CSS";

    public CSSDomain(INodeRegistry registry, Func<Node, CssComputed?> getComputedStyle, Func<Node, List<CssLoader.MatchedRule>>? getMatchedRules = null)
    {
        _registry = registry;
        _getComputedStyle = getComputedStyle;
        _getMatchedRules = getMatchedRules;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "getComputedStyleForNode" => GetComputedStyleForNode(request),
            "getMatchedStylesForNode" => GetMatchedStylesForNode(request),
            "enable" => Task.FromResult(ProtocolResponse.Success(request.Id, new { })),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Method {method} not found in domain {Domain}"))
        };
    }

    private Task<ProtocolResponse> GetComputedStyleForNode(ProtocolRequest request)
    {
        if (request.Params == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var node = _registry.GetNode(nodeId);
            
            if (node == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found"));
            
            var styles = _getComputedStyle(node);
            var computedStyle = new List<CssPropertyDto>();
            
            if (styles != null)
            {
                // 1. Helper to add properties
                void Add(string name, string? val) {
                    if (!string.IsNullOrEmpty(val)) 
                        computedStyle.Add(new CssPropertyDto { Name = name, Value = val });
                }
                
                // Box Model & Layout
                Add("display", styles.Display);
                Add("position", styles.Position);
                Add("width", styles.Width?.ToString() ?? styles.WidthExpression);
                Add("height", styles.Height?.ToString() ?? styles.HeightExpression);
                
                // Margins
                Add("margin-top", styles.Margin.Top.ToString());
                Add("margin-right", styles.Margin.Right.ToString());
                Add("margin-bottom", styles.Margin.Bottom.ToString());
                Add("margin-left", styles.Margin.Left.ToString());
                
                // Padding
                Add("padding-top", styles.Padding.Top.ToString());
                Add("padding-right", styles.Padding.Right.ToString());
                Add("padding-bottom", styles.Padding.Bottom.ToString());
                Add("padding-left", styles.Padding.Left.ToString());
                
                // Borders
                Add("border-top-width", styles.BorderThickness.Top.ToString());
                Add("border-right-width", styles.BorderThickness.Right.ToString());
                Add("border-bottom-width", styles.BorderThickness.Bottom.ToString());
                Add("border-left-width", styles.BorderThickness.Left.ToString());
                
                // Font
                Add("font-family", styles.FontFamilyName);
                Add("font-size", styles.FontSize.HasValue ? styles.FontSize.Value.ToString() + "px" : null);
                Add("color", styles.ForegroundColor?.ToString());
                Add("background-color", styles.BackgroundColor?.ToString());

                // 2. Add remaining generic map properties if not already added
                if (styles.Map != null)
                {
                    foreach (var kv in styles.Map)
                    {
                        if (!computedStyle.Any(x => x.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            computedStyle.Add(new CssPropertyDto { Name = kv.Key, Value = kv.Value });
                        }
                    }
                }
            }
            
            return Task.FromResult(ProtocolResponse.Success(request.Id, new { computedStyle = computedStyle }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, ex.Message));
        }
    }
    
    private Task<ProtocolResponse> GetMatchedStylesForNode(ProtocolRequest request)
    {
        if (request.Params == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var node = _registry.GetNode(nodeId);
            
            if (node == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found"));
            
            // Inline styles
            CssStyleDto? inlineStyle = null;
            if (node is Element element && element.Attributes.TryGetValue("style", out var styleStr))
            {
                var cssProperties = new List<CssPropertyDto>();
                var parts = styleStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split(':', 2);
                    if (kv.Length == 2)
                    {
                        cssProperties.Add(new CssPropertyDto { Name = kv[0].Trim(), Value = kv[1].Trim() });
                    }
                }
                
                inlineStyle = new CssStyleDto {
                    StyleId = new CssStyleIdDto { StyleSheetId = "inline", Ordinal = 0 },
                    CssProperties = cssProperties,
                    ShorthandEntries = new List<ShorthandEntryDto>(),
                    CssText = styleStr
                };
            }
            
            // Matched Rules
            var matchedRules = new List<MatchedRuleDto>();
            if (_getMatchedRules != null)
            {
                var validRules = _getMatchedRules(node);
                foreach (var match in validRules)
                {
                    var rule = match.Rule;
                    
                    var selectorList = new SelectorListDto 
                    { 
                        Selectors = rule.Selectors.Select(s => new ValueDto { Text = ReconstructSelector(s) }).ToList() 
                    };

                    var cssProperties = new List<CssPropertyDto>();
                    foreach(var decl in rule.Declarations.Values)
                    {
                        cssProperties.Add(new CssPropertyDto { Name = decl.Name, Value = decl.Value });
                    }

                    var ruleDto = new CssRuleDto
                    {
                        SelectorList = selectorList,
                        Origin = match.Source.Origin == CssLoader.CssOrigin.UserAgent ? "user-agent" : "regular",
                        Style = new CssStyleDto
                        {
                            StyleId = new CssStyleIdDto { StyleSheetId = "style_" + rule.SourceOrder, Ordinal = 0 },
                            CssProperties = cssProperties,
                            ShorthandEntries = new List<ShorthandEntryDto>()
                        }
                    };
                    
                    // Assume all selectors matched for now (simplification)
                    var matchingSelectors = Enumerable.Range(0, rule.Selectors.Count).ToList();

                    matchedRules.Add(new MatchedRuleDto 
                    { 
                        Rule = ruleDto,
                        MatchingSelectors = matchingSelectors 
                    });
                }
            }
            
            return Task.FromResult(ProtocolResponse.Success(request.Id, new GetMatchedStylesResponse { 
                InlineStyle = inlineStyle,
                MatchedCSSRules = matchedRules,
                Inherited = new List<InheritedStyleEntryDto>()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, ex.Message));
        }
    }

    private string ReconstructSelector(CssLoader.SelectorChain chain)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in chain.Segments)
        {
            if (sb.Length > 0) sb.Append(" ");
            
            if (!string.IsNullOrEmpty(seg.Tag)) sb.Append(seg.Tag);
            if (!string.IsNullOrEmpty(seg.Id)) sb.Append("#").Append(seg.Id);
            if (seg.Classes != null) foreach (var c in seg.Classes) sb.Append(".").Append(c);
            if (seg.PseudoClasses != null) foreach (var p in seg.PseudoClasses) sb.Append(":").Append(p);
            if (!string.IsNullOrEmpty(seg.PseudoElement)) sb.Append("::").Append(seg.PseudoElement);
            
            if (seg.Next.HasValue)
            {
                switch (seg.Next.Value)
                {
                    case CssLoader.Combinator.Child: sb.Append(" > "); break;
                    case CssLoader.Combinator.AdjacentSibling: sb.Append(" + "); break;
                    case CssLoader.Combinator.GeneralSibling: sb.Append(" ~ "); break;
                    case CssLoader.Combinator.Descendant: sb.Append(" "); break;
                }
            }
        }
        return sb.ToString().Trim();
    }
}
