using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;

namespace FenBrowser.UI
{
    public partial class DevToolsWindow : Window
    {
        private readonly IBrowser _browser;
        private readonly ObservableCollection<string> _consoleLogs = new ObservableCollection<string>();

        public DevToolsWindow()
        {
            InitializeComponent();
        }

        public DevToolsWindow(IBrowser browser) : this()
        {
            _browser = browser;
            
            var btnRefresh = this.FindControl<Button>("BtnRefresh");
            btnRefresh.Click += (s, e) => RefreshDom();

            var consoleInput = this.FindControl<TextBox>("ConsoleInput");
            consoleInput.KeyDown += ConsoleInput_KeyDown;

            var consoleOutput = this.FindControl<ListBox>("ConsoleOutput");
            consoleOutput.ItemsSource = _consoleLogs;

            if (_browser != null)
            {
                _browser.ConsoleMessage += OnConsoleMessage;
                RefreshDom();
            }

            this.Closed += (s, e) =>
            {
                if (_browser != null)
                {
                    _browser.ConsoleMessage -= OnConsoleMessage;
                }
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
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
                    var result = await _browser.ExecuteScriptAsync(script);
                    _consoleLogs.Add($"< {result}");
                }
                catch (Exception ex)
                {
                    _consoleLogs.Add($"[Error] {ex.Message}");
                }
            }
        }
    }

    public class DomNodeViewModel
    {
        public Element Element { get; }
        public string Tag => Element.Tag;
        public string TextPreview { get; }
        public string AttrString { get; }
        public IEnumerable<DomNodeViewModel> Children { get; }
        public bool HasChildren => Children != null && Children.Any();

        public DomNodeViewModel(Element element)
        {
            Element = element;
            TextPreview = element.IsText ? element.Text : "";
            
            if (element.Attr != null && element.Attr.Count > 0)
            {
                AttrString = string.Join(" ", element.Attr.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
            }
            else
            {
                AttrString = "";
            }

            if (element.Children != null)
            {
                Children = element.Children.OfType<Element>().Select(c => new DomNodeViewModel(c)).ToList();
            }
            else
            {
                Children = Enumerable.Empty<DomNodeViewModel>();
            }
        }
    }
}
