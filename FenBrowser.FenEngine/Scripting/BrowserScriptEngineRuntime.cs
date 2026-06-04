using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Parsing;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Host;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Browser-facing runtime seam for page script execution. This keeps the browser
/// pipeline independent from the concrete JS engine so FenJS can replace the
/// legacy runtime without rewriting every rendering/navigation call site.
/// </summary>
public interface IBrowserScriptEngine
{
    IExecutionContext GlobalContext { get; }
    JavaScriptRuntimeProfile RuntimeProfile { get; }
    Func<Uri, Task<string>> FetchOverride { get; set; }
    Func<Uri, string, bool> SubresourceAllowed { get; set; }
    Func<string, bool> NonceAllowed { get; set; }
    Func<HttpRequestMessage, Task<HttpResponseMessage>> FetchHandler { get; set; }
    Func<Uri, string> CookieReadBridge { get; set; }
    Action<Uri, string> CookieWriteBridge { get; set; }
    Action RequestRender { get; set; }
    Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }
    SandboxPolicy Sandbox { get; set; }
    bool AllowExternalScripts { get; set; }
    bool ExecuteInlineScriptsOnInnerHTML { get; set; }
    double WindowWidth { get; set; }
    double WindowHeight { get; set; }
    int PageScriptByteBudget { get; set; }
    event Func<string, JsPermissions, Task<bool>> PermissionRequested;

    void CaptureNavigationGlobals(Uri documentUri, long navigationId);
    void SetHistoryBridge(IHistoryBridge bridge);
    void NotifyPopState(object state);
    void DispatchEventForElement(Element element, string eventName);
    object Evaluate(string script);
    void SyncDomContext(Node domRoot, Uri baseUri = null);
    Task SetDomAsync(Node domRoot, Uri baseUri = null);
}

/// <summary>
/// Default adapter that preserves existing browser behavior while the browser
/// migration moves from FenEngine's legacy runtime to a FenJS-backed runtime.
/// </summary>
public sealed class LegacyBrowserScriptEngineAdapter : IBrowserScriptEngine
{
    private readonly JavaScriptEngine _inner;

    public LegacyBrowserScriptEngineAdapter(JavaScriptEngine inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public JavaScriptEngine Inner => _inner;
    public IExecutionContext GlobalContext => _inner.GlobalContext;
    public JavaScriptRuntimeProfile RuntimeProfile => _inner.RuntimeProfile;
    public Func<Uri, Task<string>> FetchOverride { get => _inner.FetchOverride; set => _inner.FetchOverride = value; }
    public Func<Uri, string, bool> SubresourceAllowed { get => _inner.SubresourceAllowed; set => _inner.SubresourceAllowed = value; }
    public Func<string, bool> NonceAllowed { get => _inner.NonceAllowed; set => _inner.NonceAllowed = value; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> FetchHandler { get => _inner.FetchHandler; set => _inner.FetchHandler = value; }
    public Func<Uri, string> CookieReadBridge { get => _inner.CookieReadBridge; set => _inner.CookieReadBridge = value; }
    public Action<Uri, string> CookieWriteBridge { get => _inner.CookieWriteBridge; set => _inner.CookieWriteBridge = value; }
    public Action RequestRender { get => _inner.RequestRender; set => _inner.RequestRender = value; }
    public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get => _inner.ExternalScriptFetcher; set => _inner.ExternalScriptFetcher = value; }
    public SandboxPolicy Sandbox { get => _inner.Sandbox; set => _inner.Sandbox = value; }
    public bool AllowExternalScripts { get => _inner.AllowExternalScripts; set => _inner.AllowExternalScripts = value; }
    public bool ExecuteInlineScriptsOnInnerHTML { get => _inner.ExecuteInlineScriptsOnInnerHTML; set => _inner.ExecuteInlineScriptsOnInnerHTML = value; }
    public double WindowWidth { get => _inner.WindowWidth; set => _inner.WindowWidth = value; }
    public double WindowHeight { get => _inner.WindowHeight; set => _inner.WindowHeight = value; }
    public int PageScriptByteBudget { get => _inner.PageScriptByteBudget; set => _inner.PageScriptByteBudget = value; }

    public event Func<string, JsPermissions, Task<bool>> PermissionRequested
    {
        add => _inner.PermissionRequested += value;
        remove => _inner.PermissionRequested -= value;
    }

    public void SetHistoryBridge(IHistoryBridge bridge) => _inner.SetHistoryBridge(bridge);
    public void CaptureNavigationGlobals(Uri documentUri, long navigationId) => NavigationGlobalsProbe.Capture(_inner, documentUri, navigationId);
    public void NotifyPopState(object state) => _inner.NotifyPopState(state);
    public void DispatchEventForElement(Element element, string eventName) => _inner.DispatchEventForElement(element, eventName);
    public object Evaluate(string script) => _inner.Evaluate(script);
    public void SyncDomContext(Node domRoot, Uri baseUri = null) => _inner.SyncDomContext(domRoot, baseUri);
    public Task SetDomAsync(Node domRoot, Uri baseUri = null) => _inner.SetDomAsync(domRoot, baseUri);
}

/// <summary>
/// Safe integration step for FenJS inside the browser runtime seam.
/// DOM/document lifecycle still delegates to the legacy engine until FenJS
/// host-object wiring covers the browser surface, but standalone pre-DOM eval
/// can execute through FenJS when explicitly selected.
/// </summary>
public sealed class FenJsBrowserScriptEngine : IBrowserScriptEngine
{
    private readonly LegacyBrowserScriptEngineAdapter _legacy;
    private readonly object _fenJsLock = new();
    private readonly ConcurrentDictionary<long, Timer> _fenJsTimers = new();
    private long _fenJsTimerIdCounter;
    private JsValue _fenJsGlobalThis = JsValue.Undefined;
    private readonly System.Diagnostics.Stopwatch _fenJsClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly BrowserFenJsHostHooks _hostHooks = new();
    private readonly NavigationEpoch _navigationEpoch = NavigationEpoch.Initial;
    private readonly Dictionary<object, HostObjectHandle> _hostHandleCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<BrowserEventListener> _documentEventListeners = new();
    private readonly List<BrowserEventListener> _windowEventListeners = new();
    private ConditionalWeakTable<object, Dictionary<string, JsValue>> _hostCallableCache = new();
    private ConditionalWeakTable<object, Dictionary<string, JsValue>> _hostPropertyStore = new();
    private ConditionalWeakTable<object, List<BrowserEventListener>> _elementEventListeners = new();
    private long _temporaryFenJsGlobalCounter;
    private BytecodeCompiler _compiler;
    private BytecodeInterpreter _interpreter;
    private DocumentEpoch _documentEpoch = DocumentEpoch.Initial;
    private Node _currentDomRoot;
    private Uri _currentBaseUri;
    private Element _currentScriptElement;
    private string _documentReadyState = "loading";
    private int _fenJsEvaluationCount;
    private int _legacyFallbackCount;

    public FenJsBrowserScriptEngine(IJsHost host)
    {
        _legacy = new LegacyBrowserScriptEngineAdapter(new JavaScriptEngine(host));
        ResetFenJsSession();
    }

    public JavaScriptEngine LegacyInner => _legacy.Inner;
    internal int FenJsEvaluationCount => _fenJsEvaluationCount;
    internal int LegacyFallbackCount => _legacyFallbackCount;
    public IExecutionContext GlobalContext => _legacy.GlobalContext;
    public JavaScriptRuntimeProfile RuntimeProfile => _legacy.RuntimeProfile;
    public Func<Uri, Task<string>> FetchOverride { get => _legacy.FetchOverride; set => _legacy.FetchOverride = value; }
    public Func<Uri, string, bool> SubresourceAllowed { get => _legacy.SubresourceAllowed; set => _legacy.SubresourceAllowed = value; }
    public Func<string, bool> NonceAllowed { get => _legacy.NonceAllowed; set => _legacy.NonceAllowed = value; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> FetchHandler { get => _legacy.FetchHandler; set => _legacy.FetchHandler = value; }
    public Func<Uri, string> CookieReadBridge { get => _legacy.CookieReadBridge; set => _legacy.CookieReadBridge = value; }
    public Action<Uri, string> CookieWriteBridge { get => _legacy.CookieWriteBridge; set => _legacy.CookieWriteBridge = value; }
    public Action RequestRender { get => _legacy.RequestRender; set => _legacy.RequestRender = value; }
    public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get => _legacy.ExternalScriptFetcher; set => _legacy.ExternalScriptFetcher = value; }
    public SandboxPolicy Sandbox { get => _legacy.Sandbox; set => _legacy.Sandbox = value; }
    public bool AllowExternalScripts { get => _legacy.AllowExternalScripts; set => _legacy.AllowExternalScripts = value; }
    public bool ExecuteInlineScriptsOnInnerHTML { get => _legacy.ExecuteInlineScriptsOnInnerHTML; set => _legacy.ExecuteInlineScriptsOnInnerHTML = value; }
    public double WindowWidth { get => _legacy.WindowWidth; set => _legacy.WindowWidth = value; }
    public double WindowHeight { get => _legacy.WindowHeight; set => _legacy.WindowHeight = value; }
    public int PageScriptByteBudget { get => _legacy.PageScriptByteBudget; set => _legacy.PageScriptByteBudget = value; }

    public event Func<string, JsPermissions, Task<bool>> PermissionRequested
    {
        add => _legacy.PermissionRequested += value;
        remove => _legacy.PermissionRequested -= value;
    }

    public void SetHistoryBridge(IHistoryBridge bridge) => _legacy.SetHistoryBridge(bridge);
    public void CaptureNavigationGlobals(Uri documentUri, long navigationId) => _legacy.CaptureNavigationGlobals(documentUri, navigationId);
    public void NotifyPopState(object state) => _legacy.NotifyPopState(state);
    public void DispatchEventForElement(Element element, string eventName) => _legacy.DispatchEventForElement(element, eventName);

    public object Evaluate(string script)
    {
        if (CanEvaluateWithFenJs(script))
        {
            try
            {
                return EvaluateWithFenJs(script);
            }
            catch
            {
                // Fall back to the legacy browser runtime whenever the FenJS preview
                // path cannot safely answer yet. This keeps product behavior stable
                // while the DOM/host-object bridge is being migrated.
                _legacyFallbackCount++;
            }
        }

        return _legacy.Evaluate(script);
    }

    public void SyncDomContext(Node domRoot, Uri baseUri = null)
    {
        _legacy.SyncDomContext(domRoot, baseUri);

        if (domRoot != null)
        {
            BindFenJsDomContext(domRoot, baseUri, documentReadyState: "loading");
        }
    }

    public Task SetDomAsync(Node domRoot, Uri baseUri = null)
    {
        return SetDomAsyncCore(domRoot, baseUri);
    }

    private async Task SetDomAsyncCore(Node domRoot, Uri baseUri)
    {
        _legacy.SyncDomContext(domRoot, baseUri);

        if (domRoot == null)
        {
            return;
        }

        BindFenJsDomContext(domRoot, baseUri, documentReadyState: "loading");
        await ExecutePageScriptsWithFenJsAsync(domRoot, baseUri).ConfigureAwait(false);
        ApplyScriptingEnabledSanitizer(domRoot);
        DispatchStartupLifecycleEvents();
    }

    private bool CanEvaluateWithFenJs(string script)
    {
        return !string.IsNullOrWhiteSpace(script);
    }

    private static long ResolveFenJsScriptTimeoutMs()
    {
        const long defaultTimeoutMs = 30000;
        var raw = Environment.GetEnvironmentVariable("FEN_FENJS_SCRIPT_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return parsed;
        }

        return defaultTimeoutMs;
    }

    private object EvaluateWithFenJs(string script)
    {
        return ConvertFenJsValue(EvaluateWithFenJsRaw(script));
    }

    private JsValue EvaluateWithFenJsRaw(string script)
    {
        return RunFenJsWithLargeStack(() =>
        {
            lock (_fenJsLock)
            {
                _fenJsEvaluationCount++;
                var function = _compiler.CompileScript(new SourceText(script, "<fenbrowser-fenjs-eval>"));
                new BytecodeVerifier().Verify(function);
                return _interpreter.Execute(function);
            }
        });
    }

    // The recursive-descent parser, the bytecode compiler, and the interpreter all
    // recurse with the AST/call depth. Real-world minified bundles (x.com's main.js is
    // 1.4 MB) nest expressions thousands deep and blow the ~1 MB stack of a threadpool
    // thread — the path page scripts run on. FenBrowser.Host already re-enters its main
    // loop on a 16 MB thread for exactly this reason; mirror that here so every FenJS
    // compile/execute gets a fat stack. A ThreadStatic flag makes re-entrant calls (a
    // native callback that evaluates more script) run inline instead of spawning — and,
    // critically, avoids dead-locking on _fenJsLock which the outer worker already holds.
    [ThreadStatic] private static bool _onFenJsLargeStackThread;
    // 256 MB: real-world minified bundles nest very deeply. The parser builds
    // operator chains iteratively, but the compiler's tree-walk recurses — and
    // x.com's i18n bundle compiles an ~8800-deep left-associative chain (16 MB
    // overflowed at ~1550). Give the compile/execute thread a fat stack; the
    // compiler's TryEnsureSufficientExecutionStack guard still aborts catchably
    // if even this is exceeded, rather than crashing the process.
    private const int FenJsLargeStackBytes = 256 * 1024 * 1024;

