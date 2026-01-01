using System;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Theme;
using Silk.NET.Input;
using SkiaSharp;

namespace FenBrowser.Host.Widgets;

public class SwitchWidget : Widget
{
    private bool _isChecked;
    public event Action<bool> CheckedChanged;

    private float _currentKnobPos = 0f; // 0 = unchecked, 1 = checked
    private float _targetKnobPos = 0f;
    private DateTime _lastUpdate = DateTime.Now;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                _targetKnobPos = _isChecked ? 1f : 0f;
                Invalidate();
            }
        }
    }

    public string Label { get; set; } // Optional internal label, though usually external

    public SwitchWidget()
    {
        _currentKnobPos = _isChecked ? 1f : 0f;
        _targetKnobPos = _currentKnobPos;
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(44, 24);
    }

    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            Toggle();
        }
    }

    public void Toggle()
    {
        IsChecked = !IsChecked;
        CheckedChanged?.Invoke(IsChecked);
    }

    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        var rect = new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
        
        // Debug: Log paint position and canvas matrix
        var matrix = canvas.TotalMatrix;
        string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FenBrowser", "click_debug.log");
        System.IO.File.AppendAllText(logPath, $"[SwitchPaint] Bounds={Bounds}, Matrix=({matrix.TransX:F0},{matrix.TransY:F0}) Scale=({matrix.ScaleX:F2},{matrix.ScaleY:F2})\n");
        
        // Update animation
        var now = DateTime.Now;
        float dt = (float)(now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        if (Math.Abs(_currentKnobPos - _targetKnobPos) > 0.01f)
        {
            float speed = 10f; // Animation speed
            if (_currentKnobPos < _targetKnobPos)
                _currentKnobPos = Math.Min(_targetKnobPos, _currentKnobPos + speed * dt);
            else
                _currentKnobPos = Math.Max(_targetKnobPos, _currentKnobPos - speed * dt);
            
            Invalidate(); // Keep animating
        }
        else
        {
            _currentKnobPos = _targetKnobPos;
        }

        // Track
        var startColor = SKColors.Gray;
        var endColor = theme.Accent;
        var r = (byte)(startColor.Red + (endColor.Red - startColor.Red) * _currentKnobPos);
        var g = (byte)(startColor.Green + (endColor.Green - startColor.Green) * _currentKnobPos);
        var b = (byte)(startColor.Blue + (endColor.Blue - startColor.Blue) * _currentKnobPos);
        var trackColor = new SKColor(r, g, b);

        using var trackPaint = new SKPaint
        {
            Color = trackColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        canvas.DrawRoundRect(rect, 12, 12, trackPaint);
        
        // Thumb
        float thumbRadius = 10;
        float thumbPadding = 2;
        float minX = Bounds.Left + thumbRadius + thumbPadding;
        float maxX = Bounds.Right - thumbRadius - thumbPadding;
        float thumbX = minX + (maxX - minX) * _currentKnobPos;
        float thumbY = Bounds.MidY;
        
        using var thumbPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        // Shadow for depth
        using var shadowPaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(50),
            IsAntialias = true
        };
        canvas.DrawCircle(thumbX, thumbY + 1, thumbRadius, shadowPaint);
        
        canvas.DrawCircle(thumbX, thumbY, thumbRadius, thumbPaint);
    }
}
