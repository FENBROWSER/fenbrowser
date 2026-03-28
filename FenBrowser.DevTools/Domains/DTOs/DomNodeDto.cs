using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Domains.DTOs;

/// <summary>
/// DTO representing a DOM node in protocol messages.
/// </summary>
public record DomNodeDto
{
    /// <summary>
    /// Stable node ID from NodeRegistry.
    /// </summary>
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
    
    /// <summary>
    /// Parent node ID (null for root).
    /// </summary>
    [JsonPropertyName("parentId")]
    public int? ParentId { get; init; }
    
    /// <summary>
    /// Node type (1=Element, 3=Text, 8=Comment, 9=Document).
    /// </summary>
    [JsonPropertyName("nodeType")]
    public int NodeType { get; init; }
    
    /// <summary>
    /// Node name (tag name for elements, "#text" for text nodes, "#comment" for comments).
    /// </summary>
    [JsonPropertyName("nodeName")]
    public required string NodeName { get; init; }
    
    /// <summary>
    /// Node value (text content for text nodes).
    /// </summary>
    [JsonPropertyName("nodeValue")]
    public string? NodeValue { get; init; }
    
    /// <summary>
    /// Element attributes (name -> value).
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, string>? Attributes { get; init; }
    
    /// <summary>
    /// Child node IDs (for incremental loading).
    /// </summary>
    [JsonPropertyName("childNodeIds")]
    public int[]? ChildNodeIds { get; init; }
    
    /// <summary>
    /// Full child nodes (for initial snapshot).
    /// </summary>
    [JsonPropertyName("children")]
    public DomNodeDto[]? Children { get; init; }
    
    /// <summary>
    /// Number of child nodes (for lazy loading).
    /// </summary>
    [JsonPropertyName("childNodeCount")]
    public int ChildNodeCount { get; init; }
}

/// <summary>
/// Response for DOM.getDocument.
/// </summary>
public record GetDocumentResult
{
    [JsonPropertyName("root")]
    public required DomNodeDto Root { get; init; }
}

/// <summary>
/// Parameters for DOM.highlightNode.
/// </summary>
public record HighlightNodeParams
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
    
    [JsonPropertyName("highlightConfig")]
    public HighlightConfig? HighlightConfig { get; init; }
}

/// <summary>
/// Highlight configuration.
/// </summary>
public record HighlightConfig
{
    [JsonPropertyName("contentColor")]
    public RgbaColor? ContentColor { get; init; }
    
    [JsonPropertyName("paddingColor")]
    public RgbaColor? PaddingColor { get; init; }
    
    [JsonPropertyName("marginColor")]
    public RgbaColor? MarginColor { get; init; }
    
    [JsonPropertyName("borderColor")]
    public RgbaColor? BorderColor { get; init; }
}

/// <summary>
/// RGBA color.
/// </summary>
public record RgbaColor(int R, int G, int B, float A = 1.0f);

/// <summary>
/// Event: Child node inserted.
/// </summary>
public record ChildNodeInsertedEvent
{
    [JsonPropertyName("parentNodeId")]
    public int ParentNodeId { get; init; }
    
    [JsonPropertyName("previousNodeId")]
    public int PreviousNodeId { get; init; }
    
    [JsonPropertyName("node")]
    public required DomNodeDto Node { get; init; }
}

/// <summary>
/// Event: Child node removed.
/// </summary>
public record ChildNodeRemovedEvent
{
    [JsonPropertyName("parentNodeId")]
    public int ParentNodeId { get; init; }
    
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
}

/// <summary>
/// Event: Attribute modified.
/// </summary>
public record AttributeModifiedEvent
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
    
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>
/// Event: Attribute removed.
/// </summary>
public record AttributeRemovedEvent
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
    
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Result for DOM.requestChildNodes.
/// </summary>
public record RequestChildNodesResult
{
    [JsonPropertyName("nodes")]
    public required DomNodeDto[] Nodes { get; init; }
}

/// <summary>
/// Result for DOM.getOuterHTML.
/// </summary>
public record GetOuterHtmlResult
{
    [JsonPropertyName("outerHTML")]
    public required string OuterHTML { get; init; }
}

/// <summary>
/// Result for DOM.querySelector.
/// </summary>
public record QuerySelectorResult
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
}

/// <summary>
/// Result for DOM.querySelectorAll.
/// </summary>
public record QuerySelectorAllResult
{
    [JsonPropertyName("nodeIds")]
    public required int[] NodeIds { get; init; }
}

/// <summary>
/// Event: Character data modified (text content).
/// </summary>
public record CharacterDataModifiedEvent
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
    
    [JsonPropertyName("characterData")]
    public required string CharacterData { get; init; }
}
