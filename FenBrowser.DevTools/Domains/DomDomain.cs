using FenBrowser.Core.Dom;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// DOM Domain handler.
/// Handles DOM inspection methods: getDocument, highlightNode, etc.
/// </summary>
public class DomDomain : IProtocolHandler
{
    private readonly INodeRegistry _registry;
    private readonly Func<Node?> _getRootNode;
    private readonly Action<int?>? _onHighlight;
    
    public string Domain => "DOM";
    
    /// <summary>
    /// Create DOM domain handler.
    /// </summary>
    /// <param name="registry">Node registry for ID management</param>
    /// <param name="getRootNode">Function to get the document root node</param>
    /// <param name="onHighlight">Callback when highlight is requested (null to clear)</param>
    public DomDomain(INodeRegistry registry, Func<Node?> getRootNode, Action<int?>? onHighlight = null)
    {
        _registry = registry;
        _getRootNode = getRootNode;
        _onHighlight = onHighlight;
    }
    
    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "getDocument" => GetDocumentAsync(request),
            "highlightNode" => HighlightNodeAsync(request),
            "hideHighlight" => HideHighlightAsync(request),
            "requestChildNodes" => RequestChildNodesAsync(request),
            "setAttributeValue" => SetAttributeValueAsync(request),
            "removeAttribute" => RemoveAttributeAsync(request),
            "setNodeValue" => SetNodeValueAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: DOM.{method}"))
        };
    }
    
    /// <summary>
    /// Get the full document tree (initial snapshot).
    /// </summary>
    private Task<ProtocolResponse> GetDocumentAsync(ProtocolRequest request)
    {
        var root = _getRootNode();
        if (root == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "No document loaded"));
        }
        
        // Build the full tree (for initial load)
        var rootDto = BuildNodeDto(root, depth: 4);
        
        var result = new GetDocumentResult { Root = rootDto };
        return Task.FromResult(ProtocolResponse.Success(request.Id, result));
    }
    
    /// <summary>
    /// Highlight a node (sends to overlay surface).
    /// </summary>
    private Task<ProtocolResponse> HighlightNodeAsync(ProtocolRequest request)
    {
        // Parse nodeId from params
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "nodeId required"));
        }
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            _onHighlight?.Invoke(nodeId);
            return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
        }
        catch
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Invalid nodeId"));
        }
    }
    
    /// <summary>
    /// Hide the highlight.
    /// </summary>
    private Task<ProtocolResponse> HideHighlightAsync(ProtocolRequest request)
    {
        _onHighlight?.Invoke(null);
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }
    
    /// <summary>
    /// Request child nodes for lazy loading.
    /// </summary>
    private Task<ProtocolResponse> RequestChildNodesAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "nodeId required"));
        }
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var node = _registry.GetNode(nodeId);
            
            if (node == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found"));
            }
            
            var children = node.Children
                .Select(child => BuildNodeDto(child, depth: 1))
                .ToArray();
            
            return Task.FromResult(ProtocolResponse.Success(request.Id, new { nodes = children }));
        }
        catch
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Invalid nodeId"));
        }
    }
    
    /// <summary>
    /// Set attribute value on a node.
    /// </summary>
    private Task<ProtocolResponse> SetAttributeValueAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var name = request.Params.Value.GetProperty("name").GetString();
            var value = request.Params.Value.GetProperty("value").GetString();
            
            if (string.IsNullOrEmpty(name))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Attribute name required"));
            }
            
            var node = _registry.GetNode(nodeId);
            if (node is Element element)
            {
                element.SetAttribute(name, value ?? "");
                return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
            }
            
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found or not an element"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Error: {ex.Message}"));
        }
    }
    
    /// <summary>
    /// Remove attribute from a node.
    /// </summary>
    private Task<ProtocolResponse> RemoveAttributeAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }
        
        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var name = request.Params.Value.GetProperty("name").GetString();
            
            if (string.IsNullOrEmpty(name))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Attribute name required"));
            }
            
            var node = _registry.GetNode(nodeId);
            if (node is Element element)
            {
                element.RemoveAttribute(name);
                return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
            }
            
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found or not an element"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Set node value (for text nodes).
    /// </summary>
    private Task<ProtocolResponse> SetNodeValueAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var value = request.Params.Value.GetProperty("value").GetString();

            var node = _registry.GetNode(nodeId);
            if (node is Text textNode)
            {
                textNode.Data = value ?? "";
                return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
            }

            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found or not a text node"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Error: {ex.Message}"));
        }
    }
    
    /// <summary>
    /// Build DTO for a node with specified depth.
    /// </summary>
    private DomNodeDto BuildNodeDto(Node node, int depth)
    {
        var nodeId = _registry.GetId(node);
        var parentId = node.Parent != null ? _registry.GetId(node.Parent) : (int?)null;
        
        // Determine node type (check Document FIRST since it inherits from Element)
        int nodeType = node switch
        {
            Document => 9,   // DOCUMENT_NODE
            Text => 3,       // TEXT_NODE
            Element => 1,    // ELEMENT_NODE
            _ => 0
        };
        
        // Get node name
        string nodeName = node switch
        {
            Document => "#document",
            Text => "#text",
            Element el => el.TagName?.ToUpper() ?? "UNKNOWN",
            _ => node.GetType().Name
        };
        
        // Get node value (for text nodes)
        string? nodeValue = node is Text txt ? txt.Data : null;
        
        // Get attributes (for elements)
        Dictionary<string, string>? attributes = null;
        if (node is Element el2 && el2.Attributes.Count > 0)
        {
            attributes = new Dictionary<string, string>(el2.Attributes);
        }
        
        // Get children
        DomNodeDto[]? children = null;
        int[]? childNodeIds = null;
        
        if (depth > 0 && node.Children.Count > 0)
        {
            children = node.Children
                .Select(child => BuildNodeDto(child, depth - 1))
                .ToArray();
        }
        else if (node.Children.Count > 0)
        {
            // Just return IDs for lazy loading
            childNodeIds = node.Children
                .Select(child => _registry.GetId(child))
                .ToArray();
        }
        
        return new DomNodeDto
        {
            NodeId = nodeId,
            ParentId = parentId,
            NodeType = nodeType,
            NodeName = nodeName,
            NodeValue = nodeValue,
            Attributes = attributes,
            Children = children,
            ChildNodeIds = childNodeIds,
            ChildNodeCount = (node is Element || node is Document) ? node.Children.Count : 0
        };
    }
}
