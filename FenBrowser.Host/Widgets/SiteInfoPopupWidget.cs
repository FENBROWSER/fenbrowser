using SkiaSharp;
using FenBrowser.FenEngine.Interaction;
using Silk.NET.Input;
using FenBrowser.Host.Theme;
using System;

namespace FenBrowser.Host.Widgets
{
    public class SiteInfoPopupWidget : Widget
    {
        private const float WIDTH = 380f;
        private const float PADDING = 16f;
        
        public event Action CloseRequested;
        
        // Children
        private SwitchWidget _trackingSwitch;
        private ButtonWidget _closeButton;
        private DropdownWidget _notificationsDropdown;
        private DropdownWidget _soundDropdown;
        private SKPath _closeIconPath = SKPath.ParseSvgPathData("M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");
        
        public string Hostname { get; set; } = "example.com";
        public bool IsSecure { get; set; } = true;
        
        public SiteInfoPopupWidget()
        {
            Name = "Site Info Popup";
            IsVisible = false; // Hidden by default
            
            _trackingSwitch = new SwitchWidget
            {
                IsChecked = true
            };
            AddChild(_trackingSwitch);
            
            _closeButton = new ButtonWidget
            {
                IconPath = _closeIconPath,
                IconPaintStyle = SKPaintStyle.Fill,
                FontSize = 14,
                BackgroundColor = SKColors.Transparent,
                HoverBackgroundColor = ThemeManager.Current.SurfaceHover,
                CornerRadius = 4,
                TextColor = ThemeManager.Current.Text
            };
            _closeButton.Clicked += () => CloseRequested?.Invoke();
            AddChild(_closeButton);
            
            _notificationsDropdown = new DropdownWidget
            {
                Options = new List<string> { "Ask (Default)", "Allow", "Block" },
                SelectedIndex = 2 // Block
            };
            AddChild(_notificationsDropdown);
            
            _soundDropdown = new DropdownWidget
            {
                Options = new List<string> { "Automatic (Default)", "Allow", "Mute" },
                SelectedIndex = 0 // Automatic
            };
            AddChild(_soundDropdown);
        }
        
        public void Show(float x, float y, string hostname, bool secure)
        {
            Hostname = hostname;
            IsSecure = secure;
            
            // Re-measure height based on content
            float height = 300; 
            
            this.Bounds = new SKRect(x, y, x + WIDTH, y + height);
            this.IsVisible = true;
            InvalidateLayout();
        }
        
        public void Hide()
        {
            IsVisible = false;
            _notificationsDropdown.Close();
            _soundDropdown.Close();
        }

        protected override SKSize OnMeasure(SKSize availableSpace)
        {
            return new SKSize(WIDTH, 300);
        }

        protected override void OnArrange(SKRect finalRect)
        {
            float x = finalRect.Left;
            float y = finalRect.Top;
            
            // Close button: Top-Right
            float closeSize = 28;
            _closeButton.Arrange(new SKRect(finalRect.Right - closeSize - 8, y + 8, finalRect.Right - 8, y + 8 + closeSize));
            
            // Permissions Dropdowns
            // Notifications row: ~135y
            float permY = y + 135;
            float dropW = 160; // Increased from 120 to fit "Automatic (Default)"
            float dropH = 28;
            float dropX = finalRect.Right - PADDING - dropW;
            
            _notificationsDropdown.Arrange(new SKRect(dropX, permY, dropX + dropW, permY + dropH));
            
            // Sound row: ~170y
            permY += 35;
            _soundDropdown.Arrange(new SKRect(dropX, permY, dropX + dropW, permY + dropH));
            
            // Tracking Switch: Bottom Right area of its row
            // Row starts at approx 225 based on Paint logic
            float switchY = y + 225; 
            float switchW = 44;
            float switchH = 24;
            _trackingSwitch.Arrange(new SKRect(finalRect.Right - PADDING - switchW, switchY, finalRect.Right - PADDING, switchY + switchH));
        }

        public override void Paint(SKCanvas canvas)
        {
            if (!IsVisible) return;
            
            var theme = ThemeManager.Current;
            var bounds = Bounds;
            
            // shadow
            using var shadowPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(60),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8)
            };
            canvas.DrawRoundRect(new SKRect(bounds.Left+2, bounds.Top+2, bounds.Right+2, bounds.Bottom+2), 8, 8, shadowPaint);

