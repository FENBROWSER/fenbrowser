using SkiaSharp;
using FenBrowser.Host.ProcessIsolation;

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
        Browser.TitleChanged += title =>
        {
            if (!string.IsNullOrEmpty(title))
            {
                Title = title;
                TitleChanged?.Invoke(this);
            }
        };
        
        Browser.UrlChanged += url =>
        {
            // Update title from URL only if it's currently generic
            if (Title == "New Tab" || Title == "Loading...")
            {
                Title = url.Length > 30 ? url.Substring(0, 30) + "..." : url;
                TitleChanged?.Invoke(this);
            }
        };
        
        Browser.LoadingChanged += loading =>
        {
            LoadingChanged?.Invoke(this);
        };
        
        Browser.NeedsRepaint += () =>
        {
            NeedsRepaint?.Invoke(this);
        };

        Browser.FaviconChanged += icon =>
        {
            Favicon = icon;
            NeedsRepaint?.Invoke(this); // Trigger UI update
        };
    }
    
    /// <summary>
    /// Navigate this tab to a URL.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        ProcessIsolationRuntime.Current?.OnNavigationRequested(this, url, isUserInput: true);
        await Browser.NavigateAsync(url);
    }

    /// <summary>
    /// Navigate this tab programmatically (automation/script paths).
    /// </summary>
    public async Task NavigateProgrammaticAsync(string url)
    {
        ProcessIsolationRuntime.Current?.OnNavigationRequested(this, url, isUserInput: false);
        await Browser.NavigateProgrammaticAsync(url);
    }
    
    /// <summary>
    /// Render this tab's content.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        Browser.Render(canvas, viewport);
    }
}
