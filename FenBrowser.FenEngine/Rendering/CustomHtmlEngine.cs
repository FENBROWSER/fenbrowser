using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Security;
using FenBrowser.Core.Storage;
using FenBrowser.FenEngine.Security; // Added
using FenBrowser.FenEngine.Scripting; // For IJsHost
using FenBrowser.FenEngine.Core; // Corrected namespace
using FenBrowser.FenEngine.Core.Interfaces; // Added for IExecutionContext
using FenBrowser.FenEngine.Layout; // Added for LayoutResult
using static FenBrowser.FenEngine.Rendering.CssLoader;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Core.EventLoop; // Added for EventLoopCoordinator
using FenBrowser.Core.Engine; // Added for EnginePhase
using SkiaSharp;


namespace FenBrowser.FenEngine.Rendering
{
    public sealed class RenderTelemetrySnapshot
    {
        public long TokenizingMs { get; init; }
        public long ParsingMs { get; init; }
        public long TokenizingAndParsingMs { get; init; }
        public int ParseTokenCount { get; init; }
        public int TokenizingCheckpointCount { get; init; }
        public int ParsingCheckpointCount { get; init; }
        public int ParsingDocumentCheckpointCount { get; init; }
        public int DocumentReadyTokenCount { get; init; }
        public int ParseIncrementalRepaintCount { get; init; }
        public long StreamingPreparseMs { get; init; }
        public int StreamingPreparseCheckpointCount { get; init; }
        public int StreamingPreparseRepaintCount { get; init; }
        public bool InterleavedParseUsed { get; init; }
        public int InterleavedTokenBatchSize { get; init; }
        public int InterleavedBatchCount { get; init; }
        public bool InterleavedFallbackUsed { get; init; }
        public long CssAndStyleMs { get; init; }
        public long InitialVisualTreeMs { get; init; }
        public long ScriptExecutionMs { get; init; }
        public long PostScriptVisualTreeMs { get; init; }
        public long TotalRenderMs { get; init; }
        public bool JavaScriptExecuted { get; init; }
    }

    internal sealed class DomParseResult
    {
        public Node Dom { get; set; }
        public long TokenizingMs { get; set; }
        public long ParsingMs { get; set; }
        public int TokenCount { get; set; }
        public int TokenizingCheckpointCount { get; set; }
        public int ParsingCheckpointCount { get; set; }
        public int ParsingDocumentCheckpointCount { get; set; }
        public int DocumentReadyTokenCount { get; set; }
        public int IncrementalRepaintCount { get; set; }
        public long StreamingPreparseMs { get; set; }
        public int StreamingPreparseCheckpointCount { get; set; }
        public int StreamingPreparseRepaintCount { get; set; }
        public bool InterleavedParseUsed { get; set; }
        public int InterleavedTokenBatchSize { get; set; }
        public int InterleavedBatchCount { get; set; }
        public bool InterleavedFallbackUsed { get; set; }
    }

    internal sealed class ParseCheckpointState
    {
        public int ParsingDocumentCheckpointCount { get; set; }
        public int ParsingCheckpointOrdinal { get; set; }
        public int IncrementalRepaintCount { get; set; }
    }

    /// <summary>
    /// Clean, dependency-free wrapper suitable for WP8.1 without WebView.
    /// </summary>
#nullable enable
    public sealed class CustomHtmlEngine : IDisposable
    {
        private const int IncrementalParseRepaintMaxCount = 8;
        private const int IncrementalParseRepaintCheckpointStride = 2;
        private const int StreamingPreparseMinHtmlLength = 32768;
        private const int StreamingPreparseMaxHtmlLength = 131072;
        private const int StreamingPreparseRepaintMaxCount = 4;
        private const int StreamingPreparseRepaintCheckpointStride = 3;
        private const int InterleavedPrimaryParseMinHtmlLength = 8192;
        private const int ImagePrewarmAwaitBudgetMs = 1500;
        private const int ImagePrewarmEagerCandidateLimit = 6;
        private const int ImagePrewarmQueueBudget = 32;
        private const int ImagePrewarmMaxConcurrency = 6;
        private readonly object _renderStateLock = new();

        public Func<Uri, Task<string>> ScriptFetcher { get; set; }
        public Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> FetchHandler { get; set; }

        private CspPolicy _activePolicy;
        /// <summary>Active Content Security Policy for this page. When set, subresource loads are checked against it.</summary>
        public CspPolicy ActivePolicy 
        { 
            get => _activePolicy; 
            set 
            {
                EngineLogCompat.Debug($"[ActivePolicy] SET to {(value == null ? "NULL" : "Instance hash=" + value.GetHashCode())} Stack={Environment.StackTrace}", LogCategory.Rendering);
                _activePolicy = value; 
            }
        }

        // EXPOSED STYLES FOR SKIA RENDERER
        public Dictionary<Node, CssComputed> LastComputedStyles { get; private set; }
        public List<CssLoader.CssSource> LastCssSources { get; private set; }

        public LayoutResult LastLayout { get; private set; }
        public RenderTelemetrySnapshot LastRenderTelemetry { get; private set; }
        public IExecutionContext Context => _activeJs?.GlobalContext;

        private void SetActiveDom(Node dom, bool markSnapshotUnstable = false)
        {
            lock (_renderStateLock)
            {
                _activeDom = dom;
                if (markSnapshotUnstable)
                {
                    _hasStableStyles = false;
                }

                _renderSnapshotVersion++;
            }
        }

        private void SetComputedStyles(Dictionary<Node, CssComputed> styles)
        {
            lock (_renderStateLock)
            {
                LastComputedStyles = styles;
                _hasStableStyles = styles != null;
                _renderSnapshotVersion++;
            }
        }

        private void MarkStyleSnapshotUnstable()
        {
            lock (_renderStateLock)
            {
                _hasStableStyles = false;
                _renderSnapshotVersion++;
            }
        }

        private bool HasStableComputedStyleSnapshot()
        {
            lock (_renderStateLock)
            {
                return _hasStableStyles &&
                       LastComputedStyles != null &&
                       LastComputedStyles.Count > 0;
            }
        }

