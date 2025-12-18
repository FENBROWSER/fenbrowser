using Silk.NET.Input;
using FenBrowser.Host.Widgets;

namespace FenBrowser.Host.Input;

/// <summary>
/// Central keyboard shortcut dispatcher.
/// Routes: Global → Widget → Active Tab
/// No direct DOM handling.
/// </summary>
public class KeyboardDispatcher
{
    private static KeyboardDispatcher _instance;
    public static KeyboardDispatcher Instance => _instance ??= new KeyboardDispatcher();
    
    private readonly Dictionary<(Key key, bool ctrl, bool shift, bool alt), Action> _globalShortcuts = new();
    
    /// <summary>
    /// Register a global keyboard shortcut.
    /// </summary>
    public void RegisterGlobal(Key key, bool ctrl, bool shift, bool alt, Action handler)
    {
        _globalShortcuts[(key, ctrl, shift, alt)] = handler;
    }
    
    /// <summary>
    /// Register a simple ctrl+key shortcut.
    /// </summary>
    public void RegisterCtrl(Key key, Action handler)
    {
        RegisterGlobal(key, ctrl: true, shift: false, alt: false, handler);
    }
    
    /// <summary>
    /// Register a simple key shortcut.
    /// </summary>
    public void Register(Key key, Action handler)
    {
        RegisterGlobal(key, ctrl: false, shift: false, alt: false, handler);
    }
    
    /// <summary>
    /// Dispatch a key event.
    /// Returns true if the key was handled.
    /// </summary>
    public bool Dispatch(Key key, bool ctrl, bool shift, bool alt)
    {
        // 1. Try global shortcuts first
        if (_globalShortcuts.TryGetValue((key, ctrl, shift, alt), out var handler))
        {
            handler();
            return true;
        }
        
        // 2. Forward to focused widget
        var focused = FocusManager.Instance.FocusedWidget;
        if (focused != null)
        {
            focused.OnKeyDown(key);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Dispatch a text input character.
    /// </summary>
    public bool DispatchChar(char c)
    {
        var focused = FocusManager.Instance.FocusedWidget;
        if (focused != null)
        {
            focused.OnTextInput(c);
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Clear all registered shortcuts.
    /// </summary>
    public void ClearAll()
    {
        _globalShortcuts.Clear();
    }
}
