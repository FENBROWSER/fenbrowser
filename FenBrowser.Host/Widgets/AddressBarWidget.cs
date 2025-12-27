using SkiaSharp;
using Silk.NET.Input;
using Topten.RichTextKit;
using FenBrowser.Host.Theme;
using FenBrowser.Host.Input;

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
            if (_text != value)
            {
                _text = value ?? "";
                _caretPosition = Math.Min(_caretPosition, _text.Length);
                ClearSelection();
                InvalidateTextLayout();
            }
        }
    }
    
    public string Placeholder { get; set; } = "Enter URL...";
    
    public event Action<string> NavigateRequested;
    
    // Styling
    // Styling (optional overrides)
    public SKColor? BackgroundColor { get; set; }
    public SKColor? BorderColor { get; set; }
    public SKColor? FocusBorderColor { get; set; }
    public SKColor? TextColor { get; set; }
    public SKColor? PlaceholderColor { get; set; }
    public SKColor? SelectionColor { get; set; }
    public SKColor? CaretColor { get; set; }
    public float FontSize { get; set; } = 14;
    public float Padding { get; set; } = 10; 
    public float IconPadding { get; set; } = 34; // Extra left padding for Shield Icon
    
    // Icons
    private SKPath _shieldPath = SKPath.ParseSvgPathData("M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z"); // Material/Fluent Shield

    private TextBlock _textBlock;
    private bool _isTextLayoutDirty = true;
    private Style _textStyle;
    private Style _placeholderStyle;
    
    public AddressBarWidget()
    {
        Role = WidgetRole.Edit;
        Name = "Address Bar";
        HelpText = "Enter URL to navigate";
        
        _textStyle = new Style()
        {
            FontFamily = "Segoe UI",
            FontSize = FontSize,
            TextColor = SKColors.Black,
        };
        
        _placeholderStyle = new Style()
        {
            FontFamily = "Segoe UI",
            FontSize = FontSize,
            TextColor = SKColors.Gray,
        };
    }
    
    private void InvalidateTextLayout()
    {
        _isTextLayoutDirty = true;
        Invalidate();
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(availableSpace.Width, 32);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        // Leaf widget
    }
    
    public override void Paint(SkiaSharp.SKCanvas canvas)
    {
        EnsureTextBlock();
        var theme = ThemeManager.Current;
        
        var localBounds = new SKRect(0, 0, Bounds.Width, Bounds.Height);
        
        canvas.Save();
        canvas.Translate(Bounds.Left, Bounds.Top);
        
        // Background
        using var bgPaint = new SKPaint { Color = BackgroundColor ?? theme.Background, IsAntialias = true };
        canvas.DrawRoundRect(localBounds, 8, 8, bgPaint);
        
        // Focus Glow (Glassmorphism effect)
        if (IsFocused)
        {
            using var glowPaint = new SKPaint
            {
                Color = theme.Accent.WithAlpha(40),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
            };
            var glowRect = localBounds;
            glowRect.Inflate(2, 2);
            canvas.DrawRoundRect(glowRect, 10, 10, glowPaint);
        }
        
        // Border
        using var borderPaint = new SKPaint
        {
            Color = IsFocused ? theme.Accent : (BorderColor ?? theme.Border),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = IsFocused ? 1.5f : 1
        };
        canvas.DrawRoundRect(localBounds, 8, 8, borderPaint);
        
        // Content Area Clip
        canvas.Save();
        // Left padding includes space for Icon
        canvas.ClipRect(new SKRect(IconPadding, 0, localBounds.Width - Padding, localBounds.Height));
        
        // Vertical center the text block
        float textY = (localBounds.Height - _textBlock.MeasuredHeight) / 2;
        canvas.Translate(IconPadding - _scrollOffset, textY);
        
        // Selection
        if (HasSelection())
        {
            int start = Math.Min(_selectionStart, _selectionEnd);
            int end = Math.Max(_selectionStart, _selectionEnd);
            
            var caretStart = _textBlock.GetCaretInfo(new CaretPosition(start));
            var caretEnd = _textBlock.GetCaretInfo(new CaretPosition(end));
            
            float x1 = caretStart.CaretRectangle.Left;
            float x2 = caretEnd.CaretRectangle.Left;
            
            using var selPaint = new SKPaint { Color = SelectionColor ?? theme.AccentMuted };
            canvas.DrawRect(x1, 0, x2 - x1, _textBlock.MeasuredHeight, selPaint);
        }
        
        // Paint Text
        _textBlock.Paint(canvas);
        
        // Caret
        if (IsFocused && _caretVisible)
        {
            var caretInfo = _textBlock.GetCaretInfo(new CaretPosition(_caretPosition));
            var caretRect = caretInfo.CaretRectangle;
            
            using var caretPaint = new SKPaint { Color = CaretColor ?? theme.Accent, StrokeWidth = 2f };
            canvas.DrawLine(caretRect.Left, caretRect.Top, caretRect.Left, caretRect.Bottom, caretPaint);
        }
        
        canvas.Restore();
        canvas.Restore();
        
        // Draw Shield Icon (Left)
        // Position: Left aligned, vertically centered
        canvas.Save();
        float iconSize = 16;
        float iconX = 10;
        float iconY = (Bounds.Height - iconSize) / 2;
        
        canvas.Translate(Bounds.Left + iconX, Bounds.Top + iconY);
        float scale = iconSize / 24f; // Assuming 24x24 viewbox
        canvas.Scale(scale);
        
        using var iconPaint = new SKPaint 
        { 
            Color = theme.Text, // Or Accent if secure? Let's use generic Text for now, or Green if HTTPS?
            IsAntialias = true, 
            Style = SKPaintStyle.Fill 
        };
        // If text starts with https, maybe color it green?
        if (_text.StartsWith("https://")) iconPaint.Color = SKColors.ForestGreen;
        
        canvas.DrawPath(_shieldPath, iconPaint);
        canvas.Restore();
        
        // Blink
        if (IsFocused && (DateTime.Now - _lastBlink).TotalMilliseconds > 500)
        {
            _caretVisible = !_caretVisible;
            _lastBlink = DateTime.Now;
            Invalidate();
        }
    }
    
    private void EnsureTextBlock()
    {
        if (!_isTextLayoutDirty && _textBlock != null) return;
        
        _textBlock = new TextBlock();
        _textBlock.MaxWidth = float.PositiveInfinity;
        
        _textStyle.FontSize = FontSize;
        _textStyle.TextColor = TextColor ?? ThemeManager.Current.Text;
        
        _placeholderStyle.FontSize = FontSize;
        _placeholderStyle.TextColor = PlaceholderColor ?? ThemeManager.Current.TextMuted;
        
        string display = string.IsNullOrEmpty(_text) && !IsFocused ? Placeholder : _text;
        var style = string.IsNullOrEmpty(_text) && !IsFocused ? _placeholderStyle : _textStyle;
        
        _textBlock.AddText(display, style);
        _textBlock.Layout();
        
        _isTextLayoutDirty = false;
    }
    
    private float MeasureText(string text)
    {
        // Old method, use Paragraph instead
        return 0;
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
        // Force synchronous update
        _isTextLayoutDirty = true;
        EnsureTextBlock();
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            RequestFocus();
            EnsureTextBlock();
            
            // Calculate caret position from click (relative to paragraph start)
            float clickOffset = x - (Bounds.Left + IconPadding - _scrollOffset);
            

            // Fix: If text is empty (showing placeholder), caret must be at 0.
            // If we calculate index from Placeholder text (because IsFocused is false yet), 
            // we get an index > 0 which is invalid for _text (which is empty).
            if (string.IsNullOrEmpty(_text))
            {
                _caretPosition = 0;
            }
            else
            {
                _caretPosition = GetCharIndexAtX(clickOffset);
            }

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
        if (_textBlock == null) return 0;
        var hit = _textBlock.HitTest(x, 0);
        return hit.ClosestCodePointIndex;
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
                    InvalidateTextLayout();
                }
                else if (_caretPosition > 0)
                {
                    _text = _text.Remove(_caretPosition - 1, 1);
                    _caretPosition--;
                    // Force update
                    _isTextLayoutDirty = true;
                    EnsureTextBlock();
                }
                break;
                
            case Key.Delete:
                if (HasSelection())
                {
                    DeleteSelection();
                    InvalidateTextLayout();
                }
                else if (_caretPosition < _text.Length)
                {
                    _text = _text.Remove(_caretPosition, 1);
                    // Force update
                    _isTextLayoutDirty = true;
                    EnsureTextBlock();
                }
                break;
                
            case Key.Enter:
            case Key.KeypadEnter:
                NavigateRequested?.Invoke(_text);
                break;
                
            case Key.A:
                // Ctrl+A - Select All
                if (KeyboardDispatcher.Instance.IsCtrlPressed)
                {
                    SelectAll();
                }
                break;
                
            case Key.C:
                // Ctrl+C - Copy
                if (KeyboardDispatcher.Instance.IsCtrlPressed && HasSelection())
                {
                    var selectedText = GetSelectedText();
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        ClipboardHelper.SetText(selectedText);
                    }
                }
                break;
                
            case Key.X:
                // Ctrl+X - Cut
                if (KeyboardDispatcher.Instance.IsCtrlPressed && HasSelection())
                {
                    var selectedText = GetSelectedText();
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        ClipboardHelper.SetText(selectedText);
                        DeleteSelection();
                        InvalidateTextLayout();
                    }
                }
                break;
                
            case Key.V:
                // Ctrl+V - Paste
                if (KeyboardDispatcher.Instance.IsCtrlPressed)
                {
                    var clipboardText = ClipboardHelper.GetText();
                    if (!string.IsNullOrEmpty(clipboardText))
                    {
                        // Remove any newlines from pasted text
                        clipboardText = clipboardText.Replace("\r\n", "").Replace("\n", "").Replace("\r", "");
                        
                        if (HasSelection())
                        {
                            DeleteSelection();
                        }
                        
                        _text = _text.Insert(_caretPosition, clipboardText);
                        _caretPosition += clipboardText.Length;
                        
                        _isTextLayoutDirty = true;
                        EnsureTextBlock();
                        EnsureCaretVisible();
                    }
                }
                break;
        }
        
        EnsureCaretVisible();
        Invalidate();
    }

    
    public override void OnTextInput(char c)
    {
        // 1 = SOH (Start of Heading), commonly sent by Ctrl+A
        // 127 = Delete
        // < 32 = Control characters
        if (char.IsControl(c) || c == 1 || KeyboardDispatcher.Instance.IsCtrlPressed) return;
        
        _caretVisible = true;
        _lastBlink = DateTime.Now;
        
        if (HasSelection())
        {
            DeleteSelection();
        }
        
        // Safety check
        if (_caretPosition > _text.Length) _caretPosition = _text.Length;
        if (_caretPosition < 0) _caretPosition = 0;
        
        _text = _text.Insert(_caretPosition, c.ToString());
        _caretPosition++;
        
        // Force synchronous update to prevent caret crash
        _isTextLayoutDirty = true;
        EnsureTextBlock();
        
        EnsureCaretVisible();
        Invalidate();
    }
    
    private void EnsureCaretVisible()
    {
        EnsureTextBlock();
        
        // Safety check
        if (_caretPosition > _text.Length || (_textBlock.MeasuredHeight == 0 && _text.Length == 0)) 
        {
             // If mismatch or empty
             if (_caretPosition > _text.Length) _caretPosition = _text.Length;
        }

        var caretInfo = _textBlock.GetCaretInfo(new CaretPosition(_caretPosition));
        float caretX = caretInfo.CaretRectangle.Left;
        
        float visibleWidth = Bounds.Width - IconPadding - Padding;
        
        if (caretX - _scrollOffset > visibleWidth)
        {
            _scrollOffset = caretX - visibleWidth + 20;
        }
        else if (caretX - _scrollOffset < 0)
        {
            _scrollOffset = Math.Max(0, caretX - 20);
        }
    }
    
    public void SelectAll()
    {
        _selectionStart = 0;
        _selectionEnd = _text.Length;
        _selectionStart = 0;
        _selectionEnd = _text.Length;
        _caretPosition = _text.Length;
        // Force synchronous update
        _isTextLayoutDirty = true;
        EnsureTextBlock();
        Invalidate();
    }

    public override bool CanFocus => true;
}
