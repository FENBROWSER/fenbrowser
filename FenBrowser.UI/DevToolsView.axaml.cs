using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;

namespace FenBrowser.UI
{
    public partial class DevToolsView : UserControl
    {
        private IBrowser _browser;
        // Use ObservableCollection of structured items instead of strings
        private readonly ObservableCollection<ConsoleLogItem> _consoleLogs = new ObservableCollection<ConsoleLogItem>();
        
        // Panel references
        private Grid _elementsPanel;
        private Grid _consolePanel;
        private Grid _networkPanel;
        private Button _tabElements;
        private Button _tabConsole;
        private Button _tabNetwork;
        
        public event EventHandler CloseRequested;

        public DevToolsView()
        {
            InitializeComponent();
            
            // Get panel references
            _elementsPanel = this.FindControl<Grid>("ElementsPanel");
            _consolePanel = this.FindControl<Grid>("ConsolePanel");
            _networkPanel = this.FindControl<Grid>("NetworkPanel");
            _tabElements = this.FindControl<Button>("TabElements");
            _tabConsole = this.FindControl<Button>("TabConsole");
            _tabNetwork = this.FindControl<Button>("TabNetwork");
            
            // Bind the ListBox to our collection
            var listbox = this.FindControl<ListBox>("ConsoleOutput");
            if (listbox != null) listbox.ItemsSource = _consoleLogs;
            
            // Wire up button handlers
            var btnRefresh = this.FindControl<Button>("BtnRefresh");
            var btnClose = this.FindControl<Button>("BtnClose");
            var btnClear = this.FindControl<Button>("BtnClear");
            
            if (btnRefresh != null) btnRefresh.Click += (s, e) => RefreshDom();
            if (btnClose != null) btnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            if (btnClear != null) btnClear.Click += (s, e) => _consoleLogs.Clear();
        }
        
        private void OnTabClick(object sender, RoutedEventArgs e)
        {
            // Remove active class from all tabs
            _tabElements?.Classes.Remove("active");
            _tabConsole?.Classes.Remove("active");
            _tabNetwork?.Classes.Remove("active");
            
            // Hide all panels
            if (_elementsPanel != null) _elementsPanel.IsVisible = false;
            if (_consolePanel != null) _consolePanel.IsVisible = false;
            if (_networkPanel != null) _networkPanel.IsVisible = false;
            
            // Show selected tab and panel
            if (sender is Button btn)
            {
                btn.Classes.Add("active");
                
                if (btn == _tabElements && _elementsPanel != null)
                    _elementsPanel.IsVisible = true;
                else if (btn == _tabConsole && _consolePanel != null)
                    _consolePanel.IsVisible = true;
                else if (btn == _tabNetwork && _networkPanel != null)
                    _networkPanel.IsVisible = true;
            }
        }

        public void Attach(IBrowser browser)
        {
            try { FenLogger.Debug($"[DevTools] Attach called. Browser: {browser}", LogCategory.General); } catch { }
            if (_browser == browser) return;
            
            Detach();
            _browser = browser;

            if (_browser != null)
            {
                _browser.ConsoleMessage += OnConsoleMessage;
                RefreshDom();
            }
        }

        public void Detach()
        {
            if (_browser != null)
            {
                try { FenLogger.Debug("[DevTools] Detach called.", LogCategory.General); } catch { }
                _browser.ConsoleMessage -= OnConsoleMessage;
                _browser = null;
            }
        }

        private void OnConsoleMessage(string msg)
        {
            Dispatcher.UIThread.Post(() =>
            {
                 if (_consoleLogs.Count > 1000) _consoleLogs.RemoveAt(0);
                 
                 // Parser logic to detect log level
                 var level = ConsoleLogLevel.Info;
                 var cleanMsg = msg;

                 if (msg.StartsWith("[Error] ", StringComparison.OrdinalIgnoreCase)) 
                 {
                     level = ConsoleLogLevel.Error;
                     cleanMsg = msg.Substring(8);
                 }
                 else if (msg.StartsWith("[Warn] ", StringComparison.OrdinalIgnoreCase))
                 {
                     level = ConsoleLogLevel.Warning;
                     cleanMsg = msg.Substring(7);
                 }
                 else if (msg.StartsWith("[Info] ", StringComparison.OrdinalIgnoreCase))
                 {
                     level = ConsoleLogLevel.Info;
                     cleanMsg = msg.Substring(7);
                 }

                 _consoleLogs.Add(new ConsoleLogItem(cleanMsg, level));
                 
                 // Auto-scroll
                 var listbox = this.FindControl<ListBox>("ConsoleOutput");
                 if (listbox != null && _consoleLogs.Count > 0)
                 {
                     listbox.ScrollIntoView(_consoleLogs.Last());
                 }
            });
        }

