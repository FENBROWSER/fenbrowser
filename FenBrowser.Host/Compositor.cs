using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Theme;

namespace FenBrowser.Host;

/// <summary>
/// The single authority over rendering.
/// Enforces frame pacing, applied dirty regions, and composes UI.
/// </summary>
public class Compositor
{
    private readonly Widget _root;
    private SKRect? _lastDirtyRect;
    
    public Compositor(Widget root)
    {
        _root = root;
    }
    
    /// <summary>
    /// Current DPI scale factor.
    /// </summary>
    public float DpiScale { get; set; } = 1.0f;
    
    /// <summary>
    /// Perform the frame heartbeat: Layout (if needed) -> Render.
    /// </summary>
    public void Composite(SKCanvas canvas, SKSize logicalSize)
    {
        // 1. Layout Pass (Top-Down, only if dirty)
        // Layout always works in logical units
        EnsureLayout(logicalSize);
        
        // 2. Render Pass
        Render(canvas, logicalSize);
    }
    
    private void EnsureLayout(SKSize logicalSize)
    {
        if (_root.IsLayoutDirty)
        {
            // Measure pass
            _root.Measure(logicalSize);
            
            // Arrange pass
            _root.Arrange(new SKRect(0, 0, logicalSize.Width, logicalSize.Height));
            
            FenLogger.Debug($"[Compositor] Layout executed for size {logicalSize.Width}x{logicalSize.Height}. Root desired: {_root.DesiredSize.Width}x{_root.DesiredSize.Height}", LogCategory.General);
        }
    }
    
    private void Render(SKCanvas canvas, SKSize logicalSize)
    {
        var dirtyRect = _root.DirtyRect;
        
        // Optimization: If nothing changed, don't waste GPU cycles
        // FIXME: This causes blinking because OpenGL backbuffer is not preserved!
        // We must draw every frame or use an offscreen buffer. Disabling for now.
        // if (dirtyRect == null && _lastDirtyRect != null) return;
        
        canvas.Save();
        
        // Apply DPI scaling globally. 
        // This ensures all widgets see logical coordinates but render sharp pixels.
        canvas.Scale(DpiScale, DpiScale);
        
        if (dirtyRect.HasValue)
        {
            // Clip to dirty region for optimization (in logical units)
            canvas.ClipRect(dirtyRect.Value);
            _lastDirtyRect = dirtyRect;
        }
        else
        {
            // Full frame
            _lastDirtyRect = new SKRect(0, 0, logicalSize.Width, logicalSize.Height);
        }
        
        // Clear background (only the clipped part)
        canvas.Clear(ThemeManager.Current.Background);
        
        // Paint the tree in logical space
        _root.PaintAll(canvas);
        
        canvas.Restore();
        
        // Clear dirty flags for next frame
        _root.ClearDirtyRect();
    }
}
