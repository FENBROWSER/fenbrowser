namespace FenBrowser.Host.Context;

/// <summary>
/// Represents a single context menu item.
/// </summary>
public class ContextMenuItem
{
    /// <summary>
    /// Display label for the item.
    /// </summary>
    public string Label { get; set; }
    
    /// <summary>
    /// Keyboard shortcut hint (e.g., "Ctrl+C").
    /// </summary>
    public string Shortcut { get; set; }
    
    /// <summary>
    /// Icon identifier (optional).
    /// </summary>
    public string Icon { get; set; }
    
    /// <summary>
    /// Whether this item is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Whether this is a separator (horizontal line).
    /// </summary>
    public bool IsSeparator { get; set; }
    
    /// <summary>
    /// Action to execute when clicked.
    /// </summary>
    public Action OnClick { get; set; }
    
    /// <summary>
    /// Submenu items (for nested menus).
    /// </summary>
    public List<ContextMenuItem> SubItems { get; set; }
    
    /// <summary>
    /// Create a regular menu item.
    /// </summary>
    public static ContextMenuItem Create(string label, Action onClick, string shortcut = null, bool enabled = true)
    {
        return new ContextMenuItem
        {
            Label = label,
            OnClick = onClick,
            Shortcut = shortcut,
            IsEnabled = enabled
        };
    }
    
    /// <summary>
    /// Create a separator line.
    /// </summary>
    public static ContextMenuItem Separator()
    {
        return new ContextMenuItem { IsSeparator = true };
    }
}
