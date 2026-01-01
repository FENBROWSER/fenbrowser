using System;
using SkiaSharp;
using Silk.NET.Input;
using FenBrowser.DevTools.Core;
using FenBrowser.Host.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Native widget wrapper for DevToolsController.
/// Handles layout docking and vertical resizing.
/// </summary>
public class DevToolsWidget : Widget
{
    private readonly DevToolsController _controller;
    private bool _isResizingHeight = false;
    private float _startDragY;
    private float _startHeight;

    public DevToolsWidget(DevToolsController controller)
    {
        _controller = controller;
        _controller.Invalidated += () => Invalidate();
        
        // When visibility changes, we need a layout pass
        _controller.LayoutChanged += () => InvalidateLayout();
        
        Name = "DevTools";
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        if (!_controller.IsVisible) return SKSize.Empty;
        return new SKSize(availableSpace.Width, _controller.Height);
    }

    public override void Paint(SKCanvas canvas)
    {
        if (!_controller.IsVisible) return;
        _controller.Paint(canvas, Bounds);
        
        // Optional: Draw a resize handle indicator
        if (Math.Abs(_controller.Height - _controller.MinHeight) > 1) 
        {
             // We could draw a subtle line or dots here if needed
        }
    }

    public override void OnMouseMove(float x, float y)
    {
        if (!_controller.IsVisible) return;

        bool isNearTop = Math.Abs(y - Bounds.Top) <= 5;

        if (_isResizingHeight)
        {
            float delta = _startDragY - y;
            _controller.Height = Math.Clamp(_startHeight + delta, _controller.MinHeight, _controller.MaxHeight);
            InvalidateLayout();
            
            // Map to host cursor
            var mouse = InputManager.Instance.Mouse; // We need to expose this safely
            if (mouse != null)
                CursorManager.UpdateCursorFromDevTools(mouse, FenBrowser.DevTools.Core.CursorType.VerticalResize);
            return;
        }

        if (isNearTop)
        {
            var mouse = InputManager.Instance.Mouse;
            if (mouse != null)
                CursorManager.UpdateCursorFromDevTools(mouse, FenBrowser.DevTools.Core.CursorType.VerticalResize);
            return;
        }
        else if (!_isResizingHeight)
        {
            // Reset if we were showing resize cursor but no longer near top
            var mouse = InputManager.Instance.Mouse;
            if (mouse != null)
                CursorManager.ResetCursor(mouse);
        }

        _controller.OnMouseMove(x, y);
    }

    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (!_controller.IsVisible) return;

        if (button == MouseButton.Left && Math.Abs(y - Bounds.Top) <= 5)
        {
            _isResizingHeight = true;
            _startDragY = y;
            _startHeight = _controller.Height;
            InputManager.Instance.SetCapture(this);
            return;
        }

        _controller.OnMouseDown(x, y, button == MouseButton.Right);
    }

    public override void OnMouseUp(float x, float y, MouseButton button)
    {
        if (_isResizingHeight)
        {
            _isResizingHeight = false;
            InputManager.Instance.ReleaseCapture();
            
            // Reset cursor after resize
            var mouse = InputManager.Instance.Mouse;
            if (mouse != null)
                CursorManager.ResetCursor(mouse);
            return;
        }
        
        _controller.OnMouseUp(x, y);
    }
}
