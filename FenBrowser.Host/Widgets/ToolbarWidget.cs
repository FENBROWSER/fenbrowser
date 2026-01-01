using System;
using SkiaSharp;
using FenBrowser.Host.Theme;
using FenBrowser.Core;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Main toolbar with navigation controls and address bar.
/// </summary>
public class ToolbarWidget : Widget
{
    public ButtonWidget BackButton { get; }
    // public ButtonWidget ForwardButton { get; }
    public ButtonWidget RefreshButton { get; }
    public ButtonWidget HomeButton { get; }
    public AddressBarWidget AddressBar { get; }
    public ButtonWidget FavoritesButton { get; }
    public ButtonWidget SettingsButton { get; }
    public ButtonWidget GoButton { get; }
    
    public event Action BackClicked;
    // public event Action ForwardClicked;
    public event Action RefreshClicked;
    public event Action HomeClicked;
    public event Action<string> NavigateRequested;
    public event Action FavoritesClicked;
    public event Action SettingsClicked;
    
    // Styling
    public float Height { get; set; } = 48;
    public float ButtonWidth { get; set; } = 30;
    public float Spacing { get; set; } = 2;
    
    public ToolbarWidget()
    {
        Name = "Toolbar";
        
        // SVG Paths
        var backPath = SKPath.ParseSvgPathData("M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z");
        var refreshPath = SKPath.ParseSvgPathData("M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z");
        var homePath = SKPath.ParseSvgPathData("M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z");
        var favoritesPath = SKPath.ParseSvgPathData("M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z");
        var settingsPath = SKPath.ParseSvgPathData("M19.43 12.98c.04-.32.07-.64.07-.98s-.03-.66-.07-.98l2.11-1.65c.19-.15.24-.42.12-.64l-2-3.46c-.12-.22-.39-.3-.61-.22l-2.49 1c-.52-.4-1.08-.73-1.69-.98l-.38-2.65C14.46 2.18 14.25 2 14 2h-4c-.25 0-.46.18-.49.42l-.38 2.65c-.61.25-1.17.59-1.69.98l-2.49-1c-.22-.09-.49 0-.61.22l-2 3.46c-.13.22-.07.49.12.64l2.11 1.65c-.04.32-.07.65-.07.98s.03.66.07.98l-2.11 1.65c-.19.15-.24.42-.12.64l2 3.46c.12.22.39.3.61.22l2.49-1c.52.4 1.08.73 1.69.98l.38 2.65c.03.24.24.42.49.42h4c.25 0 .46-.18.49-.42l.38-2.65c.61-.25 1.17-.59 1.69-.98l2.49 1c.22.09.49 0 .61-.22l2-3.46c.12-.22.07-.49-.12-.64l-2.11-1.65zM12 15.5c-1.93 0-3.5-1.57-3.5-3.5s1.57-3.5 3.5-3.5 3.5 1.57 3.5 3.5-1.57 3.5-3.5 3.5z");

        BackButton = new ButtonWidget 
        { 
            IconPath = backPath,
            FontSize = 13, 
            Name = "Back", 
            HelpText = "Go Back",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        RefreshButton = new ButtonWidget 
        { 
            IconPath = refreshPath,
            FontSize = 13, 
            Name = "Refresh", 
            HelpText = "Refresh Page",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        HomeButton = new ButtonWidget 
        { 
            IconPath = homePath,
            FontSize = 13, 
            Name = "Home", 
            HelpText = "Go Home",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        AddressBar = new AddressBarWidget();
        GoButton = new ButtonWidget { Text = "Go", FontSize = 12, Name = "Go", HelpText = "Navigate" };
        
        FavoritesButton = new ButtonWidget
        {
            IconPath = favoritesPath,
            FontSize = 13,
            Name = "Favorites",
            HelpText = "View Favorites",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        SettingsButton = new ButtonWidget 
        { 
            IconPath = settingsPath,
            FontSize = 13,
            Name = "Settings",
            HelpText = "Settings",
            IconPaintStyle = SKPaintStyle.Fill
        };
        
        // Events
        BackButton.Clicked += () => BackClicked?.Invoke();
        RefreshButton.Clicked += () => RefreshClicked?.Invoke();
        HomeButton.Clicked += () => HomeClicked?.Invoke();
        GoButton.Clicked += () => NavigateRequested?.Invoke(AddressBar.Text);
        AddressBar.NavigateRequested += url => NavigateRequested?.Invoke(url);
        FavoritesButton.Clicked += () => FavoritesClicked?.Invoke();
        SettingsButton.Clicked += () => SettingsClicked?.Invoke();
        
        AddChild(BackButton);
        AddChild(RefreshButton);
        AddChild(HomeButton);
        AddChild(AddressBar);
        AddChild(GoButton);
        AddChild(FavoritesButton);
        AddChild(SettingsButton);
    }
    
    public void SetUrl(string url) => AddressBar.Text = url;
    public void SetCanGoBack(bool can) => BackButton.IsVisible = true; 
    public void SetCanGoForward(bool can) { }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        FavoritesButton.IsVisible = BrowserSettings.Instance.ShowFavoritesButton;
        HomeButton.IsVisible = BrowserSettings.Instance.ShowHomeButton;
        foreach (var child in Children) child.Measure(availableSpace);
        return new SKSize(availableSpace.Width, Height);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        float x = finalRect.Left + Spacing;
        float y = finalRect.Top + (Height - ButtonWidth) / 2;
        float buttonH = ButtonWidth;
        
        BackButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        RefreshButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        if (HomeButton.IsVisible)
        {
            HomeButton.Arrange(new SKRect(x, y, x + ButtonWidth, y + buttonH));
            x += ButtonWidth + Spacing;
        }
        // x += Spacing; // Removed extra spacing to match right side symmetry
        
        float goWidth = 34; // Slightly wider than icon for text
        float settingsWidth = ButtonWidth; // Match left-side icons (30) 
        float rightEdge = finalRect.Right - Spacing;
        
        SettingsButton.Arrange(new SKRect(rightEdge - settingsWidth, y, rightEdge, y + buttonH));
        rightEdge -= (settingsWidth + Spacing);
        
        if (FavoritesButton.IsVisible)
        {
            FavoritesButton.Arrange(new SKRect(rightEdge - settingsWidth, y, rightEdge, y + buttonH));
            rightEdge -= (settingsWidth + Spacing);
        }
        
        GoButton.Arrange(new SKRect(rightEdge - goWidth, y, rightEdge, y + buttonH));
        
        float addrRight = rightEdge - goWidth - Spacing;
        AddressBar.Arrange(new SKRect(x, y + 2, addrRight, y + buttonH - 2));
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        using var paint = new SKPaint { Color = theme.Background, Style = SKPaintStyle.Fill };
        canvas.DrawRect(Bounds, paint);
    }
}
