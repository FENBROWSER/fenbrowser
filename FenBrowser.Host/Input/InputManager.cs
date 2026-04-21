using Silk.NET.Input;
using FenBrowser.Host.Widgets;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.Input;

/// <summary>
/// Manages focus and mouse capture across the UI.
/// Identifies which widget handles current input sequences.
/// </summary>
public class InputManager
{
    private Widget _focusedWidget;
    private Widget _capturedWidget;
    private static InputManager _instance;
    
    public static InputManager Instance => _instance ??= new InputManager();
    
    public Widget FocusedWidget => _focusedWidget;
    public Widget CapturedWidget => _capturedWidget;
    public IMouse Mouse { get; set; }
    
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
            
            EngineLogBridge.Info($"[Accessibility] Focus set to '{_focusedWidget.Name}' ({_focusedWidget.Role})", LogCategory.General);
        }
        
        return true;
    }
    
    public void ClearFocus() => RequestFocus(null);
    
    /// <summary>
    /// Capture all mouse events to a specific widget.
    /// Used for dragging, slider interaction, etc.
    /// </summary>
    public void SetCapture(Widget widget)
    {
        _capturedWidget = widget;
    }
    
    /// <summary>
    /// Release the mouse capture.
    /// </summary>
    public void ReleaseCapture()
    {
        _capturedWidget = null;
    }
    
    // Tab Navigation Logic
    public void FocusNext(Widget root)
    {
        var focusables = CollectFocusables(root);
        if (focusables.Count == 0) return;
        int currentIndex = _focusedWidget != null ? focusables.IndexOf(_focusedWidget) : -1;
        int nextIndex = (currentIndex + 1) % focusables.Count;
        RequestFocus(focusables[nextIndex]);
    }
    
    public void FocusPrevious(Widget root)
    {
        var focusables = CollectFocusables(root);
        if (focusables.Count == 0) return;
        int currentIndex = _focusedWidget != null ? focusables.IndexOf(_focusedWidget) : 0;
        int prevIndex = (currentIndex - 1 + focusables.Count) % focusables.Count;
        RequestFocus(focusables[prevIndex]);
    }
    
    private List<Widget> CollectFocusables(Widget root)
    {
        var result = new List<Widget>();
        CollectFocusablesRecursive(root, result);
        return result;
    }
    
    private void CollectFocusablesRecursive(Widget widget, List<Widget> result)
    {
        if (widget == null) return;
        if (widget.CanFocus && widget.IsVisible && widget.IsEnabled) result.Add(widget);
        foreach (var child in widget.Children) CollectFocusablesRecursive(child, result);
    }
}

