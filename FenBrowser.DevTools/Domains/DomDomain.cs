using FenBrowser.Core.Dom.V2;
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
    private readonly Func<Func<ProtocolResponse>, Task<ProtocolResponse>> _dispatchAsync;
    
    public string Domain => "DOM";
    
    /// <summary>
    /// Create DOM domain handler.
    /// </summary>
    /// <param name="registry">Node registry for ID management</param>
    /// <param name="getRootNode">Function to get the document root node</param>
    /// <param name="onHighlight">Callback when highlight is requested (null to clear)</param>
    /// <param name="dispatchAsync">Dispatcher used to execute DOM access on the owning thread</param>
    public DomDomain(INodeRegistry registry, Func<Node?> getRootNode, Action<int?>? onHighlight = null, Func<Func<ProtocolResponse>, Task<ProtocolResponse>>? dispatchAsync = null)
    {
        _registry = registry;
        _getRootNode = getRootNode;
        _onHighlight = onHighlight;
        _dispatchAsync = dispatchAsync ?? (operation => Task.FromResult(operation()));
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
        return DispatchAsync(() =>
        {
            var root = _getRootNode();
            if (root == null)
            {
                return ProtocolResponse.Failure(request.Id, "No document loaded");
            }

            var rootDto = BuildNodeDto(root, depth: 4);
            var result = new GetDocumentResult { Root = rootDto };
            return ProtocolResponse.Success(request.Id, result);
        });
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
            return DispatchAsync(() =>
            {
                _onHighlight?.Invoke(nodeId);
                return ProtocolResponse.Success(request.Id, new { });
            });
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
        return DispatchAsync(() =>
        {
            _onHighlight?.Invoke(null);
            return ProtocolResponse.Success(request.Id, new { });
        });
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
            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node == null)
                {
                    return ProtocolResponse.Failure(request.Id, "Node not found");
                }

                var children = node.ChildNodes
                    .Select(child => BuildNodeDto(child, depth: 1))
                    .ToArray();

                return ProtocolResponse.Success(request.Id, new { nodes = children });
            });
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
            
            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node is Element element)
                {
                    element.SetAttribute(name, value ?? "");
                    return ProtocolResponse.Success(request.Id, new { });
                }

                return ProtocolResponse.Failure(request.Id, "Node not found or not an element");
            });
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
            
            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node is Element element)
                {
                    element.RemoveAttribute(name);
                    return ProtocolResponse.Success(request.Id, new { });
                }

                return ProtocolResponse.Failure(request.Id, "Node not found or not an element");
            });
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

            return DispatchAsync(() =>
            {
                var node = _registry.GetNode(nodeId);
                if (node is Text textNode)
                {
                    textNode.Data = value ?? "";
                    return ProtocolResponse.Success(request.Id, new { });
                }

                return ProtocolResponse.Failure(request.Id, "Node not found or not a text node");
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Error: {ex.Message}"));
        }
    }
    
    private Task<ProtocolResponse> DispatchAsync(Func<ProtocolResponse> operation)
    {
        return _dispatchAsync(operation);
    }

    /// <summary>
    /// Build DTO for a node with specified depth.
    /// </summary>
    private DomNodeDto BuildNodeDto(Node node, int depth)
    {
        var nodeId = _registry.GetId(node);
        var parentId = node.ParentNode != null ? _registry.GetId(node.ParentNode) : (int?)null;
        
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
        if (node is Element el2 && el2.Attributes.Length > 0)
        {
            attributes = new Dictionary<string, string>();
            for(int i=0; i<el2.Attributes.Length; i++)
            {
                 var attr = el2.Attributes[i];
                 attributes[attr.Name] = attr.Value;
            }
        }
        
        // Get children
        DomNodeDto[]? children = null;
        int[]? childNodeIds = null;
        
        if (depth > 0 && node.ChildNodes.Length > 0)
        {
            children = node.ChildNodes
                .Select(child => BuildNodeDto(child, depth - 1))
                .ToArray();
        }
        else if (node.ChildNodes.Length > 0)
        {
            // Just return IDs for lazy loading
            childNodeIds = node.ChildNodes
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
            ChildNodeCount = (node is Element || node is Document) ? node.ChildNodes.Length : 0
        };
    }
}
