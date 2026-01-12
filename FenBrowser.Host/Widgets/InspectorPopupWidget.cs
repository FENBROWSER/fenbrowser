using SkiaSharp;
using FenBrowser.FenEngine.Interaction;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets
{
    /// <summary>
    /// Simple inspector popup that shows element details when "Inspect" is clicked.
    /// </summary>
    public class InspectorPopupWidget : Widget
    {
        private HitTestResult _hit;
        private string[] _lines;
        private SKPaint _bgPaint;
        private SKPaint _textPaint;
        private SKPaint _titlePaint;
        private SKPaint _borderPaint;
        
        public event Action CloseRequested;
        
        private const float PADDING = 12f;
        private const float LINE_HEIGHT = 20f;
        private const float WIDTH = 350f;
        
        public float Width { get; private set; }
        public float Height { get; private set; }
        
        public InspectorPopupWidget(HitTestResult hit)
        {
            _hit = hit;
            
            // Build display lines
            var bbox = hit.BoundingBox ?? default;
            _lines = new[]
            {
                $"Tag: <{hit.TagName ?? "unknown"}>",
                $"ID: {hit.ElementId ?? "(none)"}",
                $"Text: {(string.IsNullOrEmpty(hit.TextPreview) ? "(none)" : hit.TextPreview)}",
                $"",
                $"Clickable: {hit.IsClickable}",
                $"Editable: {hit.IsEditable}",
                $"Link: {hit.IsLink}",
                $"",
                $"Position: ({bbox.Left:F0}, {bbox.Top:F0})",
                $"Size: {bbox.Width:F0} × {bbox.Height:F0}",
                $"",
                hit.IsLink ? $"Href: {hit.Href ?? "(none)"}" : ""
            };
            
            // Initialize paints
            _bgPaint = new SKPaint
            {
                Color = SKColor.Parse("#1E1E1E"),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            
            _borderPaint = new SKPaint
            {
                Color = SKColor.Parse("#3C3C3C"),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1
            };
            
            _titlePaint = new SKPaint
            {
                Color = SKColor.Parse("#569CD6"),
                IsAntialias = true,
                TextSize = 14,
                Typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold)
            };
            
            _textPaint = new SKPaint
            {
                Color = SKColor.Parse("#D4D4D4"),
                IsAntialias = true,
                TextSize = 12,
                Typeface = SKTypeface.FromFamilyName("Consolas")
            };
            
            // Calculate height based on lines
            float height = PADDING * 2 + 30 + (_lines.Length * LINE_HEIGHT);
            this.Width = WIDTH;
            this.Height = height;
        }
        
        public void Show(float x, float y, float screenWidth, float screenHeight)
        {
            // Position popup (avoid going off-screen)
            float posX = x + 10;
            float posY = y + 10;
            
            if (posX + Width > screenWidth)
                posX = screenWidth - Width - 10;
            if (posY + Height > screenHeight)
                posY = screenHeight - Height - 10;
            
            posX = Math.Max(10, posX);
            posY = Math.Max(10, posY);
            
            // Set bounds
            this.Bounds = new SKRect(posX, posY, posX + Width, posY + Height);
            this.IsVisible = true;
        }
        
        public override void Paint(SKCanvas canvas)
        {
            if (!IsVisible) return;
            
            var bounds = Bounds;
            float X = bounds.Left;
            float Y = bounds.Top;
            
            // Background with rounded corners
            canvas.DrawRoundRect(bounds, 6, 6, _bgPaint);
            canvas.DrawRoundRect(bounds, 6, 6, _borderPaint);
            
            // Title
            float textY = Y + PADDING + 14;
            canvas.DrawText("Element Inspector", X + PADDING, textY, _titlePaint);
            
            // Close button hint
            var hintPaint = new SKPaint
            {
                Color = SKColor.Parse("#808080"),
                TextSize = 10,
                Typeface = SKTypeface.FromFamilyName("Segoe UI")
            };
            canvas.DrawText("(click to close)", X + Width - 80, textY, hintPaint);
            
            // Separator line
            textY += 10;
            canvas.DrawLine(X + PADDING, textY, X + Width - PADDING, textY, _borderPaint);
            textY += 10;
            
            // Content lines
            foreach (var line in _lines)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    // Color property names differently
                    if (line.Contains(": "))
                    {
                        var parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                        var propPaint = new SKPaint
                        {
                            Color = SKColor.Parse("#9CDCFE"),
                            IsAntialias = true,
                            TextSize = 12,
                            Typeface = SKTypeface.FromFamilyName("Consolas")
                        };
                        var valuePaint = new SKPaint
                        {
                            Color = SKColor.Parse("#CE9178"),
                            IsAntialias = true,
                            TextSize = 12,
                            Typeface = SKTypeface.FromFamilyName("Consolas")
                        };
                        
                        canvas.DrawText(parts[0] + ": ", X + PADDING, textY, propPaint);
                        float propWidth = propPaint.MeasureText(parts[0] + ": ");
                        if (parts.Length > 1)
                            canvas.DrawText(parts[1], X + PADDING + propWidth, textY, valuePaint);
                    }
                    else
                    {
                        canvas.DrawText(line, X + PADDING, textY, _textPaint);
                    }
                }
                textY += LINE_HEIGHT;
            }
        }
        
        public override void OnMouseDown(float x, float y, MouseButton button)
        {
            // Close on click if within bounds
            if (IsVisible && Bounds.Contains(x, y))
            {
                IsVisible = false;
                CloseRequested?.Invoke();
            }
        }
    }
}
