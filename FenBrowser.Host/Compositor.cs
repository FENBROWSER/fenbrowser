using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Theme;
using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Host;

/// <summary>
/// Owns host-widget layout, retained frame backing, and compositor layers.
/// Native Skia resources are released deterministically through <see cref="IDisposable"/>.
/// </summary>
public class Compositor : IDisposable
{
    private readonly Widget _root;
    private SKRect? _lastDirtyRect;
    private SKSurface _frameSurface;
    private SKImage _frameSnapshot;
    private SKSizeI _framePixelSize;
    private readonly List<CompositorLayer> _layers = new();
    private readonly object _layerLock = new();
    private bool _disposed;
    private long _frameVersion;

    public Compositor(Widget root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>
    /// Current DPI scale factor.
    /// </summary>
    public float DpiScale { get; set; } = 1.0f;

    /// <summary>
    /// Advances whenever the composited output changes: the retained widget frame
    /// was repainted or the layer set changed. Both the compositor thread and the
    /// window loop composite through this instance, so the window loop compares
    /// this against what it last presented to notice frames it did not draw.
    /// </summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    /// <summary>
    /// Snapshot of the current compositor layers.
    /// </summary>
    public IReadOnlyList<CompositorLayer> Layers
    {
        get
        {
            lock (_layerLock)
            {
                return _layers.ToArray();
            }
        }
    }

    /// <summary>
    /// Perform the frame heartbeat: Layout (if needed) -> Render -> layers.
    /// </summary>
    public void Composite(SKCanvas canvas, SKSize logicalSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        var layoutExecuted = EnsureLayout(logicalSize);
        Render(canvas, logicalSize, layoutExecuted);
        CompositeLayers(canvas);
    }

    private bool EnsureLayout(SKSize logicalSize)
    {
        if (!_root.IsLayoutDirty)
        {
            return false;
        }

        _root.Measure(logicalSize);
        _root.Arrange(new SKRect(0, 0, logicalSize.Width, logicalSize.Height));

        EngineLogBridge.Debug(
            $"[Compositor] Layout executed for size {logicalSize.Width}x{logicalSize.Height}. " +
            $"Root desired: {_root.DesiredSize.Width}x{_root.DesiredSize.Height}",
            LogCategory.General);
        return true;
    }

    private void Render(SKCanvas canvas, SKSize logicalSize, bool layoutExecuted)
    {
        var dirtyRect = _root.DirtyRect;

        EnsureFrameBuffer(logicalSize);
        var isBootstrapFrame = _frameSnapshot == null;
        var needsRepaint = layoutExecuted || dirtyRect.HasValue || isBootstrapFrame;

        if (needsRepaint && _frameSurface != null)
        {
            var offscreen = _frameSurface.Canvas;
            offscreen.Save();
            try
            {
                offscreen.Scale(DpiScale, DpiScale);

                if (!layoutExecuted && !isBootstrapFrame && dirtyRect.HasValue)
                {
                    // The SKSurface is retained between frames, so unchanged pixels are
                    // already present. Re-seeding it from the previous full-frame snapshot
                    // turns every tiny damage update into an O(viewport) copy. Clear only
                    // the damaged logical region, then repaint through the same clip.
                    offscreen.ClipRect(dirtyRect.Value);
                    using var clearPaint = new SKPaint
                    {
                        Color = ThemeManager.Current.Background,
                        BlendMode = SKBlendMode.Src,
                        IsAntialias = false
                    };
                    offscreen.DrawRect(dirtyRect.Value, clearPaint);
                    _lastDirtyRect = dirtyRect;
                }
                else
                {
                    offscreen.Clear(ThemeManager.Current.Background);
                    _lastDirtyRect = new SKRect(0, 0, logicalSize.Width, logicalSize.Height);
                }

                _root.PaintAll(offscreen);
            }
            finally
            {
                offscreen.Restore();
            }

            _frameSnapshot?.Dispose();
            _frameSnapshot = _frameSurface.Snapshot();
            Interlocked.Increment(ref _frameVersion);
        }

        if (_frameSnapshot != null)
        {
            canvas.DrawImage(
                _frameSnapshot,
                new SKRect(0, 0, logicalSize.Width, logicalSize.Height),
                SKSamplingOptions.Default);
        }
        else
        {
            // Emergency fallback if the backing surface could not produce a snapshot.
            canvas.Clear(ThemeManager.Current.Background);
            canvas.Save();
            try
            {
                canvas.Scale(DpiScale, DpiScale);
                _root.PaintAll(canvas);
            }
            finally
            {
                canvas.Restore();
            }
        }

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

        var info = new SKImageInfo(
            targetSize.Width,
            targetSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        _frameSurface = SKSurface.Create(info);
        _framePixelSize = targetSize;
    }

    public void AddLayer(CompositorLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        lock (_layerLock)
        {
            _layers.Add(layer);
            _layers.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        }

        Interlocked.Increment(ref _frameVersion);
    }

    public void RemoveLayer(CompositorLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (layer == null)
        {
            return;
        }

        lock (_layerLock)
        {
            if (!_layers.Remove(layer))
            {
                return;
            }
        }

        Interlocked.Increment(ref _frameVersion);
    }

    public void ClearLayers()
    {
        CompositorLayer[] layers;
        lock (_layerLock)
        {
            layers = _layers.ToArray();
            _layers.Clear();
        }

        if (layers.Length > 0)
        {
            Interlocked.Increment(ref _frameVersion);
        }

        // Layer disposal is arbitrary/re-entrant native/user code. Do not invoke it
        // while holding the collection lock.
        foreach (var layer in layers)
        {
            layer.Dispose();
        }
    }

    private void CompositeLayers(SKCanvas canvas)
    {
        CompositorLayer[] layers;
        lock (_layerLock)
        {
            layers = _layers.ToArray();
        }

        // Render callbacks may be slow or re-enter layer management. The layer lock
        // protects the collection only; it is never held while executing callbacks.
        foreach (var layer in layers)
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            canvas.Save();
            try
            {
                canvas.Scale(DpiScale, DpiScale);

                if (layer.Opacity < 1.0f)
                {
                    using var paint = new SKPaint
                    {
                        Color = SKColors.White.WithAlpha(
                            (byte)(Math.Clamp(layer.Opacity, 0f, 1f) * 255))
                    };
                    layer.Render(canvas, paint);
                }
                else
                {
                    layer.Render(canvas, null);
                }
            }
            finally
            {
                canvas.Restore();
            }
        }
    }

    public void InvalidateRect(SKRect rect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _root.Invalidate();
            return;
        }

        _root.Invalidate(rect);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearLayers();

        _frameSnapshot?.Dispose();
        _frameSnapshot = null;

        _frameSurface?.Dispose();
        _frameSurface = null;
        _framePixelSize = default;
    }
}

