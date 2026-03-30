namespace FenBrowser.Host.Context;

/// <summary>
/// Represents a single context menu item.
/// </summary>
public class ContextMenuItem
{
    private string _label = string.Empty;
    private string _shortcut = string.Empty;
    private string _icon = string.Empty;
    private List<ContextMenuItem> _subItems = new();

    /// <summary>
    /// Display label for the item.
    /// </summary>
    public string Label
    {
        get => _label;
        set => _label = NormalizeText(value);
    }
    
    /// <summary>
    /// Keyboard shortcut hint (e.g., "Ctrl+C").
    /// </summary>
    public string Shortcut
    {
        get => _shortcut;
        set => _shortcut = NormalizeText(value);
    }
    
    /// <summary>
    /// Icon identifier (optional).
    /// </summary>
    public string Icon
    {
        get => _icon;
        set => _icon = NormalizeText(value);
    }
    
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
    public List<ContextMenuItem> SubItems
    {
        get => _subItems;
        set => _subItems = NormalizeSubItems(value);
    }

    public bool CanInvoke => !IsSeparator && IsEnabled && OnClick != null;

    public bool HasSubmenu => _subItems.Count > 0;
    
    /// <summary>
    /// Create a regular menu item.
    /// </summary>
    public static ContextMenuItem Create(string label, Action onClick, string shortcut = null, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Context menu items require a non-empty label.", nameof(label));
        }

        return new ContextMenuItem
        {
            Label = label,
            OnClick = onClick,
            Shortcut = shortcut,
            IsEnabled = enabled && onClick != null,
            SubItems = new List<ContextMenuItem>()
        };
    }
    
    /// <summary>
    /// Create a separator line.
    /// </summary>
    public static ContextMenuItem Separator()
    {
        return new ContextMenuItem
        {
            IsSeparator = true,
            IsEnabled = false,
            SubItems = new List<ContextMenuItem>()
        };
    }

    public bool Invoke()
    {
        if (!CanInvoke)
        {
            return false;
        }

        OnClick();
        return true;
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static List<ContextMenuItem> NormalizeSubItems(List<ContextMenuItem> items)
    {
        if (items == null || items.Count == 0)
        {
            return new List<ContextMenuItem>();
        }

        var normalized = new List<ContextMenuItem>(items.Count);
        foreach (var item in items)
        {
            if (item != null)
            {
                normalized.Add(item);
            }
        }

        return normalized;
    }
}