            // Background
            using var bgPaint = new SKPaint { Color = theme.Surface, IsAntialias = true }; // Slightly lighter than background usually
            // Make it look like a popup menu (dark grey usually)
            bgPaint.Color = new SKColor(40, 40, 40); // Hardcoded dark theme for popup for now, or match theme
            if (!ThemeManager.IsDark) bgPaint.Color = SKColors.White;
            
            canvas.DrawRoundRect(bounds, 8, 8, bgPaint);
            
            // Border
            using var borderPaint = new SKPaint { Color = theme.Border, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRoundRect(bounds, 8, 8, borderPaint);

            float currentY = bounds.Top + PADDING;
            float leftX = bounds.Left + PADDING;
            
            // Title
            using var titlePaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 15, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
            canvas.DrawText($"About {Hostname}", leftX, currentY + 12, titlePaint);
            
            // Close button handled by child widget
            
            currentY += 40;
            
            // Section 1: Connection Status
            DrawRow(canvas, leftX, currentY, "Connection is secure", IsSecure ? SKColors.LightGreen : SKColors.Gray, true);
            currentY += 40;
            
            // Separator
            DrawSeparator(canvas, bounds.Left, bounds.Right, currentY);
            currentY += 10;
            
            // Section 2: Permissions Header
            using var grayHeader = new SKPaint { Color = theme.TextMuted, TextSize = 12, IsAntialias = true };
            canvas.DrawText("Permissions for this site", leftX, currentY + 10, grayHeader);
            currentY += 25;
            
            // Permission 1: Location/Notifications
            DrawPermissionLabel(canvas, leftX, currentY, "Notifications");
            currentY += 35;
            DrawPermissionLabel(canvas, leftX, currentY, "Sound");
            currentY += 40;
            
            // Separator
            DrawSeparator(canvas, bounds.Left, bounds.Right, currentY);
            currentY += 15;
            
            // Section 3: Tracking Prevention
            // Switch is child widget, just draw text
            using var textPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 13 };
            canvas.DrawText("Tracking prevention", leftX + 24, currentY + 16, textPaint);
            
            using var subTextPaint = new SKPaint { Color = theme.TextMuted, IsAntialias = true, TextSize = 11 };
            canvas.DrawText("Trackers (0 blocked)", leftX + 24, currentY + 34, subTextPaint);
            
            // Children (Buttons/Dropdowns/Switch) will paint on top
        }
        
        private void DrawRow(SKCanvas canvas, float x, float y, string text, SKColor iconColor, bool isLock)
        {
            var theme = ThemeManager.Current;
            
            // Icon Placeholder
            using var iconPaint = new SKPaint { Color = iconColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            if (isLock)
            {
                // Simple lock
                canvas.DrawRect(x + 4, y + 8, 12, 10, iconPaint);
                canvas.DrawArc(new SKRect(x + 7, y + 2, x + 13, y + 8), 180, 180, false, iconPaint);
            }
            
            using var textPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 14 };
            canvas.DrawText(text, x + 30, y + 16, textPaint);
            
            // Chevron
            using var chevronPaint = new SKPaint { Color = theme.TextMuted, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            float rightX = Bounds.Right - PADDING - 5;
            canvas.DrawLine(rightX - 5, y + 8, rightX, y + 12, chevronPaint);
            canvas.DrawLine(rightX, y + 12, rightX - 5, y + 16, chevronPaint);
        }
        
        private void DrawPermissionLabel(SKCanvas canvas, float x, float y, string label)
        {
             var theme = ThemeManager.Current;
             using var textPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 13 };
             canvas.DrawText(label, x, y + 16, textPaint);
             // Dropdown is drawn by child widget
        }
        
        private void DrawSeparator(SKCanvas canvas, float left, float right, float y)
        {
            using var paint = new SKPaint { Color = ThemeManager.Current.Border.WithAlpha(100), StrokeWidth = 1 };
            canvas.DrawLine(left, y, right, y, paint);
        }

        public override void OnMouseDown(float x, float y, MouseButton button)
        {
            // Propagate to children first
            // We must manually hit test children because Widget.OnMouseDown doesn't automatically propagate 
            // unless we use a container system that does. 
            // But wait, our RootWidget propagates to us. Do we propagate to children?
            // Widget.HitTestDeep usually finds the child.
            // If the RootWidget (or InputManager) calls HitTestDeep, it will find the Dropdown if it's on top.
            // So we don't need manual propagation if the input system is working correctly.
            // However, ensuring Z-order is correct (AddChild order) is important.
            // We added Dropdowns LAST, so they are on top.
            
            base.OnMouseDown(x, y, button);
        }
    }
}
