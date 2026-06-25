using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Parsing;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Host;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using FenBrowser.Js.Parser;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Layout;
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
    Func<Element, object> LayoutBoxResolver { get; set; }
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
/// FenJS browser script engine — the sole JS runtime for the browser pipeline.
/// All page scripts execute through FenJS; there is no legacy fallback.
/// </summary>
public sealed class FenJsBrowserScriptEngine : IBrowserScriptEngine
{
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

    // Direct engine properties (were delegated to legacy adapter).
    private IExecutionContext _globalContext;
    private JavaScriptRuntimeProfile _runtimeProfile;
    private IHistoryBridge _historyBridge;
    private readonly IJsHost _host;

    public FenJsBrowserScriptEngine(IJsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _runtimeProfile = JavaScriptRuntimeProfile.Balanced;
        ResetFenJsSession();
    }

    internal int FenJsEvaluationCount => _fenJsEvaluationCount;

    public IExecutionContext GlobalContext
    {
        get => _globalContext;
        private set => _globalContext = value;
    }

    public JavaScriptRuntimeProfile RuntimeProfile
    {
        get => _runtimeProfile;
        set => _runtimeProfile = value ?? JavaScriptRuntimeProfile.Balanced;
    }

    public Func<Uri, Task<string>> FetchOverride { get; set; }
    public Func<Uri, string, bool> SubresourceAllowed { get; set; }
    public Func<string, bool> NonceAllowed { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> FetchHandler { get; set; }
    public Func<Uri, string> CookieReadBridge { get; set; }
    public Action<Uri, string> CookieWriteBridge { get; set; }
    public Action RequestRender { get; set; }
    public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }
    public Func<Element, object> LayoutBoxResolver { get; set; }
    public SandboxPolicy Sandbox { get; set; }
    public bool AllowExternalScripts { get; set; }
    public bool ExecuteInlineScriptsOnInnerHTML { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int PageScriptByteBudget { get; set; }

    public event Func<string, JsPermissions, Task<bool>> PermissionRequested;

    public void SetHistoryBridge(IHistoryBridge bridge)
    {
        _historyBridge = bridge;
    }

    public void CaptureNavigationGlobals(Uri documentUri, long navigationId)
    {
        // Navigation globals (window.location, history, etc.) are installed
        // via InstallFenJsDomGlobals during BindFenJsDomContext.
    }

    public void NotifyPopState(object state)
    {
        // popstate events are dispatched through the FenJS event system
        // when the history bridge fires.
    }

    public void DispatchEventForElement(Element element, string eventName)
    {
        if (element == null || string.IsNullOrWhiteSpace(eventName))
            return;

        DispatchElementEvent(element, eventName);

        // Look up event handler from the FenJS host property store and invoke it.
        var handler = GetStoredHostPropertyOrUndefined(element, "on" + eventName);
        if (_interpreter != null && _interpreter.CanCallValue(handler))
        {
            var eventObj = _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["type"] = JsValue.FromString(eventName),
                ["target"] = ToHostOrNull(element, HostObjectKind.DomElement),
                ["currentTarget"] = ToHostOrNull(element, HostObjectKind.DomElement)
            });
            InvokeFenJsCallback(handler, ToHostOrNull(element, HostObjectKind.DomElement), eventObj);
        }
    }

    public object Evaluate(string script)
    {
        if (CanEvaluateWithFenJs(script))
        {
            return EvaluateWithFenJs(script);
        }
        return null;
    }

    public void SyncDomContext(Node domRoot, Uri baseUri = null)
    {
        // Lightweight DOM sync for recascades/re-renders — update the cached
        // DOM root WITHOUT recreating the interpreter, so pending timers,
        // promises, and async state survive.  The host hooks' document
        // reference is kept alive by BindFenJsDomContext on initial load.
        if (domRoot == null) return;
        lock (_fenJsLock)
        {
            _currentDomRoot = domRoot;
            if (baseUri != null) _currentBaseUri = baseUri;
        }
    }

    public Task SetDomAsync(Node domRoot, Uri baseUri = null)
    {
        return SetDomAsyncCore(domRoot, baseUri);
    }

    private async Task SetDomAsyncCore(Node domRoot, Uri baseUri)
    {
        Console.Error.WriteLine($"[FenJsBridge] SetDomAsyncCore called, domRoot null? {domRoot == null}, baseUri={baseUri}");

        if (domRoot == null)
        {
            return;
        }

        BindFenJsDomContext(domRoot, baseUri, documentReadyState: "loading");
        Console.Error.WriteLine($"[FenJsBridge] About to execute page scripts, domRoot tag={((domRoot as Element)?.TagName ?? "null")}, descendantCount={domRoot.Descendants().Count()}");
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
                if (_compiler == null || _interpreter == null)
                {
                    throw new InvalidOperationException(
                        "[FenJsBridge] EvaluateWithFenJsRaw: compiler or interpreter is null — " +
                        "BindFenJsDomContext/ResetFenJsSession may not have run yet. " +
                        $"_compiler={_compiler != null} _interpreter={_interpreter != null}");
                }
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

    // Persistent large-stack worker thread — created once per engine instance
    // and reused across all page-script evaluations.  Creating+joining a 256 MB
    // thread per script (60+ for a typical SPA) wastes ~500 ms per page load in
    // thread start/stop overhead alone; reusing the same thread cuts that to
    // near-zero after the first evaluation.
    private Thread _fenJsWorkerThread;
    private readonly AutoResetEvent _fenJsWorkAvailable = new AutoResetEvent(false);
    private readonly AutoResetEvent _fenJsWorkDone = new AutoResetEvent(false);
    private readonly object _fenJsWorkGate = new object();
    private Func<object> _fenJsPendingWork;
    private object _fenJsWorkResult;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo _fenJsWorkException;
    private bool _fenJsWorkerRunning;

    private void EnsureFenJsWorkerRunning()
    {
        if (_fenJsWorkerThread != null && _fenJsWorkerThread.IsAlive)
            return;

        _fenJsWorkerRunning = true;
        _fenJsWorkerThread = new Thread(FenJsWorkerLoop, FenJsLargeStackBytes)
        {
            IsBackground = true,
            Name = "FenJs-LargeStack"
        };
        _fenJsWorkerThread.Start();
    }

    private void FenJsWorkerLoop()
    {
        _onFenJsLargeStackThread = true;
        while (_fenJsWorkerRunning)
        {
            _fenJsWorkAvailable.WaitOne();
            if (!_fenJsWorkerRunning) break;

            try
            {
                _fenJsWorkResult = _fenJsPendingWork();
            }
            catch (Exception ex)
            {
                _fenJsWorkException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            _fenJsWorkDone.Set();
        }
    }

    private T RunFenJsWithLargeStack<T>(Func<T> work)
    {
        if (_onFenJsLargeStackThread)
        {
            // Re-entrant call from within the large-stack thread itself —
            // run inline to avoid deadlocking the persistent worker.
            return work();
        }

        EnsureFenJsWorkerRunning();

        // Serialise work dispatch so only one caller posts at a time.
        // The C# lock on _fenJsWorkGate is brief (just the handshake);
        // the real serialisation boundary is _fenJsLock taken inside
        // the work function itself on the large-stack thread.
        lock (_fenJsWorkGate)
        {
            _fenJsWorkException = null;
            _fenJsWorkResult = null;
            _fenJsPendingWork = () => (object)work();
            _fenJsWorkAvailable.Set();
        }

        _fenJsWorkDone.WaitOne();

        var captured = _fenJsWorkException;
        _fenJsWorkException = null;
        captured?.Throw();
        return (T)_fenJsWorkResult;
    }

    private async Task ExecutePageScriptsWithFenJsAsync(Node domRoot, Uri baseUri)
    {
        Console.Error.WriteLine($"[FenJsBridge] ExecutePageScriptsWithFenJsAsync START, domRoot={domRoot != null}, baseUri={baseUri}");
        try
        {
            var allScripts = EnumerateScriptElements(domRoot).ToList();
            Console.Error.WriteLine($"[FenJsBridge] EnumerateScriptElements DONE: totalScripts={allScripts.Count}");

            // Phase 1: validate all scripts and kick off external fetches concurrently.
            // Each entry holds everything needed to execute the script in order.
            var items = new List<ScriptExecutionItem>(allScripts.Count);
            var fetchTasks = new Dictionary<string, Task<string>>(StringComparer.Ordinal);

            for (int i = 0; i < allScripts.Count; i++)
            {
                var scriptElement = allScripts[i];
                Console.Error.WriteLine($"[FenJsBridge] Script #{i + 1}: tag={scriptElement.TagName}, src={scriptElement.GetAttribute("src")}, type={scriptElement.GetAttribute("type")}, textLen={scriptElement.TextContent?.Length ?? -1}");

                var type = scriptElement.GetAttribute("type")?.ToLowerInvariant() ?? string.Empty;
                if (!string.IsNullOrEmpty(type) &&
                    type != "text/javascript" &&
                    type != "application/javascript" &&
                    type != "module")
                {
                    if (type != "application/ld+json")
                        FenBrowser.Core.EngineLogCompat.Debug(
                            $"[FenJsBridge] Skipping script with unknown type '{type}': src={scriptElement.GetAttribute("src")}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                    continue;
                }

                if (scriptElement.HasAttribute("nomodule"))
                {
                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenJsBridge] Skipping nomodule script: src={scriptElement.GetAttribute("src")}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);
                    continue;
                }

                bool isModule = type == "module";
                var src = scriptElement.GetAttribute("src");

                if (!string.IsNullOrEmpty(src))
                {
                    // External script — validate, then kick off fetch concurrently
                    Console.Error.WriteLine($"[FenJsBridge] Phase1 external {(isModule ? "module" : "script")}: src={src}");

                    if (!AllowExternalScripts || !Sandbox.Allows(SandboxFeature.ExternalScripts))
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsBridge] Skipping external script (AllowExternal={AllowExternalScripts}, SandboxExternal={Sandbox.Allows(SandboxFeature.ExternalScripts)}): {src}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                        continue;
                    }

                    if (baseUri == null || !Uri.TryCreate(baseUri, src, out var scriptUri))
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsBridge] Skipping script with unresolvable src (baseUri={baseUri}): {src}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                        continue;
                    }

                    if (SubresourceAllowed != null && !SubresourceAllowed(scriptUri, "script"))
                    {
                        var nonce = scriptElement.GetAttribute("nonce");
                        if (string.IsNullOrEmpty(nonce) || NonceAllowed == null || !NonceAllowed(nonce))
                        {
                            FenBrowser.Core.EngineLogCompat.Warn(
                                $"[FenJsBridge] Skipping script due to SubresourceAllowed (CSP) block: {scriptUri}",
                                FenBrowser.Core.Logging.LogCategory.JavaScript);
                            continue;
                        }
                        FenBrowser.Core.EngineLogCompat.Info(
                            $"[FenJsBridge] External script allowed via nonce bypass: {scriptUri}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                    }

                    // Deduplicate: same URL → same fetch task
                    var fetchKey = scriptUri.AbsoluteUri;
                    if (!fetchTasks.TryGetValue(fetchKey, out var fetchTask))
                    {
                        fetchTask = FetchExternalPageScriptAsync(scriptUri, baseUri);
                        fetchTasks[fetchKey] = fetchTask;
                    }

                    items.Add(new ScriptExecutionItem(scriptElement, isModule, fetchKey, fetchTask, scriptUri));
                }
                else
                {
                    // Inline script — validate now, code is already in DOM
                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenJsBridge] Processing inline {(isModule ? "module" : "script")} len={scriptElement.TextContent?.Length}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);

                    if (!Sandbox.Allows(SandboxFeature.InlineScripts))
                    {
                        continue;
                    }

                    var code = CollectScriptText(scriptElement);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    items.Add(new ScriptExecutionItem(scriptElement, isModule, null, null, null, code));
                }
            }

            // Wait for all external fetches to complete before executing
            if (fetchTasks.Count > 0)
            {
                Console.Error.WriteLine($"[FenJsBridge] Waiting for {fetchTasks.Count} external script fetches to complete...");
                await Task.WhenAll(fetchTasks.Values).ConfigureAwait(false);
                Console.Error.WriteLine($"[FenJsBridge] All {fetchTasks.Count} external fetches complete.");
            }

            // Phase 2: execute scripts in document order
            Console.Error.WriteLine($"[FenJsBridge] Phase 2: executing {items.Count} scripts in document order");
            var scriptCount = 0;
            foreach (var item in items)
            {
                scriptCount++;
                string code;
                Uri moduleUri = item.ModuleUri;
                bool isModule = item.IsModule;

                if (item.FetchKey != null)
                {
                    // Get the pre-fetched result
                    var fetchTask = item.FetchTask;
                    if (fetchTask.IsFaulted)
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsBridge] Fetch failed for '{item.FetchKey}': {fetchTask.Exception?.InnerException?.Message}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                        continue;
                    }
                    code = fetchTask.Result;
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }
                }
                else
                {
                    code = item.InlineCode;
                }

                try
                {
                    SetCurrentScriptElement(item.ScriptElement);
                    if (isModule)
                    {
                        EvaluateModuleWithFenJs(code, moduleUri);
                    }
                    else
                    {
                        EvaluateWithFenJsRaw(code);
                    }
                }
                finally
                {
                    SetCurrentScriptElement(null);
                }
            }
            Console.Error.WriteLine($"[FenJsBridge] ExecutePageScriptsWithFenJsAsync DONE, scripts processed={scriptCount}");
            LogFenJsPageBootstrapState();
        }
        catch (Exception ex)
        {
            if (ex is JsThrownException jte && string.IsNullOrEmpty(jte.Description))
            {
                try { jte.Description = _interpreter.DescribeThrownValue(jte.Value); } catch { }
            }
            Console.Error.WriteLine($"[FenJsBridge] ExecutePageScriptsWithFenJsAsync EXCEPTION: {ex.Message}");
            throw;
        }
    }

    private void LogFenJsPageBootstrapState()
    {
        try
        {
            var state = EvaluateWithFenJsRaw(
                """
                (function () {
                    var waf = globalThis.AwsWafIntegration;
                    if (!waf) return 'AwsWafIntegration=<missing>';
                    return 'AwsWafIntegration=' + typeof waf +
                        ';checkForceRefresh=' + typeof waf.checkForceRefresh +
                        ';getToken=' + typeof waf.getToken +
                        ';forceRefreshToken=' + typeof waf.forceRefreshToken +
                        ';hasToken=' + typeof waf.hasToken;
                })()
                """);
            EvaluateWithFenJsRaw(
                """
                (function () {
                    var waf = globalThis.AwsWafIntegration;
                    if (!waf ||
                        typeof waf.hasToken !== 'function' ||
                        typeof waf.checkForceRefresh !== 'function') return;
                    try {
                        globalThis.__fenWafCheckProbe = 'pending';
                        waf.checkForceRefresh().then(function (value) {
                            globalThis.__fenWafCheckProbe = 'resolved:' + String(value);
                        }, function (error) {
                            globalThis.__fenWafCheckProbe = 'rejected:' + String(error && error.message ? error.message : error);
                        });
                        globalThis.__fenWafForceProbe = 'pending';
                        waf.forceRefreshToken().then(function (value) {
                            globalThis.__fenWafForceProbe = 'resolved:' + String(value);
                        }, function (error) {
                            globalThis.__fenWafForceProbe = 'rejected:' + String(error && error.message ? error.message : error);
                        });
                    } catch (error) {
                        globalThis.__fenWafCheckProbe = 'threw:' + String(error && error.message ? error.message : error);
                    }
                })()
                """);
            _interpreter.PumpMicrotasks();
            var checkProbe = EvaluateWithFenJsRaw("String(globalThis.__fenWafCheckProbe)");
            var forceProbe = EvaluateWithFenJsRaw("String(globalThis.__fenWafForceProbe)");
            FenBrowser.Core.EngineLogCompat.Info(
                "[FenJsBridge] Page bootstrap state: " + CoerceToHostString(state) +
                ";checkProbe=" + CoerceToHostString(checkProbe) +
                ";forceProbe=" + CoerceToHostString(forceProbe),
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                "[FenJsBridge] Page bootstrap state probe failed: " + ex.Message,
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
    }

    private sealed class ScriptExecutionItem
    {
        public readonly Element ScriptElement;
        public readonly bool IsModule;
        public readonly string FetchKey;        // non-null for external scripts
        public readonly Task<string> FetchTask; // non-null for external scripts
        public readonly Uri ModuleUri;          // non-null for external scripts
        public readonly string InlineCode;      // non-null for inline scripts

        public ScriptExecutionItem(Element scriptElement, bool isModule, string fetchKey, Task<string> fetchTask, Uri moduleUri, string inlineCode = null)
        {
            ScriptElement = scriptElement;
            IsModule = isModule;
            FetchKey = fetchKey;
            FetchTask = fetchTask;
            ModuleUri = moduleUri;
            InlineCode = inlineCode;
        }
    }

    private string FetchModuleTextSync(Uri uri)
    {
        if (uri == null) return string.Empty;
        try
        {
            if (ExternalScriptFetcher != null)
            {
                return ExternalScriptFetcher(uri, _currentBaseUri).GetAwaiter().GetResult() ?? string.Empty;
            }
            if (FetchOverride != null)
            {
                return FetchOverride(uri).GetAwaiter().GetResult() ?? string.Empty;
            }
            if (FetchHandler != null)
            {
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                if (_currentBaseUri != null)
                {
                    request.Headers.Referrer = _currentBaseUri;
                    if (!CorsHandler.IsSameOrigin(uri, _currentBaseUri))
                    {
                        var origin = CorsHandler.SerializeOrigin(new UriBuilder(
                            _currentBaseUri.Scheme,
                            _currentBaseUri.Host,
                            _currentBaseUri.IsDefaultPort ? -1 : _currentBaseUri.Port).Uri);
                        if (!string.IsNullOrWhiteSpace(origin))
                        {
                            request.Headers.TryAddWithoutValidation("Origin", origin);
                        }
                    }
                }
                using var response = FetchHandler(request).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn($"[FenJsBridge] FetchModuleTextSync failed for '{uri}': {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
        return string.Empty;
    }

    /// <summary>
    /// Parse and execute a script in ES module goal (allowing import/export syntax).
    /// Uses ModuleEvaluator to resolve, link, rewrite, and evaluate module dependencies recursively.
    /// </summary>
    private void EvaluateModuleWithFenJs(string code, Uri moduleUri = null)
    {
        RunFenJsWithLargeStack<object>(() =>
        {
            lock (_fenJsLock)
            {
                _fenJsEvaluationCount++;
                Uri moduleBase = moduleUri ?? _currentBaseUri;
                var evaluator = new FenBrowser.Js.Modules.ModuleEvaluator(_interpreter, specifier =>
                {
                    Uri resolvedUri = null;
                    if (Uri.TryCreate(specifier, UriKind.Absolute, out var absUri))
                    {
                        resolvedUri = absUri;
                    }
                    else if (moduleBase != null)
                    {
                        Uri.TryCreate(moduleBase, specifier, out resolvedUri);
                    }

                    if (resolvedUri != null)
                    {
                        if (SubresourceAllowed != null && !SubresourceAllowed(resolvedUri, "script"))
                        {
                            return null;
                        }
                        return FetchModuleTextSync(resolvedUri);
                    }
                    return null;
                });

                const string entrySpecifier = "<entry>";
                evaluator.RegisterSource(entrySpecifier, code);
                evaluator.Evaluate(entrySpecifier);
                return null;
            }
        });
    }

    private async Task<string> FetchExternalPageScriptAsync(Uri scriptUri, Uri referer)
    {
        if (scriptUri == null)
        {
            return null;
        }

        string code = null;

        if (ExternalScriptFetcher != null)
        {
            code = await ExternalScriptFetcher(scriptUri, referer).ConfigureAwait(false);
            FenBrowser.Core.EngineLogCompat.Info($"[FenJsBridge] ExternalScriptFetcher result for '{scriptUri}': len={code?.Length ?? -1}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            return code;
        }

        if (FetchOverride != null)
        {
            code = await FetchOverride(scriptUri).ConfigureAwait(false);
            FenBrowser.Core.EngineLogCompat.Info($"[FenJsBridge] FetchOverride result for '{scriptUri}': len={code?.Length ?? -1}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            return code;
        }

        if (FetchHandler == null)
        {
            FenBrowser.Core.EngineLogCompat.Warn($"[FenJsBridge] FetchHandler is null, cannot fetch script: {scriptUri}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
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
            code = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            FenBrowser.Core.EngineLogCompat.Info($"[FenJsBridge] FetchHandler result for '{scriptUri}': HTTP {response.StatusCode}, len={code?.Length ?? -1}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            return code;
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn($"[FenJsBridge] FetchExternalPageScriptAsync failed for '{scriptUri}': {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            return null;
        }
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
                WallClockTimeoutMs = ResolveFenJsScriptTimeoutMs(),
                MaxCallDepth = 256
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
        var navigator = CreateNavigatorHost();
        var location = new FenJsLocationHost(baseUri ?? TryCreateUri(document?.URL));
        // Always bind the host hooks so _owner is set — even if there's no
        // document, host-property resolution must not NRE when it looks up
        // _owner._interpreter.  Without this, a page that triggers a recascade
        // before the DOM is fully attached leaves the hooks orphaned.
        _hostHooks.Bind(
            this,
            document,
            navigator,
            location,
            baseUri ?? TryCreateUri(document?.URL));

        if (document == null)
        {
            return;
        }

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
        InstallFenJsMutationObserver();
        InstallFenJsRemainingWebApis();
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
        FenBrowser.Core.EngineLogCompat.Debug(
            $"[FenJsTimers] Scheduled {(repeat ? "interval" : "timeout")} id={id} delayMs={delayMs} callback={callback.Tag}",
            FenBrowser.Core.Logging.LogCategory.JavaScript);
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
                Element.prototype.getElementsByTagName = function () { return this.getElementsByTagName.apply(this, arguments); };
                Element.prototype.appendChild = function () { return this.appendChild.apply(this, arguments); };
                Element.prototype.cloneNode = function () { return this.cloneNode.apply(this, arguments); };

                HTMLElement.prototype.matches = Element.prototype.matches;
                HTMLElement.prototype.closest = Element.prototype.closest;
                HTMLElement.prototype.getElementsByTagName = Element.prototype.getElementsByTagName;
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

    /// <summary>
    /// Installs the MutationObserver constructor on the JS global scope.
    /// https://dom.spec.whatwg.org/#interface-mutationobserver
    /// </summary>
    private void InstallFenJsMutationObserver()
    {
        // Allocate the native constructor. The `construct` delegate is called when
        // JS does `new MutationObserver(callback)`.
        var mutationObserverCtor = _interpreter.AllocateNativeConstructor(
            "MutationObserver",
            // [[Call]] — not construct; throw TypeError per spec § "MutationObserver()"
            (_, _) =>
            {
                ThrowDomException("TypeError",
                    "MutationObserver constructor: 'new' is required.");
                return JsValue.Undefined;
            },
            // [[Construct]]
            args =>
            {
                if (args.Count == 0 || !_interpreter.CanCallValue(args[0]))
                {
                    ThrowDomException("TypeError",
                        "Failed to construct 'MutationObserver': 1 argument required, but only 0 present.");
                    return JsValue.Undefined; // unreachable, ThrowDomException throws
                }

                var callback = args[0];
                var host = new FenJsMutationObserverHost(callback, this);
                return ToHostOrNull(host, HostObjectKind.Other);
            },
            length: 1);

        _interpreter.RegisterGlobalValue("MutationObserver", mutationObserverCtor);
    }

    /// <summary>
    /// Installs stub implementations of Web APIs that Facebook requires for its
    /// bootstrap but which are not yet fully implemented in the engine.  Each stub
    /// provides the minimum API surface needed to prevent ReferenceError crashes
    /// while returning safe defaults (empty entries, no-ops, passthrough URLs).
    /// </summary>
    private void InstallFenJsRemainingWebApis()
    {
        _interpreter.RegisterGlobalValue(
            "__fenSyncXhr",
            _interpreter.AllocateNativeFunction(
                "__fenSyncXhr",
                (_, args) => ExecuteSynchronousXmlHttpRequest(args)));
        _interpreter.RegisterGlobalValue(
            "__fenSyncFetch",
            _interpreter.AllocateNativeFunction(
                "__fenSyncFetch",
                (_, args) => ExecuteSynchronousFetchRequest(args)));
        _interpreter.RegisterGlobalValue(
            "__fenRandomByte",
            _interpreter.AllocateNativeFunction(
                "__fenRandomByte",
                (_, _) => JsValue.FromNumber(RandomNumberGenerator.GetInt32(0, 256))));

        EvaluateWithFenJsRaw(
            """
            (function () {
                // ── Blob ── https://w3c.github.io/FileAPI/#blob-section
                // Facebook uses new Blob([data], {type: ...}) with sendBeacon.
                // Minimal stub: stores parts+type; size is always 0.
                globalThis.Blob = function Blob(parts, options) {
                    this._parts = parts || [];
                    this._type = (options && options.type) || '';
                    this.size = 0;
                    this.type = this._type;
                };
                Blob.prototype.slice = function (start, end, contentType) {
                    return new Blob(this._parts.slice(start || 0, end), { type: contentType || this._type });
                };
                Blob.prototype.text = function () {
                    return Promise.resolve(this._parts.join(''));
                };
                Blob.prototype.arrayBuffer = function () {
                    return Promise.resolve(new ArrayBuffer(0));
                };

                // ── trustedTypes ── https://w3c.github.io/trusted-types/dist/spec/
                // Facebook creates a "comet-deferred-scripts" policy to safely
                // create script URLs for deferred bundle loading.  Without this,
                // the deferred-script processor silently skips all dynamic script
                // injection and the React app never mounts.
                var _trustedPolicies = Object.create(null);
                globalThis.trustedTypes = {
                    createPolicy: function (name, rules) {
                        if (_trustedPolicies[name]) {
                            throw new TypeError("TrustedTypes policy '" + name + "' already exists.");
                        }
                        var policy = { name: name };
                        if (rules) {
                            Object.keys(rules).forEach(function (key) {
                                policy[key] = rules[key];
                            });
                        }
                        _trustedPolicies[name] = policy;
                        return policy;
                    },
                    getPolicyNames: function () {
                        return Object.keys(_trustedPolicies);
                    },
                    getAttributeType: function () { return null; },
                    getPropertyType: function () { return null; },
                    isHTML: function () { return false; },
                    isScript: function () { return false; },
                    isScriptURL: function () { return false; },
                    emptyHTML: '',
                    emptyScript: '',
                    emptyScriptURL: ''
                };

                // ── IntersectionObserver ── https://w3c.github.io/IntersectionObserver/
                // Facebook uses this for lazy-loading images and deferred content.
                // Stub: accepts observe/unobserve/disconnect but never fires callbacks.
                globalThis.IntersectionObserver = function IntersectionObserver(callback, options) {
                    this._callback = callback;
                    this._targets = [];
                    this.root = (options && options.root) || null;
                    this.rootMargin = (options && options.rootMargin) || '0px';
                    this.thresholds = (options && options.threshold) || [0];
                    if (!Array.isArray(this.thresholds)) {
                        this.thresholds = [this.thresholds];
                    }
                };
                IntersectionObserver.prototype.observe = function (target) {
                    if (this._targets.indexOf(target) < 0) {
                        this._targets.push(target);
                    }
                };
                IntersectionObserver.prototype.unobserve = function (target) {
                    var idx = this._targets.indexOf(target);
                    if (idx >= 0) { this._targets.splice(idx, 1); }
                };
                IntersectionObserver.prototype.disconnect = function () {
                    this._targets.length = 0;
                };
                IntersectionObserver.prototype.takeRecords = function () {
                    return [];
                };

                // ── ResizeObserver ── https://drafts.csswg.org/resize-observer/
                // Facebook uses this to track element size changes.
                // Stub: accepts observe/unobserve/disconnect but never fires callbacks.
                globalThis.ResizeObserver = function ResizeObserver(callback) {
                    this._callback = callback;
                    this._targets = [];
                };
                ResizeObserver.prototype.observe = function (target, options) {
                    if (this._targets.indexOf(target) < 0) {
                        this._targets.push(target);
                    }
                };
                ResizeObserver.prototype.unobserve = function (target) {
                    var idx = this._targets.indexOf(target);
                    if (idx >= 0) { this._targets.splice(idx, 1); }
                };
                ResizeObserver.prototype.disconnect = function () {
                    this._targets.length = 0;
                };

                // ── Event ── https://dom.spec.whatwg.org/#interface-event
                // GitHub uses new Event('click'), new Event('DOMContentLoaded'), etc.
                // Minimal constructor: stores type + options, supports stopPropagation /
                // preventDefault / stopImmediatePropagation.
                globalThis.Event = function Event(type, options) {
                    if (typeof type !== 'string') {
                        throw new TypeError("Failed to construct 'Event': 1 argument required.");
                    }
                    options = options || {};
                    this.type = type;
                    this.bubbles = !!options.bubbles;
                    this.cancelable = !!options.cancelable;
                    this.composed = !!options.composed;
                    this.defaultPrevented = false;
                    this.cancelBubble = false;
                    this.returnValue = true;
                    this.eventPhase = 0;
                    this.isTrusted = false;
                    this.target = null;
                    this.currentTarget = null;
                    this.srcElement = null;
                    this.timeStamp = Date.now();
                    this._propagationStopped = false;
                    this._immediatePropagationStopped = false;
                };
                Event.prototype.stopPropagation = function () {
                    this._propagationStopped = true;
                };
                Event.prototype.stopImmediatePropagation = function () {
                    this._propagationStopped = true;
                    this._immediatePropagationStopped = true;
                };
                Event.prototype.preventDefault = function () {
                    if (this.cancelable) {
                        this.defaultPrevented = true;
                    }
                };
                Event.prototype.composedPath = function () {
                    return [];
                };
                Event.NONE = 0;
                Event.CAPTURING_PHASE = 1;
                Event.AT_TARGET = 2;
                Event.BUBBLING_PHASE = 3;

                // ── CustomEvent ── https://dom.spec.whatwg.org/#interface-customevent
                globalThis.CustomEvent = function CustomEvent(type, options) {
                    Event.call(this, type, options);
                    options = options || {};
                    this.detail = options.detail !== undefined ? options.detail : null;
                };
                CustomEvent.prototype = Object.create(Event.prototype);
                CustomEvent.prototype.constructor = CustomEvent;

                // ── XMLHttpRequest ── https://xhr.spec.whatwg.org/
                // Amazon and many sites use XHR for API calls.  Stub that fires
                // onerror immediately so callers can handle the failure gracefully.
                globalThis.XMLHttpRequest = function XMLHttpRequest() {
                    this.readyState = 0;
                    this.status = 0;
                    this.statusText = '';
                    this.responseText = '';
                    this.responseXML = null;
                    this.response = null;
                    this.responseType = '';
                    this.timeout = 0;
                    this.withCredentials = false;
                    this.upload = {};
                    this.onreadystatechange = null;
                    this.onload = null;
                    this.onerror = null;
                    this.onabort = null;
                    this.ontimeout = null;
                    this.onloadend = null;
                    this.onloadstart = null;
                    this.onprogress = null;
                    this._requestHeaders = {};
                    this._listeners = {};
                    this._aborted = false;
                };
                XMLHttpRequest.UNSENT = 0;
                XMLHttpRequest.OPENED = 1;
                XMLHttpRequest.HEADERS_RECEIVED = 2;
                XMLHttpRequest.LOADING = 3;
                XMLHttpRequest.DONE = 4;
                XMLHttpRequest.prototype.open = function (method, url, async, user, password) {
                    this._method = method;
                    this._url = url;
                    this._async = async !== false;
                    this.readyState = XMLHttpRequest.OPENED;
                };
                XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
                    this._requestHeaders[name] = value;
                };
                XMLHttpRequest.prototype.addEventListener = function (type, callback) {
                    if (!type || typeof callback !== 'function') return;
                    type = String(type);
                    (this._listeners[type] || (this._listeners[type] = [])).push(callback);
                };
                XMLHttpRequest.prototype.removeEventListener = function (type, callback) {
                    type = String(type);
                    var listeners = this._listeners[type];
                    if (!listeners) return;
                    for (var i = listeners.length - 1; i >= 0; i--) {
                        if (listeners[i] === callback) listeners.splice(i, 1);
                    }
                };
                XMLHttpRequest.prototype._dispatch = function (type) {
                    var event = new Event(type);
                    event.target = this;
                    event.currentTarget = this;
                    var handler = this['on' + type];
                    if (typeof handler === 'function') handler.call(this, event);
                    var listeners = (this._listeners[type] || []).slice();
                    for (var i = 0; i < listeners.length; i++) {
                        listeners[i].call(this, event);
                    }
                };
                XMLHttpRequest.prototype.send = function (body) {
                    var self = this;
                    self._dispatch('loadstart');
                    if (typeof __fenSyncXhr === 'function') {
                        var result = __fenSyncXhr(self._method || 'GET', self._url || '', body === undefined ? '' : String(body), self._requestHeaders || {});
                        self.readyState = XMLHttpRequest.DONE;
                        self.status = result && typeof result.status === 'number' ? result.status : 0;
                        self.statusText = result && result.statusText ? String(result.statusText) : '';
                        self.responseText = result && result.responseText ? String(result.responseText) : '';
                        self.response = self.responseText;
                        if (self.onreadystatechange) self.onreadystatechange();
                        self._dispatch(self.status >= 200 && self.status < 400 ? 'load' : 'error');
                        self._dispatch('loadend');
                        return;
                    }
                    self.readyState = XMLHttpRequest.DONE;
                    self.status = 0;
                    self.statusText = 'Network Error (stub)';
                    if (self.onreadystatechange) self.onreadystatechange();
                    self._dispatch('error');
                    self._dispatch('loadend');
                };
                XMLHttpRequest.prototype.abort = function () {
                    this._aborted = true;
                    this._dispatch('abort');
                    this._dispatch('loadend');
                };
                XMLHttpRequest.prototype.getResponseHeader = function (name) {
                    return null;
                };
                XMLHttpRequest.prototype.getAllResponseHeaders = function () {
                    return '';
                };
                XMLHttpRequest.prototype.overrideMimeType = function (mime) {};

                // ── AbortSignal / AbortController ── https://dom.spec.whatwg.org/#abortcontroller
                // GitHub uses fetch() with { signal: AbortSignal.timeout(...) }.
                globalThis.AbortSignal = function AbortSignal() {
                    this.aborted = false;
                    this.reason = undefined;
                    this.onabort = null;
                };
                AbortSignal.prototype.throwIfAborted = function () {
                    if (this.aborted) throw this.reason || new DOMException('The operation was aborted.', 'AbortError');
                };
                AbortSignal.timeout = function (ms) {
                    var signal = new AbortSignal();
                    setTimeout(function () {
                        signal.aborted = true;
                        signal.reason = new DOMException('The operation was aborted due to timeout.', 'TimeoutError');
                        if (signal.onabort) signal.onabort(new Event('abort'));
                    }, ms);
                    return signal;
                };
                AbortSignal.any = function (signals) {
                    var signal = new AbortSignal();
                    if (signals && signals.length) {
                        signals.forEach(function (s) {
                            if (s && s.aborted) {
                                signal.aborted = true;
                                signal.reason = s.reason;
                            }
                        });
                    }
                    return signal;
                };

                globalThis.AbortController = function AbortController() {
                    this.signal = new AbortSignal();
                };
                AbortController.prototype.abort = function (reason) {
                    if (this.signal.aborted) return;
                    this.signal.aborted = true;
                    this.signal.reason = reason || new DOMException('The operation was aborted.', 'AbortError');
                    if (this.signal.onabort) this.signal.onabort(new Event('abort'));
                };

                // ── customElements ── https://html.spec.whatwg.org/#custom-elements
                // GitHub uses customElements.define() for web components.
                var cryptoObject = globalThis.crypto || {};
                cryptoObject.getRandomValues = function (array) {
                    if (!array || typeof array.length !== 'number') {
                        throw new TypeError("Failed to execute 'getRandomValues': argument must be an integer typed array.");
                    }
                    if (array.length > 65536) {
                        throw new DOMException("The ArrayBufferView's byte length exceeds the number of bytes of entropy available via this API.", "QuotaExceededError");
                    }
                    for (var i = 0; i < array.length; i++) {
                        array[i] = __fenRandomByte();
                    }
                    return array;
                };
                globalThis.crypto = cryptoObject;

                globalThis.customElements = {
                    _registry: Object.create(null),
                    define: function (name, constructor, options) {
                        if (this._registry[name]) {
                            throw new DOMException("Failed to execute 'define': '" + name + "' has already been defined.", "NotSupportedError");
                        }
                        this._registry[name] = { constructor: constructor, options: options };
                    },
                    get: function (name) {
                        var entry = this._registry[name];
                        return entry ? entry.constructor : undefined;
                    },
                    whenDefined: function (name) {
                        if (this._registry[name]) {
                            return Promise.resolve(this._registry[name].constructor);
                        }
                        return new Promise(function () {}); // never resolves (stub)
                    },
                    upgrade: function (root) {}
                };

                // ── DOMException ──
                globalThis.DOMException = function DOMException(message, name) {
                    this.message = message || '';
                    this.name = name || 'Error';
                };
                DOMException.prototype = Object.create(Error.prototype);
                DOMException.prototype.constructor = DOMException;

                // ── fetch ── Minimal stub that rejects with a network error.
                // GitHub and many sites use fetch() for API calls.
                function normalizeHeaderName(name) {
                    return String(name).toLowerCase();
                }

                globalThis.Headers = function Headers(init) {
                    this._map = {};
                    if (init instanceof Headers) {
                        var source = init._map;
                        for (var k in source) this._map[k] = source[k];
                    } else if (Array.isArray(init)) {
                        for (var i = 0; i < init.length; i++) {
                            if (init[i] && init[i].length >= 2) this.append(init[i][0], init[i][1]);
                        }
                    } else if (init) {
                        for (var name in init) this.append(name, init[name]);
                    }
                };
                Headers.prototype.append = function (name, value) {
                    name = normalizeHeaderName(name);
                    value = String(value);
                    this._map[name] = this._map[name] ? this._map[name] + ', ' + value : value;
                };
                Headers.prototype.set = function (name, value) {
                    this._map[normalizeHeaderName(name)] = String(value);
                };
                Headers.prototype.get = function (name) {
                    name = normalizeHeaderName(name);
                    return Object.prototype.hasOwnProperty.call(this._map, name) ? this._map[name] : null;
                };
                Headers.prototype.has = function (name) {
                    return Object.prototype.hasOwnProperty.call(this._map, normalizeHeaderName(name));
                };
                Headers.prototype.delete = function (name) {
                    delete this._map[normalizeHeaderName(name)];
                };
                Headers.prototype.forEach = function (callback, thisArg) {
                    for (var name in this._map) callback.call(thisArg, this._map[name], name, this);
                };

                function plainHeaders(headers) {
                    var result = {};
                    if (!headers) return result;
                    headers = headers instanceof Headers ? headers : new Headers(headers);
                    headers.forEach(function (value, name) { result[name] = value; });
                    return result;
                }

                globalThis.FormData = function FormData() {
                    this._entries = [];
                };
                FormData.prototype.append = function (name, value, filename) {
                    this._entries.push([String(name), value == null ? '' : String(value), filename == null ? null : String(filename)]);
                };
                FormData.prototype.set = function (name, value, filename) {
                    this.delete(name);
                    this.append(name, value, filename);
                };
                FormData.prototype.delete = function (name) {
                    name = String(name);
                    this._entries = this._entries.filter(function (entry) { return entry[0] !== name; });
                };
                FormData.prototype.get = function (name) {
                    name = String(name);
                    for (var i = 0; i < this._entries.length; i++) {
                        if (this._entries[i][0] === name) return this._entries[i][1];
                    }
                    return null;
                };

                function serializeBody(body, headers) {
                    if (body == null) return { text: '', contentType: '' };
                    if (body instanceof FormData) {
                        var boundary = '----FenFormData' + Math.random().toString(36).slice(2);
                        var text = '';
                        for (var i = 0; i < body._entries.length; i++) {
                            var entry = body._entries[i];
                            text += '--' + boundary + '\r\n';
                            text += 'Content-Disposition: form-data; name="' + String(entry[0]).replace(/"/g, '%22') + '"\r\n\r\n';
                            text += String(entry[1]) + '\r\n';
                        }
                        text += '--' + boundary + '--\r\n';
                        return { text: text, contentType: 'multipart/form-data; boundary=' + boundary };
                    }
                    if (body instanceof Blob) {
                        return { text: body._parts.join(''), contentType: body.type || '' };
                    }
                    return { text: String(body), contentType: headers.get('content-type') || '' };
                }

                globalThis.Request = function Request(input, init) {
                    init = init || {};
                    if (input instanceof Request) {
                        this.url = input.url;
                        this.method = input.method;
                        this.headers = new Headers(input.headers);
                        this.body = input.body;
                    } else {
                        this.url = String(input);
                        this.method = 'GET';
                        this.headers = new Headers();
                        this.body = null;
                    }
                    if (init.method) this.method = String(init.method).toUpperCase();
                    if (init.headers) this.headers = new Headers(init.headers);
                    if (init.body !== undefined) this.body = init.body;
                };

                globalThis.Response = function Response(body, init) {
                    init = init || {};
                    this._body = body == null ? '' : String(body);
                    this.status = init.status === undefined ? 200 : Number(init.status);
                    this.statusText = init.statusText || '';
                    this.headers = new Headers(init.headers || {});
                    this.url = init.url || '';
                    this.ok = this.status >= 200 && this.status < 300;
                };
                Response.prototype.text = function () {
                    return Promise.resolve(this._body);
                };
                Response.prototype.json = function () {
                    return Promise.resolve(JSON.parse(this._body || 'null'));
                };
                Response.prototype.clone = function () {
                    return new Response(this._body, {
                        status: this.status,
                        statusText: this.statusText,
                        headers: this.headers,
                        url: this.url
                    });
                };

                globalThis.fetch = function (input, init) {
                    init = init || {};
                    var request = input instanceof Request ? new Request(input, init) : new Request(input, init);
                    var headers = new Headers(request.headers);
                    var body = serializeBody(request.body, headers);
                    if (body.contentType && !headers.has('content-type')) {
                        headers.set('content-type', body.contentType);
                    }
                    try {
                        var result = __fenSyncFetch(request.method || 'GET', request.url || '', body.text, plainHeaders(headers));
                        return Promise.resolve(new Response(result && result.responseText ? result.responseText : '', {
                            status: result && typeof result.status === 'number' ? result.status : 0,
                            statusText: result && result.statusText ? String(result.statusText) : '',
                            headers: result && result.headers ? result.headers : {},
                            url: result && result.url ? String(result.url) : request.url
                        }));
                    } catch (error) {
                        return Promise.reject(error);
                    }
                };
            })();
            """);

        // window.getComputedStyle(element) → returns a CSSStyleDeclaration-like object
        // with the element's computed CSS properties.  React and other frameworks call
        // this during hydration to determine whether the server HTML matches the
        // client-side render.  Without it, hydration always fails and the app falls
        // back to a full client render (or crashes with TypeError).
        var getComputedStyleFn = _interpreter.AllocateNativeFunction(
            "getComputedStyle",
            (_, args) =>
            {
                if (args.Count == 0)
                {
                    return _interpreter.AllocateObject(new Dictionary<string, JsValue>());
                }

                var element = ResolveHostObjectOrNull<Element>(args[0]);
                if (element == null)
                {
                    return _interpreter.AllocateObject(new Dictionary<string, JsValue>());
                }

                var cs = element.GetComputedStyle();
                if (cs == null)
                {
                    return _interpreter.AllocateObject(new Dictionary<string, JsValue>());
                }

                var props = new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase);
                // Populate from the raw Map first (all CSS properties)
                if (cs.Map != null)
                {
                    foreach (var kv in cs.Map)
                    {
                        props[kv.Key] = JsValue.FromString(kv.Value ?? string.Empty);
                    }
                }

                // Override/add typed properties for correctness
                if (cs.Display != null) props["display"] = JsValue.FromString(cs.Display);
                if (cs.Position != null) props["position"] = JsValue.FromString(cs.Position);
                if (cs.FlexDirection != null) props["flexDirection"] = JsValue.FromString(cs.FlexDirection);
                if (cs.FlexWrap != null) props["flexWrap"] = JsValue.FromString(cs.FlexWrap);
                if (cs.JustifyContent != null) props["justifyContent"] = JsValue.FromString(cs.JustifyContent);
                if (cs.AlignItems != null) props["alignItems"] = JsValue.FromString(cs.AlignItems);
                if (cs.AlignContent != null) props["alignContent"] = JsValue.FromString(cs.AlignContent);
                if (cs.Width.HasValue) props["width"] = JsValue.FromString(cs.Width.Value + "px");
                if (cs.Height.HasValue) props["height"] = JsValue.FromString(cs.Height.Value + "px");
                if (cs.MinWidth.HasValue) props["minWidth"] = JsValue.FromString(cs.MinWidth.Value + "px");
                if (cs.MinHeight.HasValue) props["minHeight"] = JsValue.FromString(cs.MinHeight.Value + "px");
                if (cs.MaxWidth.HasValue) props["maxWidth"] = JsValue.FromString(cs.MaxWidth.Value + "px");
                if (cs.MaxHeight.HasValue) props["maxHeight"] = JsValue.FromString(cs.MaxHeight.Value + "px");
                if (cs.FontSize.HasValue) props["fontSize"] = JsValue.FromString(cs.FontSize.Value + "px");
                if (cs.ForegroundColor.HasValue) props["color"] = JsValue.FromString(cs.ForegroundColor.Value.ToString());
                if (cs.BackgroundColor.HasValue) props["backgroundColor"] = JsValue.FromString(cs.BackgroundColor.Value.ToString());
                if (cs.Opacity.HasValue) props["opacity"] = JsValue.FromString(cs.Opacity.Value.ToString(CultureInfo.InvariantCulture));
                if (cs.Visibility != null) props["visibility"] = JsValue.FromString(cs.Visibility);
                if (cs.Overflow != null) props["overflow"] = JsValue.FromString(cs.Overflow);
                if (cs.OverflowX != null) props["overflowX"] = JsValue.FromString(cs.OverflowX);
                if (cs.OverflowY != null) props["overflowY"] = JsValue.FromString(cs.OverflowY);
                if (cs.BoxSizing != null) props["boxSizing"] = JsValue.FromString(cs.BoxSizing);
                if (cs.ZIndex.HasValue) props["zIndex"] = JsValue.FromString(cs.ZIndex.Value.ToString(CultureInfo.InvariantCulture));
                if (cs.LineHeight.HasValue) props["lineHeight"] = JsValue.FromString(cs.LineHeight.Value + "px");
                if (cs.TextAlign.HasValue) props["textAlign"] = JsValue.FromString(cs.TextAlign.Value.ToString());
                if (cs.FontWeight.HasValue) props["fontWeight"] = JsValue.FromString(cs.FontWeight.Value.ToString(CultureInfo.InvariantCulture));
                if (cs.FontFamilyName != null) props["fontFamily"] = JsValue.FromString(cs.FontFamilyName);
                // Border from Thickness + Brush
                var bt = cs.BorderThickness;
                if (bt.Left != 0 || bt.Right != 0 || bt.Top != 0 || bt.Bottom != 0)
                {
                    props["borderTopWidth"] = JsValue.FromString(bt.Top + "px");
                    props["borderRightWidth"] = JsValue.FromString(bt.Right + "px");
                    props["borderBottomWidth"] = JsValue.FromString(bt.Bottom + "px");
                    props["borderLeftWidth"] = JsValue.FromString(bt.Left + "px");
                }
                if (cs.BorderBrush.HasValue)
                    props["borderTopColor"] = JsValue.FromString(cs.BorderBrush.Value.ToString());

                // Margin/padding shorthand (from Map if not explicit)
                if (cs.Margin != null)
                {
                    props["marginTop"] = JsValue.FromString(cs.Margin.Top + "px");
                    props["marginRight"] = JsValue.FromString(cs.Margin.Right + "px");
                    props["marginBottom"] = JsValue.FromString(cs.Margin.Bottom + "px");
                    props["marginLeft"] = JsValue.FromString(cs.Margin.Left + "px");
                }
                if (cs.Padding != null)
                {
                    props["paddingTop"] = JsValue.FromString(cs.Padding.Top + "px");
                    props["paddingRight"] = JsValue.FromString(cs.Padding.Right + "px");
                    props["paddingBottom"] = JsValue.FromString(cs.Padding.Bottom + "px");
                    props["paddingLeft"] = JsValue.FromString(cs.Padding.Left + "px");
                }

                // Custom properties (CSS variables)
                if (cs.CustomProperties != null)
                {
                    foreach (var kv in cs.CustomProperties)
                    {
                        var propName = kv.Key.StartsWith("--") ? kv.Key : "--" + kv.Key;
                        if (!props.ContainsKey(propName))
                            props[propName] = JsValue.FromString(kv.Value ?? string.Empty);
                    }
                }

                return _interpreter.AllocateObject(props);
            },
            length: 1);

        _interpreter.RegisterGlobalValue("getComputedStyle", getComputedStyleFn);
    }

    private JsValue ExecuteSynchronousXmlHttpRequest(IReadOnlyList<JsValue> args)
    {
        var methodText = args.Count > 0 ? CoerceToHostString(args[0]) : "GET";
        var urlText = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
        var bodyText = args.Count > 2 ? CoerceToHostString(args[2]) : string.Empty;

        if (FetchHandler == null || !TryResolveUri(urlText, _currentBaseUri, out var requestUri))
        {
            return CreateXhrResult(0, "Network Error", string.Empty);
        }

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(methodText) ? "GET" : methodText.ToUpperInvariant()), requestUri);
            if (_currentBaseUri != null)
            {
                request.Headers.Referrer = _currentBaseUri;
                if (!CorsHandler.IsSameOrigin(requestUri, _currentBaseUri))
                {
                    var origin = CorsHandler.SerializeOrigin(new UriBuilder(
                        _currentBaseUri.Scheme,
                        _currentBaseUri.Host,
                        _currentBaseUri.IsDefaultPort ? -1 : _currentBaseUri.Port).Uri);
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", origin);
                    }
                }
            }

            if (!string.Equals(request.Method.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Method.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new StringContent(bodyText ?? string.Empty, Encoding.UTF8, "text/plain");
            }

            if (args.Count > 3 && args[3].Tag == JsValueTag.Object)
            {
                ApplyXhrRequestHeaders(request, args[3]);
            }

            using var response = FetchHandler(request).GetAwaiter().GetResult();
            var responseText = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return CreateXhrResult((int)response.StatusCode, response.ReasonPhrase ?? string.Empty, responseText);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] XMLHttpRequest failed for '{requestUri}': {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
            return CreateXhrResult(0, "Network Error", string.Empty);
        }
    }

    private JsValue ExecuteSynchronousFetchRequest(IReadOnlyList<JsValue> args)
    {
        var methodText = args.Count > 0 ? CoerceToHostString(args[0]) : "GET";
        var urlText = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
        var bodyText = args.Count > 2 ? CoerceToHostString(args[2]) : string.Empty;

        if (FetchHandler == null || !TryResolveUri(urlText, _currentBaseUri, out var requestUri))
        {
            return CreateFetchResult(0, "Network Error", string.Empty, string.Empty, null);
        }

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(methodText) ? "GET" : methodText.ToUpperInvariant()), requestUri);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            if (_currentBaseUri != null)
            {
                request.Headers.Referrer = _currentBaseUri;
                if (!CorsHandler.IsSameOrigin(requestUri, _currentBaseUri))
                {
                    var origin = CorsHandler.SerializeOrigin(new UriBuilder(
                        _currentBaseUri.Scheme,
                        _currentBaseUri.Host,
                        _currentBaseUri.IsDefaultPort ? -1 : _currentBaseUri.Port).Uri);
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", origin);
                    }
                }
            }

            if (!string.Equals(request.Method.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Method.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new StringContent(bodyText ?? string.Empty, Encoding.UTF8, "text/plain");
            }

            if (args.Count > 3 && args[3].Tag == JsValueTag.Object)
            {
                ApplyXhrRequestHeaders(request, args[3]);
            }

            using var response = FetchHandler(request).GetAwaiter().GetResult();
            var responseText = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return CreateFetchResult(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                responseText,
                response.RequestMessage?.RequestUri?.ToString() ?? requestUri.ToString(),
                response);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] fetch failed for '{requestUri}': {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
            return CreateFetchResult(0, "Network Error", string.Empty, requestUri.ToString(), null);
        }
    }

    private void ApplyXhrRequestHeaders(HttpRequestMessage request, JsValue headersValue)
    {
        if (headersValue.Tag != JsValueTag.Object)
        {
            return;
        }

        var headersObject = _interpreter.Heap.GetObject(headersValue.AsObjectHandle());
        var context = (IBuiltinContext)_interpreter;
        foreach (var property in headersObject.EnumerateOwnProperties())
        {
            var name = property.Key;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!context.TryGetPropertyValue(headersObject, headersValue, name, out var value) ||
                value.Tag == JsValueTag.Undefined ||
                value.Tag == JsValueTag.Null)
            {
                continue;
            }

            var headerValue = CoerceToHostString(value);
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content != null)
            {
                if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(headerValue, out var mediaType))
                {
                    request.Content.Headers.ContentType = mediaType;
                }
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, headerValue) && request.Content != null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, headerValue);
            }
        }
    }

    private static bool TryResolveUri(string urlText, Uri baseUri, out Uri requestUri)
    {
        requestUri = null;
        if (string.IsNullOrWhiteSpace(urlText))
        {
            return false;
        }

        if (Uri.TryCreate(urlText, UriKind.Absolute, out requestUri))
        {
            return true;
        }

        return baseUri != null && Uri.TryCreate(baseUri, urlText, out requestUri);
    }

    private JsValue CreateXhrResult(int status, string statusText, string responseText)
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["status"] = JsValue.FromNumber(status),
            ["statusText"] = JsValue.FromString(statusText ?? string.Empty),
            ["responseText"] = JsValue.FromString(responseText ?? string.Empty)
        });
    }

    private JsValue CreateFetchResult(int status, string statusText, string responseText, string url, HttpResponseMessage response)
    {
        var headers = new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase);
        if (response != null)
        {
            foreach (var header in response.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = JsValue.FromString(string.Join(", ", header.Value));
            }

            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key.ToLowerInvariant()] = JsValue.FromString(string.Join(", ", header.Value));
                }
            }
        }

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["status"] = JsValue.FromNumber(status),
            ["statusText"] = JsValue.FromString(statusText ?? string.Empty),
            ["responseText"] = JsValue.FromString(responseText ?? string.Empty),
            ["url"] = JsValue.FromString(url ?? string.Empty),
            ["headers"] = _interpreter.AllocateObject(headers)
        });
    }

    private string DescribePromiseRejectionReason(JsValue reason)
    {
        if (reason.Tag != JsValueTag.Object)
        {
            return CoerceToHostString(reason);
        }

        try
        {
            var jsObject = _interpreter.Heap.GetObject(reason.AsObjectHandle());
            var context = (IBuiltinContext)_interpreter;
            if (context.TryGetPropertyValue(jsObject, reason, "stack", out var stack) &&
                stack.Tag != JsValueTag.Undefined &&
                stack.Tag != JsValueTag.Null)
            {
                return CoerceToHostString(stack);
            }

            if (context.TryGetPropertyValue(jsObject, reason, "message", out var message) &&
                message.Tag != JsValueTag.Undefined &&
                message.Tag != JsValueTag.Null)
            {
                return CoerceToHostString(message);
            }
        }
        catch
        {
        }

        return CoerceToHostString(reason);
    }

    /// <summary>
    /// Called by FenJsMutationObserverHost when its backing C# MutationObserver fires.
    /// Converts MutationRecord objects to JS and invokes the stored JS callback.
    /// Must run under the interpreter lock — the caller (OnMutations on the mutation
    /// callback thread) delegates here via RunFenJsWithLargeStack.
    /// </summary>
    internal void InvokeMutationObserverCallback(FenJsMutationObserverHost host, IReadOnlyList<MutationRecord> records)
    {
        if (records == null || records.Count == 0)
        {
            return;
        }

        if (!_interpreter.CanCallValue(host.Callback))
        {
            return;
        }

        // Convert records to JS objects. Each record is a plain object with the
        // MutationRecord properties defined by the spec.
        var jsRecords = new JsValue[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            jsRecords[i] = CreateMutationRecordJsObject(records[i]);
        }

        var recordsArray = _interpreter.AllocateArray(jsRecords);
        var observerValue = ToHostOrNull(host, HostObjectKind.Other);

        InvokeFenJsCallbackSafely(host.Callback, new[] { recordsArray, observerValue }, "MutationObserver");
    }

    /// <summary>
    /// Converts a C# MutationRecord into a JS plain object matching the
    /// MutationRecord interface.
    /// https://dom.spec.whatwg.org/#mutationrecord
    /// </summary>
    private JsValue CreateMutationRecordJsObject(MutationRecord record)
    {
        var props = new Dictionary<string, JsValue>(StringComparer.Ordinal)
        {
            ["type"] = JsValue.FromString(record.Type switch
            {
                MutationRecordType.Attributes => "attributes",
                MutationRecordType.CharacterData => "characterData",
                MutationRecordType.ChildList => "childList",
                _ => "childList"
            }),
            ["target"] = ToHostNodeOrNull(record.Target),
            ["addedNodes"] = CreateNodeArrayLike(record.AddedNodes ?? Array.Empty<Node>()),
            ["removedNodes"] = CreateNodeArrayLike(record.RemovedNodes ?? Array.Empty<Node>()),
            ["previousSibling"] = ToHostNodeOrNull(record.PreviousSibling),
            ["nextSibling"] = ToHostNodeOrNull(record.NextSibling),
            ["attributeName"] = record.AttributeName != null
                ? JsValue.FromString(record.AttributeName)
                : JsValue.Null,
            ["attributeNamespace"] = record.AttributeNamespace != null
                ? JsValue.FromString(record.AttributeNamespace)
                : JsValue.Null,
            ["oldValue"] = record.OldValue != null
                ? JsValue.FromString(record.OldValue)
                : JsValue.Null,
        };

        return _interpreter.AllocateObject(props);
    }

    /// <summary>
    /// Handles property access on MutationObserver host objects.
    /// </summary>
    private bool TryGetMutationObserverProperty(FenJsMutationObserverHost host, string property, out JsValue value)
    {
        switch (property)
        {
            case "observe":
                value = GetOrCreateHostCallable(
                    host,
                    "observe",
                    (_, args) =>
                    {
                        if (args.Count < 2)
                        {
                            ThrowDomException("TypeError",
                                "Failed to execute 'observe' on 'MutationObserver': 2 arguments required.");
                            return JsValue.Undefined;
                        }

                        var target = ResolveHostObjectOrNull<Node>(args[0]);
                        if (target == null)
                        {
                            ThrowDomException("TypeError",
                                "Failed to execute 'observe' on 'MutationObserver': parameter 1 is not of type 'Node'.");
                            return JsValue.Undefined;
                        }

                        var options = ParseMutationObserverInit(args[1]);
                        host.Observe(target, options);
                        return JsValue.Undefined;
                    },
                    length: 2);
                return true;

            case "disconnect":
                value = GetOrCreateHostCallable(
                    host,
                    "disconnect",
                    (_, _) =>
                    {
                        host.Disconnect();
                        return JsValue.Undefined;
                    },
                    length: 0);
                return true;

            case "takeRecords":
                value = GetOrCreateHostCallable(
                    host,
                    "takeRecords",
                    (_, _) =>
                    {
                        var records = host.TakeRecords();
                        var jsRecords = new JsValue[records.Count];
                        for (int i = 0; i < records.Count; i++)
                        {
                            jsRecords[i] = CreateMutationRecordJsObject(records[i]);
                        }

                        return _interpreter.AllocateArray(jsRecords);
                    },
                    length: 0);
                return true;

            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    /// <summary>
    /// Parses a JS options object into a MutationObserverInit struct.
    /// https://dom.spec.whatwg.org/#dictdef-mutationobserverinit
    /// </summary>
    private MutationObserverInit ParseMutationObserverInit(JsValue optionsValue)
    {
        var init = new MutationObserverInit();

        if (optionsValue.Tag != JsValueTag.Object)
        {
            // If options is not an object, default all to false → observe() will throw.
            return init;
        }

        init.ChildList = ReadJsBoolProperty(optionsValue, "childList");
        init.Attributes = ReadJsBoolProperty(optionsValue, "attributes");
        init.CharacterData = ReadJsBoolProperty(optionsValue, "characterData");
        init.Subtree = ReadJsBoolProperty(optionsValue, "subtree");
        init.AttributeOldValue = ReadJsBoolProperty(optionsValue, "attributeOldValue");
        init.CharacterDataOldValue = ReadJsBoolProperty(optionsValue, "characterDataOldValue");

        // attributeFilter: optional sequence<DOMString>
        var filterValue = ReadJsProperty(optionsValue, "attributeFilter");
        if (filterValue.Tag == JsValueTag.Object)
        {
            var filterList = new List<string>();
            try
            {
                var lenValue = ReadJsProperty(filterValue, "length");
                int len = 0;
                if (lenValue.Tag == JsValueTag.Int32)
                    len = lenValue.AsInt32();
                else if (lenValue.Tag == JsValueTag.Number)
                    len = (int)lenValue.AsNumber();

                for (int i = 0; i < Math.Min(len, 100); i++)
                {
                    var item = ReadJsProperty(filterValue, i.ToString());
                    if (item.Tag == JsValueTag.String)
                        filterList.Add(item.AsString());
                }
            }
            catch
            {
                // Best-effort; leave filter empty.
            }

            if (filterList.Count > 0)
                init.AttributeFilter = filterList.ToArray();
        }

        return init;
    }

    /// <summary>
    /// Reads a boolean property from a JS object, coercing via JS truthiness rules.
    /// </summary>
    private bool ReadJsBoolProperty(JsValue obj, string property)
    {
        var value = ReadJsProperty(obj, property);
        return value.Tag switch
        {
            JsValueTag.Undefined => false,
            JsValueTag.Null => false,
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => value.AsNumber() != 0,
            JsValueTag.String => value.AsString().Length > 0,
            _ => true // objects, symbols etc. are truthy
        };
    }

    /// <summary>
    /// Reads a named property from a JS object via the interpreter's property
    /// resolution (own + prototype chain). Returns JsValue.Undefined if the
    /// value is not an object or the property does not exist.
    /// </summary>
    private JsValue ReadJsProperty(JsValue obj, string property)
    {
        if (obj.Tag != JsValueTag.Object)
            return JsValue.Undefined;

        var handle = obj.AsObjectHandle();
        var jsObj = _interpreter.Heap.GetObject(handle);
        if (((IBuiltinContext)_interpreter).TryGetPropertyValue(jsObj, obj, property, out var value))
            return value;

        return JsValue.Undefined;
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
        int totalElements = 0;
        int scriptElements = 0;
        int otherElements = 0;
        foreach (var node in domRoot.SelfAndDescendants())
        {
            if (node is Element element)
            {
                totalElements++;
                if (string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
                {
                    scriptElements++;
                    Console.Error.WriteLine($"[FenJsBridge] Found SCRIPT element #{scriptElements}: src={element.GetAttribute("src")}, type={element.GetAttribute("type")}, parent={element.ParentNode?.GetType().Name}/{((element.ParentNode as Element)?.TagName ?? "null")}");
                    yield return element;
                }
                else
                {
                    otherElements++;
                }
            }
        }
        Console.Error.WriteLine($"[FenJsBridge] EnumerateScriptElements DONE: totalElements={totalElements}, scriptElements={scriptElements}, otherElements={otherElements}");
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

    private static void CollectElementsByName(Node root, string name, List<Element> results)
    {
        if (root == null || string.IsNullOrEmpty(name)) return;
        if (root is Element el && string.Equals(el.GetAttribute("name"), name, StringComparison.Ordinal))
            results.Add(el);
        for (var child = root.FirstChild; child != null; child = child.NextSibling)
            CollectElementsByName(child, name, results);
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

    private JsValue GetOrCreateStyleObject(Element element)
    {
        var store = _hostPropertyStore.GetOrCreateValue(element) ?? new Dictionary<string, JsValue>();
        _hostPropertyStore.AddOrUpdate(element, store);
        if (store.TryGetValue("__fenJsStyle", out var cached))
            return cached;

        var styleObj = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["cssText"] = JsValue.FromString(element.GetAttribute("style") ?? string.Empty),
        });
        _interpreter.SetObjectProperty(styleObj, "setProperty", GetOrCreateHostCallable(
            element, "style.setProperty",
            (_, args) =>
            {
                var prop = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                var val = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                var existing = element.GetAttribute("style") ?? string.Empty;
                element.SetAttribute("style", existing + (existing.Length > 0 && !existing.EndsWith(";") ? ";" : "") + prop + ":" + val + ";");
                return JsValue.Undefined;
            },
            length: 2), enumerable: true);
        _interpreter.SetObjectProperty(styleObj, "getPropertyValue", GetOrCreateHostCallable(
            element, "style.getPropertyValue",
            (_, _) => JsValue.FromString(string.Empty),
            length: 1), enumerable: true);
        _interpreter.SetObjectProperty(styleObj, "removeProperty", GetOrCreateHostCallable(
            element, "style.removeProperty",
            (_, _) => JsValue.FromString(string.Empty),
            length: 1), enumerable: true);
        store["__fenJsStyle"] = styleObj;
        return styleObj;
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

        // Inline script: execute textContent directly.
        if (string.IsNullOrWhiteSpace(src))
        {
            var inlineCode = scriptElement.TextContent;
            if (!string.IsNullOrWhiteSpace(inlineCode))
            {
                SetCurrentScriptElement(scriptElement);
                try
                {
                    EvaluateWithFenJsRaw(inlineCode);
                }
                finally
                {
                    SetCurrentScriptElement(null);
                }

                DispatchScriptElementEvent(scriptElement, "load");
            }

            return ToHostNodeOrNull(scriptElement);
        }

        // Data: URLs contain inline code — decode and execute directly.
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var dataCode = DecodeDataUrl(src);
            if (dataCode != null)
            {
                SetCurrentScriptElement(scriptElement);
                try
                {
                    EvaluateWithFenJsRaw(dataCode);
                }
                finally
                {
                    SetCurrentScriptElement(null);
                }

                DispatchScriptElementEvent(scriptElement, "load");
            }
            else
            {
                DispatchScriptElementEvent(scriptElement, "error");
            }

            return ToHostNodeOrNull(scriptElement);
        }

        // External script: fetch and execute.
        if (!AllowExternalScripts || !Sandbox.Allows(SandboxFeature.ExternalScripts))
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
                // code fetched from ExternalScriptFetcher
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

    /// <summary>
    /// Reads a layout dimension (offsetHeight, clientWidth, etc.) for an element
    /// by resolving its layout box from the renderer.
    /// </summary>
    private JsValue ReadElementLayoutDimension(Element element, string property)
    {
        var box = LayoutBoxResolver?.Invoke(element) as BoxModel;
        if (box == null)
            return JsValue.FromInt32(0);

        return property switch
        {
            "offsetWidth" => JsValue.FromNumber(box.BorderBox.Width),
            "offsetHeight" => JsValue.FromNumber(box.BorderBox.Height),
            "clientWidth" => JsValue.FromNumber(box.PaddingBox.Width),
            "clientHeight" => JsValue.FromNumber(box.PaddingBox.Height),
            "offsetLeft" => JsValue.FromNumber(box.BorderBox.Left),
            "offsetTop" => JsValue.FromNumber(box.BorderBox.Top),
            "clientLeft" => JsValue.FromNumber(box.BorderBox.Left - box.PaddingBox.Left),
            "clientTop" => JsValue.FromNumber(box.BorderBox.Top - box.PaddingBox.Top),
            "scrollWidth" => JsValue.FromNumber(box.ContentBox.Width),
            "scrollHeight" => JsValue.FromNumber(box.ContentBox.Height),
            "scrollTop" => JsValue.FromInt32(0),
            "scrollLeft" => JsValue.FromInt32(0),
            _ => JsValue.FromInt32(0)
        };
    }

    /// <summary>
    /// Returns a getBoundingClientRect result for an element from its layout box.
    /// </summary>
    private JsValue ReadElementBoundingClientRect(Element element)
    {
        var box = LayoutBoxResolver?.Invoke(element) as BoxModel;
        if (box == null)
        {
            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["x"] = JsValue.FromInt32(0), ["y"] = JsValue.FromInt32(0),
                ["width"] = JsValue.FromInt32(0), ["height"] = JsValue.FromInt32(0),
                ["top"] = JsValue.FromInt32(0), ["right"] = JsValue.FromInt32(0),
                ["bottom"] = JsValue.FromInt32(0), ["left"] = JsValue.FromInt32(0),
            });
        }

        var r = box.BorderBox;
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["x"] = JsValue.FromNumber(r.Left),
            ["y"] = JsValue.FromNumber(r.Top),
            ["width"] = JsValue.FromNumber(r.Width),
            ["height"] = JsValue.FromNumber(r.Height),
            ["top"] = JsValue.FromNumber(r.Top),
            ["right"] = JsValue.FromNumber(r.Right),
            ["bottom"] = JsValue.FromNumber(r.Bottom),
            ["left"] = JsValue.FromNumber(r.Left),
        });
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

    /// <summary>
    /// Decodes a data: URL into its content string.  Supports text/plain and
    /// text/javascript with optional base64 encoding.  Returns null if the URL
    /// is not a valid data: URL or decoding fails.
    /// </summary>
    private static string DecodeDataUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // data:[<mediatype>][;base64],<data>
        var commaIdx = url.IndexOf(',');
        if (commaIdx < 0)
        {
            return null;
        }

        var header = url.Substring(5, commaIdx - 5); // skip "data:"
        var data = url.Substring(commaIdx + 1);
        var isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);

        if (isBase64)
        {
            try
            {
                var bytes = Convert.FromBase64String(data);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        // URL-encoded data
        try
        {
            return Uri.UnescapeDataString(data);
        }
        catch
        {
            return data; // best-effort
        }
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
            _owner?._interpreter.EnqueueHostedPromiseJob(job);
        }

        public void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation)
        {
            if (operation != PromiseRejectionOperation.Reject || _owner == null)
            {
                return;
            }

            var reason = "<unknown>";
            try
            {
                if (promise.Tag == JsValueTag.Object &&
                    _owner._interpreter.Heap.GetObject(promise.AsObjectHandle()) is PromiseInstance promiseInstance)
                {
                    reason = _owner.DescribePromiseRejectionReason(promiseInstance.Promise.GetResultUnchecked());
                }
            }
            catch
            {
                reason = "<unavailable>";
            }

            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Unhandled promise rejection: {reason}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }

        public bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value)
        {
            if (_owner == null || _owner._interpreter == null)
            {
                value = JsValue.Undefined;
                return false;
            }
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
                case FenJsMutationObserverHost mutationObserver:
                    return _owner.TryGetMutationObserverProperty(mutationObserver, property, out value);
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        public bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value)
        {
            if (_owner == null || _owner._interpreter == null)
            {
                return false;
            }
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
                case Element element when property.StartsWith("on", StringComparison.OrdinalIgnoreCase):
                    _owner.SetStoredHostProperty(element, property.ToLowerInvariant(), value);
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
                case FenJsLocationHost location when string.Equals(property, "href", StringComparison.Ordinal):
                    var hrefStr = CoerceToHostString(value);
                    if (!string.IsNullOrWhiteSpace(hrefStr) && Uri.TryCreate(location.Uri, hrefStr, out var navUri))
                        _owner._host.Navigate(navUri);
                    return true;
                default:
                    return false;
            }
        }

        public JsValue CallHostFunction(int functionId, JsValue thisValue, ReadOnlySpan<JsValue> args)
        {
            // Host function invocation not yet wired — the interpreter does not
            // currently emit CallHostFunction opcodes. When it does, map functionId
            // to a registered host callable and invoke it here.
            _ = functionId;
            _ = thisValue;
            _ = args;
            return JsValue.Undefined;
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
                case "getElementsByName":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "getElementsByName",
                        (_, args) =>
                        {
                            var name = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(name))
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            var results = new List<Element>();
                            CollectElementsByName(document.DocumentElement, name, results);
                            return _owner.CreateNodeArrayLike(results.Cast<Node>().ToArray());
                        },
                        length: 1);
                    return true;
                case "getElementsByClassName":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "getElementsByClassName",
                        (_, args) =>
                        {
                            var className = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(className))
                                return _owner.CreateNodeArrayLike(Array.Empty<Node>());
                            return _owner.ToHostOrNull(new FenJsHtmlCollectionHost(document.GetElementsByClassName(className)), HostObjectKind.Other);
                        },
                        length: 1);
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
                case "getElementsByTagName":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "getElementsByTagName",
                        (_, args) =>
                        {
                            var qualifiedName = args.Count > 0 ? CoerceToHostString(args[0]) : "*";
                            return _owner.ToHostOrNull(
                                new FenJsHtmlCollectionHost(element.GetElementsByTagName(qualifiedName)),
                                HostObjectKind.Other);
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
                // --- Geometry / layout ---
                case "nodeType":
                    value = JsValue.FromInt32((int)element.NodeType);
                    return true;
                case "isConnected":
                    value = JsValue.FromBoolean(element.IsConnected);
                    return true;
                case "childNodes":
                    value = _owner.CreateNodeArrayLike(element.ChildNodes.ToArray());
                    return true;
                case "children":
                    {
                        var kids = new List<Node>();
                        for (int i = 0; i < element.ChildNodes.Length; i++)
                            if (element.ChildNodes[i] is Element) kids.Add(element.ChildNodes[i]);
                        value = _owner.CreateNodeArrayLike(kids);
                    }
                    return true;
                case "firstChild":
                    value = _owner.ToHostNodeOrNull(element.FirstChild);
                    return true;
                case "lastChild":
                    value = _owner.ToHostNodeOrNull(element.LastChild);
                    return true;
                case "nextElementSibling":
                    value = _owner.ToHostNodeOrNull(element.NextElementSibling);
                    return true;
                case "previousElementSibling":
                    value = _owner.ToHostNodeOrNull(element.PreviousElementSibling);
                    return true;
                case "contains":
                    value = _owner.GetOrCreateHostCallable(
                        element, "contains",
                        (_, args) =>
                        {
                            var other = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            return JsValue.FromBoolean(other != null && element.Contains(other));
                        },
                        length: 1);
                    return true;
                case "offsetWidth":
                case "offsetHeight":
                case "offsetLeft":
                case "offsetTop":
                case "clientWidth":
                case "clientHeight":
                case "clientLeft":
                case "clientTop":
                case "scrollWidth":
                case "scrollHeight":
                case "scrollTop":
                case "scrollLeft":
                    value = _owner.ReadElementLayoutDimension(element, property);
                    return true;
                case "getBoundingClientRect":
                    value = _owner.GetOrCreateHostCallable(
                        element, "getBoundingClientRect",
                        (_, _) => _owner.ReadElementBoundingClientRect(element),
                        length: 0);
                    return true;
                case "style":
                    value = _owner.GetOrCreateStyleObject(element);
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
                case "hardwareConcurrency":
                    value = JsValue.FromInt32(Math.Max(1, Environment.ProcessorCount));
                    return true;
                case "maxTouchPoints":
                    value = JsValue.FromInt32(0);
                    return true;
                case "deviceMemory":
                    value = JsValue.FromNumber(4); // common default
                    return true;
                case "language":
                    value = JsValue.FromString(navigator.Language ?? System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName);
                    return true;
                case "languages":
                    value = JsValue.FromString(navigator.Language ?? "en-US"); // simplified: return string, not array
                    return true;
                case "onLine":
                    value = JsValue.FromBoolean(true);
                    return true;
                case "appName":
                    value = JsValue.FromString("Netscape");
                    return true;
                case "appVersion":
                    value = JsValue.FromString("5.0");
                    return true;
                case "product":
                    value = JsValue.FromString("Gecko");
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetLocationProperty(FenJsLocationHost location, string property, out JsValue value)
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
                case "reload":
                    value = _owner.GetOrCreateHostCallable(
                        location, "reload",
                        (_, _2) => { _owner._host.Navigate(uri); return JsValue.Undefined; },
                        length: 1);
                    return true;
                case "replace":
                    value = _owner.GetOrCreateHostCallable(
                        location, "replace",
                        (_, args) =>
                        {
                            var url = args.Count > 0 ? CoerceToHostString(args[0]) : null;
                            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(uri, url, out var navUri))
                                _owner._host.Navigate(navUri);
                            return JsValue.Undefined;
                        }, length: 1);
                    return true;
                case "assign":
                    value = _owner.GetOrCreateHostCallable(
                        location, "assign",
                        (_, args) =>
                        {
                            var url = args.Count > 0 ? CoerceToHostString(args[0]) : null;
                            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(uri, url, out var navUri))
                                _owner._host.Navigate(navUri);
                            return JsValue.Undefined;
                        }, length: 1);
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
/// <summary>
/// Static factory for browser script engine instances.
/// FenJS is the only runtime — no legacy fallback.
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

    private static IBrowserScriptEngine CreateConfiguredDefault(IJsHost host)
    {
        return new FenJsBrowserScriptEngine(host);
    }
}
