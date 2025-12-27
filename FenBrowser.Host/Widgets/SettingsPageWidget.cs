using System;
using FenBrowser.Core;
using FenBrowser.FenEngine.Interaction; // For Widget base
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Host.Theme;
using FenBrowser.Host.Input;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.Widgets;

public class SettingsPageWidget : Widget
{
    private readonly float _sidebarWidth = 250;
    private readonly float _padding = 24;
    // private readonly float _rowHeight = 40; // Removed unused
    
    // Sidebar Items
    // private ButtonWidget _navGeneral; // Placeholder for future
    // private ButtonWidget _navAppearance; // Placeholder for future
    // private ButtonWidget _navPrivacy;    // Placeholder for future
    
    // Content Controls
    private SwitchWidget _switchJs;
    private SwitchWidget _switchTracking;
    private SwitchWidget _switchTheme;
    
    private static SKTypeface _headerFont = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
    private static SKTypeface _labelFont = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    
    public SettingsPageWidget()
    {
        IsVisible = false; // Hidden by default, shown by WebContentWidget when URL matches
        Name = "Settings Page";
        
        // --- Sidebar ---
        // We render sidebar items manually for rich visual control (Icon + Selection + Text)
        // _navGeneral = new ButtonWidget { Text = "General", FontSize = 16 };
        // AddChild(_navGeneral);
        
        // --- Content ---
        
        // JS Toggle
        _switchJs = new SwitchWidget();
        _switchJs.IsChecked = BrowserSettings.Instance.EnableJavaScript;
        _switchJs.CheckedChanged += (val) =>
        {
            BrowserSettings.Instance.EnableJavaScript = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchJs);
        
        // Tracking Toggle
        _switchTracking = new SwitchWidget();
        _switchTracking.IsChecked = BrowserSettings.Instance.EnableTrackingPrevention;
        _switchTracking.CheckedChanged += (val) =>
        {
            BrowserSettings.Instance.EnableTrackingPrevention = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchTracking);
        
        // Theme Toggle
        _switchTheme = new SwitchWidget();
        _switchTheme.IsChecked = ThemeManager.IsDark;
        _switchTheme.CheckedChanged += (val) =>
        {
            ThemeManager.ToggleTheme(); // This handles saving internally? No, mostly runtime.
            // But we need to update UI immediately
            Parent?.Invalidate(); 
        };
        AddChild(_switchTheme);
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return availableSpace;
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        base.OnArrange(finalRect);
        
        // Sidebar Layout
        float sideX = finalRect.Left;
        float sideY = finalRect.Top + 60; // Below "Settings" title area
        
        // Sidebar Layout
        // Manual layout in Paint
        
        // Content Layout
        float contentLeft = finalRect.Left + _sidebarWidth + _padding * 2;
        float contentTop = finalRect.Top + 80;
        float labelWidth = 300;
        
        float currentY = contentTop;
        
        // Theme Row
        // Label is drawn in Paint, we just arrange the switch
        _switchTheme.Arrange(new SKRect(contentLeft + labelWidth, currentY + 8, contentLeft + labelWidth + 50, currentY + 32));
        currentY += 60;
        
        // JS Row
        _switchJs.Arrange(new SKRect(contentLeft + labelWidth, currentY + 8, contentLeft + labelWidth + 50, currentY + 32));
        currentY += 60;
        
        // Tracking Row
        _switchTracking.Arrange(new SKRect(contentLeft + labelWidth, currentY + 8, contentLeft + labelWidth + 50, currentY + 32));
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // 1. Main Background (Content Area)
        using var bgPaint = new SKPaint { Color = theme.Background, IsAntialias = false };
        canvas.DrawRect(Bounds, bgPaint);
        
        // 2. Sidebar Background (Distinct Surface Color)
        var sidebarRect = new SKRect(Bounds.Left, Bounds.Top, Bounds.Left + _sidebarWidth, Bounds.Bottom);
        using var sidePaint = new SKPaint { Color = theme.Surface, IsAntialias = false };
        canvas.DrawRect(sidebarRect, sidePaint);
        
        // Sidebar Border
        using var borderPaint = new SKPaint { Color = theme.Border, Style = SKPaintStyle.Stroke, IsAntialias = false };
        canvas.DrawLine(sidebarRect.Right, sidebarRect.Top, sidebarRect.Right, sidebarRect.Bottom, borderPaint);
        
        // 3. Header "Settings"
        using var headerPaint = new SKPaint 
        { 
            Color = theme.Text, 
            IsAntialias = true, 
            TextSize = 24, 
            Typeface = _headerFont
        };
        canvas.DrawText("Settings", Bounds.Left + 24, Bounds.Top + 40, headerPaint);
        
        // --- Sidebar Items ---
        float sideItemY = Bounds.Top + 70;
        var generalItemRect = new SKRect(Bounds.Left + 10, sideItemY, Bounds.Left + _sidebarWidth - 10, sideItemY + 40);
        
        // Selection Pill (Active)
        using var pillPaint = new SKPaint { Color = theme.SurfacePressed, IsAntialias = true, Style = SKPaintStyle.Fill }; // Darker/Pressed indicates active
        // Or better, use a subtle accent tint? Let's use SurfacePressed for "Selected Gray" look common in browsers
        canvas.DrawRoundRect(generalItemRect, 4, 4, pillPaint);
        
        // Icon (Simple Gear/Cog Glyph substitute - Circle for now)
        using var iconPaint = new SKPaint { Color = theme.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawCircle(generalItemRect.Left + 20, generalItemRect.MidY, 8, iconPaint);
        
        // Text "General"
        using var itemTextPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 15, Typeface = _labelFont };
        canvas.DrawText("General", generalItemRect.Left + 40, generalItemRect.MidY + 5, itemTextPaint);
        
        
        // --- Content Area ---
        
        // 4. Content Header
        float contentLeft = Bounds.Left + _sidebarWidth + _padding * 2;
        // "General" Header
        using var subHeaderPaint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true,
            TextSize = 28,
            Typeface = _headerFont
        };
        canvas.DrawText("General", contentLeft, Bounds.Top + 50, subHeaderPaint);
        
