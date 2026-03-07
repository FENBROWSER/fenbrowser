using FenBrowser.Core.Dom.V2;
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
    private readonly Action<Node, string, string>? _setInlineStyle;
    private readonly Action? _triggerRepaint;
    private readonly Func<Func<ProtocolResponse>, Task<ProtocolResponse>> _dispatchAsync;

    public string Domain => "CSS";

    public CSSDomain(
        INodeRegistry registry, 
        Func<Node, CssComputed?> getComputedStyle, 
        Func<Node, List<CssLoader.MatchedRule>>? getMatchedRules = null,
        Action<Node, string, string>? setInlineStyle = null,
        Action? triggerRepaint = null,
        Func<Func<ProtocolResponse>, Task<ProtocolResponse>>? dispatchAsync = null)
    {
        _registry = registry;
        _getComputedStyle = getComputedStyle;
        _getMatchedRules = getMatchedRules;
        _setInlineStyle = setInlineStyle;
        _triggerRepaint = triggerRepaint;
        _dispatchAsync = dispatchAsync ?? (operation => Task.FromResult(operation()));
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "getComputedStyleForNode" => GetComputedStyleForNode(request),
            "getMatchedStylesForNode" => GetMatchedStylesForNode(request),
            "setStyleTexts" => SetStyleTexts(request),
            "enable" => Task.FromResult(ProtocolResponse.Success(request.Id, new { })),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Method {method} not found in domain {Domain}"))
        };
    }

    private Task<ProtocolResponse> SetStyleTexts(ProtocolRequest request)
    {
        if (request.Params == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        
        try
        {
            return DispatchAsync(() =>
            {
                var edits = request.Params.Value.GetProperty("edits");
                foreach (var edit in edits.EnumerateArray())
                {
                    var nodeId = edit.GetProperty("nodeId").GetInt32();
                    var propertyName = edit.GetProperty("propertyName").GetString() ?? "";
                    var value = edit.GetProperty("value").GetString() ?? "";

                    var node = _registry.GetNode(nodeId);
                    if (node != null && _setInlineStyle != null)
                    {
                        _setInlineStyle(node, propertyName, value);
                    }
                }

                _triggerRepaint?.Invoke();
                return ProtocolResponse.Success(request.Id, new { });
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, ex.Message));
        }
    }

    private Task<ProtocolResponse> GetComputedStyleForNode(ProtocolRequest request)
    {
        if (request.Params == null) return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node == null)
                {
                    return ProtocolResponse.Failure(request.Id, "Node not found");
                }

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
            
                return ProtocolResponse.Success(request.Id, new { computedStyle = computedStyle });
            });
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
            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node == null)
                {
                    return ProtocolResponse.Failure(request.Id, "Node not found");
                }

                CssStyleDto? inlineStyle = null;
            if (node is Element element && (element.GetAttribute("style") is string styleStr))
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
            
            // Matched Rules with deduplication
            var matchedRules = new List<MatchedRuleDto>();
            var seenRules = new HashSet<string>(); 
            
            if (_getMatchedRules != null)
            {
                var validRules = _getMatchedRules(node);
                foreach (var match in validRules)
                {
                    if (match.Rule is FenBrowser.FenEngine.Rendering.Css.CssStyleRule styleRule)
                    {
                        var selectorText = styleRule.Selector?.Raw ?? "*";
                        
                        // Deduplication key
                        var propsSignature = string.Join(";", styleRule.Declarations
                            .OrderBy(d => d.Property)
                            .Select(d => $"{d.Property}:{d.Value}"));
                        var ruleSignature = $"{selectorText}|{propsSignature}|{match.Source.Origin}"; // Origin usage might need cast or ToString
                        
                        if (seenRules.Contains(ruleSignature)) continue;
                        seenRules.Add(ruleSignature);
                        
                        var selectorList = new SelectorListDto 
                        { 
                            Selectors = new List<ValueDto> { new ValueDto { Text = selectorText } },
                            Text = selectorText
                        };

                        var cssProperties = new List<CssPropertyDto>();
                        foreach(var decl in styleRule.Declarations)
                        {
                            cssProperties.Add(new CssPropertyDto { Name = decl.Property, Value = decl.Value });
                        }

                        var ruleDto = new CssRuleDto
                        {
                            SelectorList = selectorList,
                            Origin = "regular", // Simplify mapping
                            Style = new CssStyleDto
                            {
                                StyleId = new CssStyleIdDto { StyleSheetId = "style_" + styleRule.Order, Ordinal = 0 },
                                CssProperties = cssProperties,
                                ShorthandEntries = new List<ShorthandEntryDto>()
                            }
                        };
                        
                        matchedRules.Add(new MatchedRuleDto 
                        { 
                            Rule = ruleDto,
                            MatchingSelectors = new List<int> { 0 } 
                        });
                    }
                }
            }
            
            // Inherited styles (Simplified loop for brevity - same logic applies)
            // Ideally should replicate ancestor logic with same type checks
            var inherited = new List<InheritedStyleEntryDto>();
             if (node is Element currentElement)
            {
                var parent = currentElement.ParentNode;
                while (parent != null && parent is Element parentElement)
                {
                    var inheritedEntry = new InheritedStyleEntryDto();
                    // ... Inline style (omitted for brevity, assume similar) ...
                    
                    if (_getMatchedRules != null)
                    {
                        var ancestorMatchedRules = new List<MatchedRuleDto>();
                        var parentRules = _getMatchedRules(parentElement);
                         foreach (var match in parentRules)
                        {
                            if (match.Rule is FenBrowser.FenEngine.Rendering.Css.CssStyleRule styleRule)
                            {
                                var selectorText = styleRule.Selector?.Raw ?? "*";
                                var cssProperties = new List<CssPropertyDto>();
                                foreach(var decl in styleRule.Declarations)
                                     cssProperties.Add(new CssPropertyDto { Name = decl.Property, Value = decl.Value });

                                var ruleDto = new CssRuleDto
                                {
                                    SelectorList = new SelectorListDto { Selectors = new List<ValueDto> { new ValueDto { Text = selectorText } }, Text = selectorText },
                                    Origin = "regular",
                                    Style = new CssStyleDto
                                    {
                                        StyleId = new CssStyleIdDto { StyleSheetId = "style_" + styleRule.Order, Ordinal = 0 },
                                        CssProperties = cssProperties,
                                        ShorthandEntries = new List<ShorthandEntryDto>()
                                    }
                                };
                                ancestorMatchedRules.Add(new MatchedRuleDto { Rule = ruleDto, MatchingSelectors = new List<int> { 0 } });
                            }
                        }
                        inheritedEntry.MatchedCSSRules = ancestorMatchedRules;
                        if (ancestorMatchedRules.Count > 0) inherited.Add(inheritedEntry);
                    }
                    parent = parentElement.ParentNode;
                }
            }
            
                return ProtocolResponse.Success(request.Id, new GetMatchedStylesResponse { 
                    InlineStyle = inlineStyle,
                    MatchedCSSRules = matchedRules,
                    Inherited = inherited
                });
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, ex.Message));
        }
    }

    private Task<ProtocolResponse> DispatchAsync(Func<ProtocolResponse> operation)
    {
        return _dispatchAsync(operation);
    }

    // Removed ReconstructSelector as we utilize Raw selector string for now
}

