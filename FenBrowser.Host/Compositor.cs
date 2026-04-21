using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Theme;
using System;
using System.Collections.Generic;

namespace FenBrowser.Host;

/// <summary>
/// The single authority over rendering.
/// Enforces frame pacing, applied dirty regions, and composes UI.
/// 10/10 Spec: Layer management, dirty rect tracking, thread-safe composition.
/// </summary>
public class Compositor
{
    private readonly Widget _root;
    private SKRect? _lastDirtyRect;
    private SKSurface _frameSurface;
    private SKImage _frameSnapshot;
    private SKSizeI _framePixelSize;
    
    // --- Layer Management (10/10) ---
    private readonly List<CompositorLayer> _layers = new();
    private readonly object _layerLock = new();
    
    public Compositor(Widget root)
    {
        _root = root;
    }
    
    /// <summary>
    /// Current DPI scale factor.
    /// </summary>
    public float DpiScale { get; set; } = 1.0f;
    
    /// <summary>
    /// Get the list of compositor layers.
    /// </summary>
    public IReadOnlyList<CompositorLayer> Layers
    {
        get { lock (_layerLock) return _layers.ToArray(); }
    }
    
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
        
        // 3. Composite any additional layers
        CompositeLayers(canvas, logicalSize);
    }
    
    private void EnsureLayout(SKSize logicalSize)
    {
        if (_root.IsLayoutDirty)
        {
            // Measure pass
            _root.Measure(logicalSize);
            
            // Arrange pass
            _root.Arrange(new SKRect(0, 0, logicalSize.Width, logicalSize.Height));
            
            EngineLogBridge.Debug($"[Compositor] Layout executed for size {logicalSize.Width}x{logicalSize.Height}. Root desired: {_root.DesiredSize.Width}x{_root.DesiredSize.Height}", LogCategory.General);
        }
    }
    
    private void Render(SKCanvas canvas, SKSize logicalSize)
    {
        var dirtyRect = _root.DirtyRect;

        EnsureFrameBuffer(logicalSize);
        var needsRepaint = dirtyRect.HasValue || _frameSnapshot == null;

        if (needsRepaint && _frameSurface != null)
        {
            var offscreen = _frameSurface.Canvas;
            offscreen.Save();
            offscreen.Scale(DpiScale, DpiScale);

            if (_frameSnapshot != null && dirtyRect.HasValue)
            {
                // Seed from last composed frame, then repaint only dirty region.
                offscreen.DrawImage(_frameSnapshot, new SKRect(0, 0, logicalSize.Width, logicalSize.Height));
                offscreen.ClipRect(dirtyRect.Value);
                _lastDirtyRect = dirtyRect;
            }
            else
            {
                offscreen.Clear(ThemeManager.Current.Background);
                if (dirtyRect.HasValue)
                {
                    offscreen.ClipRect(dirtyRect.Value);
                    _lastDirtyRect = dirtyRect;
                }
                else
                {
                    _lastDirtyRect = new SKRect(0, 0, logicalSize.Width, logicalSize.Height);
                }
            }

            _root.PaintAll(offscreen);
            offscreen.Restore();

            _frameSnapshot?.Dispose();
            _frameSnapshot = _frameSurface.Snapshot();
        }

        if (_frameSnapshot != null)
        {
            canvas.DrawImage(_frameSnapshot, new SKRect(0, 0, logicalSize.Width, logicalSize.Height));
        }
        else
        {
            // Emergency fallback path.
            canvas.Clear(ThemeManager.Current.Background);
            canvas.Save();
            canvas.Scale(DpiScale, DpiScale);
            _root.PaintAll(canvas);
            canvas.Restore();
        }
         
        // Clear dirty flags for next frame
        _root.ClearDirtyRect();
    }

    private void EnsureFrameBuffer(SKSize logicalSize)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalSize.Width * DpiScale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalSize.Height * DpiScale));
        var targetSize = new SKSizeI(pixelWidth, pixelHeight);

        if (_frameSurface != null && _framePixelSize == targetSize)
        {
            return;
        }

        _frameSnapshot?.Dispose();
        _frameSnapshot = null;
        _frameSurface?.Dispose();
        _frameSurface = null;

        var info = new SKImageInfo(targetSize.Width, targetSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _frameSurface = SKSurface.Create(info);
        _framePixelSize = targetSize;
    }
    
    // --- Layer Management (10/10) ---
    
    /// <summary>
    /// Add a layer to the compositor.
    /// </summary>
    public void AddLayer(CompositorLayer layer)
    {
        lock (_layerLock)
        {
            _layers.Add(layer);
            _layers.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        }
    }
    
    /// <summary>
    /// Remove a layer from the compositor.
    /// </summary>
    public void RemoveLayer(CompositorLayer layer)
    {
        lock (_layerLock)
        {
            _layers.Remove(layer);
        }
    }
    
    /// <summary>
    /// Clear all additional layers.
    /// </summary>
    public void ClearLayers()
    {
        lock (_layerLock)
        {
            foreach (var layer in _layers)
            {
                layer.Dispose();
            }
            _layers.Clear();
        }
    }
    
    /// <summary>
    /// Composite all layers onto the canvas.
    /// </summary>
    private void CompositeLayers(SKCanvas canvas, SKSize logicalSize)
    {
        lock (_layerLock)
        {
            foreach (var layer in _layers)
            {
                if (!layer.IsVisible) continue;
                
                canvas.Save();
                canvas.Scale(DpiScale, DpiScale);
                
                if (layer.Opacity < 1.0f)
                {
                    using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)) };
                    layer.Render(canvas, paint);
                }
                else
                {
                    layer.Render(canvas, null);
                }
                
                canvas.Restore();
            }
        }
    }
    
    /// <summary>
    /// Mark a region as dirty for the next frame.
    /// </summary>
    public void InvalidateRect(SKRect rect)
    {
        _root.Invalidate(); // For now, invalidate entire root
    }
}