        // Separator Line
        canvas.DrawLine(contentLeft, Bounds.Top + 65, Bounds.Right - _padding, Bounds.Top + 65, borderPaint);
        
        // 5. Labels for Switches
        using var labelPaint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true,
            TextSize = 16,
            Typeface = _headerFont
        };
        
        using var descPaint = new SKPaint
        {
            Color = theme.TextMuted,
            IsAntialias = true,
            TextSize = 14,
            Typeface = _labelFont
        };
        
        float currentY = Bounds.Top + 80;
        
        // Row 1: Theme
        canvas.DrawText("Dark Mode", contentLeft, currentY + 20, labelPaint);
        canvas.DrawText("Switch between light and dark themes", contentLeft, currentY + 40, descPaint);
        currentY += 60;
        
        // Row 2: JavaScript
        canvas.DrawText("JavaScript", contentLeft, currentY + 20, labelPaint);
        canvas.DrawText("Allow sites to run scripts", contentLeft, currentY + 40, descPaint);
        currentY += 60;
        
        // Row 3: Tracking
        canvas.DrawText("Tracking Prevention", contentLeft, currentY + 20, labelPaint);
        canvas.DrawText("Block known trackers", contentLeft, currentY + 40, descPaint);
        
        // Children (Switches) paint themselves
        // Since we are overriding Paint and not calling base.Paint, we must paint children manually
        foreach (var child in Children)
        {
             if (child.IsVisible)
             {
                 // Children bounds are relative to parent? 
                 // In this simple engine, Bounds are usually absolute-ish or parent-relative.
                 // SwitchWidget uses its Bounds in Paint.
                 // We rely on Arrange to have set the correct Bounds.
                 child.Paint(canvas);
             }
        }
    }
}
