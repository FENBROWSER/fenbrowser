using System;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Theme;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Host.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Text input widget for settings like URLs and paths.
/// </summary>
public class TextInputWidget : Widget
{
    private string _text = "";
    private int _cursorPosition = 0;
    private int _selectionStart = -1; // -1 means no selection
    private int _selectionEnd = -1;
    private bool _isSelecting = false;
    
    public event Action<string> TextChanged;
    
    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value ?? "";
                _cursorPosition = _text.Length;
                Invalidate();
            }
        }
    }
    
    public string Placeholder { get; set; } = "";
    
    private static SKTypeface _font = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    
    public TextInputWidget()
    {
        Name = "TextInput";
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(250, 32);
    }
    
    public override bool CanFocus => true;
    
    public override void OnFocus()
    {
        base.OnFocus();
        Invalidate();
    }
    
    public override void OnBlur()
    {
        base.OnBlur();
        ClearSelection();
        Invalidate();
    }
    
    public bool HasSelection() => _selectionStart != -1 && _selectionEnd != -1 && _selectionStart != _selectionEnd;

    public void SelectAll()
    {
        _selectionStart = 0;
        _selectionEnd = _text.Length;
        _cursorPosition = _text.Length;
        Invalidate();
    }

    public void ClearSelection()
    {
        _selectionStart = -1;
        _selectionEnd = -1;
        Invalidate();
    }

    public string GetSelectedText()
    {
        if (!HasSelection()) return string.Empty;
        int start = Math.Min(_selectionStart, _selectionEnd);
        int length = Math.Abs(_selectionStart - _selectionEnd);
        return _text.Substring(start, length);
    }

    public void DeleteSelection()
    {
        if (!HasSelection()) return;
        int start = Math.Min(_selectionStart, _selectionEnd);
        int length = Math.Abs(_selectionStart - _selectionEnd);
        _text = _text.Remove(start, length);
        _cursorPosition = start;
        ClearSelection();
        TextChanged?.Invoke(_text);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Background
        using var bgPaint = new SKPaint
        {
            Color = theme.Surface,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(Bounds, 4, 4, bgPaint);
        
        // Border (accent if focused)
        using var borderPaint = new SKPaint
        {
            Color = IsFocused ? theme.Accent : theme.Border,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = IsFocused ? 2 : 1
        };
        canvas.DrawRoundRect(Bounds, 4, 4, borderPaint);
        
        // Text or placeholder
        using var textPaint = new SKPaint
        {
            Color = string.IsNullOrEmpty(_text) ? theme.TextMuted : theme.Text,
            IsAntialias = true,
            TextSize = 14,
            Typeface = _font
        };
        
        string displayText = string.IsNullOrEmpty(_text) ? Placeholder : _text;
        
        // Clip text to bounds
        canvas.Save();
        canvas.ClipRect(new SKRect(Bounds.Left + 8, Bounds.Top, Bounds.Right - 8, Bounds.Bottom));
        // Selection Highlight
        if (IsFocused && HasSelection())
        {
            int selStart = Math.Min(_selectionStart, _selectionEnd);
            int selEnd = Math.Max(_selectionStart, _selectionEnd);
            float xStart = Bounds.Left + 10 + textPaint.MeasureText(_text.Substring(0, selStart));
            float xEnd = Bounds.Left + 10 + textPaint.MeasureText(_text.Substring(0, selEnd));
            
            using var selectionPaint = new SKPaint
            {
                Color = theme.Accent.WithAlpha(80),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(new SKRect(xStart, Bounds.Top + 4, xEnd, Bounds.Bottom - 4), selectionPaint);
        }

        canvas.DrawText(displayText, Bounds.Left + 10, Bounds.MidY + 5, textPaint);
        
        // Cursor
        if (IsFocused && !HasSelection())
        {
            float cursorX = Bounds.Left + 10 + textPaint.MeasureText(_text.Substring(0, _cursorPosition));
            using var cursorPaint = new SKPaint
            {
                Color = theme.Text,
                StrokeWidth = 1
            };
            canvas.DrawLine(cursorX, Bounds.Top + 6, cursorX, Bounds.Bottom - 6, cursorPaint);
        }
        
        canvas.Restore();
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _isSelecting = true;
            InputManager.Instance.SetCapture(this);
            
            // Invalidate existing selection if clicking without shift (simple version)
            ClearSelection();
            
            // Position cursor based on click
            float localX = x - Bounds.Left - 10;
            if (localX <= 0)
            {
                _cursorPosition = 0;
            }
            else
            {
                using var textPaint = new SKPaint
                {
                    TextSize = 14,
                    Typeface = _font
                };

                // Find the character index under the click
                int index = 0;
                float currentWidth = 0;
                for (int i = 1; i <= _text.Length; i++)
                {
                    float width = textPaint.MeasureText(_text.Substring(0, i));
                    if (width > localX)
                    {
                        // Check if it's closer to the current char or previous
                        float prevWidth = i > 1 ? textPaint.MeasureText(_text.Substring(0, i - 1)) : 0;
                        if (localX - prevWidth < width - localX)
                            index = i - 1;
                        else
                            index = i;
                        break;
                    }
                    index = i;
                    currentWidth = width;
                }
                _cursorPosition = index;
                _selectionStart = index; // Anchor for dragging
                _selectionEnd = index;
            }
            Invalidate();
        }
    }

    public override void OnMouseMove(float x, float y)
    {
        if (_isSelecting)
        {
            float localX = x - Bounds.Left - 10;
            int index = CalculateCharacterIndex(localX);
            _selectionEnd = index;
            _cursorPosition = index;
            Invalidate();
        }
    }

    public override void OnMouseUp(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _isSelecting = false;
            InputManager.Instance.ReleaseCapture();
        }
    }

    private int CalculateCharacterIndex(float localX)
    {
        if (localX <= 0) return 0;
        
        using var textPaint = new SKPaint
        {
            TextSize = 14,
            Typeface = _font
        };

        int index = 0;
        for (int i = 1; i <= _text.Length; i++)
        {
            float width = textPaint.MeasureText(_text.Substring(0, i));
            if (width > localX)
            {
                // Find closer boundary
                float prevWidth = textPaint.MeasureText(_text.Substring(0, i - 1));
                if (localX - prevWidth < width - localX)
                    return i - 1;
                else
                    return i;
            }
            index = i;
        }
        return index;
    }
    
    public override void OnTextInput(char c, bool ctrl)
    {
        if (!IsFocused) return;
        
        // Skip control characters as they are handled in OnKeyDown
        if (char.IsControl(c) || ctrl) return;

        if (HasSelection())
        {
            DeleteSelection();
        }

        _text = _text.Insert(_cursorPosition, c.ToString());
        _cursorPosition++;
        TextChanged?.Invoke(_text);
        Invalidate();
    }
    
    public override void OnKeyDown(Key key, bool ctrl, bool shift, bool alt)
    {
        if (!IsFocused) return;
        
        if (ctrl)
        {
            switch (key)
            {
                case Key.A:
                    SelectAll();
                    break;
                case Key.C:
                    var copyText = HasSelection() ? GetSelectedText() : _text;
                    if (!string.IsNullOrEmpty(copyText))
                    {
                        ClipboardHelper.SetText(copyText);
                    }
                    break;
                case Key.V:
                    string clipboardText = ClipboardHelper.GetText();
                    if (!string.IsNullOrEmpty(clipboardText))
                    {
                        if (HasSelection()) DeleteSelection();
                        _text = _text.Insert(_cursorPosition, clipboardText);
                        _cursorPosition += clipboardText.Length;
                        TextChanged?.Invoke(_text);
                        Invalidate();
                    }
                    break;
                case Key.X:
                    var cutText = HasSelection() ? GetSelectedText() : _text;
                    if (!string.IsNullOrEmpty(cutText))
                    {
                        ClipboardHelper.SetText(cutText);
                        if (HasSelection())
                        {
                            DeleteSelection();
                        }
                        else
                        {
                            _text = "";
                            _cursorPosition = 0;
                            TextChanged?.Invoke(_text);
                        }
                        Invalidate();
                    }
                    break;
            }
            return;
        }

        if (shift)
        {
            if (!HasSelection()) _selectionStart = _cursorPosition;
            
            switch (key)
            {
                case Key.Left:
                    if (_cursorPosition > 0) _cursorPosition--;
                    _selectionEnd = _cursorPosition;
                    break;
                case Key.Right:
                    if (_cursorPosition < _text.Length) _cursorPosition++;
                    _selectionEnd = _cursorPosition;
                    break;
                case Key.Home:
                    _cursorPosition = 0;
                    _selectionEnd = _cursorPosition;
                    break;
                case Key.End:
                    _cursorPosition = _text.Length;
                    _selectionEnd = _cursorPosition;
                    break;
            }
            Invalidate();
            return;
        }

        // Normal movement clears selection
        if (key == Key.Left || key == Key.Right || key == Key.Home || key == Key.End)
        {
            ClearSelection();
        }

        switch (key)
        {
            case Key.Backspace:
                if (HasSelection())
                {
                    DeleteSelection();
                    Invalidate();
                }
                else if (_cursorPosition > 0)
                {
                    _text = _text.Remove(_cursorPosition - 1, 1);
                    _cursorPosition--;
                    TextChanged?.Invoke(_text);
                    Invalidate();
                }
                break;
            case Key.Left:
                if (_cursorPosition > 0) _cursorPosition--;
                Invalidate();
                break;
            case Key.Right:
                if (_cursorPosition < _text.Length) _cursorPosition++;
                Invalidate();
                break;
            case Key.Home:
                _cursorPosition = 0;
                Invalidate();
                break;
            case Key.End:
                _cursorPosition = _text.Length;
                Invalidate();
                break;
            case Key.Delete:
                if (_cursorPosition < _text.Length)
                {
                    _text = _text.Remove(_cursorPosition, 1);
                    TextChanged?.Invoke(_text);
                    Invalidate();
                }
                break;
        }
    }
}
