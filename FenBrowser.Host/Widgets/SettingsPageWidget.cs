using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Interaction;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Host.Theme;
using FenBrowser.Host.Input;
using InputManager = FenBrowser.Host.Input.InputManager;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.Widgets;

public enum SettingsCategory
{
    General,
    Privacy,
    Appearance,
    StartHomeNewTab,
    Downloads,
    Advanced,
    System,
    Favorites,
    About
}

public class SettingsPageWidget : Widget
{
    private readonly float _sidebarWidth = 200;
    private readonly float _padding = 24;
    
    private SettingsCategory _selectedCategory = SettingsCategory.General;
    
    // Sidebar item rects for hit testing
    private Dictionary<SettingsCategory, SKRect> _sidebarItemRects = new();
    
    // Startup Radio Rects
    private Dictionary<StartupBehavior, SKRect> _startupRadioRects = new();
    
    // General Controls
    private TextInputWidget _homePageInput;
    private DropdownWidget _searchEngineDropdown;
    private SwitchWidget _restoreTabsSwitch;
    
    // Start, Home, New Tab Controls
    private ButtonWidget _addStartupSiteButton;
    private ButtonWidget _useCurrentPagesButton;
    private TextInputWidget _newStartupUrlInput;
    private List<ButtonWidget> _deleteStartupUrlButtons = new();
    
    // Privacy Controls
    private SwitchWidget _switchJs;
    private SwitchWidget _switchTracking;
    private SwitchWidget _switchDoNotTrack;
    private SwitchWidget _switchBlockCookies;
    private SwitchWidget _switchClearOnExit;
    private SwitchWidget _switchSecureDNS;
    private SwitchWidget _switchSafeBrowsing;
    private SwitchWidget _switchImproveBrowser;
    private SwitchWidget _switchBlockPopups;
    private ButtonWidget _clearDataButton;
    
    // Appearance Controls
    private SwitchWidget _switchTheme;
    private SwitchWidget _showHomeButtonSwitch;
    private SwitchWidget _showFavoritesBarSwitch;
    private SwitchWidget _showFavoritesButtonSwitch;
    private DropdownWidget _defaultZoomDropdown;
    private DropdownWidget _fontSizeDropdown;
    
    // Downloads Controls
    private TextInputWidget _downloadPathInput;
    private SwitchWidget _askDownloadLocation;
    private SwitchWidget _openFolderOnStart;
    
    // Advanced Controls
    private DropdownWidget _userAgentDropdown;
    private SwitchWidget _showDevTools;
    private SwitchWidget _enableLogging;
    private TextInputWidget _logPathInput;
    private DropdownWidget _logLevelDropdown;
    // Log category switches
    private SwitchWidget _logCatNetwork;
    private SwitchWidget _logCatHtmlParsing;
    private SwitchWidget _logCatCss;
    private SwitchWidget _logCatJavaScript;
    private SwitchWidget _logCatLayout;
    private SwitchWidget _logCatRendering;

    // System Controls
    private SwitchWidget _hardwareAccelSwitch;
    private SwitchWidget _sleepingTabsSwitch;
    private SwitchWidget _runInBackgroundSwitch;
    
    // Favorites Controls
    private TextInputWidget _newBookmarkTitle;
    private TextInputWidget _newBookmarkUrl;
    private ButtonWidget _addBookmarkButton;
    private List<ButtonWidget> _deleteBookmarkButtons = new();
    
    private static SKTypeface _headerFont = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
    private static SKTypeface _labelFont = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    private SKBitmap _aboutIcon;
    
    public SettingsPageWidget()
    {
        Name = "Settings Page";
        InitializeControls();
    }
    
    private void InitializeControls()
    {
        // === General ===
        _homePageInput = new TextInputWidget { Placeholder = "Enter home page URL" };
        _homePageInput.Text = BrowserSettings.Instance.HomePage;
        _homePageInput.TextChanged += (val) => {
            BrowserSettings.Instance.HomePage = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_homePageInput);
        
        _searchEngineDropdown = new DropdownWidget();
        _searchEngineDropdown.Options = new List<string> { "Google", "Bing", "DuckDuckGo", "Brave" };
        _searchEngineDropdown.SelectedIndex = _searchEngineDropdown.Options.IndexOf(BrowserSettings.Instance.SearchEngine);
        if (_searchEngineDropdown.SelectedIndex < 0) _searchEngineDropdown.SelectedIndex = 0;
        _searchEngineDropdown.SelectionChanged += (idx, val) => {
            BrowserSettings.Instance.SearchEngine = val;
            BrowserSettings.Instance.SearchEngineUrl = val switch {
                "Bing" => "https://www.bing.com/search?q=",
                "DuckDuckGo" => "https://duckduckgo.com/?q=",
                "Brave" => "https://search.brave.com/search?q=",
                _ => "https://www.google.com/search?q="
            };
            BrowserSettings.Instance.Save();
        };
        AddChild(_searchEngineDropdown);
        
        // === Start, Home, and New Tab ===
        _addStartupSiteButton = new ButtonWidget { Text = "Add a new page" };
        _addStartupSiteButton.Clicked += () => {
            if (!string.IsNullOrWhiteSpace(_newStartupUrlInput.Text))
            {
                BrowserSettings.Instance.StartupUrls.Add(_newStartupUrlInput.Text);
                BrowserSettings.Instance.Save();
                _newStartupUrlInput.Text = "";
                Invalidate();
            }
        };
        AddChild(_addStartupSiteButton);
        
        _useCurrentPagesButton = new ButtonWidget { Text = "Use all open tabs" };
        _useCurrentPagesButton.Clicked += () => {
             BrowserSettings.Instance.StartupUrls.Clear();
             foreach(var tab in FenBrowser.Host.Tabs.TabManager.Instance.Tabs)
             {
                 if (!string.IsNullOrEmpty(tab.Url) && !tab.Url.StartsWith("fen://") && !tab.Url.StartsWith("about:"))
                 {
                      BrowserSettings.Instance.StartupUrls.Add(tab.Url);
                 }
             }
             BrowserSettings.Instance.Save();
             Invalidate();
        };
        AddChild(_useCurrentPagesButton);
        
        _newStartupUrlInput = new TextInputWidget { Placeholder = "Enter URL" };
        AddChild(_newStartupUrlInput);
        
        _restoreTabsSwitch = new SwitchWidget();
        _restoreTabsSwitch.IsChecked = BrowserSettings.Instance.RestoreTabsOnStartup;
        _restoreTabsSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.RestoreTabsOnStartup = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_restoreTabsSwitch);

