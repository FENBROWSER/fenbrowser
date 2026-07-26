using FenBrowser.Host.Theme;
using SkiaSharp;
using Silk.NET.Input;
using System;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Type of JavaScript modal dialog.
/// </summary>
public enum JsDialogType
{
    /// <summary>window.alert() — message + OK button.</summary>
    Alert,
    /// <summary>window.confirm() — message + OK / Cancel buttons.</summary>
    Confirm,
    /// <summary>window.prompt() — message + text input + OK / Cancel buttons.</summary>
    Prompt
}

/// <summary>
/// Full-window overlay widget that renders a JavaScript modal dialog
/// (alert, confirm, prompt).  Designed to be used via RootWidget.SetOverlay
/// so it covers the entire viewport and blocks input to content beneath.
///
/// Thread safety: this widget is created and painted on the UI thread.
/// The Completed event signals the blocked JS worker thread when the user
/// dismisses the dialog.
/// </summary>
public sealed class JavascriptDialogWidget : Widget
{
    private const float DialogWidth = 440f;
    private const float DialogMinHeight = 160f;
    private const float DialogMaxHeightRatio = 0.80f;
    private const float CornerRadius = 12f;
    private const float Padding = 24f;
    private const float ButtonHeight = 36f;
    private const float ButtonMinWidth = 90f;
    private const float InputHeight = 34f;
    private const float ShadowBlur = 12f;
    private const float TitleBarHeight = 36f;

    private readonly JsDialogType _dialogType;
    private readonly string _message;
    private readonly string _defaultValue;

    // Interaction state
    private bool _okHovered;
    private bool _cancelHovered;
    private bool _okPressed;
    private bool _cancelPressed;
    private string _inputText;
    private bool _inputFocused;
    private int _inputCursorPos;

    // Position tracking for hit testing
    private SKRect _dialogBoxRect;
    private SKRect _okButtonRect;
    private SKRect _cancelButtonRect;
    private SKRect _inputRect;

    /// <summary>Fired on the UI thread when the user dismisses the dialog.</summary>
    public event Action<object> Completed;

    public JavascriptDialogWidget(JsDialogType type, string message, string defaultValue = null)
    {
        _dialogType = type;
        _message = message ?? string.Empty;
        _defaultValue = defaultValue ?? string.Empty;
        _inputText = _defaultValue;
        _inputCursorPos = _inputText.Length;
        IsVisible = true;
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Overlay fills the entire available space so backdrop covers everything.
        return availableSpace;
    }

    protected override void OnArrange(SKRect finalRect)
    {
        Bounds = finalRect;

        float maxDialogHeight = finalRect.Height * DialogMaxHeightRatio;
        float dialogHeight = Math.Min(ComputeDialogHeight(finalRect.Width - Padding * 2), maxDialogHeight);

        float dialogX = finalRect.Left + (finalRect.Width - DialogWidth) / 2f;
        float dialogY = finalRect.Top + (finalRect.Height - dialogHeight) / 2f;

        _dialogBoxRect = new SKRect(dialogX, dialogY, dialogX + DialogWidth, dialogY + dialogHeight);

        // Position buttons
        float buttonY = _dialogBoxRect.Bottom - Padding - ButtonHeight;
        float okX, cancelX;

        if (_dialogType == JsDialogType.Alert)
        {
            // OK centered
            okX = _dialogBoxRect.MidX - ButtonMinWidth / 2f;
            _okButtonRect = new SKRect(okX, buttonY, okX + ButtonMinWidth, buttonY + ButtonHeight);
            _cancelButtonRect = SKRect.Empty;
        }
        else
        {
            // OK right, Cancel left
            okX = _dialogBoxRect.Right - Padding - ButtonMinWidth;
            cancelX = okX - ButtonMinWidth - 12f;
            _okButtonRect = new SKRect(okX, buttonY, okX + ButtonMinWidth, buttonY + ButtonHeight);
            _cancelButtonRect = new SKRect(cancelX, buttonY, cancelX + ButtonMinWidth, buttonY + ButtonHeight);
        }

        // Position input field (prompt only)
        if (_dialogType == JsDialogType.Prompt)
        {
            float inputY = buttonY - InputHeight - 10f;
            _inputRect = new SKRect(
                _dialogBoxRect.Left + Padding,
                inputY,
                _dialogBoxRect.Right - Padding,
                inputY + InputHeight);
        }
        else
        {
            _inputRect = SKRect.Empty;
        }
    }

