using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
// using Avalonia;
// using Avalonia.Controls;
// using Avalonia.Controls.Documents;
// using Avalonia.Media;
// using Avalonia.Media.Imaging;
// using Avalonia.Layout;
// using Avalonia.Controls.ApplicationLifetimes;
// using Control = Avalonia.Controls.Control;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.IO;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Security;
using FenBrowser.FenEngine.Security; // Added
using FenBrowser.FenEngine.Scripting; // For IJsHost
using FenBrowser.FenEngine.Core; // Corrected namespace
using FenBrowser.FenEngine.Layout; // Added for LayoutResult
using static FenBrowser.FenEngine.Rendering.CssLoader;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;


namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Clean, dependency-free wrapper suitable for WP8.1 without WebView.
    /// </summary>
    public sealed class CustomHtmlEngine : IDisposable
    {

        public Func<Uri, Task<string>> ScriptFetcher { get; set; }

        /// <summary>Active Content Security Policy for this page. When set, subresource loads are checked against it.</summary>
        public CspPolicy ActivePolicy { get; set; }

        // EXPOSED STYLES FOR SKIA RENDERER
        public Dictionary<Node, CssComputed> LastComputedStyles { get; private set; }
        public List<CssLoader.CssSource> LastCssSources { get; private set; }

        public LayoutResult LastLayout => _cachedRenderer?.LastLayout;
        public IExecutionContext Context => _activeJs?.GlobalContext;

        public event Action<object> RepaintReady;
        private void OnRepaintReady(object control)
        {
            _lastRenderedControl = control;
            RepaintReady?.Invoke(control);
        }
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event EventHandler<Element> DomReady;
        public event Action<string> AlertTriggered;
        public event Action<string> ConsoleMessage; // New event for console logs
        public event Action<SKRect?> HighlightRectChanged;
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested; // Permission API event
        public bool EnableJavaScript { get; set; } = true;

        public void HighlightElement(Element element)
        {
            if (element == null)
            {
                RemoveHighlight();
                return;
            }

            if (JavaScriptEngine.TryGetVisualRect(element, out double x, out double y, out double w, out double h))
            {
                HighlightRectChanged?.Invoke(new SKRect((float)x, (float)y, (float)(x+w), (float)(y+h)));
            }
            else
            {
                RemoveHighlight();
            }
        }

        public void RemoveHighlight()
        {
            HighlightRectChanged?.Invoke(null);
        }

        public void ClearAllCookies()
        {
            _jsCookieJar = new CookieContainer();
        }

        public object Evaluate(string script)
        {
            FenLogger.Debug($"[CustomHtmlEngine.Evaluate] _activeJs is null: {_activeJs == null}", LogCategory.JavaScript);
            if (_activeJs != null) 
            {
                FenLogger.Debug($"[CustomHtmlEngine.Evaluate] Calling _activeJs.Evaluate with script length: {script?.Length}", LogCategory.JavaScript);
                return _activeJs.Evaluate(script);
            }
            FenLogger.Error("[CustomHtmlEngine.Evaluate] _activeJs is NULL - script not executed!", LogCategory.JavaScript);
            return null;
        }

        public Element ActiveDom => _activeDom;
        public JavaScriptEngine JsEngine => _activeJs;
        private Element _activeDom;
        private string _lastRawHtml;
        private object _lastRenderedControl;
        private Uri _activeBaseUri;
        private Func<Uri, Task<string>> _activeFetchCss;
        private Func<Uri, Task<Stream>> _activeImageLoader;
        private Action<Uri> _activeOnNavigate;
        private double? _activeViewportWidth;
        private Action<object> _activeFixedBackground;
        private JavaScriptEngine _activeJs;
        private CookieContainer _jsCookieJar = new CookieContainer();
        private readonly System.Threading.SemaphoreSlim _repaintGate = new System.Threading.SemaphoreSlim(1, 1);
        private int _repaintScheduled;
        private readonly object _uiDispatcher;
        
        // Cache view/renderer to avoid full recreation
        // private SkiaBrowserView _cachedView;
        private SkiaDomRenderer _cachedRenderer;
        
        public CustomHtmlEngine()
        {
            _uiDispatcher = UiThreadHelper.TryGetDispatcher();
        }

        private static double GetPrimaryWindowWidth()
        {
            // [MIGRATION] Window logic removed
            return 800;
        }

        private static double GetPrimaryWindowHeight()
        {
             // [MIGRATION] Window logic removed
            return 600;
        }

        private static readonly System.Collections.Generic.HashSet<string> _loadingTokens =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "loading",
                "loaded",
                "pleasewait",
                "justamoment",
                "holdtight",
                "hangtight",
                "stillworking"
            };

        // Heuristic: detect JS-heavy SPA/app-shell sites where our JS engine
        // cannot realistically reproduce full behavior (e.g., modern google.com),
        // and skip expensive JS execution + double-render for performance.
        private static bool IsJsHeavyAppShell(Uri baseUri)
        {
            try
            {
                if (baseUri == null || string.IsNullOrEmpty(baseUri.Host)) return false;
                var host = baseUri.Host.ToLowerInvariant();

                // Start conservatively: only google.com and www.google.com.
                // if (host == "google.com" || host == "www.google.com") return true;

                return false;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            try
            {
                _repaintGate.Dispose();
                // _activeJs does not implement IDisposable, just clear ref
                _activeJs = null;
                _activeDom = null;
                // _cachedView = null;
                _cachedRenderer = null;
            }
            catch { }
        }

        private async Task RaiseLoadingChangedAsync(bool isLoading)
        {
            var handler = LoadingChanged;
            if (handler == null) return;
            try
            {
                var disp = _uiDispatcher ?? UiThreadHelper.TryGetDispatcher();
                if (disp != null && !UiThreadHelper.HasThreadAccess(disp))
                {
                    await UiThreadHelper.RunAsyncAwaitable(disp, null, () =>
                    {
                        try { handler(this, isLoading); }
                        catch { }
                        return Task.CompletedTask;
                    });
                }
                else
                {
                    handler(this, isLoading);
                }
            }
            catch { }
        }

        // Resolve a possibly relative URL against a base
        private static Uri ResolveUri(Uri baseUri, string href)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(href)) return null;
                href = href.Trim();
                if (href.StartsWith("//"))
                {
                    var scheme = baseUri != null ? baseUri.Scheme : "https";
                    return new Uri(scheme + ":" + href);
                }
                Uri abs;
                if (Uri.TryCreate(href, UriKind.Absolute, out abs)) return abs;
                if (baseUri != null && Uri.TryCreate(baseUri, href, out abs)) return abs;
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------
        // Helper: Image Logic (Consolidated for Performance/DRY)
        // ---------------------------------------------------------

        private static string RewriteWebPToJpg(string u)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(u)) return u;
                
                // Quick check before regex to save CPU on WP8.1
                if (u.IndexOf("webp", StringComparison.OrdinalIgnoreCase) < 0 && 
                    u.IndexOf("avif", StringComparison.OrdinalIgnoreCase) < 0) 
                    return u;

                if (u.IndexOf("format=webp", StringComparison.OrdinalIgnoreCase) >= 0)
                    u = System.Text.RegularExpressions.Regex.Replace(u, @"format=webp", "format=jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(\?|&)(f|fmt)=webp", "$1$2=jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\.(webp|avif)(\?.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    u = System.Text.RegularExpressions.Regex.Replace(u, @"\.(webp|avif)(\?.*)?$", ".jpg$2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { }
            return u;
        }

        private static string PickBestImageFromSrcSet(string srcset, double viewportWidth)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(srcset)) return null;
                
                // Optimization: Avoid LINQ overhead on hot paths if possible, but here LINQ is readable.
                // Split by comma
                var candidates = srcset.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s =>
                    {
                        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var url = parts[0];
                        int width = 0;
                        if (parts.Length > 1)
                        {
                             var d = parts[1].Trim().ToLowerInvariant();
                             if (d.EndsWith("w")) int.TryParse(d.TrimEnd('w'), out width);
                        }
                        return new { url, width };
                    })
                    .OrderBy(s => s.width) // Sort smallest to largest
                    .ToList();

                // Logic: Find first image >= viewport width. If none, use the largest available.
                var bestCandidate = candidates.FirstOrDefault(c => c.width >= viewportWidth) ?? candidates.LastOrDefault();
                
                return RewriteWebPToJpg(bestCandidate?.url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WARN] Srcset parsing failed: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------
        // End Helper
        // ---------------------------------------------------------

        // Kick off background image fetches early (img/srcset/background-image)
        private static void PrewarmImages(Element root, Uri baseUri, Func<Uri, Task<Stream>> imageLoader, double? viewportWidth)
        {
            if (root == null || imageLoader == null) return;
            try
            {
                var tasks = new List<Task>();
                var gate = new System.Threading.SemaphoreSlim(6);
                int budget = 32; // avoid over-queuing
                double dw = viewportWidth ?? 0; 
                try { if (dw <= 0) dw = GetPrimaryWindowWidth(); } catch { }
                if (dw <= 0) dw = 480;

                // Helper to execute load
                Action<string> queueLoad = (rawUrl) => 
                {
                    if (budget <= 0) return;
                    var clean = RewriteWebPToJpg(rawUrl);
                    var abs = ResolveUri(baseUri, clean);
                    if (abs != null)
                    {
                        budget--;
                        tasks.Add(Task.Run(async () => 
                        { 
                            try 
                            { 
                                await gate.WaitAsync(); 
                                try { await imageLoader(abs); } 
                                finally { gate.Release(); } 
                            } 
                            catch { } 
                        }));
                    }
                };

                // Preload/Prefetch links
                foreach (var link in root.Descendants().Where(n => n.Tag == "link" && n.Attr != null))
                {
                    string rel;
                    if (link.Attr.TryGetValue("rel", out rel) && (rel == "preload" || rel == "prefetch"))
                    {
                        string href;
                        if (link.Attr.TryGetValue("href", out href))
                        {
                            string asAttr;
                            if (link.Attr.TryGetValue("as", out asAttr) && asAttr == "image")
                            {
                                queueLoad(href);
                            }
                        }
                    }
                }

                // Images and Backgrounds
                foreach (var n in root.SelfAndDescendants())
                {
                    if (budget <= 0) break;
                    try
                    {
                        if (n.IsText) continue;
                        if (n.Tag == "img" && n.Attr != null)
                        {
                            string src = null; n.Attr.TryGetValue("src", out src);
                            if (string.IsNullOrWhiteSpace(src))
                            {
                                string v; 
                                if (n.Attr.TryGetValue("data-src", out v)) src = v; 
                                else if (n.Attr.TryGetValue("data-original", out v)) src = v; 
                                else if (n.Attr.TryGetValue("data-lazy", out v)) src = v;
                            }

                            string srcset = null; n.Attr.TryGetValue("srcset", out srcset);
                            string chosen = null;
                            if (!string.IsNullOrWhiteSpace(srcset)) 
                            {
                                chosen = PickBestImageFromSrcSet(srcset, dw);
                            }
                            
                            if (string.IsNullOrWhiteSpace(chosen)) chosen = src;
                            queueLoad(chosen);
                        }
                        else if (n.Attr != null)
                        {
                            string style; 
                            if (n.Attr.TryGetValue("style", out style) && !string.IsNullOrWhiteSpace(style))
                            {
                                // background-image/background shorthand regex
                                var m = System.Text.RegularExpressions.Regex.Match(style, "url\\(['\"']?(?<u>[^)\"']+)['\"']?\\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success)
                                {
                                    queueLoad(m.Groups["u"].Value);
                                }
                            }
                        }
                    }
                    catch { }
                }
                // Fire-and-forget; we do not await prewarm tasks to avoid blocking render
            }
            catch { }
        }

        private static int VisualChildCount(object node)
        {
            try
            {
                if (node == null) return 0;
                var t = node.GetType();
                var prop = t.GetProperty("Children");
                if (prop != null)
                {
                    var value = prop.GetValue(node) as System.Collections.IList;
                    if (value != null) return value.Count;
                }
            }
            catch { }
            return 0;
        }

        private static object VisualGetChild(object node, int index)
        {
            try
            {
                if (node == null) return null;
                var t = node.GetType();
                var prop = t.GetProperty("Children");
                if (prop != null)
                {
                    var value = prop.GetValue(node) as System.Collections.IList;
                    if (value != null && index >= 0 && index < value.Count) return value[index];
                }
            }
            catch { }
            return null;
        }

        private static string GatherPlainText(Element n)
        {
            if (n == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            Action<Element> walk = null;
            walk = (el) =>
            {
                if (el == null) return;
                if (el.IsText) { var t = el.Text ?? string.Empty; sb.Append(t); return; }
                if (el.Children != null)
                {
                    for (int i = 0; i < el.Children.Count; i++)
                    {
                        if (el.Children[i] is Element childEl)
                            walk(childEl);
                    }
                }
            };
            walk(n);
            var s = sb.ToString();
            // collapse whitespace lightly
            bool inWs = false; var outSb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { if (!inWs) { outSb.Append(' '); inWs = true; } }
                else { outSb.Append(c); inWs = false; }
            }
            return outSb.ToString().Trim();
        }

        private async Task CaptureActiveContextAsync(
            Element dom,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            double? viewportWidth,
            Action<object> onFixedBackground,
            JavaScriptEngine js)
        {
            _activeDom = dom;
            _activeBaseUri = baseUri;
            _activeFetchCss = fetchExternalCssAsync;
            _activeImageLoader = imageLoader;
            _activeOnNavigate = onNavigate;
            _activeViewportWidth = viewportWidth;
            _activeFixedBackground = onFixedBackground;
            // Only update _activeJs if a new engine is provided; preserve existing during repaints
            if (js != null) _activeJs = js;
            
            // Keep JS engine's DOM in sync with _activeDom so JS style changes appear on render
            if (_activeJs != null && dom != null)
            {
                try 
                { 
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CaptureActiveContext] Calling SetDomAsync...\r\n"); } catch {}
                    await _activeJs.SetDomAsync(dom, baseUri).ConfigureAwait(false);
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CaptureActiveContext] SetDomAsync returned.\r\n"); } catch {}
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CaptureActiveContext] Synced JS DOM to _activeDom hash={dom.GetHashCode()}\r\n"); } catch {}
                }
                catch { }
            }
            
            if (_activeJs != null)
            {
                try { _activeJs.FetchOverride = ScriptFetcher; }
                catch { }
            }

            // Extract and fire title
            try
            {
                string title = null;
                var tnode = dom.Descendants().FirstOrDefault(n => n.Tag == "title");
                if (tnode != null) title = tnode.Text;
                
                if (!string.IsNullOrWhiteSpace(title))
                {
                    TitleChanged?.Invoke(this, title);
                }
                else if (baseUri != null)
                {
                    TitleChanged?.Invoke(this, baseUri.Host);
                }
            }
            catch { }
        }

        private void ConfigureMedia(double? viewportWidth)
        {
            try
            {
                CssParser.MediaViewportWidth = viewportWidth;
                try { CssParser.MediaViewportHeight = (double?)GetPrimaryWindowHeight(); } catch { }
                try { CssParser.MediaDppx = 1.0; } catch { }
                try { CssParser.MediaPrefersColorScheme = "light"; } catch { CssParser.MediaPrefersColorScheme = "light"; }
            }
            catch { }
        }

        private async Task<object> BuildVisualTreeAsync(
            Element dom,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            JavaScriptEngine js,
            double? viewportWidth,
            Action<object> onFixedBackground,
            bool includeDiagnosticsBanner)
        {
            if (dom == null) return null;

            ConfigureMedia(viewportWidth);

            var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            var result = await CssLoader.ComputeWithResultAsync(dom, baseUri, cssFetcher, viewportWidth, null);
            LastComputedStyles = result.Computed;
            LastCssSources = result.Sources;
            var computed = result.Computed;

            // [MIGRATION] Background check logic removed or simplified (Avalonia Brush removed)
            // Just invoking DomReady
            
            try { DomReady?.Invoke(this, dom); } catch (Exception drEx) { try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[BuildVisualTree] DomReady error: {drEx}\r\n"); } catch { } }

            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", "[BuildVisualTree] Creating renderer...\r\n"); } catch { }

            if (_cachedRenderer == null)
            {
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", "[BuildVisualTree] Creating NEW renderer...\r\n"); } catch { }
                _cachedRenderer = new SkiaDomRenderer();
            }

            // [MIGRATION] View logic removed. Host is responsible for rendering.
            
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", "[RenderAsync] Visual tree built properly (Headless)\r\n"); } catch { }
            return _cachedRenderer;
        }

        private async Task<object> RefreshAsyncInternal(bool includeDiagnosticsBanner)
        {
            // Ensure we are on the UI thread. If not, marshal the call.
            var uiDisp = _uiDispatcher ?? UiThreadHelper.TryGetDispatcher();
            if (uiDisp != null && !UiThreadHelper.HasThreadAccess(uiDisp))
            {
                var tcs = new TaskCompletionSource<object>();
                await UiThreadHelper.RunAsyncAwaitable(uiDisp, null, async () =>
                {
                    try
                    {
                        await RefreshAsyncInternal(includeDiagnosticsBanner);
                        tcs.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                return await tcs.Task;
            }

            if (_activeDom == null)
                return null;
            
            // Debug: Log _activeDom hash
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[RefreshAsyncInternal] using _activeDom hash={_activeDom.GetHashCode()}\r\n"); } catch {}

            var fetchCss = _activeFetchCss ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            return await BuildVisualTreeAsync(
                _activeDom,
                _activeBaseUri,
                fetchCss,
                _activeImageLoader,
                _activeOnNavigate,
                _activeJs,
                _activeViewportWidth,
                _activeFixedBackground,
                includeDiagnosticsBanner).ConfigureAwait(false);
        }

        private async Task DispatchRepaintAsync(object element)
        {
            if (element == null) return;
            var handler = RepaintReady;
            if (handler == null) return;

            try
            {
                var disp = UiThreadHelper.TryGetDispatcher();
                if (disp != null && !UiThreadHelper.HasThreadAccess(disp))
                {
                    // Fix: Add 'async' here so the lambda returns the expected Task
                    await UiThreadHelper.RunAsyncAwaitable(disp, null, () =>
                    {
                        try { handler(element); }
                        catch { }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    handler(element);
                }
            }
            catch { }
        }

        private void ScheduleRepaintFromJs()
        {
            FenLogger.Debug("[CustomHtmlEngine] ScheduleRepaintFromJs called", LogCategory.Rendering);
            if (!EnableJavaScript) return;
            if (_activeDom == null) return;
            var disp = UiThreadHelper.TryGetDispatcher();
            if (System.Threading.Interlocked.Exchange(ref _repaintScheduled, 1) == 1)
            {
                // FenLogger.Debug("[CustomHtmlEngine] Repaint already scheduled", LogCategory.Rendering);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[CustomHtmlEngine] ScheduleRepaintFromJs Task running\r\n"); } catch { }
                    if (_uiDispatcher != null && !UiThreadHelper.HasThreadAccess(_uiDispatcher))
                    {
                         // ... logic ...
                    }

                    await _repaintGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        FenLogger.Debug("[CustomHtmlEngine] Calling RefreshAsyncInternal", LogCategory.Rendering);
                        var element = await RefreshAsyncInternal(includeDiagnosticsBanner: false).ConfigureAwait(false);
                        
                        await DispatchRepaintAsync(element).ConfigureAwait(false);
                        FenLogger.Debug("[CustomHtmlEngine] DispatchRepaintAsync completed", LogCategory.Rendering);
                    }
                    finally
                    {
                        _repaintGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[CustomHtmlEngine] ScheduleRepaintFromJs Error: {ex.Message}\r\n"); } catch { }
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _repaintScheduled, 0);
                }
            });
        }

        public async Task<object> RefreshAsync(bool includeDiagnosticsBanner = false)
        {
            await _repaintGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RefreshAsyncInternal(includeDiagnosticsBanner).ConfigureAwait(false);
            }
            finally
            {
                _repaintGate.Release();
            }
        }

                /// Render HTML into a XAML element using the managed engine pipeline.
        /// </summary>
        public async Task<object> RenderAsync(
            string html,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            double? viewportWidth = null,
            Action<object> onFixedBackground = null,
            bool? forceJavascript = null,
            bool disableAutoFallback = false)
        {
            // Ensure we are on the UI thread. If not, marshal the call.
            var uiDisp = _uiDispatcher ?? UiThreadHelper.TryGetDispatcher();
            if (uiDisp != null && !UiThreadHelper.HasThreadAccess(uiDisp))
            {
                var tcs = new TaskCompletionSource<object>();
                await UiThreadHelper.RunAsyncAwaitable(uiDisp, null, async () =>
                {
                    try
                    {
                        var result = await RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, onFixedBackground, forceJavascript, disableAutoFallback);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                return await tcs.Task;
            }

            await RaiseLoadingChangedAsync(true);
            
            // Store raw HTML for DOM comparison feature
            _lastRawHtml = html;

            try
            {
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", $"[CustomHtmlEngine] RenderAsync Start. HTML Length: {html?.Length ?? 0}\r\n"); } catch { }
                try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Start. HTML Length: {html?.Length ?? 0}\r\n"); } catch { }
                Console.WriteLine($"[RenderAsync] Start. HTML Length: {html?.Length ?? 0}");
                
                // PROTECTION: Validate HTML size to prevent crashes
                const int MaxHtmlSize = 50 * 1024 * 1024; // 50MB limit (increased from 2MB)
                if (!string.IsNullOrEmpty(html) && html.Length > MaxHtmlSize)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] HTML too large: {html.Length} bytes, truncating to {MaxHtmlSize}\r\n"); } catch { }
                    html = html.Substring(0, MaxHtmlSize);
                }
                
                try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] HTML size: {html?.Length ?? 0} chars\r\n"); } catch { }
                
                // 1) Parse DOM (background)
                var parser = new FenBrowser.Core.Parsing.HtmlParser(html ?? string.Empty);
                Element dom = null;
                try
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Starting parse...\r\n"); } catch { }
                    dom = await Task.Run(() =>
                    {
                        try
                        {
                            return parser.Parse();
                        }
                        catch (Exception pex)
                        {
                            try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Parse exception: {pex.Message}\r\n"); } catch { }
                            throw;
                        }
                    });
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Parse complete\r\n"); } catch { }
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Parse error: {ex.Message}\r\n"); } catch { }
                    System.Diagnostics.Debug.WriteLine("Parse error: " + ex.Message);
                    return null;
                }

                if (dom == null)
                {
                    return null;
                }
                _activeDom = dom;
                try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Parsed DOM: {dom.Tag}\r\n"); } catch { }

                // 2. Load CSS (must run on UI thread because it creates IBrushes/FontFamily which are DependencyObjects)
                try
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Starting CSS load...\r\n"); } catch { }
                    
                    // Add timeout protection for CSS loading (Wikipedia has many stylesheets)
                    var cssTask = CssLoader.ComputeAsync(dom, baseUri, fetchExternalCssAsync);
                    var timeoutTask = Task.Delay(10000); // 10 second timeout
                    var completedTask = await Task.WhenAny(cssTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] CSS loading timed out after 10s\r\n"); } catch { }
                    }
                    else
                    {
                        try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] CSS loading complete\r\n"); } catch { }
                    }
                }
                catch (Exception cssEx) 
                { 
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] CSS error: {cssEx.Message}\r\n"); } catch { }
                }

                // Declarative Shadow DOM: Find templates and attach them as shadow roots
                try
                {
                    var templates = dom.Descendants().Where(n => n.Tag == "template" && n.Attr != null && n.Attr.ContainsKey("shadowrootmode")).ToList();
                    foreach (var template in templates)
                    {
                        var parent = template.Parent as Element;
                        if (parent != null)
                        {
                            // The template content becomes the shadow root (first child element)
                            var firstElementChild = template.Children?.OfType<Element>().FirstOrDefault();
                            if (firstElementChild != null)
                                parent.ShadowRoot = firstElementChild;
                            // Detach the template itself from the main DOM
                            parent.Children?.Remove(template);
                        }
                    }
                }
                catch { }


                // Hint CSS that JS is enabled: swap 'no-js' -> 'js' on <html> element if present
                try
                {
                    var htmlNode0 = dom.Descendants().FirstOrDefault(n => n.Tag == "html");
                    if (htmlNode0 != null)
                    {
                        string cls = null; if (htmlNode0.Attr != null && htmlNode0.Attr.TryGetValue("class", out cls))
                        {
                            var parts = (cls ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                            bool changed = false;
                            if (parts.RemoveAll(s => string.Equals(s, "no-js", StringComparison.OrdinalIgnoreCase)) > 0) changed = true;
                            if (!parts.Any(s => string.Equals(s, "js", StringComparison.OrdinalIgnoreCase))) { parts.Add("js"); changed = true; }
                            if (changed)
                            {
                                htmlNode0.SetAttribute("class", string.Join(" ", parts));
                            }
                        }
                        else
                        {
                            htmlNode0.SetAttribute("class", "js");
                        }
                    }
                }
                catch { }

                // 1.25) Prewarm images in the background so first paint can swap in sooner
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CustomHtmlEngine] Prewarming images...\r\n"); } catch { }
                try { PrewarmImages(dom, baseUri, imageLoader, viewportWidth); } catch { }

                bool allowJs = EnableJavaScript;
                if (forceJavascript.HasValue)
                {
                    allowJs = forceJavascript.Value;
                }

                // NOTE: Previously we had an optimization that disabled JS if no <script> tags found.
                // This was removed because:
                // 1. Sites may lazy-load scripts
                // 2. Sites depend on JS class presence (no-js -> js) for CSS styling
                // 3. It broke JS detection on whatismybrowser.com and similar sites
                // The trade-off (slightly slower pages without scripts) is acceptable for better compatibility.

                // Performance fast-path: for clearly JS-heavy SPA/app-shell sites that
                // our engine cannot fully support (e.g., modern google.com), skip
                // JavaScript execution entirely to avoid 20-30s loads and double renders.
                if (allowJs && IsJsHeavyAppShell(baseUri))
                {
                    try { System.Diagnostics.Debug.WriteLine("[SAFE-MODE] Skipping JS for heavy app-shell site " + baseUri); } catch { }
                    allowJs = false;
                }
                try { System.Diagnostics.Debug.WriteLine("[JS ENABLE] initial EnableJavaScript=" + EnableJavaScript + " baseUri=" + (baseUri!=null? baseUri.AbsoluteUri: "(null)")); } catch { }

                var jsOverride = BrowserCoreHelpers.GetJsQueryOverride(baseUri);
                if (jsOverride.HasValue)
                {
                    allowJs = jsOverride.Value;
                    try { System.Diagnostics.Debug.WriteLine("[JS ENABLE] query override detected -> " + allowJs); } catch { }
                }

                try { System.Diagnostics.Debug.WriteLine("[JS ENABLE] final allowJs=" + allowJs); } catch { }

                if (allowJs)
                {
                    try
                    {
                        var noscripts = dom.Descendants()
                            .Where(n => string.Equals(n.Tag, "noscript", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        int removed = 0;
                        foreach (var node in noscripts)
                        {
                            node.Remove(); removed++;
                        }
                        try { System.Diagnostics.Debug.WriteLine("[JS ENABLE] removed noscript count=" + removed); } catch { }
                    }
                    catch { }
                }

                var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });

                JavaScriptEngine js = null;
                if (allowJs)
                {
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CustomHtmlEngine] Creating JavaScriptEngine...\r\n"); } catch { }
                    js = new JavaScriptEngine(new JsHostAdapter(
                        navigate: onNavigate,
                        post: (_, __) => { },
                        status: _ => { },
                        requestRender: ScheduleRepaintFromJs,
                        invokeOnUiThread: action =>
                        {
                            try
                            {
                                var disp = UiThreadHelper.TryGetDispatcher();
                                // Fix: Never block threads with .Wait() in WP8.1
                                if (disp != null && !UiThreadHelper.HasThreadAccess(disp))
                                {
                                    // [MIGRATION] Headless/Custom host - just run action or assume thread safety
                                    action();
                                }
                                else action();
                            }
                            catch { action(); }
                        },
                        setTitle: null,
                        alert: (msg) => { AlertTriggered?.Invoke(msg); },
                        log: (msg) => 
                        { 
                            FenLogger.Debug($"[CustomHtmlEngine] Received log from JS Engine: {msg}", LogCategory.JavaScript);
                            ConsoleMessage?.Invoke(msg); 
                        },
                        scrollToElement: (el) =>
                        {
                            try
                            {
                                    // [MIGRATION] Dispatcher removed, and BringIntoView removed.
                                    // if (JavaScriptEngine.GetControlForElement(el) != null) ...
                            }
                            catch { }
                        }))
                    {
                        Sandbox = allowJs ? SandboxPolicy.AllowAll : SandboxPolicy.NoScripts,
                        AllowExternalScripts = allowJs,
                        SubresourceAllowed = (u, kind) =>
                        {
                            if (!allowJs) return false;
                            // Check CSP if a policy is active
                            if (ActivePolicy != null)
                            {
                                // Map kind to CSP directive
                                string directive = kind switch
                                {
                                    "script" => "script-src",
                                    "style" => "style-src",
                                    "img" => "img-src",
                                    "font" => "font-src",
                                    "media" => "media-src",
                                    "connect" => "connect-src",
                                    "frame" => "frame-src",
                                    "object" => "object-src",
                                    _ => "default-src"
                                };
                                return ActivePolicy.IsAllowed(directive, u); // External resource, not inline
                            }
                            return true;
                        },
                        ExecuteInlineScriptsOnInnerHTML = allowJs
                    };
                    
                    // Wire up permission requests
                    js.PermissionRequested += async (origin, perm) => 
                    {
                        if (PermissionRequested != null) return await PermissionRequested(origin, perm);
                        return false;
                    };

                    _activeJs = js;
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CustomHtmlEngine] JavaScriptEngine Created.\r\n"); } catch { }
                }

                if (js != null)
                {
                    js.CookieBridge = scope => _jsCookieJar;
                    js.UseMiniPrattEngine = true;
                    js.RequestRender = ScheduleRepaintFromJs;

                    // When a ResourceManager-backed fetcher is available, reuse it for
                    // script text as well so we benefit from its disk cache and
                    // origin partitioning. We pass secFetchDest="script".
                    if (fetchExternalCssAsync != null)
                    {
                        js.ExternalScriptFetcher = async (u, referer2) =>
                        {
                            try
                            {
                                // We don't have direct access to secFetchDest here, but
                                // the CSS fetcher already understands origin partitioning
                                // and disk cache behavior. For scripts, we can call back
                                // into BrowserApi/ResourceManager via ScriptFetcher when
                                // configured, or fall back to text fetch with a script
                                // Accept header in the navigation stack.
                                if (ScriptFetcher != null)
                                {
                                    return await ScriptFetcher(u).ConfigureAwait(false);
                                }
                            }
                            catch { }
                            return null;
                        };
                    }
                }

                // Domain-specific tuning: Facebook relies on heavier JS bundles to
                // replace its initial skeleton UI. For facebook.* hosts we allow a
                // larger per-page JS byte budget so more of the app can execute.
                try
                {
                    if (js != null && baseUri != null)
                    {
                        var host = (baseUri.Host ?? string.Empty).ToLowerInvariant();
                        if (host.EndsWith("facebook.com", StringComparison.Ordinal))
                        {
                            // Roughly 512 KB; this is still far smaller than a
                            // desktop browser might execute, but enough to let
                            // primary bundles run.
                            js.PageScriptByteBudget = 512 * 1024;
                            System.Diagnostics.Debug.WriteLine("[JS-BUDGET] Facebook host detected; budget raised to " + js.PageScriptByteBudget + " bytes for " + baseUri);
                        }
                    }
                }
                catch { }

                // 5. Capture context (DOM + JS) for repaints/interactions
                await CaptureActiveContextAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, viewportWidth, onFixedBackground, js).ConfigureAwait(false);

                // 6. Build visual tree
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CustomHtmlEngine] Calling BuildVisualTreeAsync...\r\n"); } catch { }
                Console.WriteLine("[RenderAsync] Building Visual Tree");
                
                // Fetch CSS is handled inside BuildVisualTreeAsync or passed as delegate
                var control = await BuildVisualTreeAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, js, viewportWidth, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                
                // 7. Execute Scripts (if enabled)
                if (js != null)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Running Scripts\r\n"); } catch { }
                    Console.WriteLine("[RenderAsync] Running Scripts");
                    
                    // STEP 1: Inject JS detection helper (runs first to ensure sites detect JS is enabled)
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_trace.txt", "[CustomHtmlEngine] Running JS...\r\n"); } catch { }
                    // STEP 1: Enable JS classes on HTML/BODY immediately
                    try
                    {
                        var detectionHelper = @"
(function() {
    try {
        var html = document.documentElement;
        if (html) {
            html.className = html.className.replace('no-js', 'js');
            if (html.className.indexOf('js') < 0) html.className += ' js';
        }
        // Handle explicit <noscript> hiding manually since we don't have CSS processor for it yet
        var nojs = document.getElementsByTagName('noscript');
        for (var i = 0; i < nojs.length; i++) {
            if (nojs[i] && nojs[i].style) nojs[i].style.display = 'none';
        }
        // Show elements that should be visible when JS is enabled
        var jsEnabled = document.querySelectorAll('.js-enabled, .with-js, [data-js]');
        for (var i = 0; i < jsEnabled.length; i++) {
            if (jsEnabled[i] && jsEnabled[i].style) jsEnabled[i].style.display = '';
        }
    } catch(e) {}
})();
";
                        js.Evaluate(detectionHelper);
                        try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Detection helper script executed\r\n"); } catch { }
                    }
                    catch (Exception dhEx)
                    {
                        try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Detection helper error: {dhEx.Message}\r\n"); } catch { }
                    }
                    
                    // STEP 2: Run main page scripts
                    try 
                    {
                        // Add timeout protection for script execution (Wikipedia has many scripts)
                        var scriptTask = Task.Run(async () => { await js.SetDomAsync(dom, baseUri).ConfigureAwait(false); });
                        var timeoutTask = Task.Delay(15000); // 15 second timeout
                        var completedTask = await Task.WhenAny(scriptTask, timeoutTask);
                        
                        if (completedTask == timeoutTask)
                        {
                            try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Script execution timed out after 15s\r\n"); } catch { }
                            Console.WriteLine("[RenderAsync] Script execution timed out");
                        }
                        else
                        {
                            try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Scripts Finished\r\n"); } catch { }
                            Console.WriteLine("[RenderAsync] Scripts Finished");
                        }
                    } 
                    catch (Exception ex) 
                    { 
                        try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Script Error: {ex.Message}\r\n"); } catch { }
                        Console.WriteLine($"[RenderAsync] Script Error: {ex.Message}"); 
                    }
                }
                else
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Scripts SKIPPED (allowJs={allowJs})\r\n"); } catch { }
                    Console.WriteLine("[RenderAsync] Running Scripts (SKIPPED)");
                    Console.WriteLine("[RenderAsync] Scripts Finished (SKIPPED)");
                }

                try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Building visual tree...\r\n"); } catch { }
                object element = null;
                try
                {
                    element = await BuildVisualTreeAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, js, viewportWidth, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Visual tree built successfully\r\n"); } catch { }
                }
                catch (Exception vtEx)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Visual tree error: {vtEx}\r\n"); } catch { }
                    Console.WriteLine($"[RenderAsync] Visual tree error: {vtEx}");
                    // Return null on error rather than crashing
                    return null;
                }

                // Auto-fallback (re-render without JS) only makes sense when we
                // actually attempted JS and are not in app-shell safe-mode.
                if (allowJs && !disableAutoFallback && !IsJsHeavyAppShell(baseUri))
                {
                    bool isEmpty;
                    try { isEmpty = element == null; /* IsEffectivelyEmpty(element); // Legacy removed */ }
                    catch { isEmpty = element == null; }

                    if (isEmpty)
                    {
                        try { System.Diagnostics.Debug.WriteLine("[RENDER] JS path empty, retrying without scripts"); } catch { }
                        await RaiseLoadingChangedAsync(false);
                        return await RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, onFixedBackground, forceJavascript: false, disableAutoFallback: true).ConfigureAwait(false);
                    }
                }

                try { System.Diagnostics.Debug.WriteLine("[RENDER] Final element null=" + (element == null)); } catch { }
                return element;
            }
            finally
            {
                await RaiseLoadingChangedAsync(false);
            }
        }

                /// Convenience wrapper to kick off a render without awaiting the resulting element.
        /// Useful for non-visual navigations when only DOM/state is needed.
        /// </summary>
        public void LoadHtml(
            string html,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            double? viewportWidth = null,
            Action<object> onFixedBackground = null)
        {
            try { var _ = RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, onFixedBackground); }
            catch { }
        }

        /// <summary>Expose the current active Lite DOM (last parsed).</summary>
        public Element GetActiveDom()
        {
            return _activeDom;
        }

        /// <summary>Get the raw HTML source that was last rendered.</summary>
        public string GetRawHtml()
        {
            return _lastRawHtml;
        }

        // ---------------- Cookie helpers for host APIs ----------------
        public IReadOnlyDictionary<string,string> GetCookieSnapshot(Uri scope)
        {
            var dict = new Dictionary<string,string>(StringComparer.Ordinal);
            try
            {
                var u = scope ?? _activeBaseUri; if (u == null) return dict;
                var coll = _jsCookieJar.GetCookies(u);
                if (coll != null)
                {
                    foreach (System.Net.Cookie c in coll)
                    {
                        if (!dict.ContainsKey(c.Name)) dict[c.Name] = c.Value ?? string.Empty;
                    }
                }
            }
            catch { }
            return dict;
        }

        public void SetCookie(Uri scope, string name, string value, string path = "/")
        {
            try
            {
                var u = scope ?? _activeBaseUri; if (u == null) return;
                var cookie = new System.Net.Cookie(name ?? string.Empty, value ?? string.Empty, path ?? "/", u.Host);
                _jsCookieJar.Add(u, cookie);
            }
            catch { }
        }

        public void DeleteCookie(Uri scope, string name)
        {
            try
            {
                var u = scope ?? _activeBaseUri; if (u == null) return;
                // Overwrite with expired cookie
                var expired = new System.Net.Cookie(name ?? string.Empty, string.Empty, "/", u.Host) { Expires = DateTime.UtcNow.AddDays(-1) };
                _jsCookieJar.Add(u, expired);
            }
            catch { }
        }








        public Task<string> MakeImageAsync()
        {
            return Task.FromResult<string>(null);
        }


        public async Task<object> ExecuteScriptAsync(string script)
        {
            await Task.CompletedTask;
            if (_activeJs == null) 
            {
                FenLogger.Error("[CustomHtmlEngine] ExecuteScriptAsync: _activeJs is NULL", LogCategory.JavaScript);
                return "undefined";
            }
            FenLogger.Debug($"[CustomHtmlEngine] Executing script on active engine: {script}", LogCategory.JavaScript);
            try
            {
               var res = _activeJs.Evaluate(script);
               return res;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}


