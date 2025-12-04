using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;

namespace FenBrowser.UI
{
    public partial class DevToolsView : UserControl
    {
        private IBrowser _browser;
        private readonly ObservableCollection<string> _consoleLogs = new ObservableCollection<string>();
        
        public event EventHandler CloseRequested;

        public DevToolsView()
        {
            InitializeComponent();
        }

        public void Attach(IBrowser browser)
        {
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
                _browser.ConsoleMessage -= OnConsoleMessage;
                _browser = null;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            
            var btnRefresh = this.FindControl<Button>("BtnRefresh");
            if (btnRefresh != null) btnRefresh.Click += (s, e) => RefreshDom();

            var btnClose = this.FindControl<Button>("BtnClose");
            if (btnClose != null) btnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);

            var consoleInput = this.FindControl<TextBox>("ConsoleInput");
            if (consoleInput != null) consoleInput.KeyDown += ConsoleInput_KeyDown;

            var consoleOutput = this.FindControl<ListBox>("ConsoleOutput");
            if (consoleOutput != null) consoleOutput.ItemsSource = _consoleLogs;

            var domTree = this.FindControl<TreeView>("DomTree");
            if (domTree != null)
            {
                domTree.SelectionChanged += DomTree_SelectionChanged;
            }
        }

        private void DomTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_browser == null) return;
            var tree = sender as TreeView;
            if (tree?.SelectedItem is DomNodeViewModel vm && vm.Element != null)
            {
                _browser.HighlightElement(vm.Element);
            }
            else
            {
                _browser.RemoveHighlight();
            }
        }

        private void OnConsoleMessage(string msg)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _consoleLogs.Add(msg);
                var list = this.FindControl<ListBox>("ConsoleOutput");
                if (list != null && _consoleLogs.Count > 0)
                {
                    list.ScrollIntoView(_consoleLogs.Count - 1);
                }
            });
        }

        private void RefreshDom()
        {
            if (_browser == null) return;
            try
            {
                var root = _browser.GetDomRoot();
                var tree = this.FindControl<TreeView>("DomTree");
                if (tree != null)
                {
                    if (root != null)
                    {
                        var vm = new DomNodeViewModel(root);
                        tree.ItemsSource = new[] { vm };
                    }
                    else
                    {
                        tree.ItemsSource = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _consoleLogs.Add($"[Error] Failed to refresh DOM: {ex.Message}");
            }
        }

        private async void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var txt = sender as TextBox;
                if (txt == null) return;
                var script = txt.Text;
                if (string.IsNullOrWhiteSpace(script)) return;

                _consoleLogs.Add($"> {script}");
                txt.Text = "";

                try
                {
                    if (_browser != null)
                    {
                        var result = await _browser.ExecuteScriptAsync(script);
                        _consoleLogs.Add($"< {result}");
                    }
                }
                catch (Exception ex)
                {
                    _consoleLogs.Add($"[Error] {ex.Message}");
                }
            }
        }
    }
}