    private T RunFenJsWithLargeStack<T>(Func<T> work)
    {
        if (_onFenJsLargeStackThread)
        {
            return work();
        }

        T result = default;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo captured = null;
        var thread = new Thread(
            () =>
            {
                _onFenJsLargeStackThread = true;
                try { result = work(); }
                catch (Exception ex) { captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            },
            FenJsLargeStackBytes)
        {
            IsBackground = true,
            Name = "FenJs-LargeStack"
        };
        thread.Start();
        thread.Join();
        captured?.Throw();
        return result;
    }

    private async Task ExecutePageScriptsWithFenJsAsync(Node domRoot, Uri baseUri)
    {
        foreach (var scriptElement in EnumerateScriptElements(domRoot))
        {
            var type = scriptElement.GetAttribute("type")?.ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(type) &&
                type != "text/javascript" &&
                type != "application/javascript" &&
                type != "module")
            {
                continue;
            }

            if (scriptElement.HasAttribute("nomodule"))
            {
                continue;
            }

            var src = scriptElement.GetAttribute("src");
            string code = null;

            if (!string.IsNullOrEmpty(src))
            {
                if (!AllowExternalScripts || !Sandbox.Allows(SandboxFeature.ExternalScripts))
                {
                    continue;
                }

                if (baseUri == null || !Uri.TryCreate(baseUri, src, out var scriptUri))
                {
                    continue;
                }

                if (SubresourceAllowed != null && !SubresourceAllowed(scriptUri, "script"))
                {
                    continue;
                }

                code = await FetchExternalPageScriptAsync(scriptUri, baseUri).ConfigureAwait(false);
            }
            else
            {
                if (!Sandbox.Allows(SandboxFeature.InlineScripts))
                {
                    continue;
                }

                code = CollectScriptText(scriptElement);
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            try
            {
                SetCurrentScriptElement(scriptElement);
                EvaluateWithFenJsRaw(code);
            }
            catch (Exception fenJsEx)
            {
                _legacyFallbackCount++;
                var snippet = code.Length > 140 ? code.Substring(0, 140) : code;
                snippet = snippet.Replace("\n", " ").Replace("\r", " ");
                var detail = fenJsEx.Message;
                if (fenJsEx is FenBrowser.Js.Interpreter.JsThrownException jte && _interpreter != null)
                {
                    try { detail = "thrown=> " + _interpreter.DescribeThrownValue(jte.Value); }
                    catch { /* diagnostics must never mask the original failure */ }
                }
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] Page script failed on FenJS, falling back to legacy engine " +
                    $"(len={code.Length}, src='{scriptElement.GetAttribute("src")}'): {detail} :: {snippet}",
                    FenBrowser.Core.Logging.LogCategory.JavaScript);
                _legacy.Evaluate(code);
            }
            finally
            {
                SetCurrentScriptElement(null);
            }
        }
    }

