using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Controls.ApplicationLifetimes;
using Control = Avalonia.Controls.Control;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.IO;
using FenBrowser.Core;
using FenBrowser.Core;
using FenBrowser.FenEngine.Scripting;
using static FenBrowser.FenEngine.Rendering.CssLoader;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Clean, dependency-free wrapper suitable for WP8.1 without WebView.
    /// </summary>
    public sealed class CustomHtmlEngine : IDisposable
    {

        public Func<Uri, Task<string>> ScriptFetcher { get; set; }

        public event Action<Control> RepaintReady;
        private void OnRepaintReady(Control control)
        {
            _lastRenderedControl = control;
            RepaintReady?.Invoke(control);
        }
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event Action<Rect?> HighlightRectChanged;
        public bool EnableJavaScript { get; set; } = true;

        public void HighlightElement(LiteElement element)
        {
            if (element == null)
            {
                RemoveHighlight();
                return;
            }

            if (JavaScriptEngine.TryGetVisualRect(element, out double x, out double y, out double w, out double h))
            {
                HighlightRectChanged?.Invoke(new Rect(x, y, w, h));
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

        public object Evaluate(string script)
        {
            if (_activeJs != null) return _activeJs.Evaluate(script);
            return null;
        }

        private LiteElement _activeDom;
        private Control _lastRenderedControl;
        private Uri _activeBaseUri;
        private Func<Uri, Task<string>> _activeFetchCss;
        private Func<Uri, Task<Stream>> _activeImageLoader;
        private Action<Uri> _activeOnNavigate;
        private double? _activeViewportWidth;
        private Action<IBrush> _activeFixedBackground;
        private JavaScriptEngine _activeJs;
        private readonly CookieContainer _jsCookieJar = new CookieContainer();
        private readonly System.Threading.SemaphoreSlim _repaintGate = new System.Threading.SemaphoreSlim(1, 1);
        private int _repaintScheduled;
        private readonly object _uiDispatcher;

        public CustomHtmlEngine()
        {
            _uiDispatcher = UiThreadHelper.TryGetDispatcher();
        }

        private static double GetPrimaryWindowWidth()
        {
            try
            {
                try { if (/* Window.Current */ (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow != null) return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Bounds.Width ?? 0; } catch { }
            }
            catch { }
            return 0;
        }

        private static double GetPrimaryWindowHeight()
        {
            try
            {
                try { if (/* Window.Current */ (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow != null) return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Bounds.Height ?? 0; } catch { }
            }
            catch { }
            return 0;
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
                if (host == "google.com" || host == "www.google.com") return true;

                return false;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            try
            {
                _repaintGate.Dispose();
                // Unsubscribe events if necessary or clean up JS engine
                _activeJs = null; 
                _activeDom = null;
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
        private static void PrewarmImages(LiteElement root, Uri baseUri, Func<Uri, Task<Stream>> imageLoader, double? viewportWidth)
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

        private static string GatherPlainText(LiteElement n)
        {
            if (n == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            Action<LiteElement> walk = null;
            walk = (el) =>
            {
                if (el == null) return;
                if (el.IsText) { var t = el.Text ?? string.Empty; sb.Append(t); return; }
                if (el.Children != null)
                {
                    for (int i = 0; i < el.Children.Count; i++) walk(el.Children[i]);
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

        private void CaptureActiveContext(
            LiteElement dom,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            double? viewportWidth,
            Action<IBrush> onFixedBackground,
            JavaScriptEngine js)
        {
            _activeDom = dom;
            _activeBaseUri = baseUri;
            _activeFetchCss = fetchExternalCssAsync;
            _activeImageLoader = imageLoader;
            _activeOnNavigate = onNavigate;
            _activeViewportWidth = viewportWidth;
            _activeFixedBackground = onFixedBackground;
            _activeJs = js;
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

        private async Task<Control> BuildVisualTreeAsync(
            LiteElement dom,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            JavaScriptEngine js,
            double? viewportWidth,
            Action<IBrush> onFixedBackground,
            bool includeDiagnosticsBanner)
        {
            if (dom == null) return null;

            ConfigureMedia(viewportWidth);

            var cssFetcher = fetchExternalCssAsync ?? (async _ => string.Empty);
            var computed = await CssLoader.ComputeAsync(dom, baseUri, cssFetcher, viewportWidth, null);

            try
            {
                if (onFixedBackground != null && computed != null)
                {
                    IBrush fixedBg = null;
                    bool bgFixed = false;
                    Func<string, string[]> splitLayers = (raw) =>
                    {
                        if (string.IsNullOrWhiteSpace(raw)) return new string[0];
                        var list = new System.Collections.Generic.List<string>();
                        var sb = new System.Text.StringBuilder(); int depth = 0; bool inQ = false; char qc = '\0';
                        foreach (var ch in raw)
                        {
                            if ((ch == '\'' || ch == '"')) { if (!inQ) { inQ = true; qc = ch; } else if (qc == ch) inQ = false; }
                            else if (!inQ && ch == '(') depth++; else if (!inQ && ch == ')') depth = Math.Max(0, depth - 1);
                            if (!inQ && depth == 0 && ch == ',') { list.Add(sb.ToString()); sb.Clear(); }
                            else sb.Append(ch);
                        }
                        if (sb.Length > 0) list.Add(sb.ToString());
                        return list.ToArray();
                    };
                    Func<LiteElement, bool> check = (el) =>
                    {
                        if (el == null) return false;
                        CssComputed c; if (!computed.TryGetValue(el, out c) || c == null) return false;
                        if (c.Background != null)
                        {
                            string att; if (c.Map != null && c.Map.TryGetValue("background-attachment", out att))
                            {
                                var atts = splitLayers(att);
                                foreach (var a in atts)
                                {
                                    if ((a ?? string.Empty).IndexOf("fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                                    { fixedBg = c.Background; return true; }
                                }
                            }
                            string bg; if (c.Map != null && c.Map.TryGetValue("background", out bg))
                            {
                                if (!string.IsNullOrWhiteSpace(bg))
                                {
                                    var layers = splitLayers(bg);
                                    foreach (var raw in layers)
                                    {
                                        var layer = (raw ?? string.Empty).Trim();
                                        if (layer.IndexOf("fixed", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            fixedBg = c.Background; return true;
                                        }
                                    }
                                }
                            }
                        }
                        return false;
                    };
                    var htmlNode = dom.Descendants().FirstOrDefault(n => n.Tag == "html");
                    var bodyNode = dom.Descendants().FirstOrDefault(n => n.Tag == "body");
                    bgFixed = check(bodyNode) || check(htmlNode);
                    
                    if (onFixedBackground != null)
                    {
                        var disps = UiThreadHelper.TryGetDispatcher();
                        if (disps != null)
                        {
                            await UiThreadHelper.RunAsyncAwaitable(disps, null, () =>
                            {
                                try { onFixedBackground((IBrush)(bgFixed ? fixedBg : null)); } catch { }
                                return System.Threading.Tasks.Task.CompletedTask;
                            });
                        }
                    }
                }
            }
            catch { }

            if (imageLoader == null)
                imageLoader = _ => Task.FromResult<Stream>(null);

            var renderer = new DomBasicRenderer
            {
                ComputedStyles = computed,
                ImageLoader = imageLoader,
                Js = js
            };

            Control element = null;
            var disp = UiThreadHelper.TryGetDispatcher(); // <-- outer 'disp'
            try
            {
                if (disp != null)
                {
                    var tcs = new TaskCompletionSource<Control>();
                    await UiThreadHelper.RunAsyncAwaitable(disp, null, async () =>
                    {
                        try
                        {
                            // Use DomBasicRenderer (Robust Pipeline)
                            try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Calling BuildAsync\r\n"); } catch { }
                            var visualRoot = await renderer.BuildAsync(dom, baseUri, onNavigate, js);
                            tcs.TrySetResult(visualRoot);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    });
                    element = await tcs.Task.ConfigureAwait(true);
                }
                else
                {
                    // Fallback if no dispatcher (shouldn't happen in UI app)
                    // We cannot run Painter here because it requires UI thread.
                    throw new InvalidOperationException("Cannot render visual tree without UI thread access.");
                }
            }
            catch (Exception threadEx)
            {
                // ... error handling ...
                var disp2 = UiThreadHelper.TryGetDispatcher(); // <-- renamed from 'disp' to 'disp2'
                if (disp2 != null)
                {
                    await UiThreadHelper.RunAsyncAwaitable(disp2, null, () =>
                    {
                        var sp = new StackPanel { Margin = new Thickness(12, 12, 12, 12) };
                        sp.Children.Add(new TextBlock { Text = "Render thread error", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Colors.Black) });
                        string detail = threadEx.Message;
                        try { detail += "\n" + (threadEx.StackTrace ?? "(no stack)"); } catch { }
                        sp.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Black), Margin = new Thickness(0,6,0,0) });
                        element = new Border { Background = new SolidColorBrush(Colors.White), Child = sp };
                        return Task.CompletedTask;
                    });
                }
            }

            // Wrap in background/border - MUST be on UI thread
            if (element != null)
            {
                // Re-dispatch if we somehow ended up on a background thread (e.g. if disp was null initially but we need UI thread now? Unlikely if disp was null).
                // If disp != null, we want to ensure this runs on UI thread.
                var disp3 = UiThreadHelper.TryGetDispatcher();
                if (disp3 != null && !UiThreadHelper.HasThreadAccess(disp3))
                {
                     var tcsWrap = new TaskCompletionSource<Control>();
                     await UiThreadHelper.RunAsyncAwaitable(disp3, null, () =>
                     {
                         try
                         {
                             if (computed != null)
                             {
                                var bodyNode = dom.Descendants().FirstOrDefault(n => n.Tag == "body");
                                var htmlNode = dom.Descendants().FirstOrDefault(n => n.Tag == "html");
                                CssComputed cb = null;
                                if (bodyNode != null && computed.TryGetValue(bodyNode, out cb) && cb != null && cb.Background != null)
                                {
                                    element = new Border { Background = cb.Background, Child = element };
                                    TryApplyContrastForeground(element, cb.Background);
                                }
                                else if (htmlNode != null && computed.TryGetValue(htmlNode, out cb) && cb != null && cb.Background != null)
                                {
                                    element = new Border { Background = cb.Background, Child = element };
                                    TryApplyContrastForeground(element, cb.Background);
                                }
                                else
                                {
                                    var bg = new SolidColorBrush(Colors.White);
                                    element = new Border { Background = bg, Child = element };
                                    TryApplyContrastForeground(element, bg);
                                }
                             }
                             tcsWrap.TrySetResult(element);
                         }
                         catch (Exception ex) { tcsWrap.TrySetException(ex); }
                         return System.Threading.Tasks.Task.CompletedTask;
                     });
                     element = await tcsWrap.Task;
                }
                else
                {
                    // Already on UI thread (or no dispatcher available), run directly
                    try
                    {
                        if (computed != null)
                        {
                            var bodyNode = dom.Descendants().FirstOrDefault(n => n.Tag == "body");
                            var htmlNode = dom.Descendants().FirstOrDefault(n => n.Tag == "html");
                            CssComputed cb = null;
                            if (bodyNode != null && computed.TryGetValue(bodyNode, out cb) && cb != null && cb.Background != null)
                            {
                                    element = new Border { Background = cb.Background, Child = element };
                                    TryApplyContrastForeground(element, cb.Background);
                            }
                            else if (htmlNode != null && computed.TryGetValue(htmlNode, out cb) && cb != null && cb.Background != null)
                            {
                                    element = new Border { Background = cb.Background, Child = element };
                                    TryApplyContrastForeground(element, cb.Background);
                            }
                            else
                            {
                                var bg = new SolidColorBrush(Colors.White);
                                element = new Border { Background = bg, Child = element };
                                TryApplyContrastForeground(element, bg);
                            }
                        }
                    }
                    catch { }
                }
            }

            if (element == null || IsEffectivelyEmpty(element))
            {
                var fallback = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12, 12, 12, 12) };
                var fg = new SolidColorBrush(Colors.White);
                string title = null;
                try { var tnode = dom.Descendants().FirstOrDefault(n => n.Tag == "title"); if (tnode != null) title = tnode.Text; } catch { }
                if (string.IsNullOrWhiteSpace(title)) title = baseUri != null ? baseUri.Host : "This page";
                fallback.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6), Foreground = fg });
                fallback.Children.Add(new TextBlock { Text = baseUri != null ? baseUri.AbsoluteUri : string.Empty, TextWrapping = TextWrapping.Wrap, Foreground = fg });
                element = new Border { Background = new SolidColorBrush(Colors.Black), Child = fallback };
            }

            // GPU Acceleration: Apply BitmapCache if enabled
            if (EnableGpuAcceleration && element != null)
            {
                try
                {
                    // Avalonia doesn't have BitmapCache - ignore
                }
                catch { }
            }

            if (includeDiagnosticsBanner)
            {
                try
                {
                    var wrap = new StackPanel { Orientation = Orientation.Vertical };
                    var banner = new Border
                    {
                        Background = new SolidColorBrush(Colors.Black),
                        Child = null
                    };
                    wrap.Children.Add(banner);
                    if (element != null) wrap.Children.Add(element);
                    element = wrap;
                }
                catch { }
            }

            return element;
        }

        public bool EnableGpuAcceleration { get; set; } = false;

        private async Task<Control> RefreshAsyncInternal(bool includeDiagnosticsBanner)
        {
            // Ensure we are on the UI thread. If not, marshal the call.
            var uiDisp = _uiDispatcher ?? UiThreadHelper.TryGetDispatcher();
            if (uiDisp != null && !UiThreadHelper.HasThreadAccess(uiDisp))
            {
                var tcs = new TaskCompletionSource<Control>();
                await UiThreadHelper.RunAsyncAwaitable(uiDisp, null, async () =>
                {
                    try
                    {
                        var result = await RefreshAsyncInternal(includeDiagnosticsBanner);
                        tcs.SetResult(result);
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

            var fetchCss = _activeFetchCss ?? (async _ => string.Empty);
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

        private async Task DispatchRepaintAsync(Control element)
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
                    await UiThreadHelper.RunAsyncAwaitable(disp, null, async () =>
                    {
                        try { handler(element); }
                        catch { }
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
            try { System.IO.File.AppendAllText("debug_log.txt", "[CustomHtmlEngine] ScheduleRepaintFromJs called\r\n"); } catch { }
            if (!EnableJavaScript) return;
            if (_activeDom == null) return;
            var disp = UiThreadHelper.TryGetDispatcher();
            if (System.Threading.Interlocked.Exchange(ref _repaintScheduled, 1) == 1)
            {
                try { System.IO.File.AppendAllText("debug_log.txt", "[CustomHtmlEngine] Repaint already scheduled\r\n"); } catch { }
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
                        try { System.IO.File.AppendAllText("debug_log.txt", "[CustomHtmlEngine] Calling RefreshAsyncInternal\r\n"); } catch { }
                        var element = await RefreshAsyncInternal(includeDiagnosticsBanner: false).ConfigureAwait(false);
                        try { System.IO.File.AppendAllText("debug_log.txt", $"[CustomHtmlEngine] RefreshAsyncInternal returned element: {element?.GetType().Name ?? "null"}\r\n"); } catch { }
                        
                        await DispatchRepaintAsync(element).ConfigureAwait(false);
                        try { System.IO.File.AppendAllText("debug_log.txt", "[CustomHtmlEngine] DispatchRepaintAsync completed\r\n"); } catch { }
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

        public async Task<Control> RefreshAsync(bool includeDiagnosticsBanner = false)
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
        public async Task<Control> RenderAsync(
            string html,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            Func<Uri, Task<Stream>> imageLoader,
            Action<Uri> onNavigate,
            double? viewportWidth = null,
            Action<IBrush> onFixedBackground = null,
            bool? forceJavascript = null,
            bool disableAutoFallback = false)
        {
            // Ensure we are on the UI thread. If not, marshal the call.
            var uiDisp = _uiDispatcher ?? UiThreadHelper.TryGetDispatcher();
            if (uiDisp != null && !UiThreadHelper.HasThreadAccess(uiDisp))
            {
                var tcs = new TaskCompletionSource<Control>();
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

            try
            {
                try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] Start. HTML Length: {html?.Length ?? 0}\r\n"); } catch { }
                Console.WriteLine($"[RenderAsync] Start. HTML Length: {html?.Length ?? 0}");
                
                // PROTECTION: Validate HTML size to prevent crashes
                const int MaxHtmlSize = 2 * 1024 * 1024; // 2MB limit
                if (!string.IsNullOrEmpty(html) && html.Length > MaxHtmlSize)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] HTML too large: {html.Length} bytes, truncating to {MaxHtmlSize}\r\n"); } catch { }
                    html = html.Substring(0, MaxHtmlSize);
                }
                
                try { System.IO.File.AppendAllText("debug_log.txt", $"[RenderAsync] HTML size: {html?.Length ?? 0} chars\r\n"); } catch { }
                
                // 1) Parse DOM (background)
                var parser = new HtmlLiteParser(html ?? string.Empty);
                LiteElement dom = null;
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
                        var parent = template.Parent;
                        if (parent != null)
                        {
                            // The template content becomes the shadow root
                            parent.ShadowRoot = template.Children; 
                            // Detach the template itself from the main DOM
                            parent.Children.Remove(template);
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

                var cssFetcher = fetchExternalCssAsync ?? (async _ => string.Empty);

                JavaScriptEngine js = null;
                if (allowJs)
                {
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
                                    var _ = disp?.InvokeAsync(() => { try { action(); } catch { } });
                                }
                                else action();
                            }
                            catch { action(); }
                        }))
                    {
                        Sandbox = allowJs ? SandboxPolicy.AllowAll : SandboxPolicy.NoScripts,
                        AllowExternalScripts = allowJs,
                        SubresourceAllowed = (u, kind) => allowJs,
                        ExecuteInlineScriptsOnInnerHTML = allowJs
                    };
                }

                if (js != null)
                {
                    js.CookieBridge = scope => _jsCookieJar;
    #if USE_NILJS
                    js.UseMiniPrattEngine = false;
    #else
                    js.UseMiniPrattEngine = true;
    #endif
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

                CaptureActiveContext(dom, baseUri, cssFetcher, imageLoader, onNavigate, viewportWidth, onFixedBackground, js);

                if (allowJs)
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[RenderAsync] Running Scripts\r\n"); } catch { }
                    Console.WriteLine("[RenderAsync] Running Scripts");
                    
                    // STEP 1: Inject JS detection helper (runs first to ensure sites detect JS is enabled)
                    // This script flips no-js -> js class and hides noscript fallback elements
                    try
                    {
                        const string detectionHelper = @"
(function() {
    try {
        // Remove no-js class, add js class on html element
        var html = document.documentElement;
        if (html && html.className) {
            html.className = html.className.replace(/\bno-js\b/gi, '').trim();
            if (html.className.indexOf('js') === -1) {
                html.className = (html.className + ' js').trim();
            }
        } else if (html) {
            html.className = 'js';
        }
        // Also check body element
        var body = document.body;
        if (body && body.className) {
            body.className = body.className.replace(/\bno-js\b/gi, '').trim();
        }
        // Hide common noscript fallback indicators by class
        var nojs = document.querySelectorAll('.no-js, .noscript, [data-nojs], .js-disabled');
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
                        var scriptTask = Task.Run(() => { js.SetDom(dom, baseUri); });
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
                Control element = null;
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
                    try { isEmpty = element == null || IsEffectivelyEmpty(element); }
                    catch { isEmpty = element == null; }

                    if (isEmpty)
                    {
                        try { System.Diagnostics.Debug.WriteLine("[RENDER] JS path empty, retrying without scripts"); } catch { }
                        await RaiseLoadingChangedAsync(false);
                        return await RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, onFixedBackground, forceJavascript: false, disableAutoFallback: true).ConfigureAwait(false);
                    }
                }

                try { System.Diagnostics.Debug.WriteLine("[RENDER] Final element null=" + (element == null) + " empty=" + (element != null && IsEffectivelyEmpty(element))); } catch { }
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
            Action<IBrush> onFixedBackground = null)
        {
            try { var _ = RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, onFixedBackground); }
            catch { }
        }

        /// <summary>Expose the current active Lite DOM (last parsed).</summary>
        public LiteElement GetActiveDom()
        {
            return _activeDom;
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

        private static bool IsEffectivelyEmpty(Control fe)
        {
            try
            {
                if (fe == null) return true;
                // Deep inspect: if only canvases with no children or no text/images, treat as empty
                return !HasMeaningfulContent(fe);
            }
            catch { return false; }
        }

        private static string NormalizeLoadingKey(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                var trimmed = value.Trim();
                if (trimmed.Length == 0) return string.Empty;
                var filtered = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
                return filtered.ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

        private static bool HasMeaningfulContent(object node)
        {
            try
            {
                if (node == null) return false;
                var progressIndicatorType = node.GetType().Name;
                if (progressIndicatorType.IndexOf("Progress", StringComparison.OrdinalIgnoreCase) >= 0 && !progressIndicatorType.Equals("Button", StringComparison.OrdinalIgnoreCase))
                    return false;
                var tb = node as TextBlock;
                if (tb != null)
                {
                    var candidate = tb.Text ?? string.Empty;
                    if (tb.Inlines != null && tb.Inlines.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var inline in tb.Inlines.OfType<Run>())
                        {
                            if (!string.IsNullOrWhiteSpace(inline.Text)) sb.Append(inline.Text);
                        }
                        if (sb.Length > 0) candidate = sb.ToString();
                    }
                    if (string.IsNullOrWhiteSpace(candidate)) return false;
                    return !_loadingTokens.Contains(NormalizeLoadingKey(candidate));
                }
                if (node is Image || node is Button) return true;
                var border = node as Border; if (border != null) return HasMeaningfulContent(border.Child);
                var sv = node as ScrollViewer; if (sv != null) return HasMeaningfulContent(sv.Content as object);
                var panel = node as Panel;
                if (panel != null)
                {
                    bool any = false;
                    for (int i = 0; i < panel.Children.Count; i++)
                    {
                        var ch = panel.Children[i];
                        var c = ch as Canvas; if (c != null && c.Children != null && c.Children.Count == 0) continue;
                        if (HasMeaningfulContent(ch)) { any = true; break; }
                    }
                    return any;
                }
                int count = VisualChildCount(node);
                if (count == 0) return false;
                for (int i = 0; i < count; i++)
                {
                    if (HasMeaningfulContent(VisualGetChild(node, i))) return true;
                }
                return false;
            }
            catch { return true; }
        }

        private static void TryApplyContrastForeground(Control container, IBrush background)
        {
            try
            {
                var scb = background as SolidColorBrush;
                if (scb == null) return;
                var c = scb.Color;
                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                var desired = (lum < 0.5) ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black);
                ApplyReadableForeground(container, desired, scb);
            }
            catch { }
        }

        private static bool NeedsOverride(IBrush fgIBrush, SolidColorBrush bg)
        {
            var fg = fgIBrush as SolidColorBrush;
            if (fg == null) return true;
            double lumFg = (0.2126 * fg.Color.R + 0.7152 * fg.Color.G + 0.0722 * fg.Color.B) / 255.0;
            double lumBg = (0.2126 * bg.Color.R + 0.7152 * bg.Color.G + 0.0722 * bg.Color.B) / 255.0;
            double L1 = (lumFg > lumBg ? lumFg : lumBg) + 0.05;
            double L2 = (lumFg > lumBg ? lumBg : lumFg) + 0.05;
            double contrast = L1 / L2;
            return contrast < 2.0;
        }

        private static void ApplyReadableForeground(object node, SolidColorBrush desired, SolidColorBrush bg)
        {
            try
            {
                if (node == null || desired == null || bg == null) return;
                var tb = node as TextBlock;
                if (tb != null)
                {
                    if (tb.Foreground == null || NeedsOverride(tb.Foreground, bg))
                        tb.Foreground = desired;
                    return;
                }
                var border = node as Border;
                if (border != null)
                {
                    if (border.Child != null) ApplyReadableForeground(border.Child, desired, bg);
                    return;
                }
                var panel = node as Panel;
                if (panel != null)
                {
                    if (panel.Children != null)
                        foreach (var ch in panel.Children) ApplyReadableForeground(ch as object, desired, bg);
                    return;
                }
                var contentCtrl = node as ContentControl;
                if (contentCtrl != null)
                {
                    var d = contentCtrl.Content as object;
                    if (d != null) ApplyReadableForeground(d, desired, bg);
                    return;
                }
                int count = VisualChildCount(node);
                for (int i = 0; i < count; i++)
                    ApplyReadableForeground(VisualGetChild(node, i), desired, bg);
            }
            catch { }
        }

        private static void ApplyDefaultForeground(object node, SolidColorBrush desired)
        {
            try
            {
                if (node == null || desired == null) return;
                var tb = node as TextBlock; if (tb != null) { if (tb.Foreground == null) tb.Foreground = desired; }
                // For other text types (e.g., Windows RichTextBlock), attempt to set 'Foreground' via reflection when available
                try
                {
                    var fgProp = node?.GetType().GetProperty("Foreground");
                    if (fgProp != null && fgProp.CanWrite)
                    {
                        var cur = fgProp.GetValue(node) as IBrush;
                        if (cur == null) fgProp.SetValue(node, desired);
                    }
                }
                catch { }
                var border = node as Border; if (border != null) { if (border.Child != null) ApplyDefaultForeground(border.Child, desired); return; }
                var panel = node as Panel; if (panel != null && panel.Children != null)
                {
                    for (int i = 0; i < panel.Children.Count; i++) ApplyDefaultForeground(panel.Children[i], desired);
                    return;
                }
                var contentCtrl = node as ContentControl;
                if (contentCtrl != null)
                {
                    var d = contentCtrl.Content as object;
                    if (d != null) ApplyDefaultForeground(d, desired);
                    return;
                }
                // Fallback: walk visual children when possible
                int count = VisualChildCount(node);
                for (int i = 0; i < count; i++) ApplyDefaultForeground(VisualGetChild(node, i), desired);
            }
            catch { }
        }
        public async Task<string> MakeImageAsync()
        {
            if (_lastRenderedControl == null) return null;
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    // Ensure layout is updated
                    if (_lastRenderedControl.IsMeasureValid == false || _lastRenderedControl.IsArrangeValid == false)
                    {
                        _lastRenderedControl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        _lastRenderedControl.Arrange(new Rect(_lastRenderedControl.DesiredSize));
                    }

                    var rtb = new RenderTargetBitmap(new PixelSize((int)_lastRenderedControl.Bounds.Width, (int)_lastRenderedControl.Bounds.Height), new Vector(96, 96));
                    rtb.Render(_lastRenderedControl);

                    using (var stream = new MemoryStream())
                    {
                        rtb.Save(stream);
                        return Convert.ToBase64String(stream.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MakeImageAsync] Error: {ex}");
                    return null;
                }
            });
        }
    }
}
