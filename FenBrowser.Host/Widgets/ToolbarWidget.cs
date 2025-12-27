using SkiaSharp;
using Silk.NET.Input;
using FenBrowser.Host.Theme;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Toolbar container with navigation buttons and address bar.
/// </summary>
public class ToolbarWidget : Widget
{
    public ButtonWidget BackButton { get; }
    // public ButtonWidget ForwardButton { get; } // Removed as per request
    public ButtonWidget RefreshButton { get; }
    public ButtonWidget HomeButton { get; }
    public AddressBarWidget AddressBar { get; }
    public ButtonWidget GoButton { get; }
    public ButtonWidget SettingsButton { get; }
    
    public event Action BackClicked;
    // public event Action ForwardClicked; // Removed as per request
    public event Action RefreshClicked;
    public event Action HomeClicked;
    public event Action<string> NavigateRequested;
    public event Action SettingsClicked;
    
    // Styling
    // Styling (optional overrides)
    public float Height { get; set; } = 48;
    public float ButtonWidth { get; set; } = 32; // Slightly smaller for modern look
    public float Spacing { get; set; } = 6;
    public SKColor? BackgroundColor { get; set; }
    public SKColor? BorderColor { get; set; }
    
    public ToolbarWidget()
    {
        Role = WidgetRole.Toolbar;
        Name = "Main Toolbar";
        
        Name = "Main Toolbar";

        // Paths
    // Paths - Fluent/Material style
        
        // Back: Standard Arrow Layout
        // Using a cleaner SVG path for "Arrow Back" (Material)
        // M 20 11 H 7.83 L 13.42 5.41 L 12 4 L 4 12 L 12 20 L 13.41 18.59 L 7.83 12.98 H 20 V 11 Z
        // This is a "Fill" path. If we want stroke, we need single line.
        // Let's use Fill for consistency with Settings now.
        // M 20 11 ... is a shape. 
        var backPath = SKPath.ParseSvgPathData("M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z");
        
        // Refresh: Thin circular arrow
        var refreshPath = SKPath.ParseSvgPathData("M 19.95 11 A 8 8 0 1 0 12 20 A 8 8 0 0 0 17 18.5 M 19.95 11 L 19.95 6 M 19.95 11 L 15 11"); // Partial arc + arrow
        
        // Home: Simple house outline
        var homePath = SKPath.ParseSvgPathData("M 4 11 L 12 4 L 20 11 L 20 20 L 14 20 L 14 14 L 10 14 L 10 20 L 4 20 Z");
        
        // Settings: Simple thin gear or just circle with dots? Users usually like the Gear.
        // Let's use a simplified stroke gear path or just "..." if they wanted Edge style menu.
        // Be safer with a thin gear.
        // Using a path that works well with Stroke is tricky for a gear (usually filled).
        // Let's try 3 horizontal dots for "Menu" style if the gear was "too big". 
        // "Settings wheel also alot bigger" -> user specifically mentioned "wheel".
        // Maybe try a simple circle with spokes?
        // Or standard Fluent Gear but small.
        // Let's try the "Ellipsis" (...) which is very Edge-like, as Edge uses ... for menu.
        // "Edge-like" usually means ...
        // But user said "settings wheel". Let's assume they want a Wheel but refined.
        // Use a simpler stroke wheel: Circle + 6 lines?
        // Or just use the SVG path as Fill but make it small.
        // User said "icons are too bold", so Stroke is better.
        // Let's go with a simple "..." path for now? No, user asked for "Settings wheel" size fix.
        // So stick to Wheel.
        // Let's use standard simplified gear path but stroke it?
        // Gear path is usually outline. 
        // Let's use a pre-defined thin gear path.
        var settingsPath = SKPath.ParseSvgPathData("M 12 15 A 3 3 0 1 0 12 9 A 3 3 0 0 0 12 15 Z M 19.4 15 A 1.1 1.1 0 0 0 20.6 16 L 21 16 A 1 1 0 0 1 22 17 L 22 19 A 1 1 0 0 1 21 20 L 20.6 20 A 1.1 1.1 0 0 0 19.4 21 L 19 21 A 1.1 1.1 0 0 0 18 22 L 18 23 A 1 1 0 0 1 17 24 L 15 24 A 1 1 0 0 1 14 23 L 14 22.4 A 1.1 1.1 0 0 0 13 21.6 L 11 21.6 A 1.1 1.1 0 0 0 10 22.4 L 10 23 A 1 1 0 0 1 9 24 L 7 24 A 1 1 0 0 1 6 23 L 6 22 A 1.1 1.1 0 0 0 5 21 L 4.6 21 A 1.1 1.1 0 0 0 3.4 20 L 3 20 A 1 1 0 0 1 2 19 L 2 17 A 1 1 0 0 1 3 16 L 3.4 16 A 1.1 1.1 0 0 0 4.6 15 L 4.6 13 A 1.1 1.1 0 0 0 3.4 12 L 3 12 A 1 1 0 0 1 2 11 L 2 9 A 1 1 0 0 1 3 8 L 3.4 8 A 1.1 1.1 0 0 0 4.6 9 L 5 9 A 1.1 1.1 0 0 0 6 8 L 6 7 A 1 1 0 0 1 7 6 L 9 6 A 1 1 0 0 1 10 7 L 10 7.6 A 1.1 1.1 0 0 0 11 8.4 L 13 8.4 A 1.1 1.1 0 0 0 14 7.6 L 14 7 A 1 1 0 0 1 15 6 L 17 6 A 1 1 0 0 1 18 7 L 18 8 A 1.1 1.1 0 0 0 19 9 L 19.4 9 A 1.1 1.1 0 0 0 20.6 8 L 21 8 A 1 1 0 0 1 22 9 L 22 11 A 1 1 0 0 1 21 12 L 20.6 12 A 1.1 1.1 0 0 0 19.4 13 L 19.4 15 Z");

        BackButton = new ButtonWidget 
        { 
            IconPath = backPath, 
            FontSize = 18, 
            Name = "Back", 
            HelpText = "Go back",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        // REMOVED Forward Button
        // ForwardButton = ...
        
        RefreshButton = new ButtonWidget 
        { 
            // Refresh path above is Stroke-based (single line arc/arrow). 
            // We need a Fill-based refresh path if we want consistency?
            // Or keep it Stroke? 
            // Actually, Refresh usually looks good as stroke.
            // But if we mix Fill (Back/Settings) and Stroke (Refresh), might look odd.
            // Let's use a Fill path for Refresh too (Material Refresh).
            IconPath = SKPath.ParseSvgPathData("M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-8 3.58-8 8s3.58 8 8 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"),
            FontSize = 16, 
            Name = "Refresh", 
            HelpText = "Reload",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        HomeButton = new ButtonWidget 
        { 
            // Home path M 4 11 ... is a shape (House).
            IconPath = homePath, 
            FontSize = 16, 
            Name = "Home", 
            HelpText = "Home",
            IconPaintStyle = SKPaintStyle.Fill // Fill looked better for House
        };
        
        AddressBar = new AddressBarWidget();
        GoButton = new ButtonWidget { Text = "Go", FontSize = 12, Name = "Go", HelpText = "Navigate" };
        
        // Settings: Back to Fill for cleaner "solid" look, but smaller.
        // A solid text-colored gear looks better than a stroked outline of a gear shape.
        SettingsButton = new ButtonWidget 
        { 
            IconPath = settingsPath, 
            FontSize = 15, // Significantly reduced (was 20, then 16)
            Name = "Settings", 
            HelpText = "Settings",
            IconPaintStyle = SKPaintStyle.Fill, // Solid fill is cleaner for complex shapes
            // IconStrokeWidth ignored for Fill
        }; 
        
        // Wire events
        BackButton.Clicked += () => BackClicked?.Invoke();
        // ForwardButton.Clicked += () => ForwardClicked?.Invoke();
        RefreshButton.Clicked += () => RefreshClicked?.Invoke();
        HomeButton.Clicked += () => HomeClicked?.Invoke();
        GoButton.Clicked += () => NavigateRequested?.Invoke(AddressBar.Text);
        AddressBar.NavigateRequested += url => NavigateRequested?.Invoke(url);
        SettingsButton.Clicked += () => SettingsClicked?.Invoke();
        
        // Add children (No Forward)
        AddChild(BackButton);
        // AddChild(ForwardButton);
        AddChild(RefreshButton);
        AddChild(HomeButton);
        AddChild(AddressBar);
        AddChild(GoButton);
        AddChild(SettingsButton);
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Measure all children
        foreach (var child in Children)
        {
            // For now, children use a fixed height or their own logic
            child.Measure(availableSpace);
        }
        
        return new SKSize(availableSpace.Width, Height);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        float x = finalRect.Left + Spacing;
        float y = finalRect.Top + (Height - ButtonWidth) / 2;
        float buttonH = ButtonWidth;
        
        // Navigation buttons
        BackButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        // ForwardButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        // x += ButtonWidth + Spacing;
        
        RefreshButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        HomeButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing * 2;
        
        // Go button at the end
        float goWidth = 50;
        float settingsWidth = 40; // Explicit wider width
        float rightEdge = finalRect.Right - Spacing;
        
        SettingsButton.Arrange(new SKRect(rightEdge - settingsWidth, y, rightEdge, y + buttonH));
        rightEdge -= (settingsWidth + Spacing);
        
        GoButton.Arrange(new SKRect(rightEdge - goWidth, y, rightEdge, y + buttonH));
        
        // Address bar fills remaining space
        float addrRight = rightEdge - goWidth - Spacing;
        AddressBar.Arrange(new SKRect(x, y + 2, addrRight, y + buttonH - 2));
    }
    
    /// <summary>
    /// Layout this widget within the available bounds.
    /// [DEPRECATED] Use Measure/Arrange instead.
    /// </summary>
    public override void Layout(SKRect available)
    {
        Measure(new SKSize(available.Width, available.Height));
        Arrange(available);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Draw toolbar background
        using var bgPaint = new SKPaint
        {
            Color = BackgroundColor ?? theme.Background,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(Bounds, bgPaint);
        
        // Draw bottom border
        using var borderPaint = new SKPaint
        {
            Color = BorderColor ?? theme.Border,
            StrokeWidth = 1
        };
        canvas.DrawLine(Bounds.Left, Bounds.Bottom, Bounds.Right, Bounds.Bottom, borderPaint);
    }
    
    public void SetCanGoBack(bool canGoBack)
    {
        BackButton.IsEnabled = canGoBack;
    }
    
    public void SetCanGoForward(bool canGoForward)
    {
        // ForwardButton.IsEnabled = canGoForward;
    }
    
    public void SetUrl(string url)
    {
        AddressBar.Text = url;
    }
}
