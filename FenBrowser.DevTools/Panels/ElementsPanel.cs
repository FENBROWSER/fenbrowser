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
    
    private float _treeScrollY;
    private float _treeMaxScrollY;
    private float _stylesScrollY; // Renamed from _stylesScrollY to follow pattern if needed, but it was already there
    private float _stylesMaxScrollY;
    
    public override bool IsDragging => _draggingSplitter;
    
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
    
    // CSS Property Live Editing
    private int? _editingCssNodeId = null;
    private string? _editingCssPropertyName = null;
    private string _editingCssPropertyValue = "";
    
    // Autocomplete state
    private List<string> _autocompleteSuggestions = new();
    private int _autocompleteSelectedIndex = 0;
    
    // Color picker state
    private bool _colorPickerVisible = false;
    
    // Property toggle state (disabled properties)
    private HashSet<string> _disabledProperties = new();
    
    // --- 10/10: Search state ---
    private string _searchQuery = "";
    private List<int> _searchResults = new();
    private int _searchCurrentIndex = 0;
    private bool _searchBoxFocused = false;
    
    // --- 10/10: Breadcrumb trail ---
    private List<int> _breadcrumbPath = new();
    
    // --- 10/10: Copy selector support ---
    private string? _lastCopiedSelector = null;
    
    // --- Double-click editing support ---
    private DateTime _lastClickTime = DateTime.MinValue;
    private int? _lastClickedNodeId = null;
    private bool _editingElementAsHtml = false;
    private int? _editingElementNodeId = null;
    private string _editingElementHtml = "";

    protected override void OnHostChanging(IDevToolsHost? previousHost)
    {
        if (previousHost != null)
        {
            previousHost.DomChanged -= HandleDomChanged;
            previousHost.ProtocolEventReceived -= HandleProtocolEvent;
        }

        _rootNode = null;
        _nodeMap.Clear();
        _flattenedNodes.Clear();
        _expandedNodes.Clear();
        _selectedNodeId = null;
        _hoveredNodeId = null;
        _hoveredIndex = -1;
        _treeScrollY = 0;
        _treeMaxScrollY = 0;
        _stylesScrollY = 0;
        _stylesMaxScrollY = 0;
        _editingAttrKey = null;
        _editingNodeId = null;
        _editingCssPropertyName = null;
        _editingElementAsHtml = false;
        _editingElementNodeId = null;
    }
    
    protected override void OnHostChanged()
    {
        if (Host != null)
        {
            Host.DomChanged += HandleDomChanged;
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
        _editingElementAsHtml = false;
        _editingElementNodeId = null;
        _ = SendHighlightCommand(null);
        Invalidate();
    }

    private void HandleDomChanged()
    {
        _ = RefreshTreeAsync();
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
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] Protocol error: {ex.Message}", LogCategory.General);
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
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] RequestChildNodes error: {ex.Message}", LogCategory.General);
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
        
        // Skip whitespace-only text nodes (matches Chrome/Edge behavior)
        if (node.NodeType == 3) // Text node
        {
            if (string.IsNullOrWhiteSpace(node.NodeValue))
                return; // Skip whitespace-only text nodes
        }

        bool hasChildren = node.ChildNodeCount > 0;
        bool isExpanded = _expandedNodes.Contains(node.NodeId);
        
        // Add opening tag / text node
        _flattenedNodes.Add(new DomTreeNode(node, depth, hasChildren, isExpanded));
        
        if (node.NodeType == 3 && node.NodeValue == "g")
        {
            FenBrowser.Core.EngineLogCompat.Debug($"[ElementsPanel] Spotted 'g' node! Parent: {node.ParentId}, Id: {node.NodeId}");
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
    
    public override void Paint(SKCanvas canvas, SKRect bounds)
    {
        Bounds = bounds;
        UpdateCursorBlink();

        // Initialize or re-sync splitter position if it drifted or window resized
        if (_splitterX <= bounds.Left || _splitterX >= bounds.Right)
        {
            _splitterX = bounds.Left + (bounds.Width * 0.6f);
        }

        _treeWidth = _splitterX - bounds.Left;
        _stylesWidth = bounds.Right - _splitterX - SPLITTER_WIDTH;

        // Clip and draw DOM tree
        var treeBounds = new SKRect(bounds.Left, bounds.Top, _splitterX, bounds.Bottom);
        canvas.Save();
        canvas.ClipRect(treeBounds);
        canvas.Translate(0, -_treeScrollY);
        DrawDomTree(canvas, treeBounds);
        canvas.Restore();

        // Draw splitter (fixed)
        DrawSplitter(canvas, bounds);

        // Draw sidebar tabs FIRST (fixed, not scrolled)
        var stylesBounds = new SKRect(_splitterX + SPLITTER_WIDTH, bounds.Top, bounds.Right, bounds.Bottom);
        DrawSidebarTabs(canvas, stylesBounds);
        
        // Clip and draw styles panel CONTENT (scrollable area below tabs)
        var stylesContentBounds = new SKRect(_splitterX + SPLITTER_WIDTH, bounds.Top + 32, bounds.Right, bounds.Bottom);
        canvas.Save();
        canvas.ClipRect(stylesContentBounds);
        canvas.Translate(0, -_stylesScrollY);
        DrawStylesPanelContent(canvas, stylesContentBounds);
        canvas.Restore();

        // Draw scrollbars
        if (_treeMaxScrollY > 0)
        {
             DrawCustomScrollbar(canvas, treeBounds, _treeScrollY, _treeMaxScrollY);
        }
        if (_stylesMaxScrollY > 0)
        {
             DrawCustomScrollbar(canvas, stylesContentBounds, _stylesScrollY, _stylesMaxScrollY);
        }

        if (_editingElementAsHtml)
        {
            DrawHtmlEditorOverlay(canvas, bounds);
        }
    }

    protected override void OnPaint(SKCanvas canvas, SKRect bounds)
    {
        // Not used anymore as we override Paint directly to manage multiple scroll areas
    }

    private void DrawCustomScrollbar(SKCanvas canvas, SKRect bounds, float currentScroll, float maxScroll)
    {
        float scrollbarWidth = 6f;
        float trackHeight = bounds.Height;
        float thumbHeight = Math.Max(20, trackHeight * (trackHeight / (trackHeight + maxScroll)));
        float thumbY = bounds.Top + (currentScroll / maxScroll) * (trackHeight - thumbHeight);
        
        var thumbRect = new SKRect(
            bounds.Right - scrollbarWidth - 2,
            thumbY,
            bounds.Right - 2,
            thumbY + thumbHeight
        );
        
        using var paint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Scrollbar);
        canvas.DrawRoundRect(thumbRect, 3, 3, paint);
    }
    
    
    private void DrawDomTree(SKCanvas canvas, SKRect bounds)
    {
        float y = bounds.Top + DevToolsTheme.PaddingNormal;
        
        // Create paints
        using var tagPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxTag);
        using var attrNamePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxAttribute);
        using var attrValuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxString);
        using var punctPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
        using var guidePaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TreeGuide, 1f);
        
        // Constants for layout
        const float leftMargin = 24f;  // Space for arrows
        float indentWidth = DevToolsTheme.IndentWidth;
        
        for (int i = 0; i < _flattenedNodes.Count; i++)
        {
            var node = _flattenedNodes[i];
            // Note: Canvas is already translated by -_treeScrollY, so we don't subtract it here
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            // Since canvas is translated, itemY appears at (itemY - scrollY) on screen
            float screenY = itemY - _treeScrollY;
            if (screenY + DevToolsTheme.ItemHeight < bounds.Top) continue;
            if (screenY > bounds.Bottom) break;
            
            // Calculate x position with consistent indentation
            float baseX = bounds.Left + leftMargin;
            float nodeX = baseX + node.Depth * indentWidth;
            
            // Draw tree guide lines (vertical + horizontal connector)
            for (int d = 0; d < node.Depth; d++)
            {
                float guideX = baseX + d * indentWidth + indentWidth / 2;
                
                // Check if we need to draw a vertical line at this depth
                // (we need it if there are siblings after this node at this depth)
                bool hasSiblingBelow = HasSiblingBelowAtDepth(i, d);
                
                if (hasSiblingBelow || d == node.Depth - 1)
                {
                    float lineTop = itemY;
                    float lineBottom = itemY + DevToolsTheme.ItemHeight;
                    
                    // For the immediate parent depth, only draw to middle then across
                    if (d == node.Depth - 1)
                    {
                        lineBottom = itemY + DevToolsTheme.ItemHeight / 2;
                        // Draw horizontal connector
                        float connectorEnd = nodeX - 4;
                        canvas.DrawLine(guideX, lineBottom, connectorEnd, lineBottom, guidePaint);
                    }
                    
                    canvas.DrawLine(guideX, lineTop, guideX, lineBottom, guidePaint);
                }
            }
            
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
            float x = nodeX;

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

            // Draw expand/collapse arrow (ONLY for elements with children)
            if (node.HasChildren && node.Node.NodeType == 1)
            {
                using var arrowPaint = DevToolsTheme.CreateFillPaint(
                    node.Node.NodeId == _selectedNodeId ? DevToolsTheme.TextPrimary : DevToolsTheme.TextSecondary);
                using var arrowPath = new SKPath();
                
                float arrowSize = DevToolsTheme.TreeArrowSize;
                float centerX = nodeX - 10;
                float centerY = itemY + DevToolsTheme.ItemHeight / 2;
                
                if (node.IsExpanded)
                {
                    // Down triangle (expanded)
                    arrowPath.MoveTo(centerX - arrowSize/2, centerY - arrowSize/3);
                    arrowPath.LineTo(centerX + arrowSize/2, centerY - arrowSize/3);
                    arrowPath.LineTo(centerX, centerY + arrowSize/2);
                }
                else
                {
                    // Right triangle (collapsed)
                    arrowPath.MoveTo(centerX - arrowSize/3, centerY - arrowSize/2);
                    arrowPath.LineTo(centerX - arrowSize/3, centerY + arrowSize/2);
                    arrowPath.LineTo(centerX + arrowSize/2, centerY);
                }
                arrowPath.Close();
                canvas.DrawPath(arrowPath, arrowPaint);
            }
            
            // Render based on node type
            if (node.Node.NodeType == 3) // Text node
            {
                if (_editingNodeId == node.Node.NodeId)
                {
                    // Draw edit background
                    float textWidth = attrValuePaint.MeasureText(_editingNodeValue);
                    var editRect = new SKRect(x - 2, itemY + 4, x + textWidth + 10, itemY + DevToolsTheme.ItemHeight - 4);
                    using var editBg = DevToolsTheme.CreateFillPaint(new SKColor(40, 44, 52));
                    using var editBorder = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TabBorder);
                    canvas.DrawRoundRect(editRect, 2, 2, editBg);
                    canvas.DrawRoundRect(editRect, 2, 2, editBorder);
                    
                    canvas.DrawText(_editingNodeValue, x, textY, attrValuePaint);
                    
                    // Draw cursor
                    if (_cursorBlink)
                    {
                        using var cursorPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TextPrimary);
                        canvas.DrawLine(x + textWidth + 1, textY - 10, x + textWidth + 1, textY + 3, cursorPaint);
                    }
                }
                else
                {
                    string displayText = node.Node.NodeValue ?? "";
                    if (displayText.Length > 60) displayText = displayText.Substring(0, 57) + "...";
                    canvas.DrawText("\"" + displayText + "\"", x, textY, attrValuePaint);
                }
            }
            else // Element node
            {
                // Opening bracket
                canvas.DrawText("<", x, textY, punctPaint);
                x += punctPaint.MeasureText("<");
                
                // Tag name
                string tagName = node.Node.NodeName.ToLower();
                canvas.DrawText(tagName, x, textY, tagPaint);
                x += tagPaint.MeasureText(tagName);
                
                // Attributes (limit to avoid overflow)
                if (node.Node.Attributes != null)
                {
                    int attrCount = 0;
                    foreach (var attr in node.Node.Attributes)
                    {
                        if (attrCount >= 3 && node.Node.Attributes.Count > 4)
                        {
                            canvas.DrawText($" ...+{node.Node.Attributes.Count - 3} more", x, textY, punctPaint);
                            break;
                        }
                        
                        string attrName = " " + attr.Key;
                        canvas.DrawText(attrName, x, textY, attrNamePaint);
                        x += attrNamePaint.MeasureText(attrName);
                        
                        canvas.DrawText("=\"", x, textY, punctPaint);
                        x += punctPaint.MeasureText("=\"");
                        
                        string attrValue = attr.Value;
                        if (attrValue.Length > 25) attrValue = attrValue.Substring(0, 22) + "...";
                        canvas.DrawText(attrValue, x, textY, attrValuePaint);
                        x += attrValuePaint.MeasureText(attrValue);
                        
                        canvas.DrawText("\"", x, textY, punctPaint);
                        x += punctPaint.MeasureText("\"");
                        
                        attrCount++;
                    }
                }
                
                // Closing bracket
                if (!node.HasChildren || !node.IsExpanded)
                {
                    if (node.HasChildren)
                        canvas.DrawText(">…</" + tagName + ">", x, textY, punctPaint);
                    else
                        canvas.DrawText(" />", x, textY, punctPaint);
                }
                else
                {
                    canvas.DrawText(">", x, textY, punctPaint);
                }
            }
        }

        _treeMaxScrollY = Math.Max(0, (y + _flattenedNodes.Count * DevToolsTheme.ItemHeight) - bounds.Bottom);
    }
    
    /// <summary>
    /// Check if there's a sibling node below at the specified depth.
    /// Used for drawing tree guide lines.
    /// </summary>
    private bool HasSiblingBelowAtDepth(int currentIndex, int depth)
    {
        for (int i = currentIndex + 1; i < _flattenedNodes.Count; i++)
        {
            var node = _flattenedNodes[i];
            if (node.Depth < depth) return false;  // Gone past the subtree
            if (node.Depth == depth && !node.IsClosingTag) return true;  // Found sibling
        }
        return false;
    }
    
    private void DrawSplitter(SKCanvas canvas, SKRect bounds)
    {
        using var splitterPaint = DevToolsTheme.CreateFillPaint(_draggingSplitter ? DevToolsTheme.TabBorder : DevToolsTheme.Border);
        canvas.DrawRect(new SKRect(_splitterX, bounds.Top, _splitterX + SPLITTER_WIDTH, bounds.Bottom), splitterPaint);
    }
    
    private void DrawSidebarTabs(SKCanvas canvas, SKRect bounds)
    {
        // Tabs (fixed at top, not scrolled)
        float tabWidth = bounds.Width / 3;
        string[] tabs = { "Styles", "Computed", "Layout" };
        
        using var tabBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var tabBorderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var activeTabPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.TabActive);
        using var tabTextPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeNormal);
        using var inactiveTabTextPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeNormal);
        
        // Draw tab bar background
        canvas.DrawRect(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + 32), tabBgPaint);
        
        for (int i = 0; i < tabs.Length; i++)
        {
            var tabRect = new SKRect(bounds.Left + i * tabWidth, bounds.Top, bounds.Left + (i + 1) * tabWidth, bounds.Top + 32);
            if (i == _sidebarTab)
            {
                canvas.DrawRect(tabRect, activeTabPaint);
            }
            
            float textWidth = tabTextPaint.MeasureText(tabs[i]);
            canvas.DrawText(tabs[i], tabRect.Left + (tabWidth - textWidth) / 2, tabRect.Top + 20, i == _sidebarTab ? tabTextPaint : inactiveTabTextPaint);
            canvas.DrawLine(tabRect.Right, tabRect.Top + 4, tabRect.Right, tabRect.Bottom - 4, tabBorderPaint);
        }
        
        canvas.DrawLine(bounds.Left, bounds.Top + 32, bounds.Right, bounds.Top + 32, tabBorderPaint);
    }

    private void DrawStylesPanelContent(SKCanvas canvas, SKRect bounds)
    {
        string[] tabs = { "Styles", "Computed", "Layout" };
        float contentTop = bounds.Top + DevToolsTheme.PaddingNormal;
        
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
            EngineLogCompat.Error($"[ElementsPanel] FetchStylesAsync error: {ex.Message}", LogCategory.General);
        }
    }

    private void DrawStylesContent(SKCanvas canvas, SKRect bounds, DomNodeDto selectedNode, float y)
    {
        y -= _stylesScrollY;
        using var propPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxProperty);
        using var valuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxValue);
        using var punctPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
        using var mutedPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
        using var selectorPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxTag);
        
        // 1. Element Inline Style
        float ex = bounds.Left + DevToolsTheme.PaddingNormal;
        canvas.DrawText("element.style", ex, y, selectorPaint);
        ex += selectorPaint.MeasureText("element.style");
        canvas.DrawText(" {", ex, y, mutedPaint);
        y += DevToolsTheme.ItemHeight;
        
        if (_matchedStyleData?.InlineStyle?.CssProperties != null)
        {
            foreach (var prop in _matchedStyleData.InlineStyle.CssProperties)
            {
                if (y > bounds.Bottom) break;
                if (y < bounds.Top + 32) { y += DevToolsTheme.ItemHeight; continue; }
                
                bool isDisabled = _disabledProperties.Contains(prop.Name);
                float x = bounds.Left + DevToolsTheme.PaddingNormal;
                
                // Draw checkbox
                DrawPropertyCheckbox(canvas, x, y - 12, !isDisabled);
                x += 20;
                
                // Draw property with strikethrough if disabled
                var textPaint = isDisabled ? mutedPaint : propPaint;
                var valPaint = isDisabled ? mutedPaint : valuePaint;
                
                canvas.DrawText(prop.Name, x, y, textPaint);
                x += Math.Max(120, textPaint.MeasureText(prop.Name) + 10);
                canvas.DrawText(": ", x, y, punctPaint);
                x += punctPaint.MeasureText(": ");
                canvas.DrawText(prop.Value + ";", x, y, valPaint);
                
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
                string? selector = rule.Rule?.SelectorList?.Text;
                if (string.IsNullOrEmpty(selector) && rule.Rule?.SelectorList?.Selectors != null)
                {
                    selector = string.Join(", ", rule.Rule.SelectorList.Selectors.Select(s => s.Text));
                }
                if (string.IsNullOrEmpty(selector)) selector = "selector";
                
                float sx = bounds.Left + DevToolsTheme.PaddingNormal;
                canvas.DrawText(selector, sx, y, selectorPaint);
                sx += selectorPaint.MeasureText(selector);
                canvas.DrawText(" {", sx, y, mutedPaint);
                
                // Draw origin (Edge parity)
                string origin = rule.Rule?.Origin ?? "regular";
                if (origin == "user-agent") origin = "user agent stylesheet";
                float originWidth = mutedPaint.MeasureText(origin);
                canvas.DrawText(origin, bounds.Right - originWidth - DevToolsTheme.PaddingNormal, y, mutedPaint);

                y += DevToolsTheme.ItemHeight;
                
                if (rule.Rule?.Style?.CssProperties != null)
                {
                    foreach (var prop in rule.Rule.Style.CssProperties)
                    {
                        if (y > bounds.Bottom) break;
                        float x = bounds.Left + DevToolsTheme.PaddingNormal * 2;
                        
                        // Check if this property is being edited
                        bool isEditing = _editingCssPropertyName == prop.Name && _editingCssNodeId == _selectedNodeId;
                        
                        canvas.DrawText(prop.Name, x, y, propPaint);
                        x += Math.Max(120, propPaint.MeasureText(prop.Name) + 10);
                        canvas.DrawText(": ", x, y, punctPaint);
                        x += punctPaint.MeasureText(": ");
                        
                        if (isEditing)
                        {
                            // Draw editable text box
                            float editBoxWidth = Math.Max(100, valuePaint.MeasureText(_editingCssPropertyValue) + 16);
                            var editRect = new SKRect(x - 4, y - 14, x + editBoxWidth, y + 4);
                            using var editBgPaint = DevToolsTheme.CreateFillPaint(new SKColor(60, 60, 90));
                            using var editBorderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.SyntaxTag);
                            canvas.DrawRoundRect(editRect, 2, 2, editBgPaint);
                            canvas.DrawRoundRect(editRect, 2, 2, editBorderPaint);
                            
                            // Draw the editing value
                            canvas.DrawText(_editingCssPropertyValue, x, y, valuePaint);
                            
                            // Draw cursor if blinking
                            if (_cursorBlink)
                            {
                                float cursorX = x + valuePaint.MeasureText(_editingCssPropertyValue);
                                using var cursorPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TextPrimary);
                                canvas.DrawLine(cursorX, y - 12, cursorX, y + 2, cursorPaint);
                            }
                        }
                        else
                        {
                            canvas.DrawText(prop.Value + ";", x, y, valuePaint);
                        }
                        
                        y += DevToolsTheme.ItemHeight;
                    }
                }
                
                canvas.DrawText("}", bounds.Left + DevToolsTheme.PaddingNormal, y, mutedPaint);
                y += DevToolsTheme.ItemHeight * 1.5f;
            }
        }
        
        // Draw autocomplete dropdown if editing and have suggestions
        if (_editingCssPropertyName != null && _autocompleteSuggestions.Count > 0)
        {
            DrawAutocompleteDropdown(canvas, bounds);
        }
        
        // Draw color picker for color properties
        if (_editingCssPropertyName != null && _colorPickerVisible && ColorPicker.IsColorProperty(_editingCssPropertyName))
        {
            DrawColorPicker(canvas, bounds);
        }
        
        // Draw Box Model diagram at the bottom of Styles tab (Edge parity)
        y += DevToolsTheme.PaddingNormal;
        DrawBoxModelDiagram(canvas, bounds, y);
        y += 180; // Height of box model diagram
        
        _stylesMaxScrollY = Math.Max(0, y + _stylesScrollY - bounds.Bottom);
    }
    
    private void DrawAutocompleteDropdown(SKCanvas canvas, SKRect bounds)
    {
        if (_autocompleteSuggestions.Count == 0) return;
        
        float dropdownX = bounds.Left + DevToolsTheme.PaddingNormal * 3;
        float dropdownY = bounds.Top + 60; // Approximate position below edit line
        float dropdownWidth = 180;
        int maxVisible = Math.Min(8, _autocompleteSuggestions.Count);
        float dropdownHeight = maxVisible * DevToolsTheme.ItemHeight;
        
        // Background
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var textPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeSmall);
        using var selectedBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundSelected);
        
        var dropdownRect = new SKRect(dropdownX, dropdownY, dropdownX + dropdownWidth, dropdownY + dropdownHeight);
        canvas.DrawRect(dropdownRect, bgPaint);
        canvas.DrawRect(dropdownRect, borderPaint);
        
        // Suggestions
        float itemY = dropdownY;
        for (int i = 0; i < maxVisible; i++)
        {
            if (i >= _autocompleteSuggestions.Count) break;
            
            var itemRect = new SKRect(dropdownX, itemY, dropdownX + dropdownWidth, itemY + DevToolsTheme.ItemHeight);
            if (i == _autocompleteSelectedIndex)
            {
                canvas.DrawRect(itemRect, selectedBgPaint);
            }
            
            canvas.DrawText(_autocompleteSuggestions[i], dropdownX + 8, itemY + 15, textPaint);
            itemY += DevToolsTheme.ItemHeight;
        }
    }
    
    private void DrawColorPicker(SKCanvas canvas, SKRect bounds)
    {
        float pickerX = bounds.Left + DevToolsTheme.PaddingNormal * 3;
        float pickerY = bounds.Top + 80; // Below autocomplete if present
        
        // Background
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        
        float width = ColorPicker.GetWidth();
        float height = ColorPicker.GetHeight() + 24; // Extra for current color preview
        
        var pickerRect = new SKRect(pickerX, pickerY, pickerX + width, pickerY + height);
        canvas.DrawRect(pickerRect, bgPaint);
        canvas.DrawRect(pickerRect, borderPaint);
        
        // Current color preview
        if (ColorPicker.TryParse(_editingCssPropertyValue, out var currentColor))
        {
            using var previewPaint = new SKPaint { Color = currentColor, IsAntialias = true };
            var previewRect = new SKRect(pickerX + ColorPicker.Padding, pickerY + ColorPicker.Padding, 
                                          pickerX + width - ColorPicker.Padding, pickerY + 20);
            canvas.DrawRect(previewRect, previewPaint);
        }
        
        // Color palette
        float swatchY = pickerY + 24;
        for (int row = 0; row < ColorPicker.Rows; row++)
        {
            float swatchX = pickerX + ColorPicker.Padding;
            for (int col = 0; col < ColorPicker.ColsPerRow; col++)
            {
                int idx = row * ColorPicker.ColsPerRow + col;
                if (idx >= ColorPicker.Palette.Length) break;
                
                using var swatchPaint = new SKPaint { Color = ColorPicker.Palette[idx], IsAntialias = true };
                var swatchRect = new SKRect(swatchX, swatchY, swatchX + ColorPicker.SwatchSize, swatchY + ColorPicker.SwatchSize);
                canvas.DrawRect(swatchRect, swatchPaint);
                
                swatchX += ColorPicker.SwatchSize + ColorPicker.Padding;
            }
            swatchY += ColorPicker.SwatchSize + ColorPicker.Padding;
        }
    }
    
    private void DrawPropertyCheckbox(SKCanvas canvas, float x, float y, bool isChecked)
    {
        float size = 12f;
        var rect = new SKRect(x, y, x + size, y + size);
        
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        
        canvas.DrawRect(rect, bgPaint);
        canvas.DrawRect(rect, borderPaint);
        
        if (isChecked)
        {
            using var checkPaint = new SKPaint
            {
                Color = DevToolsTheme.SyntaxTag,
                IsAntialias = true,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke
            };
            // Draw checkmark
            canvas.DrawLine(x + 2, y + 6, x + 5, y + 9, checkPaint);
            canvas.DrawLine(x + 5, y + 9, x + 10, y + 3, checkPaint);
        }
    }

    private void DrawBoxModelDiagram(SKCanvas canvas, SKRect bounds, float startY)
    {
        float centerX = bounds.Left + bounds.Width / 2;
        float centerY = startY + 80;
        
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
        
        string mt = GetStyle("margin-top"), mr = GetStyle("margin-right");
        string mb = GetStyle("margin-bottom"), ml = GetStyle("margin-left");
        string bt = GetStyle("border-top-width"), br = GetStyle("border-right-width");
        string bb = GetStyle("border-bottom-width"), bl = GetStyle("border-left-width");
        string pt = GetStyle("padding-top"), pr = GetStyle("padding-right");
        string pb = GetStyle("padding-bottom"), pl = GetStyle("padding-left");
        string w = GetStyle("width"), h = GetStyle("height");
        
        float mw = 220, mh = 130, bw = 170, bh = 100, pw = 120, ph = 70, cw = 70, ch = 35;
        
        void DrawBox(float boxW, float boxH, SKPaint paint, string label, string t, string r, string b, string l)
        {
            var rect = SKRect.Create(centerX - boxW/2, centerY - boxH/2, boxW, boxH);
            canvas.DrawRect(rect, paint);
            canvas.DrawText(label, rect.Left + 4, rect.Top + 12, textPaint);
            canvas.DrawText(t, rect.MidX - textPaint.MeasureText(t)/2, rect.Top + 12, textPaint);
            canvas.DrawText(b, rect.MidX - textPaint.MeasureText(b)/2, rect.Bottom - 4, textPaint);
            canvas.DrawText(l, rect.Left + 4, rect.MidY + 4, textPaint);
            canvas.DrawText(r, rect.Right - textPaint.MeasureText(r) - 4, rect.MidY + 4, textPaint);
        }
        
        DrawBox(mw, mh, marginPaint, "margin", mt, mr, mb, ml);
        DrawBox(bw, bh, borderPaint, "border", bt, br, bb, bl);
        DrawBox(pw, ph, paddingPaint, "padding", pt, pr, pb, pl);
        
        var contentRect = SKRect.Create(centerX - cw/2, centerY - ch/2, cw, ch);
        canvas.DrawRect(contentRect, contentPaint);
        string contentDim = $"{w} × {h}";
        canvas.DrawText(contentDim, centerX - textPaint.MeasureText(contentDim)/2, centerY + 4, textPaint);
    }

    private void DrawComputedContent(SKCanvas canvas, SKRect bounds, DomNodeDto selectedNode, float y)
    {
        y -= _stylesScrollY;
        using var propPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxProperty);
        using var valuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxValue);
        using var originPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted, DevToolsTheme.FontSizeSmall);

        if (_computedStyleData?.ComputedStyle != null)
        {
            foreach (var prop in _computedStyleData.ComputedStyle.OrderBy(p => p.Name))
            {
                if (y > bounds.Bottom) break;
                if (y < bounds.Top) { y += DevToolsTheme.ItemHeight; continue; }

                float x = bounds.Left + DevToolsTheme.PaddingNormal;
                canvas.DrawText(prop.Name, x, y, propPaint);
                x += Math.Max(160, propPaint.MeasureText(prop.Name) + 10);
                canvas.DrawText(prop.Value, x, y, valuePaint);
                
                // Show origin (style tracing)
                string origin = FindPropertyOrigin(prop.Name);
                if (!string.IsNullOrEmpty(origin))
                {
                    float originX = bounds.Right - originPaint.MeasureText(origin) - DevToolsTheme.PaddingNormal;
                    canvas.DrawText(origin, originX, y, originPaint);
                }
                
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
    
    /// <summary>
    /// Find which CSS rule sets a given property (for computed style tracing).
    /// </summary>
    private string FindPropertyOrigin(string propertyName)
    {
        // Check inline style first
        if (_matchedStyleData?.InlineStyle?.CssProperties != null)
        {
            if (_matchedStyleData.InlineStyle.CssProperties.Any(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
                return "element.style";
        }
        
        // Check matched rules
        if (_matchedStyleData?.MatchedCSSRules != null)
        {
            foreach (var rule in _matchedStyleData.MatchedCSSRules)
            {
                if (rule.Rule?.Style?.CssProperties?.Any(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    string? selector = rule.Rule.SelectorList?.Text;
                    if (string.IsNullOrEmpty(selector) && rule.Rule.SelectorList?.Selectors != null)
                        selector = string.Join(", ", rule.Rule.SelectorList.Selectors.Select(s => s.Text));
                    
                    string origin = rule.Rule.Origin ?? "regular";
                    if (origin == "user-agent") return "user agent";
                    return selector ?? "stylesheet";
                }
            }
        }
        
        return "inherited";
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
        // 1. Handle dragging first
        if (_draggingSplitter)
        {
             _splitterX = Math.Clamp(x, Bounds.Left + MIN_PANEL_WIDTH, Bounds.Right - MIN_PANEL_WIDTH);
             Host?.RequestCursorChange(CursorType.HorizontalResize);
             Invalidate();
             return;
        }

        // 2. Always use default cursor when not dragging
        Host?.RequestCursorChange(CursorType.Default);

        // FenBrowser.Core.EngineLogCompat.Info($"[Elements] MouseMove x={x:F1}, y={y:F1}, splitterX={_splitterX:F1}, dragging={_draggingSplitter}, bounds={Bounds}", FenBrowser.Core.Logging.LogCategory.General);

        // 3. Handle Tree hover
        if (x < _splitterX)
        {
            int index = GetNodeIndexAt(y + _treeScrollY - Bounds.Top - DevToolsTheme.PaddingNormal);
            if (index != _hoveredIndex)
            {
                _hoveredIndex = index;
                if (index >= 0 && index < _flattenedNodes.Count)
                {
                    _hoveredNodeId = _flattenedNodes[index].Node.NodeId;
                    _ = SendHighlightCommand(_hoveredNodeId);
                }
                else
                {
                    _hoveredNodeId = null;
                    _ = SendHighlightCommand(_selectedNodeId);
                }
                Invalidate();
            }
        }
        else
        {
            // Reset tree hover if moving to styles panel
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                _hoveredNodeId = null;
                _ = SendHighlightCommand(_selectedNodeId);
                Invalidate();
            }
        }
    }
    
    public override bool OnMouseDown(float x, float y, bool isRightButton)
    {
        if (_editingElementAsHtml)
        {
            return true;
        }

        // Check splitter - consistent 10px hit area
        if (Math.Abs(x - _splitterX) <= 5)
        {
            _draggingSplitter = true;
            Host?.SetCapture();  // Capture mouse to receive all events during drag
            return true;
        }
        
        // Check tree click
        if (x < _splitterX)
        {
            int index = GetNodeIndexAt(y + _treeScrollY - Bounds.Top - DevToolsTheme.PaddingNormal);
            
            if (index >= 0 && index < _flattenedNodes.Count)
            {
                var node = _flattenedNodes[index];
                
                // Calculate node X position using new constants
                float baseX = Bounds.Left + 24f;  // leftMargin from DrawDomTree
                float nodeX = baseX + node.Depth * DevToolsTheme.IndentWidth;
                float arrowCenterX = nodeX - 10;
                
                // Check if clicked on arrow (15px hit area)
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
                    // Check for double-click on element to enter edit mode
                    bool isDoubleClick = (DateTime.Now - _lastClickTime) < TimeSpan.FromMilliseconds(400)
                                       && _lastClickedNodeId == node.Node.NodeId;
                    
                    _lastClickTime = DateTime.Now;
                    _lastClickedNodeId = node.Node.NodeId;
                    
                    // Select node
                    _selectedNodeId = node.Node.NodeId;
                    _stylesScrollY = 0;
                    _computedStyleData = null;
                    _matchedStyleData = null;
                    _ = FetchStylesAsync(node.Node.NodeId);
                    
                    // Double-click on element enters "Edit as HTML" mode
                    if (isDoubleClick && node.Node.NodeType == 1 && !node.IsClosingTag)
                    {
                        _ = StartEditAsHtmlAsync(node.Node.NodeId);
                    }
                    // Single click on text node enters edit mode
                    else if (node.Node.NodeType == 3)
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
                
                // Only for Styles tab (0) - handle CSS property click-to-edit
                if (_sidebarTab == 0 && relativeY >= DevToolsTheme.ItemHeight)
                {
                    // Calculate which property row was clicked
                    int rowIndex = (int)((relativeY - DevToolsTheme.ItemHeight) / DevToolsTheme.ItemHeight);
                    
                    // Build list of all CSS properties to find clicked one
                    var allProps = new List<(string Name, string Value)>();
                    
                    // Inline styles first
                    if (_matchedStyleData?.InlineStyle?.CssProperties != null)
                    {
                        foreach (var p in _matchedStyleData.InlineStyle.CssProperties)
                            allProps.Add((p.Name, p.Value));
                    }
                    
                    // Then matched rules
                    if (_matchedStyleData?.MatchedCSSRules != null)
                    {
                        foreach (var rule in _matchedStyleData.MatchedCSSRules)
                        {
                            allProps.Add(("__selector__", "")); // Selector line
                            if (rule.Rule?.Style?.CssProperties != null)
                            {
                                foreach (var p in rule.Rule.Style.CssProperties)
                                    allProps.Add((p.Name, p.Value));
                            }
                            allProps.Add(("__closing__", "")); // Closing brace
                        }
                    }
                    
                    if (rowIndex >= 0 && rowIndex < allProps.Count)
                    {
                        var (propName, propValue) = allProps[rowIndex];
                        if (propName != "__selector__" && propName != "__closing__")
                        {
                            _editingCssNodeId = _selectedNodeId;
                            _editingCssPropertyName = propName;
                            _editingCssPropertyValue = propValue;
                            _cursorBlink = true;
                            _lastBlink = DateTime.Now;
                            
                            // Show color picker for color properties
                            _colorPickerVisible = ColorPicker.IsColorProperty(propName);
                            
                            // Initialize autocomplete
                            _autocompleteSuggestions = CssAutocomplete.GetSuggestions(propName, propValue);
                            _autocompleteSelectedIndex = 0;
                            
                            Invalidate();
                            return true;
                        }
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
        if (_editingElementAsHtml)
        {
            if (!char.IsControl(c))
            {
                _editingElementHtml += c;
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                Invalidate();
            }
        }
        else if (_editingAttrKey != null)
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
        else if (_editingCssPropertyName != null)
        {
            if (!char.IsControl(c))
            {
                _editingCssPropertyValue += c;
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                
                // Update autocomplete suggestions
                _autocompleteSuggestions = CssAutocomplete.GetSuggestions(_editingCssPropertyName, _editingCssPropertyValue);
                _autocompleteSelectedIndex = 0;
                
                Invalidate();
            }
        }
    }
    
    public override bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt)
    {
        if (_editingAttrKey == null && _editingNodeId == null && _editingCssPropertyName == null && !_editingElementAsHtml) return false;
        
        const int KEY_BACK = 8;
        const int KEY_ENTER = 13;
        const int KEY_ESC = 27;
        const int KEY_TAB = 9;

        if (_editingElementAsHtml)
        {
            if (keyCode == KEY_BACK)
            {
                if (_editingElementHtml.Length > 0)
                {
                    _editingElementHtml = _editingElementHtml.Substring(0, _editingElementHtml.Length - 1);
                    _cursorBlink = true;
                    _lastBlink = DateTime.Now;
                    Invalidate();
                }
                return true;
            }
            else if (keyCode == KEY_ENTER && ctrl)
            {
                _ = ApplyHtmlEditAsync();
                return true;
            }
            else if (keyCode == KEY_ENTER)
            {
                _editingElementHtml += Environment.NewLine;
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                Invalidate();
                return true;
            }
            else if (keyCode == KEY_TAB)
            {
                _editingElementHtml += "    ";
                _cursorBlink = true;
                _lastBlink = DateTime.Now;
                Invalidate();
                return true;
            }
            else if (keyCode == KEY_ESC)
            {
                _editingElementAsHtml = false;
                _editingElementNodeId = null;
                Invalidate();
                return true;
            }
        }
        
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
            else if (_editingCssPropertyName != null)
            {
                if (_editingCssPropertyValue.Length > 0)
                {
                    _editingCssPropertyValue = _editingCssPropertyValue.Substring(0, _editingCssPropertyValue.Length - 1);
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
            else if (_editingCssPropertyName != null) _ = ApplyCssPropertyChange();
            return true;
        }
        else if (keyCode == KEY_ESC)
        {
            _editingAttrKey = null;
            _editingNodeId = null;
            _editingCssPropertyName = null;
            _autocompleteSuggestions.Clear();
            Invalidate();
            return true;
        }
        
        // Autocomplete navigation (only when editing CSS and have suggestions)
        if (_editingCssPropertyName != null && _autocompleteSuggestions.Count > 0)
        {
            const int KEY_UP = 38;
            const int KEY_DOWN = 40;
            if (keyCode == KEY_UP)
            {
                _autocompleteSelectedIndex = Math.Max(0, _autocompleteSelectedIndex - 1);
                Invalidate();
                return true;
            }
            else if (keyCode == KEY_DOWN)
            {
                _autocompleteSelectedIndex = Math.Min(_autocompleteSuggestions.Count - 1, _autocompleteSelectedIndex + 1);
                Invalidate();
                return true;
            }
            else if (keyCode == KEY_TAB && !shift)
            {
                // Accept suggestion with Tab
                if (_autocompleteSelectedIndex >= 0 && _autocompleteSelectedIndex < _autocompleteSuggestions.Count)
                {
                    _editingCssPropertyValue = _autocompleteSuggestions[_autocompleteSelectedIndex];
                    _autocompleteSuggestions.Clear();
                    Invalidate();
                }
                return true;
            }
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
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] Attribute change error: {ex.Message}", LogCategory.General);
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
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] Node value change error: {ex.Message}", LogCategory.General);
        }
    }
    
    private async Task ApplyCssPropertyChange()
    {
        if (_editingCssPropertyName == null || _editingCssNodeId == null || Host == null) return;
        
        int nodeId = _editingCssNodeId.Value;
        string propertyName = _editingCssPropertyName;
        string value = _editingCssPropertyValue;
        
        // Clear editing state immediately for UX
        _editingCssPropertyName = null;
        _editingCssNodeId = null;
        Invalidate();
        
        try
        {
            // Send CSS.setStyleTexts command with edits array
            var edits = new[] { new { nodeId = nodeId, propertyName = propertyName, value = value } };
            var request = new ProtocolRequest<object>
            {
                Id = 1005,
                Method = "CSS.setStyleTexts",
                Params = new { edits = edits }
            };
            
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            
            // Refresh styles after applying change
            await FetchStylesAsync(nodeId);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] CSS property change error: {ex.Message}", LogCategory.General);
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
        if (_draggingSplitter)
        {
            _draggingSplitter = false;
            Host?.ReleaseCapture();  // Release capture after drag
            Invalidate();
        }
        
        // Always reset cursor on mouse up
        Host?.RequestCursorChange(CursorType.Default);
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        if (x < _splitterX)
        {
            // Tree scroll
            _treeScrollY = Math.Clamp(_treeScrollY - deltaY * 40, 0, _treeMaxScrollY);
        }
        else
        {
            // Styles scroll
            _stylesScrollY = Math.Clamp(_stylesScrollY - deltaY * 40, 0, _stylesMaxScrollY);
        }
        
        Invalidate();
    }
    
    private int GetNodeIndexAt(float relativeY)
    {
        int index = (int)(relativeY / DevToolsTheme.ItemHeight);
        return index >= 0 && index < _flattenedNodes.Count ? index : -1;
    }
    
    // --- 10/10: Search functionality ---
    
    /// <summary>
    /// Search for nodes matching the query (tag name, id, class, or text content).
    /// </summary>
    public void Search(string query)
    {
        _searchQuery = query;
        _searchResults.Clear();
        _searchCurrentIndex = 0;
        
        if (string.IsNullOrWhiteSpace(query)) return;
        
        var lowerQuery = query.ToLowerInvariant();
        foreach (var kv in _nodeMap)
        {
            var node = kv.Value;
            bool matches = false;
            
            // Match tag name
            if (node.NodeName?.ToLowerInvariant().Contains(lowerQuery) == true)
                matches = true;
            
            // Match id
            if (node.Attributes?.TryGetValue("id", out var id) == true && 
                id.ToLowerInvariant().Contains(lowerQuery))
                matches = true;
            
            // Match class
            if (node.Attributes?.TryGetValue("class", out var cls) == true && 
                cls.ToLowerInvariant().Contains(lowerQuery))
                matches = true;
            
            // Match text content
            if (node.NodeValue?.ToLowerInvariant().Contains(lowerQuery) == true)
                matches = true;
            
            if (matches) _searchResults.Add(kv.Key);
        }
        
        if (_searchResults.Count > 0)
        {
            NavigateToSearchResult(0);
        }
        
        Invalidate();
    }
    
    /// <summary>
    /// Navigate to search result by index.
    /// </summary>
    public void NavigateToSearchResult(int index)
    {
        if (_searchResults.Count == 0) return;
        
        _searchCurrentIndex = Math.Clamp(index, 0, _searchResults.Count - 1);
        int nodeId = _searchResults[_searchCurrentIndex];
        
        // Expand parents
        ExpandParentsOfNode(nodeId);
        
        // Select and scroll to node
        SelectNode(nodeId);
    }
    
    /// <summary>
    /// Go to next search result.
    /// </summary>
    public void NextSearchResult() => NavigateToSearchResult(_searchCurrentIndex + 1);
    
    /// <summary>
    /// Go to previous search result.
    /// </summary>
    public void PreviousSearchResult() => NavigateToSearchResult(_searchCurrentIndex - 1);
    
    private void ExpandParentsOfNode(int nodeId)
    {
        if (!_nodeMap.TryGetValue(nodeId, out var node)) return;
        
        int? parentId = node.ParentId;
        while (parentId.HasValue && _nodeMap.TryGetValue(parentId.Value, out var parent))
        {
            _expandedNodes.Add(parentId.Value);
            parentId = parent.ParentId;
        }
        
        RefreshFlattenedTree();
    }
    
    // --- 10/10: Breadcrumb trail ---
    
    /// <summary>
    /// Get the path from root to the specified node.
    /// </summary>
    public List<int> GetNodePath(int nodeId)
    {
        var path = new List<int>();
        int? current = nodeId;
        
        while (current.HasValue && _nodeMap.TryGetValue(current.Value, out var node))
        {
            path.Insert(0, current.Value);
            current = node.ParentId;
        }
        
        return path;
    }
    
    /// <summary>
    /// Update breadcrumb path for selected node.
    /// </summary>
    private void UpdateBreadcrumbs()
    {
        _breadcrumbPath = _selectedNodeId.HasValue ? GetNodePath(_selectedNodeId.Value) : new List<int>();
    }
    
    // --- 10/10: CSS Selector generation ---
    
    /// <summary>
    /// Generate a unique CSS selector for the specified node.
    /// </summary>
    public string GenerateCssSelector(int nodeId)
    {
        var path = new List<string>();
        int? current = nodeId;
        
        while (current.HasValue && _nodeMap.TryGetValue(current.Value, out var node))
        {
            // Stop at document or html
            if (node.NodeType == 9 || node.NodeName.ToLowerInvariant() == "html")
                break;
            
            string selector = node.NodeName.ToLowerInvariant();
            
            // Prefer id if available (stops the chain)
            if (node.Attributes?.TryGetValue("id", out var id) == true && !string.IsNullOrEmpty(id))
            {
                path.Insert(0, "#" + id);
                break;
            }
            
            // Add class if available
            if (node.Attributes?.TryGetValue("class", out var cls) == true && !string.IsNullOrEmpty(cls))
            {
                var firstClass = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstClass))
                    selector += "." + firstClass;
            }
            
            path.Insert(0, selector);
            current = node.ParentId;
        }
        
        return string.Join(" > ", path);
    }
    
    /// <summary>
    /// Copy CSS selector for selected node to clipboard.
    /// </summary>
    public void CopySelectedSelector()
    {
        if (_selectedNodeId.HasValue)
        {
            _lastCopiedSelector = GenerateCssSelector(_selectedNodeId.Value);
            Host?.CopyToClipboard(_lastCopiedSelector);
        }
    }
    
    private void SelectNode(int nodeId)
    {
        _selectedNodeId = nodeId;
        UpdateBreadcrumbs();
        _ = FetchStylesAsync(nodeId);
        _ = SendHighlightCommand(nodeId);
        Invalidate();
    }
    
    // --- Edit as HTML functionality ---
    
    /// <summary>
    /// Start editing an element as HTML.
    /// </summary>
    private async Task StartEditAsHtmlAsync(int nodeId)
    {
        if (!_nodeMap.TryGetValue(nodeId, out var node)) return;
        
        _editingElementAsHtml = true;
        _editingElementNodeId = nodeId;
        _editingElementHtml = GenerateOuterHtml(node);
        _editingAttrKey = null;
        _editingNodeId = null;
        _editingCssPropertyName = null;
        _cursorBlink = true;
        _lastBlink = DateTime.Now;
        Invalidate();

        if (Host == null) return;

        try
        {
            var request = new ProtocolRequest<object>
            {
                Id = 1007,
                Method = "DOM.getOuterHTML",
                Params = new { nodeId }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = ProtocolJson.Deserialize<ProtocolResponse<GetOuterHtmlResult>>(responseJson);
            if (response?.Result?.OuterHTML != null)
            {
                _editingElementHtml = response.Result.OuterHTML;
                Invalidate();
            }
        }
        catch (Exception ex)
        {
            EngineLogCompat.Error($"[ElementsPanel] getOuterHTML error: {ex.Message}", LogCategory.General);
        }
    }
    
    /// <summary>
    /// Generate outerHTML for a DOM node.
    /// </summary>
    private string GenerateOuterHtml(DomNodeDto node)
    {
        if (node.NodeType == 3) // Text node
            return node.NodeValue ?? "";
        
        if (node.NodeType == 8) // Comment
            return $"<!--{node.NodeValue}-->";
        
        var sb = new System.Text.StringBuilder();
        string tagName = node.NodeName.ToLower();
        
        sb.Append('<');
        sb.Append(tagName);
        
        if (node.Attributes != null)
        {
            foreach (var attr in node.Attributes)
            {
                sb.Append(' ');
                sb.Append(attr.Key);
                sb.Append("=\"");
                sb.Append(System.Web.HttpUtility.HtmlEncode(attr.Value));
                sb.Append('"');
            }
        }
        
        // Self-closing tags
        if (node.ChildNodeCount == 0 && IsSelfClosingTag(tagName))
        {
            sb.Append(" />");
            return sb.ToString();
        }
        
        sb.Append('>');
        
        // Children (simplified - just add placeholder if has children)
        if (node.ChildNodeCount > 0)
        {
            sb.Append("...");
        }
        
        sb.Append("</");
        sb.Append(tagName);
        sb.Append('>');
        
        return sb.ToString();
    }
    
    private bool IsSelfClosingTag(string tagName)
    {
        return tagName switch
        {
            "br" or "hr" or "img" or "input" or "meta" or "link" or 
            "area" or "base" or "col" or "embed" or "source" or "track" or "wbr" => true,
            _ => false
        };
    }
    
    /// <summary>
    /// Apply HTML edit changes.
    /// </summary>
    private async Task ApplyHtmlEditAsync()
    {
        if (_editingElementNodeId == null || Host == null) return;
        
        int nodeId = _editingElementNodeId.Value;
        string html = _editingElementHtml;
        
        _editingElementAsHtml = false;
        _editingElementNodeId = null;
        Invalidate();
        
        try
        {
            var request = new ProtocolRequest<object>
            {
                Id = 1006,
                Method = "DOM.setOuterHTML",
                Params = new { nodeId = nodeId, outerHTML = html }
            };
            
            await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            
            // Refresh tree after edit
            await RefreshTreeAsync();
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Error($"[ElementsPanel] setOuterHTML error: {ex.Message}", LogCategory.General);
        }
    }

    private void DrawHtmlEditorOverlay(SKCanvas canvas, SKRect bounds)
    {
        using var scrimPaint = DevToolsTheme.CreateFillPaint(SKColors.Black.WithAlpha(140));
        using var overlayPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TabBorder);
        using var titlePaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeLarge);
        using var hintPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeSmall);
        using var textPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeMedium);

        canvas.DrawRect(bounds, scrimPaint);

        var overlayRect = new SKRect(
            bounds.Left + 48,
            bounds.Top + 36,
            bounds.Right - 48,
            bounds.Bottom - 36);

        canvas.DrawRoundRect(overlayRect, 6, 6, overlayPaint);
        canvas.DrawRoundRect(overlayRect, 6, 6, borderPaint);

        float headerX = overlayRect.Left + DevToolsTheme.PaddingLarge;
        float headerY = overlayRect.Top + 24;
        canvas.DrawText("Edit Outer HTML", headerX, headerY, titlePaint);

        float hintY = headerY + 20;
        canvas.DrawText("Ctrl+Enter apply, Enter newline, Esc cancel", headerX, hintY, hintPaint);

        var editorRect = new SKRect(
            overlayRect.Left + DevToolsTheme.PaddingLarge,
            overlayRect.Top + 56,
            overlayRect.Right - DevToolsTheme.PaddingLarge,
            overlayRect.Bottom - DevToolsTheme.PaddingLarge);

        using var editorBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Background);
        using var editorBorderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.BorderLight);
        canvas.DrawRoundRect(editorRect, 4, 4, editorBgPaint);
        canvas.DrawRoundRect(editorRect, 4, 4, editorBorderPaint);

        canvas.Save();
        canvas.ClipRect(editorRect);

        var normalized = (_editingElementHtml ?? string.Empty).Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            lines = new[] { string.Empty };
        }

        const float lineHeight = 18f;
        float textX = editorRect.Left + DevToolsTheme.PaddingNormal;
        float textY = editorRect.Top + 20;

        for (int i = 0; i < lines.Length; i++)
        {
            if (textY > editorRect.Bottom - 4)
            {
                break;
            }

            canvas.DrawText(lines[i], textX, textY, textPaint);
            textY += lineHeight;
        }

        if (_cursorBlink)
        {
            string lastLine = lines[^1];
            float caretX = textX + textPaint.MeasureText(lastLine);
            float caretY = editorRect.Top + 20 + ((lines.Length - 1) * lineHeight);
            using var caretPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.TextPrimary);
            canvas.DrawLine(caretX + 1, caretY - 12, caretX + 1, caretY + 3, caretPaint);
        }

        canvas.Restore();
    }

    private void UpdateCursorBlink()
    {
        if (_editingAttrKey == null &&
            _editingNodeId == null &&
            _editingCssPropertyName == null &&
            !_editingElementAsHtml)
        {
            return;
        }

        if ((DateTime.Now - _lastBlink).TotalMilliseconds >= 500)
        {
            _cursorBlink = !_cursorBlink;
            _lastBlink = DateTime.Now;
            Invalidate();
        }
    }
    
    /// <summary>
    /// Represents a flattened DOM tree node.
    /// </summary>
    private record DomTreeNode(DomNodeDto Node, int Depth, bool HasChildren, bool IsExpanded, bool IsClosingTag = false);
}


