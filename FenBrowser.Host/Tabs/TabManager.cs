using SkiaSharp;
using FenBrowser.Host.ProcessIsolation;

namespace FenBrowser.Host.Tabs;

/// <summary>
/// Manages browser tabs lifecycle and active tab routing.
/// No cross-tab state access.
/// </summary>
public class TabManager
{
    private readonly List<BrowserTab> _tabs = new();
    private int _activeIndex = -1;
    private readonly Stack<BrowserTab> _closedTabs = new();
    
    private static TabManager _instance;
    public static TabManager Instance 
    {
        get
        {
            if (_instance == null)
            {
                _instance = new TabManager();
                _instance.Initialize();
            }
            return _instance;
        }
    }
    
    // Wire up crash events
    private void Initialize()
    {
        if (ProcessIsolationRuntime.Current != null)
        {
            ProcessIsolationRuntime.Current.RendererCrashed += OnRendererCrashed;
        }
    }

    private void OnRendererCrashed(int tabId, string reason)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab != null)
        {
            tab.NotifyCrashed(reason);
            // Optionally force UI refresh
            ActiveTabChanged?.Invoke(ActiveTab);
        }
    }
    
    /// <summary>
    /// All open tabs.
    /// </summary>
    public IReadOnlyList<BrowserTab> Tabs => _tabs;
    
    /// <summary>
    /// Currently active tab.
    /// </summary>
    public BrowserTab ActiveTab => _activeIndex >= 0 && _activeIndex < _tabs.Count 
        ? _tabs[_activeIndex] 
        : null;
    
    /// <summary>
    /// Active tab index.
    /// </summary>
    public int ActiveIndex => _activeIndex;
    
    /// <summary>
    /// Event when active tab changes.
    /// </summary>
    public event Action<BrowserTab> ActiveTabChanged;
    
    /// <summary>
    /// Event when a tab is added.
    /// </summary>
    public event Action<BrowserTab> TabAdded;
    
    /// <summary>
    /// Event when a tab is removed.
    /// </summary>
    public event Action<BrowserTab> TabRemoved;
    
    /// <summary>
    /// Create a new tab and make it active.
    /// </summary>
    public BrowserTab CreateTab(string url = null)
    {
        var tab = new BrowserTab();
        _tabs.Add(tab);
        
        // Set as active
        _activeIndex = _tabs.Count - 1;
        
        TabAdded?.Invoke(tab);
        ActiveTabChanged?.Invoke(tab);
        
        // Navigate if URL provided
        if (!string.IsNullOrEmpty(url))
        {
            tab.StartInitialNavigation(url);
        }
        
        return tab;
    }
    
    /// <summary>
    /// Close a tab by index.
    /// </summary>
    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        
        var tab = _tabs[index];
        _closedTabs.Push(tab);
        _tabs.RemoveAt(index);
        
        TabRemoved?.Invoke(tab);
        
        // Adjust active index
        if (_tabs.Count == 0)
        {
            _activeIndex = -1;
            ActiveTabChanged?.Invoke(null);
        }
        else if (_activeIndex >= _tabs.Count)
        {
            _activeIndex = _tabs.Count - 1;
            ActiveTabChanged?.Invoke(ActiveTab);
        }
        else if (_activeIndex == index)
        {
            // Closed the active tab, stay at same index (now different tab)
            ActiveTabChanged?.Invoke(ActiveTab);
        }
    }
    
    /// <summary>
    /// Close the active tab.
    /// </summary>
    public void CloseActiveTab()
    {
        if (_activeIndex >= 0)
        {
            CloseTab(_activeIndex);
        }
    }
    
    /// <summary>
    /// Switch to a tab by index.
    /// </summary>
    public void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (index == _activeIndex) return;
        
        _activeIndex = index;
        ActiveTabChanged?.Invoke(ActiveTab);
    }
    
    /// <summary>
    /// Switch to next tab (Ctrl+Tab).
    /// </summary>
    public void NextTab()
    {
        if (_tabs.Count <= 1) return;
        SwitchToTab((_activeIndex + 1) % _tabs.Count);
    }
    
    /// <summary>
    /// Switch to previous tab (Ctrl+Shift+Tab).
    /// </summary>
    public void PreviousTab()
    {
        if (_tabs.Count <= 1) return;
        SwitchToTab((_activeIndex - 1 + _tabs.Count) % _tabs.Count);
    }
    
    /// <summary>
    /// Reopen the last closed tab (Ctrl+Shift+T).
    /// </summary>
    public BrowserTab ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return null;
        
        var tab = _closedTabs.Pop();
        _tabs.Add(tab);
        _activeIndex = _tabs.Count - 1;
        
        TabAdded?.Invoke(tab);
        ActiveTabChanged?.Invoke(tab);
        
        return tab;
    }
    
    /// <summary>
    /// Render the active tab.
    /// </summary>
    public void RenderActiveTab(SKCanvas canvas, SKRect viewport)
    {
        ActiveTab?.Render(canvas, viewport);
    }
}
