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
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Storage;
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
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Storage;
using DomRange = FenBrowser.Core.Dom.V2.Range;

namespace FenBrowser.FenEngine.Scripting;

public sealed class BrowserScriptLoadingSnapshot
{
    public string Status { get; set; } = "not-run";
    public string StartedUtc { get; set; } = string.Empty;
    public string CompletedUtc { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool DomRootPresent { get; set; }
    public int TotalElements { get; set; }
    public int ScriptElements { get; set; }
    public int OtherElements { get; set; }
    public int TotalScripts { get; set; }
    public int EligibleScripts { get; set; }
    public int SkippedScripts { get; set; }
    public int InlineScripts { get; set; }
    public int ExternalScripts { get; set; }
    public int ModuleScripts { get; set; }
    public int BlockingScripts { get; set; }
    public int DeferScripts { get; set; }
    public int AsyncScripts { get; set; }
    public int AsyncPendingScripts { get; set; }
    public int FetchStarted { get; set; }
    public int FetchCompleted { get; set; }
    public int FetchFailed { get; set; }
    public int ExecutionStarted { get; set; }
    public int ExecutionCompleted { get; set; }
    public int ExecutionFailed { get; set; }
    public string InfrastructureError { get; set; } = string.Empty;
    public List<BrowserScriptLoadingRecord> Scripts { get; set; } = new();

    public BrowserScriptLoadingSnapshot Clone()
    {
        return new BrowserScriptLoadingSnapshot
        {
            Status = Status,
            StartedUtc = StartedUtc,
            CompletedUtc = CompletedUtc,
            BaseUrl = BaseUrl,
            DomRootPresent = DomRootPresent,
            TotalElements = TotalElements,
            ScriptElements = ScriptElements,
            OtherElements = OtherElements,
            TotalScripts = TotalScripts,
            EligibleScripts = EligibleScripts,
            SkippedScripts = SkippedScripts,
            InlineScripts = InlineScripts,
            ExternalScripts = ExternalScripts,
            ModuleScripts = ModuleScripts,
            BlockingScripts = BlockingScripts,
            DeferScripts = DeferScripts,
            AsyncScripts = AsyncScripts,
            AsyncPendingScripts = AsyncPendingScripts,
            FetchStarted = FetchStarted,
            FetchCompleted = FetchCompleted,
            FetchFailed = FetchFailed,
            ExecutionStarted = ExecutionStarted,
            ExecutionCompleted = ExecutionCompleted,
            ExecutionFailed = ExecutionFailed,
            InfrastructureError = InfrastructureError,
            Scripts = Scripts?.Select(script => script.Clone()).ToList() ?? new List<BrowserScriptLoadingRecord>()
        };
    }
}

public sealed class BrowserScriptLoadingRecord
{
    public int Ordinal { get; set; }
    public string ScriptId { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public int SourceOffset { get; set; } = -1;
    public int SourceLine { get; set; }
    public int SourceColumn { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Src { get; set; } = string.Empty;
    public string ResolvedUrl { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsAsync { get; set; }
    public bool IsDefer { get; set; }
    public bool IsNoModule { get; set; }
    public bool ParserInserted { get; set; } = true;
    public string Batch { get; set; } = string.Empty;
    public string Status { get; set; } = "discovered";
    public string Failure { get; set; } = string.Empty;
    public int TextLength { get; set; }
    public int CodeLength { get; set; }
    public string StartedUtc { get; set; } = string.Empty;
    public string CompletedUtc { get; set; } = string.Empty;

    public BrowserScriptLoadingRecord Clone()
    {
        return new BrowserScriptLoadingRecord
        {
            Ordinal = Ordinal,
            ScriptId = ScriptId,
            SourceLabel = SourceLabel,
            SourceOffset = SourceOffset,
            SourceLine = SourceLine,
            SourceColumn = SourceColumn,
            Kind = Kind,
            SourceType = SourceType,
            Src = Src,
            ResolvedUrl = ResolvedUrl,
            Type = Type,
            IsAsync = IsAsync,
            IsDefer = IsDefer,
            IsNoModule = IsNoModule,
            ParserInserted = ParserInserted,
            Batch = Batch,
            Status = Status,
            Failure = Failure,
            TextLength = TextLength,
            CodeLength = CodeLength,
            StartedUtc = StartedUtc,
            CompletedUtc = CompletedUtc
        };
    }
}

public sealed class BrowserEventLoopSnapshot
{
    public string Status { get; set; } = "not-run";
    public string StartedUtc { get; set; } = string.Empty;
    public string CompletedUtc { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool DomRootPresent { get; set; }
    public string DocumentReadyState { get; set; } = "loading";
    public bool DomContentLoadedFired { get; set; }
    public string DomContentLoadedUtc { get; set; } = string.Empty;
    public bool LoadFired { get; set; }
    public string LoadUtc { get; set; } = string.Empty;
    public bool BodyOnloadAttributeExecuted { get; set; }
    public int MicrotaskCheckpoints { get; set; }
    public string LastMicrotaskCheckpointUtc { get; set; } = string.Empty;
    public int TimersScheduled { get; set; }
    public int TimersExecuted { get; set; }
    public int IntervalsScheduled { get; set; }
    public int AnimationFramesScheduled { get; set; }
    public int AnimationFramesExecuted { get; set; }
    public int PendingHostTimers { get; set; }
    public int CallbackFailures { get; set; }
    public string LastCallbackOrigin { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public List<BrowserEventLoopRecord> Events { get; set; } = new();

    public BrowserEventLoopSnapshot Clone()
    {
        return new BrowserEventLoopSnapshot
        {
            Status = Status,
            StartedUtc = StartedUtc,
            CompletedUtc = CompletedUtc,
            BaseUrl = BaseUrl,
            DomRootPresent = DomRootPresent,
            DocumentReadyState = DocumentReadyState,
            DomContentLoadedFired = DomContentLoadedFired,
            DomContentLoadedUtc = DomContentLoadedUtc,
            LoadFired = LoadFired,
            LoadUtc = LoadUtc,
            BodyOnloadAttributeExecuted = BodyOnloadAttributeExecuted,
            MicrotaskCheckpoints = MicrotaskCheckpoints,
            LastMicrotaskCheckpointUtc = LastMicrotaskCheckpointUtc,
            TimersScheduled = TimersScheduled,
            TimersExecuted = TimersExecuted,
            IntervalsScheduled = IntervalsScheduled,
            AnimationFramesScheduled = AnimationFramesScheduled,
            AnimationFramesExecuted = AnimationFramesExecuted,
            PendingHostTimers = PendingHostTimers,
            CallbackFailures = CallbackFailures,
            LastCallbackOrigin = LastCallbackOrigin,
            LastError = LastError,
            Events = Events?.Select(entry => entry.Clone()).ToList() ?? new List<BrowserEventLoopRecord>()
        };
    }
}

public sealed class BrowserEventLoopRecord
{
    public string EventName { get; set; } = string.Empty;
    public string TimestampUtc { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public long Id { get; set; }
    public int DelayMs { get; set; }
    public bool Repeating { get; set; }
    public string DocumentReadyState { get; set; } = string.Empty;

    public BrowserEventLoopRecord Clone()
    {
        return new BrowserEventLoopRecord
        {
            EventName = EventName,
            TimestampUtc = TimestampUtc,
            Detail = Detail,
            Id = Id,
            DelayMs = DelayMs,
            Repeating = Repeating,
            DocumentReadyState = DocumentReadyState
        };
    }
}

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
    Func<Element, Uri, Task> FrameElementLoader { get; set; }
    Func<Element, object> LayoutBoxResolver { get; set; }
    SandboxPolicy Sandbox { get; set; }
    bool AllowExternalScripts { get; set; }
    bool ExecuteInlineScriptsOnInnerHTML { get; set; }
    double WindowWidth { get; set; }
    double WindowHeight { get; set; }
    int PageScriptByteBudget { get; set; }
    event Func<string, JsPermissions, Task<bool>> PermissionRequested;

    void CaptureNavigationGlobals(Uri documentUri, long navigationId);
    BrowserScriptLoadingSnapshot GetScriptLoadingSnapshot();
    BrowserEventLoopSnapshot GetEventLoopSnapshot();
    void SetHistoryBridge(IHistoryBridge bridge);
    void NotifyPopState(object state);
    bool DispatchEventForElement(Element element, string eventName, BrowserDomEventInit eventInit = null);
    object Evaluate(string script);
    bool TryResolveHostObject(FenBrowser.Js.Runtime.JsValue value, out object hostObject);
    object ConvertJsValueToObject(FenBrowser.Js.Runtime.JsValue value);
    void SyncDomContext(Node domRoot, Uri baseUri = null);
    Task SetDomAsync(Node domRoot, Uri baseUri = null);
}

public sealed class BrowserDomEventInit
{
    public double ClientX { get; init; }
    public double ClientY { get; init; }
    public double PageX { get; init; }
    public double PageY { get; init; }
    public double ScreenX { get; init; }
    public double ScreenY { get; init; }
    public int Button { get; init; }
    public int Buttons { get; init; }
    public int PointerId { get; init; } = 1;
    public string PointerType { get; init; } = "mouse";
    public double Pressure { get; init; }
    public bool IsPrimary { get; init; } = true;
    public bool Bubbles { get; init; } = true;
    public bool Cancelable { get; init; } = true;
    public bool Composed { get; init; } = true;
    public bool IsTrusted { get; init; } = true;
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
    private long _diagnosticCookieCaptureCounter;
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
    private StorageService _storageService = new();
    private readonly Dictionary<int, FenWebSocketHost> _webSocketHosts = new();
    private int _webSocketIdCounter;
    private ConditionalWeakTable<Element, List<BrowserEventListener>> _iframeWindowEventListeners = new();
    private readonly Dictionary<object, string> _hostPrototypeNames =
        new(ReferenceEqualityComparer.Instance);
    private List<BrowserEventListener> _activeWindowEventListeners;
    private JsValue _activeWindowEventTarget = JsValue.Undefined;
    private Uri _activeParentBaseUri;
    private bool _fenJsDomConstructorsInstalled;
    private long _temporaryFenJsGlobalCounter;
    private BytecodeCompiler _compiler;
    private BytecodeInterpreter _interpreter;
    private DocumentEpoch _documentEpoch = DocumentEpoch.Initial;
    private Node _currentDomRoot;
    private Uri _currentBaseUri;
    private Element _currentScriptElement;
    private BrowserScriptLoadingRecord _currentScriptRecord;
    private string _currentNavigationId;
    private string _documentReadyState = "loading";
    private int _fenJsEvaluationCount;
    private int _dynamicScriptTraceCounter;
    private readonly object _scriptLoadingLock = new();
    private BrowserScriptLoadingSnapshot _lastScriptLoadingSnapshot = new();
    private readonly object _eventLoopLock = new();
    private BrowserEventLoopSnapshot _lastEventLoopSnapshot = new();
    // Incremented every time ResetFenJsSession destroys the interpreter.
    // Work lambdas capture the generation at dispatch time; if it changes
    // before the worker executes, the session was reset (e.g. by a
    // navigation triggered from a Promise) and the work should abort.
    private volatile int _fenJsSessionGeneration;

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

    public BrowserScriptLoadingSnapshot GetScriptLoadingSnapshot()
    {
        lock (_scriptLoadingLock)
        {
            return _lastScriptLoadingSnapshot?.Clone() ?? new BrowserScriptLoadingSnapshot();
        }
    }

    public BrowserEventLoopSnapshot GetEventLoopSnapshot()
    {
        var readyState = GetDocumentReadyState();
        var pendingHostTimers = _fenJsTimers.Count;
        lock (_eventLoopLock)
        {
            var snapshot = _lastEventLoopSnapshot?.Clone() ?? new BrowserEventLoopSnapshot();
            snapshot.PendingHostTimers = pendingHostTimers;
            snapshot.DocumentReadyState = readyState;
            return snapshot;
        }
    }

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
    public Func<Element, Uri, Task> FrameElementLoader { get; set; }
    public Func<Element, object> LayoutBoxResolver { get; set; }
    public SandboxPolicy Sandbox { get; set; }
    public bool AllowExternalScripts { get; set; }
    public bool ExecuteInlineScriptsOnInnerHTML { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int PageScriptByteBudget { get; set; }

    /// <summary>
    /// Storage backend for IndexedDB persistence. When set, data persists across
    /// page loads. Defaults to an in-memory store if not configured.
    /// </summary>
    public IStorageBackend IndexedDbBackend { get; set; }

    public event Func<string, JsPermissions, Task<bool>> PermissionRequested;

    public void SetHistoryBridge(IHistoryBridge bridge)
    {
        _historyBridge = bridge;
    }

    public void CaptureNavigationGlobals(Uri documentUri, long navigationId)
    {
        if (!ShouldCaptureNavigationGlobals() || _interpreter == null)
        {
            return;
        }

        try
        {
            var snapshot = new Dictionary<string, object>
            {
                ["globals"] = CaptureProbeGlobals(),
                ["cookies"] = CaptureCookieNames(documentUri),
            };
            var envelope = new Dictionary<string, object>
            {
                ["schema"] = "fenbrowser.navigation-globals.v1",
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["navigationId"] = navigationId,
                ["documentUri"] = documentUri?.AbsoluteUri ?? string.Empty,
                ["snapshot"] = snapshot,
            };

            var fileName = "nav_globals_" +
                navigationId.ToString(CultureInfo.InvariantCulture) + "_" +
                DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) +
                ".json";
            var path = Path.Combine(DiagnosticPaths.GetLogsDirectory(), fileName);
            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            EngineLogCompat.Debug(
                $"[NavigationGlobalsProbe] capture failed: {ex.GetType().Name}: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
    }

    private static bool ShouldCaptureNavigationGlobals()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("FEN_NAV_GLOBALS_SNAPSHOT"), "1", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return BrowserSettings.Instance?.Logging?.LogNavigationGlobals == true;
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, object> CaptureProbeGlobals()
    {
        var globals = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (alias, expression) in NavigationGlobalProbeNames)
        {
            globals[alias] = DescribeProbeValue(ReadProbeExpression(expression));
        }

        return globals;
    }

    private static readonly (string Alias, string Expression)[] NavigationGlobalProbeNames =
    {
        ("sgs", "globalThis.sgs"),
        ("ussv", "globalThis.ussv"),
        ("sp", "globalThis.sp"),
        ("prs", "globalThis.prs"),
        ("st", "globalThis.st"),
        ("td", "globalThis.td"),
        ("google", "globalThis.google"),
        ("challenge_version", "globalThis.challenge_version"),
        ("cbs", "globalThis.cbs"),
        ("ce", "globalThis.ce"),
        ("r", "globalThis.r"),
        ("ss_cgi", "globalThis.ss_cgi"),
        ("sclm", "globalThis.sclm"),
        ("sctm", "globalThis.sctm"),
        ("eid", "globalThis.eid"),
        ("fetch", "globalThis.fetch"),
        ("XMLHttpRequest", "globalThis.XMLHttpRequest"),
        ("crypto", "globalThis.crypto"),
        ("navigator", "globalThis.navigator"),
        ("performance", "globalThis.performance"),
        ("webdriver", "globalThis.navigator && globalThis.navigator.webdriver"),
        ("chrome", "globalThis.chrome"),
        ("React", "globalThis.React"),
        ("jQuery", "globalThis.jQuery"),
        ("$", "globalThis.$"),
        ("reactDevtoolsHook_alias", "globalThis.__REACT_DEVTOOLS_GLOBAL_HOOK__"),
        ("nextData_alias", "globalThis.__NEXT_DATA__"),
    };

    private JsValue ReadProbeExpression(string expression)
    {
        const string prefix = "globalThis.";
        try
        {
            if (expression.StartsWith(prefix, StringComparison.Ordinal) &&
                expression.IndexOfAny(new[] { '&', ' ', '(', ')' }) < 0)
            {
                var globalName = expression.Substring(prefix.Length);
                return _interpreter.TryReadGlobalValue(globalName, out var globalValue)
                    ? globalValue
                    : JsValue.Undefined;
            }

            if (string.Equals(expression, "globalThis.navigator && globalThis.navigator.webdriver", StringComparison.Ordinal) &&
                _interpreter.TryReadGlobalValue("navigator", out var navigator))
            {
                return ReadJsProperty(navigator, "webdriver");
            }
        }
        catch
        {
        }

        return JsValue.Undefined;
    }

    private Dictionary<string, object> DescribeProbeValue(JsValue value)
    {
        var description = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["exists"] = value.Tag != JsValueTag.Undefined,
            ["type"] = GetProbeType(value),
        };

        try
        {
            switch (value.Tag)
            {
                case JsValueTag.Boolean:
                    description["value"] = value.AsBoolean();
                    break;
                case JsValueTag.Int32:
                    description["value"] = value.AsInt32();
                    break;
                case JsValueTag.Number:
                    description["value"] = value.AsNumber();
                    break;
                case JsValueTag.String:
                    description["charCount"] = value.AsString().Length;
                    break;
                case JsValueTag.Object:
                    if (_interpreter.CanCallValue(value))
                    {
                        var length = ReadJsProperty(value, "length");
                        if (TryReadProbeNumber(length, out var arity))
                        {
                            description["arity"] = (int)Math.Max(0, arity);
                        }
                    }

                    var obj = _interpreter.Heap.GetObject(value.AsObjectHandle());
                    if (obj != null)
                    {
                        description["ownKeys"] = obj.EnumerateOwnProperties()
                            .Select(p => p.Key)
                            .Take(80)
                            .ToArray();
                    }
                    break;
            }
        }
        catch
        {
            description["probeError"] = true;
        }

        return description;
    }

    private string GetProbeType(JsValue value)
    {
        if (value.Tag == JsValueTag.Object && _interpreter != null && _interpreter.CanCallValue(value))
        {
            return "function";
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => "undefined",
            JsValueTag.Null => "null",
            JsValueTag.Boolean => "boolean",
            JsValueTag.Int32 => "number",
            JsValueTag.Number => "number",
            JsValueTag.String => "string",
            JsValueTag.Symbol => "symbol",
            JsValueTag.BigInt => "bigint",
            JsValueTag.Object => "object",
            JsValueTag.HostObject => "hostobject",
            _ => "unknown",
        };
    }

    private static bool TryReadProbeNumber(JsValue value, out double number)
    {
        switch (value.Tag)
        {
            case JsValueTag.Int32:
                number = value.AsInt32();
                return true;
            case JsValueTag.Number:
                number = value.AsNumber();
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private Dictionary<string, object> CaptureCookieNames(Uri documentUri)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["names"] = Array.Empty<string>(),
        };

        if (documentUri == null || CookieReadBridge == null)
        {
            return result;
        }

        try
        {
            var cookie = CookieReadBridge(documentUri) ?? string.Empty;
            var names = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Select(part =>
                {
                    var eq = part.IndexOf('=');
                    return eq > 0 ? part.Substring(0, eq).Trim() : part;
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            result["names"] = names;
            result["count"] = names.Length;
        }
        catch
        {
            result["probeError"] = true;
        }

        return result;
    }

    public void NotifyPopState(object state)
    {
        // popstate events are dispatched through the FenJS event system
        // when the history bridge fires.
    }

    public bool DispatchEventForElement(Element element, string eventName, BrowserDomEventInit eventInit = null)
    {
        if (element == null || string.IsNullOrWhiteSpace(eventName))
            return true;

        try
        {
            var inputTimeoutMs = ResolveFenJsInputEventTimeoutMs();
            return RunFenJsWithLargeStack(() =>
            {
                lock (_fenJsLock)
                {
                    using (ActivateSubdocumentWindowContext(element.OwnerDocument, null))
                    {
                        return _interpreter.RunWithExecutionBudget(
                            inputTimeoutMs,
                            10_000_000,
                            () =>
                            {
                                var eventValue = CreateBrowserDomEventValue(element, eventName, eventInit, out var dispatchState);
                                var defaultAllowed = DispatchEventFull(element, eventName, eventValue, dispatchState);
                                _interpreter.PumpMicrotasks();
                                RecordMicrotaskCheckpoint("event:" + eventName);
                                return defaultAllowed;
                            });
                    }
                }
            }, inputTimeoutMs);
        }
        catch (JsThrownException ex) when (IsFenJsInputEventTimeout(ex))
        {
            throw new FenBrowser.FenEngine.Errors.FenTimeoutError(
                $"Timed out dispatching '{eventName}' event.");
        }
    }

    private static bool IsFenJsInputEventTimeout(JsThrownException ex)
    {
        var message = ex?.Message ?? string.Empty;
        return message.Contains("Script wall-clock timeout exceeded", StringComparison.Ordinal) ||
               message.Contains("Maximum instruction budget exceeded", StringComparison.Ordinal) ||
               message.Contains("Execution interrupted", StringComparison.Ordinal);
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

    public Task SetSubdocumentDomAsync(Node domRoot, Uri baseUri = null)
    {
        return SetDomAsyncCore(domRoot, baseUri, resetSession: false, restorePreviousContext: true);
    }

    private async Task SetDomAsyncCore(
        Node domRoot,
        Uri baseUri,
        bool resetSession = true,
        bool restorePreviousContext = false)
    {
        var previousDomRoot = _currentDomRoot;
        var previousBaseUri = _currentBaseUri;
        var previousReadyState = _documentReadyState;

        BeginScriptLoadingSnapshot(domRoot, baseUri);
        BeginEventLoopSnapshot(domRoot, baseUri);
        LogScriptLoading(
            "SetDomStarted",
            LogSeverity.Debug,
            "[FenJsBridge] SetDomAsyncCore called",
            new Dictionary<string, object>
            {
                ["domRootPresent"] = domRoot != null,
                ["baseUri"] = baseUri?.AbsoluteUri ?? string.Empty
            });

        if (domRoot == null)
        {
            return;
        }

        if (resetSession)
        {
            BindFenJsDomContext(domRoot, baseUri, documentReadyState: "loading");
        }
        else
        {
            RebindFenJsDomContext(domRoot, baseUri, documentReadyState: "loading");
        }

        var windowContext = !resetSession
            ? ActivateSubdocumentWindowContext(domRoot, baseUri)
            : null;

        try
        {
            LogScriptLoading(
                "SetDomReadyForScripts",
                LogSeverity.Debug,
                "[FenJsBridge] About to execute page scripts",
                new Dictionary<string, object>
                {
                    ["domRootTag"] = (domRoot as Element)?.TagName ?? "null",
                    ["descendantCount"] = domRoot.Descendants().Count()
                });
            WireInlineEventHandlers(domRoot);
            await ExecutePageScriptsWithFenJsAsync(domRoot, baseUri).ConfigureAwait(false);
            ApplyScriptingEnabledSanitizer(domRoot);
            DispatchStartupLifecycleEvents();
        }
        finally
        {
            windowContext?.Dispose();
            if (restorePreviousContext && previousDomRoot != null)
            {
                RebindFenJsDomContext(previousDomRoot, previousBaseUri, previousReadyState);
            }
        }
    }

    private bool CanEvaluateWithFenJs(string script)
    {
        return !string.IsNullOrWhiteSpace(script);
    }

    private static long ResolveFenJsScriptTimeoutMs()
    {
        const long defaultTimeoutMs = 300000;
        var raw = Environment.GetEnvironmentVariable("FEN_FENJS_SCRIPT_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return parsed;
        }

        return defaultTimeoutMs;
    }

    private static long ResolveFenJsInputEventTimeoutMs()
    {
        const long defaultTimeoutMs = 2000;
        var raw = Environment.GetEnvironmentVariable("FEN_FENJS_INPUT_EVENT_TIMEOUT_MS");
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
        var rawResult = EvaluateWithFenJsRaw(script);
        if (rawResult.Tag == FenBrowser.Js.Runtime.JsValueTag.Object ||
            rawResult.Tag == FenBrowser.Js.Runtime.JsValueTag.HostObject)
        {
            return ConvertJsValueToObject(rawResult);
        }
        return ConvertFenJsValue(rawResult);
    }

    private JsValue EvaluateWithFenJsRaw(string script)
    {
        // Snapshot the session generation at dispatch time. If a navigation
        // resets the session before the worker executes this lambda, the
        // generation will have changed and we can abort cleanly.
        var dispatchGeneration = _fenJsSessionGeneration;

        try
        {
            return RunFenJsWithLargeStack(() =>
            {
                lock (_fenJsLock)
                {
                    // If the session was reset between dispatch and now, the
                    // compiler/interpreter are gone — bail cleanly.
                    if (dispatchGeneration != _fenJsSessionGeneration ||
                        _compiler == null || _interpreter == null)
                    {
                        throw new InvalidOperationException(
                            "[FenJsBridge] JS session was reset during evaluation dispatch " +
                            $"(dispatchGen={dispatchGeneration} currentGen={_fenJsSessionGeneration} " +
                            $"_compiler={_compiler != null} _interpreter={_interpreter != null}). " +
                            "This is expected when a page script triggers a navigation " +
                            "(e.g. WAF challenge → location.reload).");
                    }
                    _fenJsEvaluationCount++;
                    var function = _compiler.CompileScript(new SourceText(script, "<fenbrowser-fenjs-eval>"));
                    new BytecodeVerifier().Verify(function);
                    return _interpreter.Execute(function);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            // Session-reset InvalidOperationException is expected after async
            // navigation — surface it cleanly without a stack trace.
            LogScriptLoading(
                "ScriptEvaluationSkipped",
                LogSeverity.Warn,
                "[FenJsBridge] EvaluateWithFenJsRaw skipped after session reset",
                new Dictionary<string, object>
                {
                    ["scriptSample"] = TruncateForLog(script, 160),
                    ["error"] = ex.Message
                });
            return JsValue.Undefined;
        }
        catch (Exception ex) when (ex is not JsThrownException)
        {
            var message = $"[FenJsBridge] EvaluateWithFenJsRaw FAILED for script '{script}': {ex.GetType().Name}: {ex.Message}";
            if (ex.StackTrace is { } st)
                message += "\n  Stack: " + st.Split('\n').FirstOrDefault()?.Trim();
            LogScriptLoading(
                "ScriptEvaluationInfrastructureFailed",
                LogSeverity.Error,
                message,
                new Dictionary<string, object>
                {
                    ["scriptSample"] = TruncateForLog(script, 160),
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                });
            throw;
        }
    }

    private JsValue CreateLegacyDomEvent(string interfaceName)
    {
        var constructorName = (interfaceName ?? "Event").Trim().ToLowerInvariant() switch
        {
            "customevent" => "CustomEvent",
            "mouseevent" or "mouseevents" => "MouseEvent",
            "wheelevent" or "wheelevents" => "WheelEvent",
            "keyboardevent" or "keyboardevents" => "KeyboardEvent",
            "uievent" or "uievents" => "UIEvent",
            "beforeunloadevent" => "BeforeUnloadEvent",
            _ => "Event"
        };

        return EvaluateWithFenJsRaw($"new {constructorName}('')");
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
    private const int FenJsBrowserParserMaxRecursionDepth = 1024;

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
        return RunFenJsWithLargeStack(work, waitForWorkerMs: -1);
    }

    private T RunFenJsWithLargeStack<T>(Func<T> work, long waitForWorkerMs)
    {
        if (_onFenJsLargeStackThread)
        {
            // Re-entrant call from within the large-stack thread itself —
            // run inline to avoid deadlocking the persistent worker.
            return work();
        }

        EnsureFenJsWorkerRunning();

        // Serialise the whole dispatch/wait/result handshake. If the lock only
        // protects posting, a second timer or rAF callback can overwrite the
        // pending worker delegate before the first waiter observes its result.
        var lockTaken = false;
        try
        {
            if (waitForWorkerMs >= 0)
            {
                lockTaken = Monitor.TryEnter(_fenJsWorkGate, TimeSpan.FromMilliseconds(waitForWorkerMs));
                if (!lockTaken)
                {
                    throw new FenBrowser.FenEngine.Errors.FenTimeoutError(
                        $"Timed out waiting for FenJS worker after {waitForWorkerMs} ms.");
                }
            }
            else
            {
                Monitor.Enter(_fenJsWorkGate, ref lockTaken);
            }

            _fenJsWorkException = null;
            _fenJsWorkResult = null;
            _fenJsPendingWork = () => (object)work();
            _fenJsWorkAvailable.Set();
            _fenJsWorkDone.WaitOne();

            var captured = _fenJsWorkException;
            var result = _fenJsWorkResult;
            _fenJsWorkException = null;
            _fenJsWorkResult = null;
            _fenJsPendingWork = null;
            captured?.Throw();

            if (result == null && typeof(T).IsValueType)
            {
                throw new InvalidOperationException(
                    "[FenJsBridge] JS worker returned null; the JS session was likely " +
                    "reset by a navigation while the evaluation was in flight. " +
                    "(_fenJsSessionGeneration=" + _fenJsSessionGeneration + ")");
            }

            return (T)result;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_fenJsWorkGate);
            }
        }

    }

    private void BeginScriptLoadingSnapshot(Node domRoot, Uri baseUri)
    {
        _currentNavigationId = LogContext.CurrentCorrelationId;
        lock (_scriptLoadingLock)
        {
            _lastScriptLoadingSnapshot = new BrowserScriptLoadingSnapshot
            {
                Status = "running",
                StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                BaseUrl = baseUri?.AbsoluteUri ?? string.Empty,
                DomRootPresent = domRoot != null
            };
        }
    }

    private void UpdateScriptLoadingSnapshot(Action<BrowserScriptLoadingSnapshot> update)
    {
        if (update == null)
        {
            return;
        }

        lock (_scriptLoadingLock)
        {
            _lastScriptLoadingSnapshot ??= new BrowserScriptLoadingSnapshot();
            update(_lastScriptLoadingSnapshot);
        }
    }

    private BrowserScriptLoadingRecord AddScriptLoadingRecord(Element scriptElement, int ordinal, bool isModule, bool isAsync, bool isDefer)
    {
        var src = scriptElement?.GetAttribute("src") ?? string.Empty;
        var record = new BrowserScriptLoadingRecord
        {
            Ordinal = ordinal,
            ScriptId = BuildScriptId(ordinal),
            SourceLabel = BuildScriptSourceLabel(scriptElement, ordinal),
            SourceOffset = scriptElement?.SourceOffset ?? -1,
            SourceLine = scriptElement?.SourceLine ?? 0,
            SourceColumn = scriptElement?.SourceColumn ?? 0,
            Kind = isModule ? "module" : "classic",
            SourceType = string.IsNullOrEmpty(src) ? "inline" : "external",
            Src = src,
            Type = scriptElement?.GetAttribute("type") ?? string.Empty,
            IsAsync = isAsync,
            IsDefer = isDefer,
            IsNoModule = scriptElement?.HasAttribute("nomodule") ?? false,
            TextLength = scriptElement?.TextContent?.Length ?? 0,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        lock (_scriptLoadingLock)
        {
            _lastScriptLoadingSnapshot ??= new BrowserScriptLoadingSnapshot();
            _lastScriptLoadingSnapshot.Scripts.Add(record);
        }

        return record;
    }

    private BrowserScriptLoadingRecord AddDynamicScriptLoadingRecord(Element scriptElement)
    {
        var ordinal = Interlocked.Increment(ref _dynamicScriptTraceCounter);
        var src = scriptElement?.GetAttribute("src") ?? string.Empty;
        var type = scriptElement?.GetAttribute("type")?.ToLowerInvariant() ?? string.Empty;
        var isModule = type == "module";
        var record = new BrowserScriptLoadingRecord
        {
            Ordinal = ordinal,
            ScriptId = "dynamic-script-" + ordinal.ToString(CultureInfo.InvariantCulture),
            SourceLabel = string.IsNullOrWhiteSpace(src)
                ? "dynamic-inline#" + ordinal.ToString(CultureInfo.InvariantCulture)
                : "dynamic-external:" + src.Trim(),
            SourceOffset = scriptElement?.SourceOffset ?? -1,
            SourceLine = scriptElement?.SourceLine ?? 0,
            SourceColumn = scriptElement?.SourceColumn ?? 0,
            Kind = isModule ? "module" : "classic",
            SourceType = string.IsNullOrEmpty(src) ? "inline" : "external",
            Src = src,
            Type = scriptElement?.GetAttribute("type") ?? string.Empty,
            IsAsync = true,
            IsDefer = false,
            IsNoModule = scriptElement?.HasAttribute("nomodule") ?? false,
            ParserInserted = false,
            TextLength = scriptElement?.TextContent?.Length ?? 0,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        lock (_scriptLoadingLock)
        {
            _lastScriptLoadingSnapshot ??= new BrowserScriptLoadingSnapshot();
            _lastScriptLoadingSnapshot.Scripts.Add(record);
        }

        return record;
    }

    private void UpdateScriptLoadingRecord(BrowserScriptLoadingRecord record, Action<BrowserScriptLoadingRecord> update)
    {
        if (record == null || update == null)
        {
            return;
        }

        lock (_scriptLoadingLock)
        {
            update(record);
        }
    }

    private void MarkScriptSkipped(BrowserScriptLoadingRecord record, string reason)
    {
        UpdateScriptLoadingRecord(record, script =>
        {
            script.Status = "skipped";
            script.Failure = reason ?? string.Empty;
            script.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        });
        UpdateScriptLoadingSnapshot(snapshot => snapshot.SkippedScripts++);
        var fields = CreateScriptRecordFields(record);
        fields["ordinal"] = record?.Ordinal ?? 0;
        fields["src"] = record?.Src ?? string.Empty;
        fields["reason"] = reason ?? string.Empty;
        LogScriptLoading(
            "ScriptSkipped",
            LogSeverity.Debug,
            "[FenJsBridge] Script skipped",
            fields);
    }

    private static string BuildScriptId(int ordinal)
    {
        return "script-" + Math.Max(0, ordinal).ToString(CultureInfo.InvariantCulture);
    }

    private static string BuildScriptSourceLabel(Element scriptElement, int ordinal)
    {
        var scriptId = BuildScriptId(ordinal);
        var src = scriptElement?.GetAttribute("src");
        if (!string.IsNullOrWhiteSpace(src))
        {
            return "external:" + src.Trim();
        }

        return "inline#" + scriptId.Substring("script-".Length);
    }

    private static void AddScriptRecordFields(IDictionary<string, object> fields, BrowserScriptLoadingRecord record)
    {
        if (fields == null || record == null)
        {
            return;
        }

        fields["scriptId"] = record.ScriptId ?? string.Empty;
        fields["scriptOrdinal"] = record.Ordinal;
        fields["scriptSourceLabel"] = record.SourceLabel ?? string.Empty;
        fields["scriptSourceType"] = record.SourceType ?? string.Empty;
        fields["scriptSourceOffset"] = record.SourceOffset;
        fields["scriptSourceLine"] = record.SourceLine;
        fields["scriptSourceColumn"] = record.SourceColumn;
        fields["sourceLabel"] = record.SourceLabel ?? string.Empty;
        fields["sourceType"] = record.SourceType ?? string.Empty;
        fields["scriptSrc"] = record.Src ?? string.Empty;
        fields["scriptResolvedUrl"] = record.ResolvedUrl ?? string.Empty;
        fields["url"] = string.IsNullOrWhiteSpace(record.ResolvedUrl) ? record.Src ?? string.Empty : record.ResolvedUrl;
        fields["inline"] = string.Equals(record.SourceType, "inline", StringComparison.OrdinalIgnoreCase);
        fields["external"] = string.Equals(record.SourceType, "external", StringComparison.OrdinalIgnoreCase);
        fields["classic"] = string.Equals(record.Kind, "classic", StringComparison.OrdinalIgnoreCase);
        fields["module"] = string.Equals(record.Kind, "module", StringComparison.OrdinalIgnoreCase);
        fields["async"] = record.IsAsync;
        fields["defer"] = record.IsDefer;
        fields["parserInserted"] = record.ParserInserted;
        fields["blockingStatus"] = ResolveScriptBlockingStatus(record);
        fields["fetchStatus"] = record.Status ?? string.Empty;
        fields["mimeType"] = record.Type ?? string.Empty;
        fields["executionOrder"] = record.Ordinal;
    }

    private static Dictionary<string, object> CreateScriptRecordFields(BrowserScriptLoadingRecord record)
    {
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        AddScriptRecordFields(fields, record);
        return fields;
    }

    private static string ResolveScriptBlockingStatus(BrowserScriptLoadingRecord record)
    {
        if (record == null)
        {
            return string.Empty;
        }

        if (record.IsAsync)
        {
            return "non-blocking";
        }

        if (record.IsDefer)
        {
            return "domcontentloaded-blocking";
        }

        return "parser-blocking";
    }

    private static EngineLogContext CreateScriptLogContext(IReadOnlyDictionary<string, object> fields)
    {
        if (fields == null)
        {
            return new EngineLogContext(NavigationId: LogContext.CurrentCorrelationId);
        }

        var resourceUrl = GetStringField(fields, "scriptResolvedUrl");
        if (string.IsNullOrWhiteSpace(resourceUrl))
        {
            resourceUrl = GetStringField(fields, "url");
        }

        if (string.IsNullOrWhiteSpace(resourceUrl))
        {
            resourceUrl = GetStringField(fields, "scriptSrc");
        }

        return new EngineLogContext(
            NavigationId: LogContext.CurrentCorrelationId,
            Url: GetStringField(fields, "baseUri"),
            ResourceUrl: resourceUrl,
            ScriptId: GetStringField(fields, "scriptId"));
    }

    private static string GetStringField(IReadOnlyDictionary<string, object> fields, string key)
    {
        if (fields == null ||
            string.IsNullOrWhiteSpace(key) ||
            !fields.TryGetValue(key, out var value) ||
            value == null)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void LogScriptLoading(
        string eventName,
        LogSeverity severity,
        string message,
        IReadOnlyDictionary<string, object> fields = null,
        LogMarker marker = LogMarker.None,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var payload = fields != null
            ? new Dictionary<string, object>(fields, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        payload["event"] = eventName ?? string.Empty;
        payload["eventName"] = eventName ?? string.Empty;
        payload["traceCategory"] = "ScriptLoader";
        var context = CreateScriptLogContext(payload);

        EngineLog.Write(
            LogSubsystem.Js,
            severity,
            message,
            marker,
            context,
            payload,
            sourceFile,
            sourceLine,
            sourceMember);
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value.Substring(0, maxLength) + "...";
    }

    private void BeginEventLoopSnapshot(Node domRoot, Uri baseUri)
    {
        lock (_eventLoopLock)
        {
            _lastEventLoopSnapshot = new BrowserEventLoopSnapshot
            {
                Status = "running",
                StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                BaseUrl = baseUri?.AbsoluteUri ?? string.Empty,
                DomRootPresent = domRoot != null,
                DocumentReadyState = "loading",
                PendingHostTimers = _fenJsTimers.Count
            };
        }

        AddEventLoopRecord("EventLoopStarted", "document-bound");
    }

    private void UpdateEventLoopSnapshot(Action<BrowserEventLoopSnapshot> update)
    {
        if (update == null)
        {
            return;
        }

        var readyState = GetDocumentReadyState();
        var pendingHostTimers = _fenJsTimers.Count;
        lock (_eventLoopLock)
        {
            _lastEventLoopSnapshot ??= new BrowserEventLoopSnapshot();
            update(_lastEventLoopSnapshot);
            _lastEventLoopSnapshot.PendingHostTimers = pendingHostTimers;
            _lastEventLoopSnapshot.DocumentReadyState = readyState;
        }
    }

    private void AddEventLoopRecord(string eventName, string detail = "", long id = 0, int delayMs = 0, bool repeating = false)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var readyState = GetDocumentReadyState();
        var pendingHostTimers = _fenJsTimers.Count;
        lock (_eventLoopLock)
        {
            _lastEventLoopSnapshot ??= new BrowserEventLoopSnapshot();
            _lastEventLoopSnapshot.Events.Add(new BrowserEventLoopRecord
            {
                EventName = eventName ?? string.Empty,
                TimestampUtc = timestamp,
                Detail = detail ?? string.Empty,
                Id = id,
                DelayMs = delayMs,
                Repeating = repeating,
                DocumentReadyState = readyState
            });
            _lastEventLoopSnapshot.PendingHostTimers = pendingHostTimers;
            _lastEventLoopSnapshot.DocumentReadyState = readyState;
        }
    }

    private void RecordMicrotaskCheckpoint(string detail)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        UpdateEventLoopSnapshot(snapshot =>
        {
            snapshot.MicrotaskCheckpoints++;
            snapshot.LastMicrotaskCheckpointUtc = timestamp;
        });
        AddEventLoopRecord("MicrotaskCheckpointCompleted", detail ?? string.Empty);
        LogEventLoop(
            "MicrotaskCheckpointCompleted",
            LogSeverity.Debug,
            "[FenJsBridge] Microtask checkpoint completed",
            new Dictionary<string, object>
            {
                ["detail"] = detail ?? string.Empty
            });
    }

    private void MarkEventLoopCompleted()
    {
        UpdateEventLoopSnapshot(snapshot =>
        {
            snapshot.Status = "completed";
            snapshot.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        });
    }

    private static void LogEventLoop(
        string eventName,
        LogSeverity severity,
        string message,
        IReadOnlyDictionary<string, object> fields = null,
        LogMarker marker = LogMarker.None,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var payload = fields != null
            ? new Dictionary<string, object>(fields, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        payload["event"] = eventName ?? string.Empty;
        payload["eventName"] = eventName ?? string.Empty;
        payload["traceCategory"] = "EventLoop";
        var taskId = ResolveEventLoopTaskId(payload, eventName);

        EngineLog.Write(
            LogSubsystem.Event,
            severity,
            message,
            marker,
            new EngineLogContext(
                NavigationId: LogContext.CurrentCorrelationId,
                TaskId: taskId),
            payload,
            sourceFile,
            sourceLine,
            sourceMember);
    }

    private static string ResolveEventLoopTaskId(IReadOnlyDictionary<string, object> fields, string eventName)
    {
        var taskId = GetStringField(fields, "taskId");
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            return taskId;
        }

        var id = GetStringField(fields, "id");
        if (string.IsNullOrWhiteSpace(id) || id == "0")
        {
            return null;
        }

        var origin = GetStringField(fields, "origin");
        if (string.Equals(origin, "requestAnimationFrame", StringComparison.Ordinal) ||
            (eventName?.IndexOf("AnimationFrame", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
        {
            return "raf-" + id;
        }

        if (string.Equals(origin, "setTimeout", StringComparison.Ordinal) ||
            string.Equals(origin, "setInterval", StringComparison.Ordinal) ||
            (eventName?.IndexOf("Timer", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (eventName?.IndexOf("Interval", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
        {
            return "timer-" + id;
        }

        return "callback-" + id;
    }

    private async Task ExecutePageScriptsWithFenJsAsync(Node domRoot, Uri baseUri)
    {
        LogScriptLoading(
            "ScriptLoadingStarted",
            LogSeverity.Debug,
            "[FenJsBridge] ExecutePageScriptsWithFenJsAsync START",
            new Dictionary<string, object>
            {
                ["domRootPresent"] = domRoot != null,
                ["baseUri"] = baseUri?.AbsoluteUri ?? string.Empty
            });
        try
        {
            var allScripts = EnumerateScriptElements(domRoot).ToList();
            UpdateScriptLoadingSnapshot(snapshot =>
            {
                snapshot.ScriptElements = allScripts.Count;
                snapshot.TotalScripts = allScripts.Count;
            });
            LogScriptLoading(
                "ScriptDiscovered",
                LogSeverity.Info,
                "[FenJsBridge] Script elements discovered",
                new Dictionary<string, object>
                {
                    ["scriptElements"] = allScripts.Count
                });

            // Phase 1: validate all scripts and kick off external fetches concurrently.
            // Each entry holds everything needed to execute the script in order.
            var items = new List<ScriptExecutionItem>(allScripts.Count);
            var fetchTasks = new Dictionary<string, Task<string>>(StringComparer.Ordinal);

            for (int i = 0; i < allScripts.Count; i++)
            {
                var scriptElement = allScripts[i];
                var type = scriptElement.GetAttribute("type")?.ToLowerInvariant() ?? string.Empty;
                var src = scriptElement.GetAttribute("src");
                bool isModule = type == "module";
                bool isAsync = false;
                bool isDefer = false;
                if (isModule)
                {
                    isDefer = !scriptElement.HasAttribute("async");
                    isAsync = scriptElement.HasAttribute("async");
                }
                else
                {
                    bool hasSrc = !string.IsNullOrEmpty(src);
                    isAsync = scriptElement.HasAttribute("async") && hasSrc;
                    isDefer = scriptElement.HasAttribute("defer") && hasSrc;
                }
                var scriptRecord = AddScriptLoadingRecord(scriptElement, i + 1, isModule, isAsync, isDefer);
                var seenFields = CreateScriptRecordFields(scriptRecord);
                seenFields["tag"] = scriptElement.TagName ?? string.Empty;
                seenFields["src"] = src ?? string.Empty;
                seenFields["type"] = scriptElement.GetAttribute("type") ?? string.Empty;
                seenFields["textLength"] = scriptElement.TextContent?.Length ?? 0;
                LogScriptLoading(
                    "ScriptDiscovered",
                    LogSeverity.Debug,
                    "[FenJsBridge] Script element seen",
                    seenFields);

                if (!string.IsNullOrEmpty(type) &&
                    type != "text/javascript" &&
                    type != "application/javascript" &&
                    type != "module")
                {
                    if (type != "application/ld+json")
                        FenBrowser.Core.EngineLogCompat.Debug(
                            $"[FenJsBridge] Skipping script with unknown type '{type}': src={scriptElement.GetAttribute("src")}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                    MarkScriptSkipped(scriptRecord, $"unsupported-type:{type}");
                    continue;
                }

                if (scriptElement.HasAttribute("nomodule"))
                {
                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenJsBridge] Skipping nomodule script: src={scriptElement.GetAttribute("src")}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);
                    MarkScriptSkipped(scriptRecord, "nomodule");
                    continue;
                }


                // Determine async/defer per WHATWG HTML §4.12.1
                // - async: execute as soon as available (external only per spec)
                // - defer: execute after parsing, before DOMContentLoaded (external only per spec)
                // - Module scripts are deferred by default; async on modules = execute ASAP
                if (isModule)
                {
                    // Modules are deferred by default; async makes them execute ASAP
                    isDefer = !scriptElement.HasAttribute("async");
                    isAsync = scriptElement.HasAttribute("async");
                }
                else
                {
                    // Classic scripts: async/defer only apply to external scripts
                    bool hasSrc = !string.IsNullOrEmpty(src);
                    isAsync = scriptElement.HasAttribute("async") && hasSrc;
                    isDefer = scriptElement.HasAttribute("defer") && hasSrc;
                }
                if (!string.IsNullOrEmpty(src))
                {
                    // External script — validate, then kick off fetch concurrently
                    UpdateScriptLoadingSnapshot(snapshot =>
                    {
                        snapshot.ExternalScripts++;
                        if (isModule)
                        {
                            snapshot.ModuleScripts++;
                        }
                    });
                    var externalFields = CreateScriptRecordFields(scriptRecord);
                    externalFields["src"] = src;
                    externalFields["isModule"] = isModule;
                    externalFields["isAsync"] = isAsync;
                    externalFields["isDefer"] = isDefer;
                    LogScriptLoading(
                        "ScriptExternalDiscovered",
                        LogSeverity.Debug,
                        "[FenJsBridge] External script discovered",
                        externalFields);

                    if (!AllowExternalScripts || !Sandbox.Allows(SandboxFeature.ExternalScripts))
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsBridge] Skipping external script (AllowExternal={AllowExternalScripts}, SandboxExternal={Sandbox.Allows(SandboxFeature.ExternalScripts)}): {src}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                        MarkScriptSkipped(scriptRecord, $"external-disabled:allowExternal={AllowExternalScripts};sandbox={Sandbox.Allows(SandboxFeature.ExternalScripts)}");
                        continue;
                    }

                    if (baseUri == null || !Uri.TryCreate(baseUri, src, out var scriptUri))
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsBridge] Skipping script with unresolvable src (baseUri={baseUri}): {src}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                        MarkScriptSkipped(scriptRecord, $"unresolvable-src:baseUri={baseUri}");
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
                            MarkScriptSkipped(scriptRecord, $"csp-block:{scriptUri}");
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
                        UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchStarted++);
                        UpdateScriptLoadingRecord(scriptRecord, record =>
                        {
                            record.ResolvedUrl = scriptUri.AbsoluteUri;
                            record.Status = "fetch-started";
                        });
                        var fetchStartedFields = CreateScriptRecordFields(scriptRecord);
                        fetchStartedFields["url"] = scriptUri.AbsoluteUri;
                        LogScriptLoading(
                            "ScriptFetchStarted",
                            LogSeverity.Debug,
                            "[FenJsBridge] External script fetch started",
                            fetchStartedFields);
                    }

                    UpdateScriptLoadingRecord(scriptRecord, record => record.ResolvedUrl = scriptUri.AbsoluteUri);
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.EligibleScripts++);
                    items.Add(new ScriptExecutionItem(scriptElement, isModule, isAsync, isDefer, fetchKey, fetchTask, scriptUri, null, scriptRecord));
                }
                else
                {
                    // Inline script — validate now, code is already in DOM
                    UpdateScriptLoadingSnapshot(snapshot =>
                    {
                        snapshot.InlineScripts++;
                        if (isModule)
                        {
                            snapshot.ModuleScripts++;
                        }
                    });
                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenJsBridge] Processing inline {(isModule ? "module" : "script")} len={scriptElement.TextContent?.Length}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);

                    if (!Sandbox.Allows(SandboxFeature.InlineScripts))
                    {
                        MarkScriptSkipped(scriptRecord, "inline-disabled");
                        continue;
                    }

                    var code = CollectScriptText(scriptElement);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        MarkScriptSkipped(scriptRecord, "empty-inline-code");
                        continue;
                    }

                    // Inline scripts: async/defer have no effect per spec; treat as blocking
                    // unless type="module" (modules are deferred by default)
                    bool inlineIsAsync = isModule && scriptElement.HasAttribute("async");
                    bool inlineIsDefer = isModule && !scriptElement.HasAttribute("async");
                    UpdateScriptLoadingRecord(scriptRecord, record =>
                    {
                        record.CodeLength = code.Length;
                        record.IsAsync = inlineIsAsync;
                        record.IsDefer = inlineIsDefer;
                        record.Status = "ready";
                    });
                    var readyFields = CreateScriptRecordFields(scriptRecord);
                    readyFields["codeLength"] = code.Length;
                    LogScriptLoading(
                        "ScriptReady",
                        LogSeverity.Debug,
                        "[FenJsBridge] Inline script ready",
                        readyFields);
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.EligibleScripts++);
                    items.Add(new ScriptExecutionItem(scriptElement, isModule, inlineIsAsync, inlineIsDefer, null, null, null, code, scriptRecord));
                }
            }

            // Categorize scripts for phased execution per WHATWG HTML §4.12.1
            var blockingItems = new List<ScriptExecutionItem>();
            var deferItems = new List<ScriptExecutionItem>();
            var asyncItems = new List<ScriptExecutionItem>();
            foreach (var item in items)
            {
                if (item.IsAsync)
                    asyncItems.Add(item);
                else if (item.IsDefer)
                    deferItems.Add(item);
                else
                    blockingItems.Add(item);
            }
            UpdateScriptLoadingSnapshot(snapshot =>
            {
                snapshot.BlockingScripts = blockingItems.Count;
                snapshot.DeferScripts = deferItems.Count;
                snapshot.AsyncScripts = asyncItems.Count;
            });
            foreach (var item in blockingItems)
            {
                UpdateScriptLoadingRecord(item.ScriptRecord, record => record.Batch = "blocking");
            }
            foreach (var item in deferItems)
            {
                UpdateScriptLoadingRecord(item.ScriptRecord, record => record.Batch = "defer");
            }
            foreach (var item in asyncItems)
            {
                UpdateScriptLoadingRecord(item.ScriptRecord, record => record.Batch = "async");
            }
            LogScriptLoading(
                "ScriptCategorized",
                LogSeverity.Info,
                "[FenJsBridge] Scripts categorized",
                new Dictionary<string, object>
                {
                    ["blocking"] = blockingItems.Count,
                    ["defer"] = deferItems.Count,
                    ["async"] = asyncItems.Count
                });

            // Phase 2a: Execute blocking scripts in document order.
            // Each blocking script must complete before the next one starts.
            LogScriptLoading("ScriptBatchStarted", LogSeverity.Debug, "[FenJsBridge] Executing blocking scripts", new Dictionary<string, object> { ["batch"] = "blocking", ["count"] = blockingItems.Count });
            await ExecuteScriptBatchAsync(blockingItems, baseUri, "blocking").ConfigureAwait(false);

            // Phase 2b: Execute defer scripts in document order after all blocking
            // scripts have completed. Deferred scripts execute before DOMContentLoaded.
            LogScriptLoading("ScriptBatchStarted", LogSeverity.Debug, "[FenJsBridge] Executing defer scripts", new Dictionary<string, object> { ["batch"] = "defer", ["count"] = deferItems.Count });
            await ExecuteScriptBatchAsync(deferItems, baseUri, "defer").ConfigureAwait(false);

            // Phase 2c: Fire-and-forget async scripts. Per WHATWG HTML §4.12.1,
            // async scripts execute as soon as they are available and must NOT
            // block DOMContentLoaded. We launch them as a background continuation
            // so the caller can fire DOMContentLoaded immediately.
            if (asyncItems.Count > 0)
            {
                UpdateScriptLoadingSnapshot(snapshot => snapshot.AsyncPendingScripts += asyncItems.Count);
                LogScriptLoading("ScriptBatchStarted", LogSeverity.Debug, "[FenJsBridge] Launching async scripts", new Dictionary<string, object> { ["batch"] = "async", ["count"] = asyncItems.Count });
                var asyncBatch = ExecuteScriptBatchAsync(asyncItems, baseUri, "async");
                _ = asyncBatch.ContinueWith(
                    task =>
                    {
                        var ex = task.Exception?.GetBaseException();
                        LogScriptLoading(
                            "AsyncScriptBatchInfrastructureFailed",
                            LogSeverity.Error,
                            "[FenJsBridge] Async script batch failed",
                            new Dictionary<string, object>
                            {
                                ["batch"] = "async",
                                ["count"] = asyncItems.Count,
                                ["errorType"] = ex?.GetType().Name ?? "TaskFaulted",
                                ["error"] = ex?.Message ?? task.Exception?.Message ?? string.Empty
                            },
                            LogMarker.EngineBug);
                    },
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            UpdateScriptLoadingSnapshot(snapshot =>
            {
                snapshot.Status = "completed";
                snapshot.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            });
            LogScriptLoading(
                "ScriptLoadingCompleted",
                LogSeverity.Info,
                "[FenJsBridge] ExecutePageScriptsWithFenJsAsync DONE",
                new Dictionary<string, object>
                {
                    ["processedSync"] = blockingItems.Count + deferItems.Count,
                    ["asyncPending"] = asyncItems.Count
                });
            // LogFenJsPageBootstrapState(); // DEBUG: disabled until NRE is fixed
        }
        catch (Exception ex)
        {
            // Per-script errors are already caught inside the execution loop.
            // This outer catch only handles infrastructure failures (e.g.
            // null _interpreter after a session reset). Log and surface but
            // do NOT re-throw — the page should render even if scripts fail.
            if (ex is JsThrownException jte && string.IsNullOrEmpty(jte.Description))
            {
                try { jte.Description = _interpreter.DescribeThrownValue(jte.Value); } catch { }
            }
            UpdateScriptLoadingSnapshot(snapshot =>
            {
                snapshot.Status = "infrastructure-error";
                snapshot.InfrastructureError = ex.Message;
                snapshot.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            });
            LogScriptLoading(
                "ScriptLoadingInfrastructureFailed",
                LogSeverity.Error,
                "[FenJsBridge] ExecutePageScriptsWithFenJsAsync infrastructure error",
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                },
                LogMarker.EngineBug);
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
            RecordMicrotaskCheckpoint("page-bootstrap-state-probe");
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

    /// <summary>
    /// Execute a batch of scripts (blocking, defer, or async) in document order.
    /// Each script's external fetch is awaited individually; execution errors are
    /// logged but never re-thrown (per WHATWG HTML §8.1.3.2).
    /// </summary>
    private async Task ExecuteScriptBatchAsync(
        List<ScriptExecutionItem> batch, Uri baseUri, string batchLabel)
    {
        if (batch.Count == 0) return;

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            string code;
            Uri moduleUri = item.ModuleUri;
            bool isModule = item.IsModule;
            if (!string.Equals(batchLabel, "async", StringComparison.OrdinalIgnoreCase))
            {
                var blockedFields = CreateScriptRecordFields(item.ScriptRecord);
                blockedFields["batch"] = batchLabel;
                blockedFields["blocksDOMContentLoaded"] = true;
                blockedFields["reason"] = string.Equals(batchLabel, "defer", StringComparison.OrdinalIgnoreCase)
                    ? "defer-script-before-domcontentloaded"
                    : "parser-blocking-script";
                LogScriptLoading(
                    "DOMContentLoadedBlockedByScript",
                    LogSeverity.Debug,
                    "[FenJsBridge] Script blocks DOMContentLoaded",
                    blockedFields);
            }

            if (item.FetchKey != null)
            {
                // Await this individual fetch — it may already be complete since
                // all fetches were kicked off concurrently in Phase 1.
                try
                {
                    code = await item.FetchTask.ConfigureAwait(false);
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchCompleted++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.CodeLength = code?.Length ?? 0;
                        record.Status = "ready";
                    });
                    var fetchCompletedFields = CreateScriptRecordFields(item.ScriptRecord);
                    fetchCompletedFields["url"] = item.FetchKey;
                    fetchCompletedFields["batch"] = batchLabel;
                    fetchCompletedFields["codeLength"] = code?.Length ?? 0;
                    LogScriptLoading(
                        "ScriptFetchCompleted",
                        LogSeverity.Debug,
                        "[FenJsBridge] External script fetch completed",
                        fetchCompletedFields);
                    var readyFields = CreateScriptRecordFields(item.ScriptRecord);
                    readyFields["url"] = item.FetchKey;
                    readyFields["batch"] = batchLabel;
                    readyFields["codeLength"] = code?.Length ?? 0;
                    LogScriptLoading(
                        "ScriptReady",
                        LogSeverity.Debug,
                        "[FenJsBridge] External script ready",
                        readyFields);
                }
                catch (Exception ex)
                {
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchFailed++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.Status = "fetch-failed";
                        record.Failure = ex.GetType().Name + ": " + ex.Message;
                        record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    });
                    FenBrowser.Core.EngineLogCompat.Warn(
                        $"[FenJsBridge] Fetch failed for '{item.FetchKey}' ({batchLabel}): {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.JavaScript);
                    var fetchFailedFields = CreateScriptRecordFields(item.ScriptRecord);
                    fetchFailedFields["url"] = item.FetchKey;
                    fetchFailedFields["batch"] = batchLabel;
                    fetchFailedFields["errorType"] = ex.GetType().Name;
                    fetchFailedFields["error"] = ex.Message;
                    LogScriptLoading(
                        "ScriptFetchFailed",
                        LogSeverity.Warn,
                        "[FenJsBridge] Fetch failed",
                        fetchFailedFields);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(code))
                {
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.SkippedScripts++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.Status = "skipped";
                        record.Failure = "empty-code";
                        record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    });
                    var skippedFields = CreateScriptRecordFields(item.ScriptRecord);
                    skippedFields["url"] = item.FetchKey;
                    skippedFields["batch"] = batchLabel;
                    LogScriptLoading(
                        "ScriptSkipped",
                        LogSeverity.Debug,
                        "[FenJsBridge] Script skipped because fetched code was empty",
                        skippedFields);
                    continue;
                }
            }
            else
            {
                code = item.InlineCode;
            }

            UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionStarted++);
            UpdateScriptLoadingRecord(item.ScriptRecord, record =>
            {
                record.Batch = batchLabel;
                record.CodeLength = code?.Length ?? record.CodeLength;
                record.Status = "execution-started";
            });
            var executionStartedFields = CreateScriptRecordFields(item.ScriptRecord);
            executionStartedFields["batch"] = batchLabel;
            executionStartedFields["source"] = item.FetchKey ?? "inline";
            executionStartedFields["isModule"] = isModule;
            executionStartedFields["codeLength"] = code?.Length ?? 0;
            var executionLogSeverity = string.Equals(batchLabel, "async", StringComparison.OrdinalIgnoreCase)
                ? LogSeverity.Info
                : LogSeverity.Debug;
            LogScriptLoading(
                "ScriptExecutionStarted",
                executionLogSeverity,
                "[FenJsBridge] Script execution started",
                executionStartedFields);
            RunFenJsWithLargeStack<object>(() =>
            {
                try
                {
                    SetCurrentScriptElement(item.ScriptElement, item.ScriptRecord);
                    if (isModule)
                    {
                        EvaluateModuleWithFenJs(code, moduleUri);
                    }
                    else
                    {
                        EvaluateWithFenJsRaw(code);
                    }
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionCompleted++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.Status = "executed";
                        record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    });
                    var executionCompletedFields = CreateScriptRecordFields(item.ScriptRecord);
                    executionCompletedFields["batch"] = batchLabel;
                    executionCompletedFields["source"] = item.FetchKey ?? "inline";
                    executionCompletedFields["isModule"] = isModule;
                    LogScriptLoading(
                        "ScriptExecutionCompleted",
                        executionLogSeverity,
                        "[FenJsBridge] Script execution completed",
                        executionCompletedFields);
                }
                catch (JsThrownException jte)
                {
                    var desc = jte.Description;
                    if (string.IsNullOrEmpty(desc))
                    {
                        try { desc = _interpreter.DescribeThrownValue(jte.Value); } catch { }
                    }
                    var srcAttr = item.ScriptElement?.GetAttribute("src");
                    var origin = string.IsNullOrEmpty(srcAttr)
                        ? $"inline script (first {Math.Min(code?.Length ?? 0, 120)} chars)"
                        : srcAttr;
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionFailed++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.Status = "execution-failed";
                        record.Failure = desc ?? jte.Message ?? string.Empty;
                        record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    });
                    RecordMissingGlobalReference(desc ?? jte.Message, item.ScriptRecord, baseUri);
                    var scriptFailedFields = CreateScriptRecordFields(item.ScriptRecord);
                    scriptFailedFields["batch"] = batchLabel;
                    scriptFailedFields["origin"] = origin;
                    scriptFailedFields["errorType"] = "JsThrownException";
                    scriptFailedFields["error"] = desc ?? jte.Message;
                    LogScriptLoading(
                        "ScriptExecutionFailed",
                        LogSeverity.Error,
                        "[FenJsBridge] Script error",
                        scriptFailedFields,
                        LogMarker.EngineBug);
                }
                catch (Exception ex)
                {
                    var srcAttr = item.ScriptElement?.GetAttribute("src");
                    var origin = string.IsNullOrEmpty(srcAttr)
                        ? $"inline script (first {Math.Min(code?.Length ?? 0, 120)} chars)"
                        : srcAttr;
                    UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionFailed++);
                    UpdateScriptLoadingRecord(item.ScriptRecord, record =>
                    {
                        record.Status = "execution-failed";
                        record.Failure = ex.GetType().Name + ": " + ex.Message;
                        record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    });
                    var scriptFailedFields = CreateScriptRecordFields(item.ScriptRecord);
                    scriptFailedFields["batch"] = batchLabel;
                    scriptFailedFields["origin"] = origin;
                    scriptFailedFields["errorType"] = ex.GetType().Name;
                    scriptFailedFields["error"] = ex.Message;
                    LogScriptLoading(
                        "ScriptExecutionFailed",
                        LogSeverity.Error,
                        "[FenJsBridge] Non-JS script error",
                        scriptFailedFields,
                        LogMarker.EngineBug);
                }
                finally
                {
                    if (ShouldCaptureNavigationGlobals() && baseUri != null)
                    {
                        CaptureNavigationGlobals(baseUri, -2000 - i);
                    }
                    SetCurrentScriptElement(null);
                }
                return null;
            });
        }

        if (string.Equals(batchLabel, "async", StringComparison.OrdinalIgnoreCase))
        {
            UpdateScriptLoadingSnapshot(snapshot => snapshot.AsyncPendingScripts = Math.Max(0, snapshot.AsyncPendingScripts - batch.Count));
            try
            {
                RequestRender?.Invoke();
                LogScriptLoading(
                    "AsyncScriptBatchRenderRequested",
                    LogSeverity.Debug,
                    "[FenJsBridge] Async script batch requested repaint",
                    new Dictionary<string, object> { ["batch"] = batchLabel, ["count"] = batch.Count });
            }
            catch
            {
                // Repaint requests are best-effort; script execution already completed.
            }
        }
    }

    private void RecordMissingGlobalReference(
        string errorDescription,
        BrowserScriptLoadingRecord scriptRecord = null,
        Uri baseUri = null)
    {
        if (!TryExtractMissingGlobalReference(errorDescription, out var apiName))
        {
            return;
        }

        EngineCapabilities.LogUnsupportedJs("globalThis", apiName, "missing global reference");
        RecordMissingBrowserApi(
            "globalThis",
            apiName,
            "missing global reference",
            errorDescription,
            scriptRecord,
            baseUri);
    }

    private static bool TryExtractMissingGlobalReference(string errorDescription, out string apiName)
    {
        apiName = null;
        if (string.IsNullOrWhiteSpace(errorDescription))
        {
            return false;
        }

        var referencePrefix = "ReferenceError:";
        var prefixIndex = errorDescription.IndexOf(referencePrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            return false;
        }

        var nameStart = prefixIndex + referencePrefix.Length;
        var suffixIndex = errorDescription.IndexOf(" is not defined", nameStart, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex <= nameStart)
        {
            return false;
        }

        var candidate = errorDescription.Substring(nameStart, suffixIndex - nameStart).Trim();
        if (!IsMissingGlobalReferenceName(candidate))
        {
            return false;
        }

        apiName = candidate;
        return true;
    }

    private static bool IsMissingGlobalReferenceName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var first = candidate[0];
        if (!(char.IsLetter(first) || first == '_' || first == '$'))
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            var ch = candidate[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '$'))
            {
                return false;
            }
        }

        return true;
    }

    private void RecordMissingHostProperty(string ownerName, string property, Uri baseUri)
    {
        EngineCapabilities.LogUnsupportedJs(ownerName, property, "missing host property");
        RecordMissingBrowserApi(
            ownerName,
            property,
            "missing host property",
            string.Empty,
            GetCurrentScriptRecord(),
            baseUri ?? _currentBaseUri);
    }

    private void RecordMissingBrowserApi(
        string objectOrPrototype,
        string propertyName,
        string reason,
        string exceptionText,
        BrowserScriptLoadingRecord scriptRecord,
        Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(objectOrPrototype) || string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        scriptRecord ??= GetCurrentScriptRecord();
        var currentScriptElement = scriptRecord == null ? GetCurrentScriptElement() : null;
        var siteUri = baseUri ?? _currentBaseUri;
        var scriptUrl = ResolveMissingApiScriptUrl(scriptRecord, currentScriptElement, siteUri);
        var line = NormalizeSourcePosition(scriptRecord?.SourceLine ?? currentScriptElement?.SourceLine ?? 0);
        var column = NormalizeSourcePosition(scriptRecord?.SourceColumn ?? currentScriptElement?.SourceColumn ?? 0);
        var ownerName = objectOrPrototype.Trim();
        var property = propertyName.Trim();

        MissingApiTracker.Record(new MissingApiObservation
        {
            ApiName = ownerName + "." + property,
            ObjectOrPrototype = ownerName,
            PropertyName = property,
            SiteUrl = siteUri?.AbsoluteUri ?? string.Empty,
            ScriptUrl = scriptUrl,
            ScriptId = scriptRecord?.ScriptId ?? string.Empty,
            NavigationId = _currentNavigationId ?? LogContext.CurrentCorrelationId ?? string.Empty,
            Line = line,
            Column = column,
            Reason = reason ?? string.Empty,
            ExceptionText = exceptionText ?? string.Empty
        });
    }

    private static int? NormalizeSourcePosition(int value)
        => value > 0 ? value : null;

    private static string ResolveMissingApiScriptUrl(
        BrowserScriptLoadingRecord scriptRecord,
        Element scriptElement,
        Uri baseUri)
    {
        if (!string.IsNullOrWhiteSpace(scriptRecord?.ResolvedUrl))
        {
            return scriptRecord.ResolvedUrl;
        }

        var src = !string.IsNullOrWhiteSpace(scriptRecord?.Src)
            ? scriptRecord.Src
            : scriptElement?.GetAttribute("src");
        if (!string.IsNullOrWhiteSpace(src) &&
            TryResolveUri(src, baseUri, out var scriptUri))
        {
            return scriptUri.AbsoluteUri;
        }

        if (Uri.TryCreate(src, UriKind.Absolute, out var absoluteScriptUri))
        {
            return absoluteScriptUri.AbsoluteUri;
        }

        return string.Empty;
    }

    private sealed class ScriptExecutionItem
    {
        public readonly Element ScriptElement;
        public readonly bool IsModule;
        public readonly bool IsAsync;           // async attribute (external scripts only per spec)
        public readonly bool IsDefer;           // defer attribute (external scripts only per spec)
        public readonly string FetchKey;        // non-null for external scripts
        public readonly Task<string> FetchTask; // non-null for external scripts
        public readonly Uri ModuleUri;          // non-null for external scripts
        public readonly string InlineCode;      // non-null for inline scripts
        public readonly BrowserScriptLoadingRecord ScriptRecord;

        public ScriptExecutionItem(Element scriptElement, bool isModule, bool isAsync, bool isDefer,
            string fetchKey, Task<string> fetchTask, Uri moduleUri, string inlineCode,
            BrowserScriptLoadingRecord scriptRecord)
        {
            ScriptElement = scriptElement;
            IsModule = isModule;
            IsAsync = isAsync;
            IsDefer = isDefer;
            FetchKey = fetchKey;
            FetchTask = fetchTask;
            ModuleUri = moduleUri;
            InlineCode = inlineCode;
            ScriptRecord = scriptRecord;
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
            _compiler = new BytecodeCompiler
            {
                ParserMaxRecursionDepth = FenJsBrowserParserMaxRecursionDepth
            };
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
                // Instruction budget prevents truly-infinite loops from hanging
                // the browser for the full wall-clock timeout (300 s).  100M
                // instructions is ~5-10 s of interpreted bytecode on a modern
                // CPU — enough for even the largest page bundles to finish.
                InstructionBudget = 100_000_000,
                MaxCallDepth = 1024,
                ParserMaxRecursionDepth = FenJsBrowserParserMaxRecursionDepth
            };

            // Wire a diagnostic Promise rejection tracker so unhandled rejections
            // surface in engine logs with the rejection reason. This is critical for
            // debugging SPA boot failures (e.g., WhatsApp Web).
            _interpreter.PromiseRejectionTracker = new FenJsDiagnosticPromiseRejectionTracker(
                _interpreter.PromiseRejectionTracker);

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
            _iframeWindowEventListeners = new ConditionalWeakTable<Element, List<BrowserEventListener>>();
            _hostPrototypeNames.Clear();
            _activeWindowEventListeners = null;
            _activeWindowEventTarget = JsValue.Undefined;
            _activeParentBaseUri = null;
            _fenJsDomConstructorsInstalled = false;

            // Close all active WebSocket connections on session reset.
            lock (_webSocketHosts)
            {
                foreach (var (_, host) in _webSocketHosts)
                {
                    try { host.Close(1001, "Navigation"); } catch { }
                    try { host.Dispose(); } catch { }
                }
                _webSocketHosts.Clear();
            }

            _hostHooks.Reset();

            if (_currentDomRoot != null)
            {
                InstallFenJsDomGlobals(_currentDomRoot, _currentBaseUri);
            }

            // Bump the generation so any in-flight work lambdas from the
            // previous session can detect the reset and abort gracefully.
            Interlocked.Increment(ref _fenJsSessionGeneration);
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

            // Process iframes that exist in the static HTML (src/srcdoc attributes).
            // Dynamic iframes inserted later are handled by QueueFrameLoadsForTree
            // called from appendChild/insertBefore/insertAdjacentHTML/insertBefore.
            QueueFrameLoadsForTree(domRoot);
        }
            return null;
        });
    }

    private void RebindFenJsDomContext(Node domRoot, Uri baseUri, string documentReadyState)
    {
        if (domRoot == null)
        {
            return;
        }

        RunFenJsWithLargeStack<object>(() =>
        {
            lock (_fenJsLock)
            {
                _currentDomRoot = domRoot;
                _currentBaseUri = baseUri;
                _documentReadyState = string.IsNullOrWhiteSpace(documentReadyState)
                    ? "loading"
                    : documentReadyState;

                var document = domRoot as Document ?? domRoot.OwnerDocument;
                var navigator = CreateNavigatorHost();
                var location = new FenJsLocationHost(baseUri ?? TryCreateUri(document?.URL));
                var history = new FenJsHistoryHost(location);

                _hostHooks.Bind(
                    this,
                    document,
                    navigator,
                    location,
                    baseUri ?? TryCreateUri(document?.URL));

                if (document == null)
                {
                    return null;
                }

                _interpreter.RegisterGlobalHostObject(
                    "document",
                    RegisterHostObject(document, HostObjectKind.DomDocument));
                _interpreter.RegisterGlobalHostObject(
                    "navigator",
                    RegisterHostObject(navigator, HostObjectKind.Other));
                _interpreter.RegisterGlobalHostObject(
                    "location",
                    RegisterHostObject(location, HostObjectKind.Other));
                _interpreter.RegisterGlobalHostObject(
                    "history",
                    RegisterHostObject(history, HostObjectKind.Other));

                SeedFenJsDocumentAndNavigatorProperties(document, navigator);
                RegisterFenJsStorageGlobals(baseUri, document);
            }

            return null;
        });
    }

    private IDisposable ActivateSubdocumentWindowContext(Node domRoot, Uri baseUri)
    {
        var document = domRoot as Document ?? domRoot?.OwnerDocument;
        var frameElement = TryGetFrameElementForDocument(document);
        if (frameElement == null)
        {
            return null;
        }

        var frameWindow = GetOrCreateIFrameContentWindow(frameElement, document, baseUri);
        var frameWindowListeners = GetIFrameWindowListeners(frameElement);
        var previousListeners = _activeWindowEventListeners;
        var previousTarget = _activeWindowEventTarget;
        var previousWindow = ReadGlobalValueOrUndefined("window");
        var previousSelf = ReadGlobalValueOrUndefined("self");
        var previousFrames = ReadGlobalValueOrUndefined("frames");
        var previousParent = ReadGlobalValueOrUndefined("parent");
        var previousTop = ReadGlobalValueOrUndefined("top");
        var previousDocument = ReadGlobalValueOrUndefined("document");
        var previousLocation = ReadGlobalValueOrUndefined("location");
        var previousBaseUri = _currentBaseUri;
        var previousParentBaseUri = _activeParentBaseUri;
        var parentWindow = _fenJsGlobalThis.Tag == JsValueTag.Undefined
            ? previousWindow
            : _fenJsGlobalThis;
        var frameBaseUri = baseUri ??
            TryCreateUri(document?.BaseURI) ??
            TryCreateUri(document?.DocumentURI) ??
            TryCreateUri(document?.URL);

        if (frameBaseUri != null)
        {
            _currentBaseUri = frameBaseUri;
        }
        _activeParentBaseUri = previousBaseUri;
        _activeWindowEventListeners = frameWindowListeners;
        _activeWindowEventTarget = frameWindow;
        _interpreter.RegisterGlobalValue("window", frameWindow);
        _interpreter.RegisterGlobalValue("self", frameWindow);
        _interpreter.RegisterGlobalValue("frames", frameWindow);
        _interpreter.RegisterGlobalValue("parent", parentWindow);
        _interpreter.RegisterGlobalValue("top", parentWindow);
        var frameDocument = ReadJsProperty(frameWindow, "document");
        var frameLocation = ReadJsProperty(frameWindow, "location");
        if (frameDocument.Tag != JsValueTag.Undefined)
        {
            _interpreter.RegisterGlobalValue("document", frameDocument);
        }
        if (frameLocation.Tag != JsValueTag.Undefined)
        {
            _interpreter.RegisterGlobalValue("location", frameLocation);
        }

        return new WindowContextScope(
            this,
            previousListeners,
            previousTarget,
            previousWindow,
            previousSelf,
            previousFrames,
            previousParent,
            previousTop,
            previousDocument,
            previousLocation,
            previousBaseUri,
            previousParentBaseUri);
    }

    private IDisposable ActivateWindowCallbackContext(JsValue windowTarget, List<BrowserEventListener> listeners)
    {
        if (windowTarget.Tag != JsValueTag.Object)
        {
            return null;
        }

        var previousListeners = _activeWindowEventListeners;
        var previousTarget = _activeWindowEventTarget;
        var previousWindow = ReadGlobalValueOrUndefined("window");
        var previousSelf = ReadGlobalValueOrUndefined("self");
        var previousFrames = ReadGlobalValueOrUndefined("frames");
        var previousParent = ReadGlobalValueOrUndefined("parent");
        var previousTop = ReadGlobalValueOrUndefined("top");
        var previousDocument = ReadGlobalValueOrUndefined("document");
        var previousLocation = ReadGlobalValueOrUndefined("location");
        var previousBaseUri = _currentBaseUri;
        var previousParentBaseUri = _activeParentBaseUri;
        var frameDocument = ReadJsProperty(windowTarget, "document");
        var frameLocation = ReadJsProperty(windowTarget, "location");
        var parentWindow = ReadJsProperty(windowTarget, "parent");
        if (parentWindow.Tag == JsValueTag.Undefined)
        {
            parentWindow = _fenJsGlobalThis;
        }
        var frameHref = ReadJsProperty(frameLocation, "href");
        var frameBaseUri = TryCreateUri(CoerceToHostString(frameHref));

        if (frameBaseUri != null)
        {
            _currentBaseUri = frameBaseUri;
        }
        _activeParentBaseUri = previousBaseUri;
        _activeWindowEventListeners = listeners;
        _activeWindowEventTarget = windowTarget;
        _interpreter.RegisterGlobalValue("window", windowTarget);
        _interpreter.RegisterGlobalValue("self", windowTarget);
        _interpreter.RegisterGlobalValue("frames", windowTarget);
        _interpreter.RegisterGlobalValue("parent", parentWindow);
        _interpreter.RegisterGlobalValue("top", parentWindow);
        if (frameDocument.Tag != JsValueTag.Undefined)
        {
            _interpreter.RegisterGlobalValue("document", frameDocument);
        }
        if (frameLocation.Tag != JsValueTag.Undefined)
        {
            _interpreter.RegisterGlobalValue("location", frameLocation);
        }

        return new WindowContextScope(
            this,
            previousListeners,
            previousTarget,
            previousWindow,
            previousSelf,
            previousFrames,
            previousParent,
            previousTop,
            previousDocument,
            previousLocation,
            previousBaseUri,
            previousParentBaseUri);
    }

    private JsValue ReadGlobalValueOrUndefined(string name)
    {
        return _interpreter != null
            ? _interpreter.ReadGlobalValueOrUndefined(name)
            : JsValue.Undefined;
    }

    private void RestoreWindowContext(
        List<BrowserEventListener> previousListeners,
        JsValue previousTarget,
        JsValue previousWindow,
        JsValue previousSelf,
        JsValue previousFrames,
        JsValue previousParent,
        JsValue previousTop,
        JsValue previousDocument,
        JsValue previousLocation,
        Uri previousBaseUri,
        Uri previousParentBaseUri)
    {
        _activeWindowEventListeners = previousListeners;
        _activeWindowEventTarget = previousTarget;
        _currentBaseUri = previousBaseUri;
        _activeParentBaseUri = previousParentBaseUri;
        RestoreGlobalValue("window", previousWindow);
        RestoreGlobalValue("self", previousSelf);
        RestoreGlobalValue("frames", previousFrames);
        RestoreGlobalValue("parent", previousParent);
        RestoreGlobalValue("top", previousTop);
        RestoreGlobalValue("document", previousDocument);
        RestoreGlobalValue("location", previousLocation);
    }

    private void RestoreGlobalValue(string name, JsValue value)
    {
        if (value.Tag != JsValueTag.Undefined)
        {
            _interpreter.RegisterGlobalValue(name, value);
        }
    }

    private List<BrowserEventListener> GetActiveWindowEventListeners()
    {
        return _activeWindowEventListeners ?? _windowEventListeners;
    }

    private FenJsWindowCallbackContext CaptureActiveWindowCallbackContext()
    {
        var target = GetActiveWindowEventTarget();
        if (target.Tag != JsValueTag.Object)
        {
            return null;
        }

        return new FenJsWindowCallbackContext(target, GetActiveWindowEventListeners());
    }

    private JsValue GetActiveWindowEventTarget()
    {
        if (_activeWindowEventTarget.Tag != JsValueTag.Undefined)
        {
            return _activeWindowEventTarget;
        }

        return _fenJsGlobalThis.Tag == JsValueTag.Undefined
            ? EvaluateWithFenJsRaw("window")
            : _fenJsGlobalThis;
    }

    private JsValue CreateTopWindowPostMessageFunction()
    {
        return _interpreter.AllocateNativeFunction(
            "postMessage",
            (_, args) =>
            {
                var data = args.Count > 0 ? args[0] : JsValue.Undefined;
                var targetOrigin = args.Count > 1 ? CoerceToHostString(args[1]) : "*";
                var sourceWindow = GetActiveWindowEventTarget();
                QueueWindowMessage(
                    _fenJsGlobalThis,
                    _windowEventListeners,
                    data,
                    sourceWindow,
                    targetOrigin,
                    GetTopWindowDeliveryOrigin());
                return JsValue.Undefined;
            },
            length: 1);
    }

    private string GetTopWindowDeliveryOrigin()
    {
        if (_activeParentBaseUri != null)
        {
            return NormalizePostMessageOrigin(_activeParentBaseUri.AbsoluteUri);
        }

        return null;
    }

    private void InstallTopWindowPostMessageBridge(JsValue globalThisValue)
    {
        var topWindowPostMessage = CreateTopWindowPostMessageFunction();
        _interpreter.RegisterGlobalValue("postMessage", topWindowPostMessage);
        if (globalThisValue.Tag == JsValueTag.Object)
        {
            _interpreter.SetObjectProperty(globalThisValue, "postMessage", topWindowPostMessage);
        }
    }

    private List<BrowserEventListener> GetIFrameWindowListeners(Element iframe)
    {
        return _iframeWindowEventListeners.GetOrCreateValue(iframe);
    }

    private static Element TryGetFrameElementForDocument(Document document)
    {
        return document?.ParentNode is Element element && IsIFrameElement(element)
            ? element
            : null;
    }

    private sealed class WindowContextScope : IDisposable
    {
        private FenJsBrowserScriptEngine _owner;
        private readonly List<BrowserEventListener> _previousListeners;
        private readonly JsValue _previousTarget;
        private readonly JsValue _previousWindow;
        private readonly JsValue _previousSelf;
        private readonly JsValue _previousFrames;
        private readonly JsValue _previousParent;
        private readonly JsValue _previousTop;
        private readonly JsValue _previousDocument;
        private readonly JsValue _previousLocation;
        private readonly Uri _previousBaseUri;
        private readonly Uri _previousParentBaseUri;

        public WindowContextScope(
            FenJsBrowserScriptEngine owner,
            List<BrowserEventListener> previousListeners,
            JsValue previousTarget,
            JsValue previousWindow,
            JsValue previousSelf,
            JsValue previousFrames,
            JsValue previousParent,
            JsValue previousTop,
            JsValue previousDocument,
            JsValue previousLocation,
            Uri previousBaseUri,
            Uri previousParentBaseUri)
        {
            _owner = owner;
            _previousListeners = previousListeners;
            _previousTarget = previousTarget;
            _previousWindow = previousWindow;
            _previousSelf = previousSelf;
            _previousFrames = previousFrames;
            _previousParent = previousParent;
            _previousTop = previousTop;
            _previousDocument = previousDocument;
            _previousLocation = previousLocation;
            _previousBaseUri = previousBaseUri;
            _previousParentBaseUri = previousParentBaseUri;
        }

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.RestoreWindowContext(
                _previousListeners,
                _previousTarget,
                _previousWindow,
                _previousSelf,
                _previousFrames,
                _previousParent,
                _previousTop,
                _previousDocument,
                _previousLocation,
                _previousBaseUri,
                _previousParentBaseUri);
        }
    }

    private sealed class FenJsWindowCallbackContext
    {
        public FenJsWindowCallbackContext(JsValue windowTarget, List<BrowserEventListener> windowListeners)
        {
            WindowTarget = windowTarget;
            WindowListeners = windowListeners;
        }

        public JsValue WindowTarget { get; }
        public List<BrowserEventListener> WindowListeners { get; }
    }

    private void InstallFenJsDomGlobals(Node domRoot, Uri baseUri)
    {
        var document = domRoot as Document ?? domRoot.OwnerDocument;
        var navigator = CreateNavigatorHost();
        var location = new FenJsLocationHost(baseUri ?? TryCreateUri(document?.URL));
        var history = new FenJsHistoryHost(location);
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
        InstallFenJsNativeRangeConstructor(document);
        // Register a native logging hook that console.log/warn/error forward to.
        // Uses FenLogger so output appears in engine logs and any attached debug console.
        _interpreter.RegisterGlobalValue(
            "__fenLog",
            _interpreter.AllocateNativeFunction(
                "__fenLog",
                (_, args) =>
                {
                    var level = args.Count > 0 ? args[0].AsString() : "log";
                    var msg = args.Count > 1 ? args[1].AsString() : "";
                    var category = LogCategory.JavaScript;
                    // Tag WhatsApp diagnostic messages so they surface even when
                    // JavaScript logging is filtered out.
                    if (msg.StartsWith("[IDB]") || msg.StartsWith("[WhatsApp]"))
                        category |= LogCategory.WhatsApp;
                    switch (level)
                    {
                        case "error":
                            FenLogger.Error($"[JS:{level}] {msg}", category);
                            break;
                        case "warn":
                            FenLogger.Warn($"[JS:{level}] {msg}", category);
                            break;
                        default:
                            FenLogger.Info($"[JS:{level}] {msg}", category);
                            break;
                    }
                    return JsValue.Undefined;
                },
                length: 2));

        // ── IndexedDB persistence bridge ──
        // Register C# native functions that the JS IDB implementation calls for
        // persistent storage. The JS side falls back to in-memory when these are
        // unavailable, so registration failures are non-fatal.
        var idbBackend = IndexedDbBackend;
        if (idbBackend != null)
        {
            var idbOrigin = _currentBaseUri != null
                ? $"{_currentBaseUri.Scheme}://{_currentBaseUri.Host}"
                : "https://web.whatsapp.com";

            _interpreter.RegisterGlobalValue(
                "__fenIdbLoad",
                _interpreter.AllocateNativeFunction(
                    "__fenIdbLoad",
                    (_, args) =>
                    {
                        var dbName = args.Count > 0 ? args[0].AsString() : "default";
                        try
                        {
                            var info = idbBackend.GetDatabaseInfo(idbOrigin, dbName)
                                .GetAwaiter().GetResult();
                            if (info == null || info.ObjectStoreNames.Count == 0)
                                return JsValue.FromString("");
                            // Rebuild a JSON snapshot of all stores
                            var snapshot = new Dictionary<string, object>();
                            foreach (var storeName in info.ObjectStoreNames)
                            {
                                var data = idbBackend.GetAll(idbOrigin, dbName, storeName)
                                    .GetAwaiter().GetResult();
                                var dict = new Dictionary<string, object>();
                                int idx = 0;
                                foreach (var val in data)
                                {
                                    dict[idx.ToString()] = val;
                                    idx++;
                                }
                                snapshot[dbName + "\0" + storeName] = new Dictionary<string, object>
                                {
                                    ["_data"] = dict,
                                    ["_indexes"] = new Dictionary<string, object>(),
                                    ["_keyPath"] = (object)null
                                };
                            }
                            var json = JsonSerializer.Serialize(snapshot);
                            FenLogger.Info($"[IDB] Loaded {info.ObjectStoreNames.Count} stores from '{dbName}'", LogCategory.WhatsApp);
                            return JsValue.FromString(json);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn($"[IDB] Load failed for '{dbName}': {ex.Message}", LogCategory.WhatsApp);
                            return JsValue.FromString("");
                        }
                    },
                    length: 1));

            _interpreter.RegisterGlobalValue(
                "__fenIdbPut",
                _interpreter.AllocateNativeFunction(
                    "__fenIdbPut",
                    (_, args) =>
                    {
                        var dbName = args.Count > 0 ? args[0].AsString() : "default";
                        var storeName = args.Count > 1 ? args[1].AsString() : "store";
                        var key = args.Count > 2 ? args[2].AsString() : "0";
                        var jsonValue = args.Count > 3 ? args[3].AsString() : "{}";
                        try
                        {
                            idbBackend.Put(idbOrigin, dbName, storeName, key, jsonValue)
                                .GetAwaiter().GetResult();
                            return JsValue.FromBoolean(true);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn($"[IDB] Put failed for '{dbName}/{storeName}': {ex.Message}", LogCategory.WhatsApp);
                            return JsValue.FromBoolean(false);
                        }
                    },
                    length: 4));

            _interpreter.RegisterGlobalValue(
                "__fenIdbDelete",
                _interpreter.AllocateNativeFunction(
                    "__fenIdbDelete",
                    (_, args) =>
                    {
                        var dbName = args.Count > 0 ? args[0].AsString() : "default";
                        var storeName = args.Count > 1 ? args[1].AsString() : "store";
                        var key = args.Count > 2 ? args[2].AsString() : "";
                        try
                        {
                            idbBackend.Delete(idbOrigin, dbName, storeName, key)
                                .GetAwaiter().GetResult();
                            return JsValue.FromBoolean(true);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn($"[IDB] Delete failed for '{dbName}/{storeName}': {ex.Message}", LogCategory.WhatsApp);
                            return JsValue.FromBoolean(false);
                        }
                    },
                    length: 3));

            _interpreter.RegisterGlobalValue(
                "__fenIdbDeleteDatabase",
                _interpreter.AllocateNativeFunction(
                    "__fenIdbDeleteDatabase",
                    (_, args) =>
                    {
                        var dbName = args.Count > 0 ? args[0].AsString() : "default";
                        try
                        {
                            idbBackend.DeleteDatabase(idbOrigin, dbName)
                                .GetAwaiter().GetResult();
                            return JsValue.FromBoolean(true);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn($"[IDB] DeleteDatabase failed for '{dbName}': {ex.Message}", LogCategory.WhatsApp);
                            return JsValue.FromBoolean(false);
                        }
                    },
                    length: 1));
        }

        // Stub navigator.sendBeacon so sites (Google, etc.) that call it
        // directly don't crash. Sites may also set it to their own function;
        // the TrySetHostProperty case for BrowserSurfaceProfile stores those.
        EvaluateWithFenJsRaw(
            // ── window.console ── Must come before alert/confirm/prompt stubs
            // since those call console.log.  Forwards to native __fenLog.
            "globalThis.console = {" +
            "  log:   function() { __fenLog('log',   Array.prototype.slice.call(arguments).join(' ')); }," +
            "  warn:  function() { __fenLog('warn',  Array.prototype.slice.call(arguments).join(' ')); }," +
            "  error: function() { __fenLog('error', Array.prototype.slice.call(arguments).join(' ')); }," +
            "  info:  function() { __fenLog('info',  Array.prototype.slice.call(arguments).join(' ')); }," +
            "  debug: function() { __fenLog('debug', Array.prototype.slice.call(arguments).join(' ')); }," +
            "  trace: function() { __fenLog('trace', Array.prototype.slice.call(arguments).join(' ')); }," +
            "  clear: function() {}," +
            "  dir:   function() { __fenLog('dir',   Array.prototype.slice.call(arguments).join(' ')); }" +
            "};" +
            // ── navigator.sendBeacon ──
            "navigator.sendBeacon = function(url, data) { return true; };" +
            // Stub navigator.plugins and navigator.mimeTypes — real browsers
            // always have these (even if empty). Google's bot detection checks
            // their presence and shape.
            "navigator.plugins = { length: 0, item: function() { return null; }, namedItem: function() { return null; }, refresh: function() {} };" +
            "navigator.mimeTypes = { length: 0, item: function() { return null; }, namedItem: function() { return null; } };" +
            // ── navigator.cookieDeprecationLabel ── https://wicg.github.io/cookie-deprecation-label/
            // Google reCAPTCHA enterprise.js checks this.  Must be an object with
            // getValue() that returns a Promise<string>.
            "navigator.cookieDeprecationLabel = { getValue: function() { return Promise.resolve('no-signal'); } };" +
            // Stub window.chrome — Chromium-based browsers always expose this.
            // Google's JS challenge checks for window.chrome.loadTimes() and
            // window.chrome.csi() as browser-authenticity signals.
            "globalThis.chrome = {" +
            "  runtime: { connect: function() {}, sendMessage: function() {}, onConnect: { addListener: function() {} }, onMessage: { addListener: function() {} } }," +
            "  loadTimes: function() { return { requestTime: Date.now() / 1000, startLoadTime: Date.now() / 1000, commitLoadTime: Date.now() / 1000, finishDocumentLoadTime: Date.now() / 1000, finishLoadTime: Date.now() / 1000, firstPaintTime: Date.now() / 1000, firstPaintAfterLoadTime: Date.now() / 1000, navigationType: 'Other', wasFetchedViaSpdy: false, wasNpnNegotiated: false, npnNegotiatedProtocol: 'unknown', connectionInfo: 'http/1.1', wasAlternateProtocolAvailable: false }; }," +
            "  csi: function() { return { startE: 0, onloadT: 0, pageT: 0, tran: 0 }; }," +
            "  app: {}" +
            "};" +
            "globalThis.matchMedia = function(query) {" +
            "  var media = String(query);" +
            "  var w = globalThis.innerWidth || 1024;" +
            "  var h = globalThis.innerHeight || 768;" +
            "  var darkMode = true; /* default to dark for SPAs like WhatsApp */" +
            "  var reducedMotion = false;" +
            "  var matches = false;" +
            "  var q = media.toLowerCase().replace(/\\s+/g, ' ').trim();" +
            "  if (q.indexOf('prefers-color-scheme: dark') >= 0) matches = darkMode;" +
            "  else if (q.indexOf('prefers-color-scheme: light') >= 0) matches = !darkMode;" +
            "  else if (q.indexOf('prefers-reduced-motion: reduce') >= 0) matches = reducedMotion;" +
            "  else if (q.indexOf('prefers-reduced-motion: no-preference') >= 0) matches = !reducedMotion;" +
            "  else {" +
            "    var minW = q.match(/\\(min-width:\\s*(\\d+(?:\\.\\d+)?)(px|em|rem)\\)/);" +
            "    var maxW = q.match(/\\(max-width:\\s*(\\d+(?:\\.\\d+)?)(px|em|rem)\\)/);" +
            "    var minH = q.match(/\\(min-height:\\s*(\\d+(?:\\.\\d+)?)(px|em|rem)\\)/);" +
            "    var maxH = q.match(/\\(max-height:\\s*(\\d+(?:\\.\\d+)?)(px|em|rem)\\)/);" +
            "    matches = true;" +
            "    if (minW) matches = matches && w >= parseFloat(minW[1]);" +
            "    if (maxW) matches = matches && w <= parseFloat(maxW[1]);" +
            "    if (minH) matches = matches && h >= parseFloat(minH[1]);" +
            "    if (maxH) matches = matches && h <= parseFloat(maxH[1]);" +
            "  }" +
            "  return {" +
            "    media: media," +
            "    matches: matches," +
            "    onchange: null," +
            "    addListener: function() {}," +
            "    removeListener: function() {}," +
            "    addEventListener: function() {}," +
            "    removeEventListener: function() {}," +
            "    dispatchEvent: function() { return true; }" +
            "  };" +
            "};" +
            // Stub IAB consent/privacy framework APIs (CCPA, TCF).
            // Sites expect these globals to exist and call them with commands
            // like __uspapi('getUSPData', 1, callback). Without these stubs,
            // code that accesses parent.__uspapiLocator or __tcfapiLocator
            // on cross-origin frames throws TypeError → browser crash.
            "globalThis.__uspapiLocator = function() {};" +
            "globalThis.__uspapi = function(cmd, version, callback) { if (callback) callback({ uspString: '1---' }, true); };" +
            "globalThis.__tcfapiLocator = function() {};" +
            "globalThis.__tcfapi = function(cmd, version, callback) { if (callback) callback({ tcString: '', gdprApplies: false }, true); };" +
            "globalThis.__gppLocator = function() {};");
        SeedFenJsDocumentAndNavigatorProperties(document, navigator);
        _interpreter.RegisterGlobalValue("NodeFilter", CreateNodeFilterConstantsObject());
        _interpreter.RegisterGlobalValue("CSS", CreateCssGlobalObject());
        _interpreter.RegisterGlobalHostObject("location", RegisterHostObject(location, HostObjectKind.Other));
        _interpreter.RegisterGlobalHostObject("history", RegisterHostObject(history, HostObjectKind.Other));
        RegisterFenJsStorageGlobals(baseUri, document);
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
                    AddBrowserEventListener(GetActiveWindowEventListeners(), args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.RegisterGlobalValue(
            "removeEventListener",
            _interpreter.AllocateNativeFunction(
                "removeEventListener",
                (_, args) =>
                {
                    RemoveBrowserEventListener(GetActiveWindowEventListeners(), args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.RegisterGlobalValue(
            "dispatchEvent",
            _interpreter.AllocateNativeFunction(
                "dispatchEvent",
                (_, args) =>
                {
                    var eventValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                    return JsValue.FromBoolean(DispatchWindowHostEvent(eventValue));
                },
                length: 1));
        _interpreter.RegisterGlobalValue(
            "__fenInvokeHostMethod",
            _interpreter.AllocateNativeFunction(
                "__fenInvokeHostMethod",
                (_, args) =>
                {
                    var receiver = args.Count > 0 ? args[0] : JsValue.Undefined;
                    var methodName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                    var methodArgs = ExtractJsArgumentList(args.Count > 2 ? args[2] : JsValue.Undefined);
                    return InvokeFenJsHostMethod(receiver, methodName, methodArgs);
                },
                length: 3));
        _interpreter.RegisterGlobalValue(
            "__fenCreateCustomElementConstructionElement",
            _interpreter.AllocateNativeFunction(
                "__fenCreateCustomElementConstructionElement",
                (_, args) =>
                {
                    var localName = args.Count > 0 ? CoerceToHostString(args[0]) : "div";
                    if (string.IsNullOrWhiteSpace(localName))
                    {
                        localName = "div";
                    }

                    var currentDocument = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
                    if (currentDocument == null)
                    {
                        return JsValue.Undefined;
                    }

                    try
                    {
                        return ToHostNodeOrNull(currentDocument.CreateElement(localName));
                    }
                    catch (DomException ex)
                    {
                        ThrowDomException(ex.Name, ex.Message);
                        return JsValue.Undefined;
                    }
                    catch (ArgumentException ex)
                    {
                        ThrowDomException("InvalidCharacterError", ex.Message);
                        return JsValue.Undefined;
                    }
                },
                length: 1));
        // ── window.alert / confirm / prompt — fire-and-forget dialog display
        // with immediate return of safe defaults.
        //
        // Synchronously blocking the JS worker for a modal dialog would require
        // the main thread's game loop to process the dialog creation action
        // (posted via WindowManager.RunOnMainThread).  If the engine loop is
        // parked (waiting for JS to finish on the large-stack worker), the
        // game loop's ProcessMainThreadQueue may not drain in time, causing the
        // browser to appear hung.
        //
        // Instead we post the dialog to the UI thread asynchronously and return
        // a safe default immediately.  The dialog still appears visually; the
        // return value is the optimistic / pre-filled answer.  A proper nested-
        // event-loop implementation (where the interpreter yields, the engine
        // loop shows the dialog, and execution resumes on dismiss) is deferred.
        _interpreter.RegisterGlobalValue(
            "alert",
            _interpreter.AllocateNativeFunction(
                "alert",
                (_, args) =>
                {
                    var msg = args.Count > 0 ? args[0].ToString() : "";
                    PostDialogAsync("alert", msg, "");
                    return JsValue.Undefined;
                },
                length: 1));
        _interpreter.RegisterGlobalValue(
            "confirm",
            _interpreter.AllocateNativeFunction(
                "confirm",
                (_, args) =>
                {
                    var msg = args.Count > 0 ? args[0].ToString() : "";
                    PostDialogAsync("confirm", msg, "");
                    return JsValue.FromBoolean(true);
                },
                length: 1));
        _interpreter.RegisterGlobalValue(
            "prompt",
            _interpreter.AllocateNativeFunction(
                "prompt",
                (_, args) =>
                {
                    var msg = args.Count > 0 ? args[0].ToString() : "";
                    var def = args.Count > 1 && args[1].Tag != FenBrowser.Js.Runtime.JsValueTag.Undefined ? args[1].ToString() : "";
                    PostDialogAsync("prompt", msg, def);
                    return string.IsNullOrEmpty(def) ? JsValue.Null : JsValue.FromString(def);
                },
                length: 2));

        // ── window.open — calls into Host to create a new tab and returns a
        // host object representing the popup window with document.write/close etc.
        _interpreter.RegisterGlobalValue(
            "open",
            _interpreter.AllocateNativeFunction(
                "open",
                (_, args) =>
                {
                    var url = args.Count > 0 ? args[0].ToString() : "";
                    var name = args.Count > 1 ? args[1].ToString() : "";
                    var features = args.Count > 2 ? args[2].ToString() : "";

                    var bridge = JsDialogBridge.OpenWindow;
                    if (bridge == null)
                    {
                        FenLogger.Warn("[open] JsDialogBridge not installed — returning null", LogCategory.JavaScript);
                        return JsValue.Null;
                    }

                    var handle = bridge(url, name, features);
                    if (handle == null) return JsValue.Null;

                    return CreatePopupWindowHostObject(handle, name, url);
                },
                length: 3));

        InstallFenJsPerformance();
        InstallFenJsTimers();
        InstallFenJsBrowserConstructors();
        InstallFenJsNativeBrowserConstructors();
        _fenJsDomConstructorsInstalled = true;
        TryAttachFenJsPrototype(EvaluateWithFenJsRaw("document"), document, HostObjectKind.DomDocument);
        InstallFenJsMutationObserver();
        InstallFenJsEventTarget();
        InstallFenJsBrowserUiApis(baseUri);
        InstallFenJsRemainingWebApis();
        InstallTopWindowPostMessageBridge(globalThisValue);
    }

    private void SeedFenJsDocumentAndNavigatorProperties(Document document, BrowserSurfaceProfile navigator)
    {
        if (document == null || navigator == null)
        {
            return;
        }

        // Stub document.fonts (FontFaceSet API) so sites that use the CSS Font
        // Loading API (Google loads 'Google Sans' this way) don't crash.
        SetStoredHostProperty(
            document,
            "fonts",
            _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["load"] = _interpreter.AllocateNativeFunction(
                    "load",
                    (_, _2) => EvaluateWithFenJsRaw("Promise.resolve([])"),
                    length: 1),
                ["ready"] = EvaluateWithFenJsRaw("Promise.resolve()"),
                ["status"] = JsValue.FromString("loaded"),
                ["check"] = _interpreter.AllocateNativeFunction(
                    "check",
                    (_, _2) => JsValue.FromBoolean(true),
                    length: 1),
                ["addEventListener"] = _interpreter.AllocateNativeFunction(
                    "addEventListener",
                    (_, _2) => JsValue.Undefined,
                    length: 2),
                ["has"] = _interpreter.AllocateNativeFunction(
                    "has",
                    (_, _2) => JsValue.FromBoolean(false),
                    length: 1),
            }));
        SetStoredHostProperty(navigator, "userAgentData", CreateNavigatorUserAgentDataObject(navigator.UserAgentData));
        SetStoredHostProperty(navigator, "userAgent", JsValue.FromString(navigator.UserAgent ?? string.Empty));
        SetStoredHostProperty(navigator, "connection", CreateNavigatorConnectionObject());
        SetStoredHostProperty(
            navigator,
            "sendBeacon",
            _interpreter.AllocateNativeFunction(
                "sendBeacon",
                (_, _) => JsValue.FromBoolean(true),
                length: 2));
        SetStoredHostProperty(
            navigator,
            "plugins",
            _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["length"] = JsValue.FromInt32(0),
                ["item"] = _interpreter.AllocateNativeFunction("item", (_, _) => JsValue.Null, length: 1),
                ["namedItem"] = _interpreter.AllocateNativeFunction("namedItem", (_, _) => JsValue.Null, length: 1),
                ["refresh"] = _interpreter.AllocateNativeFunction("refresh", (_, _) => JsValue.Undefined, length: 0)
            }));
        SetStoredHostProperty(
            navigator,
            "mimeTypes",
            _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["length"] = JsValue.FromInt32(0),
                ["item"] = _interpreter.AllocateNativeFunction("item", (_, _) => JsValue.Null, length: 1),
                ["namedItem"] = _interpreter.AllocateNativeFunction("namedItem", (_, _) => JsValue.Null, length: 1)
            }));
        SetStoredHostProperty(
            navigator,
            "cookieDeprecationLabel",
            _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["getValue"] = _interpreter.AllocateNativeFunction(
                    "getValue",
                    (_, _) => CreateResolvedPromise(JsValue.FromString("no-signal")),
                    length: 0)
            }));

        // navigator.serviceWorker — C# host property so it's available before JS runs
        // Returns resolved promises so apps that await registration don't hang.
        SetStoredHostProperty(navigator, "serviceWorker", CreateDefaultServiceWorkerStub());

        // navigator.storage — C# host property
        SetStoredHostProperty(navigator, "storage", CreateDefaultStorageStub());
    }

    public JsValue CreateDefaultServiceWorkerStub()
    {
        var swRegistration = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["installing"] = JsValue.Null,
            ["waiting"] = JsValue.Null,
            ["active"] = JsValue.Null,
            ["scope"] = JsValue.FromString("/"),
            ["update"] = _interpreter.AllocateNativeFunction(
                "update", (_, _) => CreateResolvedPromise(JsValue.Undefined), length: 0),
            ["unregister"] = _interpreter.AllocateNativeFunction(
                "unregister", (_, _) => CreateResolvedPromise(JsValue.FromBoolean(true)), length: 0)
        });
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["register"] = _interpreter.AllocateNativeFunction(
                "register",
                (_, _) => CreateResolvedPromise(swRegistration),
                length: 2),
            ["getRegistration"] = _interpreter.AllocateNativeFunction(
                "getRegistration",
                (_, _) => CreateResolvedPromise(JsValue.Undefined),
                length: 0),
            ["getRegistrations"] = _interpreter.AllocateNativeFunction(
                "getRegistrations",
                (_, _) => CreateResolvedPromise(_interpreter.AllocateArray(Array.Empty<JsValue>())),
                length: 0),
            ["ready"] = CreateResolvedPromise(swRegistration),
            ["controller"] = JsValue.Null,
            ["oncontrollerchange"] = JsValue.Null
        });
    }

    public JsValue CreateDefaultStorageStub()
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["estimate"] = _interpreter.AllocateNativeFunction(
                "estimate",
                (_, _) => CreateResolvedPromise(_interpreter.AllocateObject(
                    new Dictionary<string, JsValue>
                    {
                        ["quota"] = JsValue.FromInt32(0),
                        ["usage"] = JsValue.FromInt32(0)
                    })),
                length: 0),
            ["persist"] = _interpreter.AllocateNativeFunction(
                "persist",
                (_, _) => CreateResolvedPromise(JsValue.FromBoolean(false)),
                length: 0),
            ["persisted"] = _interpreter.AllocateNativeFunction(
                "persisted",
                (_, _) => CreateResolvedPromise(JsValue.FromBoolean(false)),
                length: 0)
        });
    }

    private void InstallFenJsBrowserUiApis(Uri baseUri)
    {
        var secureContextLiteral = IsPotentiallyTrustworthyOrigin(baseUri) ? "true" : "false";
        EvaluateWithFenJsRaw(
            """
            (function () {
                var notificationPermission = 'granted';

                function scheduleNotificationEvent(notification, eventName) {
                    setTimeout(function () {
                        var handler = notification && notification['on' + eventName];
                        if (typeof handler === 'function') {
                            try {
                                handler.call(notification, new Event(eventName));
                            } catch (_) {
                            }
                        }
                    }, 0);
                }

                function Notification(title, options) {
                    if (!(this instanceof Notification)) {
                        return new Notification(title, options);
                    }

                    if (notificationPermission === 'denied') {
                        throw new TypeError('Notification permission denied');
                    }

                    options = options || {};
                    this.title = String(title || '');
                    this.body = options.body ? String(options.body) : '';
                    this.tag = options.tag ? String(options.tag) : '';
                    this.closed = false;
                    this.onclick = null;
                    this.onshow = null;
                    this.onerror = null;
                    this.onclose = null;
                    this.close = function () {
                        if (this.closed) return;
                        this.closed = true;
                        scheduleNotificationEvent(this, 'close');
                    };
                    scheduleNotificationEvent(this, 'show');
                }

                Object.defineProperty(Notification, 'permission', {
                    get: function () { return notificationPermission; },
                    configurable: true
                });
                Notification.requestPermission = function (callback) {
                    notificationPermission = 'granted';
                    if (typeof callback === 'function') {
                        try { callback(notificationPermission); } catch (_) {}
                    }
                    return Promise.resolve(notificationPermission);
                };

                globalThis.Notification = Notification;

                // ── WebSocket ── https://websockets.spec.whatwg.org/
                if (typeof globalThis.WebSocket === 'undefined') {
                    globalThis.WebSocket = function WebSocket(url, protocols) {
                        if (!(this instanceof WebSocket)) {
                            throw new TypeError("Failed to construct 'WebSocket': Please use the 'new' operator.");
                        }
                        this.url = String(url || '');
                        this.readyState = WebSocket.CONNECTING;
                        this.protocol = '';
                        this.extensions = '';
                        this.bufferedAmount = 0;
                        this.binaryType = 'blob';
                        this.onopen = null;
                        this.onmessage = null;
                        this.onerror = null;
                        this.onclose = null;
                        this._fenId = -1;
                        this._fenPollTimer = null;
                        this._fenListeners = {};
                        this._fenBufferedEvents = [];

                        var protoArray = protocols;
                        if (protocols && !Array.isArray(protocols)) {
                            protoArray = [String(protocols)];
                        }
                        var protoJson = JSON.stringify(protoArray || []);

                        var result = __fenWebSocketConnect(this.url, protoJson);
                        if (typeof result === 'number') {
                            this._fenId = result;
                            this._startPolling();
                        } else {
                            // Connection failed synchronously
                            this.readyState = WebSocket.CLOSED;
                            var self = this;
                            // Dispatch error asynchronously
                            globalThis.setTimeout(function () {
                                if (self.onerror) self.onerror(new Event('error'));
                                self._dispatchEvent('error');
                            }, 0);
                        }
                    };
                    WebSocket.CONNECTING = 0;
                    WebSocket.OPEN = 1;
                    WebSocket.CLOSING = 2;
                    WebSocket.CLOSED = 3;

                    WebSocket.prototype._startPolling = function () {
                        var self = this;
                        if (self._fenPollTimer !== null) return;
                        self._fenPollTimer = globalThis.setInterval(function () {
                            if (self._fenId < 0) {
                                if (self._fenPollTimer !== null) {
                                    globalThis.clearInterval(self._fenPollTimer);
                                    self._fenPollTimer = null;
                                }
                                return;
                            }
                            var jsonResult = __fenWebSocketPoll(self._fenId);
                            if (jsonResult === 'null' || !jsonResult) return;
                            try {
                                var data = JSON.parse(jsonResult);
                                // Process events
                                if (data.events && data.events.length > 0) {
                                    for (var i = 0; i < data.events.length; i++) {
                                        var evt = data.events[i];
                                        if (evt.type === 'open') {
                                            self.readyState = WebSocket.OPEN;
                                            if (self.onopen) self.onopen(new Event('open'));
                                            self._dispatchEvent('open');
                                        } else if (evt.type === 'error') {
                                            self.readyState = WebSocket.CLOSED;
                                            if (self.onerror) self.onerror(new Event('error'));
                                            self._dispatchEvent('error');
                                        } else if (evt.type === 'close') {
                                            self.readyState = WebSocket.CLOSED;
                                            var closeEvent = new Event('close');
                                            closeEvent.code = evt.code || 1000;
                                            closeEvent.reason = evt.reason || '';
                                            closeEvent.wasClean = evt.code === 1000;
                                            if (self.onclose) self.onclose(closeEvent);
                                            self._dispatchEvent('close');
                                            // Stop polling
                                            if (self._fenPollTimer !== null) {
                                                globalThis.clearInterval(self._fenPollTimer);
                                                self._fenPollTimer = null;
                                            }
                                        }
                                    }
                                }
                                // Process messages
                                if (data.messages && data.messages.length > 0) {
                                    for (var j = 0; j < data.messages.length; j++) {
                                        var msg = data.messages[j];
                                        var messageEvent = new Event('message');
                                        if (msg.text !== undefined) {
                                            messageEvent.data = msg.text;
                                        } else if (msg.binary !== undefined) {
                                            messageEvent.data = msg.binary;
                                        }
                                        messageEvent.origin = '';
                                        messageEvent.lastEventId = '';
                                        if (self.onmessage) self.onmessage(messageEvent);
                                        self._dispatchEvent('message');
                                    }
                                }
                            } catch (e) {
                                // JSON parse failure — ignore
                            }
                        }, 50); // Poll every 50ms
                    };

                    WebSocket.prototype.send = function (data) {
                        if (this.readyState !== WebSocket.OPEN) {
                            throw new DOMException('WebSocket is not open.', 'InvalidStateError');
                        }
                        if (this._fenId < 0) return;
                        __fenWebSocketSend(this._fenId, String(data));
                    };

                    WebSocket.prototype.close = function (code, reason) {
                        if (this.readyState === WebSocket.CLOSED || this.readyState === WebSocket.CLOSING) {
                            return;
                        }
                        this.readyState = WebSocket.CLOSING;
                        __fenWebSocketClose(this._fenId, code || 1000, reason || '');
                        this.readyState = WebSocket.CLOSED;
                        if (this._fenPollTimer !== null) {
                            globalThis.clearInterval(this._fenPollTimer);
                            this._fenPollTimer = null;
                        }
                    };

                    WebSocket.prototype.addEventListener = function (type, callback) {
                        if (typeof callback !== 'function') return;
                        type = String(type || '');
                        var listeners = this._fenListeners[type] || (this._fenListeners[type] = []);
                        if (listeners.indexOf(callback) < 0) listeners.push(callback);
                    };

                    WebSocket.prototype.removeEventListener = function (type, callback) {
                        type = String(type || '');
                        var listeners = this._fenListeners[type];
                        if (!listeners) return;
                        for (var i = listeners.length - 1; i >= 0; i--) {
                            if (listeners[i] === callback) listeners.splice(i, 1);
                        }
                    };

                    WebSocket.prototype._dispatchEvent = function (type) {
                        var listeners = (this._fenListeners[type] || []).slice();
                        for (var i = 0; i < listeners.length; i++) {
                            listeners[i].call(this, new Event(type));
                        }
                    };
                }

                // ── indexedDB ── https://w3c.github.io/IndexedDB/
                // Fully functional in-memory implementation with persistence hooks.
                // Uses __fenIdb* C# native functions for persistence when available.
                if (typeof globalThis.indexedDB === 'undefined') {
                    // ── Helpers ──
                    var _idbStore = {}; // { "db\0store": { _data: {key: value}, _indexes: {name: {keyPath, _data: {key: value}}} } }
                    function _idbExtractKey(value, keyPath) {
                        if (keyPath === null || keyPath === undefined) return undefined;
                        if (typeof keyPath === 'string' && keyPath.indexOf('.') < 0) return value[keyPath];
                        if (typeof keyPath === 'string') {
                            var parts = keyPath.split('.');
                            var v = value;
                            for (var i = 0; i < parts.length && v != null; i++) v = v[parts[i]];
                            return v;
                        }
                        if (Array.isArray(keyPath)) { var a=[]; for (var i=0;i<keyPath.length;i++) a.push(value[keyPath[i]]); return a; }
                        return undefined;
                    }
                    function _idbFireSuccess(request, result) {
                        globalThis.setTimeout(function () {
                            request.result = result;
                            request.readyState = 'done';
                            if (request.onsuccess) request.onsuccess({ type: 'success', target: request });
                        }, 0);
                    }
                    function _idbFireError(request, message) {
                        globalThis.setTimeout(function () {
                            request.error = { name: 'AbortError', message: String(message || '') };
                            request.readyState = 'done';
                            if (request.onerror) request.onerror({ type: 'error', target: request });
                        }, 0);
                    }
                    // ── IDBRequest ──
                    function IDBRequest() {
                        this.result = undefined;
                        this.error = null;
                        this.readyState = 'pending';
                        this.onsuccess = null;
                        this.onerror = null;
                    }
                    function IDBOpenDBRequest() {
                        IDBRequest.call(this);
                        this.onupgradeneeded = null;
                        this.onblocked = null;
                    }
                    // ── IDBDatabase ──
                    function IDBDatabase(name, version) {
                        this.name = String(name || 'default');
                        this.version = version || 1;
                        this.objectStoreNames = [];
                    }
                    IDBDatabase.prototype.createObjectStore = function (storeName, options) {
                        __fenLog('warn', '[IDB] createObjectStore("' + String(storeName) + '") in "' + this.name + '"');
                        var key = this.name + '\0' + storeName;
                        _idbStore[key] = { _data: {}, _indexes: {} };
                        this.objectStoreNames.push(String(storeName));
                        var keyPath = (options && options.keyPath) || null;
                        var autoIncrement = !!(options && options.autoIncrement);
                        var storeRef = _idbStore[key];
                        storeRef._keyPath = keyPath;
                        storeRef._autoIncrement = autoIncrement;
                        return {
                            name: String(storeName),
                            keyPath: keyPath,
                            autoIncrement: autoIncrement,
                            createIndex: function (indexName, keyPath, opts) {
                                __fenLog('warn', '[IDB] createIndex("' + String(indexName) + '", "' + String(keyPath) + '") on "' + String(storeName) + '"');
                                storeRef._indexes[String(indexName)] = { keyPath: keyPath, _data: {} };
                            },
                            deleteIndex: function (indexName) {
                                delete storeRef._indexes[String(indexName)];
                            },
                            put: function (value, keyOverride) {
                                __fenLog('warn', '[IDB] put in "' + String(storeName) + '"');
                                var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, keyPath);
                                if ((k === undefined || k === null) && autoIncrement) {
                                    var max = 0;
                                    var dk = Object.keys(storeRef._data);
                                    for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                    k = max + 1;
                                }
                                if (k === undefined || k === null) { _idbFireError(new IDBRequest(), 'No key'); return; }
                                storeRef._data[k] = value;
                                // Update indexes
                                var idxNames = Object.keys(storeRef._indexes);
                                for (var i = 0; i < idxNames.length; i++) {
                                    var idx = storeRef._indexes[idxNames[i]];
                                    var ik = _idbExtractKey(value, idx.keyPath);
                                    if (ik !== undefined && ik !== null) idx._data[ik] = value;
                                }
                                return k;
                            },
                            add: function (value, keyOverride) {
                                __fenLog('warn', '[IDB] add in "' + String(storeName) + '"');
                                var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, keyPath);
                                if ((k === undefined || k === null) && autoIncrement) {
                                    var max = 0; var dk = Object.keys(storeRef._data);
                                    for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                    k = max + 1;
                                }
                                if (storeRef._data.hasOwnProperty(k)) { _idbFireError(new IDBRequest(), 'Key already exists'); return; }
                                this.put(value, k);
                                return k;
                            },
                            get: function (key) {
                                __fenLog('warn', '[IDB] get(' + String(key) + ') from "' + String(storeName) + '" → ' + (storeRef._data.hasOwnProperty(key) ? 'hit' : 'miss'));
                                return storeRef._data.hasOwnProperty(key) ? storeRef._data[key] : undefined;
                            },
                            getAll: function () {
                                var vals = []; var dk = Object.keys(storeRef._data);
                                for (var i = 0; i < dk.length; i++) vals.push(storeRef._data[dk[i]]);
                                __fenLog('warn', '[IDB] getAll() from "' + String(storeName) + '" → ' + vals.length + ' records');
                                return vals;
                            },
                            getAllKeys: function () { return Object.keys(storeRef._data); },
                            getKey: function (key) {
                                return storeRef._data.hasOwnProperty(key) ? key : undefined;
                            },
                            clear: function () { storeRef._data = {}; },
                            delete: function (key) {
                                __fenLog('warn', '[IDB] delete(' + String(key) + ') from "' + String(storeName) + '"');
                                delete storeRef._data[key];
                                var idxNames = Object.keys(storeRef._indexes);
                                for (var i = 0; i < idxNames.length; i++) delete storeRef._indexes[idxNames[i]]._data[key];
                            },
                            count: function () { return Object.keys(storeRef._data).length; },
                            index: function (indexName) {
                                __fenLog('warn', '[IDB] index("' + String(indexName) + '") on "' + String(storeName) + '"');
                                var idx = storeRef._indexes[String(indexName)] || { keyPath: null, _data: {} };
                                return {
                                    name: String(indexName),
                                    keyPath: idx.keyPath,
                                    get: function (k) {
                                        var r = idx._data.hasOwnProperty(k) ? idx._data[k] : undefined;
                                        __fenLog('warn', '[IDB] idx.get(' + String(k) + ') → ' + (r !== undefined ? 'hit' : 'miss'));
                                        return r;
                                    },
                                    getKey: function (k) { return idx._data.hasOwnProperty(k) ? k : undefined; },
                                    getAll: function () { var vals=[]; var dk=Object.keys(idx._data); for(var i=0;i<dk.length;i++) vals.push(idx._data[dk[i]]); return vals; },
                                    getAllKeys: function () { return Object.keys(idx._data); },
                                    count: function () { return Object.keys(idx._data).length; },
                                    openCursor: function () { return undefined; },
                                    openKeyCursor: function () { return undefined; }
                                };
                            }
                        };
                    };
                    IDBDatabase.prototype.deleteObjectStore = function (storeName) {
                        var key = this.name + '\0' + storeName;
                        delete _idbStore[key];
                        var idx = this.objectStoreNames.indexOf(storeName);
                        if (idx >= 0) this.objectStoreNames.splice(idx, 1);
                    };
                    IDBDatabase.prototype.transaction = function (storeNames, mode) {
                        var dbName = this.name;
                        var stores = Array.isArray(storeNames) ? storeNames : [storeNames];
                        var txMode = String(mode || 'readonly');
                        __fenLog('warn', '[IDB] transaction([' + stores.join(',') + '], "' + txMode + '") on "' + dbName + '"');
                        var tx = {
                            mode: txMode,
                            objectStoreNames: stores.slice(),
                            _dbName: dbName,
                            _active: true,
                            objectStore: function (name) {
                                var key = dbName + '\0' + name;
                                var storeRef = _idbStore[key] = _idbStore[key] || { _data: {}, _indexes: {}, _keyPath: null, _autoIncrement: false };
                                var kp = storeRef._keyPath || null;
                                var ai = storeRef._autoIncrement || false;
                                return {
                                    name: String(name),
                                    keyPath: kp,
                                    autoIncrement: ai,
                                    put: function (value, keyOverride) {
                                        var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, kp);
                                        if ((k === undefined || k === null) && ai) {
                                            var max = 0; var dk = Object.keys(storeRef._data);
                                            for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                            k = max + 1;
                                        }
                                        if (k === undefined || k === null) return;
                                        storeRef._data[k] = value;
                                        // Update indexes
                                        var idxNames = Object.keys(storeRef._indexes);
                                        for (var i = 0; i < idxNames.length; i++) {
                                            var idx = storeRef._indexes[idxNames[i]];
                                            var ik = _idbExtractKey(value, idx.keyPath);
                                            if (ik !== undefined && ik !== null) idx._data[ik] = value;
                                        }
                                        // Persist via C# bridge if available
                                        if (typeof __fenIdbPut === 'function') {
                                            try { __fenIdbPut(dbName, String(name), String(k), JSON.stringify(value)); } catch(e) {}
                                        }
                                        return k;
                                    },
                                    add: function (value, keyOverride) {
                                        var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, kp);
                                        if ((k === undefined || k === null) && ai) {
                                            var max = 0; var dk = Object.keys(storeRef._data);
                                            for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                            k = max + 1;
                                        }
                                        if (storeRef._data.hasOwnProperty(k)) return;
                                        return this.put(value, k);
                                    },
                                    get: function (k) { return storeRef._data.hasOwnProperty(k) ? storeRef._data[k] : undefined; },
                                    getAll: function () { var vals=[]; var dk=Object.keys(storeRef._data); for(var i=0;i<dk.length;i++) vals.push(storeRef._data[dk[i]]); return vals; },
                                    getAllKeys: function () { return Object.keys(storeRef._data); },
                                    getKey: function (k) { return storeRef._data.hasOwnProperty(k) ? k : undefined; },
                                    delete: function (k) {
                                        delete storeRef._data[k];
                                        var idxNames = Object.keys(storeRef._indexes);
                                        for (var i = 0; i < idxNames.length; i++) delete storeRef._indexes[idxNames[i]]._data[k];
                                        if (typeof __fenIdbDelete === 'function') {
                                            try { __fenIdbDelete(dbName, String(name), String(k)); } catch(e) {}
                                        }
                                    },
                                    clear: function () { storeRef._data = {}; },
                                    count: function () { return Object.keys(storeRef._data).length; },
                                    index: function (indexName) {
                                        var idx = storeRef._indexes[String(indexName)] || { keyPath: null, _data: {} };
                                        return {
                                            name: String(indexName),
                                            keyPath: idx.keyPath,
                                            get: function (k) { return idx._data.hasOwnProperty(k) ? idx._data[k] : undefined; },
                                            getKey: function (k) { return idx._data.hasOwnProperty(k) ? k : undefined; },
                                            getAll: function () { var vals=[]; var dk=Object.keys(idx._data); for(var i=0;i<dk.length;i++) vals.push(idx._data[dk[i]]); return vals; },
                                            getAllKeys: function () { return Object.keys(idx._data); },
                                            count: function () { return Object.keys(idx._data).length; },
                                            openCursor: function () { return undefined; },
                                            openKeyCursor: function () { return undefined; }
                                        };
                                    }
                                };
                            },
                            oncomplete: null,
                            onerror: null,
                            onabort: null,
                            abort: function () { this._active = false; },
                            commit: function () {
                                var self = this;
                                globalThis.setTimeout(function () {
                                    if (self._active && self.oncomplete) self.oncomplete({ type: 'complete', target: self });
                                    self._active = false;
                                }, 0);
                            }
                        };
                        // Auto-commit after this event loop turn
                        globalThis.setTimeout(function () {
                            if (tx._active && tx.oncomplete) tx.oncomplete({ type: 'complete', target: tx });
                            tx._active = false;
                        }, 0);
                        return tx;
                    };
                    IDBDatabase.prototype.close = function () {};

                    globalThis.indexedDB = {
                        open: function (name, version) {
                            var dbName = String(name || 'default');
                            var ver = version || 1;
                            __fenLog('warn', '[IDB] open("' + dbName + '", ' + ver + ')');
                            var request = new IDBOpenDBRequest();
                            // Load persisted data via C# bridge if available
                            if (typeof __fenIdbLoad === 'function') {
                                try {
                                    var jsonStr = __fenIdbLoad(dbName);
                                    if (jsonStr) {
                                        var loaded = JSON.parse(jsonStr);
                                        for (var k in loaded) {
                                            if (loaded.hasOwnProperty(k)) {
                                                _idbStore[k] = loaded[k];
                                                // Rebuild indexes
                                                var storeRef = _idbStore[k];
                                                storeRef._indexes = storeRef._indexes || {};
                                                var idxNames = Object.keys(storeRef._indexes);
                                                var dataKeys = Object.keys(storeRef._data || {});
                                                for (var di = 0; di < dataKeys.length; di++) {
                                                    var val = storeRef._data[dataKeys[di]];
                                                    for (var ii = 0; ii < idxNames.length; ii++) {
                                                        var idx = storeRef._indexes[idxNames[ii]];
                                                        var ik = _idbExtractKey(val, idx.keyPath);
                                                        if (ik !== undefined && ik !== null) idx._data = idx._data || {};
                                                        if (ik !== undefined && ik !== null) idx._data[ik] = val;
                                                    }
                                                }
                                            }
                                        }
                                        __fenLog('warn', '[IDB] loaded persisted data for "' + dbName + '"');
                                    }
                                } catch (e) {
                                    __fenLog('warn', '[IDB] failed to load persisted data: ' + e.message);
                                }
                            }
                            globalThis.setTimeout(function () {
                                var db = new IDBDatabase(dbName, ver);
                                // Gather existing store names
                                var prefix = dbName + '\0';
                                var storeKeys = Object.keys(_idbStore);
                                for (var i = 0; i < storeKeys.length; i++) {
                                    if (storeKeys[i].indexOf(prefix) === 0) {
                                        var sn = storeKeys[i].substring(prefix.length);
                                        if (db.objectStoreNames.indexOf(sn) < 0) db.objectStoreNames.push(sn);
                                    }
                                }
                                request.result = db;
                                request.readyState = 'done';
                                __fenLog('warn', '[IDB] open onsuccess fired for "' + dbName + '" v' + ver + ' (stores: ' + db.objectStoreNames.join(',') + ')');
                                if (request.onsuccess) request.onsuccess({ type: 'success', target: request });
                            }, 0);
                            return request;
                        },
                        deleteDatabase: function (name) {
                            var dbName = String(name || 'default');
                            __fenLog('warn', '[IDB] deleteDatabase("' + dbName + '")');
                            var prefix = dbName + '\0';
                            var keys = Object.keys(_idbStore);
                            for (var i = 0; i < keys.length; i++) {
                                if (keys[i].indexOf(prefix) === 0) delete _idbStore[keys[i]];
                            }
                            if (typeof __fenIdbDeleteDatabase === 'function') {
                                try { __fenIdbDeleteDatabase(dbName); } catch(e) {}
                            }
                            var request = new IDBOpenDBRequest();
                            globalThis.setTimeout(function () {
                                request.readyState = 'done';
                                if (request.onsuccess) request.onsuccess({ type: 'success', target: request });
                            }, 0);
                            return request;
                        },
                        cmp: function (a, b) {
                            if (a === b) return 0;
                            if (a < b) return -1;
                            return 1;
                        },
                        databases: function () { return Promise.resolve([]); }
                    };
                }

                // ── navigator.serviceWorker ──
                // C# host property is set before JS runs (CreateDefaultServiceWorkerStub),
                // which returns resolved promises so apps that await registration don't hang.
                // Only install the JS fallback if the C# property is not visible.
                if (globalThis.navigator && globalThis.navigator.serviceWorker === undefined) {
                    globalThis.navigator.serviceWorker = {
                        register: function () {
                            return Promise.resolve({
                                installing: null, waiting: null, active: null, scope: '/',
                                updateViaCache: 'imports',
                                update: function () { return Promise.resolve(); },
                                unregister: function () { return Promise.resolve(true); },
                                addEventListener: function () {}
                            });
                        },
                        getRegistration: function () { return Promise.resolve(undefined); },
                        getRegistrations: function () { return Promise.resolve([]); },
                        ready: Promise.resolve(undefined),
                        controller: null,
                        oncontrollerchange: null
                    };
                }

                // ── BroadcastChannel ── https://html.spec.whatwg.org/#broadcasting-to-other-browsing-contexts
                // WhatsApp uses this for multi-tab coordination (e.g., "you have another tab open").
                // In-process broadcast delivers messages to all channels with the same name.
                if (typeof globalThis.BroadcastChannel === 'undefined') {
                    var _bcChannels = {}; // { name: [channelInstance, ...] }
                    globalThis.BroadcastChannel = function BroadcastChannel(name) {
                        this.name = String(name || '');
                        this.onmessage = null;
                        this.onmessageerror = null;
                        this._closed = false;
                        this._listeners = {};
                        var list = _bcChannels[this.name];
                        if (!list) { list = []; _bcChannels[this.name] = list; }
                        list.push(this);
                    };
                    BroadcastChannel.prototype.postMessage = function (message) {
                        if (this._closed) throw new DOMException('Channel is closed', 'InvalidStateError');
                        var list = _bcChannels[this.name];
                        if (!list) return;
                        var evt = { data: message, origin: '', lastEventId: '', source: null, ports: [] };
                        for (var i = 0; i < list.length; i++) {
                            var ch = list[i];
                            if (ch === this || ch._closed) continue;
                            if (typeof ch.onmessage === 'function') {
                                globalThis.setTimeout(function (c, e) { return function () { c.onmessage(e); }; }(ch, evt), 0);
                            }
                            var ls = ch._listeners['message'];
                            if (ls) {
                                for (var j = 0; j < ls.length; j++) {
                                    globalThis.setTimeout(function (l, e) { return function () { l(e); }; }(ls[j], evt), 0);
                                }
                            }
                        }
                    };
                    BroadcastChannel.prototype.close = function () {
                        this._closed = true;
                        var list = _bcChannels[this.name];
                        if (list) {
                            var idx = list.indexOf(this);
                            if (idx >= 0) list.splice(idx, 1);
                        }
                    };
                    BroadcastChannel.prototype.addEventListener = function (type, callback) {
                        if (this._closed) return;
                        var ls = this._listeners[type] || (this._listeners[type] = []);
                        if (ls.indexOf(callback) < 0) ls.push(callback);
                    };
                    BroadcastChannel.prototype.removeEventListener = function (type, callback) {
                        var ls = this._listeners[type];
                        if (!ls) return;
                        var idx = ls.indexOf(callback);
                        if (idx >= 0) ls.splice(idx, 1);
                    };
                }

                // ── CacheStorage (caches) ── https://w3c.github.io/ServiceWorker/#cachestorage
                // WhatsApp and many PWAs check for caches API availability.
                if (typeof globalThis.caches === 'undefined') {
                    globalThis.CacheStorage = function CacheStorage() {};
                    globalThis.caches = {
                        open: function (cacheName) {
                            return Promise.resolve({
                                match: function (request) { return Promise.resolve(undefined); },
                                matchAll: function (request) { return Promise.resolve([]); },
                                add: function (request) { return Promise.resolve(); },
                                addAll: function (requests) { return Promise.resolve(); },
                                put: function (request, response) { return Promise.resolve(); },
                                delete: function (request) { return Promise.resolve(true); },
                                keys: function () { return Promise.resolve([]); }
                            });
                        },
                        has: function (cacheName) { return Promise.resolve(false); },
                        delete: function (cacheName) { return Promise.resolve(true); },
                        keys: function () { return Promise.resolve([]); },
                        match: function (request) { return Promise.resolve(undefined); }
                    };
                }

                // ── navigator.storage ── https://storage.spec.whatwg.org/
                if (globalThis.navigator && !globalThis.navigator.storage) {
                    globalThis.navigator.storage = {
                        estimate: function () {
                            return Promise.resolve({ quota: 0, usage: 0 });
                        },
                        persist: function () { return Promise.resolve(false); },
                        persisted: function () { return Promise.resolve(false); }
                    };
                }
            })();
            """ +
            "globalThis.isSecureContext = " + secureContextLiteral + ";" +
            "globalThis.window.isSecureContext = globalThis.isSecureContext;" +
            // Diagnostic error overlay — surfaces unhandled JS errors visibly on the page
            // so we can see what's failing without opening DevTools. Remove once stable.
            "(function(){" +
            "  var _errs=[];" +
            "  globalThis.addEventListener('error',function(e){" +
            "    var msg=e.message||String(e);" +
            "    _errs.push(msg);" +
            "    var d=document.getElementById('__fen_errs');" +
            "    if(!d){d=document.createElement('div');d.id='__fen_errs';" +
            "    d.style.cssText='position:fixed;bottom:0;left:0;right:0;max-height:30vh;overflow:auto;background:#a00;color:#fff;font:11px monospace;z-index:99999;padding:6px;opacity:0.9';" +
            "    document.body&&document.body.appendChild(d);}" +
            "    d.textContent=_errs.slice(-20).join('\\n');" +
            "  });" +
            "  globalThis.addEventListener('unhandledrejection',function(e){" +
            "    var msg='UNHANDLED: '+(e.reason&&e.reason.message||String(e.reason||''));" +
            "    _errs.push(msg);" +
            "    var d=document.getElementById('__fen_errs');" +
            "    if(!d){d=document.createElement('div');d.id='__fen_errs';" +
            "    d.style.cssText='position:fixed;bottom:0;left:0;right:0;max-height:30vh;overflow:auto;background:#a00;color:#fff;font:11px monospace;z-index:99999;padding:6px;opacity:0.9';" +
            "    document.body&&document.body.appendChild(d);}" +
            "    d.textContent=_errs.slice(-20).join('\\n');" +
            "  });" +
            "})();");
    }

    private static bool IsPotentiallyTrustworthyOrigin(Uri uri)
    {
        if (uri == null)
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
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
        var callbackContext = CaptureActiveWindowCallbackContext();

        var id = Interlocked.Increment(ref _fenJsTimerIdCounter);
        FenBrowser.Core.EngineLogCompat.Debug(
            $"[FenJsTimers] Scheduled {(repeat ? "interval" : "timeout")} id={id} delayMs={delayMs} callback={callback.Tag}",
            FenBrowser.Core.Logging.LogCategory.JavaScript);
        UpdateEventLoopSnapshot(snapshot =>
        {
            if (repeat)
            {
                snapshot.IntervalsScheduled++;
            }
            else
            {
                snapshot.TimersScheduled++;
            }
        });
        AddEventLoopRecord("TimerScheduled", callback.Tag.ToString(), id, delayMs, repeat);
        LogEventLoop(
            "TimerScheduled",
            LogSeverity.Debug,
            "[FenJsBridge] Host timer scheduled",
            new Dictionary<string, object>
            {
                ["id"] = id,
                ["delayMs"] = delayMs,
                ["repeating"] = repeat,
                ["timerType"] = repeat ? "interval" : "timeout",
                ["callbackTag"] = callback.Tag.ToString()
            });
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

                AddEventLoopRecord("TimerFired", callback.Tag.ToString(), id, delayMs, repeat);
                LogEventLoop(
                    "TimerFired",
                    LogSeverity.Debug,
                    "[FenJsBridge] Host timer fired",
                    new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["delayMs"] = delayMs,
                        ["repeating"] = repeat,
                        ["timerType"] = repeat ? "interval" : "timeout",
                        ["callbackTag"] = callback.Tag.ToString()
                    });
                InvokeFenJsCallbackSafely(callback, extraArgs, repeat ? "setInterval" : "setTimeout", id, callbackContext);
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
        var callbackContext = CaptureActiveWindowCallbackContext();
        var id = Interlocked.Increment(ref _fenJsTimerIdCounter);
        UpdateEventLoopSnapshot(snapshot => snapshot.AnimationFramesScheduled++);
        AddEventLoopRecord("RequestAnimationFrameScheduled", callback.Tag.ToString(), id, 16);
        LogEventLoop(
            "RequestAnimationFrameScheduled",
            LogSeverity.Debug,
            "[FenJsBridge] requestAnimationFrame scheduled",
            new Dictionary<string, object>
            {
                ["id"] = id,
                ["delayMs"] = 16,
                ["callbackTag"] = callback.Tag.ToString()
            });
        var timer = new Timer(
            _ =>
            {
                if (_fenJsTimers.TryRemove(id, out var self))
                {
                    self.Dispose();
                }

                var timestamp = JsValue.FromNumber(_fenJsClock.Elapsed.TotalMilliseconds);
                AddEventLoopRecord("RequestAnimationFrameFired", callback.Tag.ToString(), id, 16);
                LogEventLoop(
                    "RequestAnimationFrameFired",
                    LogSeverity.Debug,
                    "[FenJsBridge] requestAnimationFrame fired",
                    new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["delayMs"] = 16,
                        ["callbackTag"] = callback.Tag.ToString()
                    });
                InvokeFenJsCallbackSafely(callback, new[] { timestamp }, "requestAnimationFrame", id, callbackContext);
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
                AddEventLoopRecord("HostTimerCancelled", "clearTimeout/clearInterval", id);
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
    private void InvokeFenJsCallbackSafely(
        JsValue callback,
        IReadOnlyList<JsValue> args,
        string origin,
        long callbackId = 0,
        FenJsWindowCallbackContext windowContext = null)
    {
        try
        {
            RunFenJsWithLargeStack<object>(() =>
            {
                lock (_fenJsLock)
                {
                    try
                    {
                        using var callbackWindowScope = windowContext != null
                            ? ActivateWindowCallbackContext(windowContext.WindowTarget, windowContext.WindowListeners)
                            : null;
                        var callbackThis = windowContext?.WindowTarget ?? _fenJsGlobalThis;
                        var invoked = false;
                        LogEventLoop(
                            "TaskStarted",
                            LogSeverity.Debug,
                            "[FenJsBridge] event-loop callback task started",
                            new Dictionary<string, object>
                            {
                                ["origin"] = origin ?? string.Empty,
                                ["id"] = callbackId,
                                ["callbackTag"] = callback.Tag.ToString()
                            });
                        if (_interpreter.CanCallValue(callback))
                        {
                            _interpreter.InvokeFunction(callback, args ?? Array.Empty<JsValue>(), callbackThis);
                            invoked = true;
                        }

                        if (invoked)
                        {
                            UpdateEventLoopSnapshot(snapshot =>
                            {
                                if (string.Equals(origin, "requestAnimationFrame", StringComparison.Ordinal))
                                {
                                    snapshot.AnimationFramesExecuted++;
                                }
                                else if (string.Equals(origin, "setTimeout", StringComparison.Ordinal) ||
                                         string.Equals(origin, "setInterval", StringComparison.Ordinal))
                                {
                                    snapshot.TimersExecuted++;
                                }

                                snapshot.LastCallbackOrigin = origin ?? string.Empty;
                            });
                            AddEventLoopRecord("CallbackCompleted", origin ?? string.Empty, callbackId);
                            LogEventLoop(
                                "TaskCompleted",
                                LogSeverity.Debug,
                                "[FenJsBridge] event-loop callback task completed",
                                new Dictionary<string, object>
                                {
                                    ["origin"] = origin ?? string.Empty,
                                    ["id"] = callbackId,
                                    ["success"] = true
                                });
                            LogEventLoop(
                                "CallbackCompleted",
                                LogSeverity.Debug,
                                "[FenJsBridge] event-loop callback completed",
                                new Dictionary<string, object>
                                {
                                    ["origin"] = origin ?? string.Empty,
                                    ["id"] = callbackId
                                });
                        }
                        else
                        {
                            AddEventLoopRecord("CallbackSkipped", origin ?? string.Empty, callbackId);
                            LogEventLoop(
                                "TaskCompleted",
                                LogSeverity.Warn,
                                "[FenJsBridge] event-loop callback task completed without invocation",
                                new Dictionary<string, object>
                                {
                                    ["origin"] = origin ?? string.Empty,
                                    ["id"] = callbackId,
                                    ["success"] = false,
                                    ["reason"] = "callback-not-callable",
                                    ["callbackTag"] = callback.Tag.ToString()
                                });
                            LogEventLoop(
                                "CallbackSkipped",
                                LogSeverity.Warn,
                                "[FenJsBridge] event-loop callback skipped because it was not callable",
                                new Dictionary<string, object>
                                {
                                    ["origin"] = origin ?? string.Empty,
                                    ["id"] = callbackId,
                                    ["callbackTag"] = callback.Tag.ToString()
                                });
                        }

                        _interpreter.PumpMicrotasks();
                        RecordMicrotaskCheckpoint(origin);
                    }
                    catch (Exception ex)
                    {
                        UpdateEventLoopSnapshot(snapshot =>
                        {
                            snapshot.CallbackFailures++;
                            snapshot.LastCallbackOrigin = origin ?? string.Empty;
                            snapshot.LastError = ex.GetType().Name + ": " + ex.Message;
                        });
                        AddEventLoopRecord("CallbackFailed", origin ?? string.Empty, callbackId);
                        LogEventLoop(
                            "TaskFailed",
                            LogSeverity.Warn,
                            "[FenJsBridge] event-loop callback task failed",
                            new Dictionary<string, object>
                            {
                                ["origin"] = origin ?? string.Empty,
                                ["id"] = callbackId,
                                ["errorType"] = ex.GetType().Name,
                                ["error"] = ex.Message
                            },
                            LogMarker.EngineBug);
                        LogEventLoop(
                            "TaskCompleted",
                            LogSeverity.Warn,
                            "[FenJsBridge] event-loop callback task completed after failure",
                            new Dictionary<string, object>
                            {
                                ["origin"] = origin ?? string.Empty,
                                ["id"] = callbackId,
                                ["success"] = false,
                                ["errorType"] = ex.GetType().Name,
                                ["error"] = ex.Message
                            });
                        LogEventLoop(
                            "CallbackFailed",
                            LogSeverity.Warn,
                            "[FenJsBridge] event-loop callback failed",
                            new Dictionary<string, object>
                            {
                                ["origin"] = origin ?? string.Empty,
                                ["id"] = callbackId,
                                ["errorType"] = ex.GetType().Name,
                                ["error"] = ex.Message
                            });
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[FenJsTimers] {origin} callback failed: {ex.Message}",
                            FenBrowser.Core.Logging.LogCategory.JavaScript);
                    }
                }

            return null;
            });
        }
        catch (Exception ex)
        {
            UpdateEventLoopSnapshot(snapshot =>
            {
                snapshot.CallbackFailures++;
                snapshot.LastCallbackOrigin = origin ?? string.Empty;
                snapshot.LastError = ex.GetType().Name + ": " + ex.Message;
            });
            AddEventLoopRecord("CallbackCrashed", origin ?? string.Empty, callbackId);
            LogEventLoop(
                "TaskFailed",
                LogSeverity.Warn,
                "[FenJsBridge] event-loop callback task crashed",
                new Dictionary<string, object>
                {
                    ["origin"] = origin ?? string.Empty,
                    ["id"] = callbackId,
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                },
                LogMarker.EngineBug);
            LogEventLoop(
                "TaskCompleted",
                LogSeverity.Warn,
                "[FenJsBridge] event-loop callback task completed after crash",
                new Dictionary<string, object>
                {
                    ["origin"] = origin ?? string.Empty,
                    ["id"] = callbackId,
                    ["success"] = false,
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                });
            LogEventLoop(
                "CallbackCrashed",
                LogSeverity.Warn,
                "[FenJsBridge] event-loop callback crashed",
                new Dictionary<string, object>
                {
                    ["origin"] = origin ?? string.Empty,
                    ["id"] = callbackId,
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                });
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsTimers] {origin} callback crashed: {ex.GetType().Name}: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }

        try { RequestRender?.Invoke(); }
        catch { /* render request is best-effort */ }
    }

    private void InstallFenJsBrowserConstructors()
    {
        EvaluateWithFenJsRaw(
            """
            (function () {
                function defineCtor(name, baseCtor, prototypeBrands, match) {
                    var ctor = function () {
                        if (name === 'HTMLElement') {
                            if (globalThis.__fenCustomElementConstructionElement) {
                                return globalThis.__fenCustomElementConstructionElement;
                            }

                            var constructionCtor = (typeof new.target === 'function' && new.target) ||
                                (this && this.constructor);
                            var constructionLocalName = constructionCtor &&
                                constructionCtor.__fenCustomElementLocalName;
                            if (constructionLocalName &&
                                typeof globalThis.__fenCreateCustomElementConstructionElement === 'function') {
                                return globalThis.__fenCreateCustomElementConstructionElement(constructionLocalName);
                            }
                        }
                        throw new TypeError('Illegal constructor');
                    };
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

                var Window = defineCtor('Window', null, ['Window'], function (candidate) {
                    return candidate === globalThis || candidate.window === candidate;
                });

                var Node = defineCtor('Node', null, ['Node'], function (candidate) {
                    return typeof candidate.nodeName === 'string' ||
                        typeof candidate.tagName === 'string' ||
                        typeof candidate.parentNode !== 'undefined' ||
                        typeof candidate.ownerDocument !== 'undefined';
                });
                var nodeTypeConstants = {
                    ELEMENT_NODE: 1,
                    ATTRIBUTE_NODE: 2,
                    TEXT_NODE: 3,
                    CDATA_SECTION_NODE: 4,
                    ENTITY_REFERENCE_NODE: 5,
                    ENTITY_NODE: 6,
                    PROCESSING_INSTRUCTION_NODE: 7,
                    COMMENT_NODE: 8,
                    DOCUMENT_NODE: 9,
                    DOCUMENT_TYPE_NODE: 10,
                    DOCUMENT_FRAGMENT_NODE: 11,
                    NOTATION_NODE: 12
                };
                for (var nodeTypeName in nodeTypeConstants) {
                    Object.defineProperty(Node, nodeTypeName, {
                        value: nodeTypeConstants[nodeTypeName],
                        enumerable: true,
                        configurable: true
                    });
                    Object.defineProperty(Node.prototype, nodeTypeName, {
                        value: nodeTypeConstants[nodeTypeName],
                        enumerable: true,
                        configurable: true
                    });
                }

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

                var Text = defineCtor('Text', CharacterData, ['Node', 'CharacterData', 'Text'], function (candidate) {
                    return candidate.nodeName === '#text' &&
                        typeof candidate.appendData === 'function';
                });

                defineCtor('Comment', CharacterData, ['Node', 'CharacterData', 'Comment'], function (candidate) {
                    return candidate.nodeName === '#comment' &&
                        typeof candidate.replaceData === 'function';
                });

                defineCtor('CDATASection', Text, ['Node', 'CharacterData', 'Text', 'CDATASection'], function (candidate) {
                    return candidate.nodeName === '#cdata-section' &&
                        typeof candidate.appendData === 'function';
                });

                defineCtor('ProcessingInstruction', CharacterData, ['Node', 'CharacterData', 'ProcessingInstruction'], function (candidate) {
                    return candidate.nodeType === Node.PROCESSING_INSTRUCTION_NODE &&
                        typeof candidate.target === 'string' &&
                        typeof candidate.data === 'string';
                });

                var DocumentFragment = defineCtor('DocumentFragment', Node, ['Node', 'DocumentFragment'], function (candidate) {
                    return candidate.nodeName === '#document-fragment' &&
                        typeof candidate.appendChild === 'function' &&
                        typeof candidate.querySelectorAll === 'function';
                });

                var Range = defineCtor('Range', null, ['Range'], function (candidate) {
                    return candidate != null &&
                        typeof candidate.setStart === 'function' &&
                        typeof candidate.setEnd === 'function' &&
                        typeof candidate.commonAncestorContainer !== 'undefined';
                });
                var rangeConstants = {
                    START_TO_START: 0,
                    START_TO_END: 1,
                    END_TO_END: 2,
                    END_TO_START: 3
                };
                for (var rangeConstantName in rangeConstants) {
                    Object.defineProperty(Range, rangeConstantName, {
                        value: rangeConstants[rangeConstantName],
                        enumerable: true,
                        configurable: true
                    });
                    Object.defineProperty(Range.prototype, rangeConstantName, {
                        value: rangeConstants[rangeConstantName],
                        enumerable: true,
                        configurable: true
                    });
                }

                defineCtor('ShadowRoot', DocumentFragment, ['Node', 'DocumentFragment', 'ShadowRoot'], function (candidate) {
                    return candidate != null &&
                        typeof candidate.host !== 'undefined' &&
                        typeof candidate.querySelectorAll === 'function';
                });

                var Navigator = defineCtor('Navigator', null, ['Navigator'], function (candidate) {
                    return candidate === globalThis.navigator || (candidate && candidate.__fenDomBrands && candidate.__fenDomBrands.indexOf('Navigator') >= 0);
                });

                Object.defineProperty(Navigator.prototype, 'serviceWorker', {
                    get: function () {
                        return globalThis.navigator ? globalThis.navigator.serviceWorker : undefined;
                    },
                    configurable: true,
                    enumerable: true
                });

                Object.defineProperty(Navigator.prototype, 'storage', {
                    get: function () {
                        return globalThis.navigator ? globalThis.navigator.storage : undefined;
                    },
                    configurable: true,
                    enumerable: true
                });

                defineCtor('DOMStringMap', null, ['DOMStringMap'], function (candidate) {
                    return candidate && candidate.__fenDomBrands && candidate.__fenDomBrands.indexOf('DOMStringMap') >= 0;
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

                function describeFenHostProbeNode(node) {
                    if (!node) return String(node);
                    var parts = [];
                    try { parts.push(String(node.nodeName)); } catch (_nodeNameError) {}
                    try { if (node.id) parts.push('#' + String(node.id)); } catch (_idError) {}
                    try { if (node.localName) parts.push('local=' + String(node.localName)); } catch (_localNameError) {}
                    try { parts.push('type=' + String(node.nodeType)); } catch (_nodeTypeError) {}
                    try {
                        var parent = node.parentNode;
                        parts.push('parent=' + (parent ? String(parent.nodeName) : 'null'));
                    } catch (_parentError) {}
                    try {
                        var childNodes = node.childNodes;
                        var childLength = childNodes && typeof childNodes.length !== 'undefined'
                            ? Number(childNodes.length)
                            : -1;
                        parts.push('children=' + String(childLength));
                        if (childLength > 0) {
                            var childParts = [];
                            var maxChildren = childLength < 6 ? childLength : 6;
                            for (var childIndex = 0; childIndex < maxChildren; childIndex++) {
                                var childNode = childNodes[childIndex];
                                var childLabel = '';
                                try { childLabel += String(childNode.nodeName); } catch (_childNodeNameError) { childLabel += '?'; }
                                try { if (childNode.id) childLabel += '#' + String(childNode.id); } catch (_childIdError) {}
                                try { if (childNode.localName) childLabel += '[' + String(childNode.localName) + ']'; } catch (_childLocalNameError) {}
                                childParts.push(childLabel);
                            }
                            parts.push('childList=' + childParts.join('|'));
                        }
                    } catch (_childrenError) {}
                    return parts.join(':');
                }
                function recordFenHostMethodProbe(name, receiver, args) {
                    if (name !== 'contains' &&
                        name !== 'appendChild' &&
                        name !== 'insertBefore' &&
                        name !== 'replaceChild') {
                        return;
                    }
                    var first = args && args.length > 0 ? args[0] : undefined;
                    var probe = {
                        method: name,
                        receiver: describeFenHostProbeNode(receiver),
                        firstArg: describeFenHostProbeNode(first),
                        same: receiver === first
                    };
                    try { probe.stack = String((new Error()).stack || '').slice(0, 900); } catch (_hostMethodStackError) {}
                    try {
                        globalThis.__fenLastHostMethodProbe = probe;
                    } catch (_probeError) {}
                }
                function defineHostMethod(prototype, name) {
                    var hostMethod = function () {
                        recordFenHostMethodProbe(name, this, arguments);
                        return globalThis.__fenInvokeHostMethod(this, name, arguments);
                    };
                    Object.defineProperty(hostMethod, 'toString', {
                        value: function () { return 'function ' + name + '() { [native code] }'; },
                        writable: true,
                        configurable: true
                    });
                    Object.defineProperty(prototype, name, {
                        value: hostMethod,
                        writable: true,
                        configurable: true
                    });
                }

                [
                    'contains',
                    'dispatchEvent',
                    'cloneNode',
                    'appendChild',
                    'insertBefore',
                    'replaceChild',
                    'removeChild',
                    'append',
                    'prepend'
                ].forEach(function (name) { defineHostMethod(Node.prototype, name); });

                [
                    'createElement',
                    'createElementNS',
                    'createTextNode',
                    'createComment',
                    'createDocumentFragment',
                    'importNode',
                    'createRange',
                    'createAttribute',
                    'createEvent',
                    'createTreeWalker',
                    'hasStorageAccess',
                    'getElementById',
                    'querySelector',
                    'querySelectorAll',
                    'getElementsByTagName',
                ].forEach(function (name) { defineHostMethod(Document.prototype, name); });
                Window.prototype.addEventListener = function () { return globalThis.addEventListener.apply(globalThis, arguments); };
                Window.prototype.removeEventListener = function () { return globalThis.removeEventListener.apply(globalThis, arguments); };
                Window.prototype.dispatchEvent = function () { return globalThis.dispatchEvent.apply(globalThis, arguments); };
                if (Object.getPrototypeOf(globalThis) !== Window.prototype) {
                    Object.setPrototypeOf(globalThis, Window.prototype);
                }

                var elementHostMethods = [
                    'getAttribute',
                    'hasAttribute',
                    'setAttribute',
                    'removeAttribute',
                    'toggleAttribute',
                    'getAttributeNode',
                    'setAttributeNode',
                    'removeAttributeNode',
                    'hasAttributes',
                    'attachShadow',
                    'matches',
                    'closest',
                    'querySelector',
                    'querySelectorAll',
                    'getElementsByTagName',
                    'animate',
                ];
                elementHostMethods.forEach(function (name) { defineHostMethod(Element.prototype, name); });

                [
                    'setStart',
                    'setEnd',
                    'setStartBefore',
                    'setStartAfter',
                    'setEndBefore',
                    'setEndAfter',
                    'collapse',
                    'selectNode',
                    'selectNodeContents',
                    'compareBoundaryPoints',
                    'deleteContents',
                    'extractContents',
                    'cloneContents',
                    'insertNode',
                    'surroundContents',
                    'cloneRange',
                    'detach',
                    'isPointInRange',
                    'comparePoint',
                    'intersectsNode',
                    'createContextualFragment',
                    'getBoundingClientRect',
                    'getClientRects',
                    'toString'
                ].forEach(function (name) { defineHostMethod(Range.prototype, name); });
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

    private void InstallFenJsNativeRangeConstructor(Document document)
    {
        if (document == null)
        {
            return;
        }

        var rangeConstructor = _interpreter.AllocateNativeConstructor(
            "Range",
            (_, _) =>
            {
                ThrowDomException(
                    "TypeError",
                    "Failed to construct 'Range': Please use the 'new' operator.");
                return JsValue.Undefined;
            },
            _ => ToHostOrNull(new DomRange(document), HostObjectKind.Other),
            length: 0);
        _interpreter.RegisterGlobalValue("__fenNativeRangeCtor", rangeConstructor);

        EvaluateWithFenJsRaw(
            """
            (function () {
                var nativeRange = globalThis.__fenNativeRangeCtor;
                var brandedRange = globalThis.Range;
                if (brandedRange && brandedRange.prototype) {
                    nativeRange.prototype = brandedRange.prototype;
                    var hasInstance = Object.getOwnPropertyDescriptor(brandedRange, Symbol.hasInstance);
                    if (hasInstance) {
                        Object.defineProperty(nativeRange, Symbol.hasInstance, hasInstance);
                    }

                    var constants = ['START_TO_START', 'START_TO_END', 'END_TO_END', 'END_TO_START'];
                    for (var i = 0; i < constants.length; i++) {
                        var descriptor = Object.getOwnPropertyDescriptor(brandedRange, constants[i]);
                        if (descriptor) {
                            Object.defineProperty(nativeRange, constants[i], descriptor);
                        }
                    }
                }

                Object.defineProperty(globalThis, 'Range', {
                    value: nativeRange,
                    writable: true,
                    configurable: true
                });
                delete globalThis.__fenNativeRangeCtor;
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
    /// Installs EventTarget and Event constructors on the JS global scope per
    /// DOM Living Standard. Required by modern frameworks (GitHub elements, React
    /// custom elements) that extend or instantiate EventTarget directly.
    /// </summary>
    private void InstallFenJsEventTarget()
    {
        try
        {
            EvaluateWithFenJsRaw(
                """
                (function() {
                    // Minimal EventTarget polyfill per DOM Living Standard.
                    // Provides addEventListener, removeEventListener, dispatchEvent.
                    function EventTarget() {
                        this._fenListeners = Object.create(null);
                    }
                    EventTarget.prototype.addEventListener = function(type, callback, options) {
                        if (typeof callback !== 'function') return;
                        var listeners = this._fenListeners[type] || (this._fenListeners[type] = []);
                        for (var i = 0; i < listeners.length; i++) {
                            if (listeners[i].callback === callback) return;
                        }
                        listeners.push({ callback: callback, options: options || {} });
                    };
                    EventTarget.prototype.removeEventListener = function(type, callback) {
                        var listeners = this._fenListeners[type];
                        if (!listeners) return;
                        for (var i = listeners.length - 1; i >= 0; i--) {
                            if (listeners[i].callback === callback) listeners.splice(i, 1);
                        }
                    };
                    EventTarget.prototype.dispatchEvent = function(event) {
                        if (!event || typeof event.type !== 'string') return true;
                        event.target = this;
                        var listeners = (this._fenListeners[event.type] || []).slice();
                        for (var i = 0; i < listeners.length; i++) {
                            try { listeners[i].callback.call(this, event); } catch(e) {}
                        }
                        return !event.defaultPrevented;
                    };

                    // Minimal Event constructor.
                    function Event(type, options) {
                        options = options || {};
                        this.type = String(type);
                        this.bubbles = Boolean(options.bubbles);
                        this.cancelable = Boolean(options.cancelable);
                        this.composed = Boolean(options.composed);
                        this.defaultPrevented = false;
                        this.target = null;
                        this.currentTarget = null;
                        this.eventPhase = 0;
                        this.timeStamp = Date.now();
                    }
                    Event.prototype.preventDefault = function() {
                        if (this.cancelable) this.defaultPrevented = true;
                    };
                    Event.prototype.stopPropagation = function() {
                        this._stopPropagation = true;
                    };
                    Event.prototype.stopImmediatePropagation = function() {
                        this._stopImmediatePropagation = true;
                    };

                    globalThis.EventTarget = EventTarget;
                    globalThis.Event = Event;

                    // Stub HTML element constructors for custom elements /
                    // instanceof checks in modern frameworks (GitHub, React).
                    var _htmlEls = [
                        'HTMLButtonElement','HTMLDivElement','HTMLSpanElement',
                        'HTMLAnchorElement','HTMLInputElement','HTMLParagraphElement',
                        'HTMLUListElement','HTMLLIElement','HTMLHeadingElement',
                        'HTMLFormElement','HTMLImageElement','HTMLSelectElement',
                        'HTMLOptionElement','HTMLTextAreaElement','HTMLTableElement',
                        'HTMLTableRowElement','HTMLTableCellElement','HTMLTemplateElement',
                        'HTMLSlotElement','HTMLDialogElement','HTMLDetailsElement',
                        'HTMLSummaryElement','HTMLFieldSetElement','HTMLLegendElement',
                        'HTMLDataListElement','HTMLOptGroupElement','HTMLOutputElement',
                        'HTMLProgressElement','HTMLMeterElement','HTMLLabelElement',
                        'HTMLMediaElement','HTMLVideoElement','HTMLAudioElement',
                        'HTMLCanvasElement','HTMLSourceElement','HTMLTrackElement',
                        'HTMLUnknownElement','HTMLIFrameElement','HTMLObjectElement',
                        'HTMLParamElement','HTMLMapElement','HTMLAreaElement',
                        'HTMLQuoteElement','HTMLPreElement','HTMLHRElement',
                        'HTMLBRElement','HTMLDListElement','HTMLOListElement',
                        'HTMLBodyElement','HTMLHeadElement','HTMLHtmlElement',
                        'HTMLScriptElement','HTMLStyleElement','HTMLLinkElement',
                        'HTMLMetaElement','HTMLTitleElement','HTMLBaseElement',
                        'HTMLTimeElement','HTMLDataElement','HTMLPictureElement',
                        'HTMLModElement','HTMLTableCaptionElement','HTMLTableColElement',
                        'HTMLTableSectionElement','HTMLEmbedElement'
                    ];
                    for (var _i = 0; _i < _htmlEls.length; _i++) {
                        (function(name) {
                            function HTMLEl() {
                                if (!(this instanceof HTMLEl))
                                    throw new TypeError("Illegal constructor");
                            }
                            HTMLEl.prototype = Object.create(EventTarget.prototype);
                            HTMLEl.prototype.constructor = HTMLEl;
                            globalThis[name] = HTMLEl;
                        })(_htmlEls[_i]);
                    }
                })()
                """);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Debug(
                $"[FenJsBridge] EventTarget polyfill injection failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
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
            "__fenParseUrl",
            _interpreter.AllocateNativeFunction(
                "__fenParseUrl",
                (_, args) => ParseFenJsUrl(args),
                length: 2));
        _interpreter.RegisterGlobalValue(
            "__fenRandomByte",
            _interpreter.AllocateNativeFunction(
                "__fenRandomByte",
                (_, _) => JsValue.FromNumber(RandomNumberGenerator.GetInt32(0, 256))));
        _interpreter.RegisterGlobalValue(
            "__fenAtob",
            _interpreter.AllocateNativeFunction(
                "__fenAtob",
                (_, args) =>
                {
                    var data = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    try
                    {
                        var bytes = Convert.FromBase64String(data);
                        return CreateUint8ArrayFromBytes(bytes);
                    }
                    catch { return CreateUint8ArrayFromBytes(Array.Empty<byte>()); }
                }));
        _interpreter.RegisterGlobalValue(
            "__fenBtoa",
            _interpreter.AllocateNativeFunction(
                "__fenBtoa",
                (_, args) =>
                {
                    var bytes = ExtractBytesFromArrayLike(args.Count > 0 ? args[0] : JsValue.Undefined);
                    return JsValue.FromString(Convert.ToBase64String(bytes));
                }));

        // ── TextEncoder / TextDecoder ──
        // Native UTF-8 encoding bridge for Web Crypto and binary data handling.
        _interpreter.RegisterGlobalValue(
            "__fenTextEncode",
            _interpreter.AllocateNativeFunction(
                "__fenTextEncode",
                (_, args) =>
                {
                    var text = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    return CreateUint8ArrayFromBytes(bytes);
                }));
        _interpreter.RegisterGlobalValue(
            "__fenTextDecode",
            _interpreter.AllocateNativeFunction(
                "__fenTextDecode",
                (_, args) =>
                {
                    if (args.Count == 0) return JsValue.FromString(string.Empty);
                    var bytes = ExtractBytesFromArrayLike(args[0]);
                    if (bytes == null || bytes.Length == 0) return JsValue.FromString(string.Empty);
                    return JsValue.FromString(System.Text.Encoding.UTF8.GetString(bytes));
                }));

        // ── crypto.subtle ──
        // Web Crypto API bridge for AES-CBC decrypt and SHA digest.
        _interpreter.RegisterGlobalValue(
            "__fenCryptoImportKey",
            _interpreter.AllocateNativeFunction(
                "__fenCryptoImportKey",
                (_, args) => ImportCryptoKey(args)));
        _interpreter.RegisterGlobalValue(
            "__fenCryptoDecrypt",
            _interpreter.AllocateNativeFunction(
                "__fenCryptoDecrypt",
                (_, args) => CryptoDecrypt(args)));
        _interpreter.RegisterGlobalValue(
            "__fenCryptoDigest",
            _interpreter.AllocateNativeFunction(
                "__fenCryptoDigest",
                (_, args) => CryptoDigest(args)));

        // ── WebSocket native bridge ──
        _interpreter.RegisterGlobalValue(
            "__fenWebSocketConnect",
            _interpreter.AllocateNativeFunction(
                "__fenWebSocketConnect",
                (_, args) =>
                {
                    var url = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                    var protocolsJson = args.Count > 1 ? CoerceToHostString(args[1]) : null;
                    string[] protocols = null;
                    if (!string.IsNullOrWhiteSpace(protocolsJson) && protocolsJson != "[]" && protocolsJson != "null")
                    {
                        try
                        {
                            protocols = System.Text.Json.JsonSerializer.Deserialize<string[]>(protocolsJson);
                        }
                        catch { protocols = null; }
                    }
                    return ConnectWebSocket(url, protocols);
                }));
        _interpreter.RegisterGlobalValue(
            "__fenWebSocketSend",
            _interpreter.AllocateNativeFunction(
                "__fenWebSocketSend",
                (_, args) =>
                {
                    var id = args.Count > 0 ? (int)CoerceToFiniteNumber(args[0], -1) : -1;
                    var data = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                    return SendWebSocketMessage(id, data);
                }));
        _interpreter.RegisterGlobalValue(
            "__fenWebSocketClose",
            _interpreter.AllocateNativeFunction(
                "__fenWebSocketClose",
                (_, args) =>
                {
                    var id = args.Count > 0 ? (int)CoerceToFiniteNumber(args[0], -1) : -1;
                    var code = args.Count > 1 ? (int)CoerceToFiniteNumber(args[1], 1000) : 1000;
                    var reason = args.Count > 2 ? CoerceToHostString(args[2]) : string.Empty;
                    return CloseWebSocket(id, code, reason);
                }));
        _interpreter.RegisterGlobalValue(
            "__fenWebSocketPoll",
            _interpreter.AllocateNativeFunction(
                "__fenWebSocketPoll",
                (_, args) =>
                {
                    var id = args.Count > 0 ? (int)CoerceToFiniteNumber(args[0], -1) : -1;
                    return PollWebSocket(id);
                }));

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
                // URLSearchParams: enough of the WHATWG interface for real-site
                // query parsing and analytics/bootstrap code.
                globalThis.URLSearchParams = function URLSearchParams(init) {
                    if (!(this instanceof URLSearchParams)) {
                        throw new TypeError("Failed to construct 'URLSearchParams': Please use the 'new' operator.");
                    }

                    this._pairs = [];
                    if (init == null) {
                        return;
                    }

                    if (init instanceof URLSearchParams) {
                        for (var cloneIndex = 0; cloneIndex < init._pairs.length; cloneIndex++) {
                            this._pairs.push({ name: init._pairs[cloneIndex].name, value: init._pairs[cloneIndex].value });
                        }
                        return;
                    }

                    if (typeof init === 'string') {
                        var query = init.charAt(0) === '?' ? init.slice(1) : init;
                        if (query.length === 0) {
                            return;
                        }

                        var pieces = query.split('&');
                        for (var pieceIndex = 0; pieceIndex < pieces.length; pieceIndex++) {
                            if (pieces[pieceIndex] === '') {
                                continue;
                            }

                            var equalsIndex = pieces[pieceIndex].indexOf('=');
                            var rawName = equalsIndex >= 0 ? pieces[pieceIndex].slice(0, equalsIndex) : pieces[pieceIndex];
                            var rawValue = equalsIndex >= 0 ? pieces[pieceIndex].slice(equalsIndex + 1) : '';
                            this.append(decodeParam(rawName), decodeParam(rawValue));
                        }
                        return;
                    }

                    if (Array.isArray(init)) {
                        for (var pairIndex = 0; pairIndex < init.length; pairIndex++) {
                            var pair = init[pairIndex];
                            if (!pair || pair.length < 2) {
                                throw new TypeError("URLSearchParams sequence entries must be pairs.");
                            }
                            this.append(pair[0], pair[1]);
                        }
                        return;
                    }

                    if (typeof init === 'object') {
                        var keys = Object.keys(init);
                        for (var keyIndex = 0; keyIndex < keys.length; keyIndex++) {
                            this.append(keys[keyIndex], init[keys[keyIndex]]);
                        }
                    }
                };

                function decodeParam(value) {
                    var text = String(value).split('+').join(' ');
                    try {
                        return decodeURIComponent(text);
                    } catch (_) {
                        return text;
                    }
                }

                function encodeParam(value) {
                    return encodeURIComponent(String(value)).split('%20').join('+');
                }

                function createUrlSearchParamsIterator(items) {
                    var index = 0;
                    return {
                        next: function () {
                            if (index >= items.length) {
                                return { value: undefined, done: true };
                            }
                            return { value: items[index++], done: false };
                        }
                    };
                }

                URLSearchParams.prototype.append = function (name, value) {
                    this._pairs.push({ name: String(name), value: String(value) });
                };
                URLSearchParams.prototype.delete = function (name) {
                    name = String(name);
                    for (var index = this._pairs.length - 1; index >= 0; index--) {
                        if (this._pairs[index].name === name) {
                            this._pairs.splice(index, 1);
                        }
                    }
                };
                URLSearchParams.prototype.get = function (name) {
                    name = String(name);
                    for (var index = 0; index < this._pairs.length; index++) {
                        if (this._pairs[index].name === name) {
                            return this._pairs[index].value;
                        }
                    }
                    return null;
                };
                URLSearchParams.prototype.getAll = function (name) {
                    name = String(name);
                    var values = [];
                    for (var index = 0; index < this._pairs.length; index++) {
                        if (this._pairs[index].name === name) {
                            values.push(this._pairs[index].value);
                        }
                    }
                    return values;
                };
                URLSearchParams.prototype.has = function (name) {
                    return this.get(name) !== null;
                };
                URLSearchParams.prototype.set = function (name, value) {
                    name = String(name);
                    value = String(value);
                    var found = false;
                    for (var index = this._pairs.length - 1; index >= 0; index--) {
                        if (this._pairs[index].name !== name) {
                            continue;
                        }

                        if (!found) {
                            this._pairs[index].value = value;
                            found = true;
                        } else {
                            this._pairs.splice(index, 1);
                        }
                    }
                    if (!found) {
                        this.append(name, value);
                    }
                };
                URLSearchParams.prototype.sort = function () {
                    this._pairs.sort(function (left, right) {
                        return left.name < right.name ? -1 : (left.name > right.name ? 1 : 0);
                    });
                };
                URLSearchParams.prototype.forEach = function (callback, thisArg) {
                    if (typeof callback !== 'function') {
                        throw new TypeError('URLSearchParams.forEach callback must be a function.');
                    }
                    for (var index = 0; index < this._pairs.length; index++) {
                        callback.call(thisArg, this._pairs[index].value, this._pairs[index].name, this);
                    }
                };
                URLSearchParams.prototype.entries = function () {
                    var items = [];
                    for (var index = 0; index < this._pairs.length; index++) {
                        items.push([this._pairs[index].name, this._pairs[index].value]);
                    }
                    return createUrlSearchParamsIterator(items);
                };
                URLSearchParams.prototype.keys = function () {
                    var items = [];
                    for (var index = 0; index < this._pairs.length; index++) {
                        items.push(this._pairs[index].name);
                    }
                    return createUrlSearchParamsIterator(items);
                };
                URLSearchParams.prototype.values = function () {
                    var items = [];
                    for (var index = 0; index < this._pairs.length; index++) {
                        items.push(this._pairs[index].value);
                    }
                    return createUrlSearchParamsIterator(items);
                };
                URLSearchParams.prototype.toString = function () {
                    var parts = [];
                    for (var index = 0; index < this._pairs.length; index++) {
                        parts.push(encodeParam(this._pairs[index].name) + '=' + encodeParam(this._pairs[index].value));
                    }
                    return parts.join('&');
                };
                if (typeof Symbol === 'function' && Symbol.iterator) {
                    URLSearchParams.prototype[Symbol.iterator] = URLSearchParams.prototype.entries;
                }
                Object.defineProperty(URLSearchParams.prototype, 'size', {
                    get: function () { return this._pairs.length; },
                    configurable: true
                });

                globalThis.URL = function URL(input, base) {
                    if (!(this instanceof URL)) {
                        throw new TypeError("Failed to construct 'URL': Please use the 'new' operator.");
                    }

                    var parsed = __fenParseUrl(input, base);
                    if (!parsed) {
                        throw new TypeError('Invalid URL');
                    }

                    applyParsedUrl(this, parsed);
                };

                function applyParsedUrl(target, parsed) {
                    target.href = parsed.href;
                    target.origin = parsed.origin;
                    target.protocol = parsed.protocol;
                    target.username = '';
                    target.password = '';
                    target.host = parsed.host;
                    target.hostname = parsed.hostname;
                    target.port = parsed.port;
                    target.pathname = parsed.pathname;
                    target.search = parsed.search;
                    target.hash = parsed.hash;
                    target.searchParams = new URLSearchParams(target.search);
                }

                URL.prototype.toString = function () {
                    return this.href;
                };
                URL.prototype.toJSON = function () {
                    return this.href;
                };
                URL.canParse = function (input, base) {
                    return !!__fenParseUrl(input, base);
                };
                URL.parse = function (input, base) {
                    try {
                        return new URL(input, base);
                    } catch (_) {
                        return null;
                    }
                };
                URL.createObjectURL = function () {
                    return 'blob:fenbrowser/' + Math.random().toString(36).slice(2);
                };
                URL.revokeObjectURL = function () {};

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
                    // Fire initial callback with isIntersecting: true for observed elements.
                    // reCAPTCHA and other widgets depend on this for visibility detection.
                    var self = this;
                    if (typeof setTimeout === 'function') {
                        setTimeout(function () {
                            if (self._targets.indexOf(target) < 0) return;
                            var entry = {
                                target: target,
                                isIntersecting: true,
                                intersectionRatio: 1.0,
                                boundingClientRect: { top: 0, left: 0, bottom: 100, right: 100, width: 100, height: 100, x: 0, y: 0 },
                                intersectionRect: { top: 0, left: 0, bottom: 100, right: 100, width: 100, height: 100, x: 0, y: 0 },
                                rootBounds: { top: 0, left: 0, bottom: 800, right: 1200, width: 1200, height: 800, x: 0, y: 0 },
                                time: (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now()
                            };
                            try { self._callback([entry], self); } catch (e) {}
                        }, 0);
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
                Event.prototype.initEvent = function (type, bubbles, cancelable) {
                    if (this._fenDispatching) return;
                    this.type = String(type || '');
                    this.bubbles = !!bubbles;
                    this.cancelable = !!cancelable;
                    this.composed = false;
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
                CustomEvent.prototype.initCustomEvent = function (type, bubbles, cancelable, detail) {
                    if (this._fenDispatching) return;
                    Event.prototype.initEvent.call(this, type, bubbles, cancelable);
                    this.detail = detail !== undefined ? detail : null;
                };

                globalThis.UIEvent = function UIEvent(type, options) {
                    Event.call(this, type, options);
                    options = options || {};
                    this.view = options.view || null;
                    this.detail = options.detail || 0;
                };
                UIEvent.prototype = Object.create(Event.prototype);
                UIEvent.prototype.constructor = UIEvent;
                UIEvent.prototype.initUIEvent = function (type, bubbles, cancelable, view, detail) {
                    if (this._fenDispatching) return;
                    Event.prototype.initEvent.call(this, type, bubbles, cancelable);
                    this.view = view || null;
                    this.detail = detail || 0;
                };

                globalThis.MouseEvent = function MouseEvent(type, options) {
                    UIEvent.call(this, type, options);
                    options = options || {};
                    this.screenX = options.screenX || 0;
                    this.screenY = options.screenY || 0;
                    this.clientX = options.clientX || 0;
                    this.clientY = options.clientY || 0;
                    this.ctrlKey = !!options.ctrlKey;
                    this.shiftKey = !!options.shiftKey;
                    this.altKey = !!options.altKey;
                    this.metaKey = !!options.metaKey;
                    this.button = options.button || 0;
                    this.buttons = options.buttons || 0;
                    this.relatedTarget = options.relatedTarget || null;
                };
                MouseEvent.prototype = Object.create(UIEvent.prototype);
                MouseEvent.prototype.constructor = MouseEvent;
                MouseEvent.prototype.initMouseEvent = function (
                    type, bubbles, cancelable, view, detail, screenX, screenY,
                    clientX, clientY, ctrlKey, altKey, shiftKey, metaKey, button, relatedTarget) {
                    if (this._fenDispatching) return;
                    UIEvent.prototype.initUIEvent.call(this, type, bubbles, cancelable, view, detail);
                    this.screenX = screenX || 0;
                    this.screenY = screenY || 0;
                    this.clientX = clientX || 0;
                    this.clientY = clientY || 0;
                    this.ctrlKey = !!ctrlKey;
                    this.altKey = !!altKey;
                    this.shiftKey = !!shiftKey;
                    this.metaKey = !!metaKey;
                    this.button = button || 0;
                    this.buttons = this.button ? 1 << this.button : 0;
                    this.relatedTarget = relatedTarget || null;
                };

                globalThis.WheelEvent = function WheelEvent(type, options) {
                    MouseEvent.call(this, type, options);
                    options = options || {};
                    this.deltaX = Number(options.deltaX || 0);
                    this.deltaY = Number(options.deltaY || 0);
                    this.deltaZ = Number(options.deltaZ || 0);
                    this.deltaMode = Number(options.deltaMode || 0);
                };
                WheelEvent.prototype = Object.create(MouseEvent.prototype);
                WheelEvent.prototype.constructor = WheelEvent;
                WheelEvent.DOM_DELTA_PIXEL = 0;
                WheelEvent.DOM_DELTA_LINE = 1;
                WheelEvent.DOM_DELTA_PAGE = 2;
                WheelEvent.prototype.DOM_DELTA_PIXEL = 0;
                WheelEvent.prototype.DOM_DELTA_LINE = 1;
                WheelEvent.prototype.DOM_DELTA_PAGE = 2;
                WheelEvent.prototype.initWheelEvent = function (
                    type, bubbles, cancelable, view, detail, screenX, screenY,
                    clientX, clientY, button, relatedTarget, modifiersList,
                    deltaX, deltaY, deltaZ, deltaMode) {
                    if (this._fenDispatching) return;
                    MouseEvent.prototype.initMouseEvent.call(
                        this,
                        type,
                        bubbles,
                        cancelable,
                        view,
                        detail,
                        screenX,
                        screenY,
                        clientX,
                        clientY,
                        modifiersList && modifiersList.indexOf('Control') >= 0,
                        modifiersList && modifiersList.indexOf('Alt') >= 0,
                        modifiersList && modifiersList.indexOf('Shift') >= 0,
                        modifiersList && modifiersList.indexOf('Meta') >= 0,
                        button,
                        relatedTarget);
                    this.deltaX = Number(deltaX || 0);
                    this.deltaY = Number(deltaY || 0);
                    this.deltaZ = Number(deltaZ || 0);
                    this.deltaMode = Number(deltaMode || 0);
                };

                globalThis.KeyboardEvent = function KeyboardEvent(type, options) {
                    UIEvent.call(this, type, options);
                    options = options || {};
                    this.key = options.key || '';
                    this.code = options.code || '';
                    this.location = options.location || 0;
                    this.ctrlKey = !!options.ctrlKey;
                    this.shiftKey = !!options.shiftKey;
                    this.altKey = !!options.altKey;
                    this.metaKey = !!options.metaKey;
                    this.repeat = !!options.repeat;
                    this.isComposing = !!options.isComposing;
                    this.locale = options.locale || '';
                };
                KeyboardEvent.prototype = Object.create(UIEvent.prototype);
                KeyboardEvent.prototype.constructor = KeyboardEvent;
                KeyboardEvent.prototype.initKeyboardEvent = function (
                    type, bubbles, cancelable, view, key, location, modifiers, repeat, locale) {
                    if (this._fenDispatching) return;
                    UIEvent.prototype.initUIEvent.call(this, type, bubbles, cancelable, view, 0);
                    this.key = key || '';
                    this.location = location || 0;
                    this.repeat = !!repeat;
                    this.locale = locale || '';
                    modifiers = modifiers || '';
                    this.ctrlKey = modifiers.indexOf('Control') >= 0;
                    this.shiftKey = modifiers.indexOf('Shift') >= 0;
                    this.altKey = modifiers.indexOf('Alt') >= 0;
                    this.metaKey = modifiers.indexOf('Meta') >= 0;
                };

                globalThis.BeforeUnloadEvent = function BeforeUnloadEvent(type, options) {
                    Event.call(this, type || 'beforeunload', options);
                    this.returnValue = '';
                };
                BeforeUnloadEvent.prototype = Object.create(Event.prototype);
                BeforeUnloadEvent.prototype.constructor = BeforeUnloadEvent;

                globalThis.MessageEvent = function MessageEvent(type, options) {
                    Event.call(this, type, options);
                    options = options || {};
                    this.data = options.data;
                    this.origin = options.origin || '';
                    this.lastEventId = options.lastEventId || '';
                    this.source = options.source || null;
                    this.ports = options.ports || [];
                };
                MessageEvent.prototype = Object.create(Event.prototype);
                MessageEvent.prototype.constructor = MessageEvent;

                globalThis.postMessage = function (message, targetOrigin) {
                    var event = new MessageEvent('message', {
                        data: message,
                        origin: String(location && location.origin || ''),
                        source: globalThis,
                        ports: []
                    });
                    event.target = globalThis;
                    event.currentTarget = globalThis;
                    var deliver = function () {
                        if (typeof globalThis.onmessage === 'function') {
                            globalThis.onmessage.call(globalThis, event);
                        }
                        globalThis.dispatchEvent(event);
                    };
                    if (typeof setTimeout === 'function') {
                        setTimeout(deliver, 0);
                    } else {
                        deliver();
                    }
                };

                (function () {
                    function MessagePort() {
                        this.onmessage = null;
                        this._fenListeners = [];
                        this._fenPeer = null;
                        this._fenClosed = false;
                    }

                    MessagePort.prototype.postMessage = function (data) {
                        var target = this._fenPeer;
                        if (!target || target._fenClosed) return;
                        var event = new MessageEvent('message', { data: data, source: this, ports: [] });
                        event.target = target;
                        event.currentTarget = target;
                        var deliver = function () {
                            if (target._fenClosed) return;
                            if (typeof target.onmessage === 'function') {
                                target.onmessage.call(target, event);
                            }
                            var listeners = target._fenListeners.slice();
                            for (var i = 0; i < listeners.length; i++) {
                                listeners[i].call(target, event);
                            }
                        };
                        if (typeof setTimeout === 'function') {
                            setTimeout(deliver, 0);
                        } else {
                            deliver();
                        }
                    };
                    MessagePort.prototype.start = function () {};
                    MessagePort.prototype.close = function () {
                        this._fenClosed = true;
                        this._fenListeners.length = 0;
                    };
                    MessagePort.prototype.addEventListener = function (type, callback) {
                        if (type !== 'message' || typeof callback !== 'function') return;
                        if (this._fenListeners.indexOf(callback) < 0) {
                            this._fenListeners.push(callback);
                        }
                    };
                    MessagePort.prototype.removeEventListener = function (type, callback) {
                        if (type !== 'message') return;
                        for (var i = this._fenListeners.length - 1; i >= 0; i--) {
                            if (this._fenListeners[i] === callback) this._fenListeners.splice(i, 1);
                        }
                    };

                    globalThis.MessagePort = MessagePort;
                    globalThis.MessageChannel = function MessageChannel() {
                        this.port1 = new MessagePort();
                        this.port2 = new MessagePort();
                        this.port1._fenPeer = this.port2;
                        this.port2._fenPeer = this.port1;
                    };
                })();

                // ── XMLHttpRequest ── https://xhr.spec.whatwg.org/
                // Amazon and many sites use XHR for API calls.  Stub that fires
                // onerror immediately so callers can handle the failure gracefully.
                if (typeof globalThis.Worker === 'undefined') {
                    globalThis.Worker = function Worker(scriptURL, options) {
                        if (!(this instanceof Worker)) {
                            throw new TypeError("Failed to construct 'Worker': Please use the 'new' operator.");
                        }

                        this.scriptURL = String(scriptURL || '');
                        this.name = options && options.name ? String(options.name) : '';
                        this.onmessage = null;
                        this.onerror = null;
                        this.onmessageerror = null;
                        this._fenListeners = {};
                        this._fenTerminated = false;
                    };
                    Worker.prototype.postMessage = function (data) {
                        if (this._fenTerminated) return;
                    };
                    Worker.prototype.terminate = function () {
                        this._fenTerminated = true;
                        this._fenListeners = {};
                    };
                    Worker.prototype.addEventListener = function (type, callback) {
                        if (typeof callback !== 'function') return;
                        type = String(type || '');
                        var listeners = this._fenListeners[type] || (this._fenListeners[type] = []);
                        if (listeners.indexOf(callback) < 0) listeners.push(callback);
                    };
                    Worker.prototype.removeEventListener = function (type, callback) {
                        type = String(type || '');
                        var listeners = this._fenListeners[type];
                        if (!listeners) return;
                        for (var i = listeners.length - 1; i >= 0; i--) {
                            if (listeners[i] === callback) listeners.splice(i, 1);
                        }
                    };
                    Worker.prototype.dispatchEvent = function (event) {
                        if (!event || !event.type) return true;
                        event.target = this;
                        event.currentTarget = this;
                        var handler = this['on' + event.type];
                        if (typeof handler === 'function') handler.call(this, event);
                        var listeners = (this._fenListeners[event.type] || []).slice();
                        for (var i = 0; i < listeners.length; i++) {
                            listeners[i].call(this, event);
                        }
                        return true;
                    };
                }

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

                globalThis.CustomElementRegistry = function CustomElementRegistry() {
                    this._registry = Object.create(null);
                    this._whenDefined = Object.create(null);
                };
                function normalizeCustomElementName(name) {
                    return String(name || '').toLowerCase();
                }
                function findCustomElementMatches(root, name, options) {
                    var matches = [];
                    if (!root) return matches;
                    var localName = options && options.extends
                        ? String(options.extends).toLowerCase()
                        : name;
                    var isMatch = function (node) {
                        if (!node || node.nodeType !== Node.ELEMENT_NODE || typeof node.tagName !== 'string') return false;
                        if (String(node.tagName).toLowerCase() !== localName) return false;
                        return !(options && options.extends) || node.getAttribute('is') === name;
                    };
                    if (isMatch(root)) matches.push(root);
                    if (typeof root.querySelectorAll === 'function') {
                        var selector = options && options.extends
                            ? localName + '[is="' + name.replace(/"/g, '\\"') + '"]'
                            : name;
                        var list;
                        try {
                            list = root.querySelectorAll(selector);
                        } catch (_selectorError) {
                            list = root.querySelectorAll('*');
                        }
                        for (var i = 0; list && i < list.length; i++) {
                            if (isMatch(list[i])) matches.push(list[i]);
                        }
                    }
                    return matches;
                }
                function copyCustomElementPrototype(target, prototype, includeAccessors) {
                    var chain = [];
                    var stop = [HTMLElement.prototype, Element.prototype, Node.prototype, Object.prototype];
                    for (var proto = prototype; proto && stop.indexOf(proto) < 0; proto = Object.getPrototypeOf(proto)) {
                        chain.unshift(proto);
                    }
                    for (var c = 0; c < chain.length; c++) {
                        var names = Object.getOwnPropertyNames(chain[c]);
                        for (var i = 0; i < names.length; i++) {
                            var key = names[i];
                            if (key === 'constructor') continue;
                            var descriptor = Object.getOwnPropertyDescriptor(chain[c], key);
                            if (!includeAccessors && descriptor && !Object.prototype.hasOwnProperty.call(descriptor, 'value')) {
                                continue;
                            }
                            if (descriptor) {
                                try { Object.defineProperty(target, key, descriptor); } catch (_defineError) {}
                            }
                        }
                    }
                }
                function copyCustomElementInstanceState(target, source) {
                    if (!source) return;
                    var names = Object.getOwnPropertyNames(source);
                    for (var i = 0; i < names.length; i++) {
                        var key = names[i];
                        var descriptor = Object.getOwnPropertyDescriptor(source, key);
                        if (!descriptor) continue;
                        try {
                            Object.defineProperty(target, key, descriptor);
                        } catch (_defineStateError) {
                            try { target[key] = source[key]; } catch (_assignStateError) {}
                        }
                    }
                    if (typeof Object.getOwnPropertySymbols === 'function') {
                        var symbols = Object.getOwnPropertySymbols(source);
                        for (var s = 0; s < symbols.length; s++) {
                            var symbol = symbols[s];
                            var symbolDescriptor = Object.getOwnPropertyDescriptor(source, symbol);
                            if (!symbolDescriptor) continue;
                            try {
                                Object.defineProperty(target, symbol, symbolDescriptor);
                            } catch (_defineSymbolStateError) {
                                try { target[symbol] = source[symbol]; } catch (_assignSymbolStateError) {}
                            }
                        }
                    }
                }
                function isBuiltInCustomElementPrototype(prototype) {
                    return prototype === HTMLElement.prototype ||
                        prototype === Element.prototype ||
                        prototype === Node.prototype ||
                        prototype === Object.prototype;
                }
                function selectConstructedCustomElementPrototype(element, fallbackPrototype) {
                    try {
                        var constructedPrototype = Object.getPrototypeOf(element);
                        if (constructedPrototype && !isBuiltInCustomElementPrototype(constructedPrototype)) {
                            return constructedPrototype;
                        }
                    } catch (_prototypeReadError) {}
                    return fallbackPrototype;
                }
                function applyConstructedCustomElementInstance(element, instance) {
                    if (!instance ||
                        instance === element ||
                        (typeof instance !== 'object' && typeof instance !== 'function')) {
                        return;
                    }

                    var instancePrototype = selectConstructedCustomElementPrototype(instance, null);
                    if (instancePrototype) {
                        try { Object.setPrototypeOf(element, instancePrototype); } catch (_instancePrototypeError) {}
                    }
                    copyCustomElementInstanceState(element, instance);
                }
                function isRecoverableCustomElementCallError(error) {
                    var message = error && error.message ? String(error.message) : String(error);
                    return message.indexOf('Class constructor') >= 0 ||
                        message.indexOf("cannot be invoked without 'new'") >= 0 ||
                        message.indexOf('cannot be called without new') >= 0 ||
                        message.indexOf('Illegal constructor') >= 0 ||
                        message.indexOf('Illegal invocation') >= 0;
                }
                function constructCustomElement(element, entry) {
                    try {
                        var returned = entry.constructor.call(element);
                        applyConstructedCustomElementInstance(element, returned);
                        return true;
                    } catch (_callError) {
                        if (!isRecoverableCustomElementCallError(_callError)) {
                            throw _callError;
                        }

                        var previousConstructionElement = globalThis.__fenCustomElementConstructionElement;
                        globalThis.__fenCustomElementConstructionElement = element;
                        var instance;
                        try {
                            instance = new entry.constructor();
                        } finally {
                            if (previousConstructionElement) {
                                globalThis.__fenCustomElementConstructionElement = previousConstructionElement;
                            } else {
                                delete globalThis.__fenCustomElementConstructionElement;
                            }
                        }
                        applyConstructedCustomElementInstance(element, instance);
                        return true;
                    }
                }
                function describeCustomElementThrownValue(error, depth) {
                    if (depth > 2) return String(error);
                    if (error === null) return 'null';
                    var type = typeof error;
                    if (type !== 'object' && type !== 'function') return String(error);

                    var parts = [];
                    try {
                        if (error.name) parts.push(String(error.name));
                    } catch (_nameError) {}
                    try {
                        if (error.message) parts.push(String(error.message));
                    } catch (_messageError) {}

                    var names = [];
                    try {
                        names = Object.getOwnPropertyNames(error);
                    } catch (_ownNamesError) {}
                    for (var i = 0; i < names.length && i < 12; i++) {
                        var key = names[i];
                        if (key === 'name' || key === 'message' || key === 'stack') continue;
                        var value;
                        try {
                            value = error[key];
                        } catch (_valueError) {
                            value = '<unreadable>';
                        }
                        var rendered;
                        try {
                            if (value === null) {
                                rendered = 'null';
                            } else if (typeof value === 'object' || typeof value === 'function') {
                                rendered = describeCustomElementThrownValue(value, depth + 1);
                            } else {
                                rendered = String(value);
                            }
                        } catch (_renderError) {
                            rendered = Object.prototype.toString.call(value);
                        }
                        parts.push(key + '=' + rendered);
                    }

                    if (!parts.length) {
                        try {
                            var json = JSON.stringify(error);
                            if (json) parts.push(json);
                        } catch (_jsonError) {}
                    }
                    if (!parts.length) {
                        try {
                            parts.push(Object.prototype.toString.call(error));
                        } catch (_tagError) {
                            parts.push(String(error));
                        }
                    }

                    try {
                        if (error.stack) parts.push(String(error.stack));
                    } catch (_stackError) {}

                    return parts.join(': ');
                }
                function reportCustomElementReactionError(name, phase, error) {
                    try {
                        if (globalThis.console && typeof globalThis.console.error === 'function') {
                            var message = describeCustomElementThrownValue(error, 0);
                            globalThis.console.error("Custom element '" + name + "' " + phase + " failed: " + message);
                        }
                    } catch (_reportError) {}
                }
                function describeCustomElementNodeForProbe(node) {
                    if (!node) return String(node);
                    var parts = [];
                    try { parts.push(String(node.nodeName)); } catch (_nodeNameError) {}
                    try { if (node.id) parts.push('#' + String(node.id)); } catch (_idError) {}
                    try { if (node.localName) parts.push('local=' + String(node.localName)); } catch (_localNameError) {}
                    try { parts.push('type=' + String(node.nodeType)); } catch (_nodeTypeError) {}
                    try {
                        var parent = node.parentNode;
                        parts.push('parent=' + (parent ? String(parent.nodeName) : 'null'));
                    } catch (_parentError) {}
                    try {
                        var childNodes = node.childNodes;
                        var childLength = childNodes && typeof childNodes.length !== 'undefined'
                            ? Number(childNodes.length)
                            : -1;
                        parts.push('children=' + String(childLength));
                        if (childLength > 0) {
                            var childParts = [];
                            var maxChildren = childLength < 6 ? childLength : 6;
                            for (var childIndex = 0; childIndex < maxChildren; childIndex++) {
                                var childNode = childNodes[childIndex];
                                var childLabel = '';
                                try { childLabel += String(childNode.nodeName); } catch (_childNodeNameError) { childLabel += '?'; }
                                try { if (childNode.id) childLabel += '#' + String(childNode.id); } catch (_childIdError) {}
                                try { if (childNode.localName) childLabel += '[' + String(childNode.localName) + ']'; } catch (_childLocalNameError) {}
                                childParts.push(childLabel);
                            }
                            parts.push('childList=' + childParts.join('|'));
                        }
                    } catch (_childrenError) {}
                    return parts.join(':');
                }
                function probeCustomElementMutation(kind, parent, child) {
                    var probe = {
                        kind: kind,
                        parent: describeCustomElementNodeForProbe(parent),
                        child: describeCustomElementNodeForProbe(child),
                        same: parent === child
                    };
                    try { probe.parentContainsChild = !!(parent && typeof parent.contains === 'function' && parent.contains(child)); } catch (_parentContainsError) { probe.parentContainsChild = 'throws'; }
                    try { probe.childContainsParent = !!(child && typeof child.contains === 'function' && child.contains(parent)); } catch (_childContainsError) { probe.childContainsParent = 'throws'; }
                    try { probe.childParent = describeCustomElementNodeForProbe(child && child.parentNode); } catch (_childParentError) {}
                    try { probe.stack = String((new Error()).stack || '').slice(0, 900); } catch (_stackProbeError) {}
                    return probe;
                }
                function describeCustomElementLifecycleStateForProbe(element) {
                    var state = [];
                    function push(label, value) {
                        try { state.push(label + '=' + String(value)); } catch (_pushError) {}
                    }
                    push('element', describeCustomElementNodeForProbe(element));
                    try { push('own', Object.getOwnPropertyNames(element).slice(0, 18).join(',')); } catch (_ownError) {}
                    var shadow = null;
                    try { shadow = element.shadowRoot; push('shadowRoot', describeCustomElementNodeForProbe(shadow)); } catch (_shadowError) { push('shadowRoot', '<throws>'); }
                    try { push('shadowOwn', shadow ? Object.getOwnPropertyNames(shadow).slice(0, 18).join(',') : 'null'); } catch (_shadowOwnError) {}
                    try { push('shadowRoot.root', describeCustomElementNodeForProbe(shadow && shadow.root)); } catch (_shadowRootError) {}
                    try { push('shadowRoot.host', describeCustomElementNodeForProbe(shadow && shadow.host)); } catch (_shadowHostError) {}
                    try { push('element.root', describeCustomElementNodeForProbe(element.root)); } catch (_elementRootError) {}
                    try { push('element.$$', element.$$ ? 'present' : 'missing'); } catch (_dollarError) {}
                    try { push('polymerController', element.polymerController ? 'present' : 'missing'); } catch (_polymerControllerError) {}
                    try { push('appendSource', shadow && shadow.appendChild ? String(shadow.appendChild).slice(0, 160) : '<none>'); } catch (_appendSourceError) {}
                    return state.join('; ');
                }
                function invokeCustomElementConnectedCallback(element) {
                    var previousAppendChild = Node && Node.prototype ? Node.prototype.appendChild : null;
                    var previousInsertBefore = Node && Node.prototype ? Node.prototype.insertBefore : null;
                    var previousReplaceChild = Node && Node.prototype ? Node.prototype.replaceChild : null;
                    var lastProbe = null;
                    function attachProbe(error, probe) {
                        var effectiveProbe = probe || lastProbe;
                        if (!effectiveProbe) {
                            try { effectiveProbe = globalThis.__fenLastHostMethodProbe; } catch (_lastHostProbeError) {}
                        }
                        try {
                            Object.defineProperty(error, '__fenMutationProbe', {
                                value: effectiveProbe || null,
                                configurable: true
                            });
                        } catch (_attachProbeError) {}
                    }
                    try {
                        if (typeof previousAppendChild === 'function') {
                            Node.prototype.appendChild = function (child) {
                                var probe = probeCustomElementMutation('appendChild', this, child);
                                lastProbe = probe;
                                try {
                                    return previousAppendChild.apply(this, arguments);
                                } catch (error) {
                                    attachProbe(error, probe);
                                    throw error;
                                }
                            };
                        }
                        if (typeof previousInsertBefore === 'function') {
                            Node.prototype.insertBefore = function (child, reference) {
                                var probe = probeCustomElementMutation('insertBefore', this, child);
                                try { probe.reference = describeCustomElementNodeForProbe(reference); } catch (_referenceError) {}
                                lastProbe = probe;
                                try {
                                    return previousInsertBefore.apply(this, arguments);
                                } catch (error) {
                                    attachProbe(error, probe);
                                    throw error;
                                }
                            };
                        }
                        if (typeof previousReplaceChild === 'function') {
                            Node.prototype.replaceChild = function (child, oldChild) {
                                var probe = probeCustomElementMutation('replaceChild', this, child);
                                try { probe.oldChild = describeCustomElementNodeForProbe(oldChild); } catch (_oldChildError) {}
                                lastProbe = probe;
                                try {
                                    return previousReplaceChild.apply(this, arguments);
                                } catch (error) {
                                    attachProbe(error, probe);
                                    throw error;
                                }
                            };
                        }
                        return element.connectedCallback();
                    } catch (error) {
                        attachProbe(error, lastProbe);
                        try {
                            Object.defineProperty(error, '__fenLifecycleState', {
                                value: describeCustomElementLifecycleStateForProbe(element),
                                configurable: true
                            });
                        } catch (_lifecycleStateProbeError) {}
                        throw error;
                    } finally {
                        if (previousAppendChild) Node.prototype.appendChild = previousAppendChild;
                        if (previousInsertBefore) Node.prototype.insertBefore = previousInsertBefore;
                        if (previousReplaceChild) Node.prototype.replaceChild = previousReplaceChild;
                    }
                }
                function upgradeCustomElement(element, name, entry) {
                    if (!element) return;
                    var alreadyUpgraded = element.__fenCustomElementName === name;
                    if (!alreadyUpgraded) {
                        Object.defineProperty(element, '__fenCustomElementName', {
                            value: name,
                            configurable: true
                        });
                        if (entry.constructor && entry.constructor.prototype) {
                            copyCustomElementPrototype(element, entry.constructor.prototype, false);
                        }
                        if (!element.__fenCustomElementConstructed && typeof entry.constructor === 'function') {
                            try {
                                constructCustomElement(element, entry);
                                if (entry.constructor && entry.constructor.prototype) {
                                    var effectivePrototype = selectConstructedCustomElementPrototype(element, entry.constructor.prototype);
                                    if (effectivePrototype === entry.constructor.prototype) {
                                        try { Object.setPrototypeOf(element, entry.constructor.prototype); } catch (_setPrototypeError) {}
                                    }
                                    copyCustomElementPrototype(element, effectivePrototype, true);
                                }
                                Object.defineProperty(element, '__fenCustomElementConstructed', {
                                    value: true,
                                    configurable: true
                                });
                            } catch (_constructorError) {
                                reportCustomElementReactionError(name, 'constructor', _constructorError);
                                return;
                            }
                        }
                    }
                    if (element.isConnected &&
                        !element.__fenCustomElementConnected &&
                        typeof element.connectedCallback === 'function') {
                        Object.defineProperty(element, '__fenCustomElementConnected', {
                            value: true,
                            configurable: true
                        });
                        try {
                            invokeCustomElementConnectedCallback(element);
                        } catch (_connectedError) {
                            reportCustomElementReactionError(name, 'connectedCallback', _connectedError);
                        }
                    }
                }
                function upgradeCustomElementTree(root, registry, filterName) {
                    if (!root || !registry) return;
                    var names = filterName ? [filterName] : Object.keys(registry._registry);
                    for (var i = 0; i < names.length; i++) {
                        var name = names[i];
                        var entry = registry._registry[name];
                        if (!entry) continue;
                        var matches = findCustomElementMatches(root, name, entry.options);
                        for (var j = 0; j < matches.length; j++) {
                            upgradeCustomElement(matches[j], name, entry);
                        }
                    }
                }
                CustomElementRegistry.prototype.define = function (name, constructor, options) {
                    name = normalizeCustomElementName(name);
                    if (this._registry[name]) {
                        throw new DOMException("Failed to execute 'define': '" + name + "' has already been defined.", "NotSupportedError");
                    }
                    var localName = options && options.extends
                        ? String(options.extends).toLowerCase()
                        : name;
                    try {
                        Object.defineProperty(constructor, '__fenCustomElementName', {
                            value: name,
                            configurable: true
                        });
                        Object.defineProperty(constructor, '__fenCustomElementLocalName', {
                            value: localName,
                            configurable: true
                        });
                    } catch (_constructorMarkerError) {}
                    this._registry[name] = { constructor: constructor, options: options };
                    if (typeof document !== 'undefined') {
                        upgradeCustomElementTree(document, this, name);
                    }
                    var waiters = this._whenDefined[name];
                    if (waiters) {
                        for (var i = 0; i < waiters.length; i++) {
                            waiters[i](constructor);
                        }
                        delete this._whenDefined[name];
                    }
                };
                CustomElementRegistry.prototype.get = function (name) {
                    name = normalizeCustomElementName(name);
                    var entry = this._registry[name];
                    return entry ? entry.constructor : undefined;
                };
                CustomElementRegistry.prototype.whenDefined = function (name) {
                    name = normalizeCustomElementName(name);
                    if (this._registry[name]) {
                        return Promise.resolve(this._registry[name].constructor);
                    }
                    var registry = this;
                    return new Promise(function (resolve) {
                        (registry._whenDefined[name] || (registry._whenDefined[name] = [])).push(resolve);
                    });
                };
                CustomElementRegistry.prototype.upgrade = function (root) {
                    upgradeCustomElementTree(root, this);
                };
                globalThis.customElements = new CustomElementRegistry();

                // ── DOMException ──
                globalThis.DOMException = function DOMException(message, name) {
                    this.message = message || '';
                    this.name = name || 'Error';
                };
                DOMException.prototype = Object.create(Error.prototype);
                DOMException.prototype.constructor = DOMException;

                // Minimal queued ReadableStream/reader implementation for site
                // bootstrap code that constructs streams or checks the global.
                function ReadableStreamDefaultController(stream) {
                    this._stream = stream;
                }
                ReadableStreamDefaultController.prototype.enqueue = function (chunk) {
                    var stream = this._stream;
                    if (stream._closed) {
                        throw new TypeError('Cannot enqueue into a closed ReadableStream.');
                    }
                    if (stream._pendingReads.length) {
                        stream._pendingReads.shift().resolve({ value: chunk, done: false });
                    } else {
                        stream._queue.push(chunk);
                    }
                };
                ReadableStreamDefaultController.prototype.close = function () {
                    var stream = this._stream;
                    if (stream._closed) return;
                    stream._closed = true;
                    while (stream._pendingReads.length) {
                        stream._pendingReads.shift().resolve({ value: undefined, done: true });
                    }
                };
                ReadableStreamDefaultController.prototype.error = function (reason) {
                    var stream = this._stream;
                    stream._error = reason || new TypeError('ReadableStream error');
                    stream._closed = true;
                    while (stream._pendingReads.length) {
                        stream._pendingReads.shift().reject(stream._error);
                    }
                };

                function ReadableStreamDefaultReader(stream) {
                    if (!(stream instanceof ReadableStream)) {
                        throw new TypeError('ReadableStream reader requires a stream.');
                    }
                    if (stream._reader) {
                        throw new TypeError('ReadableStream is already locked.');
                    }
                    this._stream = stream;
                    stream._reader = this;
                }
                ReadableStreamDefaultReader.prototype.read = function () {
                    var stream = this._stream;
                    if (!stream) {
                        return Promise.reject(new TypeError('ReadableStream reader lock has been released.'));
                    }
                    stream._disturbed = true;
                    if (stream._error) {
                        return Promise.reject(stream._error);
                    }
                    if (stream._queue.length) {
                        return Promise.resolve({ value: stream._queue.shift(), done: false });
                    }
                    if (stream._closed || stream._cancelled) {
                        return Promise.resolve({ value: undefined, done: true });
                    }
                    return new Promise(function (resolve, reject) {
                        stream._pendingReads.push({ resolve: resolve, reject: reject });
                    });
                };
                ReadableStreamDefaultReader.prototype.cancel = function (reason) {
                    var stream = this._stream;
                    if (!stream) return Promise.resolve(undefined);
                    return stream.cancel(reason);
                };
                ReadableStreamDefaultReader.prototype.releaseLock = function () {
                    if (this._stream && this._stream._reader === this) {
                        this._stream._reader = null;
                    }
                    this._stream = null;
                };

                globalThis.ReadableStream = function ReadableStream(underlyingSource, strategy) {
                    this._queue = [];
                    this._pendingReads = [];
                    this._reader = null;
                    this._closed = false;
                    this._cancelled = false;
                    this._disturbed = false;
                    this._error = null;
                    var controller = new ReadableStreamDefaultController(this);
                    if (underlyingSource && typeof underlyingSource.start === 'function') {
                        var startResult = underlyingSource.start(controller);
                        if (startResult && typeof startResult.then === 'function') {
                            startResult.catch(controller.error.bind(controller));
                        }
                    }
                };
                Object.defineProperty(ReadableStream.prototype, 'locked', {
                    get: function () { return !!this._reader; }
                });
                ReadableStream.prototype.getReader = function (options) {
                    return new ReadableStreamDefaultReader(this);
                };
                ReadableStream.prototype.cancel = function (reason) {
                    this._cancelled = true;
                    this._closed = true;
                    this._queue.length = 0;
                    while (this._pendingReads.length) {
                        this._pendingReads.shift().resolve({ value: undefined, done: true });
                    }
                    return Promise.resolve(undefined);
                };
                ReadableStream.prototype.tee = function () {
                    var source = this;
                    var snapshot = source._queue.slice();
                    return [
                        new ReadableStream({ start: function (controller) { snapshot.forEach(function (chunk) { controller.enqueue(chunk); }); if (source._closed) controller.close(); } }),
                        new ReadableStream({ start: function (controller) { snapshot.forEach(function (chunk) { controller.enqueue(chunk); }); if (source._closed) controller.close(); } })
                    ];
                };
                ReadableStream.prototype.pipeTo = function (destination) {
                    var reader = this.getReader();
                    function pump() {
                        return reader.read().then(function (record) {
                            if (record.done) return undefined;
                            if (destination && typeof destination.write === 'function') {
                                destination.write(record.value);
                            }
                            return pump();
                        });
                    }
                    return pump();
                };
                ReadableStream.prototype.pipeThrough = function (transform) {
                    if (transform && transform.readable) return transform.readable;
                    return this;
                };
                globalThis.ReadableStreamDefaultReader = ReadableStreamDefaultReader;
                globalThis.ReadableStreamDefaultController = ReadableStreamDefaultController;

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

                // ── TextEncoder ── https://encoding.spec.whatwg.org/#textencoder
                globalThis.TextEncoder = function TextEncoder() {};
                TextEncoder.prototype.encode = function (input) {
                    if (input == null) input = '';
                    return __fenTextEncode(String(input));
                };
                TextEncoder.prototype.encoding = 'utf-8';
                TextEncoder.prototype.encodeInto = function (source, destination) {
                    // Minimal stub: encode and copy into destination Uint8Array.
                    var encoded = __fenTextEncode(String(source));
                    var written = Math.min(encoded.length, destination.length);
                    for (var i = 0; i < written; i++) destination[i] = encoded[i];
                    return { read: source.length, written: written };
                };

                // ── TextDecoder ── https://encoding.spec.whatwg.org/#textdecoder
                globalThis.TextDecoder = function TextDecoder(label, options) {
                    this.encoding = 'utf-8';
                    this.fatal = !!(options && options.fatal);
                    this.ignoreBOM = !!(options && options.ignoreBOM);
                };
                TextDecoder.prototype.decode = function (input, options) {
                    if (input == null) return '';
                    return __fenTextDecode(input);
                };

                // ── atob / btoa ── https://html.spec.whatwg.org/#atob
                globalThis.atob = function (data) {
                    if (typeof data !== 'string') throw new DOMException('atob: argument must be a string', 'InvalidCharacterError');
                    var arr = __fenAtob(data);
                    // Return a binary string (latin1-encoded) as required by the spec.
                    var result = '';
                    for (var i = 0; i < arr.length; i++) result += String.fromCharCode(arr[i]);
                    return result;
                };
                globalThis.btoa = function (data) {
                    if (typeof data !== 'string') throw new DOMException('btoa: argument must be a string', 'InvalidCharacterError');
                    // Convert each char code to a byte.
                    var bytes = [];
                    for (var i = 0; i < data.length; i++) {
                        var cp = data.charCodeAt(i);
                        if (cp > 255) throw new DOMException('btoa: string contains non-Latin1 character', 'InvalidCharacterError');
                        bytes.push(cp);
                    }
                    return __fenBtoa(bytes);
                };

                // ── crypto.subtle ── https://w3c.github.io/webcrypto/
                var subtle = {};
                subtle.importKey = function (format, keyData, algorithm, extractable, keyUsages) {
                    return Promise.resolve(__fenCryptoImportKey(format, keyData, algorithm, extractable, keyUsages));
                };
                subtle.decrypt = function (algorithm, key, data) {
                    return Promise.resolve(__fenCryptoDecrypt(algorithm, key, data));
                };
                subtle.encrypt = function (algorithm, key, data) {
                    // Stub: symmetric encrypt = decrypt for AES-CBC with same key (not generally true, but sufficient)
                    return Promise.resolve(__fenCryptoDecrypt(algorithm, key, data));
                };
                subtle.digest = function (algorithm, data) {
                    return Promise.resolve(__fenCryptoDigest(algorithm, data));
                };
                globalThis.crypto = globalThis.crypto || {};
                globalThis.crypto.subtle = subtle;
                globalThis.crypto.getRandomValues = globalThis.crypto.getRandomValues || function (array) {
                    if (!array || typeof array.length !== 'number') {
                        throw new TypeError("Failed to execute 'getRandomValues': argument must be an integer typed array.");
                    }
                    for (var i = 0; i < array.length; i++) {
                        array[i] = __fenRandomByte();
                    }
                    return array;
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
                    return CreateComputedStyleObject(new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase));
                }

                var element = ResolveHostObjectOrNull<Element>(args[0]);
                if (element == null)
                {
                    return CreateComputedStyleObject(new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase));
                }

                return CreateComputedStyleObjectForElement(element);
            },
            length: 1);

        _interpreter.RegisterGlobalValue("getComputedStyle", getComputedStyleFn);
    }

    private JsValue CreateComputedStyleObjectForElement(Element element)
    {
        if (element == null)
        {
            return CreateComputedStyleObject(new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase));
        }

        var cs = element.GetComputedStyle();
        if (cs == null)
        {
            return CreateComputedStyleObject(new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase));
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
        if (cs.ForegroundColor.HasValue) props["color"] = JsValue.FromString(CssParser.ResolveCurrentColor(cs.ForegroundColor.Value, cs.ForegroundColor).ToString());
        if (cs.BackgroundColor.HasValue) props["backgroundColor"] = JsValue.FromString(CssParser.ResolveCurrentColor(cs.BackgroundColor.Value, cs.ForegroundColor).ToString());
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

        return CreateComputedStyleObject(props);
    }

    private JsValue CreateComputedStyleObject(Dictionary<string, JsValue> props)
    {
        props ??= new Dictionary<string, JsValue>(StringComparer.OrdinalIgnoreCase);
        var styleObject = _interpreter.AllocateObject(props);
        _interpreter.SetObjectProperty(
            styleObject,
            "getPropertyValue",
            _interpreter.AllocateNativeFunction(
                "getPropertyValue",
                (_, propertyArgs) =>
                {
                    var propertyName = propertyArgs.Count > 0 ? CoerceToHostString(propertyArgs[0]) : string.Empty;
                    if (string.IsNullOrWhiteSpace(propertyName))
                    {
                        return JsValue.FromString(string.Empty);
                    }

                    if (props.TryGetValue(propertyName, out var directValue))
                    {
                        return directValue;
                    }

                    var kebabName = CamelToCssProp(propertyName);
                    if (!string.Equals(kebabName, propertyName, StringComparison.Ordinal) &&
                        props.TryGetValue(kebabName, out var kebabValue))
                    {
                        return kebabValue;
                    }

                    var camelName = CssPropToCamel(propertyName);
                    if (!string.Equals(camelName, propertyName, StringComparison.Ordinal) &&
                        props.TryGetValue(camelName, out var camelValue))
                    {
                        return camelValue;
                    }

                    return JsValue.FromString(string.Empty);
                },
                length: 1),
            enumerable: true);
        return styleObject;
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

    // ── TextEncoder / TextDecoder helpers ──

    private JsValue CreateUint8ArrayFromBytes(byte[] bytes)
    {
        if (bytes == null) bytes = Array.Empty<byte>();
        // Return a dense integer-indexed array (quacks like Uint8Array enough for
        // indexed access and .length). The WAF challenge uses integer indexing
        // rather than instanceof checks, so this suffices for crypto interop.
        var elements = new JsValue[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            elements[i] = JsValue.FromInt32(bytes[i]);
        return _interpreter.AllocateArray(elements);
    }

    private byte[] ExtractBytesFromArrayLike(JsValue value)
    {
        if (value.Tag == JsValueTag.Undefined || value.Tag == JsValueTag.Null)
            return Array.Empty<byte>();
        if (value.Tag != JsValueTag.Object)
            return Array.Empty<byte>();
        try
        {
            var obj = _interpreter.Heap.GetObject(value.AsObjectHandle());
            if (obj == null) return Array.Empty<byte>();
            // Try to get the "length" property.
            var context = (IBuiltinContext)_interpreter;
            if (!context.TryGetPropertyValue(obj, value, "length", out var lengthVal))
                return Array.Empty<byte>();
            var len = (int)lengthVal.AsNumber();
            if (len <= 0 || len > 1024 * 1024) return Array.Empty<byte>();
            var bytes = new byte[len];
            for (var i = 0; i < len; i++)
            {
                if (context.TryGetPropertyValue(obj, value, i.ToString(CultureInfo.InvariantCulture), out var byteVal))
                    bytes[i] = (byte)((int)byteVal.AsNumber() & 0xFF);
            }
            return bytes;
        }
        catch { return Array.Empty<byte>(); }
    }

    // ── crypto.subtle helpers ──

    // Per-engine crypto key storage (keys imported via crypto.subtle.importKey).
    private readonly Dictionary<long, byte[]> _cryptoKeyStore = new();
    private long _cryptoKeyIdCounter;

    private JsValue ImportCryptoKey(IReadOnlyList<JsValue> args)
    {
        // args: [format, keyData, algorithm, extractable, keyUsages]
        // format: "raw" — only format supported currently
        // keyData: Uint8Array containing the key bytes
        // algorithm: { name: "AES-CBC" }
        try
        {
            var format = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
            var keyData = args.Count > 1 ? ExtractBytesFromArrayLike(args[1]) : Array.Empty<byte>();
            var extractable = args.Count > 3 && args[3].Tag == JsValueTag.Boolean && args[3].AsBoolean();

            if (format != "raw" || keyData.Length == 0)
            {
                return _interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["type"] = JsValue.FromString("secret"),
                    ["extractable"] = JsValue.FromBoolean(extractable),
                    ["algorithm"] = _interpreter.AllocateObject(new Dictionary<string, JsValue>
                    {
                        ["name"] = JsValue.FromString("AES-CBC")
                    }),
                    ["usages"] = _interpreter.AllocateArray(Array.Empty<JsValue>())
                });
            }

            var keyId = Interlocked.Increment(ref _cryptoKeyIdCounter);
            lock (_cryptoKeyStore) { _cryptoKeyStore[keyId] = keyData; }

            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["type"] = JsValue.FromString("secret"),
                ["extractable"] = JsValue.FromBoolean(extractable),
                ["algorithm"] = _interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["name"] = JsValue.FromString("AES-CBC")
                }),
                ["usages"] = _interpreter.AllocateArray(Array.Empty<JsValue>()),
                ["_fenKeyId"] = JsValue.FromNumber(keyId),
                ["_fenKeyLen"] = JsValue.FromNumber(keyData.Length)
            });
        }
        catch
        {
            return JsValue.Undefined;
        }
    }

    private JsValue CryptoDecrypt(IReadOnlyList<JsValue> args)
    {
        // args: [algorithm, key, data]
        // algorithm: { name: "AES-CBC", iv: Uint8Array }
        // key: object from importKey (carries _fenKeyId)
        // data: Uint8Array containing ciphertext
        try
        {
            var algorithm = args.Count > 0 ? args[0] : JsValue.Undefined;
            var key = args.Count > 1 ? args[1] : JsValue.Undefined;
            var data = args.Count > 2 ? ExtractBytesFromArrayLike(args[2]) : Array.Empty<byte>();

            if (data.Length == 0) return CreateUint8ArrayFromBytes(Array.Empty<byte>());

            // Extract IV from algorithm object.
            byte[] iv = new byte[16];
            if (algorithm.Tag == JsValueTag.Object)
            {
                var algoObj = _interpreter.Heap.GetObject(algorithm.AsObjectHandle());
                var context = (IBuiltinContext)_interpreter;
                if (context.TryGetPropertyValue(algoObj, algorithm, "iv", out var ivVal))
                {
                    var extractedIv = ExtractBytesFromArrayLike(ivVal);
                    if (extractedIv.Length > 0) iv = extractedIv;
                }
            }

            // Look up key bytes from the key store or extract directly.
            byte[] aesKey = iv; // fallback
            if (key.Tag == JsValueTag.Object)
            {
                var keyObj = _interpreter.Heap.GetObject(key.AsObjectHandle());
                if (keyObj.TryGetOwnProperty("_fenKeyId", out var keyIdDesc))
                {
                    var kid = (long)keyIdDesc.Value.AsNumber();
                    lock (_cryptoKeyStore)
                    {
                        if (_cryptoKeyStore.TryGetValue(kid, out var stored))
                            aesKey = stored;
                    }
                }
                if (aesKey == iv) // fallback still in place
                {
                    var rawFromKey = ExtractBytesFromArrayLike(key);
                    if (rawFromKey.Length > 0) aesKey = rawFromKey;
                }
            }

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(data, 0, data.Length);
            return CreateUint8ArrayFromBytes(decrypted);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] CryptoDecrypt failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
            var origData = args.Count > 2 ? ExtractBytesFromArrayLike(args[2]) : Array.Empty<byte>();
            return CreateUint8ArrayFromBytes(origData);
        }
    }

    private JsValue CryptoDigest(IReadOnlyList<JsValue> args)
    {
        // args: [algorithm, data]
        // algorithm: "SHA-256" or "SHA-1" or "SHA-384" or "SHA-512"
        try
        {
            var algoName = args.Count > 0 ? CoerceToHostString(args[0]) : "SHA-256";
            var data = args.Count > 1 ? ExtractBytesFromArrayLike(args[1]) : Array.Empty<byte>();

            if (data.Length == 0) return CreateUint8ArrayFromBytes(Array.Empty<byte>());

            using System.Security.Cryptography.HashAlgorithm hash = algoName switch
            {
                "SHA-1" => System.Security.Cryptography.SHA1.Create(),
                "SHA-384" => System.Security.Cryptography.SHA384.Create(),
                "SHA-512" => System.Security.Cryptography.SHA512.Create(),
                _ => System.Security.Cryptography.SHA256.Create()
            };
            var digest = hash.ComputeHash(data);
            return CreateUint8ArrayFromBytes(digest);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] CryptoDigest failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
            return CreateUint8ArrayFromBytes(Array.Empty<byte>());
        }
    }

    // ── WebSocket bridge implementation ──────────────────────────────────────

    private JsValue ConnectWebSocket(string url, string[] protocols)
    {
        if (string.IsNullOrWhiteSpace(url))
            return JsValue.FromString("SyntaxError: invalid URL");

        try
        {
            var host = new FenWebSocketHost();
            var error = host.Connect(url, protocols);
            if (error != null)
                return JsValue.FromString(error);

            var id = Interlocked.Increment(ref _webSocketIdCounter);
            lock (_webSocketHosts) { _webSocketHosts[id] = host; }
            return JsValue.FromInt32(id);
        }
        catch (Exception ex)
        {
            return JsValue.FromString(ex.Message);
        }
    }

    private JsValue SendWebSocketMessage(int id, string data)
    {
        FenWebSocketHost host;
        lock (_webSocketHosts)
        {
            if (!_webSocketHosts.TryGetValue(id, out host))
                return JsValue.FromString("InvalidStateError: socket not found");
        }
        var error = host.Send(data);
        return error != null ? JsValue.FromString(error) : JsValue.Undefined;
    }

    private JsValue CloseWebSocket(int id, int code, string reason)
    {
        FenWebSocketHost host;
        lock (_webSocketHosts)
        {
            if (!_webSocketHosts.TryGetValue(id, out host))
                return JsValue.Undefined;
            _webSocketHosts.Remove(id);
        }
        host.Close(code, reason);
        host.Dispose();
        return JsValue.Undefined;
    }

    /// <summary>
    /// Poll a WebSocket for pending events and messages.
    /// Returns a JSON string like {"events":[{"type":"open"}],"messages":[{"text":"..."},{"binary":"base64..."}]}
    /// or "null" if nothing pending.
    /// </summary>
    private JsValue PollWebSocket(int id)
    {
        FenWebSocketHost host;
        lock (_webSocketHosts)
        {
            if (!_webSocketHosts.TryGetValue(id, out host))
                return JsValue.FromString("null");
        }

        var events = new List<object>();
        WsEvent evt;
        while ((evt = host.PollEvent()) != null)
            events.Add(new { type = evt.Type, code = evt.Code, reason = evt.Reason });

        var messages = new List<object>();
        WsMessage msg;
        while ((msg = host.PollMessage()) != null)
        {
            if (msg.IsText)
                messages.Add(new { text = msg.TextData });
            else
                messages.Add(new { binary = Convert.ToBase64String(msg.BinaryData) });
        }

        if (events.Count == 0 && messages.Count == 0)
            return JsValue.FromString("null");

        var result = new { events, messages };
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        return JsValue.FromString(json);
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

        var attributesValue = ReadJsProperty(optionsValue, "attributes");
        var characterDataValue = ReadJsProperty(optionsValue, "characterData");
        var attributesPresent = attributesValue.Tag != JsValueTag.Undefined;
        var characterDataPresent = characterDataValue.Tag != JsValueTag.Undefined;

        init.ChildList = ReadJsBoolProperty(optionsValue, "childList");
        init.Attributes = CoerceJsBoolean(attributesValue);
        init.CharacterData = CoerceJsBoolean(characterDataValue);
        init.Subtree = ReadJsBoolProperty(optionsValue, "subtree");
        init.AttributeOldValue = ReadJsBoolProperty(optionsValue, "attributeOldValue");
        init.CharacterDataOldValue = ReadJsBoolProperty(optionsValue, "characterDataOldValue");

        // attributeFilter: optional sequence<DOMString>
        var filterValue = ReadJsProperty(optionsValue, "attributeFilter");
        if (!attributesPresent &&
            (init.AttributeOldValue || filterValue.Tag != JsValueTag.Undefined))
        {
            init.Attributes = true;
        }

        if (!characterDataPresent && init.CharacterDataOldValue)
        {
            init.CharacterData = true;
        }

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
        return CoerceJsBoolean(value);
    }

    private static bool CoerceJsBoolean(JsValue value)
    {
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

    private IReadOnlyList<JsValue> ExtractJsArgumentList(JsValue argumentsValue)
    {
        if (argumentsValue.Tag != JsValueTag.Object)
        {
            return Array.Empty<JsValue>();
        }

        var lengthValue = ReadJsProperty(argumentsValue, "length");
        var length = lengthValue.Tag switch
        {
            JsValueTag.Int32 => Math.Max(0, lengthValue.AsInt32()),
            JsValueTag.Number => Math.Max(0, (int)lengthValue.AsNumber()),
            _ => 0
        };

        if (length == 0)
        {
            return Array.Empty<JsValue>();
        }

        var values = new JsValue[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = ReadJsProperty(argumentsValue, i.ToString(CultureInfo.InvariantCulture));
        }

        return values;
    }

    private JsValue InvokeFenJsHostMethod(JsValue receiver, string methodName, IReadOnlyList<JsValue> args)
    {
        if (receiver.Tag != JsValueTag.HostObject || string.IsNullOrWhiteSpace(methodName))
        {
            LogScriptLoading(
                "HostMethodIllegalInvocation",
                LogSeverity.Warn,
                "[FenJsBridge] Host method called with an invalid receiver",
                new Dictionary<string, object>
                {
                    ["method"] = methodName ?? string.Empty,
                    ["receiverTag"] = receiver.Tag.ToString()
                },
                LogMarker.EngineBug);
            ThrowDomException("TypeError", "Illegal invocation");
        }

        var handle = receiver.AsHostObjectHandle();
        var traceMutation = ShouldTraceHostMutationMethod(methodName);
        if (traceMutation)
        {
            EngineLogCompat.Warn(
                $"[FenHostMutationProbe] enter method={methodName} receiver={DescribeHostMutationValue(receiver)} arg0={(args != null && args.Count > 0 ? DescribeHostMutationValue(args[0]) : "<none>")} arg1={(args != null && args.Count > 1 ? DescribeHostMutationValue(args[1]) : "<none>")}",
                LogCategory.JavaScript);
        }

        if (!_hostHooks.TryGetHostProperty(handle, methodName, out var method) ||
            !_interpreter.CanCallValue(method))
        {
            LogScriptLoading(
                "HostMethodMissing",
                LogSeverity.Warn,
                "[FenJsBridge] Host method missing or not callable",
                new Dictionary<string, object>
                {
                    ["method"] = methodName ?? string.Empty,
                    ["receiverTag"] = receiver.Tag.ToString()
                },
                LogMarker.EngineBug);
            ThrowDomException("TypeError", "Illegal invocation");
        }

        try
        {
            var result = _interpreter.InvokeFunction(method, args ?? Array.Empty<JsValue>(), receiver);
            if (traceMutation)
            {
                EngineLogCompat.Warn(
                    $"[FenHostMutationProbe] exit method={methodName} receiver={DescribeHostMutationValue(receiver)} result={DescribeHostMutationValue(result)} arg0Now={(args != null && args.Count > 0 ? DescribeHostMutationValue(args[0]) : "<none>")}",
                    LogCategory.JavaScript);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (traceMutation)
            {
                EngineLogCompat.Warn(
                    $"[FenHostMutationProbe] throw method={methodName} receiver={DescribeHostMutationValue(receiver)} arg0={(args != null && args.Count > 0 ? DescribeHostMutationValue(args[0]) : "<none>")} error={ex.GetType().Name}:{ex.Message}",
                    LogCategory.JavaScript);
            }

            throw;
        }
    }

    private static bool ShouldTraceHostMutationMethod(string methodName)
        => string.Equals(methodName, "appendChild", StringComparison.Ordinal) ||
           string.Equals(methodName, "insertBefore", StringComparison.Ordinal) ||
           string.Equals(methodName, "replaceChild", StringComparison.Ordinal) ||
           string.Equals(methodName, "removeChild", StringComparison.Ordinal);

    private string DescribeHostMutationValue(JsValue value)
    {
        if (value.Tag != JsValueTag.HostObject)
        {
            return value.Tag.ToString();
        }

        var host = ResolveHostObjectOrNull(value);
        return host switch
        {
            Element element => $"{element.NodeName}#{element.Id}({element.GetType().Name}) parent={element.ParentNode?.NodeName ?? "null"} children={element.ChildNodes.Length}",
            DocumentFragment fragment => $"{fragment.NodeName}({fragment.GetType().Name}) parent={fragment.ParentNode?.NodeName ?? "null"} children={fragment.ChildNodes.Length}",
            Node node => $"{node.NodeName}({node.GetType().Name}) parent={node.ParentNode?.NodeName ?? "null"}",
            _ => host?.GetType().Name ?? "unresolved"
        };
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

    /// <summary>
    /// Register localStorage and sessionStorage as host objects on the global scope.
    /// Must be called after _interpreter is initialized and the base URI is known.
    /// </summary>
    private void RegisterFenJsStorageGlobals(Uri baseUri, Document document)
    {
        var origin = ResolveStorageOrigin(baseUri, document);
        var partitionKey = StoragePartitionKey.FirstParty(origin);

        var localStorage = new FenStorageAreaHost(
            _storageService.LocalStorage, partitionKey, origin);
        var sessionStorage = new FenStorageAreaHost(
            _storageService.SessionStorage, partitionKey, origin);

        _interpreter.RegisterGlobalHostObject(
            "localStorage",
            RegisterHostObject(localStorage, HostObjectKind.StorageArea));
        _interpreter.RegisterGlobalHostObject(
            "sessionStorage",
            RegisterHostObject(sessionStorage, HostObjectKind.StorageArea));
    }

    private static string ResolveStorageOrigin(Uri baseUri, Document document)
    {
        if (baseUri != null && baseUri.IsAbsoluteUri)
            return baseUri.GetLeftPart(UriPartial.Authority);
        if (document != null && !string.IsNullOrWhiteSpace(document.URL) &&
            Uri.TryCreate(document.URL, UriKind.Absolute, out var docUri))
            return docUri.GetLeftPart(UriPartial.Authority);
        return "about:blank";
    }

    private JsValue CreateNavigatorUserAgentDataObject(BrowserUserAgentDataProfile userAgentData)
    {
        userAgentData ??= new BrowserUserAgentDataProfile();

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["brands"] = CreateClientHintBrandArray(userAgentData.Brands),
            ["mobile"] = JsValue.FromBoolean(userAgentData.Mobile),
            ["platform"] = JsValue.FromString(userAgentData.Platform ?? string.Empty),
            ["toJSON"] = _interpreter.AllocateNativeFunction(
                "toJSON",
                (_, _) => _interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["brands"] = CreateClientHintBrandArray(userAgentData.Brands),
                    ["mobile"] = JsValue.FromBoolean(userAgentData.Mobile),
                    ["platform"] = JsValue.FromString(userAgentData.Platform ?? string.Empty)
                }),
                length: 0),
            ["getHighEntropyValues"] = _interpreter.AllocateNativeFunction(
                "getHighEntropyValues",
                (_, args) =>
                {
                    var snapshot = CreateUserAgentHighEntropySnapshot(
                        userAgentData,
                        args.Count > 0 ? args[0] : JsValue.Undefined);
                    var (promise, resolve, _) = ((IBuiltinContext)_interpreter).CreatePromiseCapability();
                    _ = _interpreter.InvokeFunction(resolve, new[] { snapshot }, JsValue.Undefined);
                    return promise;
                },
                length: 1)
        });
    }

    private JsValue CreateNavigatorConnectionObject()
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["downlink"] = JsValue.FromNumber(10),
            ["effectiveType"] = JsValue.FromString("4g"),
            ["rtt"] = JsValue.FromNumber(50),
            ["saveData"] = JsValue.FromBoolean(false),
            ["type"] = JsValue.FromString("unknown"),
            ["onchange"] = JsValue.Null,
            ["addEventListener"] = _interpreter.AllocateNativeFunction(
                "addEventListener",
                (_, _) => JsValue.Undefined,
                length: 2),
            ["removeEventListener"] = _interpreter.AllocateNativeFunction(
                "removeEventListener",
                (_, _) => JsValue.Undefined,
                length: 2),
            ["dispatchEvent"] = _interpreter.AllocateNativeFunction(
                "dispatchEvent",
                (_, _) => JsValue.FromBoolean(true),
                length: 1),
            ["addListener"] = _interpreter.AllocateNativeFunction(
                "addListener",
                (_, _) => JsValue.Undefined,
                length: 1),
            ["removeListener"] = _interpreter.AllocateNativeFunction(
                "removeListener",
                (_, _) => JsValue.Undefined,
                length: 1)
        });
    }

    private JsValue CreateUserAgentHighEntropySnapshot(BrowserUserAgentDataProfile userAgentData, JsValue hintsValue)
    {
        var requested = ExtractStringArrayLike(hintsValue);
        var properties = new Dictionary<string, JsValue>();

        foreach (var hint in requested)
        {
            switch (hint)
            {
                case "architecture":
                    properties["architecture"] = JsValue.FromString(userAgentData.Architecture ?? string.Empty);
                    break;
                case "bitness":
                    properties["bitness"] = JsValue.FromString(userAgentData.Bitness ?? string.Empty);
                    break;
                case "brands":
                    properties["brands"] = CreateClientHintBrandArray(userAgentData.Brands);
                    break;
                case "fullVersionList":
                    properties["fullVersionList"] = CreateClientHintBrandArray(userAgentData.FullVersionList);
                    break;
                case "mobile":
                    properties["mobile"] = JsValue.FromBoolean(userAgentData.Mobile);
                    break;
                case "model":
                    properties["model"] = JsValue.FromString(userAgentData.Model ?? string.Empty);
                    break;
                case "platform":
                    properties["platform"] = JsValue.FromString(userAgentData.Platform ?? string.Empty);
                    break;
                case "platformVersion":
                    properties["platformVersion"] = JsValue.FromString(userAgentData.PlatformVersion ?? string.Empty);
                    break;
                case "uaFullVersion":
                    properties["uaFullVersion"] = JsValue.FromString(GetPrimaryUserAgentFullVersion(userAgentData));
                    break;
                case "wow64":
                    properties["wow64"] = JsValue.FromBoolean(userAgentData.Wow64);
                    break;
            }
        }

        return _interpreter.AllocateObject(properties);
    }

    private JsValue CreateClientHintBrandArray(IReadOnlyList<BrowserClientHintBrand> brands)
    {
        var values = (brands ?? Array.Empty<BrowserClientHintBrand>())
            .Select(brand => _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["brand"] = JsValue.FromString(brand?.Brand ?? string.Empty),
                ["version"] = JsValue.FromString(brand?.Version ?? string.Empty)
            }))
            .ToArray();
        return _interpreter.AllocateArray(values);
    }

    private IReadOnlyList<string> ExtractStringArrayLike(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return Array.Empty<string>();
        }

        var obj = _interpreter.Heap.GetObject(value.AsObjectHandle());
        var context = (IBuiltinContext)_interpreter;
        if (!context.TryGetPropertyValue(obj, value, "length", out var lengthValue))
        {
            return Array.Empty<string>();
        }

        var length = Math.Max(0, (int)Math.Min(128, lengthValue.AsNumber()));
        var items = new List<string>(length);
        for (var i = 0; i < length; i++)
        {
            if (context.TryGetPropertyValue(obj, value, i.ToString(CultureInfo.InvariantCulture), out var itemValue))
            {
                items.Add(CoerceToHostString(itemValue));
            }
        }

        return items;
    }

    private static string GetPrimaryUserAgentFullVersion(BrowserUserAgentDataProfile userAgentData)
    {
        if (userAgentData?.FullVersionList == null || userAgentData.FullVersionList.Count == 0)
        {
            return string.Empty;
        }

        var preferredBrand = userAgentData.FullVersionList.FirstOrDefault(brand =>
            !IsGreaseBrand(brand?.Brand) &&
            !string.Equals(brand?.Brand, "Chromium", StringComparison.OrdinalIgnoreCase));
        if (preferredBrand != null)
        {
            return preferredBrand.Version ?? string.Empty;
        }

        var nonGreaseBrand = userAgentData.FullVersionList.FirstOrDefault(brand => !IsGreaseBrand(brand?.Brand));
        return nonGreaseBrand?.Version ?? string.Empty;
    }

    private static bool IsGreaseBrand(string brand)
    {
        return string.Equals(brand, " Not;A Brand", StringComparison.Ordinal);
    }

    private JsValue ToHostOrNull(object hostObject, HostObjectKind kind)
    {
        if (hostObject == null)
        {
            return JsValue.Null;
        }

        var value = JsValue.FromHostObject(RegisterHostObject(hostObject, kind));
        TryAttachFenJsPrototype(value, hostObject, kind);
        return value;
    }

    private void TryAttachFenJsPrototype(JsValue target, object hostObject, HostObjectKind kind)
    {
        if (!_fenJsDomConstructorsInstalled ||
            hostObject == null ||
            target.Tag != JsValueTag.HostObject)
        {
            return;
        }

        var constructorName = GetFenJsPrototypeConstructorName(hostObject, kind);
        if (string.IsNullOrEmpty(constructorName))
        {
            return;
        }

        if (_hostPrototypeNames.TryGetValue(hostObject, out var existing) &&
            string.Equals(existing, constructorName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (_interpreter.TrySetHostObjectPrototypeFromGlobalConstructor(target, constructorName))
            {
                _hostPrototypeNames[hostObject] = constructorName;
            }
        }
        catch
        {
            // Some host wrappers intentionally have no DOM constructor surface.
        }
    }

    private static string GetFenJsPrototypeConstructorName(object hostObject, HostObjectKind kind)
    {
        return hostObject switch
        {
            Document => "Document",
            ShadowRoot => "ShadowRoot",
            DocumentFragment => "DocumentFragment",
            Text => "Text",
            Comment => "Comment",
            CharacterData => "CharacterData",
            Element => "HTMLElement",
            Attr => "Attr",
            DomRange => "Range",
            FenJsHtmlCollectionHost => "HTMLCollection",
            Node when kind == HostObjectKind.DomNode => "Node",
            BrowserSurfaceProfile => "Navigator",
            FenJsDomStringMapHost => "DOMStringMap",
            _ => null
        };
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

    private JsValue UpgradeCustomElementTreeIfDefined(JsValue rootValue)
    {
        if (rootValue.Tag != JsValueTag.HostObject || _interpreter == null)
        {
            return rootValue;
        }

        if (!_interpreter.TryReadGlobalValue("customElements", out var registry))
        {
            return rootValue;
        }

        var upgrade = ReadJsProperty(registry, "upgrade");
        if (!_interpreter.CanCallValue(upgrade))
        {
            return rootValue;
        }

        try
        {
            _ = _interpreter.InvokeFunction(upgrade, new[] { rootValue }, registry);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] customElements.upgrade callback failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }

        return rootValue;
    }

    private static Uri TryCreateUri(string raw)
    {
        return Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ? parsed : null;
    }

    private IEnumerable<Element> EnumerateScriptElements(Node domRoot)
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
                    var src = element.GetAttribute("src") ?? string.Empty;
                    LogScriptLoading(
                        "ScriptElementDiscovered",
                        LogSeverity.Debug,
                        "[FenJsBridge] Found SCRIPT element",
                        new Dictionary<string, object>
                        {
                            ["scriptId"] = BuildScriptId(scriptElements),
                            ["ordinal"] = scriptElements,
                            ["sourceLabel"] = BuildScriptSourceLabel(element, scriptElements),
                            ["sourceType"] = string.IsNullOrEmpty(src) ? "inline" : "external",
                            ["scriptSourceOffset"] = element.SourceOffset,
                            ["scriptSourceLine"] = element.SourceLine,
                            ["scriptSourceColumn"] = element.SourceColumn,
                            ["src"] = src,
                            ["type"] = element.GetAttribute("type") ?? string.Empty,
                            ["parentType"] = element.ParentNode?.GetType().Name ?? string.Empty,
                            ["parentTag"] = (element.ParentNode as Element)?.TagName ?? string.Empty
                        });
                    yield return element;
                }
                else
                {
                    otherElements++;
                }
            }
        }
        UpdateScriptLoadingSnapshot(snapshot =>
        {
            snapshot.TotalElements = totalElements;
            snapshot.ScriptElements = scriptElements;
            snapshot.OtherElements = otherElements;
            snapshot.TotalScripts = scriptElements;
        });
        LogScriptLoading(
            "ScriptEnumerationCompleted",
            LogSeverity.Debug,
            "[FenJsBridge] EnumerateScriptElements DONE",
            new Dictionary<string, object>
            {
                ["totalElements"] = totalElements,
                ["scriptElements"] = scriptElements,
                ["otherElements"] = otherElements
            });
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

    internal void FocusElement(Element element)
    {
        if (element == null) return;

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

    internal void BlurElement(Element element)
    {
        if (element == null) return;

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

    internal void SubmitFormFromScript(Element element)
    {
        if (element == null || !string.Equals(element.TagName, "FORM", StringComparison.OrdinalIgnoreCase))
            return;

        // Queue the form submission asynchronously so it doesn't deadlock.
        _ = Task.Run(async () =>
        {
            try
            {
                var action = element.GetAttribute("action") ?? string.Empty;
                var method = element.GetAttribute("method") ?? "GET";
                var doc = element.OwnerDocument;
                var baseUrlStr = _currentBaseUri?.AbsoluteUri ?? doc?.BaseURI ?? doc?.URL ?? "about:blank";
                var baseUri = new Uri(baseUrlStr);

                Uri requestUri;
                if (!string.IsNullOrEmpty(action) && Uri.TryCreate(action, UriKind.Absolute, out var absoluteUri))
                {
                    requestUri = absoluteUri;
                }
                else
                {
                    requestUri = new Uri(baseUri, action ?? string.Empty);
                }

                var formData = new Dictionary<string, string>();
                foreach (var child in element.QuerySelectorAll("input, select, textarea, button"))
                {
                    if (child is not Element input) continue;
                    var name = input.GetAttribute("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var value = input.GetAttribute("value") ?? string.Empty;
                    formData[name] = value;
                }

                if (NavigateProgrammaticAsync != null)
                {
                    var queryString = string.Join("&",
                        formData.Select(kvp =>
                            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
                    var fullUri = method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                        ? requestUri.AbsoluteUri
                        : new Uri(requestUri, $"?{queryString}").AbsoluteUri;
                    await NavigateProgrammaticAsync(fullUri).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[SubmitForm] Failed: {ex.Message}", LogCategory.JavaScript);
            }
        });
    }

    internal Func<string, Task> NavigateProgrammaticAsync { get; set; }

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

    private BrowserScriptLoadingRecord GetCurrentScriptRecord()
    {
        lock (_fenJsLock)
        {
            return _currentScriptRecord;
        }
    }

    private void SetCurrentScriptElement(Element scriptElement, BrowserScriptLoadingRecord scriptRecord = null)
    {
        lock (_fenJsLock)
        {
            _currentScriptElement = scriptElement;
            _currentScriptRecord = scriptElement == null ? null : scriptRecord;
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
        if (string.IsNullOrWhiteSpace(type) || !CanInvokeBrowserEventListener(callback))
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

    private bool CanInvokeBrowserEventListener(JsValue callback)
    {
        if (_interpreter.CanCallValue(callback))
        {
            return true;
        }

        if (callback.Tag != JsValueTag.Object)
        {
            return false;
        }

        var handleEvent = ReadJsProperty(callback, "handleEvent");
        return _interpreter.CanCallValue(handleEvent);
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

    private bool ReadPropagationStopped(
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState)
    {
        return dispatchState?.StopPropagation == true ||
               ReadJsBoolProperty(eventValue, "_propagationStopped") ||
               ReadJsBoolProperty(eventValue, "cancelBubble");
    }

    private bool ReadEventDefaultPrevented(
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState)
    {
        return dispatchState?.DefaultPrevented == true ||
               ReadJsBoolProperty(eventValue, "defaultPrevented");
    }

    private bool ReadImmediatePropagationStopped(
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState)
    {
        return dispatchState?.StopImmediatePropagation == true ||
               ReadJsBoolProperty(eventValue, "_immediatePropagationStopped");
    }

    private void DispatchElementEvent(
        Element element,
        string type,
        JsValue eventValue = default,
        BrowserDomEventDispatchState dispatchState = null)
    {
        if (element != null &&
            _elementEventListeners.TryGetValue(element, out var listeners) &&
            listeners != null)
        {
            DispatchBrowserEvent(
                listeners,
                type,
                ToHostOrNull(element, HostObjectKind.DomElement),
                eventValue,
                dispatchState);
        }
    }

    /// <summary>
    /// Full WHATWG-compliant event dispatch with capture, target, and bubble phases.
    /// Dispatches to element listeners, document listeners, and window listeners
    /// following the DOM event propagation algorithm.
    /// </summary>
    private bool DispatchEventFull(
        Element target,
        string type,
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState)
    {
        if (target == null || eventValue.Tag == JsValueTag.Undefined)
            return true;

        // Check if event bubbles
        var bubblesValue = ReadJsProperty(eventValue, "bubbles");
        var bubbles = bubblesValue.Tag == JsValueTag.Boolean && bubblesValue.AsBoolean();

        // Set target on event if not already set (JS-dispatched events may already have it)
        var existingTarget = ReadJsProperty(eventValue, "target");
        if (existingTarget.Tag == JsValueTag.Undefined || existingTarget.Tag == JsValueTag.Null)
        {
            var targetHost = ToHostOrNull(target, HostObjectKind.DomElement);
            _interpreter.SetObjectProperty(eventValue, "target", targetHost);
            _interpreter.SetObjectProperty(eventValue, "srcElement", targetHost);
        }

        // Build ancestor chain: target → parent → ... → rootmost element
        var path = new List<Element>();
        var current = target;
        while (current != null)
        {
            path.Add(current);
            current = current.ParentElement;
        }
        // path[0] = target, path[Count-1] = rootmost element

        if (path.Count == 0)
            return !ReadEventDefaultPrevented(eventValue, dispatchState);

        var document = target.OwnerDocument;
        var docHost = document == null ? JsValue.Undefined : ToHostOrNull(document, HostObjectKind.DomDocument);
        var windowTarget = GetActiveWindowEventTarget();

        // 1. CAPTURE PHASE — fire capture listeners on ancestors from root down to target's parent
        _interpreter.SetObjectProperty(eventValue, "eventPhase", JsValue.FromInt32(1)); // Event.CAPTURING_PHASE
        if (windowTarget.Tag != JsValueTag.Undefined)
        {
            DispatchBrowserEventWithState(
                GetActiveWindowEventListeners(), type, windowTarget, eventValue, dispatchState, capture: true);
        }

        if (!ReadPropagationStopped(eventValue, dispatchState) &&
            docHost.Tag != JsValueTag.Undefined)
        {
            DispatchBrowserEventWithState(
                _documentEventListeners, type, docHost, eventValue, dispatchState, capture: true);
        }

        for (int i = path.Count - 1; i > 0; i--)
        {
            if (ReadPropagationStopped(eventValue, dispatchState))
                break;
            DispatchEventToElementWithPhase(path[i], type, eventValue, dispatchState, capture: true);
        }

        // 2. TARGET PHASE — fire all listeners on the target (both capture and non-capture)
        if (!ReadPropagationStopped(eventValue, dispatchState))
        {
            _interpreter.SetObjectProperty(eventValue, "eventPhase", JsValue.FromInt32(2)); // Event.AT_TARGET
            DispatchEventToElementWithPhase(target, type, eventValue, dispatchState, capture: null);
        }

        // 3. BUBBLE PHASE — fire non-capture listeners up the ancestor chain, then document, then window
        if (bubbles && !ReadPropagationStopped(eventValue, dispatchState))
        {
            _interpreter.SetObjectProperty(eventValue, "eventPhase", JsValue.FromInt32(3)); // Event.BUBBLING_PHASE
            for (int i = 1; i < path.Count; i++)
            {
                if (ReadPropagationStopped(eventValue, dispatchState))
                    break;
                DispatchEventToElementWithPhase(path[i], type, eventValue, dispatchState, capture: false);
            }

            // Document listeners
            if (!ReadPropagationStopped(eventValue, dispatchState))
            {
                if (document != null && docHost.Tag != JsValueTag.Undefined)
                {
                    DispatchBrowserEventWithState(
                        _documentEventListeners, type, docHost, eventValue, dispatchState, capture: false);

                    // Document inline on* handler
                    if (!ReadImmediatePropagationStopped(eventValue, dispatchState))
                    {
                        var docHandler = GetStoredHostPropertyOrUndefined(document, "on" + type);
                        if (_interpreter != null && _interpreter.CanCallValue(docHandler))
                        {
                            TryInvokeFenJsEventCallback(docHandler, docHost, eventValue, type);
                        }
                    }
                }
            }

            // Window listeners
            if (!ReadPropagationStopped(eventValue, dispatchState))
            {
                DispatchBrowserEventWithState(
                    GetActiveWindowEventListeners(), type, windowTarget, eventValue, dispatchState, capture: false);

                // Window inline on* handler
                if (!ReadImmediatePropagationStopped(eventValue, dispatchState) &&
                    _interpreter != null &&
                    TryReadWindowEventHandler(windowTarget, type, out var winHandler) &&
                    _interpreter.CanCallValue(winHandler))
                {
                    TryInvokeFenJsEventCallback(winHandler, windowTarget, eventValue, type);
                }
            }
        }

        return !ReadEventDefaultPrevented(eventValue, dispatchState);
    }

    /// <summary>
    /// Dispatches an event to a single element during a specific propagation phase.
    /// Filters listeners by type and capture phase, invokes callbacks with propagation
    /// checking, handles 'once' removal, and invokes inline on* handlers during
    /// bubble/target phases.
    /// </summary>
    private void DispatchEventToElementWithPhase(
        Element element,
        string type,
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState,
        bool? capture)
    {
        if (element == null)
            return;
        if (ReadImmediatePropagationStopped(eventValue, dispatchState))
            return;

        var currentTarget = ToHostOrNull(element, HostObjectKind.DomElement);
        _interpreter.SetObjectProperty(eventValue, "currentTarget", currentTarget);

        // Fire registered event listeners
        if (_elementEventListeners.TryGetValue(element, out var allListeners) &&
            allListeners != null && allListeners.Count > 0)
        {
            for (int i = 0; i < allListeners.Count; i++)
            {
                if (ReadImmediatePropagationStopped(eventValue, dispatchState))
                    break;

                var listener = allListeners[i];
                if (!string.Equals(listener.Type, type, StringComparison.Ordinal))
                    continue;
                if (capture.HasValue && listener.Capture != capture.Value)
                    continue;

                TryInvokeFenJsEventCallback(listener.Callback, currentTarget, eventValue, type);

                if (listener.Once)
                {
                    allListeners.RemoveAll(existing =>
                        string.Equals(existing.Type, listener.Type, StringComparison.Ordinal) &&
                        existing.Capture == listener.Capture &&
                        existing.Callback.Equals(listener.Callback));
                    i--; // Adjust index after removal
                }
            }
        }

        // Invoke inline on* handler during bubble/target phases (NOT during capture)
        if (!capture.HasValue || capture.Value == false)
        {
            if (!ReadImmediatePropagationStopped(eventValue, dispatchState))
            {
                var handler = GetStoredHostPropertyOrUndefined(element, "on" + type);
                if (_interpreter != null && _interpreter.CanCallValue(handler))
                {
                    TryInvokeFenJsEventCallback(handler, currentTarget, eventValue, type);
                }
            }
        }
    }

    /// <summary>
    /// Dispatches event listeners with propagation flag checking.
    /// Like DispatchBrowserEvent but respects stopImmediatePropagation.
    /// </summary>
    private void DispatchBrowserEventWithState(
        List<BrowserEventListener> listeners,
        string type,
        JsValue currentTarget,
        JsValue eventValue,
        BrowserDomEventDispatchState dispatchState,
        bool? capture = null)
    {
        if (listeners.Count == 0 || ReadImmediatePropagationStopped(eventValue, dispatchState))
            return;

        _interpreter.SetObjectProperty(eventValue, "currentTarget", currentTarget);

        for (int i = 0; i < listeners.Count; i++)
        {
            if (ReadImmediatePropagationStopped(eventValue, dispatchState))
                break;

            var listener = listeners[i];
            if (!string.Equals(listener.Type, type, StringComparison.Ordinal))
                continue;
            if (capture.HasValue && listener.Capture != capture.Value)
                continue;

            TryInvokeFenJsEventCallback(listener.Callback, currentTarget, eventValue, type);

            if (listener.Once)
            {
                listeners.RemoveAll(existing =>
                    string.Equals(existing.Type, listener.Type, StringComparison.Ordinal) &&
                    existing.Capture == listener.Capture &&
                    existing.Callback.Equals(listener.Callback));
                i--;
            }
        }
    }

    private void DispatchStartupLifecycleEvents()
    {
        var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
        if (document == null)
        {
            SetDocumentReadyState("complete");
            UpdateEventLoopSnapshot(snapshot =>
            {
                snapshot.Status = "completed-no-document";
                snapshot.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            });
            AddEventLoopRecord("DocumentReadyStateComplete", "no-document");
            return;
        }

        SetDocumentReadyState("interactive");
        var domContentLoadedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        UpdateEventLoopSnapshot(snapshot =>
        {
            snapshot.DomContentLoadedFired = true;
            snapshot.DomContentLoadedUtc = domContentLoadedUtc;
        });
        AddEventLoopRecord("DOMContentLoadedFired", "document");
        LogEventLoop(
            "DOMContentLoadedFired",
            LogSeverity.Info,
            "[FenJsBridge] DOMContentLoaded dispatched",
            new Dictionary<string, object>
            {
                ["documentReadyState"] = "interactive"
            });
        DispatchBrowserEvent(
            _documentEventListeners,
            "DOMContentLoaded",
            ToHostOrNull(document, HostObjectKind.DomDocument));

        SetDocumentReadyState("complete");
        InvokeBodyOnloadAttribute(document);
        DispatchWindowLoadHandlers();
        MarkEventLoopCompleted();
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

    private JsValue CreateResolvedPromise(JsValue resolution)
    {
        var (promise, resolve, _) = ((IBuiltinContext)_interpreter).CreatePromiseCapability();
        _ = _interpreter.InvokeFunction(resolve, new[] { resolution }, JsValue.Undefined);
        return promise;
    }

    private JsValue CreateRejectedPromise(string message, string name)
    {
        var (promise, _, reject) = ((IBuiltinContext)_interpreter).CreatePromiseCapability();
        var errorObj = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["message"] = JsValue.FromString(message ?? string.Empty),
            ["name"] = JsValue.FromString(name ?? "Error")
        });
        _ = _interpreter.InvokeFunction(reject, new[] { errorObj }, JsValue.Undefined);
        return promise;
    }

    private JsValue CreateViewTransitionResult(JsValue updateCallback)
    {
        if (_interpreter.CanCallValue(updateCallback))
        {
            try
            {
                _ = _interpreter.InvokeFunction(updateCallback, Array.Empty<JsValue>(), JsValue.Undefined);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] startViewTransition callback failed: {ex.Message}",
                    FenBrowser.Core.Logging.LogCategory.JavaScript);
            }
        }

        var result = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["ready"] = CreateResolvedPromise(JsValue.Undefined),
            ["updateCallbackDone"] = CreateResolvedPromise(JsValue.Undefined),
            ["finished"] = CreateResolvedPromise(JsValue.Undefined)
        });
        _interpreter.SetObjectProperty(
            result,
            "skipTransition",
            _interpreter.AllocateNativeFunction("skipTransition", (_, _) => JsValue.Undefined, length: 0),
            enumerable: false);
        return result;
    }

    private JsValue ParseFenJsUrl(IReadOnlyList<JsValue> args)
    {
        if (args == null ||
            args.Count == 0 ||
            args[0].Tag == JsValueTag.Undefined)
        {
            return JsValue.Null;
        }

        var input = CoerceToHostString(args[0]);
        Uri baseUri = null;
        if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
        {
            var baseText = CoerceToHostString(args[1]);
            if (!Uri.TryCreate(baseText, UriKind.Absolute, out baseUri))
            {
                return JsValue.Null;
            }
        }
        else
        {
            var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
            baseUri = _currentBaseUri ?? TryCreateUri(document?.URL);
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (baseUri == null || !Uri.TryCreate(baseUri, input, out uri))
            {
                return JsValue.Null;
            }
        }

        if (uri == null || !uri.IsAbsoluteUri)
        {
            return JsValue.Null;
        }

        var port = uri.IsDefaultPort ? string.Empty : uri.Port.ToString(CultureInfo.InvariantCulture);
        var host = string.IsNullOrEmpty(port) ? uri.Host : $"{uri.Host}:{port}";
        var origin =
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? uri.GetLeftPart(UriPartial.Authority)
                : "null";

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["href"] = JsValue.FromString(uri.AbsoluteUri),
            ["origin"] = JsValue.FromString(origin),
            ["protocol"] = JsValue.FromString(string.IsNullOrEmpty(uri.Scheme) ? string.Empty : uri.Scheme + ":"),
            ["host"] = JsValue.FromString(host ?? string.Empty),
            ["hostname"] = JsValue.FromString(uri.Host ?? string.Empty),
            ["port"] = JsValue.FromString(port),
            ["pathname"] = JsValue.FromString(string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath),
            ["search"] = JsValue.FromString(uri.Query ?? string.Empty),
            ["hash"] = JsValue.FromString(uri.Fragment ?? string.Empty)
        });
    }

    private void ApplyElementProperties(Element element, JsValue propertiesValue)
    {
        if (element == null || propertiesValue.Tag != JsValueTag.Object)
        {
            return;
        }

        var propertiesObject = _interpreter.Heap.GetObject(propertiesValue.AsObjectHandle());
        var context = (IBuiltinContext)_interpreter;
        foreach (var property in propertiesObject.EnumerateOwnProperties())
        {
            if (!property.Value.Enumerable ||
                string.IsNullOrWhiteSpace(property.Key) ||
                !context.TryGetPropertyValue(propertiesObject, propertiesValue, property.Key, out var propertyValue))
            {
                continue;
            }

            ApplyElementProperty(element, property.Key, propertyValue);
        }
    }

    private void QueueFrameLoadsForTree(Node node)
    {
        if (node is not Element element)
        {
            return;
        }

        QueueFrameElementLoad(element);

        foreach (var descendant in element.Descendants().OfType<Element>())
        {
            QueueFrameElementLoad(descendant);
        }
    }

    private static bool IsIFrameElement(Element element)
    {
        return string.Equals(element?.TagName, "iframe", StringComparison.OrdinalIgnoreCase);
    }

    private void QueueFrameElementLoad(Element element)
    {
        if (element == null ||
            !IsIFrameElement(element) ||
            !element.IsConnected ||
            FrameElementLoader == null)
        {
            return;
        }

        // 1. Check for srcdoc (takes priority over src per HTML spec)
        var srcdoc = element.GetAttribute("srcdoc");
        if (!string.IsNullOrWhiteSpace(srcdoc))
        {
            var existingDoc = GetStoredHostPropertyOrUndefined(element, "__fenFrameSrcdocHash");
            var newHash = srcdoc.GetHashCode().ToString("x", CultureInfo.InvariantCulture);
            if (existingDoc.Tag != JsValueTag.Undefined &&
                string.Equals(CoerceToHostString(existingDoc), newHash, StringComparison.Ordinal))
            {
                return; // Already loaded this srcdoc content
            }

            SetStoredHostProperty(element, "__fenFrameSrcdocHash", JsValue.FromString(newHash));

            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadFrameSrcdocAsync(element, srcdoc).ConfigureAwait(false);
                    RequestRender?.Invoke();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn(
                        $"[FenJsBridge] srcdoc frame load failed for <iframe>: {ex.Message}",
                        LogCategory.JavaScript);
                }
            });
            return;
        }

        // 2. Check for src URL
        var src = ResolveElementUrlProperty(element, "src");
        if (string.IsNullOrWhiteSpace(src) ||
            string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(src, UriKind.Absolute, out var frameUri))
        {
            return;
        }

        var existing = GetStoredHostPropertyOrUndefined(element, "__fenFrameLoadUrl");
        if (existing.Tag != JsValueTag.Undefined &&
            string.Equals(CoerceToHostString(existing), frameUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return;
        }

        SetStoredHostProperty(element, "__fenFrameLoadUrl", JsValue.FromString(frameUri.AbsoluteUri));

        _ = Task.Run(async () =>
        {
            try
            {
                await FrameElementLoader(element, frameUri).ConfigureAwait(false);
                RequestRender?.Invoke();
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] iframe load failed for '{frameUri}': {ex.Message}",
                    FenBrowser.Core.Logging.LogCategory.JavaScript);
            }
        });
    }

    /// <summary>
    /// Parses srcdoc HTML content and loads it as the iframe's subdocument.
    /// The srcdoc content is treated as an HTML document with the parent page's
    /// base URI (per HTML spec §4.8.5 — srcdoc documents have the parent's URL
    /// for same-origin purposes).
    /// </summary>
    private async Task LoadFrameSrcdocAsync(Element frameElement, string srcdocHtml)
    {
        if (string.IsNullOrWhiteSpace(srcdocHtml) || frameElement == null || !frameElement.IsConnected)
        {
            return;
        }

        try
        {
            // srcdoc documents inherit the creator document's base URL
            var frameUri = _currentBaseUri ?? new Uri("about:srcdoc");
            var parsedDocument = HtmlParser.ParseDocument(
                srcdocHtml,
                new HtmlParserOptions { BaseUri = frameUri });

            var parsedRoot = parsedDocument?.DocumentElement;
            if (parsedRoot == null)
            {
                EngineLogCompat.Warn(
                    "[FenJsBridge] srcdoc parse produced no document element",
                    LogCategory.JavaScript);
                return;
            }

            while (frameElement.FirstChild != null)
            {
                frameElement.RemoveChild(frameElement.FirstChild);
            }

            frameElement.AppendChild(parsedDocument);

            // Wire up the frame's own DOM context and scripts.
            // The subdocument gets its own JS window bound to the frame element.
            GetOrCreateIFrameContentWindow(frameElement, parsedDocument, frameUri);
            await SetSubdocumentDomAsync(parsedRoot, frameUri).ConfigureAwait(false);

            FenBrowser.Core.EngineLogCompat.Info(
                $"[FenJsBridge] srcdoc frame loaded root='{parsedRoot.TagName}'",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] srcdoc frame load failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
    }

    private void ApplyElementProperty(Element element, string property, JsValue value)
    {
        switch (property)
        {
            case "className":
                element.ClassName = CoerceToHostString(value);
                break;
            case "id":
                element.Id = CoerceToHostString(value);
                break;
            case "value":
                element.SetAttribute("value", CoerceToHostString(value));
                if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
                {
                    element.TextContent = CoerceToHostString(value);
                }
                break;
            case "tabIndex":
                element.SetAttribute(
                    "tabindex",
                    ((int)CoerceToFiniteNumber(value, 0)).ToString(CultureInfo.InvariantCulture));
                break;
            case "src":
            case "srcdoc":
            case "href":
            case "nonce":
                element.SetAttribute(property, CoerceToHostString(value));
                if (string.Equals(property, "src", StringComparison.Ordinal) &&
                    IsIFrameElement(element))
                {
                    QueueFrameElementLoad(element);
                }
                if (string.Equals(property, "srcdoc", StringComparison.Ordinal) &&
                    IsIFrameElement(element))
                {
                    QueueFrameElementLoad(element);
                }
                break;
            case "innerHTML":
                element.InnerHTML = CoerceToHostString(value);
                if (ExecuteInlineScriptsOnInnerHTML)
                {
                    ExecuteInlineScriptsFromElement(element);
                }
                break;
            case "textContent":
                element.TextContent = CoerceToHostString(value);
                break;
            default:
                SetStoredHostProperty(element, property, value);
                break;
        }
    }

    private JsValue GetOrCreateDomTokenListView(DOMTokenList tokenList)
    {
        var store = GetHostPropertyStore(tokenList);
        if (store.TryGetValue("__fenDomTokenListView", out var cached))
        {
            RefreshDomTokenIndices(cached, tokenList);
            return cached;
        }

        // Create the base view object with methods that don't need self-reference.
        var view = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["item"] = CreateDomTokenListItemMethod(tokenList),
            ["contains"] = CreateDomTokenListContainsMethod(tokenList),
            ["supports"] = CreateDomTokenListSupportsMethod(tokenList),
            ["values"] = GetOrCreateHostCallable(tokenList, "values",
                (_, _2) => BuildDomTokenArrayValue(tokenList), length: 0),
            ["keys"] = GetOrCreateHostCallable(tokenList, "keys",
                (_, _2) => BuildDomIndexArray(tokenList), length: 0),
            ["entries"] = GetOrCreateHostCallable(tokenList, "entries",
                (_, _2) => BuildDomEntryArray(tokenList), length: 0),
            ["forEach"] = GetOrCreateHostCallable(tokenList, "forEach",
                (_, _2) => JsValue.Undefined, length: 1),
            ["toString"] = GetOrCreateHostCallable(tokenList, "toString",
                (_, _2) => JsValue.FromString(tokenList.Value ?? string.Empty), length: 0),
        });

        // Now that view is declared, add the mutating methods that need self-reference.
        var viewObj = _interpreter.Heap.GetObject(view.AsObjectHandle());
        viewObj.DefineOwnProperty("add", new JsPropertyDescriptor(
            CreateDomTokenListAddMethod(tokenList, view), Writable: true, Enumerable: false, Configurable: true));
        viewObj.DefineOwnProperty("remove", new JsPropertyDescriptor(
            CreateDomTokenListRemoveMethod(tokenList, view), Writable: true, Enumerable: false, Configurable: true));
        viewObj.DefineOwnProperty("toggle", new JsPropertyDescriptor(
            CreateDomTokenListToggleMethod(tokenList, view), Writable: true, Enumerable: false, Configurable: true));
        viewObj.DefineOwnProperty("replace", new JsPropertyDescriptor(
            CreateDomTokenListReplaceMethod(tokenList, view), Writable: true, Enumerable: false, Configurable: true));

        // Set length, value, and numeric indices as plain data properties.
        RefreshDomTokenIndices(view, tokenList);

        // Install Symbol.iterator.
        var iterSymId = _interpreter.GetWellKnownSymbolId("iterator");
        if (iterSymId != 0)
        {
            var iterFn = _interpreter.AllocateNativeFunction(
                "[Symbol.iterator]",
                (thisValue, _2) =>
                {
                    var len = tokenList.Length;
                    var items = new FenBrowser.Js.Runtime.JsValue[len];
                    for (int i = 0; i < len; i++)
                    {
                        var token = tokenList.Item(i);
                        items[i] = FenBrowser.Js.Runtime.JsValue.FromString(token ?? string.Empty);
                    }
                    var idx = 0;
                    var iteratorEntries = new Dictionary<string, JsValue>
                    {
                        ["next"] = _interpreter.AllocateNativeFunction("next",
                            (_, _3) =>
                            {
                                if (idx >= len)
                                {
                                    var doneResult = new Dictionary<string, JsValue>
                                    {
                                        ["value"] = JsValue.Undefined,
                                        ["done"] = JsValue.FromBoolean(true)
                                    };
                                    return _interpreter.AllocateObject(doneResult);
                                }
                                var resultDict = new Dictionary<string, JsValue>
                                {
                                    ["value"] = items[idx++],
                                    ["done"] = JsValue.FromBoolean(false)
                                };
                                return _interpreter.AllocateObject(resultDict);
                            }, length: 0)
                    };
                    var iterObj = _interpreter.AllocateObject(iteratorEntries);
                    var iterObjHandle = iterObj.AsObjectHandle();
                    var iterObjNative = _interpreter.Heap.GetObject(iterObjHandle);
                    iterObjNative.DefineOwnSymbolProperty(iterSymId,
                        new FenBrowser.Js.Objects.JsPropertyDescriptor(
                            _interpreter.AllocateNativeFunction("[Symbol.iterator]",
                                (self, _3) => self, length: 0),
                            Writable: true, Enumerable: false, Configurable: true));
                    return iterObj;
                },
                length: 0);
            viewObj.DefineOwnSymbolProperty(iterSymId,
                new FenBrowser.Js.Objects.JsPropertyDescriptor(
                    iterFn, Writable: true, Enumerable: false, Configurable: true));
        }

        AttachFenJsPrototype(view, "DOMTokenList");
        store["__fenDomTokenListView"] = view;
        return view;
    }

    private JsValue CreateDomTokenListItemMethod(DOMTokenList tokenList)
    {
        return GetOrCreateHostCallable(tokenList, "item",
            (_, args) =>
            {
                var index = args.Count > 0 && TryCoerceIndex(args[0], out var parsedIndex)
                    ? parsedIndex : -1;
                var item = tokenList.Item(index);
                return item == null ? JsValue.Null : JsValue.FromString(item);
            }, length: 1);
    }

    private JsValue CreateDomTokenListContainsMethod(DOMTokenList tokenList)
    {
        return GetOrCreateHostCallable(tokenList, "contains",
            (_, args) =>
            {
                var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                return JsValue.FromBoolean(tokenList.Contains(token));
            }, length: 1);
    }

    private JsValue CreateDomTokenListSupportsMethod(DOMTokenList tokenList)
    {
        return GetOrCreateHostCallable(tokenList, "supports",
            (_, args) =>
            {
                var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                return JsValue.FromBoolean(tokenList.Supports(token));
            }, length: 1);
    }

    private JsValue CreateDomTokenListAddMethod(DOMTokenList tokenList, JsValue view)
    {
        return GetOrCreateHostCallable(tokenList, "add",
            (_, args) =>
            {
                tokenList.Add(args.Select(CoerceToHostString).ToArray());
                RefreshDomTokenIndices(view, tokenList);
                return JsValue.Undefined;
            });
    }

    private JsValue CreateDomTokenListRemoveMethod(DOMTokenList tokenList, JsValue view)
    {
        return GetOrCreateHostCallable(tokenList, "remove",
            (_, args) =>
            {
                tokenList.Remove(args.Select(CoerceToHostString).ToArray());
                RefreshDomTokenIndices(view, tokenList);
                return JsValue.Undefined;
            });
    }

    private JsValue CreateDomTokenListToggleMethod(DOMTokenList tokenList, JsValue view)
    {
        return GetOrCreateHostCallable(tokenList, "toggle",
            (_, args) =>
            {
                var token = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                bool? force = null;
                if (args.Count > 1 && args[1].Tag != JsValueTag.Undefined)
                    force = CoerceToHostBoolean(args[1]);
                var result = tokenList.Toggle(token, force);
                RefreshDomTokenIndices(view, tokenList);
                return JsValue.FromBoolean(result);
            }, length: 1);
    }

    private JsValue CreateDomTokenListReplaceMethod(DOMTokenList tokenList, JsValue view)
    {
        return GetOrCreateHostCallable(tokenList, "replace",
            (_, args) =>
            {
                var oldToken = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                var newToken = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                var result = tokenList.Replace(oldToken, newToken);
                RefreshDomTokenIndices(view, tokenList);
                return JsValue.FromBoolean(result);
            }, length: 2);
    }

    private void RefreshDomTokenIndices(JsValue view, DOMTokenList tokenList)
    {
        var viewObj = _interpreter.Heap.GetObject(view.AsObjectHandle());
        // Update length and value as plain properties (accessors aren't
        // reliably invoked by FenJS's [[Set]]/[[Get]] on plain objects).
        viewObj.DefineOwnProperty("length",
            new JsPropertyDescriptor(
                JsValue.FromInt32(tokenList.Length),
                Writable: true, Enumerable: false, Configurable: true));
        viewObj.DefineOwnProperty("value",
            new JsPropertyDescriptor(
                JsValue.FromString(tokenList.Value ?? string.Empty),
                Writable: true, Enumerable: true, Configurable: true));
        // Set fresh numeric indices from the live token list.
        var maxOld = 0;
        foreach (var kv in viewObj.EnumerateOwnProperties())
        {
            if (kv.Key.Length > 0 && kv.Key[0] >= '0' && kv.Key[0] <= '9' &&
                int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                maxOld = Math.Max(maxOld, n + 1);
            }
        }
        // Clear stale indices beyond the current length.
        for (var i = tokenList.Length; i < maxOld; i++)
        {
            viewObj.DeleteProperty(i.ToString(CultureInfo.InvariantCulture));
        }
        // Set/update indices for current tokens.
        for (var i = 0; i < tokenList.Length; i++)
        {
            var item = tokenList.Item(i);
            viewObj.DefineOwnProperty(
                i.ToString(CultureInfo.InvariantCulture),
                new JsPropertyDescriptor(
                    JsValue.FromString(item ?? string.Empty),
                    Writable: true, Enumerable: true, Configurable: true));
        }
    }

    // Creates a JS array-like object from the tokens.
    private JsValue BuildDomTokenArrayValue(DOMTokenList tokenList)
    {
        var entries = new Dictionary<string, JsValue>(tokenList.Length + 1)
        {
            ["length"] = JsValue.FromInt32(tokenList.Length)
        };
        for (int i = 0; i < tokenList.Length; i++)
        {
            var token = tokenList.Item(i);
            entries[i.ToString(CultureInfo.InvariantCulture)] =
                JsValue.FromString(token ?? string.Empty);
        }
        return _interpreter.AllocateObject(entries);
    }

    private JsValue BuildDomIndexArray(DOMTokenList tokenList)
    {
        var len = tokenList.Length;
        var entries = new Dictionary<string, JsValue>(len + 1)
        {
            ["length"] = JsValue.FromInt32(len)
        };
        for (int k = 0; k < len; k++)
            entries[k.ToString(CultureInfo.InvariantCulture)] = JsValue.FromInt32(k);
        return _interpreter.AllocateObject(entries);
    }

    private JsValue BuildDomEntryArray(DOMTokenList tokenList)
    {
        var len = tokenList.Length;
        var entries = new Dictionary<string, JsValue>(len + 1)
        {
            ["length"] = JsValue.FromInt32(len)
        };
        for (int k = 0; k < len; k++)
        {
            var token = tokenList.Item(k);
            var pairEntries = new Dictionary<string, JsValue>(3)
            {
                ["length"] = JsValue.FromInt32(2),
                ["0"] = JsValue.FromInt32(k),
                ["1"] = JsValue.FromString(token ?? string.Empty)
            };
            entries[k.ToString(CultureInfo.InvariantCulture)] =
                _interpreter.AllocateObject(pairEntries);
        }
        return _interpreter.AllocateObject(entries);
    }

    private JsValue GetOrCreateTemplateContent(Element template)
    {
        var cached = GetStoredHostPropertyOrUndefined(template, "__fenTemplateContent");
        if (cached.Tag != JsValueTag.Undefined)
        {
            return cached;
        }

        var fragment = template.OwnerDocument?.CreateDocumentFragment() ?? new DocumentFragment(template.OwnerDocument);
        while (template.FirstChild != null)
        {
            fragment.AppendChild(template.FirstChild);
        }

        var value = ToHostNodeOrNull(fragment);
        SetStoredHostProperty(template, "__fenTemplateContent", value);
        return value;
    }

    private JsValue GetOrCreateIFrameContentDocument(Element iframe)
    {
        foreach (var child in iframe.ChildNodes)
        {
            if (child is Document frameDocument)
            {
                var frameDocumentValue = ToHostOrNull(frameDocument, HostObjectKind.DomDocument);
                SetStoredHostProperty(iframe, "__fenIframeContentDocument", frameDocumentValue);
                return frameDocumentValue;
            }

            if (child is Element frameRoot && frameRoot.OwnerDocument != null)
            {
                var frameOwnerDocumentValue = ToHostOrNull(frameRoot.OwnerDocument, HostObjectKind.DomDocument);
                SetStoredHostProperty(iframe, "__fenIframeContentDocument", frameOwnerDocumentValue);
                return frameOwnerDocumentValue;
            }
        }

        var cached = GetStoredHostPropertyOrUndefined(iframe, "__fenIframeContentDocument");
        if (cached.Tag != JsValueTag.Undefined)
        {
            return cached;
        }

        var document = Document.CreateHtmlDocument();
        var url = ResolveElementUrlProperty(iframe, "src");
        document.URL = string.IsNullOrWhiteSpace(url) ? "about:blank" : url;
        document.BaseURI = document.URL;

        if (document.ParentNode == null)
        {
            iframe.AppendChild(document);
        }

        var value = ToHostOrNull(document, HostObjectKind.DomDocument);
        SetStoredHostProperty(iframe, "__fenIframeContentDocument", value);
        return value;
    }

    private JsValue GetOrCreateIFrameContentWindow(Element iframe)
    {
        return GetOrCreateIFrameContentWindow(iframe, null, null);
    }

    private JsValue GetOrCreateIFrameContentWindow(Element iframe, Document frameDocument, Uri frameUri)
    {
        var cached = GetStoredHostPropertyOrUndefined(iframe, "__fenIframeContentWindow");
        if (cached.Tag != JsValueTag.Undefined)
        {
            UpdateIFrameContentWindow(cached, iframe, frameDocument, frameUri);
            return cached;
        }

        var document = frameDocument == null
            ? GetOrCreateIFrameContentDocument(iframe)
            : ToHostOrNull(frameDocument, HostObjectKind.DomDocument);
        var href = ResolveIFrameWindowHref(iframe, frameDocument, frameUri);

        var frameWindowListeners = GetIFrameWindowListeners(iframe);
        var window = _interpreter.AllocateObject(new Dictionary<string, JsValue>());
        var location = CreatePlainLocationObject(href);

        _interpreter.SetObjectProperty(window, "document", document);
        _interpreter.SetObjectProperty(window, "location", location);
        _interpreter.SetObjectProperty(window, "frameElement", ToHostOrNull(iframe, HostObjectKind.DomElement));
        _interpreter.SetObjectProperty(window, "window", window);
        _interpreter.SetObjectProperty(window, "self", window);
        _interpreter.SetObjectProperty(window, "frames", window);
        _interpreter.SetObjectProperty(window, "length", JsValue.FromInt32(0));
        _interpreter.SetObjectProperty(window, "closed", JsValue.FromBoolean(false));

        var parent = _fenJsGlobalThis.Tag == JsValueTag.Undefined ? window : _fenJsGlobalThis;
        _interpreter.SetObjectProperty(window, "parent", parent);
        _interpreter.SetObjectProperty(window, "top", parent);
        _interpreter.SetObjectProperty(
            window,
            "postMessage",
            _interpreter.AllocateNativeFunction(
                "postMessage",
                (_, args) =>
                {
                    var data = args.Count > 0 ? args[0] : JsValue.Undefined;
                    var targetOrigin = args.Count > 1 ? CoerceToHostString(args[1]) : "*";
                    QueueWindowMessage(window, frameWindowListeners, data, _fenJsGlobalThis, targetOrigin);
                    return JsValue.Undefined;
                },
                length: 1));
        _interpreter.SetObjectProperty(
            window,
            "addEventListener",
            _interpreter.AllocateNativeFunction(
                "addEventListener",
                (_, args) =>
                {
                    AddBrowserEventListener(frameWindowListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.SetObjectProperty(
            window,
            "removeEventListener",
            _interpreter.AllocateNativeFunction(
                "removeEventListener",
                (_, args) =>
                {
                    RemoveBrowserEventListener(frameWindowListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.SetObjectProperty(
            window,
            "dispatchEvent",
            _interpreter.AllocateNativeFunction(
                "dispatchEvent",
                (_, args) =>
                {
                    var eventValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                    return JsValue.FromBoolean(DispatchFrameWindowHostEvent(window, frameWindowListeners, eventValue));
                },
                length: 1));

        // ── Forward standard browser globals onto the iframe contentWindow ──
        // In a real browser, window === globalThis, so window.setTimeout etc. work.
        // FenBrowser uses a plain JS object for iframe contentWindow, so we must
        // explicitly copy all standard browser APIs from the interpreter's globals
        // to make scripts like reCAPTCHA (which access window.setTimeout) work.
        var globalsToForward = new[]
        {
            // Timers
            "setTimeout", "setInterval", "clearTimeout", "clearInterval",
            "requestAnimationFrame", "cancelAnimationFrame",
            // Encoding
            "atob", "btoa",
            // DOM utilities
            "getComputedStyle", "matchMedia", "queueMicrotask", "structuredClone",
            // Observers
            "MutationObserver", "IntersectionObserver", "ResizeObserver", "PerformanceObserver",
            // Fetch / XHR
            "fetch", "XMLHttpRequest", "AbortController", "AbortSignal",
            "Headers", "Request", "Response",
            // Console
            "console",
            // Navigation objects
            "navigator", "performance", "crypto", "screen",
            // Constructors
            "Event", "CustomEvent", "MessageEvent", "URL", "URLSearchParams",
            "DOMParser", "FormData", "Blob", "File",
            "TextEncoder", "TextDecoder",
            "Map", "Set", "WeakMap", "WeakSet", "WeakRef",
            "Promise", "Proxy", "Symbol", "Intl", "Reflect",
            // Security
            "isSecureContext", "crossOriginIsolated", "origin",
            // Misc browser APIs
            "Notification", "trustedTypes", "chrome",
            "alert", "confirm", "prompt",
            // Error types
            "Error", "TypeError", "RangeError", "ReferenceError", "SyntaxError", "URIError", "EvalError",
            // Core JS (needed when reCAPTCHA does window.Array etc.)
            "Array", "Object", "Function", "String", "Number", "Boolean", "RegExp", "Date", "Math", "JSON",
            "parseInt", "parseFloat", "isNaN", "isFinite", "undefined", "NaN", "Infinity",
            "encodeURI", "encodeURIComponent", "decodeURI", "decodeURIComponent",
            "ArrayBuffer", "DataView", "Float32Array", "Float64Array",
            "Int8Array", "Int16Array", "Int32Array",
            "Uint8Array", "Uint8ClampedArray", "Uint16Array", "Uint32Array",
            "BigInt", "BigInt64Array", "BigUint64Array",
        };
        foreach (var name in globalsToForward)
        {
            var globalVal = _interpreter.ReadGlobalValueOrUndefined(name);
            if (globalVal.Tag != JsValueTag.Undefined)
            {
                _interpreter.SetObjectProperty(window, name, globalVal);
            }
        }

        SetStoredHostProperty(iframe, "__fenIframeContentWindow", window);
        var defaultViewDocument = frameDocument;
        if (defaultViewDocument == null)
        {
            foreach (var child in iframe.ChildNodes)
            {
                if (child is Document childDocument)
                {
                    defaultViewDocument = childDocument;
                    break;
                }
            }
        }

        if (defaultViewDocument != null)
        {
            SetStoredHostProperty(defaultViewDocument, "__fenDefaultView", window);
        }

        return window;
    }

    private void UpdateIFrameContentWindow(
        JsValue window,
        Element iframe,
        Document frameDocument,
        Uri frameUri)
    {
        if (window.Tag != JsValueTag.Object)
        {
            return;
        }

        var document = frameDocument == null
            ? GetOrCreateIFrameContentDocument(iframe)
            : ToHostOrNull(frameDocument, HostObjectKind.DomDocument);
        var href = ResolveIFrameWindowHref(iframe, frameDocument, frameUri);
        _interpreter.SetObjectProperty(window, "document", document);
        _interpreter.SetObjectProperty(window, "location", CreatePlainLocationObject(href));
        _interpreter.SetObjectProperty(window, "frameElement", ToHostOrNull(iframe, HostObjectKind.DomElement));

        if (frameDocument != null)
        {
            SetStoredHostProperty(frameDocument, "__fenDefaultView", window);
        }
    }

    private string ResolveIFrameWindowHref(Element iframe, Document frameDocument, Uri frameUri)
    {
        var href =
            frameUri?.AbsoluteUri ??
            frameDocument?.URL ??
            frameDocument?.DocumentURI ??
            frameDocument?.BaseURI ??
            ResolveElementUrlProperty(iframe, "src");

        return string.IsNullOrWhiteSpace(href) ? "about:blank" : href;
    }

    private JsValue CreatePlainLocationObject(string href)
    {
        var parsed = ParseFenJsUrl(new[] { JsValue.FromString(href ?? string.Empty) });
        if (parsed.Tag == JsValueTag.Object)
        {
            return parsed;
        }

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["href"] = JsValue.FromString(href ?? string.Empty),
            ["origin"] = JsValue.FromString("null"),
            ["protocol"] = JsValue.FromString(string.Empty),
            ["host"] = JsValue.FromString(string.Empty),
            ["hostname"] = JsValue.FromString(string.Empty),
            ["port"] = JsValue.FromString(string.Empty),
            ["pathname"] = JsValue.FromString(string.Empty),
            ["search"] = JsValue.FromString(string.Empty),
            ["hash"] = JsValue.FromString(string.Empty)
        });
    }

    private void QueueWindowMessage(
        JsValue targetWindow,
        List<BrowserEventListener> listeners,
        JsValue data,
        JsValue sourceWindow,
        string targetOrigin,
        string targetWindowOriginOverride = null)
    {
        if (!ShouldDeliverWindowMessage(targetWindow, targetOrigin, targetWindowOriginOverride))
        {
            return;
        }

        var origin = GetMessageSourceOrigin(sourceWindow);
        _ = Task.Run(() =>
        {
            try
            {
                RunFenJsWithLargeStack<object>(() =>
                {
                    lock (_fenJsLock)
                    {
                        using (ActivateWindowCallbackContext(targetWindow, listeners))
                        {
                            var eventValue = CreateMessageEventValue(data, origin, sourceWindow, targetWindow);
                            var handler = ReadJsProperty(targetWindow, "onmessage");
                            if (_interpreter.CanCallValue(handler))
                            {
                                TryInvokeFenJsEventCallback(handler, targetWindow, eventValue, "message");
                            }

                            DispatchBrowserEvent(listeners, "message", targetWindow, eventValue);
                            _interpreter.PumpMicrotasks();
                            RecordMicrotaskCheckpoint("postMessage");
                        }
                    }

                    return null;
                });
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] postMessage delivery failed: {ex.Message}",
                    FenBrowser.Core.Logging.LogCategory.JavaScript);
            }

            try { RequestRender?.Invoke(); }
            catch { /* render request is best-effort */ }
        });
    }

    private bool DispatchFrameWindowHostEvent(
        JsValue frameWindow,
        List<BrowserEventListener> listeners,
        JsValue eventValue)
    {
        var type = ReadEventType(eventValue);
        PrepareDispatchedEvent(eventValue, frameWindow);
        DispatchBrowserEvent(listeners, type, frameWindow, eventValue);

        var handler = ReadJsProperty(frameWindow, "on" + type);
        if (_interpreter.CanCallValue(handler))
        {
            TryInvokeFenJsEventCallback(handler, frameWindow, eventValue, type);
        }

        return !ReadJsBoolProperty(eventValue, "defaultPrevented");
    }

    private JsValue CreateMessageEventValue(
        JsValue data,
        string origin,
        JsValue sourceWindow,
        JsValue targetWindow)
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString("message"),
            ["data"] = data,
            ["origin"] = JsValue.FromString(origin ?? string.Empty),
            ["lastEventId"] = JsValue.FromString(string.Empty),
            ["source"] = sourceWindow.Tag == JsValueTag.Undefined ? JsValue.Null : sourceWindow,
            ["ports"] = _interpreter.AllocateArray(Array.Empty<JsValue>()),
            ["target"] = targetWindow,
            ["currentTarget"] = targetWindow,
            ["srcElement"] = targetWindow,
            ["bubbles"] = JsValue.FromBoolean(false),
            ["cancelable"] = JsValue.FromBoolean(false),
            ["defaultPrevented"] = JsValue.FromBoolean(false),
            ["timeStamp"] = JsValue.FromNumber(_fenJsClock.Elapsed.TotalMilliseconds)
        });
    }

    private bool ShouldDeliverWindowMessage(
        JsValue targetWindow,
        string targetOrigin,
        string targetWindowOriginOverride = null)
    {
        if (string.IsNullOrEmpty(targetOrigin) ||
            string.Equals(targetOrigin, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var expectedOrigin = targetWindowOriginOverride ?? ReadWindowOrigin(targetWindow);
        return string.Equals(
            NormalizePostMessageOrigin(targetOrigin),
            expectedOrigin,
            StringComparison.OrdinalIgnoreCase);
    }

    private string GetMessageSourceOrigin(JsValue sourceWindow)
    {
        if (sourceWindow.Tag == JsValueTag.Object)
        {
            var origin = ReadWindowOrigin(sourceWindow);
            if (!string.IsNullOrEmpty(origin))
            {
                return origin;
            }
        }

        return GetCurrentWindowOrigin();
    }

    private string ReadWindowOrigin(JsValue windowValue)
    {
        var location = ReadJsProperty(windowValue, "location");
        var origin = ReadJsProperty(location, "origin");
        if (origin.Tag != JsValueTag.Undefined && origin.Tag != JsValueTag.Null)
        {
            var originText = CoerceToHostString(origin);
            if (!string.IsNullOrWhiteSpace(originText) &&
                !string.Equals(originText, "undefined", StringComparison.Ordinal))
            {
                return originText;
            }
        }

        var href = ReadJsProperty(location, "href");
        if (href.Tag == JsValueTag.Undefined || href.Tag == JsValueTag.Null)
        {
            return null;
        }

        var hrefText = CoerceToHostString(href);
        if (string.IsNullOrWhiteSpace(hrefText) ||
            string.Equals(hrefText, "undefined", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizePostMessageOrigin(hrefText);
    }

    private string GetCurrentWindowOrigin()
    {
        if (_currentBaseUri != null)
        {
            return NormalizePostMessageOrigin(_currentBaseUri.AbsoluteUri);
        }

        return "null";
    }

    private static string NormalizePostMessageOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return value;
    }

    private void ApplyHistoryUrl(FenJsLocationHost location, string url)
    {
        if (location == null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            UpdateFenJsLocation(location, absolute);
            return;
        }

        if (location.Uri != null && Uri.TryCreate(location.Uri, url, out var resolved))
        {
            UpdateFenJsLocation(location, resolved);
        }
    }

    private void UpdateFenJsLocation(FenJsLocationHost location, Uri uri)
    {
        if (location == null || uri == null)
        {
            return;
        }

        location.Uri = uri;
        _currentBaseUri = uri;
    }

    private JsValue GetOrCreateStyleObject(Element element)
    {
        var store = _hostPropertyStore.GetOrCreateValue(element) ?? new Dictionary<string, JsValue>();
        _hostPropertyStore.AddOrUpdate(element, store);
        if (store.TryGetValue("__fenJsStyle", out var cached))
            return cached;

        var styleObj = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            // cssText getter/setter is wired via Object.defineProperty below.
        });
        _interpreter.SetObjectProperty(styleObj, "setProperty", GetOrCreateHostCallable(
            element, "style.setProperty",
            (_, args) =>
            {
                var prop = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                var val = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                // cssText sentinel: replace the entire inline style.
                if (prop == "__cssText__")
                {
                    var existing = element.GetAttribute("style") ?? string.Empty;
                    if (existing != val)
                        element.SetAttribute("style", val);
                    return JsValue.Undefined;
                }
                // Replace or append the property in the existing style string.
                // Never append duplicate declarations — deduplicate by property name.
                var styleAttr = element.GetAttribute("style") ?? string.Empty;
                bool replaced = false;
                bool changed = false;
                var sb = new System.Text.StringBuilder();
                foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = decl.Trim();
                    if (trimmed.Length == 0) continue;
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var name = trimmed.Substring(0, colonIdx).Trim();
                    if (string.Equals(name, prop, StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace the existing declaration with the new value.
                        var oldVal = trimmed.Substring(colonIdx + 1).Trim();
                        if (!string.Equals(oldVal, val, StringComparison.OrdinalIgnoreCase))
                            changed = true;
                        sb.Append(prop).Append(':').Append(val).Append(';');
                        replaced = true;
                    }
                    else
                    {
                        sb.Append(trimmed).Append(';');
                    }
                }
                if (!replaced)
                {
                    // New property — always a change.
                    sb.Append(prop).Append(':').Append(val).Append(';');
                    changed = true;
                }
                var newStyle = sb.ToString();
                if (changed)
                    element.SetAttribute("style", newStyle);
                return JsValue.Undefined;
            },
            length: 2), enumerable: true);
        _interpreter.SetObjectProperty(styleObj, "getPropertyValue", GetOrCreateHostCallable(
            element, "style.getPropertyValue",
            (_, args) =>
            {
                var prop = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                var styleAttr = element.GetAttribute("style") ?? string.Empty;
                // cssText sentinel: return the full inline style string.
                if (prop == "__cssText__")
                    return JsValue.FromString(styleAttr);
                if (string.IsNullOrEmpty(styleAttr) || string.IsNullOrEmpty(prop))
                    return JsValue.FromString(string.Empty);
                // Parse the inline style to find the requested property value.
                foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var colonIdx = decl.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var name = decl.Substring(0, colonIdx).Trim();
                    if (string.Equals(name, prop, StringComparison.OrdinalIgnoreCase))
                        return JsValue.FromString(decl.Substring(colonIdx + 1).Trim());
                }
                return JsValue.FromString(string.Empty);
            },
            length: 1), enumerable: true);
        _interpreter.SetObjectProperty(styleObj, "removeProperty", GetOrCreateHostCallable(
            element, "style.removeProperty",
            (_, args) =>
            {
                var prop = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                var styleAttr = element.GetAttribute("style") ?? string.Empty;
                if (string.IsNullOrEmpty(styleAttr) || string.IsNullOrEmpty(prop))
                    return JsValue.FromString(string.Empty);
                // Remove the property from the style string and return its old value.
                string oldValue = string.Empty;
                var remaining = new System.Text.StringBuilder();
                foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var colonIdx = decl.IndexOf(':');
                    if (colonIdx < 0) { remaining.Append(decl).Append(';'); continue; }
                    var name = decl.Substring(0, colonIdx).Trim();
                    if (string.Equals(name, prop, StringComparison.OrdinalIgnoreCase))
                    {
                        oldValue = decl.Substring(colonIdx + 1).Trim();
                    }
                    else
                    {
                        remaining.Append(decl.Trim()).Append(';');
                    }
                }
                element.SetAttribute("style", remaining.ToString());
                return JsValue.FromString(oldValue);
            },
            length: 1), enumerable: true);

        // Register the raw style object under a temporary global so the JS
        // snippet below can wrap it with property forwarding.
        var tempStyleName = "__fenStyleTmp" + Interlocked.Increment(ref _temporaryFenJsGlobalCounter)
            .ToString(CultureInfo.InvariantCulture);
        _interpreter.RegisterGlobalValue(tempStyleName, styleObj);
        try
        {
            // Define getter/setter forwarding for the most common CSS
            // properties so that `el.style.display = "block"` and
            // `el.style.opacity` work without going through setProperty().
            var cssProps = new[]
            {
                "display", "opacity", "visibility", "width", "height",
                "minWidth", "minHeight", "maxWidth", "maxHeight",
                "color", "backgroundColor", "background", "backgroundImage",
                "position", "top", "right", "bottom", "left",
                "margin", "marginTop", "marginRight", "marginBottom", "marginLeft",
                "padding", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
                "border", "borderTop", "borderRight", "borderBottom", "borderLeft",
                "borderWidth", "borderColor", "borderRadius",
                "fontSize", "fontFamily", "fontWeight", "fontStyle",
                "lineHeight", "textAlign", "textDecoration", "textTransform",
                "zIndex", "overflow", "overflowX", "overflowY",
                "transform", "transition", "animation",
                "cursor", "pointerEvents", "userSelect",
                "boxShadow", "boxSizing",
                "flex", "flexDirection", "flexWrap", "justifyContent", "alignItems", "alignContent",
                "gridTemplateColumns", "gridTemplateRows", "gap", "rowGap", "columnGap",
                "whiteSpace", "wordBreak", "wordWrap",
                "verticalAlign", "objectFit", "objectPosition",
                "outline", "outlineWidth", "outlineColor"
            };
            var setPropJs = string.Join("",
                cssProps.Select(p =>
                {
                    var camel = CamelToCssProp(p);
                    return "Object.defineProperty(globalThis." + tempStyleName + ",'" + p + "',{" +
                           "get:function(){return this.getPropertyValue('" + camel + "');}," +
                           "set:function(v){this.setProperty('" + camel + "',''+v);}," +
                           "enumerable:true,configurable:true});";
                }));
            // cssText getter/setter: reading returns the full inline style string,
            // writing replaces the entire inline style via setAttribute('style', v).
            setPropJs += "Object.defineProperty(globalThis." + tempStyleName + ",'cssText',{" +
                         "get:function(){return this.getPropertyValue('__cssText__');}," +
                         "set:function(v){this.setProperty('__cssText__',''+v);}," +
                         "enumerable:true,configurable:true});";
            EvaluateWithFenJsRaw(setPropJs);
        }
        finally
        {
            _interpreter.RegisterGlobalValue(tempStyleName, JsValue.Undefined);
        }

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
        var loadUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        UpdateEventLoopSnapshot(snapshot =>
        {
            snapshot.LoadFired = true;
            snapshot.LoadUtc = loadUtc;
        });
        AddEventLoopRecord("LoadFired", "window");
        LogEventLoop(
            "LoadFired",
            LogSeverity.Info,
            "[FenJsBridge] load dispatched",
            new Dictionary<string, object>
            {
                ["documentReadyState"] = "complete"
            });
        DispatchBrowserEvent(GetActiveWindowEventListeners(), "load", windowValue);

        if (TryReadWindowEventHandler(windowValue, "load", out var onload) &&
            _interpreter.CanCallValue(onload))
        {
            var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["type"] = JsValue.FromString("load"),
                ["target"] = windowValue,
                ["currentTarget"] = windowValue
            });
            TryInvokeFenJsEventCallback(onload, windowValue, eventValue, "load");
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

        UpdateEventLoopSnapshot(snapshot => snapshot.BodyOnloadAttributeExecuted = true);
        AddEventLoopRecord("BodyOnloadAttributeExecuted", "body");
        var bodyValue = ToHostOrNull(body, HostObjectKind.DomElement);
        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString("load"),
            ["target"] = bodyValue,
            ["currentTarget"] = bodyValue
        });

        InvokeFenJsInlineWithEvent(onload, eventValue);
    }

    /// <summary>
    /// Scans all elements in the DOM for HTML inline event-handler attributes
    /// (onclick, onchange, onsubmit, etc.) and compiles them into JS functions
    /// stored in the host property store so DispatchEventForElement can invoke
    /// them when the corresponding native event fires.
    /// </summary>
    private void WireInlineEventHandlers(Node domRoot)
    {
        if (domRoot == null)
        {
            return;
        }

        // Event handler attribute names that correspond to DOM events.
        // Excludes onload (handled separately by InvokeBodyOnloadAttribute).
        var eventNames = new[]
        {
            "click", "dblclick", "contextmenu",
            "mousedown", "mouseup", "mouseover", "mouseout", "mousemove",
            "keydown", "keyup", "keypress",
            "submit", "reset", "change", "input",
            "focus", "blur", "focusin", "focusout",
            "scroll", "wheel",
            "error", "abort",
            "touchstart", "touchend", "touchmove", "touchcancel"
        };

        var elements = domRoot.Descendants().OfType<Element>();
        foreach (var element in elements)
        {
            foreach (var eventName in eventNames)
            {
                var attrName = "on" + eventName;
                var attrValue = element.GetAttribute(attrName);
                if (string.IsNullOrWhiteSpace(attrValue))
                {
                    continue;
                }

                try
                {
                    lock (_fenJsLock)
                    {
                        // Wrap the attribute value in a function that receives
                        // `event` as its parameter — matches what real browsers
                        // do for inline handlers.
                        var source = new SourceText(
                            "(function(event){" + attrValue + "\n})",
                            "<fenbrowser-inline-handler:" + attrName + ">");
                        var compiled = _compiler.CompileScript(source);
                        new BytecodeVerifier().Verify(compiled);
                        var handler = _interpreter.Execute(compiled);
                        // handler is now a callable function; store it so that
                        // DispatchEventForElement can find it via
                        // GetStoredHostPropertyOrUndefined(element, "on" + type).
                        SetStoredHostProperty(element, attrName, handler);
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Warn(
                        $"[FenJsBridge] Failed to wire inline {attrName} handler on " +
                        $"<{element.LocalName}>: {ex.Message}",
                        LogCategory.JavaScript);
                }
            }
        }
    }

    private void DispatchBrowserEvent(
        List<BrowserEventListener> listeners,
        string type,
        JsValue currentTarget,
        JsValue eventValue = default,
        BrowserDomEventDispatchState dispatchState = null)
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

        var createdEventValue = false;
        if (eventValue.Tag == JsValueTag.Undefined)
        {
            eventValue = CreateBrowserDomEventValue(null, type, null, out dispatchState);
            createdEventValue = true;
        }

        if (createdEventValue)
        {
            _interpreter.SetObjectProperty(eventValue, "target", currentTarget);
            _interpreter.SetObjectProperty(eventValue, "srcElement", currentTarget);
        }
        _interpreter.SetObjectProperty(eventValue, "currentTarget", currentTarget);

        foreach (var listener in callbacks)
        {
            TryInvokeFenJsEventCallback(listener.Callback, currentTarget, eventValue, type);
            if (listener.Once)
            {
                listeners.RemoveAll(existing =>
                    string.Equals(existing.Type, listener.Type, StringComparison.Ordinal) &&
                    existing.Capture == listener.Capture &&
                    existing.Callback.Equals(listener.Callback));
            }
        }
    }

    private bool DispatchDocumentHostEvent(Document document, JsValue eventValue)
    {
        if (document == null)
        {
            return true;
        }

        var type = ReadEventType(eventValue);
        var target = ToHostOrNull(document, HostObjectKind.DomDocument);
        PrepareDispatchedEvent(eventValue, target);
        DispatchBrowserEvent(_documentEventListeners, type, target, eventValue);
        return !ReadJsBoolProperty(eventValue, "defaultPrevented");
    }

    private bool DispatchWindowHostEvent(JsValue eventValue)
    {
        var type = ReadEventType(eventValue);
        var target = GetActiveWindowEventTarget();
        PrepareDispatchedEvent(eventValue, target);
        DispatchBrowserEvent(GetActiveWindowEventListeners(), type, target, eventValue);

        if (TryReadWindowEventHandler(target, type, out var handler) &&
            _interpreter.CanCallValue(handler))
        {
            TryInvokeFenJsEventCallback(handler, target, eventValue, type);
        }

        return !ReadJsBoolProperty(eventValue, "defaultPrevented");
    }

    private bool TryReadWindowEventHandler(JsValue target, string type, out JsValue handler)
    {
        handler = JsValue.Undefined;
        if (target.Tag == JsValueTag.Object)
        {
            handler = ReadJsProperty(target, "on" + type);
            if (handler.Tag != JsValueTag.Undefined)
            {
                return true;
            }
        }

        return _interpreter.TryReadGlobalValue("on" + type, out handler);
    }

    private bool DispatchElementHostEvent(Element element, JsValue eventValue)
    {
        if (element == null)
        {
            return true;
        }

        var type = ReadEventType(eventValue);
        var target = ToHostOrNull(element, HostObjectKind.DomElement);
        PrepareDispatchedEvent(eventValue, target);

        // Use full dispatch with capture/bubble phases. Pass null for dispatchState
        // because JS-dispatched events track propagation via _propagationStopped on
        // the event object itself (set by Event.prototype.stopPropagation).
        return DispatchEventFull(element, type, eventValue, dispatchState: null);
    }

    private string ReadEventType(JsValue eventValue)
    {
        if (eventValue.Tag == JsValueTag.Undefined || eventValue.Tag == JsValueTag.Null)
        {
            ThrowDomException("TypeError", "Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
        }

        var typeValue = ReadJsProperty(eventValue, "type");
        var type = typeValue.Tag == JsValueTag.Undefined || typeValue.Tag == JsValueTag.Null
            ? string.Empty
            : CoerceToHostString(typeValue);
        if (string.IsNullOrEmpty(type))
        {
            ThrowDomException("InvalidStateError", "Failed to execute 'dispatchEvent': event type is empty.");
        }

        return type;
    }

    private void PrepareDispatchedEvent(JsValue eventValue, JsValue target)
    {
        if (eventValue.Tag != JsValueTag.Object)
        {
            return;
        }

        var currentTarget = ReadJsProperty(eventValue, "target");
        if (currentTarget.Tag == JsValueTag.Undefined || currentTarget.Tag == JsValueTag.Null)
        {
            _interpreter.SetObjectProperty(eventValue, "target", target);
            _interpreter.SetObjectProperty(eventValue, "srcElement", target);
        }

        _interpreter.SetObjectProperty(eventValue, "currentTarget", target);
    }

    private JsValue CreateBrowserDomEventValue(
        Element element,
        string type,
        BrowserDomEventInit eventInit,
        out BrowserDomEventDispatchState dispatchState)
    {
        eventInit ??= new BrowserDomEventInit();
        var state = new BrowserDomEventDispatchState();
        var target = element == null ? JsValue.Null : ToHostOrNull(element, HostObjectKind.DomElement);
        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString(type ?? string.Empty),
            ["target"] = target,
            ["currentTarget"] = target,
            ["srcElement"] = target,
            ["bubbles"] = JsValue.FromBoolean(eventInit.Bubbles),
            ["cancelable"] = JsValue.FromBoolean(eventInit.Cancelable),
            ["composed"] = JsValue.FromBoolean(eventInit.Composed),
            ["isTrusted"] = JsValue.FromBoolean(eventInit.IsTrusted),
            ["defaultPrevented"] = JsValue.FromBoolean(false),
            ["clientX"] = JsValue.FromNumber(eventInit.ClientX),
            ["clientY"] = JsValue.FromNumber(eventInit.ClientY),
            ["pageX"] = JsValue.FromNumber(eventInit.PageX),
            ["pageY"] = JsValue.FromNumber(eventInit.PageY),
            ["screenX"] = JsValue.FromNumber(eventInit.ScreenX),
            ["screenY"] = JsValue.FromNumber(eventInit.ScreenY),
            ["x"] = JsValue.FromNumber(eventInit.ClientX),
            ["y"] = JsValue.FromNumber(eventInit.ClientY),
            ["button"] = JsValue.FromInt32(eventInit.Button),
            ["buttons"] = JsValue.FromInt32(eventInit.Buttons),
            ["pointerId"] = JsValue.FromInt32(eventInit.PointerId),
            ["pointerType"] = JsValue.FromString(string.IsNullOrWhiteSpace(eventInit.PointerType) ? "mouse" : eventInit.PointerType),
            ["pressure"] = JsValue.FromNumber(eventInit.Pressure),
            ["isPrimary"] = JsValue.FromBoolean(eventInit.IsPrimary),
            ["timeStamp"] = JsValue.FromNumber(_fenJsClock.Elapsed.TotalMilliseconds)
        });

        _interpreter.SetObjectProperty(eventValue, "preventDefault", _interpreter.AllocateNativeFunction(
            "preventDefault",
            (_, _) =>
            {
                if (eventInit.Cancelable)
                {
                    state.DefaultPrevented = true;
                    _interpreter.SetObjectProperty(eventValue, "defaultPrevented", JsValue.FromBoolean(true));
                }

                return JsValue.Undefined;
            },
            length: 0));
        _interpreter.SetObjectProperty(eventValue, "stopPropagation", _interpreter.AllocateNativeFunction(
            "stopPropagation",
            (_, _) =>
            {
                state.StopPropagation = true;
                _interpreter.SetObjectProperty(eventValue, "cancelBubble", JsValue.FromBoolean(true));
                return JsValue.Undefined;
            },
            length: 0));
        _interpreter.SetObjectProperty(eventValue, "stopImmediatePropagation", _interpreter.AllocateNativeFunction(
            "stopImmediatePropagation",
            (_, _) =>
            {
                state.StopPropagation = true;
                state.StopImmediatePropagation = true;
                _interpreter.SetObjectProperty(eventValue, "cancelBubble", JsValue.FromBoolean(true));
                return JsValue.Undefined;
            },
            length: 0));
        _interpreter.SetObjectProperty(eventValue, "composedPath", _interpreter.AllocateNativeFunction(
            "composedPath",
            (_, _) => _interpreter.AllocateArray(BuildComposedPathValues(element)),
            length: 0));
        _interpreter.SetObjectProperty(eventValue, "cancelBubble", JsValue.FromBoolean(false));

        dispatchState = state;
        return eventValue;
    }

    private JsValue[] BuildComposedPathValues(Element element)
    {
        if (element == null)
        {
            return Array.Empty<JsValue>();
        }

        var path = new List<JsValue>();
        var current = element;
        while (current != null)
        {
            path.Add(ToHostOrNull(current, HostObjectKind.DomElement));
            current = current.ParentElement;
        }

        var document = element.OwnerDocument;
        if (document != null)
        {
            path.Add(ToHostOrNull(document, HostObjectKind.DomDocument));
        }

        var windowTarget = GetActiveWindowEventTarget();
        if (windowTarget.Tag != JsValueTag.Undefined)
        {
            path.Add(windowTarget);
        }

        return path.ToArray();
    }

    private void InvokeFenJsCallback(JsValue callback, JsValue thisValue, JsValue eventValue)
    {
        RunFenJsWithLargeStack<object>(() =>
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

            return null;
        });
    }

    private bool TryInvokeFenJsEventCallback(JsValue callback, JsValue thisValue, JsValue eventValue, string eventType)
    {
        try
        {
            if (_interpreter.CanCallValue(callback))
            {
                InvokeFenJsCallback(callback, thisValue, eventValue);
            }
            else
            {
                var handleEvent = ReadJsProperty(callback, "handleEvent");
                if (!_interpreter.CanCallValue(handleEvent))
                {
                    return false;
                }

                InvokeFenJsCallback(handleEvent, callback, eventValue);
            }

            return true;
        }
        catch (JsThrownException ex)
        {
            var description = ex.Description;
            if (string.IsNullOrEmpty(description))
            {
                try { description = _interpreter?.DescribeThrownValue(ex.Value); } catch { }
            }

            EngineLogCompat.Warn(
                $"[FenJsBridge] Event listener for '{eventType ?? string.Empty}' failed: {description ?? ex.Message}",
                LogCategory.JavaScript);
            return false;
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn(
                $"[FenJsBridge] Event listener for '{eventType ?? string.Empty}' failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.JavaScript);
            return false;
        }
    }

    private void InvokeFenJsInlineWithEvent(string script, JsValue eventValue)
    {
        RunFenJsWithLargeStack<object>(() =>
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
                catch (Exception ex)
                {
                    FenLogger.Warn(
                        $"[FenJsBridge] Inline JS handler failed: {ex.GetType().Name}: {ex.Message}",
                        LogCategory.JavaScript);
                }
                finally
                {
                    _interpreter.RegisterGlobalValue("event", previousEvent);
                }
            }

            return null;
        });
    }

    private void ParseEventListenerOptions(JsValue options, out bool capture, out bool once)
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
            case JsValueTag.Object:
                capture = ReadJsBoolProperty(options, "capture");
                once = ReadJsBoolProperty(options, "once");
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

        var scriptRecord = AddDynamicScriptLoadingRecord(scriptElement);
        var src = scriptElement.GetAttribute("src");
        var discoveredFields = CreateScriptRecordFields(scriptRecord);
        discoveredFields["dynamic"] = true;
        LogScriptLoading(
            "ScriptDiscovered",
            LogSeverity.Debug,
            "[FenJsBridge] Dynamic script element seen",
            discoveredFields);

        // Inline script: execute textContent directly.
        if (string.IsNullOrWhiteSpace(src))
        {
            var inlineCode = scriptElement.TextContent;
            if (!string.IsNullOrWhiteSpace(inlineCode))
            {
                TraceScriptReady(scriptRecord, inlineCode.Length, "dynamic-inline");
                DispatchScriptElementEvent(
                    scriptElement,
                    ExecuteDynamicScriptCode(scriptElement, scriptRecord, inlineCode, "dynamic-inline", null) ? "load" : "error");
            }
            else
            {
                MarkScriptSkipped(scriptRecord, "empty-inline-code");
            }

            return ToHostNodeOrNull(scriptElement);
        }

        // Data: URLs contain inline code — decode and execute directly.
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var dataCode = DecodeDataUrl(src);
            if (dataCode != null)
            {
                TraceScriptReady(scriptRecord, dataCode.Length, "dynamic-data");
                DispatchScriptElementEvent(
                    scriptElement,
                    ExecuteDynamicScriptCode(scriptElement, scriptRecord, dataCode, "dynamic-data", null) ? "load" : "error");
            }
            else
            {
                MarkScriptSkipped(scriptRecord, "invalid-data-url");
                DispatchScriptElementEvent(scriptElement, "error");
            }

            return ToHostNodeOrNull(scriptElement);
        }

        // External script: fetch and execute.
        if (!AllowExternalScripts || !Sandbox.Allows(SandboxFeature.ExternalScripts))
        {
            MarkScriptSkipped(scriptRecord, $"external-disabled:allowExternal={AllowExternalScripts};sandbox={Sandbox.Allows(SandboxFeature.ExternalScripts)}");
            return ToHostNodeOrNull(scriptElement);
        }

        var baseUri = _currentBaseUri;
        if (baseUri == null || !Uri.TryCreate(baseUri, src, out var scriptUri))
        {
            MarkScriptSkipped(scriptRecord, $"unresolvable-src:baseUri={baseUri}");
            DispatchScriptElementEvent(scriptElement, "error");
            return ToHostNodeOrNull(scriptElement);
        }

        if (SubresourceAllowed != null && !SubresourceAllowed(scriptUri, "script"))
        {
            MarkScriptSkipped(scriptRecord, $"csp-block:{scriptUri}");
            DispatchScriptElementEvent(scriptElement, "error");
            return ToHostNodeOrNull(scriptElement);
        }

        try
        {
            UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchStarted++);
            UpdateScriptLoadingRecord(scriptRecord, record =>
            {
                record.ResolvedUrl = scriptUri.AbsoluteUri;
                record.Status = "fetch-started";
            });
            var fetchStartedFields = CreateScriptRecordFields(scriptRecord);
            fetchStartedFields["url"] = scriptUri.AbsoluteUri;
            fetchStartedFields["batch"] = "dynamic";
            LogScriptLoading(
                "ScriptFetchStarted",
                LogSeverity.Debug,
                "[FenJsBridge] Dynamic external script fetch started",
                fetchStartedFields);

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
                MarkScriptSkipped(scriptRecord, "fetcher-missing");
                DispatchScriptElementEvent(scriptElement, "error");
                return ToHostNodeOrNull(scriptElement);
            }

            UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchCompleted++);
            UpdateScriptLoadingRecord(scriptRecord, record =>
            {
                record.CodeLength = code?.Length ?? 0;
                record.Status = "ready";
            });
            var fetchCompletedFields = CreateScriptRecordFields(scriptRecord);
            fetchCompletedFields["url"] = scriptUri.AbsoluteUri;
            fetchCompletedFields["batch"] = "dynamic";
            fetchCompletedFields["codeLength"] = code?.Length ?? 0;
            LogScriptLoading(
                "ScriptFetchCompleted",
                LogSeverity.Debug,
                "[FenJsBridge] Dynamic external script fetch completed",
                fetchCompletedFields);

            if (!string.IsNullOrWhiteSpace(code))
            {
                TraceScriptReady(scriptRecord, code.Length, "dynamic");
                if (!ExecuteDynamicScriptCode(scriptElement, scriptRecord, code, "dynamic", scriptUri))
                {
                    DispatchScriptElementEvent(scriptElement, "error");
                    return ToHostNodeOrNull(scriptElement);
                }
            }
            else
            {
                MarkScriptSkipped(scriptRecord, "empty-code");
            }

            DispatchScriptElementEvent(scriptElement, "load");
        }
        catch (Exception ex)
        {
            UpdateScriptLoadingSnapshot(snapshot => snapshot.FetchFailed++);
            UpdateScriptLoadingRecord(scriptRecord, record =>
            {
                record.Status = "fetch-failed";
                record.Failure = ex.GetType().Name + ": " + ex.Message;
                record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            });
            var fetchFailedFields = CreateScriptRecordFields(scriptRecord);
            fetchFailedFields["url"] = scriptUri.AbsoluteUri;
            fetchFailedFields["batch"] = "dynamic";
            fetchFailedFields["errorType"] = ex.GetType().Name;
            fetchFailedFields["error"] = ex.Message;
            LogScriptLoading(
                "ScriptFetchFailed",
                LogSeverity.Warn,
                "[FenJsBridge] Dynamic script fetch failed",
                fetchFailedFields);
            DispatchScriptElementEvent(scriptElement, "error");
        }

        return ToHostNodeOrNull(scriptElement);
    }

    private void TraceScriptReady(BrowserScriptLoadingRecord scriptRecord, int codeLength, string batchLabel)
    {
        UpdateScriptLoadingRecord(scriptRecord, record =>
        {
            record.CodeLength = codeLength;
            record.Status = "ready";
        });
        var readyFields = CreateScriptRecordFields(scriptRecord);
        readyFields["batch"] = batchLabel ?? string.Empty;
        readyFields["codeLength"] = codeLength;
        LogScriptLoading(
            "ScriptReady",
            LogSeverity.Debug,
            "[FenJsBridge] Script ready",
            readyFields);
    }

    private bool ExecuteDynamicScriptCode(
        Element scriptElement,
        BrowserScriptLoadingRecord scriptRecord,
        string code,
        string batchLabel,
        Uri moduleUri)
    {
        UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionStarted++);
        UpdateScriptLoadingRecord(scriptRecord, record =>
        {
            record.Batch = batchLabel ?? string.Empty;
            record.CodeLength = code?.Length ?? record.CodeLength;
            record.Status = "execution-started";
        });
        var executionStartedFields = CreateScriptRecordFields(scriptRecord);
        executionStartedFields["batch"] = batchLabel ?? string.Empty;
        executionStartedFields["source"] = scriptRecord?.ResolvedUrl ?? scriptRecord?.Src ?? "inline";
        executionStartedFields["isModule"] = string.Equals(scriptRecord?.Kind, "module", StringComparison.OrdinalIgnoreCase);
        executionStartedFields["codeLength"] = code?.Length ?? 0;
        LogScriptLoading(
            "ScriptExecutionStarted",
            LogSeverity.Debug,
            "[FenJsBridge] Dynamic script execution started",
            executionStartedFields);

        SetCurrentScriptElement(scriptElement, scriptRecord);
        try
        {
            if (string.Equals(scriptRecord?.Kind, "module", StringComparison.OrdinalIgnoreCase))
            {
                EvaluateModuleWithFenJs(code, moduleUri);
            }
            else
            {
                EvaluateWithFenJsRaw(code);
            }

            UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionCompleted++);
            UpdateScriptLoadingRecord(scriptRecord, record =>
            {
                record.Status = "executed";
                record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            });
            var executionCompletedFields = CreateScriptRecordFields(scriptRecord);
            executionCompletedFields["batch"] = batchLabel ?? string.Empty;
            executionCompletedFields["source"] = scriptRecord?.ResolvedUrl ?? scriptRecord?.Src ?? "inline";
            executionCompletedFields["isModule"] = string.Equals(scriptRecord?.Kind, "module", StringComparison.OrdinalIgnoreCase);
            LogScriptLoading(
                "ScriptExecutionCompleted",
                LogSeverity.Debug,
                "[FenJsBridge] Dynamic script execution completed",
                executionCompletedFields);
            return true;
        }
        catch (JsThrownException jte)
        {
            var desc = jte.Description;
            if (string.IsNullOrEmpty(desc))
            {
                try { desc = _interpreter.DescribeThrownValue(jte.Value); } catch { }
            }

            TraceDynamicScriptExecutionFailure(scriptRecord, batchLabel, "JsThrownException", desc ?? jte.Message);
            RecordMissingGlobalReference(desc ?? jte.Message, scriptRecord, _currentBaseUri);
            return false;
        }
        catch (Exception ex)
        {
            TraceDynamicScriptExecutionFailure(scriptRecord, batchLabel, ex.GetType().Name, ex.Message);
            return false;
        }
        finally
        {
            SetCurrentScriptElement(null);
        }
    }

    private void TraceDynamicScriptExecutionFailure(
        BrowserScriptLoadingRecord scriptRecord,
        string batchLabel,
        string errorType,
        string error)
    {
        UpdateScriptLoadingSnapshot(snapshot => snapshot.ExecutionFailed++);
        UpdateScriptLoadingRecord(scriptRecord, record =>
        {
            record.Status = "execution-failed";
            record.Failure = error ?? string.Empty;
            record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        });
        var scriptFailedFields = CreateScriptRecordFields(scriptRecord);
        scriptFailedFields["batch"] = batchLabel ?? string.Empty;
        scriptFailedFields["origin"] = scriptRecord?.Src ?? "inline";
        scriptFailedFields["errorType"] = errorType ?? string.Empty;
        scriptFailedFields["error"] = error ?? string.Empty;
        LogScriptLoading(
            "ScriptExecutionFailed",
            LogSeverity.Error,
            "[FenJsBridge] Dynamic script error",
            scriptFailedFields,
            LogMarker.EngineBug);
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
            TryInvokeFenJsEventCallback(handler, ToHostOrNull(scriptElement, HostObjectKind.DomElement), eventValue, type);
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

    private void OpenDocumentForWrite(Document document)
    {
        if (document?.Body is not ContainerNode body)
        {
            return;
        }

        while (body.FirstChild != null)
        {
            body.RemoveChild(body.FirstChild);
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
        if (ReferenceEquals(currentScript?.OwnerDocument, document) &&
            currentScript.ParentNode is ContainerNode scriptParent)
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

    public bool TryResolveHostObject(JsValue value, out object hostObject)
    {
        hostObject = ResolveHostObjectOrNull(value);
        return hostObject != null;
    }

    /// <summary>
    /// Convert a FenJS JsValue to a .NET object recursively, suitable for
    /// WebDriver serialisation. Primitive values map to their .NET equivalents;
    /// arrays become List&lt;object&gt;; plain objects become Dictionary&lt;string,object&gt;;
    /// host objects are resolved through the host-object table.
    /// </summary>
    public object ConvertJsValueToObject(JsValue value)
    {
        return ConvertJsValueToObjectImpl(value, new HashSet<long>(), 0);
    }

    private object ConvertJsValueToObjectImpl(JsValue value, HashSet<long> visited, int depth)
    {
        if (depth > 16) return "[MaxDepth]";

        switch (value.Tag)
        {
            case JsValueTag.Undefined:
            case JsValueTag.Null:
                return null;
            case JsValueTag.Boolean:
                return value.AsBoolean();
            case JsValueTag.Int32:
                return value.AsInt32();
            case JsValueTag.Number:
                return value.AsNumber();
            case JsValueTag.String:
                return value.AsString();
            case JsValueTag.Symbol:
                return value.AsSymbolDescription() ?? "Symbol()";
            case JsValueTag.BigInt:
                return value.AsBigInt().ToString();
            case JsValueTag.Object:
            {
                var handle = value.AsObjectHandle();
                var objId = handle.ToInt64();
                if (!visited.Add(objId)) return "[Circular]";

                if (_interpreter == null) return "[object Object]";

                try
                {
                    var obj = _interpreter.Heap.GetObject(handle);
                    if (obj == null) return "[object Object]";

                    // Detect arrays by checking for non-negative 'length' property
                    if (obj.TryGetOwnProperty("length", out var lenDesc))
                    {
                        int arrLen = -1;
                        if (lenDesc.Value.Tag == JsValueTag.Int32)
                            arrLen = lenDesc.Value.AsInt32();
                        else if (lenDesc.Value.Tag == JsValueTag.Number)
                        {
                            var d = lenDesc.Value.AsNumber();
                            if (d >= 0 && d <= 100_000 && d == Math.Truncate(d))
                                arrLen = (int)d;
                        }

                        if (arrLen >= 0)
                        {
                            var list = new List<object>(Math.Min(arrLen, 10000));
                            for (int i = 0; i < arrLen && i < 10000; i++)
                            {
                                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                if (obj.TryGetOwnProperty(key, out var elemDesc))
                                    list.Add(ConvertJsValueToObjectImpl(elemDesc.Value, visited, depth + 1));
                                else
                                    list.Add(null);
                            }
                            visited.Remove(objId);
                            return list;
                        }
                    }

                    // Plain object → dictionary
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var kv in obj.EnumerateOwnProperties())
                    {
                        if (kv.Key == "length" && (kv.Value.Value.Tag == JsValueTag.Int32 || kv.Value.Value.Tag == JsValueTag.Number))
                            continue; // length on array-like was handled above
                        dict[kv.Key] = ConvertJsValueToObjectImpl(kv.Value.Value, visited, depth + 1);
                    }
                    visited.Remove(objId);
                    return dict;
                }
                catch
                {
                    visited.Remove(objId);
                    return "[object Object]";
                }
            }
            case JsValueTag.HostObject:
            {
                if (TryResolveHostObject(value, out var hostObj))
                {
                    return hostObj; // Let BrowserApi handle element registration
                }
                return "[object HostObject]";
            }
            default:
                return "[Unknown]";
        }
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
            return CreateDomRect(0, 0, 0, 0);
        }

        var r = box.BorderBox;
        return CreateDomRect(r.Left, r.Top, r.Width, r.Height);
    }

    private JsValue CreateDomRect(double left, double top, double width, double height)
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["x"] = JsValue.FromNumber(left),
            ["y"] = JsValue.FromNumber(top),
            ["width"] = JsValue.FromNumber(width),
            ["height"] = JsValue.FromNumber(height),
            ["top"] = JsValue.FromNumber(top),
            ["right"] = JsValue.FromNumber(left + width),
            ["bottom"] = JsValue.FromNumber(top + height),
            ["left"] = JsValue.FromNumber(left),
        });
    }

    private JsValue CreateEmptyDomRectList()
    {
        var array = _interpreter.AllocateArray(Array.Empty<JsValue>());
        _interpreter.SetObjectProperty(
            array,
            "item",
            _interpreter.AllocateNativeFunction("item", (_, _) => JsValue.Null, length: 1),
            enumerable: false);
        return array;
    }

    private DocumentFragment CreateContextualFragment(DomRange range, string html)
    {
        var context = ResolveRangeContextElement(range);
        if (context == null)
        {
            return (range?.CommonAncestorContainer?.OwnerDocument ?? new Document()).CreateDocumentFragment();
        }

        return HtmlParser.ParseFragment(
            context,
            html ?? string.Empty,
            new HtmlParserOptions { BaseUri = _currentBaseUri },
            out _);
    }

    private static Element ResolveRangeContextElement(DomRange range)
    {
        for (var node = range?.CommonAncestorContainer; node != null; node = node.ParentNode)
        {
            if (node is Element element)
            {
                return element;
            }

            if (node is Document document)
            {
                return document.Body ?? document.DocumentElement;
            }
        }

        return null;
    }

    private void ThrowHierarchyRequestError(string message)
    {
        ThrowDomException("HierarchyRequestError", message ?? "Hierarchy request error.");
    }

    private void ThrowDomException(string name, string message)
    {
        var exceptionName = string.IsNullOrWhiteSpace(name) ? "Error" : name;
        var exceptionMessage = message ?? string.Empty;
        var context = (FenBrowser.Js.Builtins.IBuiltinContext)_interpreter;
        var errorValue = context.CreateError(exceptionMessage);
        var error = _interpreter.Heap.GetObject(errorValue.AsObjectHandle());
        error.DefineOwnProperty(
            "name",
            new FenBrowser.Js.Objects.JsPropertyDescriptor(
                JsValue.FromString(exceptionName),
                Writable: true,
                Enumerable: false,
                Configurable: true));

        throw new JsThrownException(errorValue);
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

    /// <summary>
    /// Converts a JS camelCase property name to a CSS kebab-case property name.
    /// e.g. "backgroundColor" → "background-color", "zIndex" → "z-index".
    /// </summary>
    private static string CamelToCssProp(string camel)
    {
        if (string.IsNullOrEmpty(camel))
        {
            return camel;
        }

        var sb = new StringBuilder(camel.Length + 4);
        for (int i = 0; i < camel.Length; i++)
        {
            var ch = camel[i];
            if (char.IsUpper(ch))
            {
                sb.Append('-');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string CssPropToCamel(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || propertyName.IndexOf('-') < 0)
        {
            return propertyName;
        }

        var sb = new StringBuilder(propertyName.Length);
        var uppercaseNext = false;
        for (int i = 0; i < propertyName.Length; i++)
        {
            var ch = propertyName[i];
            if (ch == '-')
            {
                uppercaseNext = true;
                continue;
            }

            sb.Append(uppercaseNext ? char.ToUpperInvariant(ch) : ch);
            uppercaseNext = false;
        }

        return sb.ToString();
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

    private static uint CoerceToHostUInt32(JsValue value, uint fallback)
    {
        switch (value.Tag)
        {
            case JsValueTag.Int32:
                return unchecked((uint)value.AsInt32());
            case JsValueTag.Number:
                var number = value.AsNumber();
                if (double.IsFinite(number))
                {
                    return unchecked((uint)number);
                }
                break;
            case JsValueTag.String:
                if (uint.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                if (double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) &&
                    double.IsFinite(parsedDouble))
                {
                    return unchecked((uint)parsedDouble);
                }
                break;
        }

        return fallback;
    }

    private static double CoerceToFiniteNumber(JsValue value, double fallback)
    {
        switch (value.Tag)
        {
            case JsValueTag.Int32:
                return value.AsInt32();
            case JsValueTag.Number:
                var number = value.AsNumber();
                return double.IsFinite(number) ? number : fallback;
            case JsValueTag.String:
                return double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    double.IsFinite(parsed)
                    ? parsed
                    : fallback;
            case JsValueTag.Boolean:
                return value.AsBoolean() ? 1 : 0;
            case JsValueTag.Null:
                return 0;
            default:
                return fallback;
        }
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

    private JsValue CreateNodeFilterConstantsObject()
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["FILTER_ACCEPT"] = JsValue.FromInt32((int)NodeFilterResult.Accept),
            ["FILTER_REJECT"] = JsValue.FromInt32((int)NodeFilterResult.Reject),
            ["FILTER_SKIP"] = JsValue.FromInt32((int)NodeFilterResult.Skip),
            ["SHOW_ALL"] = JsValue.FromNumber(NodeFilterShow.All),
            ["SHOW_ELEMENT"] = JsValue.FromInt32((int)NodeFilterShow.Element),
            ["SHOW_ATTRIBUTE"] = JsValue.FromInt32((int)NodeFilterShow.Attribute),
            ["SHOW_TEXT"] = JsValue.FromInt32((int)NodeFilterShow.Text),
            ["SHOW_CDATA_SECTION"] = JsValue.FromInt32((int)NodeFilterShow.CDataSection),
            ["SHOW_ENTITY_REFERENCE"] = JsValue.FromInt32((int)NodeFilterShow.EntityReference),
            ["SHOW_ENTITY"] = JsValue.FromInt32((int)NodeFilterShow.Entity),
            ["SHOW_PROCESSING_INSTRUCTION"] = JsValue.FromInt32((int)NodeFilterShow.ProcessingInstruction),
            ["SHOW_COMMENT"] = JsValue.FromInt32((int)NodeFilterShow.Comment),
            ["SHOW_DOCUMENT"] = JsValue.FromInt32((int)NodeFilterShow.Document),
            ["SHOW_DOCUMENT_TYPE"] = JsValue.FromInt32((int)NodeFilterShow.DocumentType),
            ["SHOW_DOCUMENT_FRAGMENT"] = JsValue.FromInt32((int)NodeFilterShow.DocumentFragment),
            ["SHOW_NOTATION"] = JsValue.FromInt32((int)NodeFilterShow.Notation)
        });
    }

    private JsValue CreateCssGlobalObject()
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["supports"] = _interpreter.AllocateNativeFunction(
                "supports",
                (_, args) => JsValue.FromBoolean(CssSupports(args)),
                length: 1),
            ["escape"] = _interpreter.AllocateNativeFunction(
                "escape",
                (_, args) => JsValue.FromString(CssEscape(args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty)),
                length: 1)
        });
    }

    private JsValue CreateEmptyStyleSheetListObject()
    {
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["length"] = JsValue.FromInt32(0),
            ["item"] = _interpreter.AllocateNativeFunction(
                "item",
                (_, _) => JsValue.Null,
                length: 1)
        });
    }

    private static bool CssSupports(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        if (args.Count == 1)
        {
            return IsPlausibleCssSupportsCondition(CoerceToHostString(args[0]));
        }

        var property = CoerceToHostString(args[0]).Trim();
        var value = CoerceToHostString(args[1]).Trim();
        return IsPlausibleCssPropertyName(property) &&
               !string.IsNullOrWhiteSpace(value) &&
               !ContainsCssParseBreaker(value) &&
               HasBalancedCssGrouping(value);
    }

    private static bool IsPlausibleCssSupportsCondition(string condition)
    {
        var text = condition?.Trim();
        if (string.IsNullOrEmpty(text) || ContainsCssParseBreaker(text))
        {
            return false;
        }

        return HasBalancedCssGrouping(text);
    }

    private static bool IsPlausibleCssPropertyName(string property)
    {
        if (string.IsNullOrWhiteSpace(property))
        {
            return false;
        }

        if (property.StartsWith("--", StringComparison.Ordinal))
        {
            return property.Length > 2 && property.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_');
        }

        return property.All(ch => char.IsLetterOrDigit(ch) || ch == '-');
    }

    private static bool ContainsCssParseBreaker(string text)
        => text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0 || text.IndexOf(';') >= 0;

    private static bool HasBalancedCssGrouping(string text)
    {
        var parentheses = 0;
        var brackets = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;

        foreach (var ch in text)
        {
            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case '(':
                    parentheses++;
                    break;
                case ')':
                    if (--parentheses < 0)
                    {
                        return false;
                    }

                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    if (--brackets < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return parentheses == 0 && brackets == 0 && !inSingleQuote && !inDoubleQuote && !escaping;
    }

    private static string CssEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\0')
            {
                sb.Append('\uFFFD');
                continue;
            }

            if ((i == 0 && char.IsDigit(ch)) ||
                (i == 1 && value[0] == '-' && char.IsDigit(ch)))
            {
                sb.Append('\\');
                sb.Append(((int)ch).ToString("x", CultureInfo.InvariantCulture));
                sb.Append(' ');
                continue;
            }

            if (i == 0 && ch == '-' && value.Length == 1)
            {
                sb.Append("\\-");
                continue;
            }

            if (ch >= 0x80 || ch == '-' || ch == '_' || char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                continue;
            }

            sb.Append('\\');
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private NodeFilter CreateTreeWalkerFilter(JsValue filterValue)
    {
        if (filterValue.Tag == JsValueTag.Undefined || filterValue.Tag == JsValueTag.Null)
        {
            return null;
        }

        var callback = filterValue;
        var thisValue = JsValue.Undefined;
        if (!_interpreter.CanCallValue(callback))
        {
            callback = ReadJsProperty(filterValue, "acceptNode");
            thisValue = filterValue;
        }

        if (!_interpreter.CanCallValue(callback))
        {
            return null;
        }

        return node =>
        {
            var result = _interpreter.InvokeFunction(callback, new[] { ToHostNodeOrNull(node) }, thisValue);
            return ToNodeFilterResult(result);
        };
    }

    private static NodeFilterResult ToNodeFilterResult(JsValue value)
    {
        var numeric = value.Tag switch
        {
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Number => double.IsFinite(value.AsNumber()) ? (int)value.AsNumber() : (int)NodeFilterResult.Accept,
            JsValueTag.String when int.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (int)NodeFilterResult.Accept
        };

        return numeric switch
        {
            (int)NodeFilterResult.Reject => NodeFilterResult.Reject,
            (int)NodeFilterResult.Skip => NodeFilterResult.Skip,
            _ => NodeFilterResult.Accept
        };
    }

    private sealed class FenJsHtmlCollectionHost
    {
        public FenJsHtmlCollectionHost(HTMLCollection collection)
        {
            Collection = collection ?? throw new ArgumentNullException(nameof(collection));
        }

        public HTMLCollection Collection { get; }
    }

    private sealed class FenJsTreeWalkerHost
    {
        public FenJsTreeWalkerHost(TreeWalker treeWalker)
        {
            TreeWalker = treeWalker ?? throw new ArgumentNullException(nameof(treeWalker));
        }

        public TreeWalker TreeWalker { get; }
    }

    private sealed class FenJsAnimationHost
    {
        public FenJsAnimationHost(Element target, JsValue keyframes, JsValue options)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Keyframes = keyframes;
            Options = options;
        }

        public Element Target { get; }
        public JsValue Keyframes { get; }
        public JsValue Options { get; }
        public string Id { get; set; } = string.Empty;
        public string PlayState { get; set; } = "idle";
        public double? StartTime { get; set; }
        public double? CurrentTime { get; set; } = 0;
        public double PlaybackRate { get; set; } = 1;
        public bool Pending { get; set; }
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

    /// <summary>
    /// Creates a JS host object representing a popup window created by window.open().
    /// The handle is an opaque object provided by the Host's OpenWindow delegate.
    /// </summary>
    /// <summary>
    /// Mark an element as style, layout, and paint dirty.  MarkDirty
    /// automatically propagates Child*Dirty flags up to the root and notifies
    /// the document, so the next SkiaDomRenderer.Render() call recomputes
    /// style, re-lays out, and rebuilds the paint tree.
    /// Required after DOM mutations that affect rendering but don't go through
    /// the normal dirty-flag path (e.g. dialog.showModal/close).
    /// </summary>
    private static void InvalidatePaintForElement(Element element)
    {
        if (element == null) return;

        // Mark the element dirty so the renderer's dirty-flag checks trigger a
        // paint-tree rebuild.
        element.MarkDirty(
            FenBrowser.Core.Dom.V2.InvalidationKind.Style |
            FenBrowser.Core.Dom.V2.InvalidationKind.Layout |
            FenBrowser.Core.Dom.V2.InvalidationKind.Paint);

        // Notify the CSS engine that this element's state changed so it
        // re-evaluates computed styles.  The dialog's display changes from
        // "none" to "block" based on the [open] attribute checked by
        // UAStyleProvider.  Without a recascade, LastComputedStyles retains
        // the old "display: none" and the layout engine never creates a box.
        FenBrowser.FenEngine.Rendering.ElementStateManager.Instance.NotifyStateChanged(element);
    }

    /// <summary>
    /// Fire-and-forget post of a modal dialog to the UI thread.
    /// The dialog appears visually but JS execution continues without waiting
    /// for the user to dismiss it.  This avoids thread-deadlock between the
    /// FenJS worker, the engine loop, and the Silk.NET game loop.
    /// </summary>
    private static void PostDialogAsync(string type, string message, string defaultValue)
    {
        var bridge = JsDialogBridge.ShowDialog;
        if (bridge == null)
        {
            FenLogger.Warn($"[{type}] JsDialogBridge not installed — dialog suppressed", LogCategory.JavaScript);
            return;
        }

        // Fire the bridge on a thread-pool thread so the caller (the JS worker)
        // returns immediately.  The bridge itself will post the dialog widget
        // creation to the main thread and block the pool thread, not the JS worker.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { bridge(type, message, defaultValue); }
            catch (Exception ex)
            {
                FenLogger.Error($"[PostDialogAsync] {type} dialog failed: {ex.Message}", LogCategory.JavaScript);
            }
        });
    }

    private JsValue CreatePopupWindowHostObject(object handle, string name, string url)
    {
        var obj = _interpreter.AllocateObject(new Dictionary<string, JsValue>());

        // closed — read-only getter
        _interpreter.SetObjectProperty(obj, "closed",
            _interpreter.AllocateNativeFunction("get closed", (_, _2) =>
            {
                var check = JsDialogBridge.IsPopupWindowClosed;
                return JsValue.FromBoolean(check != null && check(handle));
            }, length: 0));

        // name
        _interpreter.SetObjectProperty(obj, "name", JsValue.FromString(name ?? ""));

        // location.href
        var location = _interpreter.AllocateObject(new Dictionary<string, JsValue>());
        _interpreter.SetObjectProperty(location, "href", JsValue.FromString(url ?? "about:blank"));
        _interpreter.SetObjectProperty(obj, "location", location);

        // focus()
        _interpreter.SetObjectProperty(obj, "focus",
            _interpreter.AllocateNativeFunction("focus", (_, _2) => JsValue.Undefined, length: 0));

        // close()
        _interpreter.SetObjectProperty(obj, "close",
            _interpreter.AllocateNativeFunction("close", (_, _2) =>
            {
                var closer = JsDialogBridge.ClosePopupWindow;
                closer?.Invoke(handle);
                return JsValue.Undefined;
            }, length: 0));

        // document — popup document with open/write/writeln/close
        var document = _interpreter.AllocateObject(new Dictionary<string, JsValue>());
        var htmlBuffer = new System.Text.StringBuilder();

        _interpreter.SetObjectProperty(document, "readyState",
            JsValue.FromString("complete"));

        _interpreter.SetObjectProperty(document, "open",
            _interpreter.AllocateNativeFunction("open", (_, _2) =>
            {
                htmlBuffer.Clear();
                return document;
            }, length: 0));

        _interpreter.SetObjectProperty(document, "write",
            _interpreter.AllocateNativeFunction("write", (_, args) =>
            {
                if (args.Count > 0)
                    htmlBuffer.Append(args[0].ToString());
                return JsValue.Undefined;
            }, length: 1));

        _interpreter.SetObjectProperty(document, "writeln",
            _interpreter.AllocateNativeFunction("writeln", (_, args) =>
            {
                if (args.Count > 0)
                    htmlBuffer.Append(args[0].ToString());
                htmlBuffer.Append('\n');
                return JsValue.Undefined;
            }, length: 1));

        _interpreter.SetObjectProperty(document, "close",
            _interpreter.AllocateNativeFunction("close", (_, _2) =>
            {
                var finalizer = JsDialogBridge.FinalizePopupDocument;
                if (finalizer != null && htmlBuffer.Length > 0)
                {
                    finalizer(handle, htmlBuffer.ToString());
                    htmlBuffer.Clear();
                }
                return JsValue.Undefined;
            }, length: 0));

        _interpreter.SetObjectProperty(obj, "document", document);

        // self / window circular references
        _interpreter.SetObjectProperty(obj, "window", obj);
        _interpreter.SetObjectProperty(obj, "self", obj);

        return obj;
    }

    private sealed record BrowserEventListener(string Type, JsValue Callback, bool Capture, bool Once);

    private sealed class BrowserDomEventDispatchState
    {
        public bool DefaultPrevented { get; set; }
        public bool StopPropagation { get; set; }
        public bool StopImmediatePropagation { get; set; }
    }

    private sealed class FenJsLocationHost
    {
        public FenJsLocationHost(Uri uri)
        {
            Uri = uri;
        }

        public Uri Uri { get; set; }
    }

    private sealed class FenJsHistoryHost
    {
        public FenJsHistoryHost(FenJsLocationHost location)
        {
            Location = location ?? throw new ArgumentNullException(nameof(location));
        }

        public FenJsLocationHost Location { get; }
        public int Length { get; set; } = 1;
        public JsValue State { get; set; } = JsValue.Null;
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

            var hostObject = resolution.HostObject;
            var ownerName = GetHostApiOwnerName(hostObject);
            var found = TryGetHostObjectDefinedProperty(hostObject, property, out value);

            if (!found)
            {
                RecordMissingHostApi(ownerName, property);
            }

            return found;
        }

        private bool TryGetHostObjectDefinedProperty(object hostObject, string property, out JsValue value)
        {
            bool found;
            switch (hostObject)
            {
                case Document document:
                    found = TryGetDocumentProperty(document, property, out value);
                    break;
                case Element element:
                    found = TryGetElementProperty(element, property, out value);
                    break;
                case FenJsDomImplementationHost implementation:
                    found = TryGetDomImplementationProperty(implementation, property, out value);
                    break;
                case Attr attr:
                    found = TryGetAttrProperty(attr, property, out value);
                    break;
                case NamedNodeMap namedNodeMap:
                    found = TryGetNamedNodeMapProperty(namedNodeMap, property, out value);
                    break;
                case DOMTokenList tokenList:
                    found = TryGetDomTokenListProperty(tokenList, property, out value);
                    break;
                case CharacterData characterData:
                    found = TryGetCharacterDataProperty(characterData, property, out value);
                    break;
                case DocumentFragment fragment:
                    found = TryGetDocumentFragmentProperty(fragment, property, out value);
                    break;
                case DomRange range:
                    found = TryGetRangeProperty(range, property, out value);
                    break;
                case FenJsHtmlCollectionHost htmlCollection:
                    found = TryGetHtmlCollectionProperty(htmlCollection, property, out value);
                    break;
                case FenJsTreeWalkerHost treeWalker:
                    found = TryGetTreeWalkerProperty(treeWalker, property, out value);
                    break;
                case FenJsAnimationHost animation:
                    found = TryGetAnimationProperty(animation, property, out value);
                    break;
                case FenJsDomStringMapHost domStringMap:
                    found = TryGetDomStringMapProperty(domStringMap, property, out value);
                    break;
                case FenStorageAreaHost storageArea:
                    found = TryGetStorageProperty(storageArea, property, out value);
                    break;
                case BrowserSurfaceProfile navigator:
                    if (TryGetNavigatorProperty(navigator, property, out value))
                    {
                        return true;
                    }
                    // Fall back to user-assigned properties stored via TrySetHostProperty
                    // (e.g. Google stubs navigator.sendBeacon).
                    value = _owner.GetStoredHostPropertyOrUndefined(navigator, property);
                    found = value.Tag != JsValueTag.Undefined;
                    break;
                case FenJsLocationHost location:
                    found = TryGetLocationProperty(location, property, out value);
                    break;
                case FenJsHistoryHost history:
                    found = TryGetHistoryProperty(history, property, out value);
                    break;
                case FenJsMutationObserverHost mutationObserver:
                    found = _owner.TryGetMutationObserverProperty(mutationObserver, property, out value);
                    break;
                default:
                    value = JsValue.Undefined;
                    found = false;
                    break;
            }

            return found;
        }

        private static string GetHostApiOwnerName(object hostObject)
        {
            return hostObject switch
            {
                Document => "Document",
                Element => "Element",
                FenJsDomImplementationHost => "DOMImplementation",
                Attr => "Attr",
                NamedNodeMap => "NamedNodeMap",
                DOMTokenList => "DOMTokenList",
                CharacterData => "CharacterData",
                ShadowRoot => "ShadowRoot",
                DocumentFragment => "DocumentFragment",
                DomRange => "Range",
                FenJsHtmlCollectionHost => "HTMLCollection",
                FenJsTreeWalkerHost => "TreeWalker",
                FenJsAnimationHost => "Animation",
                FenJsDomStringMapHost => "DOMStringMap",
                BrowserSurfaceProfile => "Navigator",
                FenJsLocationHost => "Location",
                FenJsHistoryHost => "History",
                FenJsMutationObserverHost => "MutationObserver",
                _ => hostObject?.GetType().Name ?? "HostObject"
            };
        }

        private void RecordMissingHostApi(string ownerName, string property)
        {
            if (!ShouldRecordMissingHostApi(ownerName, property))
            {
                return;
            }

            _owner?.RecordMissingHostProperty(ownerName, property, _baseUri);
        }

        private static bool ShouldRecordMissingHostApi(string ownerName, string property)
        {
            if (string.IsNullOrWhiteSpace(ownerName) || string.IsNullOrWhiteSpace(property))
            {
                return false;
            }

            if (property == "then" ||
                property == "constructor" ||
                property == "prototype" ||
                property == "__proto__" ||
                property.StartsWith("__", StringComparison.Ordinal) ||
                property.StartsWith("_", StringComparison.Ordinal) ||
                property.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !property.All(char.IsDigit);
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
                case Document document when string.Equals(property, "fonts", StringComparison.Ordinal):
                    // Allow Google Font Loading API: document.fonts = { ... }.
                    _owner.SetStoredHostProperty(document, "fonts", value);
                    return true;
                case Document document when string.Equals(property, "title", StringComparison.Ordinal):
                    document.Title = CoerceToHostString(value);
                    return true;
                case Document document when string.Equals(property, "cookie", StringComparison.Ordinal):
                    var cookieStr = CoerceToHostString(value);
                    document.Cookie = cookieStr;
                    // Persist cookies through the host bridge so they survive
                    // navigations (critical for WAF challenge tokens).
                    if (_owner.CookieWriteBridge != null && _owner._currentBaseUri != null)
                    {
                        _owner.CookieWriteBridge(_owner._currentBaseUri, cookieStr);
                        if (cookieStr.StartsWith("SG_SS=", StringComparison.Ordinal))
                        {
                            var captureId = -Interlocked.Increment(ref _owner._diagnosticCookieCaptureCounter);
                            _owner.CaptureNavigationGlobals(_owner._currentBaseUri, captureId);
                        }
                    }
                    return true;
                case Document document when string.Equals(property, "domain", StringComparison.Ordinal):
                    _owner.SetStoredHostProperty(document, "domain", JsValue.FromString(CoerceToHostString(value)));
                    return true;
                case Document document:
                    // Catch-all for arbitrary document properties (e.g. Google
                    // sets __gwbp, __jsl, and other internal bookkeeping).
                    _owner.SetStoredHostProperty(document, property, value);
                    return true;
                case Element element when string.Equals(property, "className", StringComparison.Ordinal):
                    element.ClassName = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "id", StringComparison.Ordinal):
                    element.Id = CoerceToHostString(value);
                    return true;
                case Element element when string.Equals(property, "value", StringComparison.Ordinal):
                    SetElementValue(element, CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "tabIndex", StringComparison.Ordinal):
                    element.SetAttribute(
                        "tabindex",
                        ((int)CoerceToFiniteNumber(value, 0)).ToString(CultureInfo.InvariantCulture));
                    return true;
                case Element element when string.Equals(property, "name", StringComparison.Ordinal):
                    element.SetAttribute("name", CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "content", StringComparison.Ordinal):
                    element.SetAttribute("content", CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "src", StringComparison.Ordinal):
                    element.SetAttribute("src", CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "href", StringComparison.Ordinal):
                    element.SetAttribute("href", CoerceToHostString(value));
                    return true;
                case Element element when string.Equals(property, "nonce", StringComparison.Ordinal):
                    element.SetAttribute("nonce", CoerceToHostString(value));
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
                case Element element:
                    // Catch-all for arbitrary element properties (e.g. Google sets
                    // __gwbp, __jsl, and other internal bookkeeping properties on
                    // DOM elements). Store for later retrieval via TryGetElementProperty.
                    _owner.SetStoredHostProperty(element, property, value);
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
                case FenStorageAreaHost storageArea:
                    // Per spec, storage[key] = value is equivalent to storage.setItem(key, value).
                    var setError = storageArea.SetItem(property, CoerceToHostString(value));
                    if (setError != null)
                    {
                        // QuotaExceededError — the JS side should throw.
                        // For now we silently ignore quota errors; a full
                        // implementation would throw a DOMException.
                    }
                    return true;
                case FenJsTreeWalkerHost treeWalker when string.Equals(property, "currentNode", StringComparison.Ordinal):
                    var currentNode = _owner.ResolveHostObjectOrNull<Node>(value);
                    if (currentNode == null)
                    {
                        return false;
                    }

                    treeWalker.TreeWalker.CurrentNode = currentNode;
                    return true;
                case FenJsAnimationHost animation when string.Equals(property, "id", StringComparison.Ordinal):
                    animation.Id = CoerceToHostString(value);
                    return true;
                case FenJsAnimationHost animation when string.Equals(property, "currentTime", StringComparison.Ordinal):
                    animation.CurrentTime = value.Tag == JsValueTag.Null ? null : CoerceToFiniteNumber(value, 0);
                    return true;
                case FenJsAnimationHost animation when string.Equals(property, "startTime", StringComparison.Ordinal):
                    animation.StartTime = value.Tag == JsValueTag.Null ? null : CoerceToFiniteNumber(value, 0);
                    return true;
                case FenJsAnimationHost animation when string.Equals(property, "playbackRate", StringComparison.Ordinal):
                    animation.PlaybackRate = CoerceToFiniteNumber(value, 1);
                    return true;
                case FenJsAnimationHost animation:
                    _owner.SetStoredHostProperty(animation, property, value);
                    return true;
                case FenJsLocationHost location when string.Equals(property, "href", StringComparison.Ordinal):
                    var hrefStr = CoerceToHostString(value);
                    if (!string.IsNullOrWhiteSpace(hrefStr) && Uri.TryCreate(location.Uri, hrefStr, out var navUri))
                        _owner._host.Navigate(navUri);
                    return true;
                case FenJsHistoryHost history
                    when string.Equals(property, "pushState", StringComparison.Ordinal) ||
                         string.Equals(property, "replaceState", StringComparison.Ordinal):
                    _owner.SetStoredHostProperty(history, property, value);
                    return true;
                case BrowserSurfaceProfile navigator:
                    // Allow scripts to set arbitrary properties on navigator (e.g.
                    // Google stubs navigator.sendBeacon). Store for later retrieval.
                    _owner.SetStoredHostProperty(navigator, property, value);
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
                case "nodeType":
                    value = JsValue.FromInt32((int)document.NodeType);
                    return true;
                case "parentNode":
                case "ownerDocument":
                    value = JsValue.Null;
                    return true;
                case "id":
                    // Document nodes have no id attribute; return empty string per Chrome behavior.
                    value = JsValue.FromString(string.Empty);
                    return true;
                case "localName":
                    // Document nodes have no localName per DOM spec.
                    value = JsValue.Null;
                    return true;
                case "readyState":
                    value = JsValue.FromString(_owner.GetDocumentReadyState());
                    return true;
                case "compatMode":
                    value = JsValue.FromString("CSS1Compat");
                    return true;
                case "styleSheets":
                    value = _owner.CreateEmptyStyleSheetListObject();
                    return true;
                case "prerendering":
                    value = JsValue.FromBoolean(false);
                    return true;
                case "fonts":
                    // CSS Font Loading API: return the user-assigned fonts object
                    // (set by Google's inline script: document.fonts = { load: ..., ready: ... }).
                    value = _owner.GetStoredHostPropertyOrUndefined(document, "fonts");
                    return value.Tag != JsValueTag.Undefined;
                case "URL":
                case "documentURI":
                    value = JsValue.FromString(document.URL ?? string.Empty);
                    return true;
                case "baseURI":
                    value = JsValue.FromString(
                        document.BaseURI ??
                        document.DocumentURI ??
                        document.URL ??
                        _baseUri?.AbsoluteUri ??
                        string.Empty);
                    return true;
                case "domain":
                    value = _owner.GetStoredHostPropertyOrUndefined(document, "domain");
                    if (value.Tag == JsValueTag.Undefined)
                    {
                        value = JsValue.FromString(TryGetDocumentUri(document)?.Host ?? _baseUri?.Host ?? string.Empty);
                    }
                    return true;
                case "location":
                    value = _owner.ToHostOrNull(_location, HostObjectKind.Other);
                    return true;
                case "contentType":
                    value = JsValue.FromString(document.ContentType ?? "text/html");
                    return true;
                case "visibilityState":
                    value = JsValue.FromString(document.Hidden ? "hidden" : "visible");
                    return true;
                case "hidden":
                    value = JsValue.FromBoolean(document.Hidden);
                    return true;
                case "title":
                    value = JsValue.FromString(document.Title ?? string.Empty);
                    return true;
                case "cookie":
                    // Merge host-persisted cookies with any DOM-level cookies
                    // so the WAF challenge can read back cookies it previously set.
                    var hostCookies = _owner.CookieReadBridge != null && _owner._currentBaseUri != null
                        ? (_owner.CookieReadBridge(_owner._currentBaseUri) ?? string.Empty)
                        : string.Empty;
                    var domCookies = document.Cookie ?? string.Empty;
                    var merged = string.IsNullOrEmpty(hostCookies) ? domCookies
                        : string.IsNullOrEmpty(domCookies) ? hostCookies
                        : hostCookies + "; " + domCookies;
                    value = JsValue.FromString(merged);
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
                case "scrollingElement":
                    value = _owner.ToHostOrNull(document.DocumentElement ?? document.Body, HostObjectKind.DomElement);
                    return true;
                case "currentScript":
                    value = _owner.ToHostOrNull(_owner.GetCurrentScriptElement(), HostObjectKind.DomElement);
                    return true;
                case "defaultView":
                    value = _owner.GetStoredHostPropertyOrUndefined(document, "__fenDefaultView");
                    if (value.Tag == JsValueTag.Undefined)
                    {
                        value = _owner.EvaluateWithFenJsRaw("window");
                    }
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
                            return _owner.UpgradeCustomElementTreeIfDefined(
                                _owner.ToHostNodeOrNull(document.CreateElement(localName)));
                        },
                        length: 1);
                    return true;
                case "createElementNS":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createElementNS",
                        (_, args) =>
                        {
                            var namespaceUri = args.Count > 0 &&
                                               args[0].Tag != JsValueTag.Null &&
                                               args[0].Tag != JsValueTag.Undefined
                                ? CoerceToHostString(args[0])
                                : null;
                            var qualifiedName = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            try
                            {
                                return _owner.UpgradeCustomElementTreeIfDefined(
                                    _owner.ToHostNodeOrNull(document.CreateElementNS(namespaceUri, qualifiedName)));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 2);
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
                case "importNode":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "importNode",
                        (_, args) =>
                        {
                            var node = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (node == null)
                            {
                                _owner.ThrowDomException(
                                    "TypeError",
                                    "Failed to execute 'importNode': parameter 1 is not of type 'Node'.");
                                return JsValue.Undefined;
                            }

                            var deep = args.Count > 1 && CoerceToHostBoolean(args[1]);
                            try
                            {
                                return _owner.ToHostNodeOrNull(document.ImportNode(node, deep));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "createRange":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createRange",
                        (_, _) => _owner.ToHostOrNull(new DomRange(document), HostObjectKind.Other),
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
                case "createEvent":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createEvent",
                        (_, args) =>
                        {
                            var interfaceName = args.Count > 0 ? CoerceToHostString(args[0]) : "Event";
                            return _owner.CreateLegacyDomEvent(interfaceName);
                        },
                        length: 1);
                    return true;
                case "createTreeWalker":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "createTreeWalker",
                        (_, args) =>
                        {
                            var root = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (root == null)
                            {
                                _owner.ThrowDomException(
                                    "TypeError",
                                    "Failed to execute 'createTreeWalker': parameter 1 is not of type 'Node'.");
                            }

                            var whatToShow = args.Count > 1
                                ? CoerceToHostUInt32(args[1], NodeFilterShow.All)
                                : NodeFilterShow.All;
                            var filter = args.Count > 2
                                ? _owner.CreateTreeWalkerFilter(args[2])
                                : null;
                            return _owner.ToHostOrNull(
                                new FenJsTreeWalkerHost(document.CreateTreeWalker(root, whatToShow, filter)),
                                HostObjectKind.Other);
                        },
                        length: 1);
                    return true;
                case "hasStorageAccess":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "hasStorageAccess",
                        (_, _) => _owner.EvaluateWithFenJsRaw("Promise.resolve(true)"),
                        length: 0);
                    return true;
                case "requestStorageAccess":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "requestStorageAccess",
                        (_, _) => _owner.EvaluateWithFenJsRaw("Promise.resolve()"),
                        length: 0);
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

                            return _owner.UpgradeCustomElementTreeIfDefined(
                                _owner.ToHostNodeOrNull(document.AppendChild(child)));
                        },
                        length: 1);
                    return true;
                case "removeChild":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "removeChild",
                        (_, args) =>
                        {
                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull(args[0]) as Node : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Document child must be a DOM node.");
                            }

                            try
                            {
                                return _owner.ToHostNodeOrNull(document.RemoveChild(child));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "replaceChild":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "replaceChild",
                        (_, args) =>
                        {
                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull(args[0]) as Node : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Document replacement child must be a DOM node.");
                            }

                            var oldChild = args.Count > 1 ? _owner.ResolveHostObjectOrNull(args[1]) as Node : null;
                            if (oldChild == null)
                            {
                                _owner.ThrowDomException("TypeError", "Document child must be a DOM node.");
                            }

                            try
                            {
                                var replaced = document.ReplaceChild(child, oldChild);
                                _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(child));
                                return _owner.ToHostNodeOrNull(replaced);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 2);
                    return true;
                case "open":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "open",
                        (_, _) =>
                        {
                            _owner.OpenDocumentForWrite(document);
                            return _owner.ToHostOrNull(document, HostObjectKind.DomDocument);
                        });
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
                case "close":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "close",
                        (_, _) => JsValue.Undefined);
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
                case "dispatchEvent":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "dispatchEvent",
                        (_, args) =>
                        {
                            var eventValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                            return JsValue.FromBoolean(_owner.DispatchDocumentHostEvent(document, eventValue));
                        },
                        length: 1);
                    return true;
                case "startViewTransition":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "startViewTransition",
                        (_, args) => _owner.CreateViewTransitionResult(
                            args.Count > 0 ? args[0] : JsValue.Undefined),
                        length: 1);
                    return true;
                case "releaseCapture":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "releaseCapture",
                        (_, _) => JsValue.Undefined,
                        length: 0);
                    return true;
                case "contains":
                    value = _owner.GetOrCreateHostCallable(
                        document,
                        "contains",
                        (_, args) =>
                        {
                            var other = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            return JsValue.FromBoolean(other != null && document.Contains(other));
                        },
                        length: 1);
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
                    // Fall back to user-assigned properties (e.g. document.fonts,
                    // document.__gwbp set by Google scripts).
                    value = _owner.GetStoredHostPropertyOrUndefined(document, property);
                    return value.Tag != JsValueTag.Undefined;
            }
        }

        private static Uri TryGetDocumentUri(Document document)
        {
            if (document == null)
            {
                return null;
            }

            var raw =
                document.URL ??
                document.DocumentURI ??
                document.BaseURI;

            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
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
                case "name":
                    value = JsValue.FromString(element.GetAttribute("name") ?? string.Empty);
                    return true;
                case "tagName":
                case "nodeName":
                    value = JsValue.FromString(element.TagName ?? string.Empty);
                    return true;
                case "localName":
                    value = JsValue.FromString(element.LocalName ?? string.Empty);
                    return true;
                case "href":
                    value = JsValue.FromString(ResolveElementUrlProperty(element, "href"));
                    return true;
                case "src":
                    value = JsValue.FromString(ResolveElementUrlProperty(element, "src"));
                    return true;
                case "nonce":
                    value = JsValue.FromString(element.GetAttribute("nonce") ?? string.Empty);
                    return true;
                case "value":
                    value = JsValue.FromString(ReadElementValue(element));
                    return true;
                case "tabIndex":
                    value = JsValue.FromInt32(ReadElementTabIndex(element));
                    return true;
                case "contentWindow" when IsIFrameElement(element):
                    value = _owner.GetOrCreateIFrameContentWindow(element);
                    return true;
                case "contentDocument" when IsIFrameElement(element):
                    value = _owner.GetOrCreateIFrameContentDocument(element);
                    return true;
                case "sandbox" when IsIFrameElement(element):
                    value = element.NamespaceUri == "http://www.w3.org/1999/xhtml"
                        ? _owner.GetOrCreateDomTokenListView(element.SandboxList)
                        : JsValue.Undefined;
                    return true;
                case "content" when IsTemplateElement(element):
                    value = _owner.GetOrCreateTemplateContent(element);
                    return true;
                case "content":
                    value = JsValue.FromString(element.GetAttribute("content") ?? string.Empty);
                    return true;
                case "shadowRoot":
                    value = _owner.ToHostNodeOrNull(element.ShadowRoot);
                    return true;
                case "complete" when IsImageElement(element):
                    value = JsValue.FromBoolean(true);
                    return true;
                case "open" when IsDialogElement(element):
                    value = JsValue.FromBoolean(element.HasAttribute("open"));
                    return true;
                case "showModal" when IsDialogElement(element):
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "showModal",
                        (_, _) =>
                        {
                            element.SetAttribute("open", "");
                            element.SetAttribute("data-top-layer", "modal");
                            // OwnerDocument walks up _parentNode to find the Document.
                            // If the element was obtained via getElementById in a FenJS
                            // host-object context, the CLR parent chain is intact and
                            // OwnerDocument is non-null.  If it is null (e.g. detached
                            // element), skip the TopLayer path — the dialog will still
                            // render as a normal positioned element via CSS.
                            var doc = element.OwnerDocument;
                            if (doc != null)
                            {
                                if (!doc.TopLayer.Contains(element))
                                    doc.TopLayer.Add(element);
                            }
                            // Mark style/layout/paint dirty so the renderer rebuilds
                            // the paint tree and picks up the TopLayer change.
                            FenJsBrowserScriptEngine.InvalidatePaintForElement(element);
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "show" when IsDialogElement(element):
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "show",
                        (_, _) =>
                        {
                            element.SetAttribute("open", "");
                            FenJsBrowserScriptEngine.InvalidatePaintForElement(element);
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "close" when IsDialogElement(element):
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "close",
                        (_, args) =>
                        {
                            element.RemoveAttribute("open");
                            element.RemoveAttribute("data-top-layer");
                            element.OwnerDocument?.TopLayer.Remove(element);
                            if (args.Count > 0)
                            {
                                _owner.SetStoredHostProperty(element, "returnValue", args[0]);
                            }

                            _owner.DispatchEventForElement(element, "close");
                            FenJsBrowserScriptEngine.InvalidatePaintForElement(element);
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "returnValue" when IsDialogElement(element):
                    value = _owner.GetStoredHostPropertyOrUndefined(element, "returnValue");
                    if (value.Tag == JsValueTag.Undefined)
                    {
                        value = JsValue.FromString(string.Empty);
                    }
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
                case "dispatchEvent":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "dispatchEvent",
                        (_, args) =>
                        {
                            var eventValue = args.Count > 0 ? args[0] : JsValue.Undefined;
                            return JsValue.FromBoolean(_owner.DispatchElementHostEvent(element, eventValue));
                        },
                        length: 1);
                    return true;
                case "requestFullscreen":
                case "webkitRequestFullscreen":
                case "mozRequestFullScreen":
                case "msRequestFullscreen":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        property,
                        (_, _) => _owner.CreateResolvedPromise(JsValue.Undefined),
                        length: 0);
                    return true;
                case "setCapture":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "setCapture",
                        (_, _) => JsValue.Undefined,
                        length: 0);
                    return true;
                case "setProperties":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "setProperties",
                        (thisValue, args) =>
                        {
                            var target = _owner.ResolveHostObjectOrNull<Element>(thisValue) ?? element;
                            _owner.ApplyElementProperties(target, args.Count > 0 ? args[0] : JsValue.Undefined);
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "animate":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "animate",
                        (_, args) =>
                        {
                            var keyframes = args.Count > 0 ? args[0] : JsValue.Null;
                            var options = args.Count > 1 ? args[1] : JsValue.Undefined;
                            return _owner.ToHostOrNull(new FenJsAnimationHost(element, keyframes, options), HostObjectKind.Other);
                        },
                        length: 1);
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
                case "submit":
                    // HTMLFormElement.submit() — reCAPTCHA calls form.submit() programmatically.
                    if (string.Equals(element.TagName, "FORM", StringComparison.OrdinalIgnoreCase))
                    {
                        value = _owner.GetOrCreateHostCallable(
                            element,
                            "submit",
                            (_, _) =>
                            {
                                _owner.SubmitFormFromScript(element);
                                return JsValue.Undefined;
                            });
                        return true;
                    }
                    value = JsValue.Undefined;
                    return false;
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
                            if ((string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(attributeName, "srcdoc", StringComparison.OrdinalIgnoreCase)) &&
                                IsIFrameElement(element))
                            {
                                _owner.QueueFrameElementLoad(element);
                            }
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
                case "attachShadow":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "attachShadow",
                        (_, args) =>
                        {
                            var init = args.Count > 0 ? args[0] : JsValue.Undefined;
                            var modeValue = _owner.ReadJsProperty(init, "mode");
                            var modeText = modeValue.Tag == JsValueTag.Undefined
                                ? string.Empty
                                : CoerceToHostString(modeValue);
                            var mode = string.Equals(modeText, "closed", StringComparison.Ordinal)
                                ? ShadowRootMode.Closed
                                : string.Equals(modeText, "open", StringComparison.Ordinal)
                                    ? ShadowRootMode.Open
                                    : (ShadowRootMode?)null;

                            if (mode == null)
                            {
                                _owner.ThrowDomException(
                                    "TypeError",
                                    "Failed to execute 'attachShadow' on 'Element': mode must be 'open' or 'closed'.");
                            }

                            var slotAssignmentValue = _owner.ReadJsProperty(init, "slotAssignment");
                            var slotAssignmentText = slotAssignmentValue.Tag == JsValueTag.Undefined
                                ? "named"
                                : CoerceToHostString(slotAssignmentValue);
                            var slotAssignment = string.Equals(slotAssignmentText, "manual", StringComparison.Ordinal)
                                ? SlotAssignmentMode.Manual
                                : SlotAssignmentMode.Named;

                            try
                            {
                                var shadowRoot = element.AttachShadow(new ShadowRootInit
                                {
                                    Mode = mode.Value,
                                    DelegatesFocus = _owner.ReadJsBoolProperty(init, "delegatesFocus"),
                                    SlotAssignment = slotAssignment
                                });
                                var shadowRootValue = _owner.ToHostNodeOrNull(shadowRoot);
                                var upgradeFragmentValue = _owner.ReadJsProperty(init, "shadyUpgradeFragment");
                                _owner._interpreter.CopyHostObjectOwnProperties(upgradeFragmentValue, shadowRootValue);
                                return shadowRootValue;
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
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

                            EngineLogCompat.Warn(
                                $"[FenJsProbe] element.appendChild entering parent={element.NodeName}#{element.Id} child={child.NodeName} type={child.NodeType} childParent={child.ParentNode?.NodeName ?? "null"} childCount={(child is ContainerNode childContainer ? childContainer.ChildNodes.Length : 0)}",
                                LogCategory.JavaScript);
                            var appended = element.AppendChild(child);
                            EngineLogCompat.Warn(
                                $"[FenJsProbe] element.appendChild returned parent={element.NodeName}#{element.Id} appended={appended.NodeName} childCount={(child is ContainerNode afterChildContainer ? afterChildContainer.ChildNodes.Length : 0)}",
                                LogCategory.JavaScript);
                            _owner.QueueFrameLoadsForTree(appended);
                            if (child is Element childElement &&
                                string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                            {
                                _owner.ExecuteDynamicScriptElement(childElement);
                            }

                            return _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(appended));
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
                                _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(child));
                                _owner.QueueFrameLoadsForTree(child);
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
                case "prepend":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "prepend",
                        (_, args) =>
                        {
                            var referenceNode = element.FirstChild;
                            foreach (var arg in args)
                            {
                                if (_owner.ResolveHostObjectOrNull<Attr>(arg) != null)
                                {
                                    _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                                }

                                Node child = _owner.ResolveHostObjectOrNull<Node>(arg);
                                if (child == null)
                                {
                                    child = element.OwnerDocument?.CreateTextNode(CoerceToHostString(arg));
                                }

                                if (child == null)
                                {
                                    continue;
                                }

                                var scriptCandidates = child is DocumentFragment fragment
                                    ? fragment.ChildNodes.OfType<Element>().ToArray()
                                    : child is Element childElement
                                        ? new[] { childElement }
                                        : Array.Empty<Element>();

                                element.InsertBefore(child, referenceNode);
                                _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(child));
                                _owner.QueueFrameLoadsForTree(child);
                                foreach (var scriptElement in scriptCandidates)
                                {
                                    if (string.Equals(scriptElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _owner.ExecuteDynamicScriptElement(scriptElement);
                                    }
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
                            _owner.QueueFrameLoadsForTree(inserted);
                            if (child is Element childElement &&
                                string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                            {
                                _owner.ExecuteDynamicScriptElement(childElement);
                            }

                            return _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(inserted));
                        },
                        length: 2);
                    return true;
                case "removeChild":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "removeChild",
                        (_, args) =>
                        {
                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Child must be a DOM node.");
                            }

                            try
                            {
                                return _owner.ToHostNodeOrNull(element.RemoveChild(child));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "replaceChild":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "replaceChild",
                        (_, args) =>
                        {
                            if (args.Count > 0 && _owner.ResolveHostObjectOrNull<Attr>(args[0]) != null)
                            {
                                _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                            }

                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Replacement child must be a DOM node.");
                            }

                            var oldChild = args.Count > 1 ? _owner.ResolveHostObjectOrNull<Node>(args[1]) : null;
                            if (oldChild == null)
                            {
                                _owner.ThrowDomException("TypeError", "Child must be a DOM node.");
                            }

                            try
                            {
                                var replaced = element.ReplaceChild(child, oldChild);
                                if (child is Element childElement &&
                                    string.Equals(childElement.TagName, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                                {
                                    _owner.ExecuteDynamicScriptElement(childElement);
                                }

                                _owner.UpgradeCustomElementTreeIfDefined(_owner.ToHostNodeOrNull(child));
                                return _owner.ToHostNodeOrNull(replaced);
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
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
                case "getElementsByClassName":
                    value = _owner.GetOrCreateHostCallable(
                        element,
                        "getElementsByClassName",
                        (_, args) =>
                        {
                            var className = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostOrNull(
                                new FenJsHtmlCollectionHost(element.GetElementsByClassName(className)),
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
                case "currentStyle":
                    value = _owner.CreateComputedStyleObjectForElement(element);
                    return true;
                default:
                    // Fall back to user-assigned properties (e.g. Google sets
                    // __gwbp, __jsl on elements for internal bookkeeping).
                    value = _owner.GetStoredHostPropertyOrUndefined(element, property);
                    return value.Tag != JsValueTag.Undefined;
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
                case "nodeType":
                    value = JsValue.FromInt32((int)characterData.NodeType);
                    return true;
                case "id":
                case "localName":
                    value = JsValue.Undefined;
                    return true;
                case "ownerDocument":
                    value = _owner.ToHostOrNull(characterData.OwnerDocument, HostObjectKind.DomDocument);
                    return true;
                case "parentNode":
                    value = _owner.ToHostNodeOrNull(characterData.ParentNode);
                    return true;
                case "firstChild":
                case "lastChild":
                    value = JsValue.Null;
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
                case "nodeType":
                    value = JsValue.FromInt32((int)fragment.NodeType);
                    return true;
                case "nodeValue":
                    value = JsValue.Null;
                    return true;
                case "textContent":
                    value = JsValue.FromString(fragment.TextContent ?? string.Empty);
                    return true;
                case "isConnected":
                    value = JsValue.FromBoolean(fragment.IsConnected);
                    return true;
                case "host" when fragment is ShadowRoot shadowRoot:
                    value = _owner.ToHostOrNull(shadowRoot.Host, HostObjectKind.DomElement);
                    return true;
                case "mode" when fragment is ShadowRoot shadowRoot:
                    value = JsValue.FromString(shadowRoot.Mode == ShadowRootMode.Closed ? "closed" : "open");
                    return true;
                case "delegatesFocus" when fragment is ShadowRoot shadowRoot:
                    value = JsValue.FromBoolean(shadowRoot.DelegatesFocus);
                    return true;
                case "slotAssignment" when fragment is ShadowRoot shadowRoot:
                    value = JsValue.FromString(shadowRoot.SlotAssignment == SlotAssignmentMode.Manual ? "manual" : "named");
                    return true;
                case "ownerDocument":
                    value = _owner.ToHostOrNull(fragment.OwnerDocument, HostObjectKind.DomDocument);
                    return true;
                case "parentNode":
                    value = _owner.ToHostNodeOrNull(fragment.ParentNode);
                    return true;
                case "parentElement":
                    value = _owner.ToHostNodeOrNull(fragment.ParentNode as Element);
                    return true;
                case "firstChild":
                    value = _owner.ToHostNodeOrNull(fragment.FirstChild);
                    return true;
                case "lastChild":
                    value = _owner.ToHostNodeOrNull(fragment.LastChild);
                    return true;
                case "previousSibling":
                    value = _owner.ToHostNodeOrNull(fragment.PreviousSibling);
                    return true;
                case "nextSibling":
                    value = _owner.ToHostNodeOrNull(fragment.NextSibling);
                    return true;
                case "childNodes":
                    value = _owner.CreateNodeArrayLike(fragment.ChildNodes.ToArray());
                    return true;
                case "firstElementChild":
                    value = _owner.ToHostNodeOrNull(fragment.FirstElementChild);
                    return true;
                case "lastElementChild":
                    value = _owner.ToHostNodeOrNull(fragment.LastElementChild);
                    return true;
                case "childElementCount":
                    value = JsValue.FromInt32(fragment.ChildElementCount);
                    return true;
                case "children":
                    {
                        var children = new List<Node>();
                        for (var i = 0; i < fragment.ChildNodes.Length; i++)
                        {
                            if (fragment.ChildNodes[i] is Element)
                            {
                                children.Add(fragment.ChildNodes[i]);
                            }
                        }

                        value = _owner.CreateNodeArrayLike(children);
                    }
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
                case "insertBefore":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
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
                            try
                            {
                                return _owner.ToHostNodeOrNull(fragment.InsertBefore(child, referenceNode));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 2);
                    return true;
                case "cloneNode":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "cloneNode",
                        (_, args) =>
                        {
                            var deep = args.Count > 0 && CoerceToHostBoolean(args[0]);
                            return _owner.ToHostNodeOrNull(fragment.CloneNode(deep));
                        },
                        length: 0);
                    return true;
                case "removeChild":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "removeChild",
                        (_, args) =>
                        {
                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Child must be a DOM node.");
                            }

                            try
                            {
                                return _owner.ToHostNodeOrNull(fragment.RemoveChild(child));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 1);
                    return true;
                case "replaceChild":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "replaceChild",
                        (_, args) =>
                        {
                            if (args.Count > 0 && _owner.ResolveHostObjectOrNull<Attr>(args[0]) != null)
                            {
                                _owner.ThrowHierarchyRequestError("Attributes cannot be inserted as child nodes.");
                            }

                            var child = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            if (child == null)
                            {
                                _owner.ThrowDomException("TypeError", "Replacement child must be a DOM node.");
                            }

                            var oldChild = args.Count > 1 ? _owner.ResolveHostObjectOrNull<Node>(args[1]) : null;
                            if (oldChild == null)
                            {
                                _owner.ThrowDomException("TypeError", "Child must be a DOM node.");
                            }

                            try
                            {
                                return _owner.ToHostNodeOrNull(fragment.ReplaceChild(child, oldChild));
                            }
                            catch (DomException ex)
                            {
                                _owner.ThrowDomException(ex.Name, ex.Message);
                                return JsValue.Undefined;
                            }
                        },
                        length: 2);
                    return true;
                case "hasChildNodes":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "hasChildNodes",
                        (_, _) => JsValue.FromBoolean(fragment.HasChildNodes),
                        length: 0);
                    return true;
                case "contains":
                    value = _owner.GetOrCreateHostCallable(
                        fragment,
                        "contains",
                        (_, args) =>
                        {
                            var other = args.Count > 0 ? _owner.ResolveHostObjectOrNull<Node>(args[0]) : null;
                            return JsValue.FromBoolean(other != null && fragment.Contains(other));
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

        private bool TryGetRangeProperty(DomRange range, string property, out JsValue value)
        {
            switch (property)
            {
                case "START_TO_START":
                    value = JsValue.FromInt32(0);
                    return true;
                case "START_TO_END":
                    value = JsValue.FromInt32(1);
                    return true;
                case "END_TO_END":
                    value = JsValue.FromInt32(2);
                    return true;
                case "END_TO_START":
                    value = JsValue.FromInt32(3);
                    return true;
                case "startContainer":
                    value = _owner.ToHostNodeOrNull(range.StartContainer);
                    return true;
                case "startOffset":
                    value = JsValue.FromInt32(range.StartOffset);
                    return true;
                case "endContainer":
                    value = _owner.ToHostNodeOrNull(range.EndContainer);
                    return true;
                case "endOffset":
                    value = JsValue.FromInt32(range.EndOffset);
                    return true;
                case "collapsed":
                    value = JsValue.FromBoolean(range.Collapsed);
                    return true;
                case "commonAncestorContainer":
                    value = _owner.ToHostNodeOrNull(range.CommonAncestorContainer);
                    return true;
                case "setStart":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setStart",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetStart(
                                RequireRangeNodeArgument(args, 0, "setStart"),
                                CoerceRangeOffsetArgument(args, 1));
                            return JsValue.Undefined;
                        }),
                        length: 2);
                    return true;
                case "setEnd":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setEnd",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetEnd(
                                RequireRangeNodeArgument(args, 0, "setEnd"),
                                CoerceRangeOffsetArgument(args, 1));
                            return JsValue.Undefined;
                        }),
                        length: 2);
                    return true;
                case "setStartBefore":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setStartBefore",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetStartBefore(RequireRangeNodeArgument(args, 0, "setStartBefore"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "setStartAfter":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setStartAfter",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetStartAfter(RequireRangeNodeArgument(args, 0, "setStartAfter"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "setEndBefore":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setEndBefore",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetEndBefore(RequireRangeNodeArgument(args, 0, "setEndBefore"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "setEndAfter":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "setEndAfter",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SetEndAfter(RequireRangeNodeArgument(args, 0, "setEndAfter"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "collapse":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "collapse",
                        (_, args) => InvokeRange(() =>
                        {
                            range.Collapse(args.Count > 0 && CoerceToHostBoolean(args[0]));
                            return JsValue.Undefined;
                        }),
                        length: 0);
                    return true;
                case "selectNode":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "selectNode",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SelectNode(RequireRangeNodeArgument(args, 0, "selectNode"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "selectNodeContents":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "selectNodeContents",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SelectNodeContents(RequireRangeNodeArgument(args, 0, "selectNodeContents"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "compareBoundaryPoints":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "compareBoundaryPoints",
                        (_, args) => InvokeRange(() =>
                        {
                            var how = (ushort)CoerceRangeOffsetArgument(args, 0);
                            var otherRange = args.Count > 1 ? _owner.ResolveHostObjectOrNull<DomRange>(args[1]) : null;
                            if (otherRange == null)
                            {
                                _owner.ThrowDomException(
                                    "TypeError",
                                    "Failed to execute 'compareBoundaryPoints' on 'Range': parameter 2 is not of type 'Range'.");
                            }

                            return JsValue.FromInt32(range.CompareBoundaryPoints(how, otherRange));
                        }),
                        length: 2);
                    return true;
                case "deleteContents":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "deleteContents",
                        (_, _) => InvokeRange(() =>
                        {
                            range.DeleteContents();
                            return JsValue.Undefined;
                        }),
                        length: 0);
                    return true;
                case "extractContents":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "extractContents",
                        (_, _) => InvokeRange(() => _owner.ToHostNodeOrNull(range.ExtractContents())),
                        length: 0);
                    return true;
                case "cloneContents":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "cloneContents",
                        (_, _) => InvokeRange(() => _owner.ToHostNodeOrNull(range.CloneContents())),
                        length: 0);
                    return true;
                case "insertNode":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "insertNode",
                        (_, args) => InvokeRange(() =>
                        {
                            range.InsertNode(RequireRangeNodeArgument(args, 0, "insertNode"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "surroundContents":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "surroundContents",
                        (_, args) => InvokeRange(() =>
                        {
                            range.SurroundContents(RequireRangeNodeArgument(args, 0, "surroundContents"));
                            return JsValue.Undefined;
                        }),
                        length: 1);
                    return true;
                case "cloneRange":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "cloneRange",
                        (_, _) => InvokeRange(() => _owner.ToHostOrNull(range.CloneRange(), HostObjectKind.Other)),
                        length: 0);
                    return true;
                case "detach":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "detach",
                        (_, _) => InvokeRange(() =>
                        {
                            range.Detach();
                            return JsValue.Undefined;
                        }),
                        length: 0);
                    return true;
                case "isPointInRange":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "isPointInRange",
                        (_, args) => InvokeRange(() => JsValue.FromBoolean(range.IsPointInRange(
                            RequireRangeNodeArgument(args, 0, "isPointInRange"),
                            CoerceRangeOffsetArgument(args, 1)))),
                        length: 2);
                    return true;
                case "comparePoint":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "comparePoint",
                        (_, args) => InvokeRange(() => JsValue.FromInt32(range.ComparePoint(
                            RequireRangeNodeArgument(args, 0, "comparePoint"),
                            CoerceRangeOffsetArgument(args, 1)))),
                        length: 2);
                    return true;
                case "intersectsNode":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "intersectsNode",
                        (_, args) => InvokeRange(() => JsValue.FromBoolean(range.IntersectsNode(
                            RequireRangeNodeArgument(args, 0, "intersectsNode")))),
                        length: 1);
                    return true;
                case "createContextualFragment":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "createContextualFragment",
                        (_, args) => InvokeRange(() =>
                        {
                            var html = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            return _owner.ToHostNodeOrNull(_owner.CreateContextualFragment(range, html));
                        }),
                        length: 1);
                    return true;
                case "getBoundingClientRect":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "getBoundingClientRect",
                        (_, _) => _owner.CreateDomRect(0, 0, 0, 0),
                        length: 0);
                    return true;
                case "getClientRects":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "getClientRects",
                        (_, _) => _owner.CreateEmptyDomRectList(),
                        length: 0);
                    return true;
                case "toString":
                    value = _owner.GetOrCreateHostCallable(
                        range,
                        "toString",
                        (_, _) => InvokeRange(() => JsValue.FromString(range.ToString())),
                        length: 0);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private JsValue InvokeRange(Func<JsValue> action)
        {
            try
            {
                return action();
            }
            catch (DomException ex)
            {
                _owner.ThrowDomException(ex.Name, ex.Message);
                return JsValue.Undefined;
            }
            catch (ArgumentException ex)
            {
                _owner.ThrowDomException("TypeError", ex.Message);
                return JsValue.Undefined;
            }
            catch (InvalidOperationException ex)
            {
                _owner.ThrowDomException("InvalidStateError", ex.Message);
                return JsValue.Undefined;
            }
        }

        private Node RequireRangeNodeArgument(IReadOnlyList<JsValue> args, int index, string methodName)
        {
            var node = args.Count > index ? _owner.ResolveHostObjectOrNull<Node>(args[index]) : null;
            if (node != null)
            {
                return node;
            }

            _owner.ThrowDomException(
                "TypeError",
                $"Failed to execute '{methodName}' on 'Range': parameter {index + 1} is not of type 'Node'.");
            return null;
        }

        private static int CoerceRangeOffsetArgument(IReadOnlyList<JsValue> args, int index)
        {
            return args.Count > index && TryCoerceIndex(args[index], out var offset)
                ? offset
                : 0;
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

        private bool TryGetTreeWalkerProperty(FenJsTreeWalkerHost treeWalkerHost, string property, out JsValue value)
        {
            var treeWalker = treeWalkerHost.TreeWalker;
            switch (property)
            {
                case "root":
                    value = _owner.ToHostNodeOrNull(treeWalker.Root);
                    return true;
                case "whatToShow":
                    value = JsValue.FromNumber(treeWalker.WhatToShow);
                    return true;
                case "filter":
                    value = JsValue.Null;
                    return true;
                case "currentNode":
                    value = _owner.ToHostNodeOrNull(treeWalker.CurrentNode);
                    return true;
                case "parentNode":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "parentNode",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.ParentNode()),
                        length: 0);
                    return true;
                case "firstChild":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "firstChild",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.FirstChild()),
                        length: 0);
                    return true;
                case "lastChild":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "lastChild",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.LastChild()),
                        length: 0);
                    return true;
                case "previousSibling":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "previousSibling",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.PreviousSibling()),
                        length: 0);
                    return true;
                case "nextSibling":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "nextSibling",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.NextSibling()),
                        length: 0);
                    return true;
                case "previousNode":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "previousNode",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.PreviousNode()),
                        length: 0);
                    return true;
                case "nextNode":
                    value = _owner.GetOrCreateHostCallable(
                        treeWalkerHost,
                        "nextNode",
                        (_, _) => _owner.ToHostNodeOrNull(treeWalker.NextNode()),
                        length: 0);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetAnimationProperty(FenJsAnimationHost animation, string property, out JsValue value)
        {
            switch (property)
            {
                case "id":
                    value = JsValue.FromString(animation.Id);
                    return true;
                case "effect":
                    value = animation.Keyframes.Tag == JsValueTag.Undefined ? JsValue.Null : animation.Keyframes;
                    return true;
                case "timeline":
                    value = JsValue.Null;
                    return true;
                case "startTime":
                    value = animation.StartTime.HasValue ? JsValue.FromNumber(animation.StartTime.Value) : JsValue.Null;
                    return true;
                case "currentTime":
                    value = animation.CurrentTime.HasValue ? JsValue.FromNumber(animation.CurrentTime.Value) : JsValue.Null;
                    return true;
                case "playbackRate":
                    value = JsValue.FromNumber(animation.PlaybackRate);
                    return true;
                case "playState":
                    value = JsValue.FromString(animation.PlayState);
                    return true;
                case "pending":
                    value = JsValue.FromBoolean(animation.Pending);
                    return true;
                case "replaceState":
                    value = JsValue.FromString("active");
                    return true;
                case "ready":
                case "finished":
                    value = _owner.CreateResolvedPromise(_owner.ToHostOrNull(animation, HostObjectKind.Other));
                    return true;
                case "cancel":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "cancel",
                        (_, _) =>
                        {
                            animation.PlayState = "idle";
                            animation.Pending = false;
                            animation.CurrentTime = 0;
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "finish":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "finish",
                        (_, _) =>
                        {
                            animation.PlayState = "finished";
                            animation.Pending = false;
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "play":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "play",
                        (_, _) =>
                        {
                            animation.PlayState = "running";
                            animation.Pending = false;
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "pause":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "pause",
                        (_, _) =>
                        {
                            animation.PlayState = "paused";
                            animation.Pending = false;
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "reverse":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "reverse",
                        (_, _) =>
                        {
                            animation.PlaybackRate = animation.PlaybackRate == 0 ? -1 : -animation.PlaybackRate;
                            animation.PlayState = "running";
                            animation.Pending = false;
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "updatePlaybackRate":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "updatePlaybackRate",
                        (_, args) =>
                        {
                            animation.PlaybackRate = args.Count > 0
                                ? CoerceToFiniteNumber(args[0], animation.PlaybackRate)
                                : animation.PlaybackRate;
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "persist":
                case "commitStyles":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        property,
                        (_, _) => JsValue.Undefined,
                        length: 0);
                    return true;
                case "addEventListener":
                case "removeEventListener":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        property,
                        (_, _) => JsValue.Undefined,
                        length: 2);
                    return true;
                case "dispatchEvent":
                    value = _owner.GetOrCreateHostCallable(
                        animation,
                        "dispatchEvent",
                        (_, _) => JsValue.FromBoolean(true),
                        length: 1);
                    return true;
                default:
                    value = _owner.GetStoredHostPropertyOrUndefined(animation, property);
                    return value.Tag != JsValueTag.Undefined;
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

        private bool TryGetNavigatorProperty(BrowserSurfaceProfile navigator, string property, out JsValue value)
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
                    // Must return a frozen array per spec. Bot detection checks Array.isArray(navigator.languages).
                    {
                        var lang = navigator.Language ?? "en-US";
                        var shortLang = lang.Contains("-") ? lang.Substring(0, lang.IndexOf('-')) : lang;
                        value = _owner._interpreter.AllocateArray(new[]
                        {
                            JsValue.FromString(lang),
                            JsValue.FromString(shortLang)
                        });
                    }
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
                case "serviceWorker":
                    value = _owner.GetStoredHostPropertyOrUndefined(navigator, "serviceWorker");
                    if (value.Tag == JsValueTag.Undefined)
                    {
                        value = _owner.CreateDefaultServiceWorkerStub();
                        _owner.SetStoredHostProperty(navigator, "serviceWorker", value);
                    }
                    return true;
                case "storage":
                    value = _owner.GetStoredHostPropertyOrUndefined(navigator, "storage");
                    if (value.Tag == JsValueTag.Undefined)
                    {
                        value = _owner.CreateDefaultStorageStub();
                        _owner.SetStoredHostProperty(navigator, "storage", value);
                    }
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetStorageProperty(FenStorageAreaHost storageArea, string property, out JsValue value)
        {
            switch (property)
            {
                case "length":
                    value = JsValue.FromInt32(storageArea.Length);
                    return true;
                case "key":
                    value = _owner.GetOrCreateHostCallable(
                        storageArea, "key",
                        (_, args) =>
                        {
                            var index = args.Count > 0 ? (int)CoerceToFiniteNumber(args[0], 0) : 0;
                            var result = storageArea.Key(index);
                            return result != null ? JsValue.FromString(result) : JsValue.Null;
                        },
                        length: 1);
                    return true;
                case "getItem":
                    value = _owner.GetOrCreateHostCallable(
                        storageArea, "getItem",
                        (_, args) =>
                        {
                            var key = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var result = storageArea.GetItem(key);
                            return result != null ? JsValue.FromString(result) : JsValue.Null;
                        },
                        length: 1);
                    return true;
                case "setItem":
                    value = _owner.GetOrCreateHostCallable(
                        storageArea, "setItem",
                        (_, args) =>
                        {
                            var key = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            var val = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            storageArea.SetItem(key, val);
                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "removeItem":
                    value = _owner.GetOrCreateHostCallable(
                        storageArea, "removeItem",
                        (_, args) =>
                        {
                            var key = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
                            storageArea.RemoveItem(key);
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "clear":
                    value = _owner.GetOrCreateHostCallable(
                        storageArea, "clear",
                        (_, _) =>
                        {
                            storageArea.Clear();
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                default:
                    // Per spec, storage[key] is equivalent to storage.getItem(key).
                    var item = storageArea.GetItem(property);
                    if (item != null)
                    {
                        value = JsValue.FromString(item);
                        return true;
                    }
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private bool TryGetHistoryProperty(FenJsHistoryHost history, string property, out JsValue value)
        {
            if (string.Equals(property, "pushState", StringComparison.Ordinal) ||
                string.Equals(property, "replaceState", StringComparison.Ordinal))
            {
                value = _owner.GetStoredHostPropertyOrUndefined(history, property);
                if (value.Tag != JsValueTag.Undefined)
                {
                    return true;
                }
            }

            switch (property)
            {
                case "length":
                    value = JsValue.FromInt32(_owner._historyBridge?.Length ?? history.Length);
                    return true;
                case "state":
                    value = _owner._historyBridge != null
                        ? CoerceBridgeHistoryState(_owner._historyBridge.State)
                        : history.State;
                    return true;
                case "pushState":
                    value = _owner.GetOrCreateHostCallable(
                        history,
                        "pushState",
                        (_, args) =>
                        {
                            var state = args.Count > 0 ? args[0] : JsValue.Null;
                            var title = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            var url = args.Count > 2 && args[2].Tag != JsValueTag.Undefined
                                ? CoerceToHostString(args[2])
                                : string.Empty;

                            if (_owner._historyBridge != null)
                            {
                                _owner._historyBridge.PushState(state, title, url);
                                history.Length = _owner._historyBridge.Length;
                                history.State = CoerceBridgeHistoryState(_owner._historyBridge.State);
                                _owner.UpdateFenJsLocation(history.Location, _owner._historyBridge.CurrentUrl);
                            }
                            else
                            {
                                history.State = state;
                                history.Length = Math.Max(1, history.Length + 1);
                                _owner.ApplyHistoryUrl(history.Location, url);
                            }

                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "replaceState":
                    value = _owner.GetOrCreateHostCallable(
                        history,
                        "replaceState",
                        (_, args) =>
                        {
                            var state = args.Count > 0 ? args[0] : JsValue.Null;
                            var title = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
                            var url = args.Count > 2 && args[2].Tag != JsValueTag.Undefined
                                ? CoerceToHostString(args[2])
                                : string.Empty;

                            if (_owner._historyBridge != null)
                            {
                                _owner._historyBridge.ReplaceState(state, title, url);
                                history.Length = _owner._historyBridge.Length;
                                history.State = CoerceBridgeHistoryState(_owner._historyBridge.State);
                                _owner.UpdateFenJsLocation(history.Location, _owner._historyBridge.CurrentUrl);
                            }
                            else
                            {
                                history.State = state;
                                _owner.ApplyHistoryUrl(history.Location, url);
                            }

                            return JsValue.Undefined;
                        },
                        length: 2);
                    return true;
                case "go":
                    value = _owner.GetOrCreateHostCallable(
                        history,
                        "go",
                        (_, args) =>
                        {
                            var delta = args.Count > 0 ? (int)CoerceToFiniteNumber(args[0], 0) : 0;
                            _owner._historyBridge?.Go(delta);
                            if (_owner._historyBridge != null)
                            {
                                history.Length = _owner._historyBridge.Length;
                                history.State = CoerceBridgeHistoryState(_owner._historyBridge.State);
                                _owner.UpdateFenJsLocation(history.Location, _owner._historyBridge.CurrentUrl);
                            }
                            return JsValue.Undefined;
                        },
                        length: 1);
                    return true;
                case "back":
                    value = _owner.GetOrCreateHostCallable(
                        history,
                        "back",
                        (_, _) =>
                        {
                            _owner._historyBridge?.Go(-1);
                            if (_owner._historyBridge != null)
                            {
                                history.Length = _owner._historyBridge.Length;
                                history.State = CoerceBridgeHistoryState(_owner._historyBridge.State);
                                _owner.UpdateFenJsLocation(history.Location, _owner._historyBridge.CurrentUrl);
                            }
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                case "forward":
                    value = _owner.GetOrCreateHostCallable(
                        history,
                        "forward",
                        (_, _) =>
                        {
                            _owner._historyBridge?.Go(1);
                            if (_owner._historyBridge != null)
                            {
                                history.Length = _owner._historyBridge.Length;
                                history.State = CoerceBridgeHistoryState(_owner._historyBridge.State);
                                _owner.UpdateFenJsLocation(history.Location, _owner._historyBridge.CurrentUrl);
                            }
                            return JsValue.Undefined;
                        },
                        length: 0);
                    return true;
                default:
                    value = JsValue.Undefined;
                    return false;
            }
        }

        private static JsValue CoerceBridgeHistoryState(object state)
        {
            return state switch
            {
                null => JsValue.Null,
                JsValue jsValue => jsValue,
                string text => JsValue.FromString(text),
                bool boolean => JsValue.FromBoolean(boolean),
                int int32 => JsValue.FromInt32(int32),
                double number => JsValue.FromNumber(number),
                float number => JsValue.FromNumber(number),
                long integer => JsValue.FromNumber(integer),
                _ => JsValue.Null
            };
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

        private static bool IsImageElement(Element element)
        {
            return string.Equals(element?.TagName, "img", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(element?.TagName, "image", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDialogElement(Element element)
        {
            return string.Equals(element?.TagName, "dialog", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTemplateElement(Element element)
        {
            return string.Equals(element?.TagName, "template", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIFrameElement(Element element)
        {
            return string.Equals(element?.TagName, "iframe", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadElementValue(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var attrValue = element.GetAttribute("value");
            if (attrValue != null)
            {
                return attrValue;
            }

            if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                return element.TextContent ?? string.Empty;
            }

            return string.Empty;
        }

        private static void SetElementValue(Element element, string value)
        {
            if (element == null)
            {
                return;
            }

            value ??= string.Empty;
            element.SetAttribute("value", value);
            if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            {
                element.TextContent = value;
            }
        }

        private static int ReadElementTabIndex(Element element)
        {
            var raw = element?.GetAttribute("tabindex");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (element == null)
            {
                return -1;
            }

            var tagName = element.TagName ?? string.Empty;
            if (string.Equals(tagName, "a", StringComparison.OrdinalIgnoreCase))
            {
                return element.HasAttribute("href") ? 0 : -1;
            }

            return
                string.Equals(tagName, "button", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "select", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "iframe", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : -1;
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

/// <summary>
/// Diagnostic Promise rejection tracker that logs every unhandled rejection
/// to FenLogger with the WhatsApp category so SPA boot failures are visible
/// in engine logs. Delegates to the inner tracker for normal operation.
/// </summary>
internal sealed class FenJsDiagnosticPromiseRejectionTracker : IHostPromiseRejectionTracker
{
    private readonly IHostPromiseRejectionTracker _inner;
    private int _rejectedCount;
    private int _handledCount;

    public FenJsDiagnosticPromiseRejectionTracker(IHostPromiseRejectionTracker inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Track(JsValue promise, PromiseRejectionOperation operation)
    {
        _inner.Track(promise, operation);
        if (operation == PromiseRejectionOperation.Reject)
        {
            var count = Interlocked.Increment(ref _rejectedCount);
            // Only log first 20 rejections to avoid flooding logs;
            // the inner InMemoryPromiseRejectionTracker retains the full list.
            if (count <= 20)
            {
                try
                {
                    FenLogger.Warn(
                        $"[PromiseRejection] Unhandled rejection #{count} detected (promise: {promise})",
                        LogCategory.WhatsApp | LogCategory.JavaScript);
                }
                catch
                {
                    FenLogger.Warn(
                        $"[PromiseRejection] Unhandled rejection #{count} detected",
                        LogCategory.WhatsApp);
                }
            }
        }
        else if (operation == PromiseRejectionOperation.Handle)
        {
            Interlocked.Increment(ref _handledCount);
        }
    }
}
