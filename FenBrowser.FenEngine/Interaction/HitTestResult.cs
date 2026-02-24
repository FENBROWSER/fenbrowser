namespace FenBrowser.FenEngine.Interaction;

/// <summary>
/// Cursor type for UI feedback.
/// Maps to system cursors.
/// </summary>
public enum CursorType
{
    Default,
    Pointer,    // Hand cursor for links
    Text,       // I-beam for text inputs
    Wait,       // Loading
    NotAllowed, // Disabled/not interactive
    Move,       // Draggable
    ResizeNS,   // Vertical resize
    ResizeEW,   // Horizontal resize
    ResizeNESW, // Diagonal resize
    ResizeNWSE, // Diagonal resize
    Crosshair,  // Precise selection
    Grab,       // Grabbable
    Grabbing    // Currently grabbing
}

/// <summary>
/// Immutable hit test result projection.
/// Contains no DOM references, no setters, no behavior.
/// Safe to pass across boundaries.
/// </summary>
public readonly record struct HitTestResult(
    /// <summary>Tag name of the hit element (lowercase).</summary>
    string TagName,
    
    /// <summary>Href if this is a link element or inside one.</summary>
    string? Href,
    
    /// <summary>Suggested cursor for this element.</summary>
    CursorType Cursor,
    
    /// <summary>Whether clicking triggers action (links, buttons).</summary>
    bool IsClickable,
    
    /// <summary>Whether element can receive focus.</summary>
    bool IsFocusable,
    
    /// <summary>Whether element is a text input/textarea.</summary>
    bool IsEditable,
    
    /// <summary>
    /// Element ID if present. Useful for debugging/logging only.
    /// Do NOT use for DOM manipulation.
    /// </summary>
    string? ElementId = null,
    
    /// <summary>
    /// Text content preview (first N chars). For status bar display.
    /// </summary>
    string? TextPreview = null,
    
    /// <summary>
    /// The actual native element that was hit. 
    /// Used for internal state updates (hover, focus).
    /// </summary>
    object? NativeElement = null,
    
    /// <summary>
    /// The bounding box of the hit element in local coordinates.
    /// </summary>
    SkiaSharp.SKRect? BoundingBox = null,

    /// <summary>
    /// The src attribute of an img element (if hit element is an img).
    /// </summary>
    string? ImageSrc = null
)
{
    /// <summary>
    /// Empty result when no element is hit.
    /// </summary>
    public static readonly HitTestResult None = new(
        TagName: "",
        Href: null,
        Cursor: CursorType.Default,
        IsClickable: false,
        IsFocusable: false,
        IsEditable: false
    );
    
    /// <summary>
    /// Whether any element was hit.
    /// </summary>
    public bool HasHit => !string.IsNullOrEmpty(TagName);
    
    /// <summary>
    /// Whether this is a navigable link.
    /// </summary>
    public bool IsLink => !string.IsNullOrEmpty(Href);
}