/// <summary>
/// A composable host layer for overlays, popups, and other independently rendered UI.
/// </summary>
public class CompositorLayer : IDisposable
{
    public string Name { get; set; }
    public int ZIndex { get; set; }
    public SKRect Bounds { get; set; }
    public float Opacity { get; set; } = 1.0f;
    public bool IsVisible { get; set; } = true;
    public bool IsDirty { get; set; } = true;

    private SKSurface _surface;
    private SKSizeI _surfacePixelSize;
    private readonly Action<SKCanvas> _renderCallback;
    private bool _disposed;

    public CompositorLayer(string name, SKRect bounds, Action<SKCanvas> renderCallback)
    {
        Name = name;
        Bounds = bounds;
        _renderCallback = renderCallback;
    }

    public void Render(SKCanvas canvas, SKPaint paint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);

        if (_renderCallback != null)
        {
            canvas.Save();
            try
            {
                canvas.Translate(Bounds.Left, Bounds.Top);
                var localBounds = new SKRect(0, 0, Bounds.Width, Bounds.Height);
                canvas.ClipRect(localBounds);

                if (paint != null)
                {
                    // Callback-backed layers draw directly into the destination
                    // canvas. Without a save-layer, their opacity paint was ignored
                    // entirely while surface-backed layers respected it. Apply the
                    // group alpha to the callback's whole rendered result.
                    canvas.SaveLayer(localBounds, paint);
                    try
                    {
                        _renderCallback(canvas);
                    }
                    finally
                    {
                        canvas.Restore();
                    }
                }
                else
                {
                    _renderCallback(canvas);
                }
            }
            finally
            {
                canvas.Restore();
            }

            return;
        }

        if (_surface != null)
        {
            using var image = _surface.Snapshot();
            canvas.DrawImage(image, Bounds.Left, Bounds.Top, SKSamplingOptions.Default, paint);
        }
    }

    /// <summary>
    /// Create or resize the offscreen backing surface for this layer.
    /// </summary>
    public void EnsureSurface(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var targetSize = new SKSizeI(Math.Max(1, width), Math.Max(1, height));
        if (_surface != null && _surfacePixelSize == targetSize)
        {
            return;
        }

        _surface?.Dispose();
        _surface = null;

        _surface = SKSurface.Create(new SKImageInfo(targetSize.Width, targetSize.Height));
        _surfacePixelSize = targetSize;
        IsDirty = true;
    }

    public SKCanvas GetCanvas()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _surface?.Canvas;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface?.Dispose();
        _surface = null;
        _surfacePixelSize = default;
    }
}
