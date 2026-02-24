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
    /// Indicates if the background renderer process for this tab has crashed.
    /// </summary>
    public bool IsCrashed { get; private set; }
    
    /// <summary>
    /// Reason for the crash, if available.
    /// </summary>
    public string CrashReason { get; private set; }
    
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
    
    /// <summary>
    /// Event when the renderer process for this tab crashes.
    /// </summary>
    public event Action<BrowserTab, string> Crashed;
    
    public BrowserTab()
    {
        Id = _nextId++;
        Browser = new BrowserIntegration(this);
        
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
        IsCrashed = false; // Reset crash state on new navigation
        ProcessIsolationRuntime.Current?.OnNavigationRequested(this, url, isUserInput: true);
        await Browser.NavigateAsync(url);
    }

    /// <summary>
    /// Navigate this tab programmatically (automation/script paths).
    /// </summary>
    public async Task NavigateProgrammaticAsync(string url)
    {
        IsCrashed = false; // Reset crash state on new navigation
        ProcessIsolationRuntime.Current?.OnNavigationRequested(this, url, isUserInput: false);
        await Browser.NavigateProgrammaticAsync(url);
    }
    
    /// <summary>
    /// Called by TabManager when the OOP coordinator reports a crash.
    /// </summary>
    public void NotifyCrashed(string reason)
    {
        IsCrashed = true;
        CrashReason = reason;
        Crashed?.Invoke(this, reason);
        NeedsRepaint?.Invoke(this);
    }
    
    /// <summary>
    /// Render this tab's content.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        if (IsCrashed)
        {
            RenderCrashScreen(canvas, viewport);
            return;
        }

        Browser.Render(canvas, viewport);
    }
    
    private void RenderCrashScreen(SKCanvas canvas, SKRect viewport)
    {
        canvas.Clear(SKColor.Parse("#202124")); // Dark gray Chrome-like background

        using var iconPaint = new SKPaint
        {
            Color = SKColor.Parse("#8AB4F8"),
            IsAntialias = true,
            TextSize = 64,
            Typeface = SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default
        };

        using var titlePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 32,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default
        };
        
        using var descPaint = new SKPaint
        {
            Color = SKColor.Parse("#9AA0A6"),
            IsAntialias = true,
            TextSize = 16,
            Typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default
        };

        float centerY = viewport.Height / 2 - 50;
        float centerX = viewport.Width / 2;

        // "Sad face" icon
        canvas.DrawText(":(", centerX - iconPaint.MeasureText(":(") / 2, centerY - 60, iconPaint);

        // Title
        string titleText = "Aw, Snap!";
        canvas.DrawText(titleText, centerX - titlePaint.MeasureText(titleText) / 2, centerY, titlePaint);

        // Description
        string primaryDesc = "Something went wrong while displaying this webpage.";
        canvas.DrawText(primaryDesc, centerX - descPaint.MeasureText(primaryDesc) / 2, centerY + 40, descPaint);

        // Crash reason details
        if (!string.IsNullOrEmpty(CrashReason))
        {
            string reasonText = $"Error code: {CrashReason}";
            canvas.DrawText(reasonText, centerX - descPaint.MeasureText(reasonText) / 2, centerY + 70, descPaint);
        }
    }
}
