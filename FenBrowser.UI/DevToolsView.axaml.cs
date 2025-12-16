using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace FenBrowser.UI
{
    public partial class DevToolsView : UserControl
    {
        // Panel references
        private Grid _elementsPanel;
        private Grid _consolePanel;
        private Grid _sourcesPanel;
        private Grid _networkPanel;
        private Grid _performancePanel;
        private Grid _memoryPanel;
        private Grid _applicationPanel;
        
        // Active tab tracking
        private Button _activeTab;
        
        // Attached browser
        private IBrowser _browser;
        private LiteElement _rootElement;
        
        // Console - use existing ConsoleLogItem class with constructor
        private readonly ObservableCollection<ConsoleLogItem> _consoleLogs = new();
        
        // Network
        private readonly ObservableCollection<NetworkRequestItem> _networkItems = new();
        
        // Memory
        private readonly ObservableCollection<MemorySnapshotItem> _snapshots = new();
        
        // Storage
        private readonly ObservableCollection<StorageItem> _storageItems = new();
        
        // Console History Navigation
        private int _historyIndex = -1;
        
        // Event for close request
        public event EventHandler CloseRequested;

        public DevToolsView()
        {
            InitializeComponent();
            
            // Get panel references
            _elementsPanel = this.FindControl<Grid>("ElementsPanel");
            _consolePanel = this.FindControl<Grid>("ConsolePanel");
            _sourcesPanel = this.FindControl<Grid>("SourcesPanel");
            _networkPanel = this.FindControl<Grid>("NetworkPanel");
            _performancePanel = this.FindControl<Grid>("PerformancePanel");
            _memoryPanel = this.FindControl<Grid>("MemoryPanel");
            _applicationPanel = this.FindControl<Grid>("ApplicationPanel");
            
            _activeTab = this.FindControl<Button>("TabElements");
            
            // Setup console
            var consoleOutput = this.FindControl<ListBox>("ConsoleOutput");
            if (consoleOutput != null) consoleOutput.ItemsSource = _consoleLogs;
            
            // Setup network - using ListBox instead of DataGrid (no separate package needed)
            var networkList = this.FindControl<ListBox>("NetworkList");
            if (networkList != null) networkList.ItemsSource = _networkItems;
            
            // Setup memory snapshots
            var snapshotsList = this.FindControl<ListBox>("SnapshotsList");
            if (snapshotsList != null) snapshotsList.ItemsSource = _snapshots;
            
            // Setup storage - using ListBox instead of DataGrid
            var storageList = this.FindControl<ListBox>("StorageList");
            if (storageList != null) storageList.ItemsSource = _storageItems;
            
            // Wire up clear buttons
            var btnClear = this.FindControl<Button>("BtnClear");
            if (btnClear != null) btnClear.Click += (s, e) => _consoleLogs.Clear();
            
            var btnClearNetwork = this.FindControl<Button>("BtnClearNetwork");
            if (btnClearNetwork != null) btnClearNetwork.Click += (s, e) => { _networkItems.Clear(); UpdateNetworkSummary(); };
            
            var btnClearStorage = this.FindControl<Button>("BtnClearStorage");
            if (btnClearStorage != null) btnClearStorage.Click += (s, e) => ClearCurrentStorage();
            
            // Wire up performance buttons
            var btnStartProfiling = this.FindControl<Button>("BtnStartProfiling");
            var btnStopProfiling = this.FindControl<Button>("BtnStopProfiling");
            if (btnStartProfiling != null) btnStartProfiling.Click += OnStartProfiling;
            if (btnStopProfiling != null) btnStopProfiling.Click += OnStopProfiling;
            
            // Wire up memory buttons
            var btnTakeSnapshot = this.FindControl<Button>("BtnTakeSnapshot");
            var btnForceGC = this.FindControl<Button>("BtnForceGC");
            if (btnTakeSnapshot != null) btnTakeSnapshot.Click += OnTakeSnapshot;
            if (btnForceGC != null) btnForceGC.Click += OnForceGC;
            
            // Wire up debugger buttons
            var btnResume = this.FindControl<Button>("BtnResume");
            var btnStepOver = this.FindControl<Button>("BtnStepOver");
            var btnStepInto = this.FindControl<Button>("BtnStepInto");
            var btnStepOut = this.FindControl<Button>("BtnStepOut");
            if (btnResume != null) btnResume.Click += (s, e) => DevToolsCore.Instance.Resume();
            if (btnStepOver != null) btnStepOver.Click += (s, e) => DevToolsCore.Instance.StepOver();
            if (btnStepInto != null) btnStepInto.Click += (s, e) => DevToolsCore.Instance.StepInto();
            if (btnStepOut != null) btnStepOut.Click += (s, e) => DevToolsCore.Instance.StepOut();
            
            // Subscribe to DevTools events
            DevToolsCore.Instance.OnBreakpointHit += OnBreakpointHit;
            DevToolsCore.Instance.OnResumed += OnResumed;
            DevToolsCore.Instance.OnNetworkRequest += OnNetworkRequest;
            DevToolsCore.Instance.OnMemorySnapshot += OnMemorySnapshot;
            
            // Initialize storage tree selection
            var storageTree = this.FindControl<TreeView>("StorageTree");
            if (storageTree != null) storageTree.SelectionChanged += StorageTree_SelectionChanged;

            // Hook up close button
            var btnClose = this.FindControl<Button>("BtnClose");
            if (btnClose != null) btnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            
            // Hook up refresh button
            var btnRefresh = this.FindControl<Button>("BtnRefresh");
            if (btnRefresh != null) btnRefresh.Click += (s, e) => _browser?.RefreshAsync();
        }

        public void OnTabClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            
            // Remove active class from previous tab
            if (_activeTab != null)
                _activeTab.Classes.Remove("active");
            
            // Add active class to new tab
            btn.Classes.Add("active");
            _activeTab = btn;
            
            // Hide all panels
            if (_elementsPanel != null) _elementsPanel.IsVisible = false;
            if (_consolePanel != null) _consolePanel.IsVisible = false;
            if (_sourcesPanel != null) _sourcesPanel.IsVisible = false;
            if (_networkPanel != null) _networkPanel.IsVisible = false;
            if (_performancePanel != null) _performancePanel.IsVisible = false;
            if (_memoryPanel != null) _memoryPanel.IsVisible = false;
            if (_applicationPanel != null) _applicationPanel.IsVisible = false;
            
            // Show selected panel
            switch (btn.Name)
            {
                case "TabElements":
                    if (_elementsPanel != null) _elementsPanel.IsVisible = true;
                    RefreshDom();
                    break;
                case "TabConsole":
                    if (_consolePanel != null) _consolePanel.IsVisible = true;
                    break;
                case "TabSources":
                    if (_sourcesPanel != null) _sourcesPanel.IsVisible = true;
                    RefreshSources();
                    break;
                case "TabNetwork":
                    if (_networkPanel != null) _networkPanel.IsVisible = true;
                    break;
                case "TabPerformance":
                    if (_performancePanel != null) _performancePanel.IsVisible = true;
                    break;
                case "TabMemory":
                    if (_memoryPanel != null) _memoryPanel.IsVisible = true;
                    UpdateMemoryDisplay();
                    break;
                case "TabApplication":
                    if (_applicationPanel != null) _applicationPanel.IsVisible = true;
                    break;
            }
        }

        /// <summary>
        /// Attach DevTools to an IBrowser instance
        /// </summary>
        public void Attach(IBrowser browser)
        {
            _browser = browser;
            _rootElement = browser?.GetDomRoot();
            
            if (_browser != null)
            {
                _browser.ConsoleMessage += msg => AddConsoleMessage("info", msg);
                _browser.RepaintReady += (s, e) =>
                {
                     // Update root if changed
                     _rootElement = _browser.GetDomRoot();
                };
            }

            if (_browser is BrowserHost host)
            {
                // Wire up Global Context for Console (so it works when running)
                Action updateGlobalContext = () =>
                {
                     if (host.Engine?.JsEngine?.GlobalContext != null)
                     {
                         DevToolsCore.Instance.SetGlobalContext(host.Engine.JsEngine.GlobalContext);
                     }
                };
                
                // Update on navigation
                host.Navigated += (s, e) => updateGlobalContext();
                host.LoadingChanged += (s, loading) => { if (!loading) updateGlobalContext(); };
                
                // Initial try
                updateGlobalContext();
                
                // Wire up Style Inspector Rule Matcher (Real Cascade)
                DevToolsCore.Instance.RuleMatcher = (el) =>
                {
                    if (host.Engine?.LastCssSources != null)
                    {
                        return CssLoader.GetMatchedRules(el, host.Engine.LastCssSources); 
                    }
                    return new List<CssLoader.MatchedRule>();
                };

                // Wire up Computed Style Provider
                DevToolsCore.Instance.ComputedStyleProvider = (el) =>
                {
                    if (host.Engine?.LastComputedStyles != null)
                    {
                        return host.Engine.LastComputedStyles.TryGetValue(el, out var computed) ? computed : null;
                    }
                    return null;
                };
            }
            
            RefreshDom();
        }

        /// <summary>
        /// Detach from current browser
        /// </summary>
        public void Detach()
        {
            _browser = null;
            _rootElement = null;
        }

        #region Console Tab

        /// <summary>
        /// Add a console message (called from browser engine)
        /// </summary>
        public void AddConsoleMessage(string level, string message)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                ConsoleLogLevel logLevel;
                switch (level?.ToLowerInvariant())
                {
                    case "error": logLevel = ConsoleLogLevel.Error; break;
                    case "warn":
                    case "warning": logLevel = ConsoleLogLevel.Warning; break;
                    default: logLevel = ConsoleLogLevel.Info; break;
                }
                
                var item = new ConsoleLogItem(message, logLevel);
                _consoleLogs.Add(item);
                
                // Auto-scroll
                var consoleOutput = this.FindControl<ListBox>("ConsoleOutput");
                if (consoleOutput != null && _consoleLogs.Count > 0)
                {
                    consoleOutput.ScrollIntoView(_consoleLogs[^1]);
                }
            });
        }

        private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;
            
            var history = DevToolsCore.Instance.ConsoleHistory;
            
            // Up arrow - previous history
            if (e.Key == Key.Up)
            {
                if (history.Count > 0)
                {
                    if (_historyIndex < 0) _historyIndex = history.Count;
                    _historyIndex = Math.Max(0, _historyIndex - 1);
                    textBox.Text = history[_historyIndex];
                    textBox.CaretIndex = textBox.Text.Length;
                }
                e.Handled = true;
                return;
            }
            
            // Down arrow - next history
            if (e.Key == Key.Down)
            {
                if (history.Count > 0 && _historyIndex >= 0)
                {
                    _historyIndex = Math.Min(history.Count - 1, _historyIndex + 1);
                    textBox.Text = history[_historyIndex];
                    textBox.CaretIndex = textBox.Text.Length;
                }
                e.Handled = true;
                return;
            }
            
            // Tab - autocomplete
            if (e.Key == Key.Tab)
            {
                var prefix = textBox.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(prefix))
                {
                    var completions = DevToolsCore.Instance.GetCompletions(prefix);
                    if (completions.Count == 1)
                    {
                        textBox.Text = completions[0];
                        textBox.CaretIndex = textBox.Text.Length;
                    }
                    else if (completions.Count > 1)
                    {
                        // Show suggestions in console
                        _consoleLogs.Add(new ConsoleLogItem($"Suggestions: {string.Join(", ", completions)}", ConsoleLogLevel.Info));
                    }
                }
                e.Handled = true;
                return;
            }
            
            // Enter - execute
            if (e.Key == Key.Enter)
            {
                if (string.IsNullOrWhiteSpace(textBox.Text)) return;
            
                var cmd = textBox.Text.Trim();
                textBox.Text = "";
                _historyIndex = -1; // Reset history index
            
                // Add to history
                DevToolsCore.Instance.AddToHistory(cmd);
            
                // Add input to console
                _consoleLogs.Add(new ConsoleLogItem(cmd, ConsoleLogLevel.Input));
            
                // Execute JavaScript
                try
                {
                    var result = DevToolsCore.Instance.EvaluateExpression(cmd);
                    var output = result?.ToString() ?? "undefined";
                    _consoleLogs.Add(new ConsoleLogItem(output, ConsoleLogLevel.Result));
                }
                catch (Exception ex)
                {
                    _consoleLogs.Add(new ConsoleLogItem($"Error: {ex.Message}", ConsoleLogLevel.Error));
                }
                
                // Auto-scroll
                var consoleOutput = this.FindControl<ListBox>("ConsoleOutput");
                if (consoleOutput != null && _consoleLogs.Count > 0)
                {
                    consoleOutput.ScrollIntoView(_consoleLogs[^1]);
                }
            }
        }

        #endregion

        #region Elements Tab

        private void RefreshDom()
        {
            var tree = this.FindControl<TreeView>("DomTree");
            if (tree == null || _rootElement == null) return;
            
            var rootModel = DomElementModel.FromLiteElement(_rootElement);
            tree.ItemsSource = rootModel != null ? new[] { rootModel } : null;
        }

        private void DomTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tree = sender as TreeView;
            if (tree?.SelectedItem is DomElementModel model)
            {
                UpdateStylesPanel(model);
            }
        }

        private void UpdateStylesPanel(DomElementModel model)
        {
            var stylesPanel = this.FindControl<StackPanel>("StylesPanel");
            if (stylesPanel == null) return;
            
            stylesPanel.Children.Clear();
            
            // Element info
            stylesPanel.Children.Add(new TextBlock
            {
                Text = $"<{model.Tag}>",
                Foreground = new SolidColorBrush(Color.Parse("#569CD6")),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            });
            
            // Computed styles
            if (model.Element != null)
            {
                var computed = DevToolsCore.Instance.GetComputedStyles(model.Element);
                
                stylesPanel.Children.Add(new TextBlock
                {
                    Text = "Computed Styles",
                    Foreground = new SolidColorBrush(Color.Parse("#9D9D9D")),
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 4)
                });
                
                if (computed != null)
                {
                    foreach (var kv in computed.Map)
                    {
                    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{kv.Key}: ",
                        Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = kv.Value,
                        Foreground = new SolidColorBrush(Color.Parse("#CE9178")),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    });
                    stylesPanel.Children.Add(row);
                }
            }
        }
    }

        #endregion

        #region Sources Tab

        private void RefreshSources()
        {
            var tree = this.FindControl<TreeView>("SourceTree");
            if (tree == null) return;
            
            var sources = DevToolsCore.Instance.GetSources().ToList();
            var items = new List<TreeViewItem>();
            
            foreach (var source in sources)
            {
                var item = new TreeViewItem
                {
                    Header = System.IO.Path.GetFileName(source.Url) ?? source.Url,
                    Tag = source.Url
                };
                item.DoubleTapped += (s, e) => ShowSourceFile(source.Url);
                items.Add(item);
            }
            
            tree.ItemsSource = items;
        }

        private void ShowSourceFile(string url)
        {
            var codeView = this.FindControl<StackPanel>("CodeView");
            if (codeView == null) return;
            
            var content = DevToolsCore.Instance.GetSourceContent(url);
            if (content == null) return;
            
            codeView.Children.Clear();
            var lines = content.Split('\n');
            var breakpoints = DevToolsCore.Instance.GetBreakpointsForFile(url).ToList();
            
            // Regex for syntax highlighting
            var keywords = @"\b(break|case|catch|class|const|continue|debugger|default|delete|do|else|export|extends|finally|for|function|if|import|in|instanceof|let|new|return|super|switch|this|throw|try|typeof|var|void|while|with|yield|async|await|null|true|false)\b";
            var pattern = $@"(?<Comment>//.*)|(?<String>""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|(?<Keyword>{keywords})";
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled);

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNum = i + 1;
                var hasBreakpoint = breakpoints.Any(bp => bp.LineNumber == lineNum);
                var isCurrentLine = DevToolsCore.Instance.IsPaused && 
                                   DevToolsCore.Instance.CurrentFile == url && 
                                   DevToolsCore.Instance.CurrentLine == lineNum;
                
                var row = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("16,40,*"),
                    Background = isCurrentLine ? new SolidColorBrush(Color.Parse("#3C3C00")) : Brushes.Transparent
                };
                
                // Breakpoint indicator
                if (hasBreakpoint)
                {
                    row.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new SolidColorBrush(Color.Parse("#F14C4C")),
                        Margin = new Thickness(2),
                        [Grid.ColumnProperty] = 0
                    });
                }
                
                // Line number
                row.Children.Add(new TextBlock
                {
                    Text = lineNum.ToString(),
                    Foreground = new SolidColorBrush(Color.Parse("#6A6A6A")),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 8, 0),
                    [Grid.ColumnProperty] = 1
                });
                
                // Code with Syntax Highlighting
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    [Grid.ColumnProperty] = 2
                };
                
                // Default color
                // tb.Foreground = new SolidColorBrush(Color.Parse("#D4D4D4"));
                
                var lineText = lines[i];
                int lastIndex = 0;
                
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(lineText))
                {
                    // Unmatched text before match
                    if (m.Index > lastIndex)
                    {
                        tb.Inlines.Add(new Avalonia.Controls.Documents.Run 
                        { 
                            Text = lineText.Substring(lastIndex, m.Index - lastIndex),
                            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4"))
                        });
                    }
                    
                    if (m.Groups["Comment"].Success)
                    {
                        tb.Inlines.Add(new Avalonia.Controls.Documents.Run 
                        { 
                            Text = m.Value, 
                            Foreground = new SolidColorBrush(Color.Parse("#6A9955")) 
                        });
                    }
                    else if (m.Groups["String"].Success)
                    {
                        tb.Inlines.Add(new Avalonia.Controls.Documents.Run 
                        { 
                            Text = m.Value, 
                            Foreground = new SolidColorBrush(Color.Parse("#CE9178")) 
                        });
                    }
                    else if (m.Groups["Keyword"].Success)
                    {
                        tb.Inlines.Add(new Avalonia.Controls.Documents.Run 
                        { 
                            Text = m.Value, 
                            Foreground = new SolidColorBrush(Color.Parse("#569CD6")) 
                        });
                    }
                    
                    lastIndex = m.Index + m.Length;
                }
                
                // Remaining text
                if (lastIndex < lineText.Length)
                {
                    tb.Inlines.Add(new Avalonia.Controls.Documents.Run 
                    { 
                        Text = lineText.Substring(lastIndex),
                        Foreground = new SolidColorBrush(Color.Parse("#D4D4D4"))
                    });
                }
                
                row.Children.Add(tb);
                
                // Click to toggle breakpoint
                int capturedLine = lineNum;
                row.PointerPressed += (s, e) =>
                {
                    if (breakpoints.Any(bp => bp.LineNumber == capturedLine))
                    {
                        var bp = breakpoints.First(b => b.LineNumber == capturedLine);
                        DevToolsCore.Instance.RemoveBreakpoint(bp.Id);
                    }
                    else
                    {
                        DevToolsCore.Instance.SetBreakpoint(url, capturedLine);
                    }
                    ShowSourceFile(url); // Refresh
                };
                
                codeView.Children.Add(row);
            }
        }

        private void OnBreakpointHit(string url, int line, string reason)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var debugStatus = this.FindControl<TextBlock>("DebugStatus");
                if (debugStatus != null)
                    debugStatus.Text = $"Paused at {System.IO.Path.GetFileName(url)}:{line} ({reason})";
                
                ShowSourceFile(url);
                UpdateCallStack();
            });
        }

        private void OnResumed()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var debugStatus = this.FindControl<TextBlock>("DebugStatus");
                if (debugStatus != null) debugStatus.Text = "Running";
            });
        }

        private void UpdateCallStack()
        {
            var callStackList = this.FindControl<ListBox>("CallStackList");
            var scopeList = this.FindControl<ListBox>("ScopeList");
            if (callStackList == null || scopeList == null) return;
            
            var frames = DevToolsCore.Instance.GetCallStack();
            callStackList.ItemsSource = frames.Select(f => $"{f.FunctionName} ({System.IO.Path.GetFileName(f.Url)}:{f.LineNumber})").ToList();
            
            var locals = DevToolsCore.Instance.GetLocalVariables();
            scopeList.ItemsSource = locals.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        }

        #endregion

        #region Network Tab

        private void OnNetworkRequest(NetworkRequest request)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var existing = _networkItems.FirstOrDefault(n => n.Id == request.Id);
                if (existing != null)
                {
                    existing.Update(request);
                }
                else
                {
                    _networkItems.Add(new NetworkRequestItem(request));
                }
                UpdateNetworkSummary();
            });
        }

        private void UpdateNetworkSummary()
        {
            var summary = this.FindControl<TextBlock>("NetworkSummary");
            if (summary == null) return;
            
            var totalSize = _networkItems.Sum(n => n.Size);
            summary.Text = $"{_networkItems.Count} requests | {FormatBytes(totalSize)} transferred";
        }

        #endregion

        #region Performance Tab

        private void OnStartProfiling(object sender, RoutedEventArgs e)
        {
            DevToolsCore.Instance.StartProfiling();
            
            var btnStart = this.FindControl<Button>("BtnStartProfiling");
            var btnStop = this.FindControl<Button>("BtnStopProfiling");
            var status = this.FindControl<TextBlock>("ProfilingStatus");
            
            if (btnStart != null) btnStart.IsEnabled = false;
            if (btnStop != null) btnStop.IsEnabled = true;
            if (status != null) status.Text = "Recording...";
        }

        private void OnStopProfiling(object sender, RoutedEventArgs e)
        {
            var report = DevToolsCore.Instance.StopProfiling();
            
            var btnStart = this.FindControl<Button>("BtnStartProfiling");
            var btnStop = this.FindControl<Button>("BtnStopProfiling");
            var status = this.FindControl<TextBlock>("ProfilingStatus");
            
            if (btnStart != null) btnStart.IsEnabled = true;
            if (btnStop != null) btnStop.IsEnabled = false;
            if (status != null) status.Text = $"Recorded {report.Entries.Count} events";
            
            // Update metrics
            var fps = this.FindControl<TextBlock>("MetricFPS");
            var script = this.FindControl<TextBlock>("MetricScriptTime");
            var layout = this.FindControl<TextBlock>("MetricLayoutTime");
            var paint = this.FindControl<TextBlock>("MetricPaintTime");
            
            if (fps != null) fps.Text = $"{report.AverageFPS:F1}";
            
            var scriptTime = report.FrameTimings.Sum(f => f.ScriptTime);
            var layoutTime = report.FrameTimings.Sum(f => f.LayoutTime);
            var paintTime = report.FrameTimings.Sum(f => f.PaintTime);
            
            if (script != null) script.Text = $"{scriptTime:F0} ms";
            if (layout != null) layout.Text = $"{layoutTime:F0} ms";
            if (paint != null) paint.Text = $"{paintTime:F0} ms";
        }

        #endregion

        #region Memory Tab

        private void OnTakeSnapshot(object sender, RoutedEventArgs e)
        {
            var snapshot = DevToolsCore.Instance.TakeHeapSnapshot();
            UpdateMemoryDisplay();
        }

        private void OnForceGC(object sender, RoutedEventArgs e)
        {
            DevToolsCore.Instance.ForceGC();
            UpdateMemoryDisplay();
        }

        private void OnMemorySnapshot(MemorySnapshot snapshot)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _snapshots.Add(new MemorySnapshotItem
                {
                    Id = snapshot.Id,
                    Timestamp = snapshot.Timestamp.ToString("HH:mm:ss"),
                    Size = $"{snapshot.TotalMemoryMB:F1} MB"
                });
            });
        }

        private void UpdateMemoryDisplay()
        {
            var process = Process.GetCurrentProcess();
            
            var total = this.FindControl<TextBlock>("MemoryTotal");
            var gc = this.FindControl<TextBlock>("MemoryGC");
            var gen0 = this.FindControl<TextBlock>("MemoryGen0");
            var gen2 = this.FindControl<TextBlock>("MemoryGen2");
            
            if (total != null) total.Text = $"{process.WorkingSet64 / (1024.0 * 1024.0):F1} MB";
            if (gc != null) gc.Text = $"{GC.GetTotalMemory(false) / (1024.0 * 1024.0):F1} MB";
            if (gen0 != null) gen0.Text = GC.CollectionCount(0).ToString();
            if (gen2 != null) gen2.Text = GC.CollectionCount(2).ToString();
        }

        #endregion

        #region Application Tab

        private void StorageTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tree = sender as TreeView;
            if (tree?.SelectedItem is TreeViewItem item)
            {
                var header = item.Header?.ToString() ?? "";
                RefreshStorageView(header);
            }
        }

        private void RefreshStorageView(string storageType)
        {
            _storageItems.Clear();
            
            switch (storageType)
            {
                case "Local Storage":
                    foreach (var kv in DevToolsCore.Instance.GetAllLocalStorage())
                        _storageItems.Add(new StorageItem { Key = kv.Key, Value = kv.Value });
                    break;
                case "Session Storage":
                    foreach (var kv in DevToolsCore.Instance.GetAllSessionStorage())
                        _storageItems.Add(new StorageItem { Key = kv.Key, Value = kv.Value });
                    break;
                case "Cookies":
                    foreach (var cookie in DevToolsCore.Instance.GetAllCookies())
                        _storageItems.Add(new StorageItem { Key = cookie.Name, Value = cookie.Value });
                    break;
            }
            
            var summary = this.FindControl<TextBlock>("StorageSummary");
            if (summary != null) summary.Text = $"{_storageItems.Count} items";
        }

        private void ClearCurrentStorage()
        {
            var storageTree = this.FindControl<TreeView>("StorageTree");
            if (storageTree?.SelectedItem is TreeViewItem item)
            {
                var header = item.Header?.ToString() ?? "";
                switch (header)
                {
                    case "Local Storage":
                        DevToolsCore.Instance.ClearLocalStorage();
                        break;
                    case "Session Storage":
                        DevToolsCore.Instance.ClearSessionStorage();
                        break;
                    case "Cookies":
                        DevToolsCore.Instance.ClearCookies();
                        break;
                }
                RefreshStorageView(header);
            }
        }

        #endregion

        #region Helpers

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        #endregion
    }
    
    /// <summary>
    /// ViewModel for DOM tree items
    /// </summary>
    public class DomElementModel
    {
        public string Tag { get; set; }
        public string AttrString { get; set; }
        public string TextPreview { get; set; }
        public bool HasChildren { get; set; }
        public List<DomElementModel> Children { get; set; } = new();
        public LiteElement Element { get; set; }

        public static DomElementModel FromLiteElement(LiteElement el)
        {
            if (el == null) return null;
            if (el.IsText) return null;
            
            var model = new DomElementModel
            {
                Tag = el.Tag ?? "?",
                AttrString = FormatAttributes(el),
                TextPreview = GetTextPreview(el),
                HasChildren = el.Children?.Any(c => !c.IsText) == true,
                Element = el
            };
            
            if (el.Children != null)
            {
                foreach (var child in el.Children.Where(c => !c.IsText))
                {
                    var childModel = FromLiteElement(child);
                    if (childModel != null) model.Children.Add(childModel);
                }
            }
            
            return model;
        }

        private static string FormatAttributes(LiteElement el)
        {
            if (el.Attr == null || el.Attr.Count == 0) return "";
            
            var parts = new List<string>();
            if (el.Attr.TryGetValue("id", out var id))
                parts.Add($"id=\"{TruncateString(id, 20)}\"");
            if (el.Attr.TryGetValue("class", out var cls))
                parts.Add($"class=\"{TruncateString(cls, 30)}\"");
            
            return string.Join(" ", parts);
        }

        private static string GetTextPreview(LiteElement el)
        {
            var textChild = el.Children?.FirstOrDefault(c => c.IsText && !string.IsNullOrWhiteSpace(c.Text));
            if (textChild != null)
                return TruncateString(textChild.Text.Trim(), 50);
            return "";
        }

        private static string TruncateString(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    // ===== Storage Editing Event Handlers in DevToolsView =====
    public partial class DevToolsView
    {
        private string _currentStorageType = "localStorage";
        
        private void AddStorageItem(object sender, RoutedEventArgs e)
        {
            var newItem = new StorageItem { Key = "newKey", Value = "newValue" };
            _storageItems.Add(newItem);
            UpdateStorageSummary();
            
            // Persist to actual storage via browser
            SaveStorageItem(newItem.Key, newItem.Value);
        }
        
        private void DeleteStorageItem(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string key)
            {
                var item = _storageItems.FirstOrDefault(i => i.Key == key);
                if (item != null)
                {
                    _storageItems.Remove(item);
                    RemoveStorageItem(key);
                    UpdateStorageSummary();
                }
            }
        }
        
        private void ClearStorage(object sender, RoutedEventArgs e)
        {
            _storageItems.Clear();
            ClearAllStorageItems();
            UpdateStorageSummary();
        }
        
        private void StorageKeyChanged(object sender, RoutedEventArgs e)
        {
            // Key changes need to delete old and add new
            RefreshStorageList();
        }
        
        private void StorageValueChanged(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is StorageItem item)
            {
                SaveStorageItem(item.Key, item.Value);
            }
        }
        
        private void UpdateStorageSummary()
        {
            var summary = this.FindControl<TextBlock>("StorageSummary");
            if (summary != null)
            {
                summary.Text = $"{_storageItems.Count} items";
            }
        }
        
        private void SaveStorageItem(string key, string value)
        {
            // Save via DevTools bridge - uses browser's localStorage/sessionStorage
            if (_browser is BrowserHost host)
            {
                try
                {
                    if (_currentStorageType == "localStorage")
                        host.Engine?.JsEngine?.RunInline($"localStorage.setItem('{key}', '{value}');");
                    else
                        host.Engine?.JsEngine?.RunInline($"sessionStorage.setItem('{key}', '{value}');");
                }
                catch { /* ignore storage errors */ }
            }
        }
        
        private void RemoveStorageItem(string key)
        {
            if (_browser is BrowserHost host)
            {
                try
                {
                    if (_currentStorageType == "localStorage")
                        host.Engine?.JsEngine?.RunInline($"localStorage.removeItem('{key}');");
                    else
                        host.Engine?.JsEngine?.RunInline($"sessionStorage.removeItem('{key}');");
                }
                catch { /* ignore storage errors */ }
            }
        }
        
        private void ClearAllStorageItems()
        {
            if (_browser is BrowserHost host)
            {
                try
                {
                    if (_currentStorageType == "localStorage")
                        host.Engine?.JsEngine?.RunInline("localStorage.clear();");
                    else
                        host.Engine?.JsEngine?.RunInline("sessionStorage.clear();");
                }
                catch { /* ignore storage errors */ }
            }
        }
        
        private void RefreshStorageList()
        {
            // Could implement full refresh from browser storage
            UpdateStorageSummary();
        }
    }

    /// <summary>
    /// Network request item for DataGrid
    /// </summary>
    public class NetworkRequestItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string MimeType { get; set; }
        public long Size { get; set; }
        public double Duration { get; set; }
        public string SizeFormatted => Size < 1024 ? $"{Size} B" : Size < 1024 * 1024 ? $"{Size / 1024.0:F1} KB" : $"{Size / (1024.0 * 1024.0):F1} MB";
        public string DurationFormatted => Duration < 1000 ? $"{Duration:F0} ms" : $"{Duration / 1000.0:F1} s";
        public double WaterfallProgress { get; set; } = 100;

        public NetworkRequestItem(NetworkRequest request)
        {
            Update(request);
        }

        public void Update(NetworkRequest request)
        {
            Id = request.Id;
            try
            {
                Name = System.IO.Path.GetFileName(new Uri(request.Url).LocalPath) ?? request.Url;
            }
            catch
            {
                Name = request.Url;
            }
            Status = request.StatusCode > 0 ? request.StatusCode.ToString() : "pending";
            MimeType = request.MimeType ?? "";
            Size = request.Size;
            Duration = request.Duration;
        }
    }

    /// <summary>
    /// Memory snapshot item
    /// </summary>
    public class MemorySnapshotItem
    {
        public string Id { get; set; }
        public string Timestamp { get; set; }
        public string Size { get; set; }
    }

    /// <summary>
    /// Storage item for DataGrid
    /// </summary>
    public class StorageItem
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }
}
