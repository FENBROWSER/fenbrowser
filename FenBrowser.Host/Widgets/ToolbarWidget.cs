using SkiaSharp;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Toolbar container with navigation buttons and address bar.
/// </summary>
public class ToolbarWidget : Widget
{
    public ButtonWidget BackButton { get; }
    public ButtonWidget ForwardButton { get; }
    public ButtonWidget RefreshButton { get; }
    public ButtonWidget HomeButton { get; }
    public AddressBarWidget AddressBar { get; }
    public ButtonWidget GoButton { get; }
    
    public event Action BackClicked;
    public event Action ForwardClicked;
    public event Action RefreshClicked;
    public event Action HomeClicked;
    public event Action<string> NavigateRequested;
    
    // Styling
    public float Height { get; set; } = 48;
    public float ButtonWidth { get; set; } = 36;
    public float Spacing { get; set; } = 4;
    public SKColor BackgroundColor { get; set; } = new SKColor(248, 249, 250);
    public SKColor BorderColor { get; set; } = new SKColor(220, 220, 220);
    
    public ToolbarWidget()
    {
        BackButton = new ButtonWidget { Text = "←", FontSize = 18 };
        ForwardButton = new ButtonWidget { Text = "→", FontSize = 18 };
        RefreshButton = new ButtonWidget { Text = "↻", FontSize = 16 };
        HomeButton = new ButtonWidget { Text = "🏠", FontSize = 14 };
        AddressBar = new AddressBarWidget();
        GoButton = new ButtonWidget { Text = "Go", FontSize = 12 };
        
        // Wire events
        BackButton.Clicked += () => BackClicked?.Invoke();
        ForwardButton.Clicked += () => ForwardClicked?.Invoke();
        RefreshButton.Clicked += () => RefreshClicked?.Invoke();
        HomeButton.Clicked += () => HomeClicked?.Invoke();
        GoButton.Clicked += () => NavigateRequested?.Invoke(AddressBar.Text);
        AddressBar.NavigateRequested += url => NavigateRequested?.Invoke(url);
        
        // Add children
        AddChild(BackButton);
        AddChild(ForwardButton);
        AddChild(RefreshButton);
        AddChild(HomeButton);
        AddChild(AddressBar);
        AddChild(GoButton);
    }
    
    public override void Layout(SKRect available)
    {
        // Toolbar spans full width at top
        Bounds = new SKRect(available.Left, available.Top, available.Right, available.Top + Height);
        
        float x = Bounds.Left + Spacing;
        float y = Bounds.Top + (Height - ButtonWidth) / 2;
        float buttonH = ButtonWidth;
        
        // Navigation buttons
        BackButton.Layout(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        ForwardButton.Layout(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        RefreshButton.Layout(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing;
        
        HomeButton.Layout(new SKRect(x, y, x + ButtonWidth, y + buttonH));
        x += ButtonWidth + Spacing * 2;
        
        // Go button at the end
        float goWidth = 50;
        float rightEdge = Bounds.Right - Spacing;
        GoButton.Layout(new SKRect(rightEdge - goWidth, y, rightEdge, y + buttonH));
        
        // Address bar fills remaining space
        float addrRight = rightEdge - goWidth - Spacing;
        AddressBar.Layout(new SKRect(x, y + 2, addrRight, y + buttonH - 2));
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Draw toolbar background
        using var bgPaint = new SKPaint
        {
            Color = BackgroundColor,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(Bounds, bgPaint);
        
        // Draw bottom border
        using var borderPaint = new SKPaint
        {
            Color = BorderColor,
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
        ForwardButton.IsEnabled = canGoForward;
    }
    
    public void SetUrl(string url)
    {
        AddressBar.Text = url;
    }
}