        private void RefreshDom()
        {
            var domTree = this.FindControl<TreeView>("DomTree");
            if (domTree == null || _browser == null) return;
            
            try
            {
                var root = _browser.GetDomRoot();
                if (root != null)
                {
                    var viewModels = new List<DomElementModel> { DomElementModel.FromLiteElement(root) };
                    domTree.ItemsSource = viewModels;
                }
                else
                {
                    domTree.ItemsSource = new List<DomElementModel> 
                    { 
                        new DomElementModel { Tag = "html", TextPreview = "(No document loaded)" }
                    };
                }
            }
            catch (Exception ex)
            {
                domTree.ItemsSource = new List<DomElementModel> 
                { 
                    new DomElementModel { Tag = "error", TextPreview = ex.Message }
                };
            }
        }

        private void DomTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             // Placeholder for selection change - could highlight element in page
        }

        private async void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var txt = sender as TextBox;
                if (txt == null) return;
                var script = txt.Text;
                if (string.IsNullOrWhiteSpace(script)) return; // Don't log empty lines

                // Log user input
                _consoleLogs.Add(new ConsoleLogItem(script, ConsoleLogLevel.Input));
                txt.Text = "";
                
                // Scroll to bottom
                var listbox = this.FindControl<ListBox>("ConsoleOutput");
                if (listbox != null) listbox.ScrollIntoView(_consoleLogs.Last());

                try
                {
                    try { FenLogger.Debug($"[DevTools] Executing: {script}", LogCategory.General); } catch { }

                    if (_browser != null)
                    {
                        var result = await _browser.ExecuteScriptAsync(script);
                        // Log result
                        _consoleLogs.Add(new ConsoleLogItem(result?.ToString() ?? "undefined", ConsoleLogLevel.Result));
                        if (listbox != null) listbox.ScrollIntoView(_consoleLogs.Last());
                    }
                }
                catch (Exception ex)
                {
                    _consoleLogs.Add(new ConsoleLogItem(ex.Message, ConsoleLogLevel.Error));
                    try { FenLogger.Error($"[DevTools] Error executing script: {ex}", LogCategory.Errors, ex); } catch { }
                    if (listbox != null) listbox.ScrollIntoView(_consoleLogs.Last());
                }
            }
        }
    }
    
    /// <summary>
    /// ViewModel for DOM tree items
    /// </summary>
    public class DomElementModel
    {
        public string Tag { get; set; } = "";
        public string AttrString { get; set; } = "";
        public string TextPreview { get; set; } = "";
        public bool HasChildren => Children?.Count > 0;
        public List<DomElementModel> Children { get; set; } = new List<DomElementModel>();
        
        public static DomElementModel FromLiteElement(LiteElement el)
        {
            var model = new DomElementModel
            {
                Tag = el.Tag ?? "#text",
                AttrString = FormatAttributes(el.Attr),
                TextPreview = GetTextPreview(el)
            };
            
            if (el.Children != null)
            {
                foreach (var child in el.Children)
                {
                    model.Children.Add(FromLiteElement(child));
                }
            }
            
            return model;
        }
        
        private static string FormatAttributes(Dictionary<string, string> attr)
        {
            if (attr == null || attr.Count == 0) return "";
            
            var parts = new List<string>();
            foreach (var kv in attr.Take(3)) // Limit to 3 attrs for display
            {
                if (kv.Key == "class")
                    parts.Add($"class=\"{TruncateString(kv.Value, 20)}\"");
                else if (kv.Key == "id")
                    parts.Add($"id=\"{TruncateString(kv.Value, 20)}\"");
                else
                    parts.Add($"{kv.Key}=\"{TruncateString(kv.Value, 15)}\"");
            }
            if (attr.Count > 3) parts.Add("...");
            return string.Join(" ", parts);
        }
        
        private static string GetTextPreview(LiteElement el)
        {
            if (!string.IsNullOrWhiteSpace(el.Text))
            {
                return TruncateString(el.Text.Trim(), 50);
            }
            return "";
        }
        
        private static string TruncateString(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", "").Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }
}