    private async Task<string> FetchExternalPageScriptAsync(Uri scriptUri, Uri referer)
    {
        if (scriptUri == null)
        {
            return null;
        }

        if (ExternalScriptFetcher != null)
        {
            return await ExternalScriptFetcher(scriptUri, referer).ConfigureAwait(false);
        }

        if (FetchOverride != null)
        {
            return await FetchOverride(scriptUri).ConfigureAwait(false);
        }

        if (FetchHandler == null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
        if (referer != null)
        {
            request.Headers.Referrer = referer;
            if (!CorsHandler.IsSameOrigin(scriptUri, referer))
            {
                var origin = CorsHandler.SerializeOrigin(new UriBuilder(
                    referer.Scheme,
                    referer.Host,
                    referer.IsDefaultPort ? -1 : referer.Port).Uri);
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                }
            }
        }

        using var response = await FetchHandler(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private void ResetFenJsSession()
    {
        // Establish the large-stack worker BEFORE taking _fenJsLock. ResetFenJsSession
        // transitively calls InstallFenJsDomGlobals -> EvaluateWithFenJsRaw, which spawns
        // the large-stack thread. If we held _fenJsLock on a plain render thread and then
        // spawned, the worker would block forever on the lock the joining caller still
        // holds (Monitor is not cross-thread reentrant) -> deadlock. Acquiring the fat
        // stack first means the nested EvaluateWithFenJsRaw sees _onFenJsLargeStackThread
        // and runs inline, so the lock is taken once, on a single thread, reentrantly.
        RunFenJsWithLargeStack<object>(() =>
        {
        lock (_fenJsLock)
        {
            _compiler = new BytecodeCompiler();
            _interpreter = new BytecodeInterpreter
            {
                HostObjectTable = new HostObjectTable(),
                HostHooks = _hostHooks,
                HostResolveContext = new HostObjectResolveContext(
                    CurrentRealmId: 0,
                    CurrentDocumentEpoch: _documentEpoch,
                    CurrentNavigationEpoch: _navigationEpoch),
                // Bound per-invocation execution so a runaway or pathologically slow
                // page script (or one our interpreter mis-evaluates into a spin loop)
                // surfaces as a catchable RangeError instead of freezing the browser
                // thread forever. Override via FEN_FENJS_SCRIPT_TIMEOUT_MS.
                WallClockTimeoutMs = ResolveFenJsScriptTimeoutMs()
            };

            // The generational nursery GC (tier-4 scaffold) does not yet root every
            // transient handle that real-world bundles keep live across an allocation
            // burst, so an auto-MinorCollect fired mid-bundle can reclaim a still-reachable
            // object and resurface as "Stale heap handle." — which aborts page boot for
            // allocation-heavy SPAs like x.com (its webpack runtime trips it immediately).
            // Until the missing roots are tracked down, disable the auto-trigger for the
            // browser page-script interpreter: a single page load does not need nursery
            // sweeps, and correctness here matters far more than reclaiming young cells.
            _interpreter.Heap.YoungAllocationsPerMinorGc = 0;

            _hostHandleCache.Clear();
            _documentEventListeners.Clear();
            _windowEventListeners.Clear();
            _hostCallableCache = new ConditionalWeakTable<object, Dictionary<string, JsValue>>();
            _hostPropertyStore = new ConditionalWeakTable<object, Dictionary<string, JsValue>>();
            _elementEventListeners = new ConditionalWeakTable<object, List<BrowserEventListener>>();
            _hostHooks.Reset();

            if (_currentDomRoot != null)
            {
                InstallFenJsDomGlobals(_currentDomRoot, _currentBaseUri);
            }
        }
            return null;
        });
    }

    private static object ConvertFenJsValue(FenBrowser.Js.Runtime.JsValue value)
    {
        return value.Tag switch
        {
            FenBrowser.Js.Runtime.JsValueTag.Undefined => null,
            FenBrowser.Js.Runtime.JsValueTag.Null => null,
            FenBrowser.Js.Runtime.JsValueTag.Boolean => value.AsBoolean(),
            FenBrowser.Js.Runtime.JsValueTag.Int32 => value.AsInt32(),
            FenBrowser.Js.Runtime.JsValueTag.Number => value.AsNumber(),
            FenBrowser.Js.Runtime.JsValueTag.String => value.AsString(),
            FenBrowser.Js.Runtime.JsValueTag.Symbol => value.AsSymbolDescription() ?? "Symbol()",
            FenBrowser.Js.Runtime.JsValueTag.BigInt => value.AsBigInt().ToString(),
            _ => throw new InvalidOperationException("FenJS preview eval returned a host-bound or object result.")
        };
    }

    private void BindFenJsDomContext(Node domRoot, Uri baseUri, string documentReadyState)
    {
        // Acquire the large stack before _fenJsLock for the same reason as
        // ResetFenJsSession: the nested ResetFenJsSession -> InstallFenJsDomGlobals ->
        // EvaluateWithFenJsRaw must run inline on this worker rather than spawning a
        // second thread that deadlocks on the lock we hold here.
        RunFenJsWithLargeStack<object>(() =>
        {
        lock (_fenJsLock)
        {
            _currentDomRoot = domRoot;
            _currentBaseUri = baseUri;
            _documentReadyState = string.IsNullOrWhiteSpace(documentReadyState)
                ? "loading"
                : documentReadyState;
            _documentEpoch = _documentEpoch.Next();
            ResetFenJsSession();
        }
            return null;
        });
    }

    private void InstallFenJsDomGlobals(Node domRoot, Uri baseUri)
    {
        var document = domRoot as Document ?? domRoot.OwnerDocument;
        if (document == null)
        {
            return;
        }

        var navigator = CreateNavigatorHost();
        var location = new FenJsLocationHost(baseUri ?? TryCreateUri(document.URL));
        _hostHooks.Bind(
            this,
            document,
            navigator,
            location,
            baseUri ?? TryCreateUri(document.URL));

        _interpreter.RegisterGlobalHostObject("document", RegisterHostObject(document, HostObjectKind.DomDocument));
        _interpreter.RegisterGlobalHostObject("navigator", RegisterHostObject(navigator, HostObjectKind.Other));
        _interpreter.RegisterGlobalHostObject("location", RegisterHostObject(location, HostObjectKind.Other));
        _interpreter.RegisterGlobalValue("innerWidth", JsValue.FromNumber(WindowWidth));
        _interpreter.RegisterGlobalValue("innerHeight", JsValue.FromNumber(WindowHeight));
        _interpreter.RegisterGlobalValue("outerWidth", JsValue.FromNumber(WindowWidth));
        _interpreter.RegisterGlobalValue("outerHeight", JsValue.FromNumber(WindowHeight));

        var globalThisValue = EvaluateWithFenJsRaw("globalThis");
        _fenJsGlobalThis = globalThisValue;
        _interpreter.RegisterGlobalValue("window", globalThisValue);
        _interpreter.RegisterGlobalValue("self", globalThisValue);
        _interpreter.RegisterGlobalValue("top", globalThisValue);
        _interpreter.RegisterGlobalValue("parent", globalThisValue);
        _interpreter.RegisterGlobalValue(
            "addEventListener",
            _interpreter.AllocateNativeFunction(
                "addEventListener",
                (_, args) =>
                {
                    AddBrowserEventListener(_windowEventListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.RegisterGlobalValue(
            "removeEventListener",
            _interpreter.AllocateNativeFunction(
                "removeEventListener",
                (_, args) =>
                {
                    RemoveBrowserEventListener(_windowEventListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));

        InstallFenJsPerformance();
        InstallFenJsTimers();
        InstallFenJsBrowserConstructors();
        InstallFenJsNativeBrowserConstructors();
    }

    // W3C High Resolution Time / Performance Timeline. SPA frameworks (React, and
    // x.com's bootstrap specifically) call performance.now()/mark()/measure() during
    // hydration; a missing `performance` global throws ReferenceError and aborts the
    // app mount before any content renders. https://www.w3.org/TR/hr-time-3/
    private void InstallFenJsPerformance()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timeOrigin = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalMilliseconds
            - stopwatch.Elapsed.TotalMilliseconds;

        _interpreter.RegisterGlobalValue(
            "__fenPerformanceNow",
            _interpreter.AllocateNativeFunction(
                "now",
                (_, _) => JsValue.FromNumber(stopwatch.Elapsed.TotalMilliseconds),
                length: 0));
        _interpreter.RegisterGlobalValue("__fenPerformanceTimeOrigin", JsValue.FromNumber(timeOrigin));

        EvaluateWithFenJsRaw(
            """
            (function () {
                var nowFn = __fenPerformanceNow;
                var origin = __fenPerformanceTimeOrigin;
                var marks = Object.create(null);
                var entries = [];

                function pushEntry(name, entryType, startTime, duration) {
                    var e = {
                        name: String(name),
                        entryType: entryType,
                        startTime: startTime,
                        duration: duration,
                        toJSON: function () {
                            return { name: this.name, entryType: this.entryType, startTime: this.startTime, duration: this.duration };
                        }
                    };
                    entries.push(e);
                    return e;
                }

                var perf = {
                    now: function () { return nowFn(); },
                    timeOrigin: origin,
                    mark: function (name) {
                        var t = nowFn();
                        marks[name] = t;
                        return pushEntry(name, 'mark', t, 0);
                    },
                    measure: function (name, startMark, endMark) {
                        var s = (startMark != null && marks[startMark] != null) ? marks[startMark] : 0;
                        var e = (endMark != null && marks[endMark] != null) ? marks[endMark] : nowFn();
                        return pushEntry(name, 'measure', s, e - s);
                    },
                    clearMarks: function (name) {
                        if (name == null) { marks = Object.create(null); }
                        else { delete marks[name]; }
                    },
                    clearMeasures: function () {},
                    clearResourceTimings: function () {},
                    setResourceTimingBufferSize: function () {},
                    getEntries: function () { return entries.slice(); },
                    getEntriesByType: function (type) {
                        return entries.filter(function (e) { return e.entryType === type; });
                    },
                    getEntriesByName: function (name, type) {
                        return entries.filter(function (e) {
                            return e.name === String(name) && (type == null || e.entryType === type);
                        });
                    },
                    toJSON: function () { return { timeOrigin: origin }; }
                };

                perf.timing = {
                    navigationStart: origin, fetchStart: origin, domainLookupStart: origin,
                    domainLookupEnd: origin, connectStart: origin, connectEnd: origin,
                    secureConnectionStart: origin, requestStart: origin, responseStart: origin,
                    responseEnd: origin, domLoading: origin, domInteractive: origin,
                    domContentLoadedEventStart: origin, domContentLoadedEventEnd: origin,
                    domComplete: origin, loadEventStart: origin, loadEventEnd: origin,
                    unloadEventStart: 0, unloadEventEnd: 0, redirectStart: 0, redirectEnd: 0
                };
                perf.navigation = { type: 0, redirectCount: 0 };

                Object.defineProperty(globalThis, 'performance', {
                    value: perf, writable: true, configurable: true, enumerable: true
                });
            })();
            """);
    }

    // HTML timers + animation frames, executed on the SAME FenJS interpreter as page
    // scripts. Without these, any script calling setTimeout threw "setTimeout is not
    // defined" in FenJS and fell back to the legacy engine, whose global state is
    // disjoint — so deferred callbacks (every SPA bootstrap, incl. x.com) could not see
    // globals set by the page. Keeping callbacks on one interpreter preserves window
    // state across the whole page lifecycle. https://html.spec.whatwg.org/#timers
    private void InstallFenJsTimers()
    {
        _interpreter.RegisterGlobalValue(
            "setTimeout",
            _interpreter.AllocateNativeFunction(
                "setTimeout",
                (_, args) => ScheduleFenJsTimer(args, repeat: false),
                length: 1));
        _interpreter.RegisterGlobalValue(
            "setInterval",
            _interpreter.AllocateNativeFunction(
                "setInterval",
                (_, args) => ScheduleFenJsTimer(args, repeat: true),
                length: 1));
        _interpreter.RegisterGlobalValue(
            "clearTimeout",
            _interpreter.AllocateNativeFunction(
                "clearTimeout",
                (_, args) => ClearFenJsTimer(args),
                length: 1));
        _interpreter.RegisterGlobalValue(
            "clearInterval",
            _interpreter.AllocateNativeFunction(
                "clearInterval",
                (_, args) => ClearFenJsTimer(args),
                length: 1));
        _interpreter.RegisterGlobalValue(
            "requestAnimationFrame",
            _interpreter.AllocateNativeFunction(
                "requestAnimationFrame",
                (_, args) => ScheduleFenJsAnimationFrame(args),
                length: 1));
        _interpreter.RegisterGlobalValue(
            "cancelAnimationFrame",
            _interpreter.AllocateNativeFunction(
                "cancelAnimationFrame",
                (_, args) => ClearFenJsTimer(args),
                length: 1));
    }

    private JsValue ScheduleFenJsTimer(IReadOnlyList<JsValue> args, bool repeat)
    {
        if (args == null || args.Count == 0)
        {
            return JsValue.FromNumber(0);
        }

        var callback = args[0];
        var delayMs = args.Count > 1 ? ToFiniteDelay(args[1]) : 0;
        var extraArgs = args.Count > 2 ? args.Skip(2).ToArray() : Array.Empty<JsValue>();

        var id = Interlocked.Increment(ref _fenJsTimerIdCounter);
        var period = repeat ? Math.Max(4, delayMs) : Timeout.Infinite;
        var timer = new Timer(
            _ =>
            {
                if (!repeat)
                {
                    if (_fenJsTimers.TryRemove(id, out var self))
                    {
                        self.Dispose();
                    }
                }

                InvokeFenJsCallbackSafely(callback, extraArgs, "setTimeout/setInterval");
            },
            null,
            Math.Max(0, delayMs),
            period);

        _fenJsTimers[id] = timer;
        return JsValue.FromNumber(id);
    }

    private JsValue ScheduleFenJsAnimationFrame(IReadOnlyList<JsValue> args)
    {
        if (args == null || args.Count == 0)
        {
            return JsValue.FromNumber(0);
        }

        var callback = args[0];
        var id = Interlocked.Increment(ref _fenJsTimerIdCounter);
        var timer = new Timer(
            _ =>
            {
                if (_fenJsTimers.TryRemove(id, out var self))
                {
                    self.Dispose();
                }

                var timestamp = JsValue.FromNumber(_fenJsClock.Elapsed.TotalMilliseconds);
                InvokeFenJsCallbackSafely(callback, new[] { timestamp }, "requestAnimationFrame");
            },
            null,
            16,
            Timeout.Infinite);

        _fenJsTimers[id] = timer;
        return JsValue.FromNumber(id);
    }

    private JsValue ClearFenJsTimer(IReadOnlyList<JsValue> args)
    {
        if (args != null && args.Count > 0 && args[0].Tag != JsValueTag.Undefined)
        {
            var id = (long)ReadJsNumber(args[0]);
            if (_fenJsTimers.TryRemove(id, out var timer))
            {
                timer.Dispose();
            }
        }

        return JsValue.Undefined;
    }

    private static int ToFiniteDelay(JsValue value)
    {
        var d = ReadJsNumber(value);
        if (double.IsNaN(d) || d < 0)
        {
            return 0;
        }

        return d > int.MaxValue ? int.MaxValue : (int)d;
    }

    private static double ReadJsNumber(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.String => double.TryParse(
                value.AsString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0d,
            _ => 0d
        };
    }

    // Invoke a FenJS callback from a host timer thread, holding the interpreter lock so
    // it can never race with page-script evaluation, then drain microtasks and request
    // a repaint so DOM mutations made by the callback become visible.
    private void InvokeFenJsCallbackSafely(JsValue callback, IReadOnlyList<JsValue> args, string origin)
    {
        RunFenJsWithLargeStack<object>(() =>
        {
            lock (_fenJsLock)
            {
                try
                {
                    if (_interpreter.CanCallValue(callback))
                    {
                        _interpreter.InvokeFunction(callback, args ?? Array.Empty<JsValue>(), _fenJsGlobalThis);
                    }
                    _interpreter.PumpMicrotasks();
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn(
                        $"[FenJsTimers] {origin} callback failed: {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
            }

            return null;
        });

        try { RequestRender?.Invoke(); }
        catch { /* render request is best-effort */ }
    }

    private void InstallFenJsBrowserConstructors()
    {
        EvaluateWithFenJsRaw(
            """
            (function () {
                function defineCtor(name, baseCtor, prototypeBrands, match) {
                    var ctor = function () { throw new TypeError('Illegal constructor'); };
                    var proto = baseCtor ? Object.create(baseCtor.prototype) : {};
                    Object.defineProperty(proto, '__fenDomBrands', {
                        value: prototypeBrands.slice(),
                        configurable: true
                    });
                    Object.defineProperty(proto, 'constructor', {
                        value: ctor,
                        writable: true,
                        configurable: true
                    });
                    Object.defineProperty(proto, Symbol.toStringTag, {
                        value: name,
                        configurable: true
                    });
                    ctor.prototype = proto;
                    Object.defineProperty(ctor, Symbol.hasInstance, {
                        value: function (candidate) {
                            if (candidate == null) {
                                return false;
                            }

                            var candidateType = typeof candidate;
                            if (candidateType !== 'object' && candidateType !== 'function') {
                                return false;
                            }

                            var brands = candidate.__fenDomBrands;
                            if (brands && typeof brands.indexOf === 'function' && brands.indexOf(name) >= 0) {
                                return true;
                            }

                            try {
                                return !!match(candidate);
                            } catch (_error) {
                                return false;
                            }
                        },
                        configurable: true
                    });
                    Object.defineProperty(globalThis, name, {
                        value: ctor,
                        writable: true,
                        configurable: true
                    });
                    return ctor;
                }

                var Node = defineCtor('Node', null, ['Node'], function (candidate) {
                    return typeof candidate.nodeName === 'string' ||
                        typeof candidate.tagName === 'string' ||
                        typeof candidate.parentNode !== 'undefined' ||
                        typeof candidate.ownerDocument !== 'undefined';
                });

                var Document = defineCtor('Document', Node, ['Node', 'Document'], function (candidate) {
                    return typeof candidate.readyState === 'string' &&
                        typeof candidate.createElement === 'function' &&
                        typeof candidate.documentElement !== 'undefined';
                });

                var Element = defineCtor('Element', Node, ['Node', 'Element', 'HTMLElement'], function (candidate) {
                    return typeof candidate.tagName === 'string' &&
                        typeof candidate.getAttribute === 'function' &&
                        typeof candidate.setAttribute === 'function';
                });

                var HTMLElement = defineCtor('HTMLElement', Element, ['Node', 'Element', 'HTMLElement'], function (candidate) {
                    return typeof candidate.tagName === 'string' &&
                        typeof candidate.className === 'string' &&
                        typeof candidate.getAttribute === 'function';
                });

                var CharacterData = defineCtor('CharacterData', Node, ['Node', 'CharacterData'], function (candidate) {
                    return typeof candidate.nodeName === 'string' &&
                        typeof candidate.data === 'string' &&
                        typeof candidate.appendData === 'function' &&
                        typeof candidate.replaceData === 'function';
                });

                defineCtor('Text', CharacterData, ['Node', 'CharacterData', 'Text'], function (candidate) {
                    return candidate.nodeName === '#text' &&
                        typeof candidate.appendData === 'function';
                });

                defineCtor('Comment', CharacterData, ['Node', 'CharacterData', 'Comment'], function (candidate) {
                    return candidate.nodeName === '#comment' &&
                        typeof candidate.replaceData === 'function';
                });

                defineCtor('DocumentFragment', Node, ['Node', 'DocumentFragment'], function (candidate) {
                    return candidate.nodeName === '#document-fragment' &&
                        typeof candidate.appendChild === 'function' &&
                        typeof candidate.querySelectorAll === 'function';
                });

                defineCtor('Attr', Node, ['Node', 'Attr'], function (candidate) {
                    return typeof candidate.name === 'string' &&
                        typeof candidate.value === 'string' &&
                        typeof candidate.specified === 'boolean';
                });

                defineCtor('NamedNodeMap', null, ['NamedNodeMap'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.getNamedItem === 'function';
                });

                defineCtor('DOMTokenList', null, ['DOMTokenList'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.contains === 'function' &&
                        typeof candidate.toggle === 'function';
                });

                defineCtor('NodeList', null, ['NodeList'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.namedItem === 'undefined';
                });

                defineCtor('HTMLCollection', null, ['HTMLCollection'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.namedItem === 'function';
                });

                Document.prototype.createElement = function () { return this.createElement.apply(this, arguments); };
                Document.prototype.createTextNode = function () { return this.createTextNode.apply(this, arguments); };
                Document.prototype.createComment = function () { return this.createComment.apply(this, arguments); };
                Document.prototype.createDocumentFragment = function () { return this.createDocumentFragment.apply(this, arguments); };
                Document.prototype.createAttribute = function () { return this.createAttribute.apply(this, arguments); };
                Document.prototype.getElementById = function () { return this.getElementById.apply(this, arguments); };
                Document.prototype.querySelector = function () { return this.querySelector.apply(this, arguments); };
                Document.prototype.querySelectorAll = function () { return this.querySelectorAll.apply(this, arguments); };
                Document.prototype.getElementsByTagName = function () { return this.getElementsByTagName.apply(this, arguments); };
                Document.prototype.cloneNode = function () { return this.cloneNode.apply(this, arguments); };
                Document.prototype.appendChild = function () { return this.appendChild.apply(this, arguments); };

                Element.prototype.getAttribute = function () { return this.getAttribute.apply(this, arguments); };
                Element.prototype.hasAttribute = function () { return this.hasAttribute.apply(this, arguments); };
                Element.prototype.setAttribute = function () { return this.setAttribute.apply(this, arguments); };
                Element.prototype.removeAttribute = function () { return this.removeAttribute.apply(this, arguments); };
                Element.prototype.toggleAttribute = function () { return this.toggleAttribute.apply(this, arguments); };
                Element.prototype.getAttributeNode = function () { return this.getAttributeNode.apply(this, arguments); };
                Element.prototype.setAttributeNode = function () { return this.setAttributeNode.apply(this, arguments); };
                Element.prototype.removeAttributeNode = function () { return this.removeAttributeNode.apply(this, arguments); };
                Element.prototype.hasAttributes = function () { return this.hasAttributes.apply(this, arguments); };
                Element.prototype.matches = function () { return this.matches.apply(this, arguments); };
                Element.prototype.closest = function () { return this.closest.apply(this, arguments); };
                Element.prototype.querySelector = function () { return this.querySelector.apply(this, arguments); };
                Element.prototype.querySelectorAll = function () { return this.querySelectorAll.apply(this, arguments); };
                Element.prototype.appendChild = function () { return this.appendChild.apply(this, arguments); };
                Element.prototype.cloneNode = function () { return this.cloneNode.apply(this, arguments); };

                HTMLElement.prototype.matches = Element.prototype.matches;
                HTMLElement.prototype.closest = Element.prototype.closest;
            })();
            """);
    }

    private void InstallFenJsNativeBrowserConstructors()
    {
        var documentConstructor = _interpreter.AllocateNativeConstructor(
            "Document",
            (_, _) => ToHostOrNull(new Document(), HostObjectKind.DomDocument),
            _ => ToHostOrNull(new Document(), HostObjectKind.DomDocument));
        _interpreter.RegisterGlobalValue("__fenNativeDocumentCtor", documentConstructor);

        EvaluateWithFenJsRaw(
            """
            (function () {
                var nativeDocument = globalThis.__fenNativeDocumentCtor;
                var brandedDocument = globalThis.Document;
                nativeDocument.prototype = brandedDocument.prototype;
                Object.defineProperty(
                    nativeDocument,
                    Symbol.hasInstance,
                    Object.getOwnPropertyDescriptor(brandedDocument, Symbol.hasInstance));
                Object.defineProperty(globalThis, 'Document', {
                    value: nativeDocument,
                    writable: true,
                    configurable: true
                });
                delete globalThis.__fenNativeDocumentCtor;
            })();
            """);
    }

    private HostObjectHandle RegisterHostObject(object hostObject, HostObjectKind kind)
    {
        if (_hostHandleCache.TryGetValue(hostObject, out var existing))
        {
            return existing;
        }

        var handle = _interpreter.HostObjectTable.Register(
            hostObject,
            new HostObjectEntry(
                Generation: 0,
                Kind: kind,
                RealmId: 0,
                Origin: _currentBaseUri?.GetLeftPart(UriPartial.Authority) ?? "about:blank",
                DocumentEpoch: _documentEpoch,
                NavigationEpoch: _navigationEpoch,
                FrameId: 0,
                PermissionFlags: 0));
        _hostHandleCache[hostObject] = handle;
        return handle;
    }

    private BrowserSurfaceProfile CreateNavigatorHost()
    {
        return BrowserSettings.GetBrowserSurface(BrowserSettings.Instance.SelectedUserAgent);
    }

    private JsValue ToHostOrNull(object hostObject, HostObjectKind kind)
    {
        if (hostObject == null)
        {
            return JsValue.Null;
        }

        return JsValue.FromHostObject(RegisterHostObject(hostObject, kind));
    }

    private JsValue ToHostNodeOrNull(Node node)
    {
        return node switch
        {
            Document document => ToHostOrNull(document, HostObjectKind.DomDocument),
            Element element => ToHostOrNull(element, HostObjectKind.DomElement),
            Node otherNode => ToHostOrNull(otherNode, HostObjectKind.DomNode),
            _ => JsValue.Null
        };
    }

    private static Uri TryCreateUri(string raw)
    {
        return Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ? parsed : null;
    }

    private static IEnumerable<Element> EnumerateScriptElements(Node domRoot)
    {
        foreach (var node in domRoot.SelfAndDescendants())
        {
            if (node is Element element &&
                string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
            {
                yield return element;
            }
        }
    }

    private static string CollectScriptText(Node node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        if (node is Text text)
        {
            return text.Data ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            builder.Append(CollectScriptText(child));
        }

        return builder.ToString();
    }

    private static void ApplyScriptingEnabledSanitizer(Node root)
    {
        if (root == null)
        {
            return;
        }

        static void FlipNoJsClass(Element element)
        {
            if (element == null)
            {
                return;
            }

            var classValue = element.GetAttribute("class");
            if (string.IsNullOrWhiteSpace(classValue))
            {
                return;
            }

            var parts = new HashSet<string>(
                classValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            var changed = false;

            if (parts.Remove("no-js"))
            {
                changed = true;
            }

            if (!parts.Contains("js"))
            {
                parts.Add("js");
                changed = true;
            }

            if (changed)
            {
                element.SetAttribute("class", string.Join(" ", parts));
            }
        }

        var container = root as ContainerNode ?? root.OwnerDocument;
        if (container == null)
        {
            return;
        }

        FlipNoJsClass(container.GetElementsByTagName("html").FirstOrDefault());
        FlipNoJsClass(container.GetElementsByTagName("body").FirstOrDefault());
    }

    private string GetDocumentReadyState()
    {
        return string.IsNullOrWhiteSpace(_documentReadyState) ? "loading" : _documentReadyState;
    }

    private void SetDocumentReadyState(string documentReadyState)
    {
        lock (_fenJsLock)
        {
            _documentReadyState = string.IsNullOrWhiteSpace(documentReadyState)
                ? "loading"
                : documentReadyState;
        }
    }

    private Element GetCurrentScriptElement()
    {
        lock (_fenJsLock)
        {
            return _currentScriptElement;
        }
    }

    private void SetCurrentScriptElement(Element scriptElement)
    {
        lock (_fenJsLock)
        {
            _currentScriptElement = scriptElement;
        }
    }

    private Dictionary<string, JsValue> GetHostPropertyStore(object receiver)
    {
        return _hostPropertyStore.GetOrCreateValue(receiver);
    }

    private void AddBrowserEventListener(List<BrowserEventListener> listeners, IReadOnlyList<JsValue> args)
    {
        if (args.Count < 2)
        {
            return;
        }

        var type = CoerceToHostString(args[0]);
        var callback = args[1];
        if (string.IsNullOrWhiteSpace(type) || !_interpreter.CanCallValue(callback))
        {
            return;
        }

        var capture = false;
        var once = false;
        if (args.Count >= 3)
        {
            ParseEventListenerOptions(args[2], out capture, out once);
        }

        listeners.Add(new BrowserEventListener(type, callback, capture, once));
    }

    private void RemoveBrowserEventListener(List<BrowserEventListener> listeners, IReadOnlyList<JsValue> args)
    {
        if (args.Count < 2)
        {
            return;
        }

        var type = CoerceToHostString(args[0]);
        var callback = args[1];
        var capture = false;
        if (args.Count >= 3)
        {
            ParseEventListenerOptions(args[2], out capture, out _);
        }

        for (var i = listeners.Count - 1; i >= 0; i--)
        {
            var listener = listeners[i];
            if (string.Equals(listener.Type, type, StringComparison.Ordinal) &&
                listener.Capture == capture &&
                listener.Callback.Equals(callback))
            {
                listeners.RemoveAt(i);
            }
        }
    }

    private List<BrowserEventListener> GetElementListeners(Element element)
    {
        return _elementEventListeners.GetOrCreateValue(element);
    }

    private void DispatchElementEvent(Element element, string type)
    {
        if (element != null &&
            _elementEventListeners.TryGetValue(element, out var listeners) &&
            listeners != null)
        {
            DispatchBrowserEvent(listeners, type, ToHostOrNull(element, HostObjectKind.DomElement));
        }
    }

    // Mirrors the legacy engine's HTMLElement.focus()/blur(): updates the document's
    // active element and the shared focus state the renderer reads, then dispatches the
    // matching event. The "already focused" guard avoids re-entrant focus dispatch when a
    // 'focus' listener itself calls element.focus() (per HTML §"focusing steps").
    private void FocusElement(Element element)
    {
        if (element == null)
        {
            return;
        }

        var document = element.OwnerDocument;
        var alreadyFocused = document != null && ReferenceEquals(document.ActiveElement, element);
        if (document != null)
        {
            document.ActiveElement = element;
        }

        FenBrowser.FenEngine.Rendering.ElementStateManager.Instance.SetActiveElement(element);

        if (!alreadyFocused)
        {
            DispatchElementEvent(element, "focus");
        }
    }

    private void BlurElement(Element element)
    {
        if (element == null)
        {
            return;
        }

        var document = element.OwnerDocument;
        var wasFocused = document != null && ReferenceEquals(document.ActiveElement, element);
        if (wasFocused)
        {
            document.ActiveElement = null;
        }

        FenBrowser.FenEngine.Rendering.ElementStateManager.Instance.SetActiveElement(null);

        if (wasFocused)
        {
            DispatchElementEvent(element, "blur");
        }
    }

    private void DispatchStartupLifecycleEvents()
    {
        var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
        if (document == null)
        {
            SetDocumentReadyState("complete");
            return;
        }

        SetDocumentReadyState("interactive");
        DispatchBrowserEvent(
            _documentEventListeners,
            "DOMContentLoaded",
            ToHostOrNull(document, HostObjectKind.DomDocument));

        SetDocumentReadyState("complete");
        InvokeBodyOnloadAttribute(document);
        DispatchWindowLoadHandlers();
    }

    private JsValue GetStoredHostPropertyOrUndefined(object receiver, string property)
    {
        var store = GetHostPropertyStore(receiver);
        return store.TryGetValue(property, out var value) ? value : JsValue.Undefined;
    }

    private void SetStoredHostProperty(object receiver, string property, JsValue value)
    {
        var store = GetHostPropertyStore(receiver);
        store[property] = value;
    }

    private JsValue GetOrCreateDomTokenListView(DOMTokenList tokenList)
    {
        var store = GetHostPropertyStore(tokenList);
        if (store.TryGetValue("__fenDomTokenListView", out var cached))
        {
            return cached;
        }

        var view = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["length"] = JsValue.FromInt32(tokenList.Length),
            ["value"] = JsValue.FromString(tokenList.Value ?? string.Empty),
            ["item"] = GetOrCreateHostCallable(
                tokenList,
                "item",
                (_, args) =>
                {
                    var index = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex)
                        ? parsedIndex
                        : -1;
                    var item = tokenList.Item(index);
                    return item == null ? JsValue.Null : JsValue.FromString(item);
                },
                length: 1),
            ["contains"] = GetOrCreateHostCallable(
                tokenList,
                "contains",
                (_, args) =>
                {
                    var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    return JsValue.FromBoolean(tokenList.Contains(token));
                },
                length: 1),
            ["add"] = GetOrCreateHostCallable(
                tokenList,
                "add",
                (_, args) =>
                {
                    tokenList.Add(args.Select(CoerceToHostString).ToArray());
                    return JsValue.Undefined;
                }),
            ["remove"] = GetOrCreateHostCallable(
                tokenList,
                "remove",
                (_, args) =>
                {
                    tokenList.Remove(args.Select(CoerceToHostString).ToArray());
                    return JsValue.Undefined;
                }),
            ["toggle"] = GetOrCreateHostCallable(
                tokenList,
                "toggle",
                (_, args) =>
                {
                    var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    bool? force = null;
                    if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
                    {
                        force = CoerceToHostBoolean(args[1]);
                    }

                    return JsValue.FromBoolean(tokenList.Toggle(token, force));
                },
                length: 1),
            ["replace"] = GetOrCreateHostCallable(
                tokenList,
                "replace",
                (_, args) =>
                {
                    var oldToken = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    var newToken = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                    return JsValue.FromBoolean(tokenList.Replace(oldToken, newToken));
                },
                length: 2),
            ["supports"] = GetOrCreateHostCallable(
                tokenList,
                "supports",
                (_, args) =>
                {
                    var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    return JsValue.FromBoolean(tokenList.Supports(token));
                },
                length: 1)
        });

        for (var i = 0; i < tokenList.Length; i++)
        {
            var item = tokenList.Item(i);
            if (item != null)
            {
                _interpreter.SetObjectProperty(view, i.ToString(CultureInfo.InvariantCulture), JsValue.FromString(item));
            }
        }

        AttachFenJsPrototype(view, "DOMTokenList");
        store["__fenDomTokenListView"] = view;
        return view;
    }

    private void AttachFenJsPrototype(JsValue target, string constructorName)
    {
        var tempName = "__fenTmp" + Interlocked.Increment(ref _temporaryFenJsGlobalCounter).ToString(CultureInfo.InvariantCulture);
        _interpreter.RegisterGlobalValue(tempName, target);
        try
        {
            EvaluateWithFenJsRaw($"Object.setPrototypeOf(globalThis.{tempName}, {constructorName}.prototype);");
        }
        finally
        {
            _interpreter.RegisterGlobalValue(tempName, JsValue.Undefined);
            EvaluateWithFenJsRaw($"delete globalThis.{tempName};");
        }
    }

    private void DispatchWindowLoadHandlers()
    {
        var windowValue = EvaluateWithFenJsRaw("window");
        DispatchBrowserEvent(_windowEventListeners, "load", windowValue);

        if (_interpreter.TryReadGlobalValue("onload", out var onload) &&
            _interpreter.CanCallValue(onload))
        {
            var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["type"] = JsValue.FromString("load"),
                ["target"] = windowValue,
                ["currentTarget"] = windowValue
            });
            InvokeFenJsCallback(onload, windowValue, eventValue);
        }
    }

    private void InvokeBodyOnloadAttribute(Document document)
    {
        var body = document.Body;
        var onload = body?.GetAttribute("onload");
        if (string.IsNullOrWhiteSpace(onload))
        {
            return;
        }

        var bodyValue = ToHostOrNull(body, HostObjectKind.DomElement);
        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString("load"),
            ["target"] = bodyValue,
            ["currentTarget"] = bodyValue
        });

        InvokeFenJsInlineWithEvent(onload, eventValue);
    }

    private void DispatchBrowserEvent(List<BrowserEventListener> listeners, string type, JsValue currentTarget)
    {
        if (listeners.Count == 0)
        {
            return;
        }

        var callbacks = new List<BrowserEventListener>();
        for (var i = 0; i < listeners.Count; i++)
        {
            var listener = listeners[i];
            if (string.Equals(listener.Type, type, StringComparison.Ordinal))
            {
                callbacks.Add(listener);
            }
        }

        if (callbacks.Count == 0)
        {
            return;
        }

        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString(type),
            ["target"] = currentTarget,
            ["currentTarget"] = currentTarget
        });

        foreach (var listener in callbacks)
        {
            InvokeFenJsCallback(listener.Callback, currentTarget, eventValue);
            if (listener.Once)
            {
                listeners.RemoveAll(existing =>
                    string.Equals(existing.Type, listener.Type, StringComparison.Ordinal) &&
                    existing.Capture == listener.Capture &&
                    existing.Callback.Equals(listener.Callback));
            }
        }
    }

    private void InvokeFenJsCallback(JsValue callback, JsValue thisValue, JsValue eventValue)
    {
        lock (_fenJsLock)
        {
            var previousEvent = _interpreter.TryReadGlobalValue("event", out var existingEvent)
                ? existingEvent
                : JsValue.Undefined;
            _interpreter.RegisterGlobalValue("event", eventValue);
            try
            {
                _fenJsEvaluationCount++;
                _ = _interpreter.InvokeFunction(callback, new[] { eventValue }, thisValue);
            }
            finally
            {
                _interpreter.RegisterGlobalValue("event", previousEvent);
            }
        }
    }

    private void InvokeFenJsInlineWithEvent(string script, JsValue eventValue)
    {
        lock (_fenJsLock)
        {
            var previousEvent = _interpreter.TryReadGlobalValue("event", out var existingEvent)
                ? existingEvent
                : JsValue.Undefined;
            _interpreter.RegisterGlobalValue("event", eventValue);
            try
            {
                _fenJsEvaluationCount++;
                var function = _compiler.CompileScript(new SourceText(script, "<fenbrowser-fenjs-inline-handler>"));
                new BytecodeVerifier().Verify(function);
                _ = _interpreter.Execute(function);
            }
            finally
            {
                _interpreter.RegisterGlobalValue("event", previousEvent);
            }
        }
    }

    private static void ParseEventListenerOptions(JsValue options, out bool capture, out bool once)
    {
        capture = false;
        once = false;

        switch (options.Tag)
        {
            case JsValueTag.Undefined:
            case JsValueTag.Null:
                return;
            case JsValueTag.Boolean:
                capture = options.AsBoolean();
                return;
            case JsValueTag.Int32:
                capture = options.AsInt32() != 0;
                return;
            case JsValueTag.Number:
                capture = Math.Abs(options.AsNumber()) > 0;
                return;
        }
    }

    private JsValue ExecuteDynamicScriptElement(Element scriptElement)
    {
        if (scriptElement == null ||
            !string.Equals(scriptElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
        {
            return JsValue.Undefined;
        }

        var src = scriptElement.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src) ||
            !AllowExternalScripts ||
            !Sandbox.Allows(SandboxFeature.ExternalScripts))
        {
            return ToHostNodeOrNull(scriptElement);
        }

        var baseUri = _currentBaseUri;
        if (baseUri == null || !Uri.TryCreate(baseUri, src, out var scriptUri))
        {
            DispatchScriptElementEvent(scriptElement, "error");
            return ToHostNodeOrNull(scriptElement);
        }

        if (SubresourceAllowed != null && !SubresourceAllowed(scriptUri, "script"))
        {
            DispatchScriptElementEvent(scriptElement, "error");
            return ToHostNodeOrNull(scriptElement);
        }

        try
        {
            string code;
            if (ExternalScriptFetcher != null)
            {
                code = ExternalScriptFetcher(scriptUri, baseUri).GetAwaiter().GetResult();
            }
            else if (FetchOverride != null)
            {
                code = FetchOverride(scriptUri).GetAwaiter().GetResult();
            }
            else
            {
                DispatchScriptElementEvent(scriptElement, "error");
                return ToHostNodeOrNull(scriptElement);
            }

            if (!string.IsNullOrWhiteSpace(code))
            {
                SetCurrentScriptElement(scriptElement);
                try
                {
                    EvaluateWithFenJsRaw(code);
                }
                finally
                {
                    SetCurrentScriptElement(null);
                }
            }

            DispatchScriptElementEvent(scriptElement, "load");
        }
        catch
        {
            DispatchScriptElementEvent(scriptElement, "error");
        }

        return ToHostNodeOrNull(scriptElement);
    }

    private void DispatchScriptElementEvent(Element scriptElement, string type)
    {
        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString(type),
            ["target"] = ToHostOrNull(scriptElement, HostObjectKind.DomElement),
            ["currentTarget"] = ToHostOrNull(scriptElement, HostObjectKind.DomElement)
        });

        var handler = GetStoredHostPropertyOrUndefined(scriptElement, "on" + type);
        if (_interpreter.CanCallValue(handler))
        {
            InvokeFenJsCallback(handler, ToHostOrNull(scriptElement, HostObjectKind.DomElement), eventValue);
        }
    }