        _restoreTabsSwitch = new SwitchWidget();
        _restoreTabsSwitch.IsChecked = BrowserSettings.Instance.RestoreTabsOnStartup;
        _restoreTabsSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.RestoreTabsOnStartup = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_restoreTabsSwitch);
        
        // === Privacy ===
        _switchJs = new SwitchWidget();
        _switchJs.IsChecked = BrowserSettings.Instance.EnableJavaScript;
        _switchJs.CheckedChanged += (val) => {
            BrowserSettings.Instance.EnableJavaScript = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchJs);
        
        _switchTracking = new SwitchWidget();
        _switchTracking.IsChecked = BrowserSettings.Instance.EnableTrackingPrevention;
        _switchTracking.CheckedChanged += (val) => {
            BrowserSettings.Instance.EnableTrackingPrevention = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchTracking);
        
        _switchDoNotTrack = new SwitchWidget();
        _switchDoNotTrack.IsChecked = BrowserSettings.Instance.SendDoNotTrack;
        _switchDoNotTrack.CheckedChanged += (val) => {
            BrowserSettings.Instance.SendDoNotTrack = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchDoNotTrack);
        
        _switchBlockCookies = new SwitchWidget();
        _switchBlockCookies.IsChecked = BrowserSettings.Instance.BlockThirdPartyCookies;
        _switchBlockCookies.CheckedChanged += (val) => {
            BrowserSettings.Instance.BlockThirdPartyCookies = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchBlockCookies);
        
        _switchClearOnExit = new SwitchWidget();
        _switchClearOnExit.IsChecked = BrowserSettings.Instance.ClearCookiesOnExit;
        _switchClearOnExit.CheckedChanged += (val) => {
            BrowserSettings.Instance.ClearCookiesOnExit = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchClearOnExit);

        _switchSecureDNS = new SwitchWidget();
        _switchSecureDNS.IsChecked = BrowserSettings.Instance.UseSecureDNS;
        _switchSecureDNS.CheckedChanged += (val) => {
            BrowserSettings.Instance.UseSecureDNS = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchSecureDNS);

        _switchSafeBrowsing = new SwitchWidget();
        _switchSafeBrowsing.IsChecked = BrowserSettings.Instance.SafeBrowsing;
        _switchSafeBrowsing.CheckedChanged += (val) => {
            BrowserSettings.Instance.SafeBrowsing = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchSafeBrowsing);

        _switchImproveBrowser = new SwitchWidget();
        _switchImproveBrowser.IsChecked = BrowserSettings.Instance.ImproveBrowser;
        _switchImproveBrowser.CheckedChanged += (val) => {
            BrowserSettings.Instance.ImproveBrowser = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchImproveBrowser);

        _switchBlockPopups = new SwitchWidget();
        _switchBlockPopups.IsChecked = BrowserSettings.Instance.BlockPopups;
        _switchBlockPopups.CheckedChanged += (val) => {
            BrowserSettings.Instance.BlockPopups = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_switchBlockPopups);

        _clearDataButton = new ButtonWidget { Text = "Clear now" };
        _clearDataButton.Clicked += () => {
            EngineLogBridge.Info("Clearing browsing data...", LogCategory.General);
            // In a real implementation we would call a cleanup service here
        };
        AddChild(_clearDataButton);
        
        // === Appearance ===
        _switchTheme = new SwitchWidget();
        _switchTheme.IsChecked = ThemeManager.IsDark;
        _switchTheme.CheckedChanged += (val) => {
            ThemeManager.SetTheme(val); // Explicitly set based on switch value
            Parent?.Invalidate();
        };
        AddChild(_switchTheme);

        _showHomeButtonSwitch = new SwitchWidget();
        _showHomeButtonSwitch.IsChecked = BrowserSettings.Instance.ShowHomeButton;
        _showHomeButtonSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.ShowHomeButton = val;
            BrowserSettings.Instance.Save();
            RefreshLayout();
        };
        AddChild(_showHomeButtonSwitch);

        _showFavoritesBarSwitch = new SwitchWidget();
        _showFavoritesBarSwitch.IsChecked = BrowserSettings.Instance.ShowFavoritesBar;
        _showFavoritesBarSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.ShowFavoritesBar = val;
            BrowserSettings.Instance.Save();
            RefreshLayout();
        };
        AddChild(_showFavoritesBarSwitch);

        _showFavoritesButtonSwitch = new SwitchWidget();
        _showFavoritesButtonSwitch.IsChecked = BrowserSettings.Instance.ShowFavoritesButton;
        _showFavoritesButtonSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.ShowFavoritesButton = val;
            BrowserSettings.Instance.Save();
            RefreshLayout();
        };
        AddChild(_showFavoritesButtonSwitch);

        _defaultZoomDropdown = new DropdownWidget();
        _defaultZoomDropdown.Options = new List<string> { "25%", "50%", "75%", "100%", "125%", "150%", "200%" };
        string zoomStr = $"{(int)(BrowserSettings.Instance.DefaultZoom * 100)}%";
        _defaultZoomDropdown.SelectedIndex = _defaultZoomDropdown.Options.IndexOf(zoomStr);
        if (_defaultZoomDropdown.SelectedIndex < 0) _defaultZoomDropdown.SelectedIndex = 3; // 100%
        _defaultZoomDropdown.SelectionChanged += (idx, val) => {
            string cleaned = val.Replace("%", "");
            if (double.TryParse(cleaned, out double zoomPct)) {
                BrowserSettings.Instance.DefaultZoom = zoomPct / 100.0;
                BrowserSettings.Instance.Save();
            }
        };
        AddChild(_defaultZoomDropdown);

        _fontSizeDropdown = new DropdownWidget();
        _fontSizeDropdown.Options = new List<string> { "Very Small", "Small", "Medium", "Large", "Very Large" };
        _fontSizeDropdown.SelectedIndex = _fontSizeDropdown.Options.IndexOf(BrowserSettings.Instance.FontSize);
        if (_fontSizeDropdown.SelectedIndex < 0) _fontSizeDropdown.SelectedIndex = 2; // Medium
        _fontSizeDropdown.SelectionChanged += (idx, val) => {
            BrowserSettings.Instance.FontSize = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_fontSizeDropdown);
        
        // === Downloads ===
        _downloadPathInput = new TextInputWidget { Placeholder = "Download folder path" };
        _downloadPathInput.Text = BrowserSettings.Instance.DownloadPath;
        _downloadPathInput.TextChanged += (val) => {
            BrowserSettings.Instance.DownloadPath = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_downloadPathInput);
        
        _askDownloadLocation = new SwitchWidget();
        _askDownloadLocation.IsChecked = BrowserSettings.Instance.AskDownloadLocation;
        _askDownloadLocation.CheckedChanged += (val) => {
            BrowserSettings.Instance.AskDownloadLocation = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_askDownloadLocation);

        _openFolderOnStart = new SwitchWidget();
        _openFolderOnStart.IsChecked = BrowserSettings.Instance.OpenDownloadFolderOnStart;
        _openFolderOnStart.CheckedChanged += (val) => {
            BrowserSettings.Instance.OpenDownloadFolderOnStart = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_openFolderOnStart);
        
        // === Advanced ===
        _userAgentDropdown = new DropdownWidget();
        _userAgentDropdown.Options = new List<string> { "Chrome", "Firefox", "FenBrowser" };
        _userAgentDropdown.SelectedIndex = (int)BrowserSettings.Instance.SelectedUserAgent;
        _userAgentDropdown.SelectionChanged += (idx, val) => {
            BrowserSettings.Instance.SelectedUserAgent = (UserAgentType)idx;
            BrowserSettings.Instance.Save();
        };
        AddChild(_userAgentDropdown);
        
        _showDevTools = new SwitchWidget();
        _showDevTools.IsChecked = BrowserSettings.Instance.ShowDeveloperTools;
        _showDevTools.CheckedChanged += (val) => {
            BrowserSettings.Instance.ShowDeveloperTools = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_showDevTools);
        
        _enableLogging = new SwitchWidget();
        _enableLogging.IsChecked = BrowserSettings.Instance.Logging.EnableLogging;
        _enableLogging.CheckedChanged += (val) => {
            BrowserSettings.Instance.Logging.EnableLogging = val;
            BrowserSettings.Instance.Save();
            // Apply immediately to engine logging runtime
            FenBrowser.Core.Logging.EngineLog.InitializeFromSettings();
        };
        AddChild(_enableLogging);
        
        _logPathInput = new TextInputWidget { Placeholder = "Log folder path" };
        _logPathInput.Text = BrowserSettings.Instance.Logging.LogPath;
        _logPathInput.TextChanged += (val) => {
            BrowserSettings.Instance.Logging.LogPath = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_logPathInput);
        
        _logLevelDropdown = new DropdownWidget();
        _logLevelDropdown.Options = new List<string> { "Error", "Warn", "Info", "Debug", "Trace" };
        _logLevelDropdown.SelectedIndex = BrowserSettings.Instance.Logging.MinimumLevel;
        _logLevelDropdown.SelectionChanged += (idx, val) => {
            BrowserSettings.Instance.Logging.MinimumLevel = idx;
            BrowserSettings.Instance.Save();
            FenBrowser.Core.Logging.EngineLog.InitializeFromSettings();
        };
        AddChild(_logLevelDropdown);
        
        // Log category switches
        var cats = BrowserSettings.Instance.Logging.EnabledCategories;
        
        _logCatNetwork = new SwitchWidget();
        _logCatNetwork.IsChecked = (cats & (1 << 4)) != 0; // Network = 1 << 4
        _logCatNetwork.CheckedChanged += (val) => UpdateLogCategory(4, val);
        AddChild(_logCatNetwork);
        
        _logCatHtmlParsing = new SwitchWidget();
        _logCatHtmlParsing.IsChecked = (cats & (1 << 13)) != 0; // HtmlParsing = 1 << 13
        _logCatHtmlParsing.CheckedChanged += (val) => UpdateLogCategory(13, val);
        AddChild(_logCatHtmlParsing);
        
        _logCatCss = new SwitchWidget();
        _logCatCss.IsChecked = (cats & (1 << 2)) != 0; // CSS = 1 << 2
        _logCatCss.CheckedChanged += (val) => UpdateLogCategory(2, val);
        AddChild(_logCatCss);
        
        _logCatJavaScript = new SwitchWidget();
        _logCatJavaScript.IsChecked = (cats & (1 << 3)) != 0; // JavaScript = 1 << 3
        _logCatJavaScript.CheckedChanged += (val) => UpdateLogCategory(3, val);
        AddChild(_logCatJavaScript);
        
        _logCatLayout = new SwitchWidget();
        _logCatLayout.IsChecked = (cats & (1 << 6)) != 0; // Layout = 1 << 6
        _logCatLayout.CheckedChanged += (val) => UpdateLogCategory(6, val);
        AddChild(_logCatLayout);
        
        _logCatRendering = new SwitchWidget();
        _logCatRendering.IsChecked = (cats & (1 << 1)) != 0; // Rendering = 1 << 1
        _logCatRendering.CheckedChanged += (val) => UpdateLogCategory(1, val);
        AddChild(_logCatRendering);

        // === System ===
        _hardwareAccelSwitch = new SwitchWidget();
        _hardwareAccelSwitch.IsChecked = BrowserSettings.Instance.HardwareAcceleration;
        _hardwareAccelSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.HardwareAcceleration = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_hardwareAccelSwitch);

        _sleepingTabsSwitch = new SwitchWidget();
        _sleepingTabsSwitch.IsChecked = BrowserSettings.Instance.EnableSleepingTabs;
        _sleepingTabsSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.EnableSleepingTabs = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_sleepingTabsSwitch);

        _runInBackgroundSwitch = new SwitchWidget();
        _runInBackgroundSwitch.CheckedChanged += (val) => {
            BrowserSettings.Instance.RunInBackground = val;
            BrowserSettings.Instance.Save();
        };
        AddChild(_runInBackgroundSwitch);

        // === Favorites ===
        _newBookmarkTitle = new TextInputWidget { Placeholder = "Title" };
        AddChild(_newBookmarkTitle);
        _newBookmarkUrl = new TextInputWidget { Placeholder = "URL" };
        AddChild(_newBookmarkUrl);
        _addBookmarkButton = new ButtonWidget { Text = "Add Favorite" };
        _addBookmarkButton.Clicked += () => {
            if (!string.IsNullOrWhiteSpace(_newBookmarkTitle.Text) && !string.IsNullOrWhiteSpace(_newBookmarkUrl.Text)) {
                BrowserSettings.Instance.Bookmarks.Add(new Bookmark { Title = _newBookmarkTitle.Text, Url = _newBookmarkUrl.Text });
                BrowserSettings.Instance.Save();
                _newBookmarkTitle.Text = "";
                _newBookmarkUrl.Text = "";
                RefreshFavoritesUI();
            }
        };
        AddChild(_addBookmarkButton);
    }
    
    private void UpdateLogCategory(int bit, bool enabled)
    {
        var current = BrowserSettings.Instance.Logging.EnabledCategories;
        if (enabled)
            current |= (1 << bit);
        else
            current &= ~(1 << bit);
        BrowserSettings.Instance.Logging.EnabledCategories = current;
        BrowserSettings.Instance.Save();
        FenBrowser.Core.Logging.EngineLog.InitializeFromSettings();
    }

    private void RefreshLayout()
    {
        // Traverse parent hierarchy to find RootWidget
        Widget current = Parent;
        while (current != null && !(current is RootWidget))
        {
            current = current.Parent;
        }
        
        if (current is RootWidget root)
        {
            EngineLogBridge.Info($"[Settings] RefreshLayout found RootWidget. Calling RefreshBookmarks and Invalidate.", FenBrowser.Core.Logging.LogCategory.General);
            root.BookmarksBar.RefreshBookmarks();
            root.InvalidateLayout();
            root.Invalidate();
        }
    }

    private void RefreshFavoritesUI()
    {
        // Refresh the BookmarksBar in Root if possible
        // This is a bit tricky as we don't have direct access here easily without more wiring
        // But we can signal Parent to invalidate or use a global event.
        // For now, let's assume UI will reflect on next open or we find a way to notify.
        
        // Actually, let's try to find RootWidget
        var root = Parent as RootWidget;
        if (root == null && Parent is WebContentWidget wcw) root = wcw.Parent as RootWidget;
        
        root?.BookmarksBar.RefreshBookmarks();
        Invalidate();
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSpace);
        }
        return availableSpace;
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        Bounds = finalRect;
        
        float contentLeft = finalRect.Left + _sidebarWidth + _padding * 2;
        float contentTop = finalRect.Top + 80;
        float controlWidth = 250;
        // Position switches based on observed click coordinates - around X=900-950 for 1920px window
        float switchX = finalRect.Left + _sidebarWidth + 700; // ~900px from left edge
        
        // Arrange controls based on selected category
        float currentY = contentTop;
        
        // Hide all controls first
        foreach (var child in Children) child.IsVisible = false;
        
        switch (_selectedCategory)
        {
            case SettingsCategory.General:
                _searchEngineDropdown.IsVisible = true;
                _searchEngineDropdown.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 180, currentY + 57));
                currentY += 70;
                break;
                
            case SettingsCategory.StartHomeNewTab:
                // 1. Startup Action (Radios - manual paint, but we arrange controls if any exist)
                // We have no widgets for radios themselves, just rects.
                // Reserve space.
                currentY += 140; // Space for Radio options
                
                // If "Specific Page" is selected (Enum index 2), show custom list
                if (BrowserSettings.Instance.StartupAction == StartupBehavior.OpenSpecificPage)
                {
                    float listY = currentY;
                    _newStartupUrlInput.IsVisible = true;
                    _newStartupUrlInput.Arrange(new SKRect(contentLeft, listY, contentLeft + 300, listY + 32));
                    
                    _addStartupSiteButton.IsVisible = true;
                    _addStartupSiteButton.Arrange(new SKRect(contentLeft + 310, listY, contentLeft + 420, listY + 32));
                    
                    _useCurrentPagesButton.IsVisible = true;
                    _useCurrentPagesButton.Arrange(new SKRect(contentLeft + 430, listY, contentLeft + 580, listY + 32));
                    
                    currentY += 50;
                    
                    // Specific sites list space
                    currentY += BrowserSettings.Instance.StartupUrls.Count * 40;
                }
                
                currentY += 40;
                
                // 2. Home Button
                _showHomeButtonSwitch.IsVisible = true;
                _showHomeButtonSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                
                if (BrowserSettings.Instance.ShowHomeButton)
                {
                    currentY += 50;
                    _homePageInput.IsVisible = true;
                    _homePageInput.Arrange(new SKRect(contentLeft, currentY + 10, contentLeft + 350, currentY + 42));
                }
                currentY += 60;
                
                // 3. New Tab Page options (if any)
                break;
                
            case SettingsCategory.Privacy:
                _switchJs.IsVisible = true;
                _switchJs.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _switchTracking.IsVisible = true;
                _switchTracking.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _switchDoNotTrack.IsVisible = true;
                _switchDoNotTrack.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _switchBlockCookies.IsVisible = true;
                _switchBlockCookies.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _switchClearOnExit.IsVisible = true;
                _switchClearOnExit.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _switchSecureDNS.IsVisible = true;
                _switchSecureDNS.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _switchSafeBrowsing.IsVisible = true;
                _switchSafeBrowsing.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _switchBlockPopups.IsVisible = true;
                _switchBlockPopups.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 80;

                _clearDataButton.IsVisible = true;
                // Align button to the right side (same as switch X) but adjust width
                _clearDataButton.Arrange(new SKRect(switchX - 40, currentY + 10, switchX + 50, currentY + 42));
                break;
                
            case SettingsCategory.Appearance:
                _switchTheme.IsVisible = true;
                _switchTheme.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _showFavoritesBarSwitch.IsVisible = true;
                _showFavoritesBarSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _showFavoritesButtonSwitch.IsVisible = true;
                _showFavoritesButtonSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 70;

                _defaultZoomDropdown.IsVisible = true;
                _defaultZoomDropdown.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 120, currentY + 57));
                currentY += 70;

                _fontSizeDropdown.IsVisible = true;
                _fontSizeDropdown.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 150, currentY + 57));
                break;
                
            case SettingsCategory.Downloads:
                _downloadPathInput.IsVisible = true;
                _downloadPathInput.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 350, currentY + 57));
                currentY += 70;
                
                _askDownloadLocation.IsVisible = true;
                _askDownloadLocation.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _openFolderOnStart.IsVisible = true;
                _openFolderOnStart.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                break;
                
            case SettingsCategory.Advanced:
                _userAgentDropdown.IsVisible = true;
                _userAgentDropdown.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 180, currentY + 57));
                currentY += 70;
                
                _showDevTools.IsVisible = true;
                _showDevTools.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _enableLogging.IsVisible = true;
                _enableLogging.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;
                
                _logPathInput.IsVisible = true;
                _logPathInput.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 400, currentY + 57));
                currentY += 70;
                
                _logLevelDropdown.IsVisible = true;
                _logLevelDropdown.Arrange(new SKRect(contentLeft, currentY + 25, contentLeft + 120, currentY + 57));
                currentY += 80;
                
                // Log category switches (2 columns)
                float col1X = contentLeft;
                float col2X = contentLeft + 250;
                
                _logCatNetwork.IsVisible = true;
                _logCatNetwork.Arrange(new SKRect(col1X + 100, currentY + 8, col1X + 150, currentY + 32));
                
                _logCatHtmlParsing.IsVisible = true;
                _logCatHtmlParsing.Arrange(new SKRect(col2X + 100, currentY + 8, col2X + 150, currentY + 32));
                currentY += 40;
                
                _logCatCss.IsVisible = true;
                _logCatCss.Arrange(new SKRect(col1X + 100, currentY + 8, col1X + 150, currentY + 32));
                
                _logCatJavaScript.IsVisible = true;
                _logCatJavaScript.Arrange(new SKRect(col2X + 100, currentY + 8, col2X + 150, currentY + 32));
                currentY += 40;
                
                _logCatLayout.IsVisible = true;
                _logCatLayout.Arrange(new SKRect(col1X + 100, currentY + 8, col1X + 150, currentY + 32));
                
                _logCatRendering.IsVisible = true;
                _logCatRendering.Arrange(new SKRect(col2X + 100, currentY + 8, col2X + 150, currentY + 32));
                break;
            
            case SettingsCategory.System:
                _hardwareAccelSwitch.IsVisible = true;
                _hardwareAccelSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _sleepingTabsSwitch.IsVisible = true;
                _sleepingTabsSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                currentY += 60;

                _runInBackgroundSwitch.IsVisible = true;
                _runInBackgroundSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                break;
                
            case SettingsCategory.Favorites:
                _newBookmarkTitle.IsVisible = true;
                _newBookmarkTitle.Arrange(new SKRect(contentLeft, currentY + 20, contentLeft + 150, currentY + 52));
                _newBookmarkUrl.IsVisible = true;
                _newBookmarkUrl.Arrange(new SKRect(contentLeft + 160, currentY + 20, contentLeft + 360, currentY + 52));
                _addBookmarkButton.IsVisible = true;
                _addBookmarkButton.Arrange(new SKRect(contentLeft + 370, currentY + 20, contentLeft + 480, currentY + 52));
                currentY += 80;
                
                // Delete buttons are dynamic, but we can't easily arrange them here 
                // without keeping track of them. Let's handle them in Paint/OnMouseMove
                // for simplicity in this hacky DockPanel-based UI.
                break;
                
            case SettingsCategory.About:
                // No controls, just text painted
                break;
        }
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Main Background
        using var bgPaint = new SKPaint { Color = theme.Background, IsAntialias = false };
        canvas.DrawRect(Bounds, bgPaint);
        
        // Sidebar Background
        var sidebarRect = new SKRect(Bounds.Left, Bounds.Top, Bounds.Left + _sidebarWidth, Bounds.Bottom);
        using var sidePaint = new SKPaint { Color = theme.Surface, IsAntialias = false };
        canvas.DrawRect(sidebarRect, sidePaint);
        
        // Sidebar Border
        using var borderPaint = new SKPaint { Color = theme.Border, Style = SKPaintStyle.Stroke, IsAntialias = false };
        canvas.DrawLine(sidebarRect.Right, sidebarRect.Top, sidebarRect.Right, sidebarRect.Bottom, borderPaint);
        
        // Header "Settings"
        using var headerPaint = new SKPaint 
        { 
            Color = theme.Text, 
            IsAntialias = true, 
            TextSize = 28, 
            Typeface = _headerFont
        };
        canvas.DrawText("Settings", Bounds.Left + 20, Bounds.Top + 40, headerPaint);
        
        // Sidebar Items
        _sidebarItemRects.Clear();
        float sideItemY = Bounds.Top + 70;
        var categories = new[] {
            (SettingsCategory.General, "General", "⚙"),
            (SettingsCategory.Privacy, "Privacy & Security", "🔒"),
            (SettingsCategory.Appearance, "Appearance", "🎨"),
            (SettingsCategory.StartHomeNewTab, "Start, Home, and New Tab", "🏠"),
            (SettingsCategory.Downloads, "Downloads", "📥"),
            (SettingsCategory.Advanced, "Advanced", "🔧"),
            (SettingsCategory.System, "System", "💻"),
            (SettingsCategory.Favorites, "Favorites", "⭐"),
            (SettingsCategory.About, "About", "ℹ")
        };
        
        using var itemTextPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 14, Typeface = _labelFont };
        
        foreach (var (cat, name, icon) in categories)
        {
            var itemRect = new SKRect(Bounds.Left + 8, sideItemY, Bounds.Left + _sidebarWidth - 8, sideItemY + 36);
            _sidebarItemRects[cat] = itemRect;
            
            // Selection highlight
            if (_selectedCategory == cat)
            {
                using var pillPaint = new SKPaint { Color = theme.SurfacePressed, IsAntialias = true };
                canvas.DrawRoundRect(itemRect, 4, 4, pillPaint);
                
                // Accent bar
                using var accentPaint = new SKPaint { Color = theme.Accent, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(itemRect.Left, itemRect.Top + 6, itemRect.Left + 3, itemRect.Bottom - 6), 2, 2, accentPaint);
            }
            
            canvas.DrawText(name, itemRect.Left + 16, itemRect.MidY + 5, itemTextPaint);
            sideItemY += 40;
        }
        
        // Content Area
        float contentLeft = Bounds.Left + _sidebarWidth + _padding * 2;
        
        // Category Title
        using var subHeaderPaint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true,
            TextSize = 24,
            Typeface = _headerFont
        };
        
        string categoryTitle = _selectedCategory switch
        {
            SettingsCategory.General => "General",
            SettingsCategory.Privacy => "Privacy & Security",
            SettingsCategory.Appearance => "Appearance",
            SettingsCategory.Downloads => "Downloads",
            SettingsCategory.Advanced => "Advanced",
            SettingsCategory.System => "System",
            SettingsCategory.Favorites => "Favorites",
            SettingsCategory.About => "About FenBrowser",
            _ => ""
        };
        
        canvas.DrawText(categoryTitle, contentLeft, Bounds.Top + 50, subHeaderPaint);
        canvas.DrawLine(contentLeft, Bounds.Top + 65, Bounds.Right - _padding, Bounds.Top + 65, borderPaint);
        
        // Labels for current category
        using var labelPaint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 15, Typeface = _headerFont };
        using var descPaint = new SKPaint { Color = theme.TextMuted, IsAntialias = true, TextSize = 13, Typeface = _labelFont };
        
        float currentY = Bounds.Top + 80;
        float switchX = Bounds.Left + _sidebarWidth + 700;
        
        switch (_selectedCategory)
        {
            case SettingsCategory.General:
                canvas.DrawText("Search engine", contentLeft, currentY + 18, labelPaint);
                // Removed Home page and Startup Action drawings
                break;
                
            case SettingsCategory.StartHomeNewTab:
                canvas.DrawText("When FenBrowser starts", contentLeft, currentY + 20, labelPaint);
                currentY += 40;
                
                // Radio Options
                _startupRadioRects.Clear();
                var options = new[] { 
                    (StartupBehavior.OpenNewTab, "Open the new tab page"),
                    (StartupBehavior.RestoreLastSession, "Open tabs from previous session"),
                    (StartupBehavior.OpenSpecificPage, "Open these pages:")
                };
                
                foreach (var (behavior, text) in options)
                {
                    bool selected = BrowserSettings.Instance.StartupAction == behavior;
                    
                    // Radio Circle
                    float radioY = currentY + 15;
                    // Draw Outer Circle
                    using var radioBorder = new SKPaint { Color = selected ? theme.Accent : theme.TextMuted, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                    canvas.DrawCircle(contentLeft + 10, radioY, 8, radioBorder);
                    
                    if (selected)
                    {
                         using var radioFill = new SKPaint { Color = theme.Accent, Style = SKPaintStyle.Fill, IsAntialias = true };
                         canvas.DrawCircle(contentLeft + 10, radioY, 4, radioFill);
                    }
                    
                    // Text
                    canvas.DrawText(text, contentLeft + 30, radioY + 5, labelPaint);
                    
                    // Save Rect for hit testing
                    float textWidth = labelPaint.MeasureText(text);
                    _startupRadioRects[behavior] = new SKRect(contentLeft, currentY, contentLeft + 30 + textWidth + 20, currentY + 30);
                    
                    currentY += 35;
                }
                
                // Specific Pages List
                if (BrowserSettings.Instance.StartupAction == StartupBehavior.OpenSpecificPage)
                {
                    // Input/Buttons are children, handled by Arrange/base.Paint
                    currentY += 50; 
                    
                    var urls = BrowserSettings.Instance.StartupUrls;
                    // Note: We need to match Arrange's layout
                    float listY = currentY;
                    
                    foreach (var url in urls)
                    {
                        canvas.DrawText(url, contentLeft + 10, listY + 20, labelPaint);
                        
                        // Draw "Remove" button visual
                        var delRect = new SKRect(contentLeft + 400, listY, contentLeft + 470, listY + 32);
                        using var delPaint = new SKPaint { Color = SKColors.Red.WithAlpha(30), IsAntialias = true };
                        canvas.DrawRoundRect(delRect, 4, 4, delPaint);
                        using var delTextPaint = new SKPaint { Color = SKColors.Red, IsAntialias = true, TextSize = 12, TextAlign = SKTextAlign.Center };
                        canvas.DrawText("Remove", delRect.MidX, delRect.MidY + 4, delTextPaint);
                        
                        listY += 40;
                    }
                    currentY = listY; // Update currentY for next elements
                }
                
                currentY += 40;
                canvas.DrawLine(contentLeft, currentY, Bounds.Right - _padding, currentY, borderPaint);
                currentY += 30;
                
                // Home Button
                canvas.DrawText("Home button", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Show home button on the toolbar", contentLeft, currentY + 38, descPaint);
                
                if (BrowserSettings.Instance.ShowHomeButton)
                {
                    currentY += 80;
                    canvas.DrawText("Set home page URL", contentLeft, currentY, descPaint);
                }
                break;
                
            case SettingsCategory.Privacy:
                canvas.DrawText("JavaScript", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Allow sites to run scripts", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Tracking Prevention", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Block known trackers", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Send \"Do Not Track\"", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Request sites not to track you", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Block Third-Party Cookies", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Prevent cross-site tracking", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Clear cookies on exit", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Delete all cookies when browser closes", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Use Secure DNS", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Resolve hostnames via DNS-over-HTTPS when available", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Safe Browsing", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Protects you and your device from dangerous sites", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Block pop-ups", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Prevent websites from opening unnecessary windows", contentLeft, currentY + 38, descPaint);
                currentY += 40;
                canvas.DrawLine(contentLeft, currentY + 20, Bounds.Right - _padding, currentY + 20, borderPaint);
                currentY += 50;
                canvas.DrawText("Clear browsing data", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Delete history, cookies, and cache now", contentLeft, currentY + 38, descPaint);
                break;
                
            case SettingsCategory.Appearance:
                canvas.DrawText("Dark Mode", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Switch between light and dark themes", contentLeft, currentY + 38, descPaint);
                currentY += 60;

                _showFavoritesBarSwitch.IsVisible = true;
                _showFavoritesBarSwitch.Arrange(new SKRect(switchX, currentY + 8, switchX + 50, currentY + 32));
                // Note: Re-enabling visibility here is redundant if Arrange handles it but harmless for paint logic flow
                
                canvas.DrawText("Show favorites bar", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Always show shortcuts below address bar", contentLeft, currentY + 38, descPaint);
                currentY += 60;

                canvas.DrawText("Show favorites button", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Show the favorites star in the toolbar", contentLeft, currentY + 38, descPaint);
                currentY += 70;
                canvas.DrawText("Default zoom", contentLeft, currentY + 18, labelPaint);
                currentY += 70;
                canvas.DrawText("Font size", contentLeft, currentY + 18, labelPaint);
                break;
                
            case SettingsCategory.Downloads:
                canvas.DrawText("Download location", contentLeft, currentY + 18, labelPaint);
                currentY += 70;
                canvas.DrawText("Ask where to save", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Prompt for download location each time", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Open folder on startup", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Automatically open downloads when browser starts", contentLeft, currentY + 38, descPaint);
                break;
                
            case SettingsCategory.Advanced:
                canvas.DrawText("User Agent", contentLeft, currentY + 18, labelPaint);
                currentY += 70;
                canvas.DrawText("Developer Tools", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Show developer tools in context menu", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Debug Logging", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Enable detailed logging for debugging", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Log save location", contentLeft, currentY + 18, labelPaint);
                currentY += 70;
                canvas.DrawText("Log level", contentLeft, currentY + 18, labelPaint);
                canvas.DrawText("Filter by severity (Error, Warn, Info, Debug, Trace)", contentLeft, currentY + 36, descPaint);
                currentY += 80;
                
                // Category labels in 2 columns
                float col1X = contentLeft;
                float col2X = contentLeft + 250;
                canvas.DrawText("Log Categories:", contentLeft, currentY - 10, labelPaint);
                
                canvas.DrawText("Network", col1X, currentY + 20, labelPaint);
                canvas.DrawText("HTML Parsing", col2X, currentY + 20, labelPaint);
                currentY += 40;
                
                canvas.DrawText("CSS", col1X, currentY + 20, labelPaint);
                canvas.DrawText("JavaScript", col2X, currentY + 20, labelPaint);
                currentY += 40;
                
                canvas.DrawText("Layout", col1X, currentY + 20, labelPaint);
                canvas.DrawText("Rendering", col2X, currentY + 20, labelPaint);
                break;
            
            case SettingsCategory.System:
                canvas.DrawText("Hardware Acceleration", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Use GPU for rendering when possible", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Sleeping Tabs", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Save resources by putting inactive tabs to sleep", contentLeft, currentY + 38, descPaint);
                currentY += 60;
                canvas.DrawText("Continue running background apps", contentLeft, currentY + 20, labelPaint);
                canvas.DrawText("Keep browser running after closing last window", contentLeft, currentY + 38, descPaint);
                break;

            case SettingsCategory.Favorites:
                canvas.DrawText("Add new favorite", contentLeft, currentY - 5, labelPaint);
                currentY += 80;
                canvas.DrawLine(contentLeft, currentY, Bounds.Right - _padding, currentY, borderPaint);
                currentY += 30;
                
                var bookmarks = BrowserSettings.Instance.Bookmarks;
                _deleteBookmarkButtons.Clear(); // Just for logic tracking if needed, but we draw manually
                
                foreach (var bm in bookmarks)
                {
                    canvas.DrawText(bm.Title, contentLeft, currentY + 15, labelPaint);
                    canvas.DrawText(bm.Url, contentLeft, currentY + 32, descPaint);
                    
                    // Delete "button" rect
                    var delRect = new SKRect(Bounds.Right - _padding - 80, currentY, Bounds.Right - _padding, currentY + 32);
                    using var delPaint = new SKPaint { Color = SKColors.Red.WithAlpha(30), IsAntialias = true };
                    canvas.DrawRoundRect(delRect, 4, 4, delPaint);
                    using var delTextPaint = new SKPaint { Color = SKColors.Red, IsAntialias = true, TextSize = 12, TextAlign = SKTextAlign.Center };
                    canvas.DrawText("Remove", delRect.MidX, delRect.MidY + 4, delTextPaint);
                    
                    currentY += 45;
                }
                break;
                
            case SettingsCategory.About:
                // Lazy load icon
                if (_aboutIcon == null)
                {
                    try
                    {
                         var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                         if (System.IO.File.Exists(iconPath))
                         {
                             _aboutIcon = SKBitmap.Decode(iconPath);
                         }
                    } catch {}
                }

                if (_aboutIcon != null)
                {
                    // Draw 64x64 icon
                    var iconRect = new SKRect(contentLeft, currentY, contentLeft + 64, currentY + 64);
                    using var iconPaint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                    canvas.DrawBitmap(_aboutIcon, iconRect, iconPaint);
                    
                    // Indent text
                    float textX = contentLeft + 80;
                    
                    var version = System.Reflection.Assembly.GetEntryAssembly()?
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault()?.InformationalVersion 
                        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) 
                        ?? "0.1.0-alpha";

                    canvas.DrawText("FenBrowser", textX, currentY + 25, subHeaderPaint);
                    canvas.DrawText($"Version {version} (Advanced Edition)", textX, currentY + 50, labelPaint);
                    
                    currentY += 80;
                }
                else
                {
                    var version = System.Reflection.Assembly.GetEntryAssembly()?
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault()?.InformationalVersion 
                        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) 
                        ?? "0.1.0-alpha";

                    canvas.DrawText("FenBrowser", contentLeft, currentY + 30, subHeaderPaint);
                    currentY += 50;
                    canvas.DrawText($"Version {version} (Advanced Edition)", contentLeft, currentY + 20, labelPaint);
                    currentY += 30;
                }
                
                canvas.DrawText("A modular, secure, and privacy-focused browser", contentLeft, currentY + 20, descPaint);
                currentY += 40;
                canvas.DrawText("Built with .NET, SkiaSharp, and Silk.NET", contentLeft, currentY + 20, descPaint);
                currentY += 40;
                canvas.DrawText("Inspired by modern web design principles", contentLeft, currentY + 20, descPaint);
                break;
        }
        
        // Paint visible children - Paint open dropdowns last to ensure they are on top
        DropdownWidget openDropdown = null;
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                if (child is DropdownWidget dw && dw.IsOpen)
                {
                    openDropdown = dw;
                    continue;
                }
                child.Paint(canvas);
            }
        }
        openDropdown?.Paint(canvas);
    }
    
    public override void OnMouseMove(float x, float y)
    {
        var mouse = InputManager.Instance.Mouse;
        if (mouse == null) return;

        bool overInteractive = false;

        // Route to children first (especially for dropdown popups)
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                child.OnMouseMove(x, y);

                // Update cursor if mouse is over an interactive child
                if (child.HitTest(x, y))
                {
                    if (child is SwitchWidget || child is DropdownWidget || child is ButtonWidget)
                    {
                        CursorManager.UpdateCursor(mouse, FenBrowser.FenEngine.Interaction.CursorType.Pointer);
                        overInteractive = true;
                    }
                    else if (child is TextInputWidget)
                    {
                        CursorManager.UpdateCursor(mouse, FenBrowser.FenEngine.Interaction.CursorType.Text);
                        overInteractive = true;
                    }
                }
            }
        }

        // Check sidebar items if not over a child
        if (!overInteractive)
        {
            foreach (var (cat, rect) in _sidebarItemRects)
            {
                if (rect.Contains(x, y))
                {
                    CursorManager.UpdateCursor(mouse, FenBrowser.FenEngine.Interaction.CursorType.Pointer);
                    overInteractive = true;
                    break;
                }
            }
        }

        if (!overInteractive)
        {
            CursorManager.ResetCursor(mouse);
        }
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (button != MouseButton.Left) return;

        // 1. Check if we hit an open dropdown
        DropdownWidget openDropdown = null;
        foreach (var child in Children)
        {
            if (child.IsVisible && child is DropdownWidget dw && dw.IsOpen)
            {
                openDropdown = dw;
                break;
            }
        }

        if (openDropdown != null)
        {
            if (openDropdown.HitTest(x, y))
            {
                openDropdown.OnMouseDown(x, y, button);
                return;
            }
            else
            {
                // Clicked outside open dropdown - close it
                openDropdown.Close();
                // continue to handle the click (it might be for another control)
            }
        }
        
        // Check sidebar clicks
        foreach (var (cat, rect) in _sidebarItemRects)
        {
            if (rect.Contains(x, y))
            {
                _selectedCategory = cat;
                // Re-arrange to show correct controls
                OnArrange(Bounds);
                // Reset scroll if we had it
                Invalidate();
                return;
            }
        }

        // Check Remove clicks in Favorites
        if (_selectedCategory == SettingsCategory.Favorites && button == MouseButton.Left)
        {
            float contentLeft = Bounds.Left + _sidebarWidth + _padding * 2;
            float currentY = Bounds.Top + 190;
            var bookmarks = BrowserSettings.Instance.Bookmarks;
            for (int i = 0; i < bookmarks.Count; i++)
            {
                var delRect = new SKRect(Bounds.Right - _padding - 80, currentY, Bounds.Right - _padding, currentY + 32);
                if (delRect.Contains(x, y))
                {
                    bookmarks.RemoveAt(i);
                    BrowserSettings.Instance.Save();
                    RefreshFavoritesUI();
                    return;
                }
                currentY += 45;
            }
        }
        
        // Check Start/Home interactions
        if (_selectedCategory == SettingsCategory.StartHomeNewTab && button == MouseButton.Left)
        {
            // 1. Radio Buttons
            foreach (var (behavior, rect) in _startupRadioRects)
            {
                if (rect.Contains(x, y))
                {
                    BrowserSettings.Instance.StartupAction = behavior;
                    BrowserSettings.Instance.Save();
                    OnArrange(Bounds);
                    Invalidate();
                    return;
                }
            }
            
            // 2. Remove Buttons for Custom Sites
            if (BrowserSettings.Instance.StartupAction == StartupBehavior.OpenSpecificPage)
            {
                float contentLeft = Bounds.Left + _sidebarWidth + _padding * 2;
                // Matches Paint logic: Top(80) + Header(40) + Radios(105) + Space(50) = 275 offset
                float listY = Bounds.Top + 275;
                
                var urls = BrowserSettings.Instance.StartupUrls;
                for (int i = 0; i < urls.Count; i++)
                {
                    var delRect = new SKRect(contentLeft + 400, listY, contentLeft + 470, listY + 32);
                    if (delRect.Contains(x, y))
                    {
                        urls.RemoveAt(i);
                        BrowserSettings.Instance.Save();
                        OnArrange(Bounds);
                        Invalidate();
                        return;
                    }
                    listY += 40;
                }
            }
        }
        
        // Route to visible children
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                bool hit = child.HitTest(x, y);
                if (hit)
                {
                    child.OnMouseDown(x, y, button);
                    return;
                }
            }
        }


        // Handle row clicks for Appearance (robustness for varying screen/clicks)
        if (_selectedCategory == SettingsCategory.Appearance && button == MouseButton.Left && x > Bounds.Left + _sidebarWidth)
        {
             float contentTop = Bounds.Top + 80;
             float currentY = contentTop; // Theme (80)
             
             // Theme: +60
             if (y >= currentY && y < currentY + 60) _switchTheme.Toggle();
             currentY += 60;
             
             // Home Button: +60
             if (y >= currentY && y < currentY + 60) _showHomeButtonSwitch.Toggle();
             currentY += 60;
             
             // Favorites Bar: +60
             if (y >= currentY && y < currentY + 60) _showFavoritesBarSwitch.Toggle();
             currentY += 60;
             
             // Favorites Button: +70
             if (y >= currentY && y < currentY + 70) _showFavoritesButtonSwitch.Toggle();
        }
    }
    
    public override void OnTextInput(char c, bool ctrl)
    {
        // Route to focused text input
        foreach (var child in Children)
        {
            if (child.IsVisible && child is TextInputWidget input && input.IsFocused)
            {
                input.OnTextInput(c, ctrl);
                return;
            }
        }
    }
    
    public override void OnKeyDown(Key key, bool ctrl, bool shift, bool alt)
    {
        foreach (var child in Children)
        {
            if (child.IsVisible && child is TextInputWidget input && input.IsFocused)
            {
                input.OnKeyDown(key, ctrl, shift, alt);
                return;
            }
        }
    }
}


