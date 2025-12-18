using SkiaSharp;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Text input widget for URL entry.
/// Supports text editing, selection, and caret.
/// </summary>
public class AddressBarWidget : Widget
{
    private string _text = "";
    private int _caretPosition = 0;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private float _scrollOffset = 0;
    private DateTime _lastBlink = DateTime.Now;
    private bool _caretVisible = true;
    
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            _caretPosition = Math.Min(_caretPosition, _text.Length);
            ClearSelection();
            Invalidate();
        }
    }
    
    public string Placeholder { get; set; } = "Enter URL...";
    
    public event Action<string> NavigateRequested;
    
    // Styling
    public SKColor BackgroundColor { get; set; } = SKColors.White;
    public SKColor BorderColor { get; set; } = new SKColor(180, 180, 180);
    public SKColor FocusBorderColor { get; set; } = new SKColor(66, 133, 244);
    public SKColor TextColor { get; set; } = SKColors.Black;
    public SKColor PlaceholderColor { get; set; } = new SKColor(160, 160, 160);
    public SKColor SelectionColor { get; set; } = new SKColor(66, 133, 244, 80);
    public SKColor CaretColor { get; set; } = SKColors.Black;
    public float FontSize { get; set; } = 14;
    public float Padding { get; set; } = 8;
    
    private SKPaint _textPaint;
    
    public AddressBarWidget()
    {
        _textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = FontSize,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
        };
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Update text paint
        _textPaint.TextSize = FontSize;
        _textPaint.Color = TextColor;
        
        // Draw background
        using var bgPaint = new SKPaint
        {
            Color = BackgroundColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        var rect = new SKRoundRect(Bounds, 4);
        canvas.DrawRoundRect(rect, bgPaint);
        
        // Draw border
        using var borderPaint = new SKPaint
        {
            Color = IsFocused ? FocusBorderColor : BorderColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = IsFocused ? 2 : 1
        };
        canvas.DrawRoundRect(rect, borderPaint);
        
        // Set clip for text area
        canvas.Save();
        canvas.ClipRect(new SKRect(Bounds.Left + Padding, Bounds.Top, Bounds.Right - Padding, Bounds.Bottom));
        
        float textY = Bounds.MidY - (_textPaint.FontMetrics.Ascent + _textPaint.FontMetrics.Descent) / 2;
        float textX = Bounds.Left + Padding - _scrollOffset;
        
        // Draw selection
        if (HasSelection())
        {
            int start = Math.Min(_selectionStart, _selectionEnd);
            int end = Math.Max(_selectionStart, _selectionEnd);
            
            float selStartX = textX + MeasureText(_text.Substring(0, start));
            float selEndX = textX + MeasureText(_text.Substring(0, end));
            
            using var selPaint = new SKPaint { Color = SelectionColor };
            canvas.DrawRect(selStartX, Bounds.Top + 4, selEndX - selStartX, Bounds.Height - 8, selPaint);
        }
        
        // Draw text or placeholder
        if (string.IsNullOrEmpty(_text) && !IsFocused)
        {
            _textPaint.Color = PlaceholderColor;
            canvas.DrawText(Placeholder, textX, textY, _textPaint);
        }
        else
        {
            _textPaint.Color = TextColor;
            canvas.DrawText(_text, textX, textY, _textPaint);
        }
        
        // Draw caret
        if (IsFocused && _caretVisible)
        {
            float caretX = textX + MeasureText(_text.Substring(0, _caretPosition));
            using var caretPaint = new SKPaint
            {
                Color = CaretColor,
                StrokeWidth = 1.5f,
                IsAntialias = true
            };
            canvas.DrawLine(caretX, Bounds.Top + 6, caretX, Bounds.Bottom - 6, caretPaint);
        }
        
        canvas.Restore();
        
        // Blink caret
        if (IsFocused && (DateTime.Now - _lastBlink).TotalMilliseconds > 500)
        {
            _caretVisible = !_caretVisible;
            _lastBlink = DateTime.Now;
        }
    }
    
    private float MeasureText(string text)
    {
        return _textPaint.MeasureText(text);
    }
    
    private bool HasSelection()
    {
        return _selectionStart >= 0 && _selectionEnd >= 0 && _selectionStart != _selectionEnd;
    }
    
    private void ClearSelection()
    {
        _selectionStart = -1;
        _selectionEnd = -1;
    }
    
    private string GetSelectedText()
    {
        if (!HasSelection()) return "";
        int start = Math.Min(_selectionStart, _selectionEnd);
        int end = Math.Max(_selectionStart, _selectionEnd);
        return _text.Substring(start, end - start);
    }
    
    private void DeleteSelection()
    {
        if (!HasSelection()) return;
        int start = Math.Min(_selectionStart, _selectionEnd);
        int end = Math.Max(_selectionStart, _selectionEnd);
        _text = _text.Remove(start, end - start);
        _caretPosition = start;
        ClearSelection();
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            RequestFocus();
            
            // Calculate caret position from click
            float textX = Bounds.Left + Padding - _scrollOffset;
            float clickOffset = x - textX;
            
            _caretPosition = GetCharIndexAtX(clickOffset);
            _selectionStart = _caretPosition;
            _selectionEnd = _caretPosition;
            _caretVisible = true;
            Invalidate();
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        // Extend selection if dragging
        // (Left button held - would need mouse state tracking)
    }
    
    private int GetCharIndexAtX(float x)
    {
        if (string.IsNullOrEmpty(_text)) return 0;
        
        for (int i = 0; i <= _text.Length; i++)
        {
            float charX = MeasureText(_text.Substring(0, i));
            if (charX > x) return Math.Max(0, i);
        }
        return _text.Length;
    }
    
    public override void OnKeyDown(Key key)
    {
        _caretVisible = true;
        _lastBlink = DateTime.Now;
        
        switch (key)
        {
            case Key.Left:
                if (_caretPosition > 0) _caretPosition--;
                ClearSelection();
                break;
                
            case Key.Right:
                if (_caretPosition < _text.Length) _caretPosition++;
                ClearSelection();
                break;
                
            case Key.Home:
                _caretPosition = 0;
                ClearSelection();
                break;
                
            case Key.End:
                _caretPosition = _text.Length;
                ClearSelection();
                break;
                
            case Key.Backspace:
                if (HasSelection())
                {
                    DeleteSelection();
                }
                else if (_caretPosition > 0)
                {
                    _text = _text.Remove(_caretPosition - 1, 1);
                    _caretPosition--;
                }
                break;
                
            case Key.Delete:
                if (HasSelection())
                {
                    DeleteSelection();
                }
                else if (_caretPosition < _text.Length)
                {
                    _text = _text.Remove(_caretPosition, 1);
                }
                break;
                
            case Key.Enter:
            case Key.KeypadEnter:
                NavigateRequested?.Invoke(_text);
                break;
                
            case Key.A:
                // Ctrl+A - Select All (would need modifier check)
                break;
        }
        
        EnsureCaretVisible();
        Invalidate();
    }
    
    public override void OnTextInput(char c)
    {
        if (char.IsControl(c)) return;
        
        _caretVisible = true;
        _lastBlink = DateTime.Now;
        
        if (HasSelection())
        {
            DeleteSelection();
        }
        
        _text = _text.Insert(_caretPosition, c.ToString());
        _caretPosition++;
        
        EnsureCaretVisible();
        Invalidate();
    }
    
    private void EnsureCaretVisible()
    {
        float caretX = MeasureText(_text.Substring(0, _caretPosition));
        float visibleWidth = Bounds.Width - Padding * 2;
        
        if (caretX - _scrollOffset > visibleWidth)
        {
            _scrollOffset = caretX - visibleWidth + 10;
        }
        else if (caretX - _scrollOffset < 0)
        {
            _scrollOffset = Math.Max(0, caretX - 10);
        }
    }
    
    public void SelectAll()
    {
        _selectionStart = 0;
        _selectionEnd = _text.Length;
        _caretPosition = _text.Length;
        Invalidate();
    }
}