    private void ExecuteInlineScriptsFromElement(Element rootElement)
    {
        if (rootElement == null)
        {
            return;
        }

        foreach (var scriptElement in rootElement.SelfAndDescendants().OfType<Element>())
        {
            if (!string.Equals(scriptElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scriptElement.GetAttribute("src")))
            {
                continue;
            }

            var code = CollectScriptText(scriptElement);
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            SetCurrentScriptElement(scriptElement);
            try
            {
                EvaluateWithFenJsRaw(code);
            }
            finally
            {
                SetCurrentScriptElement(null);
            }
        }
    }

    private void InsertAdjacentHtml(Element anchor, string position, string html)
    {
        if (anchor == null)
        {
            return;
        }

        var fragment = HtmlParser.ParseFragment(anchor, html ?? string.Empty, options: null, out _);
        var insertedRoots = new List<Element>();
        var normalizedPosition = (position ?? string.Empty).Trim().ToLowerInvariant();

        void TrackInserted(Node node)
        {
            if (node is Element element)
            {
                insertedRoots.Add(element);
            }
        }

        switch (normalizedPosition)
        {
            case "afterbegin":
            {
                var parent = anchor as ContainerNode;
                var first = anchor.FirstChild;
                while (fragment.FirstChild != null)
                {
                    var node = fragment.FirstChild;
                    fragment.RemoveChild(node);
                    parent?.InsertBefore(node, first);
                    TrackInserted(node);
                }
                break;
            }
            case "beforeend":
            {
                var parent = anchor as ContainerNode;
                while (fragment.FirstChild != null)
                {
                    var node = fragment.FirstChild;
                    fragment.RemoveChild(node);
                    parent?.AppendChild(node);
                    TrackInserted(node);
                }
                break;
            }
            case "beforebegin":
            {
                var parent = anchor.ParentNode as ContainerNode;
                while (fragment.FirstChild != null)
                {
                    var node = fragment.FirstChild;
                    fragment.RemoveChild(node);
                    parent?.InsertBefore(node, anchor);
                    TrackInserted(node);
                }
                break;
            }
            case "afterend":
            {
                var parent = anchor.ParentNode as ContainerNode;
                var next = anchor.NextSibling;
                while (fragment.FirstChild != null)
                {
                    var node = fragment.FirstChild;
                    fragment.RemoveChild(node);
                    parent?.InsertBefore(node, next);
                    TrackInserted(node);
                }
                break;
            }
            default:
                return;
        }

        if (ExecuteInlineScriptsOnInnerHTML)
        {
            foreach (var insertedRoot in insertedRoots)
            {
                ExecuteInlineScriptsFromElement(insertedRoot);
            }
        }
    }

