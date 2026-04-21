using Silk.NET.Input;
using FenCursorType = FenBrowser.FenEngine.Interaction.CursorType;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Core.Logging;
using FenBrowser.Core;

namespace FenBrowser.Host.Input;

/// <summary>
/// Maps interaction roles to system cursors.
/// Stateless, throttled updates, no DOM access.
/// </summary>
public static class CursorManager
{
    private static FenCursorType _currentCursor = FenCursorType.Default;
    private static DateTime _lastUpdate = DateTime.MinValue;
    private static readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(16); // ~60fps
    
    private static FenCursorType _pendingCursor = FenCursorType.Default;

    /// <summary>
    /// Update the cursor based on hit test result.
    /// Only throttle when cursor is NOT changing (to prevent hammering same cursor).
    /// Cursor changes go through immediately.
    /// </summary>
    public static void UpdateCursor(IMouse mouse, FenCursorType cursor)
    {
        _pendingCursor = cursor;
    }
    
    public static void ApplyPendingCursor(IMouse mouse)
    {
        if (_pendingCursor == _currentCursor)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUpdate) < _throttleInterval)
                return;
            _lastUpdate = now;
            return;
        }
        
        EngineLogBridge.Debug($"[Cursor] Changed: {_currentCursor} → {_pendingCursor}", LogCategory.Events);
        _currentCursor = _pendingCursor;
        _lastUpdate = DateTime.UtcNow;
        
        var silkCursor = MapToSilkCursor(_pendingCursor);
        mouse.Cursor.StandardCursor = silkCursor;
    }
    
    /// <summary>
    /// Update cursor from hit test result.
    /// </summary>
    public static void UpdateFromHitTest(IMouse mouse, HitTestResult result)
    {
        UpdateCursor(mouse, result.Cursor);
    }
    
    /// <summary>
    /// Update cursor from DevTools request.
    /// </summary>
    public static void UpdateCursorFromDevTools(IMouse mouse, FenBrowser.DevTools.Core.CursorType cursor)
    {
        var fenCursor = cursor switch
        {
            FenBrowser.DevTools.Core.CursorType.Pointer => FenCursorType.Pointer,
            FenBrowser.DevTools.Core.CursorType.Text => FenCursorType.Text,
            FenBrowser.DevTools.Core.CursorType.HorizontalResize => FenCursorType.ResizeEW,
            FenBrowser.DevTools.Core.CursorType.VerticalResize => FenCursorType.ResizeNS,
            FenBrowser.DevTools.Core.CursorType.Crosshair => FenCursorType.Crosshair,
            _ => FenCursorType.Default
        };
        
        UpdateCursor(mouse, fenCursor);
    }
    
    /// <summary>
    /// Reset to default cursor.
    /// </summary>
    public static void ResetCursor(IMouse mouse)
    {
        UpdateCursor(mouse, FenCursorType.Default);
    }
    
    /// <summary>
    /// Map our CursorType to Silk.NET StandardCursor.
    /// </summary>
    private static StandardCursor MapToSilkCursor(FenCursorType cursor)
    {
        return cursor switch
        {
            FenCursorType.Default => StandardCursor.Default,
            FenCursorType.Pointer => StandardCursor.Hand,
            FenCursorType.Text => StandardCursor.IBeam,
            FenCursorType.Wait => StandardCursor.Default, // Silk.NET lacks wait cursor
            FenCursorType.NotAllowed => StandardCursor.Default, // Silk.NET lacks this
            FenCursorType.Move => StandardCursor.Default,
            FenCursorType.ResizeNS => StandardCursor.VResize,
            FenCursorType.ResizeEW => StandardCursor.HResize,
            FenCursorType.ResizeNESW => StandardCursor.Default,
            FenCursorType.ResizeNWSE => StandardCursor.Default,
            FenCursorType.Crosshair => StandardCursor.Crosshair,
            FenCursorType.Grab => StandardCursor.Default,
            FenCursorType.Grabbing => StandardCursor.Default,
            _ => StandardCursor.Default
        };
    }
}


