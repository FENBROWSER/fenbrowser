using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
using FenBrowser.FenEngine.Core.Interfaces; // Added for IExecutionContext
using FenBrowser.FenEngine.Layout; // Added for LayoutResult
using static FenBrowser.FenEngine.Rendering.CssLoader;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Core.EventLoop; // Added for EventLoopCoordinator
using FenBrowser.Core.Engine; // Added for EnginePhase
using SkiaSharp;


namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Clean, dependency-free wrapper suitable for WP8.1 without WebView.
    /// </summary>
    public sealed class CustomHtmlEngine : IDisposable
    {

        public Func<Uri, Task<string>> ScriptFetcher { get; set; }
        public Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> FetchHandler { get; set; }

        private CspPolicy _activePolicy;
        /// <summary>Active Content Security Policy for this page. When set, subresource loads are checked against it.</summary>
        public CspPolicy ActivePolicy 
        { 
            get => _activePolicy; 
            set 
            {
                FenLogger.Debug($"[ActivePolicy] SET to {(value == null ? "NULL" : "Instance hash=" + value.GetHashCode())} Stack={Environment.StackTrace}", LogCategory.Rendering);
                _activePolicy = value; 
            }
        }

        // EXPOSED STYLES FOR SKIA RENDERER
        public Dictionary<Node, CssComputed> LastComputedStyles { get; private set; }
        public List<CssLoader.CssSource> LastCssSources { get; private set; }

        public LayoutResult LastLayout { get; private set; }
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


        private FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge _historyBridge;

        public void InitHistory(FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge bridge)
        {
            _historyBridge = bridge;
            if (_activeJs != null) _activeJs.SetHistoryBridge(bridge);
        }

        public void NotifyPopState(object state)
        {
             _activeJs?.NotifyPopState(state);
        }


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

        public void HandlePointerEvent(string eventType, float x, float y)
        {
            if (_cachedRenderer == null || _activeJs == null) return;
            try
            {
                if (_cachedRenderer.HitTest(x, y, out var result))
                {
                    if (result.NativeElement is Element el)
                    {
                         // Dispatch to JS
                        _activeJs.DispatchEventForElement(el, eventType);
                    }
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[CustomHtmlEngine] HandlePointerEvent error: {ex.Message}", LogCategory.Rendering);
            }
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

        private DateTime _lastRepaintTime = DateTime.MinValue;

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

        // Heuristic removed: No site-specific optimizations.
        private static bool IsJsHeavyAppShell(Uri baseUri)
        {
            return false;
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
            catch (Exception ex)
            {
                FenLogger.Warn($"[CustomHtmlEngine] Dispose error: {ex.Message}", LogCategory.Rendering);
            }
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
                        catch (Exception ex) 
                        {
                            FenLogger.Error($"[CustomHtmlEngine] LoadingChanged handler error (async): {ex.Message}", LogCategory.Rendering);
                        }
                        return Task.CompletedTask;
                    });
                }
                else
                {
                    handler(this, isLoading);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[CustomHtmlEngine] LoadingChanged handler error: {ex.Message}", LogCategory.Rendering);
            }
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
            catch (Exception ex)
            {
                FenLogger.Debug($"[CustomHtmlEngine] ResolveUri failed for '{href}': {ex.Message}", LogCategory.Rendering);
            }
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
            catch (Exception ex)
            {
                FenLogger.Debug($"[CustomHtmlEngine] RewriteWebPToJpg failed for '{u}': {ex.Message}", LogCategory.Rendering);
            }
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
                            catch (Exception ex)
                            {
                                FenLogger.Warn($"[CustomHtmlEngine] Image load task failed: {ex.Message}", LogCategory.Rendering);
                            } 
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
                    catch (Exception ex)
                    {
                        FenLogger.Debug($"[CustomHtmlEngine] PrewarmImages node processing error: {ex.Message}", LogCategory.Rendering);
                    }
                }
                // Fire-and-forget; we do not await prewarm tasks to avoid blocking render
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[CustomHtmlEngine] PrewarmImages failed: {ex.Message}", LogCategory.Rendering);
            }
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
            catch (Exception ex)
            {
                FenLogger.Debug($"[CustomHtmlEngine] VisualChildCount error: {ex.Message}", LogCategory.Rendering);
            }
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
            catch (Exception ex)
            {
                 FenLogger.Debug($"[CustomHtmlEngine] VisualGetChild error: {ex.Message}", LogCategory.Rendering);
            }
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
                    FenLogger.Debug("[CaptureActiveContext] Calling SetDomAsync...", LogCategory.Rendering);
                    await _activeJs.SetDomAsync(dom, baseUri).ConfigureAwait(false);
                    FenLogger.Debug("[CaptureActiveContext] SetDomAsync returned.", LogCategory.Rendering);
                    FenLogger.Debug($"[CaptureActiveContext] Synced JS DOM to _activeDom hash={dom.GetHashCode()}", LogCategory.Rendering);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[CustomHtmlEngine] Failed to sync ActiveDom to JS: {ex.Message}", LogCategory.Rendering);
                }
            }
            
            if (_activeJs != null)
            {
                try { _activeJs.FetchOverride = ScriptFetcher; }
                catch (Exception ex) { FenLogger.Warn($"[CustomHtmlEngine] Failed to set FetchOverride: {ex.Message}", LogCategory.Rendering); }
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
            catch (Exception ex)
            {
                FenLogger.Warn($"[CustomHtmlEngine] Title extraction failed: {ex.Message}", LogCategory.Rendering);
            }
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
            catch (Exception ex)
            {
                FenLogger.Warn($"[CustomHtmlEngine] ConfigureMedia failed: {ex.Message}", LogCategory.Rendering);
            }
        }

        private async Task<object> BuildVisualTreeAsync(
            Element dom,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            JavaScriptEngine js,
            double? viewportWidth,
            double? viewportHeight,
            Action<object> onFixedBackground,
            bool includeDiagnosticsBanner)
        {
            if (dom == null) return null;
            var _buildTreeStopwatch = System.Diagnostics.Stopwatch.StartNew();

            ConfigureMedia(viewportWidth);

            var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            
            // NOTE: Don't set LastComputedStyles = null here!
            // The EngineLoop polls for styles during CSS computation (which can take 20+ seconds).
            // Setting to null causes the renderer to skip styling until computation completes.
            // Instead, keep the previous styles visible until new ones are ready.
            LastCssSources = null;
            try
            {
                FenLogger.Debug("[CustomHtmlEngine] BuildVisualTree: Using CSS Engine...", LogCategory.Rendering);
                
                // Use the configured CSS engine
                var cssEngine = CssEngineFactory.GetEngine();
                FenLogger.Debug($"[CustomHtmlEngine] BuildVisualTree: Engine={cssEngine.EngineName}", LogCategory.Rendering);
                
                LastComputedStyles = await cssEngine.ComputeStylesAsync(dom, baseUri, cssFetcher, viewportWidth, viewportHeight);
                FenLogger.Debug($"[PERF] CSS ComputeStyles: {_buildTreeStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                // CRITICAL FIX: Assign computed styles to nodes so Layout Engine can see them!
                if (LastComputedStyles != null)
                {
                    foreach (var kvp in LastComputedStyles)
                    {
                        if (kvp.Key != null)
                        {
                            kvp.Key.ComputedStyle = kvp.Value;
                        }
                    }
                }
                
                // [Verification] Register success
                FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(false, LastComputedStyles.Count);
                
                // Get CSS sources from engine for DevTools
                try
                {
                    if (cssEngine is Css.CustomCssEngine customEngine)
                    {
                        LastCssSources = customEngine.LastSources;
                    }
                }
                catch (Exception srcEx)
                {
                    FenLogger.Error($"[CustomHtmlEngine] CSS Sources retrieval error: {srcEx.Message}", LogCategory.Rendering);
                    LastCssSources = null;
                }
                FenLogger.Debug($"[PERF] BuildVisualTree Complete: {_buildTreeStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                
                FenLogger.Debug($"[CustomHtmlEngine] BuildVisualTree: CSS Success. Styles Count={LastComputedStyles?.Count}", LogCategory.Rendering);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[CustomHtmlEngine] CssLoader CRASH: {ex}", LogCategory.Rendering);
                // Continue to ensure RepaintReady fires even if CSS fails? 
                // Better to have partial render than white screen if possible
                LastComputedStyles = new Dictionary<Node, CssComputed>();
            }

            // var computed = result.Computed; // Use LastComputedStyles instead
            var computed = LastComputedStyles;

            // [MIGRATION] Background check logic removed or simplified (Avalonia Brush removed)
            // Just invoking DomReady
            
            try { DomReady?.Invoke(this, dom); } catch (Exception drEx) { FenLogger.Error($"[BuildVisualTree] DomReady error: {drEx}", LogCategory.Rendering); }

            FenLogger.Debug("[BuildVisualTree] Creating renderer...", LogCategory.Rendering);

            if (_cachedRenderer == null)
            {
                FenLogger.Debug("[BuildVisualTree] Creating NEW renderer...", LogCategory.Rendering);
                _cachedRenderer = new SkiaDomRenderer();
            }

            // [MIGRATION] View logic removed. Host is responsible for rendering.
            
            FenLogger.Debug("[RenderAsync] Visual tree built properly (Headless)", LogCategory.Rendering);
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
            // Debug: Log _activeDom hash
            FenLogger.Debug($"[RefreshAsyncInternal] using _activeDom hash={_activeDom.GetHashCode()}", LogCategory.Rendering);

            var fetchCss = _activeFetchCss ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            return await BuildVisualTreeAsync(
                _activeDom,
                _activeBaseUri,
                fetchCss,
                _activeImageLoader,
                _activeOnNavigate,
                _activeJs,
                _activeViewportWidth,
                (double?)GetPrimaryWindowHeight(),
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
                        catch (Exception ex) { FenLogger.Error($"[CustomHtmlEngine] RepaintReady async handler error: {ex.Message}", LogCategory.Rendering); }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    handler(element);
                }
            }
            catch (Exception ex)
            {
                 FenLogger.Error($"[CustomHtmlEngine] DispatchRepaintAsync error: {ex.Message}", LogCategory.Rendering);
            }
        }

                private void ScheduleRepaintFromJs()
        {
            // STRICT CONTROL FLOW:
            // JavaScript CANNOT push frames. It can only mark state as dirty.
            
            // 1. ASSERT: JS cannot trigger rendering logic directly.
            EnginePhaseManager.AssertNotInPhase(EnginePhase.JSExecution, EnginePhase.Microtasks);

            FenLogger.Debug("[CustomHtmlEngine] ScheduleRepaintFromJs (Dirty Flag Set)", LogCategory.Rendering);

            // 2. Mark dirty ONLY via the Coordinator.
            // The Event Loop or Host will check this flag at the appropriate checkpoint.
            EventLoopCoordinator.Instance.NotifyLayoutDirty();
        }

        private async Task<Element> RunDomParseAsync(string html)
        {
            // CRITICAL FIX: Use production HtmlTreeBuilder from Core.Parsing
            // The FenEngine version doesn't properly implement RAWTEXT state for style/script,
            // causing CSS content to leak as visible text nodes
            var builder = new FenBrowser.Core.Parsing.HtmlTreeBuilder(html ?? string.Empty);
            try
            {
                FenLogger.Debug("[RenderAsync] Starting parse (Production Parser)...", LogCategory.Rendering);
                var doc = await Task.Run(() =>
                {
                    try { return builder.Build(); }
                    catch (Exception pex)
                    {
                        FenLogger.Error($"[RenderAsync] Parse exception: {pex.Message}", LogCategory.Rendering);
                        return new FenBrowser.Core.Dom.Document();
                    }
                });
                
                FenLogger.Debug("[RenderAsync] Parse complete", LogCategory.Rendering);
                
                // DEBUG: Dump DOM
                try {
                     var sb = new StringBuilder();
                     DumpTree(doc.DocumentElement ?? doc, sb, 0);
                     System.IO.File.WriteAllText(@"c:\Users\udayk\Videos\FENBROWSER\dom_dump.txt", sb.ToString());
                } catch {}

                // Return DocumentElement (the HTML element), not the Document wrapper
                return doc.DocumentElement ?? doc;
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[RenderAsync] Parse error: {ex.Message}", LogCategory.Rendering);
                return null;
            }
        }

        private async Task LoadCssAsync(Element dom, Uri baseUri, Func<Uri, Task<string>> fetchExternalCssAsync)
        {
             try
             {
                 FenLogger.Debug("[RenderAsync] Starting CSS load...", LogCategory.Rendering);
                 var cssTask = CssLoader.ComputeAsync(dom, baseUri, fetchExternalCssAsync, null, null, (msg) => FenLogger.Debug(msg, LogCategory.Rendering));
                 var timeoutTask = Task.Delay(30000); // Increased from 10s to 30s for complex pages
                 var completedTask = await Task.WhenAny(cssTask, timeoutTask);
                 
                 if (completedTask == timeoutTask)
                 {
                     FenLogger.Warn("[RenderAsync] CSS loading timed out after 30s", LogCategory.Rendering);
                     FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(true, 0);
                 }
                 else
                 {
                     // CRITICAL FIX: Actually store the computed styles!
                     LastComputedStyles = await cssTask;
                     FenLogger.Info($"[RenderAsync] CSS loading complete. Styles Count={LastComputedStyles?.Count ?? 0}", LogCategory.Rendering);
                     FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(false, LastComputedStyles?.Count ?? 0);
                     
                     // CRITICAL FIX: Trigger repaint after CSS completes so layout re-runs with styles
                     // This ensures flexbox centering, visibility, and other CSS properties are applied
                     FenLogger.Debug("[RenderAsync] Triggering repaint after CSS completion", LogCategory.Rendering);
                     OnRepaintReady(null);
                 }
             }
             catch (Exception cssEx) 
             { 
                 FenLogger.Error($"[RenderAsync] CSS error: {cssEx.Message}", LogCategory.Rendering);
             }
        }

        private JavaScriptEngine SetupJavaScriptEngine(
             Uri baseUri, 
             Action<Uri> onNavigate, 
             bool allowJs, 
             Func<Uri, Task<string>> fetchExternalCssAsync)
        {
             if (!allowJs) return null;

             FenLogger.Debug("[CustomHtmlEngine] Creating JavaScriptEngine...", LogCategory.Rendering);
             var js = new JavaScriptEngine(new JsHostAdapter(
                 navigate: onNavigate,
                 post: (_, __) => { },
                 status: _ => { },
                 requestRender: ScheduleRepaintFromJs,
                 invokeOnUiThread: action =>
                 {
                     try
                     {
                         var disp = UiThreadHelper.TryGetDispatcher();
                         if (disp != null && !UiThreadHelper.HasThreadAccess(disp)) action();
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
                 scrollToElement: (el) => { }))
             {
                 Sandbox = allowJs ? SandboxPolicy.AllowAll : SandboxPolicy.NoScripts,
                 AllowExternalScripts = allowJs,
                 SubresourceAllowed = (u, kind) =>
                 {
                     if (!allowJs) return false;
                     if (ActivePolicy != null)
                     {
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
                         return ActivePolicy.IsAllowed(directive, u); 
                     }
                     return true;
                 },
                 ExecuteInlineScriptsOnInnerHTML = allowJs
             };

             // Wire up CSP Nonce check
             js.NonceAllowed = (nonce) =>
             {
                 if (!allowJs) return false;
                 if (ActivePolicy != null)
                 {
                     bool allowed = ActivePolicy.IsAllowed("script-src", null, nonce, isInline: true);
                     if (allowed && nonce == "wrong456") 
                     {
                          FenLogger.Error($"[CSP-CRITICAL] Nonce 'wrong456' ALLOWED! ActivePolicy hash={ActivePolicy.GetHashCode()}", LogCategory.Rendering);
                          // Dump directives
                          foreach (var d in ActivePolicy.Directives)
                          {
                              FenLogger.Error($"  [DIR] {d.Key}: Sources={string.Join(",",d.Value.Sources)} Nonces={string.Join(",",d.Value.Nonces)}", LogCategory.Rendering);
                          }
                     }
                     return allowed;
                 }
                 FenLogger.Warn("[CSP] ActivePolicy is NULL during nonce check!", LogCategory.Rendering);
                 return true;
             };
             
             // Wire up permission requests
             js.PermissionRequested += async (origin, perm) => 
             {
                 if (PermissionRequested != null) return await PermissionRequested(origin, perm);
                 return false;
             };

             js.CookieBridge = scope => _jsCookieJar;
             js.UseMiniPrattEngine = true;
             js.RequestRender = ScheduleRepaintFromJs;

             if (fetchExternalCssAsync != null)
             {
                 js.ExternalScriptFetcher = async (u, referer2) =>
                 {
                     if (ScriptFetcher != null) return await ScriptFetcher(u).ConfigureAwait(false);
                     return null;
                 };
             }

             // Domain-specific tuning
             try
             {
                 if (baseUri != null)
                 {
                     var host = (baseUri.Host ?? string.Empty).ToLowerInvariant();
                     if (host.EndsWith("facebook.com", StringComparison.Ordinal))
                     {
                         js.PageScriptByteBudget = 512 * 1024;
                     }
                 }
             }
             catch { }

             if (js != null) 
             {
                 js.FetchHandler = FetchHandler;
                 // [Compliance] Inject Window Dimensions
                 if (_activeViewportWidth.HasValue) js.WindowWidth = _activeViewportWidth.Value;
                 js.WindowHeight = GetPrimaryWindowHeight();
             }

             return js;
        }

        private async Task RunScriptsAsync(JavaScriptEngine js, Element dom, Uri baseUri)
        {
            if (js == null) return;
            
            FenLogger.Debug("[RenderAsync] Running Scripts", LogCategory.Rendering);

            // 1. Detection helper
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
        // DISABLED: FenBrowser keeps noscript visible for fallback content
        // var nojs = document.getElementsByTagName('noscript');
        // for (var i = 0; i < nojs.length; i++) {
        //     if (nojs[i] && nojs[i].style) nojs[i].style.display = 'none';
        // }
        var jsEnabled = document.querySelectorAll('.js-enabled, .with-js, [data-js]');
        for (var i = 0; i < jsEnabled.length; i++) {
            if (jsEnabled[i] && jsEnabled[i].style) jsEnabled[i].style.display = '';
        }
    } catch(e) {}
})();";
                 js.Evaluate(detectionHelper);
                 FenLogger.Debug("[RenderAsync] Detection helper script executed", LogCategory.Rendering);
            }
            catch (Exception dhEx)
            {
                 FenLogger.Warn($"[RenderAsync] Detection helper error: {dhEx.Message}", LogCategory.Rendering);
            }

            // 2. Main Page Scripts
            try 
            {
                var scriptTask = Task.Run(async () => { await js.SetDomAsync(dom, baseUri).ConfigureAwait(false); });
                var timeoutTask = Task.Delay(15000); 
                var completedTask = await Task.WhenAny(scriptTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                    FenLogger.Warn("[RenderAsync] Script execution timed out after 15s", LogCategory.Rendering);
                else
                    FenLogger.Debug("[RenderAsync] Scripts Finished", LogCategory.Rendering);
            } 
            catch (Exception ex) 
            { 
                FenLogger.Error($"[RenderAsync] Script Error: {ex.Message}", LogCategory.Rendering);
            }
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
            var _pageLoadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Store raw HTML for DOM comparison feature
            _lastRawHtml = html;

            try
            {
                FenLogger.Info($"[CustomHtmlEngine] RenderAsync Start. HTML Length: {html?.Length ?? 0}", LogCategory.Rendering);
                
                const int MaxHtmlSize = 50 * 1024 * 1024; 
                if (!string.IsNullOrEmpty(html) && html.Length > MaxHtmlSize)
                {
                    FenLogger.Warn($"[RenderAsync] HTML too large: {html.Length} bytes, truncating to {MaxHtmlSize}", LogCategory.Rendering);
                    html = html.Substring(0, MaxHtmlSize);
                }

                // 1. Helper: Parse DOM
                var dom = await RunDomParseAsync(html);
                if (dom == null) return null;
                _activeDom = dom;
                
                // CRITICAL FIX: Invalidate old styles immediately so the renderer stops using them.
                // This forces BrowserIntegration to wait (showing previous frame or spinner) until new styles are ready.
                LastComputedStyles = null;
                
                FenLogger.Debug($"[PERF] DOM Parse: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                // 2. Helper: Load CSS
                await LoadCssAsync(dom, baseUri, fetchExternalCssAsync);
                FenLogger.Debug($"[PERF] CSS Load: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                // 2.5. Security: CSP Meta Parsing
                ActivePolicy = null;
                try
                {
                    // Scan HEAD for <meta http-equiv="Content-Security-Policy">
                    // Simple search in all descendants or just head? Descendants is safer if HEAD parsing is loose.
                    var metaCsp = dom.Descendants()
                        .FirstOrDefault(n => 
                            string.Equals(n.Tag, "meta", StringComparison.OrdinalIgnoreCase) &&
                            n.Attr != null && 
                            n.Attr.TryGetValue("http-equiv", out var equiv) && 
                            string.Equals(equiv, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase));
                    
                    if (metaCsp != null && metaCsp.Attr.TryGetValue("content", out var cspContent))
                    {
                         ActivePolicy = CspPolicy.Parse(cspContent);
                         FenLogger.Info($"[Security] Active CSP from Meta: {cspContent}", LogCategory.Rendering);
                    }
                }
                catch (Exception cspEx)
                {
                    FenLogger.Warn($"[Security] Failed to parse CSP meta: {cspEx.Message}", LogCategory.Rendering);
                }

                // 3. Declarative Shadow DOM
                try
                {
                    var templates = dom.Descendants().Where(n => n.Tag == "template" && n.Attr != null && n.Attr.ContainsKey("shadowrootmode")).ToList();
                    foreach (var template in templates)
                    {
                        var parent = template.Parent as Element;
                        if (parent != null)
                        {
                            var mode = template.Attr["shadowrootmode"];
                            if (mode == "open" || mode == "closed")
                            {
                                try
                                {
                                    var shadow = parent.AttachShadow(mode);
                                    // Move children from template to shadow root
                                    var children = template.Children?.ToList();
                                    if (children != null)
                                    {
                                        foreach (var child in children)
                                        {
                                            shadow.AppendChild(child);
                                        }
                                    }
                                    // Remove the template element itself
                                    if (parent.Children != null) parent.Children.Remove(template);
                                }
                                catch (Exception dsdEx)
                                {
                                    FenLogger.Warn($"[DSD] Failed to attach shadow root: {dsdEx.Message}", LogCategory.Rendering);
                                }
                            }
                        }
                    }
                }
                catch { }

                // 4. Prewarm Images
                FenLogger.Debug("[CustomHtmlEngine] Prewarming images...", LogCategory.Rendering);
                try { PrewarmImages(dom, baseUri, imageLoader, viewportWidth); } catch { }

                // 5. Setup Javascript
                bool allowJs = EnableJavaScript;
                if (forceJavascript.HasValue) allowJs = forceJavascript.Value;

                if (allowJs && IsJsHeavyAppShell(baseUri))
                {
                    FenLogger.Debug($"[SAFE-MODE] Skipping JS for heavy app-shell site {baseUri}", LogCategory.Rendering);
                    allowJs = false;
                }

                if (allowJs)
                {
                    // DISABLED: FenBrowser has limited JS support, so we keep noscript 
                    // fallback content visible for sites that heavily depend on JS (like Google Search)
                    // var noscripts = dom.Descendants().Where(n => string.Equals(n.Tag, "noscript", StringComparison.OrdinalIgnoreCase)).ToList();
                    // foreach (var node in noscripts) node.Remove();
                }

                // FIX: Google Search puts <style>table,div,span,p{display:none}</style> inside <noscript>.
                // Since we render <noscript> (due to partial JS support), this style applies globally and hides everything.
                // We must remove <style> tags from potentially visible <noscript> elements.
                try
                {
                    var noscriptElements = dom.Descendants()
                        .Where(n => string.Equals(n.Tag, "noscript", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var ns in noscriptElements)
                    {
                        var stylesInNoscript = ns.Descendants()
                            .Where(s => string.Equals(s.Tag, "style", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        
                        foreach (var style in stylesInNoscript)
                        {
                            FenLogger.Debug($"[CustomHtmlEngine] Removing harmful <style> from <noscript>", LogCategory.Rendering);
                            style.Remove();
                        }
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[CustomHtmlEngine] Failed to sanitize noscript: {ex.Message}", LogCategory.Rendering);
                }

                _activeJs = SetupJavaScriptEngine(baseUri, onNavigate, allowJs, fetchExternalCssAsync);
                if (_activeJs != null && _historyBridge != null) _activeJs.SetHistoryBridge(_historyBridge);
                FenLogger.Debug($"[PERF] JS Setup: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });
                await CaptureActiveContextAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, viewportWidth, onFixedBackground, _activeJs).ConfigureAwait(false);

                FenLogger.Debug("[CustomHtmlEngine] Calling BuildVisualTreeAsync...", LogCategory.Rendering);
                var vh = (double?)GetPrimaryWindowHeight();
                var control = await BuildVisualTreeAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, _activeJs, viewportWidth, vh, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                FenLogger.Debug($"[PERF] Visual Tree 1: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                // 6. Run Scripts
                object element = control; 
                if (_activeJs != null)
                {
                    await RunScriptsAsync(_activeJs, dom, baseUri);
                    FenLogger.Debug($"[PERF] Script Run: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                    // Re-build visual tree after scripts
                    FenLogger.Debug("[RenderAsync] Re-building visual tree...", LogCategory.Rendering);
                    try
                    {
                        var vh2 = (double?)GetPrimaryWindowHeight();
                        element = await BuildVisualTreeAsync(dom, baseUri, cssFetcher, imageLoader, onNavigate, _activeJs, viewportWidth, vh2, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                        FenLogger.Debug($"[PERF] Visual Tree 2: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                    }
                    catch (Exception vtEx)
                    {
                        FenLogger.Error($"[RenderAsync] Visual tree error: {vtEx}", LogCategory.Rendering);
                        return null;
                    }
                }
                else
                {
                     FenLogger.Debug($"[RenderAsync] Scripts SKIPPED (allowJs={allowJs}) element={control!=null}", LogCategory.Rendering);
                }
                return element;
            }
            finally
            {
                FenLogger.Debug($"[PERF] FULL PAGE LOAD TIME: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
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
        private void DumpTree(FenBrowser.Core.Dom.Node node, StringBuilder sb, int depth)
        {
            sb.Append(new string(' ', depth * 2));
            sb.Append(node.NodeName);
            if (node is FenBrowser.Core.Dom.Element el)
            {
                if (el.Attr != null) foreach(var k in el.Attr.Keys) sb.Append($" {k}='{el.Attr[k]}'");
            }
            if (node is FenBrowser.Core.Dom.Text txt)
            {
                sb.Append($" \"{(txt.Data ?? "").Replace("\n", "\\n").Replace("\r", "\\r")}\"");
            }
            sb.AppendLine();
            if (node.Children != null)
            {
               foreach(var child in node.Children) DumpTree(child, sb, depth + 1);
            }
        }
    }
}


