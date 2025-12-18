using SkiaSharp;

namespace FenBrowser.Host.Tabs;

/// <summary>
/// Represents a single browser tab.
/// Each tab owns its own BrowserIntegration (engine instance).
/// No shared DOM, no cross-tab leakage.
/// </summary>
public class BrowserTab
{
    private static int _nextId = 1;
    
    /// <summary>
    /// Unique tab identifier.
    /// </summary>
    public int Id { get; }
    
    /// <summary>
    /// Tab's own browser integration (engine instance).
    /// </summary>
    public BrowserIntegration Browser { get; }
    
    /// <summary>
    /// Tab title (from page title or URL).
    /// </summary>
    public string Title { get; set; } = "New Tab";
    
    /// <summary>
    /// Tab favicon (null if not loaded).
    /// </summary>
    public SKBitmap Favicon { get; set; }
    
    /// <summary>
    /// Whether this tab is currently loading.
    /// </summary>
    public bool IsLoading => Browser.IsLoading;
    
    /// <summary>
    /// Current URL of this tab.
    /// </summary>
    public string Url => Browser.CurrentUrl;
    
    /// <summary>
    /// Event when tab title changes.
    /// </summary>
    public event Action<BrowserTab> TitleChanged;
    
    /// <summary>
    /// Event when tab loading state changes.
    /// </summary>
    public event Action<BrowserTab> LoadingChanged;
    
    /// <summary>
    /// Event when tab needs repaint.
    /// </summary>
    public event Action<BrowserTab> NeedsRepaint;
    
    public BrowserTab()
    {
        Id = _nextId++;
        Browser = new BrowserIntegration();
        
        // Wire browser events to tab events
        Browser.UrlChanged += url =>
        {
            // Update title from URL if no page title
            if (string.IsNullOrEmpty(Title) || Title == "New Tab")
            {
                Title = url.Length > 30 ? url.Substring(0, 30) + "..." : url;
            }
            TitleChanged?.Invoke(this);
        };
        
        Browser.LoadingChanged += loading =>
        {
            LoadingChanged?.Invoke(this);
        };
        
        Browser.NeedsRepaint += () =>
        {
            NeedsRepaint?.Invoke(this);
        };
    }
    
    /// <summary>
    /// Navigate this tab to a URL.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        await Browser.NavigateAsync(url);
    }
    
    /// <summary>
    /// Render this tab's content.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        Browser.Render(canvas, viewport);
    }
}