    private void WriteDocumentMarkup(Document document, IReadOnlyList<JsValue> args, bool appendNewLine)
    {
        if (document == null)
        {
            return;
        }

        var html = string.Concat(args.Select(CoerceToHostString));
        if (appendNewLine)
        {
            html += Environment.NewLine;
        }

        if (string.IsNullOrEmpty(html))
        {
            return;
        }

        var contextElement = document.Body ?? document.DocumentElement;
        var fragment = HtmlParser.ParseFragment(
            contextElement,
            html,
            new HtmlParserOptions { BaseUri = _currentBaseUri },
            out _);

        if (fragment.ChildNodes == null || fragment.ChildNodes.Length == 0)
        {
            return;
        }

        var insertionParent = ResolveDocumentWriteInsertionParent(document, out var referenceNode);
        if (insertionParent == null)
        {
            return;
        }

        foreach (var child in fragment.ChildNodes.ToArray())
        {
            if (referenceNode != null)
            {
                insertionParent.InsertBefore(child, referenceNode);
            }
            else
            {
                insertionParent.AppendChild(child);
            }
        }
    }

    private ContainerNode ResolveDocumentWriteInsertionParent(Document document, out Node referenceNode)
    {
        referenceNode = null;

        var currentScript = GetCurrentScriptElement();
        if (currentScript?.ParentNode is ContainerNode scriptParent)
        {
            referenceNode = currentScript.NextSibling;
            return scriptParent;
        }

        if (document.Body is ContainerNode body)
        {
            return body;
        }

        if (document.DocumentElement is ContainerNode documentElement)
        {
            return documentElement;
        }

        return document;
    }

    private JsValue GetOrCreateHostCallable(
        object receiver,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length = 0)
    {
        var methods = _hostCallableCache.GetOrCreateValue(receiver);
        if (methods.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var created = _interpreter.AllocateNativeFunction(name, call, length);
        methods[name] = created;
        return created;
    }

    private object ResolveHostObjectOrNull(JsValue value)
    {
        if (value.Tag != JsValueTag.HostObject)
        {
            return null;
        }

        var resolution = _interpreter.HostObjectTable.Resolve(value.AsHostObjectHandle(), _interpreter.HostResolveContext);
        return resolution.IsOk ? resolution.HostObject : null;
    }

    private T ResolveHostObjectOrNull<T>(JsValue value) where T : class
    {
        return ResolveHostObjectOrNull(value) as T;
    }

    private void ThrowHierarchyRequestError(string message)
    {
        ThrowDomException("HierarchyRequestError", message ?? "Hierarchy request error.");
    }

