using SkiaSharp;
using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Css;
using FenBrowser.Core;

namespace FenBrowser.DevTools.Panels;

/// <summary>
/// Elements panel showing DOM tree and element details.
/// Uses the DevTools protocol for all data access.
/// </summary>
public class ElementsPanel : DevToolsPanelBase
{
    public override string Title => "Elements";
    public override string? Shortcut => "Ctrl+Shift+C";
    
    // DOM tree state (Protocol-based)
    private DomNodeDto? _rootNode;
    private readonly Dictionary<int, DomNodeDto> _nodeMap = new();
    private readonly List<DomTreeNode> _flattenedNodes = new();
    private readonly HashSet<int> _expandedNodes = new();
    private int? _selectedNodeId;
    private int? _hoveredNodeId;
    private int _hoveredIndex = -1;
    
    // Layout
    private float _splitterX = 400f; // Default valid value
    private bool _draggingSplitter;
    private int _sidebarTab = 0; // 0: Styles, 1: Computed, 2: Layout
    private const float MIN_PANEL_WIDTH = 200f;
    private const float SPLITTER_WIDTH = 4f;
    
    // Styles panel scroll
    private float _stylesScrollY;
    private float _stylesMaxScrollY;
    
    private float _treeWidth;
    private float _stylesWidth;
    
    // Mutation/Editing State
    private string? _editingAttrKey = null;
    private string _editingAttrValue = "";
    private int? _editingNodeId = null;
    private string _editingNodeValue = "";
    private bool _cursorBlink = true;
    private DateTime _lastBlink = DateTime.Now;

    // Style and Layout Data
    private GetComputedStyleResponse? _computedStyleData;
    private GetMatchedStylesResponse? _matchedStyleData;
    
    protected override void OnHostChanged()
    {
        if (Host != null)
        {
            Host.DomChanged += () => _ = RefreshTreeAsync();
            Host.ProtocolEventReceived += HandleProtocolEvent;
        }
        
        _ = RefreshTreeAsync();
    }
    
    public override void OnActivate()
    {
        _ = RefreshTreeAsync();
    }
    
    public override void OnDeactivate()
    {
        _hoveredIndex = -1;
        _hoveredNodeId = null;
        _ = SendHighlightCommand(null);
        Invalidate();
    }
    