        private void UpdateRenderState(Node dom, Dictionary<Node, CssComputed> styles)
        {
            lock (_renderStateLock)
            {
                if (dom != null)
                {
                    _activeDom = dom;
                }

                LastComputedStyles = styles;
                _hasStableStyles = styles != null;
                _renderSnapshotVersion++;
            }
        }

        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[CustomHtmlEngine] Detached async operation failed: {ex.Message}", LogCategory.Rendering);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        public RenderContext? BuildRenderContext()
        {
            return _cachedRenderer?.CreateRenderContext();
        }

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
        public event Func<string, bool> ConfirmTriggered;
        public event Func<string, string, string> PromptTriggered;
        public event Action<string> ConsoleMessage; // New event for console logs
        public event Action<SKRect?> HighlightRectChanged;
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested; // Permission API event
        public bool EnableJavaScript { get; set; } = true;
        public bool EnableIncrementalParseRepaint { get; set; } = true;
        public bool EnableStreamingParsePrepass { get; set; } = false;
        public bool EnableInterleavedPrimaryParse { get; set; } = true;


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
                EngineLogCompat.Error($"[CustomHtmlEngine] HandlePointerEvent error: {ex.Message}", LogCategory.Rendering);
            }
        }

        public void ClearAllCookies()
        {
            CookieJar.ClearAll();
        }

        public object Evaluate(string script)
        {
            EngineLogCompat.Debug($"[CustomHtmlEngine.Evaluate] _activeJs is null: {_activeJs == null}", LogCategory.JavaScript);
            if (_activeJs != null) 
            {
                EngineLogCompat.Debug($"[CustomHtmlEngine.Evaluate] Calling _activeJs.Evaluate with script length: {script?.Length}", LogCategory.JavaScript);
                return _activeJs.Evaluate(script);
            }
            EngineLogCompat.Error("[CustomHtmlEngine.Evaluate] _activeJs is NULL - script not executed!", LogCategory.JavaScript);
            return null;
        }

        public Node ActiveDom => _activeDom;
        public JavaScriptEngine JsEngine => _activeJs;
        private Node _activeDom;
        private long _renderSnapshotVersion;
        private bool _hasStableStyles;
        private string _lastRawHtml;
        private object _lastRenderedControl;
        private Uri _activeBaseUri;
        private Func<Uri, Task<string>> _activeFetchCss;
        private Func<Uri, Task<Stream>> _activeImageLoader;
        private Action<Uri> _activeOnNavigate;
        private double? _activeViewportWidth;
        private double? _activeViewportHeight;
        private Action<object> _activeFixedBackground;
        private JavaScriptEngine _activeJs;
        public BrowserCookieJar CookieJar { get; set; } = new BrowserCookieJar();
        private readonly System.Threading.SemaphoreSlim _repaintGate = new System.Threading.SemaphoreSlim(1, 1);
        private int _repaintScheduled;
        private readonly object _uiDispatcher;
        
        // Cache view/renderer to avoid full recreation
        // private SkiaBrowserView _cachedView;
        private SkiaDomRenderer _cachedRenderer;
        
        public CustomHtmlEngine()
        {
            _uiDispatcher = UiThreadHelper.TryGetDispatcher();
            EventLoopCoordinator.Instance.SetRenderCallback(ProcessQueuedRenderUpdate);
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

        // Some pages expose a usable fallback DOM but trap limited engines in long-running
        // bootstrap script payloads. Prefer the fallback path when the raw document already
        // advertises a noscript/meta-refresh recovery flow.
        private static bool IsGoogleSearchAccessTroubleDocument(string html, Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(html) || !IsGoogleHost(baseUri))
            {
                return false;
            }

            var hasEnableJsRecovery =
                html.IndexOf("/httpservice/retry/enablejs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("if you are not redirected within a few seconds", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasEnableJsRecovery)
            {
                return false;
            }

            var hasTroubleBanner =
                html.IndexOf("id=\"yvlrue\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("id='yvlrue'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("trouble accessing google search", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasTroubleBanner;
        }

        private static string BuildGoogleAccessTroubleFallbackHtml(string html, Uri baseUri)
        {
            if (!IsGoogleSearchAccessTroubleDocument(html, baseUri))
            {
                return html ?? string.Empty;
            }

            var bannerInner = "If you're having trouble accessing Google Search, please retry your search.";
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    html,
                    "<div\\b[^>]*\\bid\\s*=\\s*['\\\"]yvlrue['\\\"][^>]*>(.*?)</div>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                if (match.Success && match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    bannerInner = match.Groups[1].Value;
                }
            }
            catch
            {
                // Keep default banner text on regex failure.
            }

            bannerInner = bannerInner
                .Replace("href=\"/", "href=\"https://www.google.com/", StringComparison.OrdinalIgnoreCase)
                .Replace("href='/", "href='https://www.google.com/", StringComparison.OrdinalIgnoreCase);

            return
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Google Search</title></head>" +
                "<body style=\"font-family:Arial,sans-serif;padding:24px;line-height:1.45;color:#202124;background:#fff;\">" +
                $"<div id=\"yvlrue\">{bannerInner}</div>" +
                "</body></html>";
        }

        private static bool ShouldPreferFallbackDom(string html, Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            if (IsGoogleHost(baseUri))
            {
                return false;
            }

            if (html.IndexOf("<noscript", StringComparison.OrdinalIgnoreCase) < 0 ||
                html.IndexOf("http-equiv=\"refresh\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(
                html,
                "<script\\b[^>]*>(.*?)</script>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var totalScriptChars = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                totalScriptChars += match.Groups[1].Length;
                if (totalScriptChars >= 20000)
                {
                    return true;
                }
            }

            return false;
        }

        private static string StripScriptsForFallbackDom(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html ?? string.Empty;
            }

            return System.Text.RegularExpressions.Regex.Replace(
                html,
                "<script\\b[^>]*>.*?</script>",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        private static string RemoveInlineDisplayNone(string inlineStyle)
        {
            if (string.IsNullOrWhiteSpace(inlineStyle))
            {
                return string.Empty;
            }

            var updated = System.Text.RegularExpressions.Regex.Replace(
                inlineStyle,
                @"(?:^|;)\s*display\s*:\s*none\s*;?",
                ";",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            updated = System.Text.RegularExpressions.Regex.Replace(
                updated,
                @";{2,}",
                ";",
                System.Text.RegularExpressions.RegexOptions.None).Trim().Trim(';').Trim();

            return updated;
        }

        private static bool LooksLikeVisibleFallbackCandidate(Element element, Uri baseUri)
        {
            if (element == null)
            {
                return false;
            }

            if (!element.Descendants().OfType<Element>().Any(child =>
                string.Equals(child.TagName, "a", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var text = WebUtility.HtmlDecode(element.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text) || text.Length < 24)
            {
                return false;
            }

            if (IsGoogleHost(baseUri))
            {
                return false;
            }

            return text.IndexOf("trouble accessing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("not redirected within a few seconds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (text.IndexOf("click here", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 text.IndexOf("feedback", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int PromoteHiddenFallbackContent(Node domRoot, Uri baseUri)
        {
            if (domRoot == null)
            {
                return 0;
            }

            var promoted = 0;
            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                var inlineStyle = element.GetAttribute("style");
                if (string.IsNullOrWhiteSpace(inlineStyle) ||
                    inlineStyle.IndexOf("display", StringComparison.OrdinalIgnoreCase) < 0 ||
                    inlineStyle.IndexOf("none", StringComparison.OrdinalIgnoreCase) < 0 ||
                    !LooksLikeVisibleFallbackCandidate(element, baseUri))
                {
                    continue;
                }

                var updatedStyle = RemoveInlineDisplayNone(inlineStyle);
                if (string.IsNullOrWhiteSpace(updatedStyle))
                {
                    element.RemoveAttribute("style");
                }
                else
                {
                    element.SetAttribute("style", updatedStyle);
                }

                promoted++;
            }

            return promoted;
        }

        private static bool IsGoogleHost(Uri baseUri)
        {
            var host = baseUri?.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            host = host.ToLowerInvariant();
            return host == "google.com" ||
                   host.StartsWith("google.", StringComparison.Ordinal) ||
                   host.EndsWith(".google.com", StringComparison.Ordinal) ||
                   host.Contains(".google.", StringComparison.Ordinal);
        }

        private static int RemoveGoogleAccessTroubleBanners(Node domRoot, Uri baseUri)
        {
            if (domRoot == null || !IsGoogleHost(baseUri))
            {
                return 0;
            }

            var toRemove = new List<Element>();
            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                var text = WebUtility.HtmlDecode(element.Text ?? string.Empty);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var hasTroubleMessage =
                    text.IndexOf("trouble accessing google search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("having trouble accessing google search", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasTroubleMessage)
                {
                    continue;
                }

                var hasFallbackActionText =
                    text.IndexOf("click here", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("send feedback", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasFallbackActionText)
                {
                    continue;
                }

                toRemove.Add(element);
            }

            foreach (var element in toRemove)
            {
                element.Remove();
            }

            return toRemove.Count;
        }

        private static int RemoveGoogleTroubleBannerArtifacts(Node domRoot, Uri baseUri)
        {
            if (domRoot == null || !IsGoogleHost(baseUri))
            {
                return 0;
            }

            var toRemove = new List<Element>();
            var promoted = 0;
            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                var tag = element.TagName?.ToLowerInvariant();
                if (tag == "div" && string.Equals(element.GetAttribute("id"), "yvlrue", StringComparison.OrdinalIgnoreCase))
                {
                    var inlineStyle = element.GetAttribute("style");
                    if (!string.IsNullOrWhiteSpace(inlineStyle) &&
                        inlineStyle.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        inlineStyle.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var updatedStyle = RemoveInlineDisplayNone(inlineStyle);
                        if (string.IsNullOrWhiteSpace(updatedStyle))
                        {
                            element.RemoveAttribute("style");
                        }
                        else
                        {
                            element.SetAttribute("style", updatedStyle);
                        }
                    }

                    promoted++;
                    continue;
                }

                if (tag != "script")
                {
                    continue;
                }

                var scriptText = element.Text ?? string.Empty;
                if (scriptText.IndexOf("sg_trbl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    scriptText.IndexOf("cssId='yvlrue'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    scriptText.IndexOf("cssId=\"yvlrue\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    toRemove.Add(element);
                }
            }

            foreach (var element in toRemove)
            {
                element.Remove();
            }

            return toRemove.Count + promoted;
        }

        private static bool IsGoogleSearchChallengeDocument(Node domRoot, Uri baseUri)
        {
            if (domRoot == null || !IsGoogleHost(baseUri))
            {
                return false;
            }

            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                if (string.Equals(element.GetAttribute("id"), "yvlrue", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.Equals(element.TagName, "a", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var href = element.GetAttribute("href") ?? string.Empty;
                if (href.IndexOf("emsg=SG_REL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static int ForceGoogleChallengeBannerVisible(Node domRoot, Uri baseUri, bool removeUnhideScript)
        {
            if (!IsGoogleSearchChallengeDocument(domRoot, baseUri))
            {
                return 0;
            }

            var changed = 0;
            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                if (!string.Equals(element.GetAttribute("id"), "yvlrue", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var inlineStyle = element.GetAttribute("style") ?? string.Empty;
                var updatedStyle = inlineStyle;

                if (!string.IsNullOrWhiteSpace(inlineStyle))
                {
                    updatedStyle = System.Text.RegularExpressions.Regex.Replace(
                        updatedStyle,
                        @"(?:^|;)\s*display\s*:\s*none(?:\s*!\s*important)?\s*;?",
                        ";",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    updatedStyle = System.Text.RegularExpressions.Regex.Replace(
                        updatedStyle,
                        @"(?:^|;)\s*visibility\s*:\s*hidden(?:\s*!\s*important)?\s*;?",
                        ";",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    updatedStyle = System.Text.RegularExpressions.Regex.Replace(
                        updatedStyle,
                        @"(?:^|;)\s*opacity\s*:\s*0(?:\.0+)?(?:\s*!\s*important)?\s*;?",
                        ";",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                updatedStyle = updatedStyle?.Trim() ?? string.Empty;
                if (!updatedStyle.EndsWith(";", StringComparison.Ordinal) && updatedStyle.Length > 0)
                {
                    updatedStyle += ";";
                }

                updatedStyle += "display:block;visibility:visible;opacity:1;";
                updatedStyle = System.Text.RegularExpressions.Regex.Replace(updatedStyle, @";{2,}", ";");
                updatedStyle = updatedStyle.Trim().Trim(';');

                element.SetAttribute("style", updatedStyle);

                if (string.Equals(element.GetAttribute("hidden"), "hidden", StringComparison.OrdinalIgnoreCase) ||
                    element.HasAttribute("hidden"))
                {
                    element.RemoveAttribute("hidden");
                }

                if (string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    element.SetAttribute("aria-hidden", "false");
                }

                changed++;
            }

            if (removeUnhideScript)
            {
                changed += RemoveGoogleTroubleBannerArtifacts(domRoot, baseUri);
            }

            return changed;
        }

        private static int RemoveEncodedNoscriptBootstrapFallbacks(IEnumerable<Element> noscriptElements)
        {
            if (noscriptElements == null)
            {
                return 0;
            }

            var removed = 0;
            foreach (var noscript in noscriptElements)
            {
                var text = noscript?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var looksEncodedBootstrap =
                    text.IndexOf("<meta", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    text.IndexOf("http-equiv=\"refresh\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    text.IndexOf("<style>", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!looksEncodedBootstrap)
                {
                    continue;
                }

                noscript.Remove();
                removed++;
            }

            return removed;
        }

        // Some pages expose a usable fallback DOM but trap limited engines in long-running
        // inline bootstrap scripts. Prefer the fallback path when the document advertises
        // a noscript/meta-refresh recovery flow and ships a very large inline script payload.
        private static bool IsJsHeavyAppShell(Node domRoot, Uri baseUri)
        {
            if (domRoot == null)
            {
                return false;
            }

            if (IsGoogleHost(baseUri))
            {
                return false;
            }

            bool hasNoscriptRefresh = false;
            int inlineScriptChars = 0;

            foreach (var element in domRoot.Descendants().OfType<Element>())
            {
                if (string.Equals(element.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
                {
                    if (element.Descendants().OfType<Element>().Any(child =>
                        string.Equals(child.TagName, "meta", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(child.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasNoscriptRefresh = true;
                    }

                    continue;
                }

                if (!string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase) ||
                    element.HasAttribute("src"))
                {
                    continue;
                }

                var scriptText = element.Text;
                if (string.IsNullOrWhiteSpace(scriptText))
                {
                    continue;
                }

                inlineScriptChars += scriptText.Length;
                if (hasNoscriptRefresh && inlineScriptChars >= 20000)
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            try
            {
                EventLoopCoordinator.Instance.SetRenderCallback(null);
                _repaintGate.Dispose();
                // _activeJs does not implement IDisposable, just clear ref
                _activeJs = null;
                SetActiveDom(null);
                // _cachedView = null;
                _cachedRenderer = null;
                JavaScriptEngine.SetVisualRectProvider(null);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[CustomHtmlEngine] Dispose error: {ex.Message}", LogCategory.Rendering);
            }
        }

        private void ProcessQueuedRenderUpdate()
        {
            try
            {
                RefreshAsync(includeDiagnosticsBanner: false).GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[CustomHtmlEngine] Queued render update failed: {ex.Message}", LogCategory.Rendering);
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
                            EngineLogCompat.Error($"[CustomHtmlEngine] LoadingChanged handler error (async): {ex.Message}", LogCategory.Rendering);
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
                EngineLogCompat.Error($"[CustomHtmlEngine] LoadingChanged handler error: {ex.Message}", LogCategory.Rendering);
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
                EngineLogCompat.Debug($"[CustomHtmlEngine] ResolveUri failed for '{href}': {ex.Message}", LogCategory.Rendering);
            }
            return null;
        }

        // ---------------------------------------------------------
        // Helper: Image Logic (Consolidated for Performance/DRY)
        // ---------------------------------------------------------

        // Preserve WebP assets (Skia supports them); only rewrite AVIF when absolutely necessary.
        private static string RewriteWebPToJpg(string u)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(u)) return u;

                // Fast exit when no AVIF tokens present.
                if (u.IndexOf("avif", StringComparison.OrdinalIgnoreCase) < 0)
                    return u;

                // Some CDNs gate AVIF behind query params; fall back to JPEG for compatibility.
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(\?|&)(f|fmt)=avif", "$1$2=jpg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\.avif(\?.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    u = System.Text.RegularExpressions.Regex.Replace(u, @"\.avif(\?.*)?$", ".jpg$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[CustomHtmlEngine] RewriteWebPToJpg failed for '{u}': {ex.Message}", LogCategory.Rendering);
            }
            return u;
        }

        private static string PickBestImageFromSrcSet(string srcset, double viewportWidth)
        {
            var selected = ResponsiveImageSourceSelector.PickBestImageCandidate(null, srcset, viewportWidth);
            return RewriteWebPToJpg(selected);
        }

        // ---------------------------------------------------------
        // End Helper
        // ---------------------------------------------------------

        // Kick off background image fetches early (img/srcset/background-image)
        private static async Task PrewarmImagesAsync(Element root, Uri baseUri, Func<Uri, Task<Stream>> imageLoader, double? viewportWidth)
        {
            if (root == null || imageLoader == null) return;
            try
            {
                var eagerLoads = new List<Uri>();
                var backgroundLoads = new List<Uri>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var gate = new System.Threading.SemaphoreSlim(ImagePrewarmMaxConcurrency);
                int budget = ImagePrewarmQueueBudget; // avoid over-queuing
                double dw = viewportWidth ?? 0; 
                try { if (dw <= 0) dw = GetPrimaryWindowWidth(); } catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Failed reading primary window width: {ex.Message}", LogCategory.Rendering); }
                if (dw <= 0) dw = 480;

                async Task LoadAndCacheAsync(Uri abs)
                {
                    try
                    {
                        await gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var data = await ImageLoader.FetchBytesForCurrentContextAsync(abs).ConfigureAwait(false);
                            if (data != null && data.Length > 0)
                            {
                                try
                                {
                                    using var memory = new MemoryStream(data, writable: false);
                                    if (await ImageLoader.PrewarmImageAsync(abs.AbsoluteUri, memory).ConfigureAwait(false))
                                    {
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    EngineLogCompat.Debug($"[CustomHtmlEngine] Byte-fetch prewarm fallback triggered for {abs}: {ex.Message}", LogCategory.Rendering);
                                }
                            }

                            using var stream = await imageLoader(abs).ConfigureAwait(false);
                            if (stream != null)
                            {
                                await ImageLoader.PrewarmImageAsync(abs.AbsoluteUri, stream).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Warn($"[CustomHtmlEngine] Image load task failed: {ex.Message}", LogCategory.Rendering);
                    }
                }

                void queueLoad(string rawUrl)
                {
                    if (budget <= 0 || string.IsNullOrWhiteSpace(rawUrl)) return;
                    var clean = RewriteWebPToJpg(rawUrl);
                    var abs = ResolveUri(baseUri, clean);
                    if (abs == null || !seen.Add(abs.AbsoluteUri)) return;

                    budget--;
                    if (eagerLoads.Count < ImagePrewarmEagerCandidateLimit)
                    {
                        eagerLoads.Add(abs);
                    }
                    else
                    {
                        backgroundLoads.Add(abs);
                    }
                }

                // Preload/Prefetch links
                foreach (var link in root.Descendants().OfType<Element>().Where(n => string.Equals(n.TagName, "link", StringComparison.OrdinalIgnoreCase) && n.Attr != null))
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
                        if (n.IsText()) continue;
                        if (n is Element el)
                        {
                            if (string.Equals(el.TagName, "img", StringComparison.OrdinalIgnoreCase) && el.Attr != null)
                            {
                                string src = null; el.Attr.TryGetValue("src", out src);
                                if (string.IsNullOrWhiteSpace(src))
                                {
                                    string v; 
                                    if (el.Attr.TryGetValue("data-src", out v)) src = v; 
                                    else if (el.Attr.TryGetValue("data-original", out v)) src = v; 
                                    else if (el.Attr.TryGetValue("data-lazy", out v)) src = v;
                                }

                                string srcset = null; el.Attr.TryGetValue("srcset", out srcset);
                                string chosen = null;
                                if (!string.IsNullOrWhiteSpace(srcset)) 
                                {
                                    chosen = PickBestImageFromSrcSet(srcset, dw);
                                }
                                
                                if (string.IsNullOrWhiteSpace(chosen)) chosen = src;
                                queueLoad(chosen);
                            }
                            else if (el.Attr != null)
                            {
                                string style; 
                                if (el.Attr.TryGetValue("style", out style) && !string.IsNullOrWhiteSpace(style))
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
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[CustomHtmlEngine] PrewarmImages node processing error: {ex.Message}", LogCategory.Rendering);
                    }
                }

                if (eagerLoads.Count > 0)
                {
                    var eagerTasks = eagerLoads.Select(LoadAndCacheAsync).ToArray();
                    var eagerAggregate = Task.WhenAll(eagerTasks);
                    var completed = await Task.WhenAny(eagerAggregate, Task.Delay(ImagePrewarmAwaitBudgetMs)).ConfigureAwait(false);
                    if (completed == eagerAggregate)
                    {
                        await eagerAggregate.ConfigureAwait(false);
                    }
                    else
                    {
                        EngineLogCompat.Debug(
                            $"[CustomHtmlEngine] Timed out waiting for eager image prewarm batch after {ImagePrewarmAwaitBudgetMs}ms ({eagerLoads.Count} candidate(s))",
                            LogCategory.Rendering);
                    }
                }

                foreach (var abs in backgroundLoads)
                {
                    _ = RunDetachedAsync(() => LoadAndCacheAsync(abs));
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[CustomHtmlEngine] PrewarmImages failed: {ex.Message}", LogCategory.Rendering);
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
                EngineLogCompat.Debug($"[CustomHtmlEngine] VisualChildCount error: {ex.Message}", LogCategory.Rendering);
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
                 EngineLogCompat.Debug($"[CustomHtmlEngine] VisualGetChild error: {ex.Message}", LogCategory.Rendering);
            }
            return null;
        }

        private static string GatherPlainText(Node n)
        {
            if (n == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            Action<Node> walk = null;
            walk = (node) =>
            {
                if (node == null) return;
                if (node.IsText()) { var t = node.TextContent ?? string.Empty; sb.Append(t); return; }
                if (node.ChildNodes != null)
                {
                    foreach (var child in node.ChildNodes)
                    {
                        walk(child);
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
            double? viewportHeight,
            Action<object> onFixedBackground,
            JavaScriptEngine js)
        {
            SetActiveDom(dom);
            _activeBaseUri = baseUri;
            _activeFetchCss = fetchExternalCssAsync;
            _activeImageLoader = imageLoader;
            _activeOnNavigate = onNavigate;
            _activeViewportWidth = viewportWidth;
            _activeViewportHeight = viewportHeight;
            _activeFixedBackground = onFixedBackground;
            // Only update _activeJs if a new engine is provided; preserve existing during repaints
            if (js != null) _activeJs = js;
            
            // Keep the JS bridge synchronized with the live DOM without executing page scripts.
            // Full script execution still happens later in RunScriptsAsync with a timeout budget.
            if (_activeJs != null && dom != null)
            {
                try 
                { 
                    EngineLogCompat.Debug("[CaptureActiveContext] Calling SyncDomContext...", LogCategory.Rendering);
                    _activeJs.SyncDomContext(dom, baseUri);
                    EngineLogCompat.Debug("[CaptureActiveContext] SyncDomContext returned.", LogCategory.Rendering);
                    EngineLogCompat.Debug($"[CaptureActiveContext] Synced JS DOM to _activeDom hash={dom.GetHashCode()}", LogCategory.Rendering);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[CustomHtmlEngine] Failed to sync ActiveDom to JS: {ex.Message}", LogCategory.Rendering);
                }
            }
            
            if (_activeJs != null)
            {
                try { _activeJs.FetchOverride = ScriptFetcher; }
                catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Failed to set FetchOverride: {ex.Message}", LogCategory.Rendering); }
            }

            // Extract and fire title
            try
            {
                string title = null;
                var tnode = dom.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.NodeName, "title", StringComparison.OrdinalIgnoreCase));
                if (tnode != null) title = tnode.TextContent;
                
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
                EngineLogCompat.Warn($"[CustomHtmlEngine] Title extraction failed: {ex.Message}", LogCategory.Rendering);
            }
        }

        private void ConfigureMedia(double? viewportWidth, double? viewportHeight)
        {
            try
            {
                var surface = BrowserSettings.GetBrowserSurface(
                    BrowserSettings.Instance.SelectedUserAgent,
                    BrowserViewportMetrics.Create(
                        viewportWidth ?? 1280,
                        viewportHeight ?? 720));

                CssParser.MediaViewportWidth = surface.Viewport.WindowWidth;
                CssParser.MediaViewportHeight = surface.Viewport.WindowHeight;
                try { CssParser.MediaDppx = surface.Viewport.DevicePixelRatio; } catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Failed setting media dppx: {ex.Message}", LogCategory.Rendering); }
                CssParser.MediaPrefersColorScheme = surface.Viewport.PreferredColorScheme;
                CssParser.PrefersDarkMode = string.Equals(surface.Viewport.PreferredColorScheme, "dark", StringComparison.OrdinalIgnoreCase);
                CssParser.MediaPrefersReducedMotion = surface.Viewport.ReducedMotion ? "reduce" : "no-preference";
                CssParser.MediaHover = surface.Viewport.Hover ? "hover" : "none";
                CssParser.MediaAnyHover = CssParser.MediaHover;
                CssParser.MediaPointer = surface.Viewport.FinePointer ? "fine" : "coarse";
                CssParser.MediaAnyPointer = CssParser.MediaPointer;
                CssParser.MediaScripting = EnableJavaScript ? "enabled" : "none";
                CssParser.MediaDisplayMode = "browser";
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[CustomHtmlEngine] ConfigureMedia failed: {ex.Message}", LogCategory.Rendering);
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

            ConfigureMedia(viewportWidth, viewportHeight);

            var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            
            // NOTE: Don't set LastComputedStyles = null here!
            // The EngineLoop polls for styles during CSS computation (which can take 20+ seconds).
            // Setting to null causes the renderer to skip styling until computation completes.
            // Instead, keep the previous styles visible until new ones are ready.
            LastCssSources = null;
            try
            {
                // PERF: LoadCssAsync() in RenderAsync already ran CascadeIntoComputedStyles()
                // and assigned n.ComputedStyle to every node. Reuse those styles instead of
                // running the full O(elements Ã— rules) cascade a second time.
                if (LastComputedStyles != null && LastComputedStyles.Count > 0)
                {
                    EngineLogCompat.Debug($"[BuildVisualTree] Reusing {LastComputedStyles.Count} pre-computed styles (skipping duplicate cascade)", LogCategory.Rendering);
                    FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(false, LastComputedStyles.Count);
                }
                else
                {
                    EngineLogCompat.Debug("[CustomHtmlEngine] BuildVisualTree: Using CSS Engine (no pre-computed styles)...", LogCategory.Rendering);

                    var cssEngine = CssEngineFactory.GetEngine();
                    EngineLogCompat.Debug($"[CustomHtmlEngine] BuildVisualTree: Engine={cssEngine.EngineName}", LogCategory.Rendering);

                    SetComputedStyles(await cssEngine.ComputeStylesAsync(dom, baseUri, cssFetcher, viewportWidth, viewportHeight));
                    EngineLogCompat.Debug($"[PERF] CSS ComputeStyles: {_buildTreeStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                    // Assign computed styles to nodes so Layout Engine can see them
                    if (LastComputedStyles != null)
                    {
                        foreach (var kvp in LastComputedStyles)
                        {
                            if (kvp.Key != null)
                                kvp.Key.ComputedStyle = kvp.Value;
                        }
                    }

                    FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(false, LastComputedStyles?.Count ?? 0);
                }
                EngineLogCompat.Debug($"[PERF] BuildVisualTree Complete: {_buildTreeStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                EngineLogCompat.Debug($"[CustomHtmlEngine] BuildVisualTree: CSS Success. Styles Count={LastComputedStyles?.Count}", LogCategory.Rendering);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[CustomHtmlEngine] CssLoader CRASH: {ex}", LogCategory.Rendering);
                SetComputedStyles(new Dictionary<Node, CssComputed>());
            }

            // var computed = result.Computed; // Use LastComputedStyles instead
            var computed = LastComputedStyles;

            // [MIGRATION] Background check logic removed or simplified (Avalonia Brush removed)
            // Just invoking DomReady
            
            try { DomReady?.Invoke(this, dom); } catch (Exception drEx) { EngineLogCompat.Error($"[BuildVisualTree] DomReady error: {drEx}", LogCategory.Rendering); }

            EngineLogCompat.Debug("[BuildVisualTree] Creating renderer...", LogCategory.Rendering);

            if (_cachedRenderer == null)
            {
                EngineLogCompat.Debug("[BuildVisualTree] Creating NEW renderer...", LogCategory.Rendering);
                _cachedRenderer = new SkiaDomRenderer();
            }

            JavaScriptEngine.SetVisualRectProvider(element =>
            {
                if (element == null || _cachedRenderer == null)
                {
                    return null;
                }

                var rectSource = element;
                var box = _cachedRenderer.GetElementBox(element);
                if (box == null)
                {
                    var elementId = element.GetAttribute("id");
                    if (!string.IsNullOrWhiteSpace(elementId))
                    {
                        var root = (_activeDom as Element) ?? element.OwnerDocument?.DocumentElement;
                        if (root != null)
                        {
                            var mapped = root.SelfAndDescendants()
                                .OfType<Element>()
                                .FirstOrDefault(candidate =>
                                    !ReferenceEquals(candidate, element) &&
                                    string.Equals(candidate.GetAttribute("id"), elementId, StringComparison.Ordinal));
                            if (mapped != null)
                            {
                                rectSource = mapped;
                                box = _cachedRenderer.GetElementBox(mapped);
                            }
                        }
                    }
                }

                if (box == null)
                {
                    return null;
                }

                var rect = box.BorderBox;
                if (LastComputedStyles != null &&
                    rectSource != null &&
                    LastComputedStyles.TryGetValue(rectSource, out var computed) &&
                    computed != null &&
                    string.Equals(computed.Position, "relative", StringComparison.OrdinalIgnoreCase))
                {
                    // Compensate for relative left/top offsets when layout metrics still report static origin.
                    if (computed.Left.HasValue && Math.Abs(rect.Left) < 0.5f && Math.Abs(computed.Left.Value) > 0.5)
                    {
                        rect = SKRect.Create(
                            rect.Left + (float)computed.Left.Value,
                            rect.Top,
                            rect.Width,
                            rect.Height);
                    }

                    if (computed.Top.HasValue && Math.Abs(rect.Top) < 0.5f && Math.Abs(computed.Top.Value) > 0.5)
                    {
                        rect = SKRect.Create(
                            rect.Left,
                            rect.Top + (float)computed.Top.Value,
                            rect.Width,
                            rect.Height);
                    }
                }

                return rect;
            });

            // [MIGRATION] View logic removed. Host is responsible for rendering.
            
            EngineLogCompat.Debug("[RenderAsync] Visual tree built properly (Headless)", LogCategory.Rendering);
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
            EngineLogCompat.Debug($"[RefreshAsyncInternal] using _activeDom hash={_activeDom.GetHashCode()}", LogCategory.Rendering);

            var fetchCss = _activeFetchCss ?? (async _ => { await Task.CompletedTask; return string.Empty; });
            return await BuildVisualTreeAsync(
                (_activeDom as Element) ?? (_activeDom as Document)?.DocumentElement,
                _activeBaseUri,
                fetchCss,
                _activeImageLoader,
                _activeOnNavigate,
                _activeJs,
                _activeViewportWidth,
                _activeViewportHeight ?? GetPrimaryWindowHeight(),
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
                        catch (Exception ex) { EngineLogCompat.Error($"[CustomHtmlEngine] RepaintReady async handler error: {ex.Message}", LogCategory.Rendering); }
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
                 EngineLogCompat.Error($"[CustomHtmlEngine] DispatchRepaintAsync error: {ex.Message}", LogCategory.Rendering);
            }
        }

                private void ScheduleRepaintFromJs()
        {
            // STRICT CONTROL FLOW:
            // JavaScript CANNOT push frames. It can only mark state as dirty.
            
            // 1. JS is allowed to request a future repaint, but it must never
            // trigger layout/paint re-entrantly from within the hot path.
            EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            EngineLogCompat.Debug("[CustomHtmlEngine] ScheduleRepaintFromJs (Dirty Flag Set)", LogCategory.Rendering);

            // 2. Mark dirty ONLY via the Coordinator.
            // The Event Loop or Host will check this flag at the appropriate checkpoint.
            EventLoopCoordinator.Instance.NotifyLayoutDirty();
        }

        private async Task<DomParseResult> RunDomParseAsync(string html)
        {
            var parseInput = html ?? string.Empty;
            // CRITICAL FIX: Use production HtmlTreeBuilder from Core.Parsing
            // The FenEngine version doesn't properly implement RAWTEXT state for style/script,
            // causing CSS content to leak as visible text nodes
            var builder = new FenBrowser.Core.Parsing.HtmlTreeBuilder(parseInput);
            builder.ParseCheckpointTokenInterval = 512;
            builder.InterleavedTokenBatchSize = ResolveInterleavedTokenBatchSize(EnableInterleavedPrimaryParse, parseInput.Length);
            var parseCheckpointState = new ParseCheckpointState();
            var streamingPreparseMs = 0L;
            var streamingPreparseCheckpointCount = 0;
            var streamingPreparseRepaintCount = 0;
            AttachParseDocumentCheckpointCallback(builder, parseCheckpointState);
            try
            {
                if (ShouldRunStreamingParsePrepass(EnableStreamingParsePrepass, parseInput.Length))
                {
                    streamingPreparseMs = await RunStreamingPreparseAsync(
                        parseInput,
                        checkpointCount => streamingPreparseCheckpointCount = checkpointCount,
                        repaintCount => streamingPreparseRepaintCount = repaintCount).ConfigureAwait(false);
                }

                EngineLogCompat.Debug("[RenderAsync] Starting parse (Production Parser)...", LogCategory.Rendering);
                var interleavedFallbackUsed = false;
                var doc = await Task.Run<Node>(() =>
                {
                    try
                    {
                        return builder.BuildWithPipelineStages(PipelineContext.Current);
                    }
                    catch (Exception pex)
                    {
                        EngineLogCompat.Error($"[RenderAsync] Parse exception: {pex.Message}", LogCategory.Rendering);
                        if (builder.InterleavedTokenBatchSize > 0)
                        {
                            try
                            {
                                EngineLogCompat.Warn("[RenderAsync] Retrying parse with interleaved mode disabled", LogCategory.Rendering);
                                parseCheckpointState.ParsingDocumentCheckpointCount = 0;
                                parseCheckpointState.ParsingCheckpointOrdinal = 0;
                                parseCheckpointState.IncrementalRepaintCount = 0;

                                var fallbackBuilder = new FenBrowser.Core.Parsing.HtmlTreeBuilder(parseInput);
                                fallbackBuilder.ParseCheckpointTokenInterval = builder.ParseCheckpointTokenInterval;
                                fallbackBuilder.InterleavedTokenBatchSize = 0;
                                AttachParseDocumentCheckpointCallback(fallbackBuilder, parseCheckpointState);
                                var fallbackDoc = fallbackBuilder.BuildWithPipelineStages(PipelineContext.Current);
                                builder = fallbackBuilder;
                                interleavedFallbackUsed = true;
                                return fallbackDoc;
                            }
                            catch (Exception fallbackEx)
                            {
                                EngineLogCompat.Error($"[RenderAsync] Fallback parse exception: {fallbackEx.Message}", LogCategory.Rendering);
                            }
                        }

                        return new FenBrowser.Core.Dom.V2.Document();
                    }
                });
                
                EngineLogCompat.Debug("[RenderAsync] Parse complete", LogCategory.Rendering);
                
                // DEBUG: Dump DOM
                try {
                     var sb = new StringBuilder();
                     DumpTree((doc as FenBrowser.Core.Dom.V2.Document)?.DocumentElement ?? doc, sb, 0);
                     System.IO.File.WriteAllText(DiagnosticPaths.GetRootArtifactPath("dom_dump.txt"), sb.ToString());
                } catch (Exception ex) { EngineLogCompat.Warn($"[RenderAsync] Failed writing dom_dump.txt: {ex.Message}", LogCategory.Rendering); }

                var metrics = builder.LastBuildMetrics ?? new FenBrowser.Core.Parsing.HtmlParseBuildMetrics();
                return new DomParseResult
                {
                    // Return DocumentElement (the HTML element), not the Document wrapper
                    Dom = (doc as FenBrowser.Core.Dom.V2.Document)?.DocumentElement ?? doc,
                    TokenizingMs = Math.Max(0, metrics.TokenizingMs),
                    ParsingMs = Math.Max(0, metrics.ParsingMs),
                    TokenCount = Math.Max(0, metrics.TokenCount),
                    TokenizingCheckpointCount = Math.Max(0, metrics.TokenizingCheckpointCount),
                    ParsingCheckpointCount = Math.Max(0, metrics.ParsingCheckpointCount),
                    ParsingDocumentCheckpointCount = Math.Max(0, parseCheckpointState.ParsingDocumentCheckpointCount),
                    DocumentReadyTokenCount = Math.Max(0, metrics.DocumentReadyTokenCount),
                    IncrementalRepaintCount = Math.Max(0, parseCheckpointState.IncrementalRepaintCount),
                    StreamingPreparseMs = Math.Max(0, streamingPreparseMs),
                    StreamingPreparseCheckpointCount = Math.Max(0, streamingPreparseCheckpointCount),
                    StreamingPreparseRepaintCount = Math.Max(0, streamingPreparseRepaintCount),
                    InterleavedParseUsed = metrics.UsedInterleavedBuild,
                    InterleavedTokenBatchSize = Math.Max(0, metrics.InterleavedTokenBatchSize),
                    InterleavedBatchCount = Math.Max(0, metrics.InterleavedBatchCount),
                    InterleavedFallbackUsed = interleavedFallbackUsed
                };
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[RenderAsync] Parse error: {ex.Message}", LogCategory.Rendering);
                return null;
            }
        }

        private async Task LoadCssAsync(Element dom, Uri baseUri, Func<Uri, Task<string>> fetchExternalCssAsync)
        {
             try
             {
                 EngineLogCompat.Debug("[RenderAsync] Starting CSS load...", LogCategory.Rendering);
                 var cssTask = CssLoader.ComputeAsync(dom, baseUri, fetchExternalCssAsync, null, null, (msg) => EngineLogCompat.Debug(msg, LogCategory.Rendering));
                 var timeoutTask = Task.Delay(30000); // Increased from 10s to 30s for complex pages
                 var completedTask = await Task.WhenAny(cssTask, timeoutTask);
                 
                 if (completedTask == timeoutTask)
                 {
                     EngineLogCompat.Warn("[RenderAsync] CSS loading timed out after 30s", LogCategory.Rendering);
                     FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(true, 0);
                 }
                 else
                 {
                     // CRITICAL FIX: Actually store the computed styles!
                     var computedStyles = await cssTask;
                     UpdateRenderState(dom, computedStyles);
                     EngineLogCompat.Info($"[RenderAsync] CSS loading complete. Styles Count={LastComputedStyles?.Count ?? 0}", LogCategory.Rendering);
                     FenBrowser.Core.Verification.ContentVerifier.RegisterCssState(false, LastComputedStyles?.Count ?? 0);

                     // Assign computed styles to DOM nodes so the renderer can access them via
                     // Node.ComputedStyle even when the styles dict is not passed directly.
                     if (LastComputedStyles != null)
                     {
                         foreach (var kvp in LastComputedStyles)
                             if (kvp.Key != null) kvp.Key.ComputedStyle = kvp.Value;
                     }

                     ClearStyleDirtyFlags(dom);

                     // Sync _activeDom to the real parsed DOM (dom parameter) so that when
                     // OnRepaintReady fires, GetActiveDom() returns the same tree whose nodes
                     // are the keys in LastComputedStyles.  Without this fix _activeDom would
                     // still point to a stale incremental-parse clone, causing every style
                     // lookup to miss and producing a blank frame.
                     // Intentionally quiet in hot path: WPT runs are sensitive to stdout flooding.

                     // CRITICAL FIX: Trigger repaint after CSS completes so layout re-runs with styles
                     // This ensures flexbox centering, visibility, and other CSS properties are applied
                     EngineLogCompat.Debug("[RenderAsync] Triggering repaint after CSS completion", LogCategory.Rendering);
                     OnRepaintReady(dom);
                 }
             }
             catch (Exception cssEx) 
             { 
                 EngineLogCompat.Error($"[RenderAsync] CSS error: {cssEx.Message}", LogCategory.Rendering);
             }
        }

        // --- Dynamic re-cascade (hover/focus/DOM mutations) ---

        // Holds the in-flight re-cascade task so we don't stack them up.
        private volatile Task _pendingRecascade = null;

        /// <summary>
        /// Schedule a CSS re-cascade on the current DOM using cached render parameters.
        /// Safe to call from any thread (e.g. ElementStateManager.OnStateChanged).
        /// If a re-cascade is already in-flight, the call is a no-op; the next
        /// RepaintReady that fires after the in-flight task completes will carry the
        /// freshest styles.
        /// </summary>
        /// <summary>
        /// Tracks whether a full recascade is needed (stylesheet change) vs incremental (DOM mutation).
        /// </summary>
        private volatile bool _fullRecascadeRequired;

        public void ScheduleRecascade(bool fullRecascade = false)
        {
            if (_activeDom == null || _activeBaseUri == null || _activeFetchCss == null)
                return;

            if (fullRecascade)
                _fullRecascadeRequired = true;

            // Only one re-cascade in flight at a time.
            var pending = _pendingRecascade;
            if (pending != null && !pending.IsCompleted)
                return;

            _pendingRecascade = RunDetachedAsync(async () =>
            {
                try
                {
                    if (_fullRecascadeRequired)
                    {
                        _fullRecascadeRequired = false;
                        EngineLogCompat.Info("[CustomHtmlEngine] Full recascade (stylesheet change)", LogCategory.CSS);
                        await RecascadeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await IncrementalRecascadeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[CustomHtmlEngine] RecascadeAsync failed: {ex.Message}",
                        LogCategory.Rendering);
                }
            });
        }

        /// <summary>
        /// Re-runs the CSS cascade on the currently active DOM using cached parameters.
        /// Updates LastComputedStyles and fires RepaintReady so the engine loop redraws.
        /// </summary>
        public async Task RecascadeAsync()
        {
            if (_activeDom == null || _activeBaseUri == null || _activeFetchCss == null)
                return;

            var domEl = (_activeDom as FenBrowser.Core.Dom.V2.Element)
                     ?? (_activeDom as FenBrowser.Core.Dom.V2.Document)?.DocumentElement;
            if (domEl == null) return;

            await LoadCssAsync(domEl, _activeBaseUri, _activeFetchCss).ConfigureAwait(false);
        }

        /// <summary>
        /// Walks the DOM using ChildStyleDirty flags to find dirty subtree roots,
        /// recascades only those subtrees, then fires OnRepaintReady.
        /// Falls back to full recascade if >30% of tree is dirty.
        /// </summary>
        private async Task IncrementalRecascadeAsync()
        {
            if (_activeDom == null) return;

            var domEl = (_activeDom as FenBrowser.Core.Dom.V2.Element)
                     ?? (_activeDom as FenBrowser.Core.Dom.V2.Document)?.DocumentElement;
            if (domEl == null) return;

            // Collect dirty subtree roots by walking ChildStyleDirty flags
            var dirtyRoots = new List<Element>();
            int totalElements = 0;
            CollectDirtySubtrees(domEl, dirtyRoots, ref totalElements);

            // Fallback: if >30% dirty, full recascade is cheaper than incremental
            if (totalElements > 0 && dirtyRoots.Count > totalElements * 0.3)
            {
                EngineLogCompat.Info($"[CustomHtmlEngine] Incremental too broad ({dirtyRoots.Count}/{totalElements} dirty) — falling back to full recascade", LogCategory.CSS);
                await RecascadeAsync().ConfigureAwait(false);
                return;
            }

            if (dirtyRoots.Count == 0)
            {
                // No dirty nodes — still fire repaint in case layout changed
                EngineLogCompat.Debug(
                    "[CustomHtmlEngine] Incremental recascade found no dirty roots; skipping no-op recascade.",
                    LogCategory.CSS);
                return;
            }

            EngineLogCompat.Info(
                $"[CustomHtmlEngine] Incremental recascade: Processing {dirtyRoots.Count} dirty subtree(s)...",
                LogCategory.CSS);

            foreach (var root in dirtyRoots)
            {
                try
                {
                    // Compute styles for this subtree only
                    var subtreeStyles = await CssLoader.ComputeAsync(root, _activeBaseUri, _activeFetchCss).ConfigureAwait(false);
                    
                    if (subtreeStyles != null)
                    {
                        lock (_renderStateLock)
                        {
                            // Merge into main styles dictionary
                            foreach (var kvp in subtreeStyles)
                            {
                                LastComputedStyles[kvp.Key] = kvp.Value;
                                kvp.Key.ComputedStyle = kvp.Value;
                            }
                        }
                    }
                    
                    ClearStyleDirtyFlags(root);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[CustomHtmlEngine] Incremental subtree recascade failed for <{root.TagName}>: {ex.Message}", LogCategory.CSS);
                }
            }

            // Trigger repaint with updated styles
            OnRepaintReady(domEl);
        }

        /// <summary>
        /// Walks the DOM tree using ChildStyleDirty flags. Prunes branches where ChildStyleDirty=false.
        /// Collects the shallowest dirty Elements as subtree roots.
        /// </summary>
        private static void CollectDirtySubtrees(Element root, List<Element> dirtyRoots, ref int totalElements)
        {
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                totalElements++;

                if (el.StyleDirty)
                {
                    // This element is dirty — add it as a subtree root (don't recurse into children,
                    // because RecascadeSubtree will handle the full subtree)
                    dirtyRoots.Add(el);
                    continue;
                }

                if (!el.ChildStyleDirty)
                    continue; // Prune: no dirty descendants in this branch

                // Walk children looking for dirty nodes
                var children = el.ChildNodes;
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    if (children[i] is Element childEl)
                        stack.Push(childEl);
                }
            }
        }

        /// <summary>
        /// Re-cascades a single dirty subtree: recomputes ComputedStyle for each element
        /// using its inline styles and inherited parent style. Clears StyleDirty flags.
        /// </summary>
        private static bool NeedsPostScriptStyleRefresh(
            Node root,
            IReadOnlyDictionary<Node, CssComputed> computedStyles)
        {
            if (root == null)
            {
                return false;
            }

            if (root.StyleDirty || root.ChildStyleDirty)
            {
                return true;
            }

            if (computedStyles == null || computedStyles.Count == 0)
            {
                return true;
            }

            return HasNodeMissingComputedStyle(root, computedStyles);
        }

        private static bool HasNodeMissingComputedStyle(
            Node node,
            IReadOnlyDictionary<Node, CssComputed> computedStyles)
        {
            if (node == null)
            {
                return false;
            }

            if (!computedStyles.ContainsKey(node))
            {
                return true;
            }

            var children = node.ChildNodes;
            if (children == null || children.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < children.Length; i++)
            {
                if (HasNodeMissingComputedStyle(children[i], computedStyles))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ClearStyleDirtyFlags(Node root)
        {
            if (root == null)
            {
                return;
            }

            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                node.ClearDirty(InvalidationKind.Style);

                var children = node.ChildNodes;
                if (children == null || children.Length == 0)
                {
                    continue;
                }

                for (int i = children.Length - 1; i >= 0; i--)
                {
                    if (children[i] != null)
                    {
                        stack.Push(children[i]);
                    }
                }
            }
        }

        private JavaScriptEngine SetupJavaScriptEngine(
             Uri baseUri,
             Action<Uri> onNavigate, 
             bool allowJs, 
             Func<Uri, Task<string>> fetchExternalCssAsync,
             double? viewportWidth,
             double? viewportHeight)
        {
             if (!allowJs) return null;

             EngineLogCompat.Debug("[CustomHtmlEngine] Creating JavaScriptEngine...", LogCategory.Rendering);
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
                     catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] UI dispatch invoke failed, running inline action: {ex.Message}", LogCategory.Rendering); action(); }
                 },
                 setTitle: null,
                 alert: (msg) => { AlertTriggered?.Invoke(msg); },
                 confirm: (msg) =>
                 {
                     var handler = ConfirmTriggered;
                     if (handler != null)
                     {
                         return handler(msg);
                     }

                     AlertTriggered?.Invoke(msg);
                     return true;
                 },
                 prompt: (msg, defaultValue) =>
                 {
                     var handler = PromptTriggered;
                     if (handler != null)
                     {
                         return handler(msg, defaultValue);
                     }

                     AlertTriggered?.Invoke(msg);
                     return defaultValue ?? string.Empty;
                 },
                 log: (msg) => 
                 { 
                     EngineLogCompat.Debug($"[CustomHtmlEngine] Received log from JS Engine: {msg}", LogCategory.JavaScript);
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
                        return ActivePolicy.IsAllowed(directive, u, baseUri);
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
                    bool allowed = ActivePolicy.IsAllowed("script-src", null, nonce, baseUri, isInline: true);
                     if (allowed && nonce == "wrong456") 
                     {
                          EngineLogCompat.Error($"[CSP-CRITICAL] Nonce 'wrong456' ALLOWED! ActivePolicy hash={ActivePolicy.GetHashCode()}", LogCategory.Rendering);
                          // Dump directives
                          foreach (var d in ActivePolicy.Directives)
                          {
                              EngineLogCompat.Error($"  [DIR] {d.Key}: Sources={string.Join(",",d.Value.Sources)} Nonces={string.Join(",",d.Value.Nonces)}", LogCategory.Rendering);
                          }
                     }
                     return allowed;
                 }
                 // No CSP policy from HTTP header or meta tag â€” permissively allow inline scripts.
                 // This is correct behavior; the warning was misleading noise.
                 EngineLogCompat.Debug("[CSP] No ActivePolicy during nonce check â€” permissively allowing inline script.", LogCategory.Rendering);
                 return true;
             };
             
             // Wire up permission requests
             js.PermissionRequested += async (origin, perm) => 
             {
                 if (PermissionRequested != null) return await PermissionRequested(origin, perm);
                 return false;
             };

             js.CookieReadBridge = scope => CookieJar.GetDocumentCookieString(scope, _activeBaseUri ?? scope);
             js.CookieWriteBridge = (scope, cookieString) =>
                 CookieJar.SetDocumentCookie(scope, cookieString, _activeBaseUri ?? scope, BrowserSettings.Instance.BlockThirdPartyCookies);
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
             catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Domain tuning failed: {ex.Message}", LogCategory.Rendering); }

             if (js != null) 
             {
                 js.FetchHandler = FetchHandler;
                 // [Compliance] Inject Window Dimensions
                if (viewportWidth.HasValue) js.WindowWidth = viewportWidth.Value;
                if (viewportHeight.HasValue) js.WindowHeight = viewportHeight.Value;
            }

             return js;
        }

        private async Task RunScriptsAsync(JavaScriptEngine js, Element dom, Uri baseUri)
        {
            if (js == null) return;
            
            EngineLogCompat.Debug("[RenderAsync] Running Scripts", LogCategory.Rendering);

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
                 EngineLogCompat.Debug("[RenderAsync] Detection helper script executed", LogCategory.Rendering);
            }
            catch (Exception dhEx)
            {
                 EngineLogCompat.Warn($"[RenderAsync] Detection helper error: {dhEx.Message}", LogCategory.Rendering);
            }

            // 2. Main Page Scripts
            try 
            {
                var scriptTask = RunDetachedAsync(async () => { await js.SetDomAsync(dom, baseUri).ConfigureAwait(false); });
                var scriptTimeoutMs = Math.Max(
                    15000,
                    (int)js.RuntimeProfile.MaxExecutionTime.TotalMilliseconds * 3);
                var timeoutTask = Task.Delay(scriptTimeoutMs); 
                var completedTask = await Task.WhenAny(scriptTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                    EngineLogCompat.Warn($"[RenderAsync] Script execution timed out after {scriptTimeoutMs / 1000}s", LogCategory.Rendering);
                else
                    EngineLogCompat.Debug("[RenderAsync] Scripts Finished", LogCategory.Rendering);
            } 
            catch (Exception ex) 
            { 
                EngineLogCompat.Error($"[RenderAsync] Script Error: {ex.Message}", LogCategory.Rendering);
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
            double? viewportHeight = null,
            Action<object>? onFixedBackground = null,
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
                        var result = await RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, viewportHeight, onFixedBackground, forceJavascript, disableAutoFallback);
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
            long lastStageMarkMs = 0;
            long tokenizingMs = 0;
            long parsingMs = 0;
            long tokenizingAndParsingMs = 0;
            int parseTokenCount = 0;
            int tokenizingCheckpointCount = 0;
            int parsingCheckpointCount = 0;
            int parsingDocumentCheckpointCount = 0;
            int documentReadyTokenCount = 0;
            int incrementalParseRepaintCount = 0;
            long streamingPreparseMs = 0;
            int streamingPreparseCheckpointCount = 0;
            int streamingPreparseRepaintCount = 0;
            bool interleavedParseUsed = false;
            int interleavedTokenBatchSize = 0;
            int interleavedBatchCount = 0;
            bool interleavedFallbackUsed = false;
            long cssAndStyleMs = 0;
            long initialVisualTreeMs = 0;
            long scriptExecutionMs = 0;
            long postScriptVisualTreeMs = 0;
            bool javascriptExecuted = false;
            
            // Store raw HTML for DOM comparison feature
            _lastRawHtml = html;

            try
            {
                EngineLogCompat.Info($"[CustomHtmlEngine] RenderAsync Start. HTML Length: {html?.Length ?? 0}", LogCategory.Rendering);
                MarkStyleSnapshotUnstable();

                var preferFallbackDom = ShouldPreferFallbackDom(html, baseUri);
                if (preferFallbackDom)
                {
                    html = StripScriptsForFallbackDom(html);
                    EngineLogCompat.Warn($"[SAFE-MODE] Stripped script payloads before parse for fallback-friendly page {baseUri}", LogCategory.Rendering);
                }
                
                const int MaxHtmlSize = 50 * 1024 * 1024; 
                if (!string.IsNullOrEmpty(html) && html.Length > MaxHtmlSize)
                {
                    EngineLogCompat.Warn($"[RenderAsync] HTML too large: {html.Length} bytes, truncating to {MaxHtmlSize}", LogCategory.Rendering);
                    html = html.Substring(0, MaxHtmlSize);
                }

                // 1. Helper: Parse DOM
                var parseResult = await RunDomParseAsync(html);
                var dom = parseResult?.Dom;
                if (dom == null) return null;
                SetActiveDom(dom, markSnapshotUnstable: true);
                tokenizingMs = Math.Max(0, parseResult?.TokenizingMs ?? 0);
                parsingMs = Math.Max(0, parseResult?.ParsingMs ?? 0);
                parseTokenCount = Math.Max(0, parseResult?.TokenCount ?? 0);
                tokenizingCheckpointCount = Math.Max(0, parseResult?.TokenizingCheckpointCount ?? 0);
                parsingCheckpointCount = Math.Max(0, parseResult?.ParsingCheckpointCount ?? 0);
                parsingDocumentCheckpointCount = Math.Max(0, parseResult?.ParsingDocumentCheckpointCount ?? 0);
                documentReadyTokenCount = Math.Max(0, parseResult?.DocumentReadyTokenCount ?? 0);
                incrementalParseRepaintCount = Math.Max(0, parseResult?.IncrementalRepaintCount ?? 0);
                streamingPreparseMs = Math.Max(0, parseResult?.StreamingPreparseMs ?? 0);
                streamingPreparseCheckpointCount = Math.Max(0, parseResult?.StreamingPreparseCheckpointCount ?? 0);
                streamingPreparseRepaintCount = Math.Max(0, parseResult?.StreamingPreparseRepaintCount ?? 0);
                interleavedParseUsed = parseResult?.InterleavedParseUsed ?? false;
                interleavedTokenBatchSize = Math.Max(0, parseResult?.InterleavedTokenBatchSize ?? 0);
                interleavedBatchCount = Math.Max(0, parseResult?.InterleavedBatchCount ?? 0);
                interleavedFallbackUsed = parseResult?.InterleavedFallbackUsed ?? false;
                
                // CRITICAL FIX: Invalidate old styles immediately so the renderer stops using them.
                // This forces BrowserIntegration to wait (showing previous frame or spinner) until new styles are ready.
                SetComputedStyles(null);
                
                var elapsed = _pageLoadStopwatch.ElapsedMilliseconds;
                tokenizingAndParsingMs = Math.Max(0, tokenizingMs + parsingMs);
                if (tokenizingAndParsingMs <= 0)
                {
                    tokenizingAndParsingMs = Math.Max(0, elapsed - lastStageMarkMs);
                }
                lastStageMarkMs = elapsed;
                EngineLogCompat.Debug(
                    $"[PERF] DOM Parse: total={elapsed}ms tokenizing={tokenizingMs}ms parsing={parsingMs}ms tokens={parseTokenCount} docReadyToken={documentReadyTokenCount} parseRepaints={incrementalParseRepaintCount} streaming(ms={streamingPreparseMs},cp={streamingPreparseCheckpointCount},rp={streamingPreparseRepaintCount}) interleaved(used={(interleavedParseUsed ? 1 : 0)},batch={interleavedTokenBatchSize},chunks={interleavedBatchCount},fallback={(interleavedFallbackUsed ? 1 : 0)}) checkpoints(t={tokenizingCheckpointCount},p={parsingCheckpointCount},dom={parsingDocumentCheckpointCount})",
                    LogCategory.Rendering);

                var googleChallengeSanitized = ForceGoogleChallengeBannerVisible(dom, baseUri, removeUnhideScript: true);
                if (googleChallengeSanitized > 0)
                {
                    EngineLogCompat.Info($"[CustomHtmlEngine] GoogleChallengeSanitizeApplied changes={googleChallengeSanitized}", LogCategory.Rendering);
                }

                // 2. Helper: Load CSS
                await LoadCssAsync((dom as Element) ?? (dom as Document)?.DocumentElement, baseUri, fetchExternalCssAsync);
                elapsed = _pageLoadStopwatch.ElapsedMilliseconds;
                cssAndStyleMs = Math.Max(0, elapsed - lastStageMarkMs);
                lastStageMarkMs = elapsed;
                EngineLogCompat.Debug($"[PERF] CSS Load: {elapsed}ms", LogCategory.Rendering);

                // 2.5. Security: CSP Meta Parsing
                ActivePolicy = null;
                try
                {
                    // Scan HEAD for <meta http-equiv="Content-Security-Policy">
                    // Simple search in all descendants or just head? Descendants is safer if HEAD parsing is loose.
                    var metaCsp = dom.Descendants().OfType<Element>()
                        .FirstOrDefault(n => 
                            string.Equals(n.TagName, "meta", StringComparison.OrdinalIgnoreCase) &&
                            n.GetAttribute("http-equiv") != null && 
                            string.Equals(n.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase));
                    
                    if (metaCsp != null && metaCsp.GetAttribute("content") != null)
                    {
                         var cspContent = metaCsp.GetAttribute("content");
                         ActivePolicy = CspPolicy.Parse(cspContent);
                         EngineLogCompat.Info($"[Security] Active CSP from Meta: {cspContent}", LogCategory.Rendering);
                    }
                }
                catch (Exception cspEx)
                {
                    EngineLogCompat.Warn($"[Security] Failed to parse CSP meta: {cspEx.Message}", LogCategory.Rendering);
                }

                // 3. Declarative Shadow DOM
                try
                {
                    var templates = dom.Descendants().OfType<Element>().Where(n => n.TagName == "template" && n.HasAttribute("shadowrootmode")).ToList();
                    foreach (var template in templates)
                    {
                        var parent = template.ParentElement;
                        if (parent != null)
                        {
                            var mode = template.GetAttribute("shadowrootmode");
                            if (mode == "open" || mode == "closed")
                            {
                                try
                                {
                                    var shadow = parent.AttachShadow(new ShadowRootInit { Mode = mode == "open" ? ShadowRootMode.Open : ShadowRootMode.Closed });
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
                                    template.Remove();
                                }
                                catch (Exception dsdEx)
                                {
                                    EngineLogCompat.Warn($"[DSD] Failed to attach shadow root: {dsdEx.Message}", LogCategory.Rendering);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Declarative shadow DOM processing failed: {ex.Message}", LogCategory.Rendering); }

                // 4. Prewarm Images
                EngineLogCompat.Debug("[CustomHtmlEngine] Prewarming images...", LogCategory.Rendering);
                try { await PrewarmImagesAsync((dom as Element) ?? (dom as Document)?.DocumentElement, baseUri, imageLoader, viewportWidth).ConfigureAwait(false); } catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] PrewarmImages invocation failed: {ex.Message}", LogCategory.Rendering); }

                // 5. Setup Javascript
                bool allowJs = EnableJavaScript;
                if (forceJavascript.HasValue) allowJs = forceJavascript.Value;

                if (preferFallbackDom)
                {
                    allowJs = false;
                }

                if (allowJs && IsJsHeavyAppShell(dom, baseUri))
                {
                    EngineLogCompat.Debug($"[SAFE-MODE] Skipping JS for heavy app-shell page {baseUri}", LogCategory.Rendering);
                    allowJs = false;
                }

                // HTML spec Â§4.12.1: When scripting is enabled, <noscript> must not render.
                // Remove noscript elements entirely when JS is on to prevent their raw HTML-encoded
                // fallback content (scripts, styles, inline HTML strings) from leaking into the page.
                if (allowJs)
                {
                    try
                    {
                        var noscripts = dom.Descendants().OfType<Element>()
                            .Where(n => string.Equals(n.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var ns in noscripts) ns.Remove();
                        if (noscripts.Count > 0)
                            EngineLogCompat.Debug($"[CustomHtmlEngine] Removed {noscripts.Count} <noscript> element(s) (JS on â€” spec Â§4.12.1)", LogCategory.Rendering);
                    }
                    catch (Exception nsEx)
                    {
                        EngineLogCompat.Warn($"[CustomHtmlEngine] Failed to remove noscript elements: {nsEx.Message}", LogCategory.Rendering);
                    }
                }
                else
                {
                    // JS is disabled â€” noscript content should be visible.
                    // FIX: Google Search puts <style>table,div,span,p{display:none}</style> inside <noscript>.
                    // Since we render <noscript>, this style applies globally and hides everything.
                    // Remove only harmful <style> tags from <noscript> when keeping noscript visible.
                    try
                    {
                        var fallbackDomMutated = false;
                        var noscriptElements = dom.Descendants().OfType<Element>()
                            .Where(n => string.Equals(n.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var ns in noscriptElements)
                        {
                            var stylesInNoscript = ns.Descendants().OfType<Element>()
                                .Where(s => string.Equals(s.TagName, "style", StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            foreach (var style in stylesInNoscript)
                            {
                                EngineLogCompat.Debug($"[CustomHtmlEngine] Removing harmful <style> from <noscript>", LogCategory.Rendering);
                                style.Remove();
                                fallbackDomMutated = true;
                            }
                        }

                        var promotedFallbacks = PromoteHiddenFallbackContent(dom, baseUri);
                        if (promotedFallbacks > 0)
                        {
                            var removedNoscriptBootstrap = RemoveEncodedNoscriptBootstrapFallbacks(noscriptElements);
                            fallbackDomMutated = true;
                            EngineLogCompat.Debug(
                                $"[CustomHtmlEngine] Promoted {promotedFallbacks} hidden fallback block(s); removed {removedNoscriptBootstrap} encoded <noscript> bootstrap block(s)",
                                LogCategory.Rendering);
                        }

                        if (fallbackDomMutated)
                        {
                            await LoadCssAsync((dom as Element) ?? (dom as Document)?.DocumentElement, baseUri, fetchExternalCssAsync);
                            EngineLogCompat.Debug("[CustomHtmlEngine] Recomputed CSS after fallback DOM sanitization", LogCategory.Rendering);
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Warn($"[CustomHtmlEngine] Failed to sanitize noscript: {ex.Message}", LogCategory.Rendering);
                    }
                }

                _activeJs = SetupJavaScriptEngine(baseUri, onNavigate, allowJs, fetchExternalCssAsync, viewportWidth, viewportHeight);
                if (_activeJs != null && _historyBridge != null) _activeJs.SetHistoryBridge(_historyBridge);
                EngineLogCompat.Debug($"[PERF] JS Setup: {_pageLoadStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

                var cssFetcher = fetchExternalCssAsync ?? (async _ => { await Task.CompletedTask; return string.Empty; });
                await CaptureActiveContextAsync(dom as Element, baseUri, cssFetcher, imageLoader, onNavigate, viewportWidth, viewportHeight, onFixedBackground, _activeJs).ConfigureAwait(false);

                EngineLogCompat.Debug("[CustomHtmlEngine] Calling BuildVisualTreeAsync...", LogCategory.Rendering);
                var vh = viewportHeight ?? _activeViewportHeight ?? GetPrimaryWindowHeight();
                var control = await BuildVisualTreeAsync(dom as Element, baseUri, cssFetcher, imageLoader, onNavigate, _activeJs, viewportWidth, vh, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                elapsed = _pageLoadStopwatch.ElapsedMilliseconds;
                initialVisualTreeMs = Math.Max(0, elapsed - lastStageMarkMs);
                lastStageMarkMs = elapsed;
                EngineLogCompat.Debug($"[PERF] Visual Tree 1: {elapsed}ms", LogCategory.Rendering);

                // 6. Run Scripts
                object element = control; 
                if (_activeJs != null)
                {
                    javascriptExecuted = true;
                    await RunScriptsAsync(_activeJs, dom as Element, baseUri);
                    var postScriptGoogleSanitized = ForceGoogleChallengeBannerVisible(dom, baseUri, removeUnhideScript: false);
                    if (postScriptGoogleSanitized > 0)
                    {
                        EngineLogCompat.Info($"[CustomHtmlEngine] GoogleChallengeBannerForcedVisible postScriptChanges={postScriptGoogleSanitized}", LogCategory.Rendering);
                    }
                    if (NeedsPostScriptStyleRefresh(dom, LastComputedStyles))
                    {
                        EngineLogCompat.Debug("[RenderAsync] Recomputing CSS after script-driven DOM/style mutations", LogCategory.Rendering);
                        await LoadCssAsync((dom as Element) ?? (dom as Document)?.DocumentElement, baseUri, fetchExternalCssAsync);
                    }
                    elapsed = _pageLoadStopwatch.ElapsedMilliseconds;
                    scriptExecutionMs = Math.Max(0, elapsed - lastStageMarkMs);
                    lastStageMarkMs = elapsed;
                    EngineLogCompat.Debug($"[PERF] Script Run: {elapsed}ms", LogCategory.Rendering);
                    // Re-build visual tree after scripts
                    EngineLogCompat.Debug("[RenderAsync] Re-building visual tree...", LogCategory.Rendering);
                    try
                    {
                        var vh2 = viewportHeight ?? _activeViewportHeight ?? GetPrimaryWindowHeight();
                        element = await BuildVisualTreeAsync(dom as Element, baseUri, cssFetcher, imageLoader, onNavigate, _activeJs, viewportWidth, vh2, onFixedBackground, includeDiagnosticsBanner: false).ConfigureAwait(false);
                        elapsed = _pageLoadStopwatch.ElapsedMilliseconds;
                        postScriptVisualTreeMs = Math.Max(0, elapsed - lastStageMarkMs);
                        lastStageMarkMs = elapsed;
                        EngineLogCompat.Debug($"[PERF] Visual Tree 2: {elapsed}ms", LogCategory.Rendering);
                        // Fire repaint so the engine loop re-renders with post-script DOM state.
                        OnRepaintReady(_activeDom);
                    }
                    catch (Exception vtEx)
                    {
                        EngineLogCompat.Error($"[RenderAsync] Visual tree error: {vtEx}", LogCategory.Rendering);
                        return null;
                    }
                }
                else
                {
                     EngineLogCompat.Debug($"[RenderAsync] Scripts SKIPPED (allowJs={allowJs}) element={control!=null}", LogCategory.Rendering);
                }
                return element;
            }
            finally
            {
                var totalRenderMs = _pageLoadStopwatch.ElapsedMilliseconds;
                LastRenderTelemetry = new RenderTelemetrySnapshot
                {
                    TokenizingMs = tokenizingMs,
                    ParsingMs = parsingMs,
                    TokenizingAndParsingMs = tokenizingAndParsingMs,
                    ParseTokenCount = parseTokenCount,
                    TokenizingCheckpointCount = tokenizingCheckpointCount,
                    ParsingCheckpointCount = parsingCheckpointCount,
                    ParsingDocumentCheckpointCount = parsingDocumentCheckpointCount,
                    DocumentReadyTokenCount = documentReadyTokenCount,
                    ParseIncrementalRepaintCount = incrementalParseRepaintCount,
                    StreamingPreparseMs = streamingPreparseMs,
                    StreamingPreparseCheckpointCount = streamingPreparseCheckpointCount,
                    StreamingPreparseRepaintCount = streamingPreparseRepaintCount,
                    InterleavedParseUsed = interleavedParseUsed,
                    InterleavedTokenBatchSize = interleavedTokenBatchSize,
                    InterleavedBatchCount = interleavedBatchCount,
                    InterleavedFallbackUsed = interleavedFallbackUsed,
                    CssAndStyleMs = cssAndStyleMs,
                    InitialVisualTreeMs = initialVisualTreeMs,
                    ScriptExecutionMs = scriptExecutionMs,
                    PostScriptVisualTreeMs = postScriptVisualTreeMs,
                    TotalRenderMs = totalRenderMs,
                    JavaScriptExecuted = javascriptExecuted
                };
                EngineLogCompat.Debug($"[PERF] FULL PAGE LOAD TIME: {totalRenderMs}ms", LogCategory.Rendering);
                await RaiseLoadingChangedAsync(false);
            }
        }

        private async Task<long> RunStreamingPreparseAsync(string html, Action<int> checkpointCountUpdated, Action<int> repaintCountUpdated)
        {
            if (string.IsNullOrEmpty(html))
            {
                checkpointCountUpdated?.Invoke(0);
                repaintCountUpdated?.Invoke(0);
                return 0;
            }

            var checkpointCount = 0;
            var repaintCount = 0;
            var parseStopwatch = Stopwatch.StartNew();
            try
            {
                using var parser = new StreamingHtmlParser(html);
                await parser.ParseIncrementallyAsync(document =>
                {
                    checkpointCount++;
                    if (TryEmitStreamingParseRepaint(document, checkpointCount, ref repaintCount))
                    {
                        repaintCountUpdated?.Invoke(repaintCount);
                    }
                    checkpointCountUpdated?.Invoke(checkpointCount);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[RenderAsync] Streaming preparse failed: {ex.Message}", LogCategory.Rendering);
            }

            checkpointCountUpdated?.Invoke(checkpointCount);
            repaintCountUpdated?.Invoke(repaintCount);
            parseStopwatch.Stop();
            return parseStopwatch.ElapsedMilliseconds;
        }

        private bool TryEmitStreamingParseRepaint(Document document, int checkpointOrdinal, ref int repaintCount)
        {
            if (document?.DocumentElement == null)
            {
                return false;
            }

            if (!HasStableComputedStyleSnapshot())
            {
                return false;
            }

            if (!ShouldEmitStreamingPreparseRepaint(checkpointOrdinal, repaintCount))
            {
                return false;
            }

            Element snapshotRoot = null;
            try
            {
                snapshotRoot = document.DocumentElement.CloneNode(true) as Element;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[RenderAsync] Streaming preparse snapshot clone failed: {ex.Message}", LogCategory.Rendering);
                return false;
            }

            if (snapshotRoot == null)
            {
                return false;
            }

            repaintCount++;
            SetActiveDom(snapshotRoot, markSnapshotUnstable: true);
            // Do NOT null out LastComputedStyles here. The engine loop polls for styles
            // and nulling them causes hundreds of wasted render frames with Styles=NULL
            // before the CSS cascade completes. Keep previous styles visible so layout
            // can at least partially render with whatever styles were last computed.
            OnRepaintReady(snapshotRoot);
            return true;
        }

        private void TryEmitIncrementalParseRepaint(Document document, HtmlParseCheckpoint checkpoint, int parsingCheckpointOrdinal, ref int incrementalRepaintCount)
        {
            if (!EnableIncrementalParseRepaint ||
                document?.DocumentElement == null ||
                checkpoint == null ||
                checkpoint.Phase != HtmlParseBuildPhase.Parsing)
            {
                return;
            }

            if (!HasStableComputedStyleSnapshot())
            {
                return;
            }

            if (!ShouldEmitIncrementalParseRepaint(parsingCheckpointOrdinal, checkpoint.IsFinal, incrementalRepaintCount))
            {
                return;
            }

            Element snapshotRoot = null;
            try
            {
                snapshotRoot = document.DocumentElement.CloneNode(true) as Element;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[RenderAsync] Incremental parse snapshot clone failed: {ex.Message}", LogCategory.Rendering);
                return;
            }

            if (snapshotRoot == null)
            {
                return;
            }

            incrementalRepaintCount++;
            SetActiveDom(snapshotRoot, markSnapshotUnstable: true);
            // Do NOT null out LastComputedStyles — see TryEmitStreamingParseRepaint comment.
            OnRepaintReady(snapshotRoot);
        }

        private static bool ShouldEmitIncrementalParseRepaint(int parsingCheckpointOrdinal, bool isFinalCheckpoint, int incrementalRepaintCount)
        {
            if (incrementalRepaintCount >= IncrementalParseRepaintMaxCount)
            {
                return false;
            }

            if (incrementalRepaintCount == 0)
            {
                return true;
            }

            if (isFinalCheckpoint)
            {
                return true;
            }

            return parsingCheckpointOrdinal > 0 &&
                (parsingCheckpointOrdinal % IncrementalParseRepaintCheckpointStride) == 0;
        }

        private static bool ShouldRunStreamingParsePrepass(bool enabled, int htmlLength)
        {
            // The streaming preparse is a progressive hint pass, not the source of truth.
            // Bound it to mid-sized documents so it cannot stall first paint on very large pages.
            return enabled &&
                htmlLength >= StreamingPreparseMinHtmlLength &&
                htmlLength <= StreamingPreparseMaxHtmlLength;
        }

        private static int ResolveInterleavedTokenBatchSize(bool enabled, int htmlLength)
        {
            if (!enabled || htmlLength < InterleavedPrimaryParseMinHtmlLength)
            {
                return 0;
            }

            if (htmlLength >= 524288)
            {
                return 512;
            }

            if (htmlLength >= 131072)
            {
                return 256;
            }

            return 128;
        }

        private void AttachParseDocumentCheckpointCallback(
            FenBrowser.Core.Parsing.HtmlTreeBuilder builder,
            ParseCheckpointState parseCheckpointState)
        {
            if (builder == null || parseCheckpointState == null)
            {
                return;
            }

            builder.ParseDocumentCheckpointCallback = (document, checkpoint) =>
            {
                if (checkpoint != null && checkpoint.Phase == HtmlParseBuildPhase.Parsing)
                {
                    parseCheckpointState.ParsingDocumentCheckpointCount++;
                    parseCheckpointState.ParsingCheckpointOrdinal++;
                    var repaintCount = parseCheckpointState.IncrementalRepaintCount;
                    TryEmitIncrementalParseRepaint(document, checkpoint, parseCheckpointState.ParsingCheckpointOrdinal, ref repaintCount);
                    parseCheckpointState.IncrementalRepaintCount = repaintCount;
                }
            };
        }

        private static bool ShouldEmitStreamingPreparseRepaint(int checkpointOrdinal, int repaintCount)
        {
            if (repaintCount >= StreamingPreparseRepaintMaxCount)
            {
                return false;
            }

            if (repaintCount == 0)
            {
                return true;
            }

            return checkpointOrdinal > 0 &&
                (checkpointOrdinal % StreamingPreparseRepaintCheckpointStride) == 0;
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
            Action<object>? onFixedBackground = null)
        {
            try { var _ = RenderAsync(html, baseUri, fetchExternalCssAsync, imageLoader, onNavigate, viewportWidth, null, onFixedBackground); }
            catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Fire-and-forget render launch failed: {ex.Message}", LogCategory.Rendering); }
        }

        /// <summary>Expose the current active Lite DOM (last parsed).</summary>
        public Node GetActiveDom()
        {
            lock (_renderStateLock)
            {
                return _activeDom;
            }
        }

        public BrowserRenderSnapshot GetRenderSnapshot()
        {
            lock (_renderStateLock)
            {
                var root = (_activeDom as Element) ?? (_activeDom as Document)?.DocumentElement;
                return new BrowserRenderSnapshot(
                    root,
                    LastComputedStyles,
                    _renderSnapshotVersion,
                    _hasStableStyles);
            }
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
                foreach (var entry in CookieJar.Snapshot(u, _activeBaseUri ?? u))
                {
                    if (!dict.ContainsKey(entry.Key)) dict[entry.Key] = entry.Value ?? string.Empty;
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] Cookie snapshot failed: {ex.Message}", LogCategory.Rendering); }
            return dict;
        }

        public void SetCookie(Uri scope, string name, string value, string path = "/")
        {
            try
            {
                var u = scope ?? _activeBaseUri; if (u == null) return;
                CookieJar.SetDocumentCookie(
                    u,
                    $"{name ?? string.Empty}={value ?? string.Empty}; Path={path ?? "/"}",
                    _activeBaseUri ?? u,
                    BrowserSettings.Instance.BlockThirdPartyCookies);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] SetCookie failed: {ex.Message}", LogCategory.Rendering); }
        }

        public void DeleteCookie(Uri scope, string name)
        {
            try
            {
                var u = scope ?? _activeBaseUri; if (u == null) return;
                CookieJar.DeleteDocumentCookie(u, name, _activeBaseUri ?? u);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[CustomHtmlEngine] DeleteCookie failed: {ex.Message}", LogCategory.Rendering); }
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
                EngineLogCompat.Error("[CustomHtmlEngine] ExecuteScriptAsync: _activeJs is NULL", LogCategory.JavaScript);
                return "undefined";
            }
            EngineLogCompat.Debug($"[CustomHtmlEngine] Executing script on active engine: {script}", LogCategory.JavaScript);
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
        private void DumpTree(FenBrowser.Core.Dom.V2.Node node, StringBuilder sb, int depth)
        {
            sb.Append(new string(' ', depth * 2));
            sb.Append(node is Element el1 ? el1.TagName : node.NodeName);
            if (node is Element el)
            {
                if (el.Attributes != null)
                {
                    foreach (var attr in el.Attributes)
                        sb.Append($" {attr.Name}='{attr.Value}'");
                }
            }
            if (node is Text txt)
            {
                sb.Append($" \"{(txt.Data ?? "").Replace("\n", "\\n").Replace("\r", "\\r")}\"");
            }
            sb.AppendLine();
            if (node.ChildNodes != null)
            {
               foreach(var child in node.ChildNodes) DumpTree(child, sb, depth + 1);
            }
        }
    }
}