    private void ThrowDomException(string name, string message)
    {
        throw new JsThrownException(_interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["name"] = JsValue.FromString(name ?? "Error"),
            ["message"] = JsValue.FromString(message ?? string.Empty)
        }));
    }

    private static string CoerceToHostString(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Int32 => value.AsInt32().ToString(CultureInfo.InvariantCulture),
            JsValueTag.Number => value.AsNumber().ToString(CultureInfo.InvariantCulture),
            JsValueTag.String => value.AsString(),
            JsValueTag.Symbol => value.AsSymbolDescription() is string d ? $"Symbol({d})" : "Symbol()",
            JsValueTag.BigInt => value.AsBigInt().ToString(CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static bool CoerceToHostBoolean(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => Math.Abs(value.AsNumber()) > double.Epsilon,
            JsValueTag.String => !string.IsNullOrEmpty(value.AsString()),
            JsValueTag.Null => false,
            JsValueTag.Undefined => false,
            _ => true
        };
    }

    private static bool TryCoerceIndex(JsValue value, out int index)
    {
        switch (value.Tag)
        {
            case JsValueTag.Int32:
                index = value.AsInt32();
                return true;
            case JsValueTag.Number:
                var number = value.AsNumber();
                if (double.IsFinite(number) &&
                    Math.Truncate(number) == number &&
                    number >= int.MinValue &&
                    number <= int.MaxValue)
                {
                    index = (int)number;
                    return true;
                }
                break;
            case JsValueTag.String:
                if (int.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    return true;
                }
                break;
        }

        index = -1;
        return false;
    }

    private JsValue CreateNodeArrayLike(IEnumerable<Node> nodes)
    {
        var items = new List<JsValue>();
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                items.Add(ToHostNodeOrNull(node));
            }
        }

        var array = _interpreter.AllocateArray(items);
        var itemFunction = _interpreter.AllocateNativeFunction(
            "item",
            (_, args) =>
            {
                var index = -1;
                if (args.Count > 0)
                {
                    switch (args[0].Tag)
                    {
                        case JsValueTag.Int32:
                            index = args[0].AsInt32();
                            break;
                        case JsValueTag.Number:
                            var number = args[0].AsNumber();
                            if (double.IsFinite(number) &&
                                Math.Truncate(number) == number &&
                                number >= int.MinValue &&
                                number <= int.MaxValue)
                            {
                                index = (int)number;
                            }
                            break;
                        case JsValueTag.String:
                            if (int.TryParse(args[0].AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
                            {
                                index = parsedIndex;
                            }
                            break;
                    }
                }

                if (index < 0 || index >= items.Count)
                {
                    return JsValue.Null;
                }

                return items[index];
            },
            length: 1);
        _interpreter.SetObjectProperty(array, "item", itemFunction, enumerable: false);
        return array;
    }

    private sealed class FenJsHtmlCollectionHost
    {
        public FenJsHtmlCollectionHost(HTMLCollection collection)
        {
            Collection = collection ?? throw new ArgumentNullException(nameof(collection));
        }

        public HTMLCollection Collection { get; }
    }

    private sealed class FenJsDomStringMapHost
    {
        public FenJsDomStringMapHost(Element element)
        {
            Element = element ?? throw new ArgumentNullException(nameof(element));
        }

        public Element Element { get; }
    }

    private sealed class FenJsDomImplementationHost
    {
        public FenJsDomImplementationHost(Document ownerDocument)
        {
            OwnerDocument = ownerDocument ?? throw new ArgumentNullException(nameof(ownerDocument));
        }

        public Document OwnerDocument { get; }
    }

    private sealed record BrowserEventListener(string Type, JsValue Callback, bool Capture, bool Once);

    private sealed class FenJsLocationHost
    {
        public FenJsLocationHost(Uri uri)
        {
            Uri = uri;
        }

        public Uri Uri { get; }
    }

    private sealed class BrowserFenJsHostHooks : IHostHooks
    {
        private FenJsBrowserScriptEngine _owner;
        private Document _document;
        private BrowserSurfaceProfile _navigator;
        private FenJsLocationHost _location;
        private Uri _baseUri;

        public void Reset()
        {
            _owner = null;
            _document = null;
            _navigator = null;
            _location = null;
            _baseUri = null;
        }

        public void Bind(
            FenJsBrowserScriptEngine owner,
            Document document,
            BrowserSurfaceProfile navigator,
            FenJsLocationHost location,
            Uri baseUri)
        {
            _owner = owner;
            _document = document;
            _navigator = navigator;
            _location = location;
            _baseUri = baseUri;
        }

        public void EnqueuePromiseJob(PromiseJob job)
        {
        }

        public void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation)
        {
            _ = promise;
            _ = operation;
        }

        public bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value)
        {
            var resolution = _owner._interpreter.HostObjectTable.Resolve(handle, _owner._interpreter.HostResolveContext);
            if (!resolution.IsOk)
            {
                value = JsValue.Undefined;
                return false;
            }

            switch (resolution.HostObject)
            {
                case Document document:
                    return TryGetDocumentProperty(document, property, out value);
                case Element element:
                    return TryGetElementProperty(element, property, out value);
                case FenJsDomImplementationHost implementation:
                    return TryGetDomImplementationProperty(implementation, property, out value);
                case Attr attr:
                    return TryGetAttrProperty(attr, property, out value);
                case NamedNodeMap namedNodeMap:
                    return TryGetNamedNodeMapProperty(namedNodeMap, property, out value);
                case DOMTokenList tokenList:
                    return TryGetDomTokenListProperty(tokenList, property, out value);
                case CharacterData characterData:
                    return TryGetCharacterDataProperty(characterData, property, out value);
                case DocumentFragment fragment:
                    return TryGetDocumentFragmentProperty(fragment, property, out value);
                case FenJsHtmlCollectionHost htmlCollection:
                    return TryGetHtmlCollectionProperty(htmlCollection, property, out value);
                case FenJsDomStringMapHost domStringMap:
                    return TryGetDomStringMapProperty(domStringMap, property, out value);
                case BrowserSurfaceProfile navigator:
                    return TryGetNavigatorProperty(navigator, property, out value);
                case FenJsLocationHost location:
                    return TryGetLocationProperty(location, property, out value);
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        public bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value)
        {
            var resolution = _owner._interpreter.HostObjectTable.Resolve(handle, _owner._interpreter.HostResolveContext);
            if (!resolution.IsOk)
            {
                return false;
            }

            switch (resolution.HostObject)
            {
                case Document document when string.Equals(property, "title", StringComparison.Ordinal):
                    document.Title = CoerceToHostString(value);
                    return true;
                case Document document when string.Equals(property, "cookie", StringComparison.Ordinal):
                    document.Cookie = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "className", StringComparison.Ordinal):
                    element.ClassName = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "id", StringComparison.Ordinal):
                    element.Id = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "src", StringComparison.Ordinal):
                    element.SetAttribute("src", CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "innerHTML", StringComparison.Ordinal):
                    element.InnerHTML = CoerceToHostString(value);
                    if (_owner.ExecuteInlineScriptsOnInnerHTML)
                    {
                        _owner.ExecuteInlineScriptsFromElement(element);
                    }
                    return true;
                case Element element when string.Equals(property, "textContent", StringComparison.Ordinal):
                    element.TextContent = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "onload", StringComparison.Ordinal):
                    _owner.SetStoredHostProperty(element, "onload", value);
                    return true;
                case Element element when string.Equals(property, "onerror", StringComparison.Ordinal):
                    _owner.SetStoredHostProperty(element, "onerror", value);
                    return true;
                case Attr attr when string.Equals(property, "value", StringComparison.Ordinal):
                    attr.Value = CoerceToHostString(value);
                    return true;
                case Attr attr when string.Equals(property, "nodeValue", StringComparison.Ordinal):
                    attr.Value = CoerceToHostString(value);
                    return true;
                case Attr attr when string.Equals(property, "textContent", StringComparison.Ordinal):
                    attr.Value = CoerceToHostString(value);
                    return true;
                case CharacterData characterData when string.Equals(property, "data", StringComparison.Ordinal):
                    characterData.Data = CoerceToHostString(value);
                    return true;
                case CharacterData characterData when string.Equals(property, "nodeValue", StringComparison.Ordinal):
                    characterData.NodeValue = CoerceToHostString(value);
                    return true;
                case CharacterData characterData when string.Equals(property, "textContent", StringComparison.Ordinal):
                    characterData.TextContent = CoerceToHostString(value);
                    return true;
                case FenJsDomStringMapHost domStringMap:
                    domStringMap.Element.SetAttribute(PropertyNameToDatasetAttribute(property), CoerceToHostString(value));
                    return true;
                default:
                    return false;
            }
        }

        public JsValue CallHostFunction(int functionId, JsValue thisValue, ReadOnlySpan<JsValue> args)
        {
            _ = functionId;
            _ = thisValue;
            _ = args;
            throw new NotSupportedException("FenJS browser host functions are not wired yet.");
        }

        private bool TryGetDocumentProperty(Document document, string property, out JsValue value)
        {
            switch (property)
            {
                case "nodeName":
                    value = JsValue.FromString("#document");
                    return true;
                case "parentNode":
                case "ownerDocument":
                    value = JsValue.Null;
                    return true;
                case "readyState":
                    value = JsValue.FromString(_owner.GetDocumentReadyState());
                    return true;
                case "URL":
                case "documentURI":
                    value = JsValue.FromString(document.URL ?? string.Empty);
                    return true;
                case "title":
                    value = JsValue.FromString(document.Title ?? string.Empty);
                    return true;
                case "cookie":
                    value = JsValue.FromString(document.Cookie ?? string.Empty);
                    return true;
                case "body":
                    value = _owner.ToHostOrNull(document.Body, HostObjectKind.DomElement);
                    return true;
                case "head":
                    value = _owner.ToHostOrNull(document.Head, HostObjectKind.DomElement);
                    return true;
                case "documentElement":
                    value = _owner.ToHostOrNull(document.DocumentElement, HostObjectKind.DomElement);
                    return true;
                case "currentScript":
                    value = _owner.ToHostOrNull(_owner.GetCurrentScriptElement(), HostObjectKind.DomElement);
                    return true;
                case "defaultView":
                    value = _owner.EvaluateWithFenJsRaw("window");
                    return true;
                case "implementation":
                    value = _owner.ToHostOrNull(new FenJsDomImplementationHost(document), HostObjectKind.Other);
                    return true;
                case "getElementById":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "getElementById",
                        (_, args) =>
                        {
                            var id = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(document.GetElementById(id));
                        },
                        length: 1);
                    return true;
                case "querySelector":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "querySelector",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return _owner.ToHostNodeOrNull(document.QuerySelector(selector));
                            }
                            catch (DomException)
                            {
                                return JsValue.Null;
                            }
                        },
                        length: 1);
                    return true;
                case "querySelectorAll":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "querySelectorAll",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(selector))
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }

                            try
                            {
                                return _owner.CreateNodeArrayLike(document.QuerySelectorAll(selector));
                            }
                            catch (DomException)
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }
                        },
                        length: 1);
                    return true;
                case "createElement":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createElement",
                        (_, args) =>
                        {
                            var localName = args.Count > 0 ? CoerceToHostString(args[0]) : "div";
                            return _owner.ToHostNodeOrNull(document.CreateElement(localName));
                        },
                        length: 1);
                    return true;
                case "createTextNode":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createTextNode",
                        (_, args) =>
                        {
                            var data = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(document.CreateTextNode(data));
                        },
                        length: 1);
                    return true;
                case "createComment":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createComment",
                        (_, args) =>
                        {
                            var data = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(document.CreateComment(data));
                        },
                        length: 1);
                    return true;
                case "createDocumentFragment":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createDocumentFragment",
                        (_, _) => _owner.ToHostNodeOrNull(document.CreateDocumentFragment()),
                        length: 0);
                    return true;
                case "createAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createAttribute",
                        (_, args) =>
                        {
                            var localName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostOrNull(document.CreateAttribute(localName), HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "createAttributeNS":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createAttributeNS",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            var qualifiedName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            return _owner.ToHostOrNull(document.CreateAttributeNS(namespaceUri, qualifiedName), HostObjectKind.Other);
                        },
                        length: 2);
                    return true;
                case "getElementsByTagName":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "getElementsByTagName",
                        (_, args) =>
                        {
                            var qualifiedName = args.Count > 0 ? CoerceToHostString(args[0]) : "*";
                            return _owner.ToHostOrNull(
                                new FenJsHtmlCollectionHost(document.GetElementsByTagName(qualifiedName)),
                                HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "cloneNode":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "cloneNode",
                        (_, args) =>
                        {
                            var deep = args.Count > 0 && CoerceToHostBoolean(args[0]);
                            return _owner.ToHostNodeOrNull(document.CloneNode(deep));
                        },
                        length: 1);
                    return true;
                case "appendChild":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "appendChild",
                        (_, args) =>
                        {
                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull(args[0]) as Node : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("HierarchyRequestError", "Document child must be a DOM node.");
                            }

                            return _owner.ToHostNodeOrNull(document.AppendChild(child));
                        },
                        length: 1);
                    return true;
                case "write":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "write",
                        (_, args) =>
                        {
                            _owner.WriteDocumentMarkup(document, args, appendNewLine: false);
                            return JsValue.Undefined;
                        });
                    return true;
                case "writeln":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "writeln",
                        (_, args) =>
                        {
                            _owner.WriteDocumentMarkup(document, args, appendNewLine: true);
                            return JsValue.Undefined;
                        });
                    return true;
                case "addEventListener":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "addEventListener",
                        (_, args) =>
                        {
                            _owner.AddBrowserEventListener(_owner._documentEventListeners, args);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "removeEventListener":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "removeEventListener",
                        (_, args) =>
                        {
                            _owner.RemoveBrowserEventListener(_owner._documentEventListeners, args);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetElementProperty(Element element, string property, out JsValue value)
        {
            switch (property)
            {
                case "id":
                    value = JsValue.FromString(element.Id ?? string.Empty);
                    return true;
                case "className":
                    value = JsValue.FromString(element.ClassName ?? string.Empty);
                    return true;
                case "tagName":
                case "nodeName":
                    value = JsValue.FromString(element.TagName ?? string.Empty);
                    return true;
                case "src":
                    value = JsValue.FromString(ResolveElementUrlProperty(element, "src"));
                    return true;
                case "textContent":
                    value = JsValue.FromString(element.TextContent ?? string.Empty);
                    return true;
                case "nextSibling":
                    value = _owner.ToHostNodeOrNull(element.NextSibling);
                    return true;
                case "previousSibling":
                    value = _owner.ToHostNodeOrNull(element.PreviousSibling);
                    return true;
                case "parentNode":
                    value = _owner.ToHostNodeOrNull(element.ParentNode);
                    return true;
                case "innerHTML":
                    value = JsValue.FromString(element.InnerHTML ?? string.Empty);
                    return true;
                case "onload":
                    value = _owner.GetStoredHostPropertyOrUndefined(element, "onload");
                    return true;
                case "onerror":
                    value = _owner.GetStoredHostPropertyOrUndefined(element, "onerror");
                    return true;
                case "firstElementChild":
                    value = _owner.ToHostNodeOrNull(element.FirstElementChild);
                    return true;
                case "parentElement":
                    value = _owner.ToHostNodeOrNull(element.ParentElement);
                    return true;
                case "ownerDocument":
                    value = _owner.ToHostOrNull(element.OwnerDocument, HostObjectKind.DomDocument);
                    return true;
                case "attributes":
                    value = _owner.ToHostOrNull(element.Attributes, HostObjectKind.Other);
                    return true;
                case "classList":
                    value = _owner.GetOrCreateDomTokenListView(element.ClassList);
                    return true;
                case "dataset":
                    value = _owner.ToHostOrNull(new FenJsDomStringMapHost(element), HostObjectKind.Other);
                    return true;
                case "addEventListener":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "addEventListener",
                        (_, args) =>
                        {
                            _owner.AddBrowserEventListener(_owner.GetElementListeners(element), args);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "removeEventListener":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "removeEventListener",
                        (_, args) =>
                        {
                            _owner.RemoveBrowserEventListener(_owner.GetElementListeners(element), args);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "focus":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "focus",
                        (_, _) =>
                        {
                            _owner.FocusElement(element);
                            return JsValue.Undefined;
                        });
                    return true;
                case "blur":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "blur",
                        (_, _) =>
                        {
                            _owner.BlurElement(element);
                            return JsValue.Undefined;
                        });
                    return true;
                case "getAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "getAttribute",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var attributeValue = element.GetAttribute(attributeName);
                            return attributeValue == null ? JsValue.Null : JsValue.FromString(attributeValue);
                        },
                        length: 1);
                    return true;
                case "hasAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "hasAttribute",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return JsValue.FromBoolean(element.HasAttribute(attributeName));
                        },
                        length: 1);
                    return true;
                case "setAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "setAttribute",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var attributeValue = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            element.SetAttribute(attributeName, attributeValue);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "getAttributeNode":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "getAttributeNode",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostOrNull(element.GetAttributeNode(attributeName), HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "getAttributeNodeNS":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "getAttributeNodeNS",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            var localName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            return _owner.ToHostOrNull(element.GetAttributeNodeNS(namespaceUri, localName), HostObjectKind.Other);
                        },
                        length: 2);
                    return true;
                case "removeAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "removeAttribute",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            element.RemoveAttribute(attributeName);
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "setAttributeNode":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "setAttributeNode",
                        (_, args) =>
                        {
                            var attr = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Attr>(args[0]) : null;
                            if (attr == null)
                            {
                                return JsValue.Null;
                            }

                            try
                            {
                                return _owner.ToHostOrNull(element.SetAttributeNode(attr), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "removeAttributeNode":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "removeAttributeNode",
                        (_, args) =>
                        {
                            var attr = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Attr>(args[0]) : null;
                            if (attr == null)
                            {
                                return JsValue.Null;
                            }

                            try
                            {
                                return _owner.ToHostOrNull(element.RemoveAttributeNode(attr), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "toggleAttribute":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "toggleAttribute",
                        (_, args) =>
                        {
                            var attributeName = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            bool? force = null;
                            if (args.Count > 1)
                            {
                                force = args[1].Tag switch
                                {
                                    JsValueTag.Boolean => args[1].AsBoolean(),
                                    JsValueTag.Undefined => null,
                                    JsValueTag.Null => false,
                                    _ => !string.Equals(CoerceToHostString(args[1]), "false", StringComparison.OrdinalIgnoreCase)
                                };
                            }

                            return JsValue.FromBoolean(element.ToggleAttribute(attributeName, force));
                        },
                        length: 2);
                    return true;
                case "hasAttributes":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "hasAttributes",
                        (_, _) => JsValue.FromBoolean(element.HasAttributes()),
                        length: 0);
                    return true;
                case "appendChild":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "appendChild",
                        (_, args) =>
                        {
                            if (args.Count > 0 && _owner.ResolveHostObjectOrNull<Attr>(args[0]) != null)
                            {
                                _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                            }

                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                return JsValue.Null;
                            }

                            var appended = element.AppendChild(child);
                            if (child is Element childElement &&
                                string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                            {
                                _owner.ExecuteDynamicScriptElement(childElement);
                            }

                            return _owner.ToHostNodeOrNull(appended);
                        },
                        length: 1);
                    return true;
                case "append":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "append",
                        (_, args) =>
                        {
                            foreach (var arg in args)
                            {
                                Node child = _owner.ResolveHostObjectOrNull<Node>(arg);
                                if (child == null)
                                {
                                    child = element.OwnerDocument?.CreateTextNode(CoerceToHostString(arg));
                                }

                                if (child == null)
                                {
                                    continue;
                                }

                                element.AppendChild(child);
                                if (child is Element childElement &&
                                    string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                                {
                                    _owner.ExecuteDynamicScriptElement(childElement);
                                }
                            }

                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "cloneNode":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "cloneNode",
                        (_, args) =>
                        {
                            var deep = args.Count > 0 && CoerceToHostBoolean(args[0]);
                            return _owner.ToHostNodeOrNull(element.CloneNode(deep));
                        },
                        length: 1);
                    return true;
                case "insertBefore":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "insertBefore",
                        (_, args) =>
                        {
                            if (args.Count > 0 && _owner.ResolveHostObjectOrNull<Attr>(args[0]) != null)
                            {
                                _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                            }

                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                return JsValue.Null;
                            }

                            var referenceNode = args.Count > 1 ? _owner.ResolveHostObjectOrNull<Node>(args[1]) : null;
                            var inserted = element.InsertBefore(child, referenceNode);
                            if (child is Element childElement &&
                                string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                            {
                                _owner.ExecuteDynamicScriptElement(childElement);
                            }

                            return _owner.ToHostNodeOrNull(inserted);
                        },
                        length: 2);
                    return true;
                case "remove":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "remove",
                        (_, _) =>
                        {
                            if (element.ParentNode is ContainerNode parent)
                            {
                                parent.RemoveChild(element);
                            }

                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "matches":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "matches",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return JsValue.FromBoolean(element.Matches(selector));
                            }
                            catch (DomException)
                            {
                                return JsValue.FromBoolean(false);
                            }
                        },
                        length: 1);
                    return true;
                case "closest":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "closest",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return _owner.ToHostNodeOrNull(element.Closest(selector));
                            }
                            catch (DomException)
                            {
                                return JsValue.Null;
                            }
                        },
                        length: 1);
                    return true;
                case "querySelector":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "querySelector",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return _owner.ToHostNodeOrNull(element.QuerySelector(selector));
                            }
                            catch (DomException)
                            {
                                return JsValue.Null;
                            }
                        },
                        length: 1);
                    return true;
                case "querySelectorAll":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "querySelectorAll",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(selector))
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }

                            try
                            {
                                return _owner.CreateNodeArrayLike(element.QuerySelectorAll(selector));
                            }
                            catch (DomException)
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }
                        },
                        length: 1);
                    return true;
                case "insertAdjacentHTML":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "insertAdjacentHTML",
                        (_, args) =>
                        {
                            var position = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var html = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            _owner.InsertAdjacentHtml(element, position, html);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetDomImplementationProperty(FenJsDomImplementationHost implementation, string property, out JsValue value)
        {
            switch (property)
            {
                case "hasFeature":
                    value = _owner.GetOrCreateHostCallable(
                        implementation,
                        "hasFeature",
                        (_, _) => JsValue.FromBoolean(true),
                        length: 2);
                    return true;
                case "createDocument":
                    value = _owner.GetOrCreateHostCallable(
                        implementation,
                        "createDocument",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            var qualifiedName = args.Count > 1 && args[1].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[1])
                                : null;
                            return _owner.ToHostOrNull(
                                Document.CreateXmlDocument(namespaceUri, qualifiedName),
                                HostObjectKind.DomDocument);
                        },
                        length: 2);
                    return true;
                case "createHTMLDocument":
                    value = _owner.GetOrCreateHostCallable(
                        implementation,
                        "createHTMLDocument",
                        (_, args) =>
                        {
                            var title = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            return _owner.ToHostOrNull(
                                Document.CreateHtmlDocument(title),
                                HostObjectKind.DomDocument);
                        },
                        length: 1);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetNamedNodeMapProperty(NamedNodeMap namedNodeMap, string property, out JsValue value)
        {
            switch (property)
            {
                case "length":
                    value = JsValue.FromInt32(namedNodeMap.Length);
                    return true;
                case "item":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "item",
                        (_, args) =>
                        {
                            var index = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex)
                                ? parsedIndex
                                : -1;
                            return _owner.ToHostOrNull(namedNodeMap.Item(index), HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "getNamedItem":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "getNamedItem",
                        (_, args) =>
                        {
                            var name = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostOrNull(namedNodeMap.GetNamedItem(name), HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "getNamedItemNS":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "getNamedItemNS",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            var localName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            return _owner.ToHostOrNull(namedNodeMap.GetNamedItemNS(namespaceUri, localName), HostObjectKind.Other);
                        },
                        length: 2);
                    return true;
                case "setNamedItem":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "setNamedItem",
                        (_, args) =>
                        {
                            var attr = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Attr>(args[0]) : null;
                            if (attr == null)
                            {
                                return JsValue.Null;
                            }

                            try
                            {
                                return _owner.ToHostOrNull(namedNodeMap.SetNamedItem(attr), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "setNamedItemNS":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "setNamedItemNS",
                        (_, args) =>
                        {
                            var attr = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Attr>(args[0]) : null;
                            if (attr == null)
                            {
                                return JsValue.Null;
                            }

                            try
                            {
                                return _owner.ToHostOrNull(namedNodeMap.SetNamedItemNS(attr), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "removeNamedItem":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "removeNamedItem",
                        (_, args) =>
                        {
                            var name = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return _owner.ToHostOrNull(namedNodeMap.RemoveNamedItem(name), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "removeNamedItemNS":
                    value = _owner.GetOrCreateHostCallable(
                        namedNodeMap,
                        "removeNamedItemNS",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 && args[0].Tag != JsValueTag.Null
                                ? CoerceToHostString(args[0])
                                : null;
                            var localName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            try
                            {
                                return _owner.ToHostOrNull(namedNodeMap.RemoveNamedItemNS(namespaceUri, localName), HostObjectKind.Other);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 2);
                    return true;
                default:
                    if (int.TryParse(property, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        value = _owner.ToHostOrNull(namedNodeMap.Item(index), HostObjectKind.Other);
                        return true;
                    }

                    var named = namedNodeMap.GetNamedItem(property);
                    if (named != null)
                    {
                        value = _owner.ToHostOrNull(named, HostObjectKind.Other);
                        return true;
                    }

                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetDomTokenListProperty(DOMTokenList tokenList, string property, out JsValue value)
        {
            switch (property)
            {
                case "length":
                    value = JsValue.FromInt32(tokenList.Length);
                    return true;
                case "value":
                    value = JsValue.FromString(tokenList.Value ?? string.Empty);
                    return true;
                case "item":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "item",
                        (_, args) =>
                        {
                            var index = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex)
                                ? parsedIndex
                                : -1;
                            var item = tokenList.Item(index);
                            return item == null ? JsValue.Null : JsValue.FromString(item);
                        },
                        length: 1);
                    return true;
                case "contains":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "contains",
                        (_, args) =>
                        {
                            var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return JsValue.FromBoolean(tokenList.Contains(token));
                        },
                        length: 1);
                    return true;
                case "add":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "add",
                        (_, args) =>
                        {
                            tokenList.Add(args.Select(CoerceToHostString).ToArray());
                            return JsValue.Undefined;
                        });
                    return true;
                case "remove":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "remove",
                        (_, args) =>
                        {
                            tokenList.Remove(args.Select(CoerceToHostString).ToArray());
                            return JsValue.Undefined;
                        });
                    return true;
                case "toggle":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "toggle",
                        (_, args) =>
                        {
                            var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            bool? force = null;
                            if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
                            {
                                force = CoerceToHostBoolean(args[1]);
                            }

                            return JsValue.FromBoolean(tokenList.Toggle(token, force));
                        },
                        length: 1);
                    return true;
                case "replace":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "replace",
                        (_, args) =>
                        {
                            var oldToken = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var newToken = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            return JsValue.FromBoolean(tokenList.Replace(oldToken, newToken));
                        },
                        length: 2);
                    return true;
                case "supports":
                    value = _owner.GetOrCreateHostCallable(
                        tokenList,
                        "supports",
                        (_, args) =>
                        {
                            var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return JsValue.FromBoolean(tokenList.Supports(token));
                        },
                        length: 1);
                    return true;
                default:
                    if (int.TryParse(property, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        var item = tokenList.Item(index);
                        value = item == null ? JsValue.Undefined : JsValue.FromString(item);
                        return true;
                    }

                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetAttrProperty(Attr attr, string property, out JsValue value)
        {
            switch (property)
            {
                case "name":
                case "nodeName":
                    value = JsValue.FromString(attr.Name ?? string.Empty);
                    return true;
                case "localName":
                    value = JsValue.FromString(attr.LocalName ?? string.Empty);
                    return true;
                case "prefix":
                    value = attr.Prefix == null ? JsValue.Null : JsValue.FromString(attr.Prefix);
                    return true;
                case "namespaceURI":
                    value = attr.NamespaceUri == null ? JsValue.Null : JsValue.FromString(attr.NamespaceUri);
                    return true;
                case "value":
                case "nodeValue":
                case "textContent":
                    value = JsValue.FromString(attr.Value ?? string.Empty);
                    return true;
                case "ownerElement":
                    value = _owner.ToHostOrNull(attr.OwnerElement, HostObjectKind.DomElement);
                    return true;
                case "specified":
                    value = JsValue.FromBoolean(attr.Specified);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetCharacterDataProperty(CharacterData characterData, string property, out JsValue value)
        {
            switch (property)
            {
                case "data":
                case "nodeValue":
                case "textContent":
                    value = JsValue.FromString(characterData.Data ?? string.Empty);
                    return true;
                case "length":
                    value = JsValue.FromInt32(characterData.Length);
                    return true;
                case "nodeName":
                    value = JsValue.FromString(characterData.NodeName ?? string.Empty);
                    return true;
                case "ownerDocument":
                    value = _owner.ToHostOrNull(characterData.OwnerDocument, HostObjectKind.DomDocument);
                    return true;
                case "parentNode":
                    value = _owner.ToHostNodeOrNull(characterData.ParentNode);
                    return true;
                case "nextSibling":
                    value = _owner.ToHostNodeOrNull(characterData.NextSibling);
                    return true;
                case "previousSibling":
                    value = _owner.ToHostNodeOrNull(characterData.PreviousSibling);
                    return true;
                case "appendData":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "appendData",
                        (_, args) =>
                        {
                            characterData.AppendData(args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty);
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "insertData":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "insertData",
                        (_, args) =>
                        {
                            var offset = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex) ? parsedIndex : 0;
                            var data = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            characterData.InsertData(offset, data);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "deleteData":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "deleteData",
                        (_, args) =>
                        {
                            var offset = args.Count > 0 && TryCoerceIndex(args[0], out var parsedOffset) ? parsedOffset : 0;
                            var count = args.Count > 1 && TryCoerceIndex(args[1], out var parsedCount) ? parsedCount : 0;
                            characterData.DeleteData(offset, count);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "replaceData":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "replaceData",
                        (_, args) =>
                        {
                            var offset = args.Count > 0 && TryCoerceIndex(args[0], out var parsedOffset) ? parsedOffset : 0;
                            var count = args.Count > 1 && TryCoerceIndex(args[1], out var parsedCount) ? parsedCount : 0;
                            var data = args.Count > 2 ? CoerceToHostString(args[2]) : string.Empty;
                            characterData.ReplaceData(offset, count, data);
                            return JsValue.Undefined;
                        },
                        length: 3);
                    return true;
                case "substringData":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "substringData",
                        (_, args) =>
                        {
                            var offset = args.Count > 0 && TryCoerceIndex(args[0], out var parsedOffset) ? parsedOffset : 0;
                            var count = args.Count > 1 && TryCoerceIndex(args[1], out var parsedCount) ? parsedCount : 0;
                            return JsValue.FromString(characterData.SubstringData(offset, count));
                        },
                        length: 2);
                    return true;
                case "remove":
                    value = _owner.GetOrCreateHostCallable(
                        characterData,
                        "remove",
                        (_, _) =>
                        {
                            characterData.Remove();
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetDocumentFragmentProperty(DocumentFragment fragment, string property, out JsValue value)
        {
            switch (property)
            {
                case "nodeName":
                    value = JsValue.FromString(fragment.NodeName ?? string.Empty);
                    return true;
                case "ownerDocument":
                    value = _owner.ToHostOrNull(fragment.OwnerDocument, HostObjectKind.DomDocument);
                    return true;
                case "parentNode":
                    value = _owner.ToHostNodeOrNull(fragment.ParentNode);
                    return true;
                case "firstChild":
                    value = _owner.ToHostNodeOrNull(fragment.FirstChild);
                    return true;
                case "appendChild":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "appendChild",
                        (_, args) =>
                        {
                            if (args.Count > 0 && _owner.ResolveHostObjectOrNull<Attr>(args[0]) != null)
                            {
                                _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                            }

                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                return JsValue.Null;
                            }

                            return _owner.ToHostNodeOrNull(fragment.AppendChild(child));
                        },
                        length: 1);
                    return true;
                case "append":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "append",
                        (_, args) =>
                        {
                            foreach (var arg in args)
                            {
                                Node child = _owner.ResolveHostObjectOrNull<Node>(arg);
                                if (child == null)
                                {
                                    child = fragment.OwnerDocument?.CreateTextNode(CoerceToHostString(arg));
                                }

                                if (child != null)
                                {
                                    fragment.AppendChild(child);
                                }
                            }

                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "getElementById":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "getElementById",
                        (_, args) =>
                        {
                            var id = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(fragment.GetElementById(id));
                        },
                        length: 1);
                    return true;
                case "querySelector":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "querySelector",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            try
                            {
                                return _owner.ToHostNodeOrNull(fragment.QuerySelector(selector));
                            }
                            catch (DomException)
                            {
                                return JsValue.Null;
                            }
                        },
                        length: 1);
                    return true;
                case "querySelectorAll":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "querySelectorAll",
                        (_, args) =>
                        {
                            var selector = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(selector))
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }

                            try
                            {
                                return _owner.CreateNodeArrayLike(fragment.QuerySelectorAll(selector));
                            }
                            catch (DomException)
                            {
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            }
                        },
                        length: 1);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetHtmlCollectionProperty(FenJsHtmlCollectionHost htmlCollection, string property, out JsValue value)
        {
            var collection = htmlCollection.Collection;
            switch (property)
            {
                case "length":
                    value = JsValue.FromInt32(collection.Length);
                    return true;
                case "item":
                    value = _owner.GetOrCreateHostCallable(
                        htmlCollection,
                        "item",
                        (_, args) =>
                        {
                            var index = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex)
                                ? parsedIndex
                                : -1;
                            return _owner.ToHostNodeOrNull(collection[index]);
                        },
                        length: 1);
                    return true;
                case "namedItem":
                    value = _owner.GetOrCreateHostCallable(
                        htmlCollection,
                        "namedItem",
                        (_, args) =>
                        {
                            var name = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(collection.NamedItem(name));
                        },
                        length: 1);
                    return true;
                default:
                    if (int.TryParse(property, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        value = _owner.ToHostNodeOrNull(collection[index]);
                        return true;
                    }

                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetDomStringMapProperty(FenJsDomStringMapHost domStringMap, string property, out JsValue value)
        {
            var attributeName = PropertyNameToDatasetAttribute(property);
            var attributeValue = domStringMap.Element.GetAttribute(attributeName);
            value = attributeValue == null ? JsValue.Undefined : JsValue.FromString(attributeValue);
            return true;
        }

        private static string PropertyNameToDatasetAttribute(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return "data-";
            }

            var builder = new StringBuilder("data-");
            for (var i = 0; i < propertyName.Length; i++)
            {
                var ch = propertyName[i];
                if (char.IsUpper(ch))
                {
                    builder.Append('-');
                    builder.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static bool TryCoerceIndex(JsValue value, out int index)
        {
            switch (value.Tag)
            {
                case JsValueTag.Int32:
                    index = value.AsInt32();
                    return true;
                case JsValueTag.Number:
                    var number = value.AsNumber();
                    if (double.IsFinite(number) && Math.Truncate(number) == number && number >= int.MinValue && number <= int.MaxValue)
                    {
                        index = (int)number;
                        return true;
                    }
                    break;
                case JsValueTag.String:
                    if (int.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    {
                        return true;
                    }
                    break;
            }

            index = -1;
            return false;
        }

        private static bool TryGetNavigatorProperty(BrowserSurfaceProfile navigator, string property, out JsValue value)
        {
            switch (property)
            {
                case "userAgent":
                    value = JsValue.FromString(navigator.UserAgent ?? string.Empty);
                    return true;
                case "platform":
                    value = JsValue.FromString(navigator.PlatformToken ?? string.Empty);
                    return true;
                case "vendor":
                    value = JsValue.FromString(navigator.Vendor ?? string.Empty);
                    return true;
                case "cookieEnabled":
                    value = JsValue.FromBoolean(navigator.CookieEnabled);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private static bool TryGetLocationProperty(FenJsLocationHost location, string property, out JsValue value)
        {
            var uri = location.Uri;
            var absolute = uri?.AbsoluteUri ?? string.Empty;
            switch (property)
            {
                case "href":
                    value = JsValue.FromString(absolute);
                    return true;
                case "origin":
                    value = JsValue.FromString(uri?.GetLeftPart(UriPartial.Authority) ?? string.Empty);
                    return true;
                case "protocol":
                    value = JsValue.FromString(uri?.Scheme is string scheme && scheme.Length > 0 ? scheme + ":" : string.Empty);
                    return true;
                case "host":
                    value = JsValue.FromString(uri?.IsDefaultPort == false ? $"{uri.Host}:{uri.Port}" : uri?.Host ?? string.Empty);
                    return true;
                case "hostname":
                    value = JsValue.FromString(uri?.Host ?? string.Empty);
                    return true;
                case "pathname":
                    value = JsValue.FromString(uri?.AbsolutePath ?? string.Empty);
                    return true;
                case "search":
                    value = JsValue.FromString(uri?.Query ?? string.Empty);
                    return true;
                case "hash":
                    value = JsValue.FromString(uri?.Fragment ?? string.Empty);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private static string ResolveElementUrlProperty(Element element, string attributeName)
        {
            var raw = element?.GetAttribute(attributeName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            {
                return absolute.AbsoluteUri;
            }

            var ownerDocument = element.OwnerDocument;
            var baseRaw =
                ownerDocument?.BaseURI ??
                ownerDocument?.DocumentURI ??
                ownerDocument?.URL;

            if (Uri.TryCreate(baseRaw, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, raw, out var resolved))
            {
                return resolved.AbsoluteUri;
            }

            return raw;
        }
    }
}

/// <summary>
/// Centralized runtime selection for browser script execution.
/// The browser now runs on the FenJS-backed runtime by default; the legacy
/// FenEngine runtime remains reachable only as an explicit escape hatch via
/// <c>FEN_BROWSER_SCRIPT_ENGINE=legacy</c> (rollback during the FenJS rollout)
/// and as the internal safety-net fallback inside <see cref="FenJsBrowserScriptEngine"/>.
/// </summary>
public static class BrowserScriptEngineRuntime
{
    private static Func<IJsHost, IBrowserScriptEngine> _factory = CreateConfiguredDefault;

    public static Func<IJsHost, IBrowserScriptEngine> Factory
    {
        get => _factory;
        set => _factory = value ?? CreateConfiguredDefault;
    }

    public static IBrowserScriptEngine Create(IJsHost host)
    {
        return _factory(host);
    }

    public static void Reset()
    {
        _factory = CreateConfiguredDefault;
    }

    private static IBrowserScriptEngine CreateLegacy(IJsHost host)
    {
        return new LegacyBrowserScriptEngineAdapter(new JavaScriptEngine(host));
    }

    private static IBrowserScriptEngine CreateFenJs(IJsHost host)
    {
        return new FenJsBrowserScriptEngine(host);
    }

    private static IBrowserScriptEngine CreateConfiguredDefault(IJsHost host)
    {
        // FenJS is the default browser script runtime. The legacy FenEngine
        // runtime stays reachable as an explicit rollback escape hatch:
        // FEN_BROWSER_SCRIPT_ENGINE=legacy (alias: fenengine).
        var mode = Environment.GetEnvironmentVariable("FEN_BROWSER_SCRIPT_ENGINE");
        if (string.Equals(mode, "legacy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "fenengine", StringComparison.OrdinalIgnoreCase))
        {
            return CreateLegacy(host);
        }

        return CreateFenJs(host);
    }
}