    private float ComputeDialogHeight(float availableWidth)
    {
        // Estimate text height with wrapping
        float textAreaWidth = DialogWidth - Padding * 2;
        float messageHeight = MeasureTextHeight(_message, textAreaWidth, 14f);
        float titleHeight = 20f;

        float height = Padding + titleHeight + 10f + messageHeight + 20f + ButtonHeight + Padding;

        if (_dialogType == JsDialogType.Prompt)
        {
            height += InputHeight + 10f;
        }

        return Math.Max(DialogMinHeight, height);
    }

    private static float MeasureTextHeight(string text, float maxWidth, float textSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), textSize);

        // Simple approximation: count lines by measuring each word
        float lineWidth = 0f;
        float lineHeight = font.Spacing;
        int lines = 1;
        var words = text.Split(' ');

        foreach (var word in words)
        {
            float wordWidth = font.MeasureText(word);
            if (lineWidth + wordWidth > maxWidth && lineWidth > 0)
            {
                lines++;
                lineWidth = wordWidth + font.MeasureText(" ");
            }
            else
            {
                lineWidth += wordWidth + font.MeasureText(" ");
            }
        }

        return lines * lineHeight;
    }

    public override void Paint(SKCanvas canvas)
    {
        if (!IsVisible) return;

        var theme = ThemeManager.Current;

        // ── Backdrop ──
        using var backdropPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            IsAntialias = false
        };
        canvas.DrawRect(Bounds, backdropPaint);

        // ── Dialog shadow ──
        using var shadowPaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(80),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ShadowBlur)
        };
        var shadowRect = new SKRect(
            _dialogBoxRect.Left + 3, _dialogBoxRect.Top + 3,
            _dialogBoxRect.Right + 3, _dialogBoxRect.Bottom + 3);
        canvas.DrawRoundRect(shadowRect, CornerRadius, CornerRadius, shadowPaint);

        // ── Dialog background ──
        SKColor bgColor = ThemeManager.IsDark
            ? new SKColor(45, 45, 50)
            : SKColors.White;
        using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = true };
        canvas.DrawRoundRect(_dialogBoxRect, CornerRadius, CornerRadius, bgPaint);

        // ── Dialog border ──
        using var borderPaint = new SKPaint
        {
            Color = theme.Border,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };
        canvas.DrawRoundRect(_dialogBoxRect, CornerRadius, CornerRadius, borderPaint);

        // ── Title bar ──
        string title = _dialogType switch
        {
            JsDialogType.Alert => "Alert",
            JsDialogType.Confirm => "Confirm",
            JsDialogType.Prompt => "Prompt",
            _ => "Dialog"
        };

        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 14f);
        using var titlePaint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true
        };
        canvas.DrawText(title, _dialogBoxRect.Left + Padding, _dialogBoxRect.Top + Padding + 14f, SKTextAlign.Left, titleFont, titlePaint);

        // ── Message text ──
        float textY = _dialogBoxRect.Top + Padding + 36f;
        float textAreaWidth = _dialogBoxRect.Width - Padding * 2;
        DrawWrappedText(canvas, _message, _dialogBoxRect.Left + Padding, textY,
            textAreaWidth, 14f, theme.Text);

        // ── Input field (prompt only) ──
        if (_dialogType == JsDialogType.Prompt)
        {
            PaintInputField(canvas, theme);
        }

        // ── Buttons ──
        PaintButton(canvas, _okButtonRect, "OK", _okHovered, _okPressed, isPrimary: true);

        if (_dialogType != JsDialogType.Alert)
        {
            PaintButton(canvas, _cancelButtonRect, "Cancel", _cancelHovered, _cancelPressed, isPrimary: false);
        }
    }

    private void PaintInputField(SKCanvas canvas, Theme.Theme theme)
    {
        // Input background
        var inputBg = ThemeManager.IsDark ? new SKColor(30, 30, 35) : new SKColor(243, 244, 246);
        using var inputBgPaint = new SKPaint { Color = inputBg, IsAntialias = true };
        canvas.DrawRoundRect(_inputRect, 6, 6, inputBgPaint);

        // Input border
        var borderColor = _inputFocused ? theme.Accent : theme.Border;
        using var inputBorderPaint = new SKPaint
        {
            Color = borderColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _inputFocused ? 2f : 1f
        };
        canvas.DrawRoundRect(_inputRect, 6, 6, inputBorderPaint);

        // Input text
        if (!string.IsNullOrEmpty(_inputText))
        {
            using var inputTextFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14f);
            using var textPaint = new SKPaint
            {
                Color = theme.Text,
                IsAntialias = true
            };
            canvas.DrawText(_inputText, _inputRect.Left + 8f, _inputRect.MidY + 5f, SKTextAlign.Left, inputTextFont, textPaint);
        }

        // Cursor
        if (_inputFocused)
        {
            float cursorX = _inputRect.Left + 8f;
            if (!string.IsNullOrEmpty(_inputText))
            {
                using var measureFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14f);
                cursorX += measureFont.MeasureText(_inputText.Substring(0,
                    Math.Min(_inputCursorPos, _inputText.Length)));
            }

            var cursorRect = new SKRect(cursorX, _inputRect.Top + 6f,
                cursorX + 1.5f, _inputRect.Bottom - 6f);
            using var cursorPaint = new SKPaint { Color = theme.Text, IsAntialias = false };
            canvas.DrawRect(cursorRect, cursorPaint);
        }
    }

    private void PaintButton(SKCanvas canvas, SKRect rect, string label,
        bool hovered, bool pressed, bool isPrimary)
    {
        if (rect == SKRect.Empty) return;

        var theme = ThemeManager.Current;

        SKColor bg;
        if (pressed)
        {
            bg = isPrimary ? new SKColor(30, 80, 180) : new SKColor(180, 180, 180);
        }
        else if (hovered)
        {
            bg = isPrimary ? new SKColor(50, 110, 220) : new SKColor(210, 210, 210);
        }
        else
        {
            bg = isPrimary ? theme.Accent : (ThemeManager.IsDark ? new SKColor(70, 70, 75) : new SKColor(229, 231, 235));
        }

        using var bgPaint = new SKPaint { Color = bg, IsAntialias = true };
        canvas.DrawRoundRect(rect, 6, 6, bgPaint);

        var textColor = isPrimary ? SKColors.White : theme.Text;
        using var textFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13f);
        using var textPaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true
        };
        float labelW = textFont.MeasureText(label);
        canvas.DrawText(label, rect.MidX - labelW / 2, rect.MidY + 5f, SKTextAlign.Left, textFont, textPaint);
    }

    private void DrawWrappedText(SKCanvas canvas, string text, float x, float y,
        float maxWidth, float textSize, SKColor color)
    {
        if (string.IsNullOrEmpty(text)) return;

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), textSize);
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true
        };

        float lineHeight = font.Spacing;
        float currentX = x;
        float currentY = y + font.Metrics.Ascent * -1; // baseline

        var words = text.Split(' ');
        foreach (var word in words)
        {
            float wordWidth = font.MeasureText(word);
            float spaceWidth = font.MeasureText(" ");

            if (currentX + wordWidth > x + maxWidth && currentX > x)
            {
                currentY += lineHeight;
                currentX = x;
            }

            canvas.DrawText(word, currentX, currentY, SKTextAlign.Left, font, paint);
            currentX += wordWidth + spaceWidth;
        }
    }

    // ── Input handling ──

    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button != MouseButton.Left) return;

        if (_okButtonRect.Contains(x, y))
        {
            _okPressed = true;
            Invalidate();
            return;
        }

        if (_dialogType != JsDialogType.Alert && _cancelButtonRect.Contains(x, y))
        {
            _cancelPressed = true;
            Invalidate();
            return;
        }

        if (_dialogType == JsDialogType.Prompt && _inputRect.Contains(x, y))
        {
            _inputFocused = true;
            Invalidate();
            return;
        }

        _inputFocused = false;
        _okPressed = false;
        _cancelPressed = false;

        // Click outside dialog box → dismiss with cancel behavior
        if (!_dialogBoxRect.Contains(x, y))
        {
            DismissWithCancel();
        }
    }

    public override void OnMouseUp(float x, float y, MouseButton button)
    {
        if (button != MouseButton.Left) return;

        bool wasOkPressed = _okPressed;
        bool wasCancelPressed = _cancelPressed;
        _okPressed = false;
        _cancelPressed = false;

        if (wasOkPressed && _okButtonRect.Contains(x, y))
        {
            DismissWithOk();
            return;
        }

        if (wasCancelPressed && _cancelButtonRect.Contains(x, y))
        {
            DismissWithCancel();
            return;
        }

        Invalidate();
    }

    public override void OnMouseMove(float x, float y)
    {
        bool newOkHover = _okButtonRect.Contains(x, y);
        bool newCancelHover = _cancelButtonRect.Contains(x, y);

        if (newOkHover != _okHovered || newCancelHover != _cancelHovered)
        {
            _okHovered = newOkHover;
            _cancelHovered = newCancelHover;
            Invalidate();
        }
    }

    public override void OnKeyDown(Key key, bool ctrl, bool shift, bool alt)
    {
        switch (key)
        {
            case Key.Enter:
            case Key.KeypadEnter:
                DismissWithOk();
                break;

            case Key.Escape:
                DismissWithCancel();
                break;

            default:
                if (_dialogType == JsDialogType.Prompt && _inputFocused)
                {
                    HandleInputKey(key, ctrl);
                }
                break;
        }
    }

    public override void OnTextInput(char c, bool ctrl)
    {
        if (_dialogType == JsDialogType.Prompt && _inputFocused && !ctrl)
        {
            if (c >= 32) // printable characters only
            {
                _inputText = _inputText.Insert(_inputCursorPos, c.ToString());
                _inputCursorPos++;
                Invalidate();
            }
        }
    }

    private void HandleInputKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Left:
                if (_inputCursorPos > 0) _inputCursorPos--;
                Invalidate();
                break;
            case Key.Right:
                if (_inputCursorPos < _inputText.Length) _inputCursorPos++;
                Invalidate();
                break;
            case Key.Home:
                _inputCursorPos = 0;
                Invalidate();
                break;
            case Key.End:
                _inputCursorPos = _inputText.Length;
                Invalidate();
                break;
            case Key.Backspace:
                if (_inputCursorPos > 0)
                {
                    _inputText = _inputText.Remove(_inputCursorPos - 1, 1);
                    _inputCursorPos--;
                    Invalidate();
                }
                break;
            case Key.Delete:
                if (_inputCursorPos < _inputText.Length)
                {
                    _inputText = _inputText.Remove(_inputCursorPos, 1);
                    Invalidate();
                }
                break;
        }
    }

    private void DismissWithOk()
    {
        object result = _dialogType switch
        {
            JsDialogType.Alert => null,
            JsDialogType.Confirm => true,
            JsDialogType.Prompt => (object)_inputText ?? string.Empty,
            _ => null
        };

        IsVisible = false;
        Completed?.Invoke(result);
        Invalidate();
    }

    private void DismissWithCancel()
    {
        object result = _dialogType switch
        {
            JsDialogType.Alert => null, // alert has no cancel concept — treat as OK
            JsDialogType.Confirm => false,
            JsDialogType.Prompt => null,
            _ => null
        };

        IsVisible = false;
        Completed?.Invoke(result);
        Invalidate();
    }
}
