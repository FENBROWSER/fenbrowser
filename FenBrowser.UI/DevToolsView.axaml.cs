using Avalonia;
using Avalonia.Controls;
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
        
        public event EventHandler CloseRequested;

        public DevToolsView()
        {
            InitializeComponent();
            // FIX: Bind the ListBox to our collection
            var listbox = this.FindControl<ListBox>("ConsoleOutput");
            if (listbox != null) listbox.ItemsSource = _consoleLogs;
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
             // Placeholder for DOM tree refresh
        }

        private void DomTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             // Placeholder for selection change
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


}