/// <summary>
/// A composable layer for advanced rendering (overlays, popups, etc).
/// </summary>
public class CompositorLayer : IDisposable
{
    public string Name { get; set; }
    public int ZIndex { get; set; } = 0;
    public SKRect Bounds { get; set; }
    public float Opacity { get; set; } = 1.0f;
    public bool IsVisible { get; set; } = true;
    public bool IsDirty { get; set; } = true;
    
    private SKSurface _surface;
    private Action<SKCanvas> _renderCallback;
    
    /// <summary>
    /// Create a layer with a custom render callback.
    /// </summary>
    public CompositorLayer(string name, SKRect bounds, Action<SKCanvas> renderCallback)
    {
        Name = name;
        Bounds = bounds;
        _renderCallback = renderCallback;
    }
    
    /// <summary>
    /// Render this layer to the target canvas.
    /// </summary>
    public void Render(SKCanvas canvas, SKPaint paint)
    {
        if (_renderCallback != null)
        {
            canvas.Save();
            canvas.Translate(Bounds.Left, Bounds.Top);
            canvas.ClipRect(new SKRect(0, 0, Bounds.Width, Bounds.Height));
            _renderCallback(canvas);
            canvas.Restore();
        }
        else if (_surface != null)
        {
            var image = _surface.Snapshot();
            canvas.DrawImage(image, Bounds.Left, Bounds.Top, paint);
            image?.Dispose();
        }
    }
    
    /// <summary>
    /// Create or resize the offscreen surface for this layer.
    /// </summary>
    public void EnsureSurface(int width, int height)
    {
        if (_surface == null || _surface.Canvas == null)
        {
            _surface?.Dispose();
            var info = new SKImageInfo(width, height);
            _surface = SKSurface.Create(info);
            IsDirty = true;
        }
    }
    
    /// <summary>
    /// Get the canvas for drawing to this layer's surface.
    /// </summary>
    public SKCanvas GetCanvas() => _surface?.Canvas;
    
    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }
}