    private void HandleProtocolEvent(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var method = doc.RootElement.GetProperty("method").GetString();
            
            if (method == "DOM.attributeModified")
            {
                var evt = ProtocolJson.Deserialize<ProtocolEvent<AttributeModifiedEvent>>(json);
                if (evt?.Params != null && _nodeMap.TryGetValue(evt.Params.NodeId, out var node))
                {
                    var newAttrs = node.Attributes != null ? new Dictionary<string, string>(node.Attributes) : new();
                    newAttrs[evt.Params.Name] = evt.Params.Value ?? "";
                    _nodeMap[evt.Params.NodeId] = node with { Attributes = newAttrs };
                    RefreshFlattenedTree();
                    Invalidate();
                }
            }
            else if (method == "DOM.attributeRemoved")
            {
                var evt = ProtocolJson.Deserialize<ProtocolEvent<AttributeRemovedEvent>>(json);
                if (evt?.Params != null && _nodeMap.TryGetValue(evt.Params.NodeId, out var node))
                {
                    if (node.Attributes != null && node.Attributes.ContainsKey(evt.Params.Name))
                    {
                        var newAttrs = new Dictionary<string, string>(node.Attributes);
                        newAttrs.Remove(evt.Params.Name);
                        _nodeMap[evt.Params.NodeId] = node with { Attributes = newAttrs };
                        RefreshFlattenedTree();
                        Invalidate();
                    }
                }
            }
            else if (method == "DOM.childNodeInserted" || method == "DOM.childNodeRemoved")
            {
                // Simple strategy: refresh full tree on structural change for now
                _ = RefreshTreeAsync();
            }
            else if (method == "DOM.characterDataModified")
            {
                var evt = ProtocolJson.Deserialize<ProtocolEvent<CharacterDataModifiedEvent>>(json);
                if (evt?.Params != null && _nodeMap.TryGetValue(evt.Params.NodeId, out var node))
                {
                    _nodeMap[evt.Params.NodeId] = node with { NodeValue = evt.Params.CharacterData };
                    RefreshFlattenedTree();
                    Invalidate();
                }
            }
            else if (method == "CSS.styleSheetAdded" || method == "CSS.mediaQueryResultChanged")
            {
                if (_selectedNodeId.HasValue) _ = FetchStylesAsync(_selectedNodeId.Value);
            }
        }
        catch { /* Ignore parse errors */ }
    }
    
    
    private async Task RefreshTreeAsync()
    {
        if (Host == null) return;
        
        try
        {
            var request = new ProtocolRequest<object> { Id = 1001, Method = "DOM.getDocument", Params = new { } };
            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<ProtocolResponse<GetDocumentResult>>(responseJson, ProtocolJson.Options);
            
            if (response?.Result?.Root != null)
            {
                _rootNode = response.Result.Root;
                _nodeMap.Clear();
                UpdateNodeMap(_rootNode);
                
                // Auto-expand root and its main children (html, body)
                var nodesToExpand = new List<DomNodeDto>();
                nodesToExpand.Add(_rootNode);

                _expandedNodes.Add(_rootNode.NodeId);
                if (_rootNode.Children != null)
                {
                    foreach (var child in _rootNode.Children)
                    {
                        if (child.NodeName.ToLower() == "html")
                        {
                            _expandedNodes.Add(child.NodeId);
                            nodesToExpand.Add(child);
                            
                            if (child.Children != null)
                            {
                                foreach (var grandChild in child.Children)
                                {
                                    if (grandChild.NodeName.ToLower() == "body")
                                    {
                                        _expandedNodes.Add(grandChild.NodeId);
                                        nodesToExpand.Add(grandChild);
                                    }
                                }
                            }
                        }
                    }
                }

                // Ensure all expanded nodes have their children requested
                foreach (var node in nodesToExpand)
                {
                    if (node.Children == null && node.ChildNodeCount > 0)
                    {
                        await RequestChildNodesAsync(node.NodeId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.FenLogger.Error($"[ElementsPanel] Protocol error: {ex.Message}", LogCategory.General);
        }
        
        RefreshFlattenedTree();
        Invalidate();
    }
    
    private async Task RequestChildNodesAsync(int nodeId)
    {
        if (Host == null) return;
        
        try
        {
            var request = new ProtocolRequest<object> { Id = 1002, Method = "DOM.requestChildNodes", Params = new { nodeId = nodeId } };
            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<ProtocolResponse<RequestChildNodesResult>>(responseJson, ProtocolJson.Options);
            
            if (response?.Result != null && _nodeMap.TryGetValue(nodeId, out var parent))
            {
                // Update parent with children
                var updatedParent = parent with { Children = response.Result.Nodes };
                _nodeMap[nodeId] = updatedParent;
                
                // Also update root node if this is part of it (recursively find and update)
                // In a simple flat node structure, we'd just need to make sure the root node structure is updated
                // But since _rootNode is the tree, we need to find the node within _rootNode
                UpdateNodeInTree(_rootNode, updatedParent);
                
                // Update map for new children
                foreach (var child in response.Result.Nodes)
                    UpdateNodeMap(child);
                
                RefreshFlattenedTree();
                Invalidate();
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.FenLogger.Error($"[ElementsPanel] RequestChildNodes error: {ex.Message}", LogCategory.General);
        }
    }
    
    private void UpdateNodeInTree(DomNodeDto? root, DomNodeDto updated)
    {
        if (root == null) return;
        
        // If root itself is being updated
        if (root.NodeId == updated.NodeId)
        {
             if (root == _rootNode) _rootNode = updated;
             return;
        }

        if (root.Children == null) return;
        
        for (int i = 0; i < root.Children.Length; i++)
        {
            if (root.Children[i].NodeId == updated.NodeId)
            {
                root.Children[i] = updated;
                return;
            }
            UpdateNodeInTree(root.Children[i], updated);
        }
    }
    
    private void UpdateNodeMap(DomNodeDto node)
    {
        _nodeMap[node.NodeId] = node;
        if (node.Children != null)
        {
            foreach (var child in node.Children)
                UpdateNodeMap(child);
        }
    }
    
    private void RefreshFlattenedTree()
    {
        _flattenedNodes.Clear();
        
        if (_rootNode != null)
        {
            AddNodeToFlatList(_rootNode, 0);
        }
        
        // Calculate max scroll
        MaxScrollY = Math.Max(0, _flattenedNodes.Count * DevToolsTheme.ItemHeight - Bounds.Height + (DevToolsTheme.PaddingNormal * 4));
    }
    
    private void AddNodeToFlatList(DomNodeDto node, int depth)
    {
        // Skip certain node types for cleaner tree (optional, but keep for now)
        if (node.NodeType == 9) // Document
        {
             if (node.Children != null)
             {
                 foreach (var child in node.Children) AddNodeToFlatList(child, depth);
             }
             return;
        }

        bool hasChildren = node.ChildNodeCount > 0;
        bool isExpanded = _expandedNodes.Contains(node.NodeId);
        
        // Add opening tag / text node
        _flattenedNodes.Add(new DomTreeNode(node, depth, hasChildren, isExpanded));
        
        if (node.NodeType == 3 && node.NodeValue == "g")
        {
            FenBrowser.Core.FenLogger.Debug($"[ElementsPanel] Spotted 'g' node! Parent: {node.ParentId}, Id: {node.NodeId}");
        }
        
        if (isExpanded && node.Children != null)
        {
            foreach (var child in node.Children)
            {
                AddNodeToFlatList(child, depth + 1);
            }
            
            // Add closing tag for elements that have children/content
            if (node.NodeType == 1 && (node.Children.Length > 0 || node.NodeName == "script" || node.NodeName == "style"))
            {
                _flattenedNodes.Add(new DomTreeNode(node, depth, false, false, true));
            }
        }
    }
    
    protected override void OnPaint(SKCanvas canvas, SKRect bounds)
    {
        // Initialize splitter position
        if (_splitterX == 0)
        {
            _splitterX = bounds.Left + (bounds.Width * 0.6f);
        }
        
        _treeWidth = _splitterX - bounds.Left;
        _stylesWidth = bounds.Right - _splitterX - SPLITTER_WIDTH;
        
        // Draw DOM tree
        var treeBounds = new SKRect(bounds.Left, bounds.Top, _splitterX, bounds.Bottom);
        DrawDomTree(canvas, treeBounds);
        
        // Draw splitter
        DrawSplitter(canvas, bounds);
        
        // Draw styles panel
        var stylesBounds = new SKRect(_splitterX + SPLITTER_WIDTH, bounds.Top, bounds.Right, bounds.Bottom);
        DrawStylesPanel(canvas, stylesBounds);
    }
    
    
    private void DrawDomTree(SKCanvas canvas, SKRect bounds)
    {
        float y = bounds.Top + DevToolsTheme.PaddingNormal - ScrollY;
        
        using var tagPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxTag);
        using var attrNamePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxAttribute);
        using var attrValuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxString);
        using var punctPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
        
        for (int i = 0; i < _flattenedNodes.Count; i++)
        {
            var node = _flattenedNodes[i];
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top) continue;
            if (itemY > bounds.Bottom) break;
            
            // Add base margin for arrows
            float x = bounds.Left + 20 + node.Depth * 16f;
            
            // Draw selection / hover background (only for opening tags/text)
            if (!node.IsClosingTag)
            {
                if (node.Node.NodeId == _selectedNodeId)
                {
                    using var selectPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundSelected);
                    canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), selectPaint);
                }
                else if (i == _hoveredIndex)
                {
                    using var hoverPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundHover);
                    canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), hoverPaint);
                }
            }
            
            float textY = itemY + DevToolsTheme.ItemHeight / 2 + 4;

            // Draw opening tag, text, or closing tag
            if (node.IsClosingTag)
            {
                string closingTagName = node.Node.NodeName.ToLower();
                canvas.DrawText("</", x, textY, punctPaint);
                float bracketWidth = punctPaint.MeasureText("</");
                canvas.DrawText(closingTagName, x + bracketWidth, textY, tagPaint);
                float tagWidth = tagPaint.MeasureText(closingTagName);
                canvas.DrawText(">", x + bracketWidth + tagWidth, textY, punctPaint);
                continue;
            }

            // Draw expand/collapse arrow (ONLY for elements)
            if (node.HasChildren && node.Node.NodeType == 1)
            {
                using var arrowPaint = DevToolsTheme.CreateFillPaint(node.Node.NodeId == _selectedNodeId ? DevToolsTheme.TextPrimary : DevToolsTheme.TextSecondary);
                using var arrowPath = new SKPath();
                
                float centerX = x - 12; // More space for even larger hit area
                float centerY = itemY + DevToolsTheme.ItemHeight / 2;
                
                if (node.IsExpanded)
                {
                    // Down triangle
                    arrowPath.MoveTo(centerX - 6, centerY - 4);
                    arrowPath.LineTo(centerX + 6, centerY - 4);
                    arrowPath.LineTo(centerX, centerY + 5);
                }
                else
                {
                    // Right triangle
                    arrowPath.MoveTo(centerX - 4, centerY - 6);
                    arrowPath.LineTo(centerX - 4, centerY + 6);
                    arrowPath.LineTo(centerX + 5, centerY);
                }
                arrowPath.Close();
                canvas.DrawPath(arrowPath, arrowPaint);
            }
            
            if (node.Node.NodeType == 3) // Text
            {
                if (_editingNodeId == node.Node.NodeId)
                {
                    // Draw edit background
                    float textWidth = attrValuePaint.MeasureText(_editingNodeValue);
                    var editRect = new SKRect(x - 2, itemY + 4, x + textWidth + 8, itemY + DevToolsTheme.ItemHeight - 4);
                    using var editBg = DevToolsTheme.CreateFillPaint(new SKColor(50, 50, 50));
                    canvas.DrawRect(editRect, editBg);
                    
                    canvas.DrawText(_editingNodeValue, x, textY, attrValuePaint);
                    
                    // Draw cursor
                    if (_cursorBlink)
                        canvas.DrawLine(x + textWidth, textY - 12, x + textWidth, textY + 2, attrValuePaint);
                }
                else
                {
                    canvas.DrawText("\"" + node.Node.NodeValue + "\"", x, textY, attrValuePaint);
                }
            }
            else
            {
                // Opening bracket
                canvas.DrawText("<", x, textY, punctPaint);
                x += punctPaint.MeasureText("<");
                
                // Tag name
                string tagName = node.Node.NodeName.ToLower();
                canvas.DrawText(tagName, x, textY, tagPaint);
                x += tagPaint.MeasureText(tagName);
                
                // Attributes
                if (node.Node.Attributes != null)
                {
                    foreach (var attr in node.Node.Attributes)
                    {
                        string attrName = " " + attr.Key;
                        canvas.DrawText(attrName, x, textY, attrNamePaint);
                        x += attrNamePaint.MeasureText(attrName);
                        
                        canvas.DrawText("=\"", x, textY, punctPaint);
                        x += punctPaint.MeasureText("=\"");
                        
                        string attrValue = attr.Value;
                        if (attrValue.Length > 30) attrValue = attrValue.Substring(0, 27) + "...";
                        canvas.DrawText(attrValue, x, textY, attrValuePaint);
                        x += attrValuePaint.MeasureText(attrValue);
                        
                        canvas.DrawText("\"", x, textY, punctPaint);
                        x += punctPaint.MeasureText("\"");
                    }
                }
                
                // Closing bracket
                if (!node.HasChildren || !node.IsExpanded)
                {
                    if (node.HasChildren)
                        canvas.DrawText(">...</" + tagName + ">", x, textY, punctPaint);
                    else
                        canvas.DrawText(" />", x, textY, punctPaint);
                }
                else
                {
                    canvas.DrawText(">", x, textY, punctPaint);
                }
            }
        }
    }
    
    private void DrawSplitter(SKCanvas canvas, SKRect bounds)
    {
        using var splitterPaint = DevToolsTheme.CreateFillPaint(_draggingSplitter ? DevToolsTheme.TabBorder : DevToolsTheme.Border);
        canvas.DrawRect(new SKRect(_splitterX, bounds.Top, _splitterX + SPLITTER_WIDTH, bounds.Bottom), splitterPaint);
    }
    
    private void DrawStylesPanel(SKCanvas canvas, SKRect bounds)
    {
        // Tabs
        float tabWidth = bounds.Width / 3;
        string[] tabs = { "Styles", "Computed", "Layout" };
        
        using var tabBorderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var activeTabPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.TabActive);
        using var tabTextPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeNormal);
        using var inactiveTabTextPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeNormal);
        
        for (int i = 0; i < tabs.Length; i++)
        {
            var tabRect = new SKRect(bounds.Left + i * tabWidth, bounds.Top, bounds.Left + (i + 1) * tabWidth, bounds.Top + 32);
            if (i == _sidebarTab)
            {
                canvas.DrawRect(tabRect, activeTabPaint);
                canvas.DrawLine(tabRect.Left, tabRect.Bottom, tabRect.Right, tabRect.Bottom, activeTabPaint); // Hide border at bottom
            }
            
            float textWidth = tabTextPaint.MeasureText(tabs[i]);
            canvas.DrawText(tabs[i], tabRect.Left + (tabWidth - textWidth) / 2, tabRect.Top + 20, i == _sidebarTab ? tabTextPaint : inactiveTabTextPaint);
            canvas.DrawLine(tabRect.Right, tabRect.Top + 4, tabRect.Right, tabRect.Bottom - 4, tabBorderPaint);
        }
        
        canvas.DrawLine(bounds.Left, bounds.Top + 32, bounds.Right, bounds.Top + 32, tabBorderPaint);
        
        float contentTop = bounds.Top + 32 + DevToolsTheme.PaddingNormal;
        
        if (_selectedNodeId == null)
        {
            using var hintPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("Select an element to see " + tabs[_sidebarTab].ToLower(), bounds.Left + DevToolsTheme.PaddingNormal, contentTop + 20, hintPaint);
            return;
        }
        
        if (!_nodeMap.TryGetValue(_selectedNodeId.Value, out var selectedNode)) return;
        
        switch (_sidebarTab)
        {
            case 0: DrawStylesContent(canvas, bounds, selectedNode, contentTop); break;
            case 1: DrawComputedContent(canvas, bounds, selectedNode, contentTop); break;
            case 2: DrawLayoutContent(canvas, bounds, selectedNode, contentTop); break;
        }
    }

    private async Task FetchStylesAsync(int nodeId)
    {
        try
        {
            // Fetch matched styles (for Styles tab)
            var matchedJson = await Host.SendProtocolCommandAsync($"{{\"id\": {nodeId + 1000}, \"method\": \"CSS.getMatchedStylesForNode\", \"params\": {{\"nodeId\": {nodeId}}}}}");
            var matchedResp = ProtocolJson.Deserialize<ProtocolResponse<GetMatchedStylesResponse>>(matchedJson);
            if (matchedResp?.Result != null) _matchedStyleData = matchedResp.Result;

            // Fetch computed styles (for Computed tab)
            var computedJson = await Host.SendProtocolCommandAsync($"{{\"id\": {nodeId + 2000}, \"method\": \"CSS.getComputedStyleForNode\", \"params\": {{\"nodeId\": {nodeId}}}}}");
            var computedResp = ProtocolJson.Deserialize<ProtocolResponse<GetComputedStyleResponse>>(computedJson);
            if (computedResp?.Result != null) _computedStyleData = computedResp.Result;

            Invalidate();
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[ElementsPanel] FetchStylesAsync error: {ex.Message}", LogCategory.General);
        }
    }

    private void DrawStylesContent(SKCanvas canvas, SKRect bounds, DomNodeDto selectedNode, float y)
    {
        y -= _stylesScrollY;
        using var propPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxProperty);
        using var valuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxValue);
        using var punctPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
        using var mutedPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
        
        // 1. Element Inline Style
        canvas.DrawText("element.style {", bounds.Left + DevToolsTheme.PaddingNormal, y, mutedPaint);
        y += DevToolsTheme.ItemHeight;
        
        if (_matchedStyleData?.InlineStyle?.CssProperties != null)
        {
            foreach (var prop in _matchedStyleData.InlineStyle.CssProperties)
            {
                if (y > bounds.Bottom) break;
                if (y < bounds.Top + 32) { y += DevToolsTheme.ItemHeight; continue; }
                
                float x = bounds.Left + DevToolsTheme.PaddingNormal * 2;
                canvas.DrawText(prop.Name, x, y, propPaint);
                x += Math.Max(120, propPaint.MeasureText(prop.Name) + 10);
                canvas.DrawText(": ", x, y, punctPaint);
                x += punctPaint.MeasureText(": ");
                canvas.DrawText(prop.Value + ";", x, y, valuePaint);
                y += DevToolsTheme.ItemHeight;
            }
        }
        else if (selectedNode.Attributes != null)
        {
            // Fallback: show attributes if protocol matched style is missing
            foreach (var kv in selectedNode.Attributes)
            {
                if (y > bounds.Bottom) break;
                if (y < bounds.Top + 32) { y += DevToolsTheme.ItemHeight; continue; }
                
                float x = bounds.Left + DevToolsTheme.PaddingNormal * 2;
                canvas.DrawText(kv.Key, x, y, propPaint);
                x += Math.Max(120, propPaint.MeasureText(kv.Key) + 10);
                canvas.DrawText(": ", x, y, punctPaint);
                x += punctPaint.MeasureText(": ");
                canvas.DrawText(kv.Value + ";", x, y, valuePaint);
                y += DevToolsTheme.ItemHeight;
            }
        }
        
        canvas.DrawText("}", bounds.Left + DevToolsTheme.PaddingNormal, y, mutedPaint);
        y += DevToolsTheme.ItemHeight * 1.5f;

        // 2. Placeholder for user agent or other rules
        if (_matchedStyleData?.MatchedCSSRules != null)
        {
            foreach (var rule in _matchedStyleData.MatchedCSSRules)
            {
                string selector = rule.Rule?.SelectorList?.Text ?? "selector";
                canvas.DrawText(selector + " {", bounds.Left + DevToolsTheme.PaddingNormal, y, mutedPaint);
                y += DevToolsTheme.ItemHeight;
                
                if (rule.Rule?.Style?.CssProperties != null)
                {
                    foreach (var prop in rule.Rule.Style.CssProperties)
                    {
                        if (y > bounds.Bottom) break;
                        float x = bounds.Left + DevToolsTheme.PaddingNormal * 2;
                        canvas.DrawText(prop.Name, x, y, propPaint);
                        x += Math.Max(120, propPaint.MeasureText(prop.Name) + 10);
                        canvas.DrawText(": ", x, y, punctPaint);
                        x += punctPaint.MeasureText(": ");
                        canvas.DrawText(prop.Value + ";", x, y, valuePaint);
                        y += DevToolsTheme.ItemHeight;
                    }
                }
                
                canvas.DrawText("}", bounds.Left + DevToolsTheme.PaddingNormal, y, mutedPaint);
                y += DevToolsTheme.ItemHeight * 1.5f;
            }
        }
        
        _stylesMaxScrollY = Math.Max(0, y + _stylesScrollY - bounds.Bottom);
    }

    private void DrawComputedContent(SKCanvas canvas, SKRect bounds, DomNodeDto selectedNode, float y)
    {
        y -= _stylesScrollY;
        using var propPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxProperty);
        using var valuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxValue);
        using var punctPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);

        if (_computedStyleData?.ComputedStyle != null)
        {
            foreach (var prop in _computedStyleData.ComputedStyle.OrderBy(p => p.Name))
            {
                if (y > bounds.Bottom) break;
                if (y < bounds.Top + 32) { y += DevToolsTheme.ItemHeight; continue; }

                float x = bounds.Left + DevToolsTheme.PaddingNormal;
                canvas.DrawText(prop.Name, x, y, propPaint);
                x += Math.Max(160, propPaint.MeasureText(prop.Name) + 10);
                canvas.DrawText(prop.Value, x, y, valuePaint);
                
                y += DevToolsTheme.ItemHeight;
            }
        }
        else
        {
            using var hintPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No computed styles available", bounds.Left + DevToolsTheme.PaddingNormal, y + 20, hintPaint);
            y += 40;
        }
        
        _stylesMaxScrollY = Math.Max(0, y + _stylesScrollY - bounds.Bottom);
    }

    private void DrawLayoutContent(SKCanvas canvas, SKRect bounds, DomNodeDto selectedNode, float y)
    {
        // Box Model visualization with real data
        float centerX = bounds.Left + bounds.Width / 2;
        float centerY = y + 100;
        
        using var marginPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BoxMargin);
        using var borderPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BoxBorder);
        using var paddingPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BoxPadding);
        using var contentPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BoxContent);
        using var textPaint = DevToolsTheme.CreateTextPaint(new SKColor(30, 30, 30), 10);
        
        string GetStyle(string name) 
        {
            if (_computedStyleData?.ComputedStyle == null) return "-";
            return _computedStyleData.ComputedStyle.FirstOrDefault(s => s.Name == name)?.Value ?? "-";
        }
        
        string marginTop = GetStyle("margin-top");
        string marginRight = GetStyle("margin-right");
        string marginBottom = GetStyle("margin-bottom");
        string marginLeft = GetStyle("margin-left");
        
        string borderTop = GetStyle("border-top-width");
        string borderRight = GetStyle("border-right-width");
        string borderBottom = GetStyle("border-bottom-width");
        string borderLeft = GetStyle("border-left-width");
        
        string paddingTop = GetStyle("padding-top");
        string paddingRight = GetStyle("padding-right");
        string paddingBottom = GetStyle("padding-bottom");
        string paddingLeft = GetStyle("padding-left");
        
        string width = GetStyle("width");
        string height = GetStyle("height");
        
        float mw = 260, mh = 140;
        float bw = 200, bh = 100;
        float pw = 140, ph = 60;
        float cw = 80, ch = 30;
        
        void DrawBox(float w, float h, SKPaint paint, string label, string t, string r, string b, string l)
        {
            var rect = SKRect.Create(centerX - w/2, centerY - h/2, w, h);
            canvas.DrawRect(rect, paint);
            
            // Draw Label
            canvas.DrawText(label, rect.Left + 4, rect.Top + 12, textPaint);
            
            // Draw Values
            float midX = rect.MidX;
            float midY = rect.MidY;
            
            // Top
            float tw = textPaint.MeasureText(t);
            canvas.DrawText(t, midX - tw/2, rect.Top + 12, textPaint);
            
            // Bottom
            float bw = textPaint.MeasureText(b);
            canvas.DrawText(b, midX - bw/2, rect.Bottom - 4, textPaint);
            
            // Left
            float lw = textPaint.MeasureText(l);
            canvas.DrawText(l, rect.Left + 4, midY + 4, textPaint);
            
            // Right (Label might overlap, simplified)
            float rw = textPaint.MeasureText(r);
            canvas.DrawText(r, rect.Right - rw - 4, midY + 4, textPaint);
        }
        
        DrawBox(mw, mh, marginPaint, "margin", marginTop, marginRight, marginBottom, marginLeft);
        DrawBox(bw, bh, borderPaint, "border", borderTop, borderRight, borderBottom, borderLeft);
        DrawBox(pw, ph, paddingPaint, "padding", paddingTop, paddingRight, paddingBottom, paddingLeft);
        
        // Content
        var contentRect = SKRect.Create(centerX - cw/2, centerY - ch/2, cw, ch);
        canvas.DrawRect(contentRect, contentPaint);
        string contentDim = $"{width} x {height}";
        float cdw = textPaint.MeasureText(contentDim);
        canvas.DrawText(contentDim, centerX - cdw/2, centerY + 5, textPaint);
        
        using var hintPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
        canvas.DrawText("Computed Layout Properties", bounds.Left + DevToolsTheme.PaddingNormal, y + 200, hintPaint);
        
        _stylesMaxScrollY = Math.Max(0, (y + 200 + DevToolsTheme.ItemHeight) + _stylesScrollY - bounds.Bottom);
    }
    
    public override void OnMouseMove(float x, float y)
    {
        // Check splitter hover
        if (x >= _splitterX - 2 && x <= _splitterX + SPLITTER_WIDTH + 2)
        {
            // Could set cursor to resize
        }
        
        // Check tree hover
        if (x < _splitterX)
        {
            int index = GetNodeIndexAt(y);
            if (index != _hoveredIndex)
            {
                _hoveredIndex = index;
                
                // Highlight element on page (Hovered node OR Selected node if not hovering)
                if (index >= 0 && index < _flattenedNodes.Count)
                {
                    _hoveredNodeId = _flattenedNodes[index].Node.NodeId;
                    _ = SendHighlightCommand(_hoveredNodeId);
                }
                else
                {
                    _hoveredNodeId = null;
                    _ = SendHighlightCommand(_selectedNodeId); // Fallback to selected node
                }
                
                Invalidate();
            }
        }
        else
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                _hoveredNodeId = null;
                _ = SendHighlightCommand(_selectedNodeId); // Fallback to selected node
                Invalidate();
            }
        }
        
        // Handle splitter drag
        if (_draggingSplitter)
        {
            _splitterX = Math.Clamp(x, Bounds.Left + MIN_PANEL_WIDTH, Bounds.Right - MIN_PANEL_WIDTH);
            Invalidate();
        }
    }
    
    public override bool OnMouseDown(float x, float y, bool isRightButton)
    {
        // Check splitter
        if (x >= _splitterX - 2 && x <= _splitterX + SPLITTER_WIDTH + 2)
        {
            _draggingSplitter = true;
            return true;
        }
        
        // Check tree click
        if (x < _splitterX)
        {
            int index = GetNodeIndexAt(y);
            
            if (index >= 0 && index < _flattenedNodes.Count)
            {
                var node = _flattenedNodes[index];
                
                // Check if clicked on arrow - larger hit area (30px)
                float baseNodeX = Bounds.Left + 20 + node.Depth * 16f;
                float arrowCenterX = baseNodeX - 12;
                if (Math.Abs(x - arrowCenterX) <= 15 && node.HasChildren)
                {
                    // Toggle expand
                    if (node.IsExpanded)
                        _expandedNodes.Remove(node.Node.NodeId);
                    else
                    {
                        _expandedNodes.Add(node.Node.NodeId);
                        // Trigger lazy load if children aren't present
                        if (node.Node.Children == null && node.Node.ChildNodeCount > 0)
                        {
                            _ = RequestChildNodesAsync(node.Node.NodeId);
                        }
                    }
                    
                    // Also select the node to keep highlight persistent
                    if (_selectedNodeId != node.Node.NodeId)
                    {
                        _selectedNodeId = node.Node.NodeId;
                        _stylesScrollY = 0;
                        _computedStyleData = null;
                        _matchedStyleData = null;
                        _ = FetchStylesAsync(node.Node.NodeId);
                    }
                    
                    RefreshFlattenedTree();
                }
                else
                {
                     // Select node
                    _selectedNodeId = node.Node.NodeId;
                    _stylesScrollY = 0; // Reset styles scroll
                    _computedStyleData = null;
                    _matchedStyleData = null;
                    _ = FetchStylesAsync(node.Node.NodeId);
                    
                    // Click on text node enters edit mode
                    if (node.Node.NodeType == 3)
                    {
                        _editingNodeId = node.Node.NodeId;
                        _editingNodeValue = node.Node.NodeValue ?? "";
                        _cursorBlink = true;
                        _lastBlink = DateTime.Now;
                    }
                    else
                    {
                        _editingNodeId = null;
                    }
                }
                
                Invalidate();
                return true;
            }
        }
        
        // Check styles panel click
        if (x > _splitterX)
        {
            // Check tab clicks
            if (y >= Bounds.Top && y <= Bounds.Top + 32)
            {
                float relativeX = x - _splitterX - SPLITTER_WIDTH;
                float panelWidth = Bounds.Right - _splitterX - SPLITTER_WIDTH;
                int tabIdx = (int)(relativeX / (panelWidth / 3));
                if (tabIdx >= 0 && tabIdx < 3 && tabIdx != _sidebarTab)
                {
                    _sidebarTab = tabIdx;
                    _stylesScrollY = 0;
                    Invalidate();
                    return true;
                }
            }

            if (_selectedNodeId != null && _nodeMap.TryGetValue(_selectedNodeId.Value, out var selectedNode))
            {
                float contentTop = Bounds.Top + 32 + DevToolsTheme.PaddingNormal;
                float relativeY = y - contentTop + _stylesScrollY;
                
                // Only for Styles tab (0)
                if (_sidebarTab == 0 && relativeY >= DevToolsTheme.ItemHeight)
                {
                    int attrIndex = (int)((relativeY - DevToolsTheme.ItemHeight) / DevToolsTheme.ItemHeight);
                    if (selectedNode.Attributes != null && attrIndex >= 0 && attrIndex < selectedNode.Attributes.Count)
                    {
                        var kv = selectedNode.Attributes.ElementAt(attrIndex);
                        
                        _editingAttrKey = kv.Key;
                        _editingAttrValue = kv.Value;
                        _editingNodeId = null; // Clear node editing
                        _cursorBlink = true;
                        _lastBlink = DateTime.Now;
                        
                        Invalidate();
                        return true;
                    }
                }
            }
        }
        
        // Clear edit state if clicked elsewhere
        if (_editingAttrKey != null || _editingNodeId != null)
        {
            _editingAttrKey = null;
            _editingNodeId = null;
            Invalidate();
        }

        return false;
    }
    
    public override void OnTextInput(char c)
    {
        if (_editingAttrKey != null)
        {
            if (!char.IsControl(c))
            {
                _editingAttrValue += c;
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                Invalidate();
            }
        }
        else if (_editingNodeId != null)
        {
            if (!char.IsControl(c))
            {
                _editingNodeValue += c;
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                Invalidate();
            }
        }
    }
    
    public override bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt)
    {
        if (_editingAttrKey == null && _editingNodeId == null) return false;
        
        const int KEY_BACK = 8;
        const int KEY_ENTER = 13;
        const int KEY_ESC = 27;
        
        if (keyCode == KEY_BACK)
        {
            if (_editingAttrKey != null)
            {
                if (_editingAttrValue.Length > 0)
                {
                    _editingAttrValue = _editingAttrValue.Substring(0, _editingAttrValue.Length - 1);
                    _cursorBlink = true;
                    _lastBlink = DateTime.Now;
                    Invalidate();
                }
            }
            else if (_editingNodeId != null)
            {
                if (_editingNodeValue.Length > 0)
                {
                    _editingNodeValue = _editingNodeValue.Substring(0, _editingNodeValue.Length - 1);
                    _cursorBlink = true;
                    _lastBlink = DateTime.Now;
                    Invalidate();
                }
            }
            return true;
        }
        else if (keyCode == KEY_ENTER)
        {
            if (_editingAttrKey != null) _ = ApplyAttributeChange();
            else if (_editingNodeId != null) _ = ApplyNodeValueChange();
            return true;
        }
        else if (keyCode == KEY_ESC)
        {
            _editingAttrKey = null;
            _editingNodeId = null;
            Invalidate();
            return true;
        }
        
        return false;
    }
    
    private async Task ApplyAttributeChange()
    {
        if (_editingAttrKey == null || _selectedNodeId == null || Host == null) return;
        
        string key = _editingAttrKey;
        string value = _editingAttrValue;
        int nodeId = _selectedNodeId.Value;
        
        // Clear editing state immediately for UX
        _editingAttrKey = null;
        Invalidate();
        
        try
        {
            string method = string.IsNullOrWhiteSpace(value) ? "DOM.removeAttribute" : "DOM.setAttributeValue";
            object parameters = string.IsNullOrWhiteSpace(value) 
                ? new { nodeId = nodeId, name = key } 
                : new { nodeId = nodeId, name = key, value = value };

            var request = new ProtocolRequest<object>
            {
                Id = 1003,
                Method = method,
                Params = parameters
            };
            
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
        }
        catch (Exception ex)
        {
            FenBrowser.Core.FenLogger.Error($"[ElementsPanel] Attribute change error: {ex.Message}", LogCategory.General);
        }
    }

    private async Task ApplyNodeValueChange()
    {
        if (_editingNodeId == null || Host == null) return;
        
        int nodeId = _editingNodeId.Value;
        string value = _editingNodeValue;
        
        _editingNodeId = null;
        Invalidate();
        
        try
        {
            var request = new ProtocolRequest<object>
            {
                Id = 1004,
                Method = "DOM.setNodeValue",
                Params = new { nodeId = nodeId, value = value }
            };
            
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
        }
        catch (Exception ex)
        {
            FenBrowser.Core.FenLogger.Error($"[ElementsPanel] Node value change error: {ex.Message}", LogCategory.General);
        }
    }
    
    private async Task SendHighlightCommand(int? nodeId)
    {
        if (Host == null) return;
        
        if (nodeId == null)
        {
            var req = new ProtocolRequest<object> { Id = 2001, Method = "DOM.hideHighlight", Params = new { } };
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(req, ProtocolJson.Options));
        }
        else
        {
            var req = new ProtocolRequest<HighlightNodeParams> 
            { 
                Id = 2002, 
                Method = "DOM.highlightNode", 
                Params = new HighlightNodeParams 
                { 
                    NodeId = nodeId.Value,
                    HighlightConfig = new HighlightConfig
                    {
                        ContentColor = new RgbaColor(111, 168, 220, 168) // 0.66f is approx 168 in 0-255
                    }
                } 
            };
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(req, ProtocolJson.Options));
        }
    }
    
    public override void OnMouseUp(float x, float y)
    {
        _draggingSplitter = false;
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        if (x < _splitterX)
        {
            // Tree scroll
            ScrollY = Math.Clamp(ScrollY - deltaY * 40, 0, MaxScrollY);
        }
        else
        {
            // Styles scroll
            _stylesScrollY = Math.Clamp(_stylesScrollY - deltaY * 40, 0, _stylesMaxScrollY);
        }
        
        Invalidate();
    }
    
    private int GetNodeIndexAt(float y)
    {
        float relativeY = (y - Bounds.Top) - DevToolsTheme.PaddingNormal + ScrollY;
        int index = (int)(relativeY / DevToolsTheme.ItemHeight);
        return index >= 0 && index < _flattenedNodes.Count ? index : -1;
    }
    
    /// <summary>
    /// Represents a flattened DOM tree node.
    /// </summary>
    private record DomTreeNode(DomNodeDto Node, int Depth, bool HasChildren, bool IsExpanded, bool IsClosingTag = false);
}
