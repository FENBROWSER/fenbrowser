using FenBrowser.Host.Widgets;

namespace FenBrowser.Host.Input;

/// <summary>
/// Manages focus ownership across the UI.
/// Single focus owner, explicit transitions, stateless.
/// Follows UI Events and HTML focus navigation rules.
/// </summary>
public class FocusManager
{
    private Widget _focusedWidget;
    private static FocusManager _instance;
    
    public static FocusManager Instance => _instance ??= new FocusManager();
    
    /// <summary>
    /// Currently focused widget.
    /// </summary>
    public Widget FocusedWidget => _focusedWidget;
    
    /// <summary>
    /// Request focus for a widget.
    /// </summary>
    public bool RequestFocus(Widget widget)
    {
        if (_focusedWidget == widget) return true;
        
        // Blur previous
        if (_focusedWidget != null)
        {
            _focusedWidget.IsFocused = false;
            _focusedWidget.OnBlur();
        }
        
        // Focus new
        _focusedWidget = widget;
        if (_focusedWidget != null)
        {
            _focusedWidget.IsFocused = true;
            _focusedWidget.OnFocus();
        }
        
        return true;
    }
    
    /// <summary>
    /// Clear focus.
    /// </summary>
    public void ClearFocus()
    {
        RequestFocus(null);
    }
    
    /// <summary>
    /// Focus next focusable widget (Tab navigation).
    /// </summary>
    public void FocusNext(Widget root)
    {
        var focusables = CollectFocusables(root);
        if (focusables.Count == 0) return;
        
        int currentIndex = _focusedWidget != null ? focusables.IndexOf(_focusedWidget) : -1;
        int nextIndex = (currentIndex + 1) % focusables.Count;
        RequestFocus(focusables[nextIndex]);
    }
    
    /// <summary>
    /// Focus previous focusable widget (Shift+Tab navigation).
    /// </summary>
    public void FocusPrevious(Widget root)
    {
        var focusables = CollectFocusables(root);
        if (focusables.Count == 0) return;
        
        int currentIndex = _focusedWidget != null ? focusables.IndexOf(_focusedWidget) : 0;
        int prevIndex = (currentIndex - 1 + focusables.Count) % focusables.Count;
        RequestFocus(focusables[prevIndex]);
    }
    
    /// <summary>
    /// Collect all focusable widgets in tree order.
    /// </summary>
    private List<Widget> CollectFocusables(Widget root)
    {
        var result = new List<Widget>();
        CollectFocusablesRecursive(root, result);
        return result;
    }
    
    private void CollectFocusablesRecursive(Widget widget, List<Widget> result)
    {
        if (widget == null) return;
        
        if (widget.CanFocus && widget.IsVisible && widget.IsEnabled)
        {
            result.Add(widget);
        }
        
        foreach (var child in widget.Children)
        {
            CollectFocusablesRecursive(child, result);
        }
    }
}
