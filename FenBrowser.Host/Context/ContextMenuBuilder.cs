using FenBrowser.FenEngine.Interaction;

namespace FenBrowser.Host.Context;

/// <summary>
/// Builds context menus based on HitTestResult and current state.
/// No DOM traversal, no renderer calls.
/// Uses only HitTestResult, selection state, and clipboard policy.
/// </summary>
public static class ContextMenuBuilder
{
    /// <summary>
    /// Build a context menu for the given hit test result.
    /// </summary>
    public static List<ContextMenuItem> Build(
        HitTestResult hitTest, 
        bool hasSelection = false,
        bool canPaste = true,
        Action<string> onNavigate = null,
        Action onCopy = null,
        Action onPaste = null,
        Action onSelectAll = null,
        Action onReload = null,
        Action onBack = null,
        Action onForward = null,
        Action<string> onOpenInNewTab = null,
        Action<string> onCopyLink = null,
        Action<string> onSaveImage = null)
    {
        var items = new List<ContextMenuItem>();
        
        // Link context
        if (hitTest.IsLink && !string.IsNullOrEmpty(hitTest.Href))
        {
            items.Add(ContextMenuItem.Create("Open Link", () => onNavigate?.Invoke(hitTest.Href)));
            items.Add(ContextMenuItem.Create("Open Link in New Tab", () => onOpenInNewTab?.Invoke(hitTest.Href)));
            items.Add(ContextMenuItem.Create("Copy Link Address", () => onCopyLink?.Invoke(hitTest.Href)));
            items.Add(ContextMenuItem.Separator());
        }
        
        // Image context (check if it's an img tag)
        if (hitTest.TagName == "img")
        {
            items.Add(ContextMenuItem.Create("Save Image As...", () => onSaveImage?.Invoke(hitTest.Href)));
            items.Add(ContextMenuItem.Create("Copy Image", () => { /* Copy image to clipboard */ }));
            items.Add(ContextMenuItem.Separator());
        }
        
        // Text/editing context
        if (hasSelection)
        {
            items.Add(ContextMenuItem.Create("Copy", () => onCopy?.Invoke(), "Ctrl+C"));
        }
        
        if (hitTest.IsEditable)
        {
            if (hasSelection)
            {
                items.Add(ContextMenuItem.Create("Cut", () => { /* Cut */ }, "Ctrl+X"));
            }
            items.Add(ContextMenuItem.Create("Paste", () => onPaste?.Invoke(), "Ctrl+V", canPaste));
            items.Add(ContextMenuItem.Separator());
        }
        
        // Select all (always available)
        items.Add(ContextMenuItem.Create("Select All", () => onSelectAll?.Invoke(), "Ctrl+A"));
        
        // Page context (always available)
        items.Add(ContextMenuItem.Separator());
        
        items.Add(ContextMenuItem.Create("Back", () => onBack?.Invoke(), "Alt+←"));
        items.Add(ContextMenuItem.Create("Forward", () => onForward?.Invoke(), "Alt+→"));
        items.Add(ContextMenuItem.Create("Reload", () => onReload?.Invoke(), "Ctrl+R"));
        
        items.Add(ContextMenuItem.Separator());
        items.Add(ContextMenuItem.Create("View Page Source", () => { /* View source */ }, "Ctrl+U"));
        items.Add(ContextMenuItem.Create("Inspect Element", () => { /* Open dev tools */ }, "Ctrl+Shift+I"));
        
        // Remove trailing separator if present
        while (items.Count > 0 && items[items.Count - 1].IsSeparator)
        {
            items.RemoveAt(items.Count - 1);
        }
        
        // Remove leading separator if present
        while (items.Count > 0 && items[0].IsSeparator)
        {
            items.RemoveAt(0);
        }
        
        return items;
    }
    
    /// <summary>
    /// Build a minimal context menu for empty areas.
    /// </summary>
    public static List<ContextMenuItem> BuildDefault(
        Action onReload = null,
        Action onBack = null,
        Action onForward = null)
    {
        return new List<ContextMenuItem>
        {
            ContextMenuItem.Create("Back", () => onBack?.Invoke(), "Alt+←"),
            ContextMenuItem.Create("Forward", () => onForward?.Invoke(), "Alt+→"),
            ContextMenuItem.Create("Reload", () => onReload?.Invoke(), "Ctrl+R"),
            ContextMenuItem.Separator(),
            ContextMenuItem.Create("View Page Source", () => { }, "Ctrl+U")
        };
    }
}
