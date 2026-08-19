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
using FenBrowser.Js.Heap;
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

public sealed record BrowserFrameExecutionOptions
{
    public Func<Uri, string, bool> SubresourceAllowed { get; init; }
    public Func<string, bool> NonceAllowed { get; init; }
    public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; init; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> FetchHandler { get; init; }
}

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
    public List<BrowserCallbackFailureRecord> CallbackFailureRecords { get; set; } = new();
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
            CallbackFailureRecords = CallbackFailureRecords?.ToList() ?? new List<BrowserCallbackFailureRecord>(),
            Events = Events?.Select(entry => entry.Clone()).ToList() ?? new List<BrowserEventLoopRecord>()
        };
    }
}

public sealed record BrowserCallbackFailureRecord
{
    public int SchemaVersion { get; init; } = 1;
    public long Sequence { get; init; }
    public string TimestampUtc { get; init; } = string.Empty;
    public string NavigationId { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public string RealmId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string CallbackId { get; init; } = string.Empty;
    public string CallbackCategory { get; init; } = string.Empty;
    public long? TimerId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string ScriptId { get; init; } = string.Empty;
    public string ScriptUrl { get; init; } = string.Empty;
    public string ScriptSourceLabel { get; init; } = string.Empty;
    public int SourceLine { get; init; }
    public int SourceColumn { get; init; }
    public string CallbackFunctionName { get; init; } = string.Empty;
    public string ReceiverRepresentation { get; init; } = string.Empty;
    public string ReceiverHostType { get; init; } = string.Empty;
    public string ReceiverJsType { get; init; } = string.Empty;
    public string ArgumentTypeSummary { get; init; } = string.Empty;
    public string ExceptionType { get; init; } = string.Empty;
    public string ExceptionMessage { get; init; } = string.Empty;
    public string JsStack { get; init; } = string.Empty;
    public string HostStack { get; init; } = string.Empty;
    public string DocumentReadyState { get; init; } = string.Empty;
    public string LifecycleMilestone { get; init; } = string.Empty;
    public bool BlockedProgress { get; init; }
    public string RedactionStatus { get; init; } = "metadata-only";
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
    Action FlushPendingLayout { get; set; }
    Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }
    Func<Element, Uri, Task> FrameElementLoader { get; set; }
    Func<Element, object> LayoutBoxResolver { get; set; }
    Func<Element, (double X, double Y)> FrameScrollReader { get; set; }
    Action<Element, double, double> FrameScrollWriter { get; set; }
    SandboxPolicy Sandbox { get; set; }
    FenBrowser.Core.Security.DocumentSecurityContext DocumentSecurityContext { get; set; }
    CancellationToken ExecutionCancellation { get; set; }
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
    void NotifyFrameScrollChanged(Element frameElement);
    bool DispatchEventForElement(Element element, string eventName, BrowserDomEventInit eventInit = null);
    /// <summary>
    /// Phase 12: non-blocking variant. Dispatches a JS event asynchronously so
    /// the engine thread is not blocked while the JS worker executes handlers.
    /// </summary>
    Task<bool> DispatchEventForElementAsync(Element element, string eventName, BrowserDomEventInit eventInit = null);
    object Evaluate(string script);
    bool TryResolveHostObject(FenBrowser.Js.Runtime.JsValue value, out object hostObject);
    object ConvertJsValueToObject(FenBrowser.Js.Runtime.JsValue value);
    void SyncDomContext(Node domRoot, Uri baseUri = null);
    Task SetDomAsync(Node domRoot, Uri baseUri = null, CancellationToken cancellationToken = default);
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
    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public int PointerId { get; init; } = 1;
    public string PointerType { get; init; } = "mouse";
    public double Pressure { get; init; }
    public bool IsPrimary { get; init; } = true;
    public Element RelatedTarget { get; init; }
    public bool Bubbles { get; init; } = true;
    public bool Cancelable { get; init; } = true;
    public bool Composed { get; init; } = true;
    public bool IsTrusted { get; init; } = true;
    public string Key { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public int KeyCode { get; init; }
    public string Data { get; init; }
    public string InputType { get; init; } = string.Empty;
    public bool IsComposing { get; init; }
}

internal sealed record BrowserHostLifetimeSnapshot(
    int RuntimeSessionGeneration,
    long DocumentEpoch,
    long NavigationEpoch,
    int HostTableLiveCount,
    int HostTableSlotCount,
    int HostHandleIdentityCacheCount,
    int HostPrototypeNameCount,
    int DocumentEventListenerCount,
    int WindowEventListenerCount,
    int PendingPromiseRejectionCount,
    int ActiveWebSocketCount);

/// <summary>
/// FenJS browser script engine â€” the sole JS runtime for the browser pipeline.
/// All page scripts execute through FenJS; there is no legacy fallback.
/// </summary>
public sealed class FenJsBrowserScriptEngine : IBrowserScriptEngine, IHeapRootSource
{
    private sealed class MessagePortEndpoint
    {
        public MessagePortEndpoint Peer { get; set; }
        public FenJsBrowserScriptEngine Owner { get; set; }
        public JsValue Port { get; set; } = JsValue.Undefined;
        public bool Closed { get; set; }
    }

    private sealed class FenJsTimerRegistration : IDisposable
    {
        public Timer Timer { get; set; }
        public JsValue Callback { get; init; }
        public IReadOnlyList<JsValue> Arguments { get; init; } = Array.Empty<JsValue>();
        public FenJsWindowCallbackContext WindowContext { get; init; }
        public int CallbackPending;

        public void Dispose() => Timer?.Dispose();
    }

    private readonly object _fenJsLock = new();
    private readonly object _windowMessageQueueLock = new();
    private Task _windowMessageDeliveryTail = Task.CompletedTask;
    private readonly ConcurrentDictionary<long, FenJsTimerRegistration> _fenJsTimers = new();
    private long _fenJsTimerIdCounter;
    private long _diagnosticCookieCaptureCounter;
    private JsValue _fenJsGlobalThis = JsValue.Undefined;
    private JsValue _visualViewport = JsValue.Undefined;
    private JsValue _fenJsTopWindowFacade = JsValue.Undefined;
    private JsValue _fenJsSameOriginTopWindowFacade = JsValue.Undefined;
    private readonly System.Diagnostics.Stopwatch _fenJsClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly BrowserFenJsHostHooks _hostHooks = new();
    private readonly NavigationEpoch _navigationEpoch = NavigationEpoch.Initial;
    private string _currentDocumentId = string.Empty;
    private long _callbackFailureSequence;
    private long _diagnosticCallbackIdSequence;
    private FenJsDiagnosticPromiseRejectionTracker _diagnosticPromiseRejectionTracker;
    private readonly Dictionary<BytecodeFunction, CallbackSourceProvenance> _callbackFunctionProvenance = new();
    private readonly Dictionary<JsValue, PendingPromiseRejectionDiagnostic> _pendingPromiseRejectionDiagnostics = new();
    private readonly Dictionary<object, HostObjectHandle> _hostHandleCache =
        new(ReferenceEqualityComparer.Instance);
    private Node _selectionAnchorNode;
    private Node _selectionFocusNode;
    private int _selectionAnchorOffset;
    private int _selectionFocusOffset;
    private readonly List<BrowserEventListener> _documentEventListeners = new();
    private readonly List<BrowserEventListener> _windowEventListeners = new();
    private readonly List<BrowserEventListener> _visualViewportEventListeners = new();
    private readonly List<BrowserEventListener> _embeddedParentWindowListeners = new();
    private ConditionalWeakTable<object, Dictionary<string, JsValue>> _hostCallableCache = new();
    private ConditionalWeakTable<object, Dictionary<string, JsValue>> _hostPropertyStore = new();
    private ConditionalWeakTable<object, HashSet<string>> _missingHostPropertyReads = new();
    private ConditionalWeakTable<object, List<BrowserEventListener>> _elementEventListeners = new();
    private int _suppressMissingHostAssignmentTracking;
    private StorageService _storageService = new();
    private readonly Dictionary<int, FenWebSocketHost> _webSocketHosts = new();
    private int _webSocketIdCounter;
    private ConditionalWeakTable<Element, List<BrowserEventListener>> _iframeWindowEventListeners = new();
    private ConditionalWeakTable<Element, FenJsBrowserScriptEngine> _iframeRealms = new();
    private readonly Dictionary<long, MessagePortEndpoint> _messagePortEndpoints = new();
    private FenJsBrowserScriptEngine _parentRealmOwner;
    private Element _embeddingFrameElement;
    private JsValue _embeddedParentWindowProxy = JsValue.Undefined;
    private readonly Dictionary<object, string> _hostPrototypeNames =
        new(ReferenceEqualityComparer.Instance);
    private List<BrowserEventListener> _activeWindowEventListeners;
    private JsValue _activeWindowEventTarget = JsValue.Undefined;
    private JsValue _fenJsFileConstructor = JsValue.Undefined;
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
    private long _fenJsAllocationCountAtLastBoundaryGc;
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

    void IHeapRootSource.TraceRoots(IHeapTracer tracer)
    {
        TraceJsRoot(tracer, _fenJsGlobalThis);
        TraceJsRoot(tracer, _visualViewport);
        TraceJsRoot(tracer, _fenJsTopWindowFacade);
        TraceJsRoot(tracer, _fenJsSameOriginTopWindowFacade);
        TraceJsRoot(tracer, _embeddedParentWindowProxy);
        TraceJsRoot(tracer, _activeWindowEventTarget);
        TraceJsRoot(tracer, _fenJsFileConstructor);

        TraceBrowserEventListeners(tracer, _documentEventListeners);
        TraceBrowserEventListeners(tracer, _windowEventListeners);
        TraceBrowserEventListeners(tracer, _visualViewportEventListeners);
        TraceBrowserEventListeners(tracer, _embeddedParentWindowListeners);
        if (_activeWindowEventListeners != null)
        {
            TraceBrowserEventListeners(tracer, _activeWindowEventListeners);
        }

        foreach (var entry in _elementEventListeners)
        {
            TraceBrowserEventListeners(tracer, entry.Value);
        }
        foreach (var entry in _iframeWindowEventListeners)
        {
            TraceBrowserEventListeners(tracer, entry.Value);
        }
        foreach (var entry in _hostCallableCache)
        {
            TraceJsRoots(tracer, entry.Value.Values);
        }
        foreach (var entry in _hostPropertyStore)
        {
            TraceJsRoots(tracer, entry.Value.Values);
        }

        foreach (var (promise, diagnostic) in _pendingPromiseRejectionDiagnostics)
        {
            TraceJsRoot(tracer, promise);
            TraceJsRoot(tracer, diagnostic.Reason);
        }

        foreach (var endpoint in _messagePortEndpoints.Values)
        {
            if (ReferenceEquals(endpoint.Owner, this))
            {
                TraceJsRoot(tracer, endpoint.Port);
            }
        }

        foreach (var registration in _fenJsTimers.Values)
        {
            TraceJsRoot(tracer, registration.Callback);
            TraceJsRoots(tracer, registration.Arguments);
            if (registration.WindowContext != null)
            {
                TraceJsRoot(tracer, registration.WindowContext.WindowTarget);
                TraceBrowserEventListeners(tracer, registration.WindowContext.WindowListeners);
            }
        }

        foreach (var hostObject in _hostHandleCache.Keys)
        {
            if (hostObject is FenJsMutationObserverHost observer)
            {
                TraceJsRoot(tracer, observer.Callback);
            }
        }
    }

    private static void TraceBrowserEventListeners(
        IHeapTracer tracer,
        IEnumerable<BrowserEventListener> listeners)
    {
        foreach (var listener in listeners)
        {
            TraceJsRoot(tracer, listener.Callback);
        }
    }

    private static void TraceJsRoots(IHeapTracer tracer, IEnumerable<JsValue> values)
    {
        foreach (var value in values)
        {
            TraceJsRoot(tracer, value);
        }
    }

    private static void TraceJsRoot(IHeapTracer tracer, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            tracer.Trace(value.AsObjectHandle());
        }
    }
    private IHistoryBridge _historyBridge;
    private readonly IJsHost _host;
    private double _windowWidth;
    private double _windowHeight;
    private CancellationToken _executionCancellation;

    public FenJsBrowserScriptEngine(IJsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _runtimeProfile = JavaScriptRuntimeProfile.Balanced;
        ResetFenJsSession();
    }

    internal int FenJsEvaluationCount => _fenJsEvaluationCount;

    internal BrowserHostLifetimeSnapshot GetHostLifetimeSnapshotForTest()
    {
        lock (_fenJsLock)
        {
            var table = _interpreter?.HostObjectTable;
            lock (_webSocketHosts)
            {
                return new BrowserHostLifetimeSnapshot(
                    RuntimeSessionGeneration: _fenJsSessionGeneration,
                    DocumentEpoch: _documentEpoch.Value,
                    NavigationEpoch: _navigationEpoch.Value,
                    HostTableLiveCount: table?.LiveCount ?? 0,
                    HostTableSlotCount: table?.SlotCountForTest ?? 0,
                    HostHandleIdentityCacheCount: _hostHandleCache.Count,
                    HostPrototypeNameCount: _hostPrototypeNames.Count,
                    DocumentEventListenerCount: _documentEventListeners.Count,
                    WindowEventListenerCount: _windowEventListeners.Count,
                    PendingPromiseRejectionCount: _pendingPromiseRejectionDiagnostics.Count,
                    ActiveWebSocketCount: _webSocketHosts.Count);
            }
        }
    }

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
    public Action FlushPendingLayout { get; set; }
    public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }
    public Func<Element, Uri, Task> FrameElementLoader { get; set; }
    public Func<Element, object> LayoutBoxResolver { get; set; }
    public Func<Element, (double X, double Y)> FrameScrollReader { get; set; }
    public Action<Element, double, double> FrameScrollWriter { get; set; }
    public SandboxPolicy Sandbox { get; set; }
    public FenBrowser.Core.Security.DocumentSecurityContext DocumentSecurityContext { get; set; }
    public CancellationToken ExecutionCancellation
    {
        get => _executionCancellation;
        set => _executionCancellation = value;
    }
    public bool AllowExternalScripts { get; set; }
    public bool ExecuteInlineScriptsOnInnerHTML { get; set; }
    public double WindowWidth
    {
        get => _windowWidth;
        set => UpdateViewportDimensions(value, _windowHeight);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set => UpdateViewportDimensions(_windowWidth, value);
    }
    public int PageScriptByteBudget { get; set; }

    /// <summary>
    /// Storage backend for IndexedDB persistence. When set, data persists across
    /// page loads. Defaults to an in-memory store if not configured.
    /// </summary>
    public IStorageBackend IndexedDbBackend { get; set; }

#pragma warning disable CS0067 // Compatibility event is forwarded through BrowserApi; this runtime does not raise it directly.
    public event Func<string, JsPermissions, Task<bool>> PermissionRequested;
#pragma warning restore CS0067

    /// <summary>
    /// Supplies the Permissions-Policy of the current document. Set by the host
    /// (BrowserApi) after each navigation; null means no policy is in effect
    /// (features are allowed, matching an absent header).
    /// </summary>
    public Func<FenBrowser.Core.Security.PermissionsPolicy> PermissionsPolicyProvider { get; set; }

    /// <summary>
    /// Evaluates the current document's Permissions-Policy for a feature.
    /// A missing provider or absent header grants the feature (the policy
    /// default), matching the spec's default allowlist behavior.
    /// </summary>
    public bool IsFeatureAllowedByPolicy(FenBrowser.Core.Security.PolicyControlledFeature feature, string origin)
    {
        var policy = DocumentSecurityContext?.PermissionsPolicy ?? PermissionsPolicyProvider?.Invoke();
        if (policy == null || policy == FenBrowser.Core.Security.PermissionsPolicy.None)
        {
            return true;
        }

        return policy.IsFeatureAllowed(feature, origin, origin);
    }

    /// <summary>
    /// Resolves the current document origin for Permissions-Policy checks.
    /// Falls back to the document's URL when no explicit origin is available.
    /// </summary>
    private string TryResolveCurrentOrigin(Element element)
    {
        try
        {
            var document = element?.OwnerDocument ?? _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
            if (document != null &&
                !string.IsNullOrEmpty(document.DocumentURI) &&
                Uri.TryCreate(document.DocumentURI, UriKind.Absolute, out var docUri))
            {
                return docUri.GetLeftPart(UriPartial.Authority);
            }
        }
        catch
        {
            // Fall through to empty origin.
        }

        return string.Empty;
    }

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
                .Select(RedactSensitiveCookieName)
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

    // Cookie *names* can themselves reveal identity-bearing identifiers
    // (e.g. "session-token", "csrf", "auth-user"). Hash names that match a
    // sensitive pattern so the diagnostic never records a usable identifier.
    private static string RedactSensitiveCookieName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name ?? string.Empty;
        }

        var lower = name.ToLowerInvariant();
        bool sensitive =
            lower.Contains("token", StringComparison.Ordinal) ||
            lower.Contains("session", StringComparison.Ordinal) ||
            lower.Contains("auth", StringComparison.Ordinal) ||
            lower.Contains("csrf", StringComparison.Ordinal) ||
            lower.Contains("secret", StringComparison.Ordinal) ||
            lower.Contains("key", StringComparison.Ordinal) ||
            lower.Contains("sid", StringComparison.Ordinal) ||
            lower.Contains("ssid", StringComparison.Ordinal) ||
            lower.Contains("credential", StringComparison.Ordinal) ||
            lower.Contains("password", StringComparison.Ordinal) ||
            lower.Contains("passwd", StringComparison.Ordinal);

        if (!sensitive)
        {
            return name;
        }

        // Stable, short FNV-1a hash so the same cookie is identifiable across
        // captures without revealing its name.
        return "h:" + StableHash(name);
    }

    private static string StableHash(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
        unchecked
        {
            ulong hash = 1469598103934665603UL; // FNV-1a 64 offset basis
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return hash.ToString("x12");
        }
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

        if (TryGetFrameRealm(element.OwnerDocument, out var frameRealm))
        {
            LogInputPipelineDispatch(element, eventName, "iframe");
            var defaultAllowed = frameRealm.DispatchEventForElement(element, eventName, eventInit);
            SyncFrameRealmObservables(TryGetFrameElementForDocument(element.OwnerDocument), frameRealm);
            LogInputPipelineCompletion(element, eventName, defaultAllowed);
            return defaultAllowed;
        }

        try
        {
            var inputTimeoutMs = ResolveFenJsInputEventTimeoutMs();
            return RunFenJsWithLargeStack(() =>
            {
                lock (_fenJsLock)
                {
                    var currentDocument = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
                    var windowContext = _parentRealmOwner != null &&
                        ReferenceEquals(element.OwnerDocument, currentDocument)
                            ? null
                            : ActivateSubdocumentWindowContext(element.OwnerDocument, null);
                    using (windowContext)
                    {
                        return _interpreter.RunWithExecutionBudget(
                            inputTimeoutMs,
                            FenJsBrowserTaskInstructionBudget,
                            () =>
                            {
                                var eventValue = CreateBrowserDomEventValue(element, eventName, eventInit, out var dispatchState);
                                var defaultAllowed = DispatchElementEventWithActivation(
                                    element,
                                    eventName,
                                    eventValue,
                                    dispatchState);
                                _interpreter.PumpMicrotasks();
                                RecordMicrotaskCheckpoint("event:" + eventName);
                                return defaultAllowed;
                            });
                    }
                }
            }, inputTimeoutMs, FenJsBrowserTaskInstructionBudget, prioritize: true);
        }
        catch (JsThrownException ex) when (IsFenJsInputEventTimeout(ex))
        {
            throw new FenBrowser.FenEngine.Errors.FenTimeoutError(
                $"Timed out dispatching '{eventName}' event.");
        }
    }

    /// <summary>
    /// Phase 12: non-blocking variant of <see cref="DispatchEventForElement"/>.
    /// Dispatches a JS event to the given element and returns a Task that
    /// completes when JS execution finishes (or times out). The calling thread
    /// is never blocked â€” the JS worker signals completion via TCS.
    /// </summary>
    public async Task<bool> DispatchEventForElementAsync(Element element, string eventName, BrowserDomEventInit eventInit = null)
    {
        if (element == null || string.IsNullOrWhiteSpace(eventName))
            return true;

        if (TryGetFrameRealm(element.OwnerDocument, out var frameRealm))
        {
            LogInputPipelineDispatch(element, eventName, "iframe");
            var defaultAllowed = frameRealm.DispatchEventForElement(element, eventName, eventInit);
            SyncFrameRealmObservables(TryGetFrameElementForDocument(element.OwnerDocument), frameRealm);
            LogInputPipelineCompletion(element, eventName, defaultAllowed);
            return defaultAllowed;
        }

        try
        {
            var inputTimeoutMs = ResolveFenJsInputEventTimeoutMs();
            return await RunFenJsWithLargeStackAsync(() =>
            {
                lock (_fenJsLock)
                {
                    var currentDocument = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
                    var windowContext = _parentRealmOwner != null &&
                        ReferenceEquals(element.OwnerDocument, currentDocument)
                            ? null
                            : ActivateSubdocumentWindowContext(element.OwnerDocument, null);
                    using (windowContext)
                    {
                        return _interpreter.RunWithExecutionBudget(
                            inputTimeoutMs,
                            FenJsBrowserTaskInstructionBudget,
                            () =>
                            {
                                var eventValue = CreateBrowserDomEventValue(element, eventName, eventInit, out var dispatchState);
                                var defaultAllowed = DispatchElementEventWithActivation(
                                    element,
                                    eventName,
                                    eventValue,
                                    dispatchState);
                                _interpreter.PumpMicrotasks();
                                RecordMicrotaskCheckpoint("event:" + eventName);
                                return defaultAllowed;
                            });
                    }
                }
            }, inputTimeoutMs, FenJsBrowserTaskInstructionBudget, prioritize: true).ConfigureAwait(false);
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

    private static bool IsInputPipelineEvent(string eventName) =>
        string.Equals(eventName, "pointerdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventName, "mousedown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventName, "pointerup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventName, "mouseup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventName, "click", StringComparison.OrdinalIgnoreCase);

    private static void LogInputPipelineDispatch(Element element, string eventName, string realm)
    {
        if (!IsInputPipelineEvent(eventName)) return;
        var id = element?.GetAttribute("id");
        var target = element == null
            ? "<none>"
            : string.IsNullOrEmpty(id) ? $"<{element.TagName}>" : $"<{element.TagName}#{id}>";
        FenBrowser.Core.EngineLogCompat.Info(
            $"[InputPipeline] JS dispatch type='{eventName}' target='{target}' realm='{realm}'",
            FenBrowser.Core.Logging.LogCategory.Events);
    }

    private static void LogInputPipelineCompletion(Element element, string eventName, bool defaultAllowed)
    {
        if (!IsInputPipelineEvent(eventName)) return;
        var id = element?.GetAttribute("id");
        var target = element == null
            ? "<none>"
            : string.IsNullOrEmpty(id) ? $"<{element.TagName}>" : $"<{element.TagName}#{id}>";
        FenBrowser.Core.EngineLogCompat.Info(
            $"[InputPipeline] JS complete type='{eventName}' target='{target}' defaultAllowed={defaultAllowed}",
            FenBrowser.Core.Logging.LogCategory.Events);
    }

    public object Evaluate(string script)
    {
        if (CanEvaluateWithFenJs(script))
        {
            lock (_fenJsLock)
            {
                CollectFenJsHeapAtSafeBoundary();
            }
            return EvaluateWithFenJs(script);
        }
        return null;
    }

    public void SyncDomContext(Node domRoot, Uri baseUri = null)
    {
        // Lightweight DOM sync for recascades/re-renders â€” update the cached
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

    public Task SetDomAsync(Node domRoot, Uri baseUri = null, CancellationToken cancellationToken = default)
    {
        ExecutionCancellation = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return SetDomAsyncCore(domRoot, baseUri);
    }

    public Task SetSubdocumentDomAsync(
        Node domRoot,
        Uri baseUri = null,
        BrowserFrameExecutionOptions options = null)
    {
        var document = domRoot as Document ?? domRoot?.OwnerDocument;
        var frameElement = TryGetFrameElementForDocument(document);
        return frameElement == null
            ? SetDomAsyncCore(domRoot, baseUri, resetSession: false, restorePreviousContext: true)
            : SetSubdocumentRealmAsync(frameElement, domRoot, baseUri, options);
    }

    private async Task SetSubdocumentRealmAsync(
        Element frameElement,
        Node domRoot,
        Uri baseUri,
        BrowserFrameExecutionOptions options)
    {
        var frameRealm = _iframeRealms.GetValue(frameElement, CreateFrameRealm);
        frameRealm._parentRealmOwner = this;
        frameRealm._embeddingFrameElement = frameElement;
        CopyFrameRealmConfiguration(frameRealm);
        if (options != null)
        {
            frameRealm.SubresourceAllowed = options.SubresourceAllowed ?? frameRealm.SubresourceAllowed;
            frameRealm.NonceAllowed = options.NonceAllowed ?? frameRealm.NonceAllowed;
            frameRealm.ExternalScriptFetcher = options.ExternalScriptFetcher ?? frameRealm.ExternalScriptFetcher;
            frameRealm.FetchHandler = options.FetchHandler ?? frameRealm.FetchHandler;
        }

        await frameRealm.SetDomAsync(domRoot, baseUri).ConfigureAwait(false);

        lock (_fenJsLock)
        {
            var frameDocument = domRoot as Document ?? domRoot?.OwnerDocument;
            var proxy = GetOrCreateIFrameContentWindow(frameElement, frameDocument, baseUri);
            CopyFrameRealmObservables(frameRealm, proxy);
        }
    }

    private FenJsBrowserScriptEngine CreateFrameRealm(Element frameElement)
    {
        var realm = new FenJsBrowserScriptEngine(_host)
        {
            _parentRealmOwner = this,
            _embeddingFrameElement = frameElement
        };
        CopyFrameRealmConfiguration(realm);
        return realm;
    }

    private void CopyFrameRealmConfiguration(FenJsBrowserScriptEngine realm)
    {
        realm.RuntimeProfile = RuntimeProfile;
        realm.FetchOverride = FetchOverride;
        realm.SubresourceAllowed = SubresourceAllowed;
        realm.NonceAllowed = NonceAllowed;
        realm.FetchHandler = FetchHandler;
        realm.CookieReadBridge = CookieReadBridge;
        realm.CookieWriteBridge = CookieWriteBridge;
        realm.RequestRender = () =>
        {
            SyncFrameRealmObservables(realm._embeddingFrameElement, realm);
            RequestRender?.Invoke();
        };
        realm.FlushPendingLayout = FlushPendingLayout;
        realm.ExternalScriptFetcher = ExternalScriptFetcher;
        realm.FrameElementLoader = FrameElementLoader;
        realm.LayoutBoxResolver = LayoutBoxResolver;
        realm.FrameScrollReader = FrameScrollReader;
        realm.FrameScrollWriter = FrameScrollWriter;
        realm.ExecutionCancellation = ExecutionCancellation;
        realm.Sandbox = realm._embeddingFrameElement?.HasAttribute("sandbox") == true
            ? SandboxPolicy.FromIframeSandboxAttribute(realm._embeddingFrameElement.GetAttribute("sandbox"))
            : Sandbox;
        realm.AllowExternalScripts = AllowExternalScripts && realm.Sandbox.Allows(SandboxFeature.ExternalScripts);
        realm.ExecuteInlineScriptsOnInnerHTML = ExecuteInlineScriptsOnInnerHTML && realm.Sandbox.Allows(SandboxFeature.InlineScripts);
        realm.PermissionsPolicyProvider = PermissionsPolicyProvider;
        realm.DocumentSecurityContext = DocumentSecurityContext;
        realm.WindowWidth = ResolveFrameViewportDimension(realm._embeddingFrameElement, "width", WindowWidth);
        realm.WindowHeight = ResolveFrameViewportDimension(realm._embeddingFrameElement, "height", WindowHeight);
        realm.PageScriptByteBudget = PageScriptByteBudget;
        realm.IndexedDbBackend = IndexedDbBackend;
    }

    private static double ResolveFrameViewportDimension(Element frame, string attribute, double fallback) =>
        frame != null && TryReadDeclaredPixelDimension(frame, attribute, out var value) ? value : fallback;

    private bool TryGetFrameRealm(Document document, out FenJsBrowserScriptEngine realm)
    {
        var frame = TryGetFrameElementForDocument(document);
        if (frame != null && _iframeRealms.TryGetValue(frame, out realm))
        {
            return true;
        }

        realm = null;
        return false;
    }

    internal object EvaluateInSubdocumentForTest(Document document, string script) =>
        TryGetFrameRealm(document, out var realm) ? realm.Evaluate(script) : null;

    private void SyncFrameRealmObservables(Element frame, FenJsBrowserScriptEngine realm)
    {
        if (frame == null || realm == null)
        {
            return;
        }

        lock (_fenJsLock)
        {
            var proxy = GetOrCreateIFrameContentWindow(frame);
            CopyFrameRealmObservables(realm, proxy);
        }
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

        if (resetSession && _parentRealmOwner != null && _embeddingFrameElement != null)
        {
            ConfigureEmbeddedRealmGlobals();
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
                    // compiler/interpreter are gone â€” bail cleanly.
                    if (dispatchGeneration != _fenJsSessionGeneration ||
                        _compiler == null || _interpreter == null)
                    {
                        throw new InvalidOperationException(
                            "[FenJsBridge] JS session was reset during evaluation dispatch " +
                            $"(dispatchGen={dispatchGeneration} currentGen={_fenJsSessionGeneration} " +
                            $"_compiler={_compiler != null} _interpreter={_interpreter != null}). " +
                            "This is expected when a page script triggers a navigation " +
                            "(e.g. WAF challenge â†’ location.reload).");
                    }
                    _fenJsEvaluationCount++;
                    var function = _compiler.CompileScript(new SourceText(script, "<fenbrowser-fenjs-eval>"));
                    RegisterCallbackFunctionProvenance(function, GetCurrentScriptRecord());
                    new BytecodeVerifier().Verify(function);
                    return _interpreter.Execute(function);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            // Session-reset InvalidOperationException is expected after async
            // navigation â€” surface it cleanly without a stack trace.
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
    // thread â€” the path page scripts run on. FenBrowser.Host already re-enters its main
    // loop on a 16 MB thread for exactly this reason; mirror that here so every FenJS
    // compile/execute gets a fat stack. A ThreadStatic flag makes re-entrant calls (a
    // native callback that evaluates more script) run inline instead of spawning â€” and,
    // critically, avoids dead-locking on _fenJsLock which the outer worker already holds.
    [ThreadStatic] private static bool _onFenJsLargeStackThread;
    // 256 MB: real-world minified bundles nest very deeply. The parser builds
    // operator chains iteratively, but the compiler's tree-walk recurses â€” and
    // x.com's i18n bundle compiles an ~8800-deep left-associative chain (16 MB
    // overflowed at ~1550). Give the compile/execute thread a fat stack; the
    // compiler's TryEnsureSufficientExecutionStack guard still aborts catchably
    // if even this is exceeded, rather than crashing the process.
    private const int FenJsLargeStackBytes = 256 * 1024 * 1024;
    private const int FenJsBrowserInstructionBudget = 100_000_000;
    private const int FenJsBrowserTaskInstructionBudget = 10_000_000;
    private const int FenJsBrowserParserMaxRecursionDepth = 1024;
    private const int FenJsBoundaryGcAllocationThreshold = 4_096;

    private void CollectFenJsHeapAtSafeBoundary()
    {
        if (_interpreter == null || _interpreter.IsExecuting)
        {
            return;
        }

        var heap = _interpreter.Heap;
        if (heap.AllocationCount - _fenJsAllocationCountAtLastBoundaryGc < FenJsBoundaryGcAllocationThreshold)
        {
            return;
        }

        heap.CollectGarbage();
        _fenJsAllocationCountAtLastBoundaryGc = heap.AllocationCount;
    }

    // Persistent large-stack worker thread â€” created once per engine instance
    // and reused across all page-script evaluations.  Creating+joining a 256 MB
    // thread per script (60+ for a typical SPA) wastes ~500 ms per page load in
    // thread start/stop overhead alone; reusing the same thread cuts that to
    // near-zero after the first evaluation.
    private Thread _fenJsWorkerThread;
    private readonly AutoResetEvent _fenJsWorkAvailable = new AutoResetEvent(false);
    private readonly ConcurrentQueue<FenJsWorkItem> _fenJsInputWorkQueue = new();
    private readonly ConcurrentQueue<FenJsWorkItem> _fenJsWorkQueue = new();
    private bool _fenJsWorkerRunning;

    private sealed class FenJsWorkItem
    {
        public FenJsWorkItem(Func<object> work, int instructionBudget)
        {
            Work = work ?? throw new ArgumentNullException(nameof(work));
            InstructionBudget = instructionBudget;
        }

        public Func<object> Work { get; }
        public int InstructionBudget { get; }
        public TaskCompletionSource<object> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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

            while (TryDequeueFenJsWork(out var workItem))
            {
                // A bounded caller may time out while its item is still queued.
                // Do not execute that stale event after the caller has moved on.
                if (workItem.Completion.Task.IsCompleted)
                    continue;

                try
                {
                    var interpreter = _interpreter;
                    var result = interpreter == null
                        ? workItem.Work()
                        : interpreter.RunWithExecutionBudget(
                            ResolveFenJsScriptTimeoutMs(),
                            workItem.InstructionBudget,
                            workItem.Work);
                    workItem.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    workItem.Completion.TrySetException(ex);
                }
            }
        }
    }

    private bool TryDequeueFenJsWork(out FenJsWorkItem workItem)
    {
        return _fenJsInputWorkQueue.TryDequeue(out workItem) ||
               _fenJsWorkQueue.TryDequeue(out workItem);
    }

    private T RunFenJsWithLargeStack<T>(Func<T> work)
    {
        return RunFenJsWithLargeStack(
            work,
            waitForWorkerMs: -1,
            instructionBudget: FenJsBrowserInstructionBudget);
    }

    private JsValue EvaluateBootstrapWithFenJsRaw(string script)
    {
        Interlocked.Increment(ref _suppressMissingHostAssignmentTracking);
        try
        {
            return EvaluateWithFenJsRaw(script);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressMissingHostAssignmentTracking);
        }
    }

    private T RunFenJsWithLargeStack<T>(
        Func<T> work,
        long waitForWorkerMs,
        int instructionBudget = FenJsBrowserInstructionBudget,
        bool prioritize = false)
    {
        if (_onFenJsLargeStackThread)
        {
            // Re-entrant call from within the large-stack thread itself â€”
            // run inline to avoid deadlocking the persistent worker.
            return work();
        }

        EnsureFenJsWorkerRunning();

        var workItem = new FenJsWorkItem(() => (object)work(), instructionBudget);
        (prioritize ? _fenJsInputWorkQueue : _fenJsWorkQueue).Enqueue(workItem);
        _fenJsWorkAvailable.Set();

        object result;
        if (waitForWorkerMs >= 0)
        {
            try
            {
                result = workItem.Completion.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(waitForWorkerMs))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException)
            {
                var timeout = new FenBrowser.FenEngine.Errors.FenTimeoutError(
                    $"Timed out waiting for FenJS worker after {waitForWorkerMs} ms.");
                workItem.Completion.TrySetException(timeout);
                throw timeout;
            }
        }
        else
        {
            result = workItem.Completion.Task.GetAwaiter().GetResult();
        }

        if (result == null && typeof(T).IsValueType)
        {
            throw new InvalidOperationException(
                "[FenJsBridge] JS worker returned null; the JS session was likely " +
                "reset by a navigation while the evaluation was in flight. " +
                "(_fenJsSessionGeneration=" + _fenJsSessionGeneration + ")");
        }

        return (T)result;
    }

    /// <summary>
    /// Phase 12: non-blocking variant of <see cref="RunFenJsWithLargeStack{T}"/>.
    /// Posts work to the JS worker thread and returns a Task that completes when
    /// the worker finishes, without blocking the calling (engine) thread. Applies
    /// the same timeout policy as the synchronous path via CancellationToken.
    /// </summary>
    private async Task<T> RunFenJsWithLargeStackAsync<T>(
        Func<T> work,
        long timeoutMs = -1,
        int instructionBudget = FenJsBrowserInstructionBudget,
        bool prioritize = false)
    {
        if (_onFenJsLargeStackThread)
        {
            // Re-entrant: run inline.
            return work();
        }

        EnsureFenJsWorkerRunning();

        var workItem = new FenJsWorkItem(
            () => (object)work(),
            instructionBudget);
        var cts = timeoutMs > 0
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs))
            : null;

        (prioritize ? _fenJsInputWorkQueue : _fenJsWorkQueue).Enqueue(workItem);
        _fenJsWorkAvailable.Set();

        try
        {
            if (cts != null)
            {
                using var reg = cts.Token.Register(() =>
                {
                    workItem.Completion.TrySetException(new FenBrowser.FenEngine.Errors.FenTimeoutError(
                        $"FenJS async work timed out after {timeoutMs}ms"));
                });

                using (cts)
                {
                    var result = await workItem.Completion.Task.ConfigureAwait(false);
                    return (T)result;
                }
            }
            else
            {
                var result = await workItem.Completion.Task.ConfigureAwait(false);
                return (T)result;
            }
        }
        catch (FenBrowser.FenEngine.Errors.FenTimeoutError)
        {
            // Propagate timeout directly without wrapping.
            throw;
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
        _currentDocumentId = "document-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Interlocked.Exchange(ref _callbackFailureSequence, 0);
        Interlocked.Exchange(ref _diagnosticCallbackIdSequence, 0);
        _diagnosticPromiseRejectionTracker?.Reset();
        _callbackFunctionProvenance.Clear();
        _pendingPromiseRejectionDiagnostics.Clear();
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
        _diagnosticPromiseRejectionTracker?.FlushPending();
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
        _diagnosticPromiseRejectionTracker?.FlushPending();
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


                // Determine async/defer per WHATWG HTML Â§4.12.1
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
                    // External script â€” validate, then kick off fetch concurrently
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

                    // Deduplicate: same URL â†’ same fetch task
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
                    // Inline script â€” validate now, code is already in DOM
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

                    var inlineNonce = scriptElement.GetAttribute("nonce");
                    if (NonceAllowed != null && !NonceAllowed(inlineNonce))
                    {
                        MarkScriptSkipped(scriptRecord, "csp-inline-blocked");
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

            // Categorize scripts for phased execution per WHATWG HTML Â§4.12.1
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

            // Phase 2c: Fire-and-forget async scripts. Per WHATWG HTML Â§4.12.1,
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
            // do NOT re-throw â€” the page should render even if scripts fail.
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
    /// logged but never re-thrown (per WHATWG HTML Â§8.1.3.2).
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
                // Await this individual fetch â€” it may already be complete since
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

    private void RecordMissingHostProperty(
        object receiver,
        string ownerName,
        string property,
        Uri baseUri,
        bool functionPrototypeMarkerObserved = false,
        MissingApiOperationKind operationKind = MissingApiOperationKind.Read)
    {
        if (receiver != null)
        {
            var reads = _missingHostPropertyReads.GetOrCreateValue(receiver);
            lock (reads)
            {
                reads.Add(property);
            }
        }

        EngineCapabilities.LogUnsupportedJs(ownerName, property, "missing host property");
        RecordMissingBrowserApi(
            ownerName,
            property,
            "missing host property",
            string.Empty,
            GetCurrentScriptRecord(),
            baseUri ?? _currentBaseUri,
            operationKind,
            functionPrototypeMarkerObserved: functionPrototypeMarkerObserved);
    }

    private void RecordHostPropertyAssignment(object receiver, string ownerName, string property, Uri baseUri)
    {
        if (Volatile.Read(ref _suppressMissingHostAssignmentTracking) != 0 ||
            receiver == null ||
            string.IsNullOrWhiteSpace(property))
        {
            return;
        }

        var reads = _missingHostPropertyReads.GetOrCreateValue(receiver);
        bool assignmentBeforeRead;
        lock (reads)
        {
            assignmentBeforeRead = !reads.Contains(property);
        }

        if (GetHostPropertyStore(receiver).ContainsKey(property))
        {
            return;
        }

        RecordMissingBrowserApi(
            ownerName,
            property,
            assignmentBeforeRead
                ? "host property assigned before read"
                : "host property assigned after read",
            string.Empty,
            GetCurrentScriptRecord(),
            baseUri ?? _currentBaseUri,
            MissingApiOperationKind.Write,
            assignmentBeforeRead,
            assignmentObserved: true);
    }

    private void RecordMissingBrowserApi(
        string objectOrPrototype,
        string propertyName,
        string reason,
        string exceptionText,
        BrowserScriptLoadingRecord scriptRecord,
        Uri baseUri,
        MissingApiOperationKind operationKind = MissingApiOperationKind.Read,
        bool assignmentBeforeRead = false,
        bool assignmentObserved = false,
        bool functionPrototypeMarkerObserved = false,
        bool descriptorTargetIsPrototype = false)
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
            ExceptionText = exceptionText ?? string.Empty,
            OperationKind = operationKind,
            ReceiverType = ownerName,
            AssignmentBeforeRead = assignmentBeforeRead,
            AssignmentObserved = assignmentObserved,
            FunctionPrototypeMarkerObserved = functionPrototypeMarkerObserved,
            DescriptorTargetIsPrototype = descriptorTargetIsPrototype
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

                string entrySpecifier = moduleUri?.AbsoluteUri ?? "<entry>";
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

    private FenJsBrowserScriptEngine[] DetachFrameRealms()
    {
        lock (_fenJsLock)
        {
            var realms = _iframeRealms
                .Select(entry => entry.Value)
                .Distinct()
                .ToArray();
            _iframeRealms = new ConditionalWeakTable<Element, FenJsBrowserScriptEngine>();
            return realms;
        }
    }

    private void ReleaseRealmResources()
    {
        foreach (var frameRealm in DetachFrameRealms())
        {
            frameRealm.ReleaseRealmResources();
        }

        Interlocked.Increment(ref _fenJsSessionGeneration);
        RequestRender = null;
        _parentRealmOwner = null;
        _embeddingFrameElement = null;
        CloseOwnedMessagePorts();

        foreach (var timerEntry in _fenJsTimers.ToArray())
        {
            if (_fenJsTimers.TryRemove(timerEntry.Key, out var timer))
            {
                timer.Dispose();
            }
        }

        lock (_webSocketHosts)
        {
            foreach (var (_, host) in _webSocketHosts)
            {
                try { host.Close(1001, "Navigation"); } catch { }
                try { host.Dispose(); } catch { }
            }
            _webSocketHosts.Clear();
        }
    }

    private void CloseOwnedMessagePorts()
    {
        lock (_fenJsLock)
        {
            foreach (var endpoint in _messagePortEndpoints.Values.Distinct())
            {
                endpoint.Closed = true;
                endpoint.Owner = null;
                endpoint.Port = JsValue.Undefined;
            }
            _messagePortEndpoints.Clear();
        }
    }

    private void ResetFenJsSession()
    {
        var detachedFrameRealms = DetachFrameRealms();
        foreach (var frameRealm in detachedFrameRealms)
        {
            frameRealm.ReleaseRealmResources();
        }

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
            CloseOwnedMessagePorts();
            foreach (var timerEntry in _fenJsTimers.ToArray())
            {
                if (_fenJsTimers.TryRemove(timerEntry.Key, out var timer))
                {
                    timer.Dispose();
                }
            }

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
                // CPU â€” enough for even the largest page bundles to finish.
                InstructionBudget = FenJsBrowserInstructionBudget,
                InterruptCallback = () => !_executionCancellation.IsCancellationRequested,
                MaxCallDepth = 1024,
                ParserMaxRecursionDepth = FenJsBrowserParserMaxRecursionDepth
            };
            _fenJsAllocationCountAtLastBoundaryGc = 0;
            _interpreter.Heap.AddRootSource(this);

            // Wire a diagnostic Promise rejection tracker so unhandled rejections
            // surface in engine logs with the rejection reason and source ownership.
            _diagnosticPromiseRejectionTracker = new FenJsDiagnosticPromiseRejectionTracker(
                _interpreter.PromiseRejectionTracker,
                TrackPromiseRejection,
                RecordPromiseRejection);
            _interpreter.PromiseRejectionTracker = _diagnosticPromiseRejectionTracker;

            _hostHandleCache.Clear();
            _documentEventListeners.Clear();
            _windowEventListeners.Clear();
            _visualViewportEventListeners.Clear();
            _embeddedParentWindowListeners.Clear();
            _hostCallableCache = new ConditionalWeakTable<object, Dictionary<string, JsValue>>();
            _hostPropertyStore = new ConditionalWeakTable<object, Dictionary<string, JsValue>>();
            _missingHostPropertyReads = new ConditionalWeakTable<object, HashSet<string>>();
            _elementEventListeners = new ConditionalWeakTable<object, List<BrowserEventListener>>();
            _iframeWindowEventListeners = new ConditionalWeakTable<Element, List<BrowserEventListener>>();
            _fenJsTopWindowFacade = JsValue.Undefined;
            _fenJsSameOriginTopWindowFacade = JsValue.Undefined;
            _visualViewport = JsValue.Undefined;
            _hostPrototypeNames.Clear();
            _activeWindowEventListeners = null;
            _activeWindowEventTarget = JsValue.Undefined;
            _fenJsFileConstructor = JsValue.Undefined;
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
            ClearSelectionState();
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
                ClearSelectionState();

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
        var parentWindow = ReadJsProperty(frameWindow, "parent");
        if (parentWindow.Tag == JsValueTag.Undefined)
        {
            parentWindow = _fenJsGlobalThis.Tag == JsValueTag.Undefined
                ? previousWindow
                : GetOrCreateTopWindowFacade();
        }
        var frameBaseUri = baseUri ??
            TryCreateUri(document?.BaseURI) ??
            TryCreateUri(document?.DocumentURI) ??
            TryCreateUri(document?.URL);

        if (frameBaseUri != null)
        {
            _currentBaseUri = frameBaseUri;
        }
        _activeParentBaseUri = GetParentDocumentUri(frameElement) ?? previousBaseUri;
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
        var callbackFrameElement = ResolveHostObjectOrNull<Element>(ReadJsProperty(windowTarget, "__fenFrameElement"));
        _activeParentBaseUri = GetParentDocumentUri(callbackFrameElement) ?? previousBaseUri;
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
                var ports = args.Count > 2 ? args[2] : JsValue.Undefined;
                var sourceWindow = GetActiveWindowEventTarget();
                QueueWindowMessage(
                    _fenJsGlobalThis,
                    _windowEventListeners,
                    data,
                    sourceWindow,
                    ports,
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
        // Always bind the host hooks so _owner is set â€” even if there's no
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
        _interpreter.RegisterGlobalValue(
            "__fenDispatchMessagePort",
            _interpreter.AllocateNativeFunction(
                "__fenDispatchMessagePort",
                (_, args) =>
                {
                    var windowTarget = args.Count > 0 ? args[0] : JsValue.Undefined;
                    var callback = args.Count > 1 ? args[1] : JsValue.Undefined;
                    var callbackThis = args.Count > 2 ? args[2] : JsValue.Undefined;
                    var eventValue = args.Count > 3 ? args[3] : JsValue.Undefined;
                    if (!_interpreter.CanCallValue(callback))
                    {
                        return JsValue.Undefined;
                    }

                    var frameElement = ResolveHostObjectOrNull<Element>(ReadJsProperty(windowTarget, "__fenFrameElement"));
                    var listeners = frameElement == null
                        ? _windowEventListeners
                        : GetIFrameWindowListeners(frameElement);
                    using (ActivateWindowCallbackContext(windowTarget, listeners))
                    {
                        _interpreter.InvokeFunction(callback, new[] { eventValue }, callbackThis);
                    }

                    return JsValue.Undefined;
                },
                length: 4));
        _interpreter.RegisterGlobalValue(
            "__fenRegisterMessageChannel",
            _interpreter.AllocateNativeFunction(
                "__fenRegisterMessageChannel",
                (_, args) => RegisterMessageChannel(args),
                length: 2));
        _interpreter.RegisterGlobalValue(
            "__fenPostMessagePort",
            _interpreter.AllocateNativeFunction(
                "__fenPostMessagePort",
                (_, args) => PostMessagePort(args),
                length: 3));
        _interpreter.RegisterGlobalValue(
            "__fenCloseMessagePort",
            _interpreter.AllocateNativeFunction(
                "__fenCloseMessagePort",
                (_, args) => CloseMessagePort(args),
                length: 1));
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
                    _host.Log(msg);
                    return JsValue.Undefined;
                },
                length: 2));

        // â”€â”€ IndexedDB persistence bridge â”€â”€
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
        EvaluateBootstrapWithFenJsRaw(
            // â”€â”€ window.console â”€â”€ Must come before alert/confirm/prompt stubs
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
            // â”€â”€ navigator.sendBeacon â”€â”€
            "navigator.sendBeacon = function(url, data) { return true; };" +
            // Stub navigator.plugins and navigator.mimeTypes â€” real browsers
            // always have these (even if empty). Google's bot detection checks
            // their presence and shape.
            "navigator.plugins = { length: 0, item: function() { return null; }, namedItem: function() { return null; }, refresh: function() {} };" +
            "navigator.mimeTypes = { length: 0, item: function() { return null; }, namedItem: function() { return null; } };" +
            // â”€â”€ navigator.cookieDeprecationLabel â”€â”€ https://wicg.github.io/cookie-deprecation-label/
            // Google reCAPTCHA enterprise.js checks this.  Must be an object with
            // getValue() that returns a Promise<string>.
            "navigator.cookieDeprecationLabel = { getValue: function() { return Promise.resolve('no-signal'); } };" +
            // Stub window.chrome â€” Chromium-based browsers always expose this.
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
            // on cross-origin frames throws TypeError â†’ browser crash.
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
        _interpreter.RegisterGlobalValue("devicePixelRatio", JsValue.FromNumber(1));
        _visualViewport = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["width"] = JsValue.FromNumber(WindowWidth),
            ["height"] = JsValue.FromNumber(WindowHeight),
            ["offsetLeft"] = JsValue.FromInt32(0),
            ["offsetTop"] = JsValue.FromInt32(0),
            ["pageLeft"] = JsValue.FromInt32(0),
            ["pageTop"] = JsValue.FromInt32(0),
            ["scale"] = JsValue.FromNumber(1),
            ["onresize"] = JsValue.Null,
            ["onscroll"] = JsValue.Null
        });
        _interpreter.SetObjectProperty(
            _visualViewport,
            "addEventListener",
            _interpreter.AllocateNativeFunction(
                "addEventListener",
                (_, args) =>
                {
                    AddBrowserEventListener(_visualViewportEventListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.SetObjectProperty(
            _visualViewport,
            "removeEventListener",
            _interpreter.AllocateNativeFunction(
                "removeEventListener",
                (_, args) =>
                {
                    RemoveBrowserEventListener(_visualViewportEventListeners, args);
                    return JsValue.Undefined;
                },
                length: 2));
        _interpreter.SetObjectProperty(
            _visualViewport,
            "dispatchEvent",
            _interpreter.AllocateNativeFunction(
                "dispatchEvent",
                (_, args) => JsValue.FromBoolean(
                    DispatchVisualViewportEvent(args.Count > 0 ? args[0] : JsValue.Undefined)),
                length: 1));
        _interpreter.RegisterGlobalValue("visualViewport", _visualViewport);
        var screenOrientation = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString(WindowWidth >= WindowHeight ? "landscape-primary" : "portrait-primary"),
            ["angle"] = JsValue.FromInt32(0)
        });
        var screen = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["width"] = JsValue.FromNumber(WindowWidth),
            ["height"] = JsValue.FromNumber(WindowHeight),
            ["availWidth"] = JsValue.FromNumber(WindowWidth),
            ["availHeight"] = JsValue.FromNumber(WindowHeight),
            ["availLeft"] = JsValue.FromInt32(0),
            ["availTop"] = JsValue.FromInt32(0),
            ["colorDepth"] = JsValue.FromInt32(24),
            ["pixelDepth"] = JsValue.FromInt32(24),
            ["orientation"] = screenOrientation
        });
        _interpreter.RegisterGlobalValue("screen", screen);

        var globalThisValue = EvaluateWithFenJsRaw("globalThis");
        _fenJsGlobalThis = globalThisValue;
        _interpreter.RegisterGlobalValue("window", globalThisValue);
        _interpreter.RegisterGlobalValue("self", globalThisValue);
        _interpreter.RegisterGlobalValue("top", globalThisValue);
        _interpreter.RegisterGlobalValue("parent", globalThisValue);
        _interpreter.RegisterGlobalValue("name", JsValue.FromString(string.Empty));
        _interpreter.RegisterGlobalValue(
            "getSelection",
            _interpreter.AllocateNativeFunction(
                "getSelection",
                (_, _) => CreateSelectionValue(),
                length: 0));
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
        // â”€â”€ window.alert / confirm / prompt â€” fire-and-forget dialog display
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
                    var msg = args.Count > 0 ? ToDialogString(args[0]) : "";
                    _host.Alert(msg);
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
                    var msg = args.Count > 0 ? ToDialogString(args[0]) : "";
                    var accepted = _host.Confirm(msg);
                    PostDialogAsync("confirm", msg, "");
                    return JsValue.FromBoolean(accepted);
                },
                length: 1));
        _interpreter.RegisterGlobalValue(
            "prompt",
            _interpreter.AllocateNativeFunction(
                "prompt",
                (_, args) =>
                {
                    var msg = args.Count > 0 ? ToDialogString(args[0]) : "";
                    var def = args.Count > 1 && args[1].Tag != FenBrowser.Js.Runtime.JsValueTag.Undefined ? ToDialogString(args[1]) : "";
                    var response = _host.Prompt(msg, def);
                    PostDialogAsync("prompt", msg, def);
                    return response == null ? JsValue.Null : JsValue.FromString(response);
                },
                length: 2));

        // â”€â”€ window.open â€” calls into Host to create a new tab and returns a
        // host object representing the popup window with document.write/close etc.
        _interpreter.RegisterGlobalValue(
            "open",
            _interpreter.AllocateNativeFunction(
                "open",
                (_, args) =>
                {
                    var url = args.Count > 0 ? ToDialogString(args[0]) : "";
                    var name = args.Count > 1 ? ToDialogString(args[1]) : "";
                    var features = args.Count > 2 ? ToDialogString(args[2]) : "";

                    var bridge = JsDialogBridge.OpenWindow;
                    if (bridge == null)
                    {
                        FenLogger.Warn("[open] JsDialogBridge not installed â€” returning null", LogCategory.JavaScript);
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
        InstallFenJsEventTarget();
        InstallFenJsNativeBrowserConstructors();
        _fenJsDomConstructorsInstalled = true;
        TryAttachFenJsPrototype(EvaluateWithFenJsRaw("document"), document, HostObjectKind.DomDocument);
        InstallFenJsMutationObserver();
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

        // navigator.serviceWorker â€” C# host property so it's available before JS runs
        // Returns resolved promises so apps that await registration don't hang.
        SetStoredHostProperty(navigator, "serviceWorker", CreateDefaultServiceWorkerStub());

        // navigator.storage â€” C# host property
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
        var crossOriginIsolatedLiteral = IsCrossOriginIsolatedContext(baseUri) ? "true" : "false";
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

                // â”€â”€ WebSocket â”€â”€ https://websockets.spec.whatwg.org/
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
                                // JSON parse failure â€” ignore
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

                // â”€â”€ indexedDB â”€â”€ https://w3c.github.io/IndexedDB/
                // Partial in-memory compatibility implementation with persistence hooks.
                // Uses __fenIdb* C# native functions for persistence when available.
                if (typeof globalThis.indexedDB === 'undefined') {
                    // â”€â”€ Helpers â”€â”€
                    var _idbStore = {}; // { "db\0store": { _data: {key: value}, _indexes: {name: {keyPath, _data: {key: value}}} } }
                    var _idbVersions = {};
                    function _idbInitEventTarget(target) {
                        target._idbListeners = {};
                        target.addEventListener = function (type, callback) {
                            if (typeof callback !== 'function' &&
                                (callback === null || typeof callback !== 'object')) return;
                            type = String(type || '');
                            var listeners = this._idbListeners[type] || (this._idbListeners[type] = []);
                            if (listeners.indexOf(callback) < 0) listeners.push(callback);
                        };
                        target.removeEventListener = function (type, callback) {
                            var listeners = this._idbListeners[String(type || '')];
                            if (!listeners) return;
                            var index = listeners.indexOf(callback);
                            if (index >= 0) listeners.splice(index, 1);
                        };
                    }
                    function _idbDispatch(target, type, init) {
                        var event = init || {};
                        event.type = type;
                        if (!event.target) event.target = target;
                        event.currentTarget = target;
                        event.defaultPrevented = !!event.defaultPrevented;
                        event._idbHadException = !!event._idbHadException;
                        event.preventDefault = function () { this.defaultPrevented = true; };
                        event.stopPropagation = function () {};
                        function invoke(callback) {
                            try {
                                if (typeof callback === 'function') {
                                    callback.call(target, event);
                                } else {
                                    var handleEvent = callback.handleEvent;
                                    if (typeof handleEvent !== 'function') throw new TypeError('handleEvent is not callable');
                                    handleEvent.call(callback, event);
                                }
                            } catch (error) {
                                event._idbHadException = true;
                            }
                        }
                        var handler = target['on' + type];
                        if (typeof handler === 'function') invoke(handler);
                        var listeners = (target._idbListeners[type] || []).slice();
                        for (var i = 0; i < listeners.length; i++) invoke(listeners[i]);
                        return !event.defaultPrevented;
                    }
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
                            var event = {};
                            _idbDispatch(request, 'success', event);
                            var transaction = request.transaction;
                            if (transaction && transaction._pending > 0) transaction._pending--;
                            if (event._idbHadException) {
                                _idbAbortTransaction(transaction);
                            } else {
                                _idbMaybeCompleteTransaction(transaction);
                            }
                        }, 0);
                    }
                    function _idbFireError(request, message) {
                        globalThis.setTimeout(function () {
                            request.error = { name: 'AbortError', message: String(message || '') };
                            request.readyState = 'done';
                            var event = {};
                            _idbDispatch(request, 'error', event);
                            var transaction = request.transaction;
                            if (transaction) {
                                _idbDispatch(transaction, 'error', event);
                                if (transaction.db) _idbDispatch(transaction.db, 'error', event);
                            }
                            if (transaction && transaction._pending > 0) transaction._pending--;
                            if (event._idbHadException || !event.defaultPrevented) {
                                _idbAbortTransaction(transaction);
                            } else {
                                _idbMaybeCompleteTransaction(transaction);
                            }
                        }, 0);
                    }
                    function _idbAbortTransaction(transaction, errorName) {
                        if (!transaction || !transaction._active) return;
                        transaction._active = false;
                        transaction._aborted = true;
                        transaction.error = {
                            name: String(errorName || 'AbortError'),
                            message: 'The transaction was aborted'
                        };
                        var event = {};
                        _idbDispatch(transaction, 'abort', event);
                        if (transaction.db) _idbDispatch(transaction.db, 'abort', event);
                    }
                    function _idbMaybeCompleteTransaction(transaction) {
                        if (!transaction || !transaction._active ||
                            transaction._pending > 0 || transaction._completionQueued) return;
                        transaction._completionQueued = true;
                        globalThis.setTimeout(function () {
                            transaction._completionQueued = false;
                            if (!transaction._active || transaction._pending > 0) return;
                            transaction._active = false;
                            _idbDispatch(transaction, 'complete');
                        }, 0);
                    }
                    // â”€â”€ IDBRequest â”€â”€
                    function IDBRequest() {
                        this.result = undefined;
                        this.error = null;
                        this.readyState = 'pending';
                        this.onsuccess = null;
                        this.onerror = null;
                        this.source = null;
                        this.transaction = null;
                        _idbInitEventTarget(this);
                    }
                    function IDBOpenDBRequest() {
                        IDBRequest.call(this);
                        this.onupgradeneeded = null;
                        this.onblocked = null;
                    }
                    function _idbRequestResult(source, transaction, result) {
                        var request = new IDBRequest();
                        request.source = source || null;
                        request.transaction = transaction || null;
                        if (transaction && transaction._active) transaction._pending++;
                        _idbFireSuccess(request, result);
                        return request;
                    }
                    function _idbRequestError(source, transaction, message) {
                        var request = new IDBRequest();
                        request.source = source || null;
                        request.transaction = transaction || null;
                        if (transaction && transaction._active) transaction._pending++;
                        _idbFireError(request, message);
                        return request;
                    }
                    function IDBIndex(name, objectStore, keyPath, options, indexRef, transaction) {
                        this.name = String(name);
                        this.objectStore = objectStore;
                        this.keyPath = keyPath;
                        this.unique = !!(options && options.unique);
                        this.multiEntry = !!(options && options.multiEntry);
                        this._indexRef = indexRef;
                        this._transaction = transaction;
                    }
                    IDBIndex.prototype.get = function (key) {
                        var data = this._indexRef._data;
                        return _idbRequestResult(
                            this,
                            this._transaction,
                            data.hasOwnProperty(key) ? data[key] : undefined);
                    };
                    IDBIndex.prototype.getKey = function (key) {
                        var data = this._indexRef._data;
                        return _idbRequestResult(
                            this,
                            this._transaction,
                            data.hasOwnProperty(key) ? key : undefined);
                    };
                    IDBIndex.prototype.getAll = function () {
                        var values = [];
                        var keys = Object.keys(this._indexRef._data);
                        for (var i = 0; i < keys.length; i++) values.push(this._indexRef._data[keys[i]]);
                        return _idbRequestResult(this, this._transaction, values);
                    };
                    IDBIndex.prototype.getAllKeys = function () {
                        return _idbRequestResult(this, this._transaction, Object.keys(this._indexRef._data));
                    };
                    IDBIndex.prototype.count = function () {
                        return _idbRequestResult(this, this._transaction, Object.keys(this._indexRef._data).length);
                    };
                    IDBIndex.prototype.openCursor = function () { return undefined; };
                    IDBIndex.prototype.openKeyCursor = function () { return undefined; };
                    globalThis.IDBIndex = IDBIndex;

                    function _idbIsValidKeyPath(keyPath) {
                        if (Array.isArray(keyPath)) {
                            if (keyPath.length === 0) return false;
                            for (var i = 0; i < keyPath.length; i++) {
                                if (keyPath[i] === '' || !_idbIsValidKeyPath(keyPath[i])) return false;
                            }
                            return true;
                        }
                        if (typeof keyPath !== 'string') return false;
                        if (keyPath === '') return true;
                        var parts = keyPath.split('.');
                        for (var j = 0; j < parts.length; j++) {
                            if (!/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(parts[j])) return false;
                        }
                        return true;
                    }

                    function _idbCreateIndex(objectStore, storeRef, transaction, indexName, keyPath, options) {
                        var name = String(indexName);
                        if (storeRef._deleted || !transaction || transaction.mode !== 'versionchange') {
                            throw new DOMException(
                                'Index creation requires a live versionchange object store.',
                                'InvalidStateError');
                        }
                        if (!transaction._active) {
                            throw new DOMException('The transaction is inactive.', 'TransactionInactiveError');
                        }
                        if (storeRef._indexes.hasOwnProperty(name)) {
                            throw new DOMException('An index with this name already exists.', 'ConstraintError');
                        }
                        if (!_idbIsValidKeyPath(keyPath)) {
                            throw new DOMException('The key path is invalid.', 'SyntaxError');
                        }
                        if (Array.isArray(keyPath) && options && options.multiEntry) {
                            throw new DOMException(
                                'A multiEntry index cannot use a sequence key path.',
                                'InvalidAccessError');
                        }

                        var indexRef = {
                            keyPath: keyPath,
                            unique: !!(options && options.unique),
                            multiEntry: !!(options && options.multiEntry),
                            _data: {}
                        };
                        storeRef._indexes[name] = indexRef;
                        storeRef._indexNames = storeRef._indexNames || [];
                        storeRef._indexNames.push(name);

                        var duplicate = false;
                        var dataKeys = Object.keys(storeRef._data);
                        for (var i = 0; i < dataKeys.length; i++) {
                            var value = storeRef._data[dataKeys[i]];
                            var indexKey = _idbExtractKey(value, keyPath);
                            if (indexKey === undefined || indexKey === null) continue;
                            if (indexRef.unique && indexRef._data.hasOwnProperty(indexKey)) {
                                duplicate = true;
                                continue;
                            }
                            indexRef._data[indexKey] = value;
                        }
                        if (duplicate) {
                            globalThis.setTimeout(function () {
                                _idbAbortTransaction(transaction, 'ConstraintError');
                            }, 0);
                        }
                        return new IDBIndex(name, objectStore, keyPath, options, indexRef, transaction);
                    }
                    // â”€â”€ IDBDatabase â”€â”€
                    function IDBDatabase(name, version) {
                        this.name = String(name || 'default');
                        this.version = version || 1;
                        this.objectStoreNames = [];
                        _idbInitEventTarget(this);
                    }
                    IDBDatabase.prototype.createObjectStore = function (storeName, options) {
                        __fenLog('warn', '[IDB] createObjectStore("' + String(storeName) + '") in "' + this.name + '"');
                        var key = this.name + '\0' + storeName;
                        _idbStore[key] = {
                            _data: {},
                            _indexes: {},
                            _indexNames: [],
                            _deleted: false
                        };
                        this.objectStoreNames.push(String(storeName));
                        var keyPath = (options && options.keyPath) || null;
                        var autoIncrement = !!(options && options.autoIncrement);
                        var storeRef = _idbStore[key];
                        storeRef._keyPath = keyPath;
                        storeRef._autoIncrement = autoIncrement;
                        var transaction = this._versionchangeTransaction;
                        var objectStore = {
                            name: String(storeName),
                            keyPath: keyPath,
                            autoIncrement: autoIncrement,
                            indexNames: storeRef._indexNames,
                            createIndex: function (indexName, keyPath, opts) {
                                __fenLog('warn', '[IDB] createIndex("' + String(indexName) + '", "' + String(keyPath) + '") on "' + String(storeName) + '"');
                                return _idbCreateIndex(
                                    objectStore,
                                    storeRef,
                                    transaction,
                                    indexName,
                                    keyPath,
                                    opts);
                            },
                            deleteIndex: function (indexName) {
                                delete storeRef._indexes[String(indexName)];
                                var indexPosition = storeRef._indexNames.indexOf(String(indexName));
                                if (indexPosition >= 0) storeRef._indexNames.splice(indexPosition, 1);
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
                                __fenLog('warn', '[IDB] get(' + String(key) + ') from "' + String(storeName) + '" â†’ ' + (storeRef._data.hasOwnProperty(key) ? 'hit' : 'miss'));
                                return storeRef._data.hasOwnProperty(key) ? storeRef._data[key] : undefined;
                            },
                            getAll: function () {
                                var vals = []; var dk = Object.keys(storeRef._data);
                                for (var i = 0; i < dk.length; i++) vals.push(storeRef._data[dk[i]]);
                                __fenLog('warn', '[IDB] getAll() from "' + String(storeName) + '" â†’ ' + vals.length + ' records');
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
                                        __fenLog('warn', '[IDB] idx.get(' + String(k) + ') â†’ ' + (r !== undefined ? 'hit' : 'miss'));
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
                        return objectStore;
                    };
                    IDBDatabase.prototype.deleteObjectStore = function (storeName) {
                        var key = this.name + '\0' + storeName;
                        if (_idbStore[key]) _idbStore[key]._deleted = true;
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
                            db: this,
                            objectStoreNames: stores.slice(),
                            _dbName: dbName,
                            _active: true,
                            _pending: 0,
                            _completionQueued: false,
                            objectStore: function (name) {
                                var key = dbName + '\0' + name;
                                var storeRef = _idbStore[key] = _idbStore[key] || {
                                    _data: {},
                                    _indexes: {},
                                    _indexNames: [],
                                    _deleted: false,
                                    _keyPath: null,
                                    _autoIncrement: false
                                };
                                storeRef._indexNames = storeRef._indexNames || Object.keys(storeRef._indexes);
                                var kp = storeRef._keyPath || null;
                                var ai = storeRef._autoIncrement || false;
                                var objectStore = {
                                    name: String(name),
                                    keyPath: kp,
                                    autoIncrement: ai,
                                    indexNames: storeRef._indexNames,
                                    createIndex: function (indexName, keyPath, options) {
                                        return _idbCreateIndex(
                                            objectStore,
                                            storeRef,
                                            tx,
                                            indexName,
                                            keyPath,
                                            options);
                                    },
                                    deleteIndex: function (indexName) {
                                        if (storeRef._deleted || tx.mode !== 'versionchange') {
                                            throw new DOMException(
                                                'Index deletion requires a live versionchange object store.',
                                                'InvalidStateError');
                                        }
                                        if (!tx._active) {
                                            throw new DOMException(
                                                'The transaction is inactive.',
                                                'TransactionInactiveError');
                                        }
                                        delete storeRef._indexes[String(indexName)];
                                        var indexPosition = storeRef._indexNames.indexOf(String(indexName));
                                        if (indexPosition >= 0) storeRef._indexNames.splice(indexPosition, 1);
                                    },
                                    put: function (value, keyOverride) {
                                        var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, kp);
                                        if ((k === undefined || k === null) && ai) {
                                            var max = 0; var dk = Object.keys(storeRef._data);
                                            for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                            k = max + 1;
                                        }
                                        if (k === undefined || k === null) {
                                            return _idbRequestError(this, tx, 'No key');
                                        }
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
                                        return _idbRequestResult(this, tx, k);
                                    },
                                    add: function (value, keyOverride) {
                                        var k = arguments.length > 1 ? keyOverride : _idbExtractKey(value, kp);
                                        if ((k === undefined || k === null) && ai) {
                                            var max = 0; var dk = Object.keys(storeRef._data);
                                            for (var i = 0; i < dk.length; i++) { var n = +dk[i]; if (!isNaN(n) && n > max) max = n; }
                                            k = max + 1;
                                        }
                                        if (storeRef._data.hasOwnProperty(k)) {
                                            return _idbRequestError(this, tx, 'Key already exists');
                                        }
                                        return this.put(value, k);
                                    },
                                    get: function (k) {
                                        var result = storeRef._data.hasOwnProperty(k) ? storeRef._data[k] : undefined;
                                        return _idbRequestResult(this, tx, result);
                                    },
                                    getAll: function () {
                                        var vals=[]; var dk=Object.keys(storeRef._data);
                                        for(var i=0;i<dk.length;i++) vals.push(storeRef._data[dk[i]]);
                                        return _idbRequestResult(this, tx, vals);
                                    },
                                    getAllKeys: function () {
                                        return _idbRequestResult(this, tx, Object.keys(storeRef._data));
                                    },
                                    getKey: function (k) {
                                        var result = storeRef._data.hasOwnProperty(k) ? k : undefined;
                                        return _idbRequestResult(this, tx, result);
                                    },
                                    delete: function (k) {
                                        delete storeRef._data[k];
                                        var idxNames = Object.keys(storeRef._indexes);
                                        for (var i = 0; i < idxNames.length; i++) delete storeRef._indexes[idxNames[i]]._data[k];
                                        if (typeof __fenIdbDelete === 'function') {
                                            try { __fenIdbDelete(dbName, String(name), String(k)); } catch(e) {}
                                        }
                                        return _idbRequestResult(this, tx, undefined);
                                    },
                                    clear: function () {
                                        storeRef._data = {};
                                        return _idbRequestResult(this, tx, undefined);
                                    },
                                    count: function () {
                                        return _idbRequestResult(this, tx, Object.keys(storeRef._data).length);
                                    },
                                    index: function (indexName) {
                                        var idx = storeRef._indexes[String(indexName)] || { keyPath: null, _data: {} };
                                        return new IDBIndex(
                                            String(indexName),
                                            objectStore,
                                            idx.keyPath,
                                            idx,
                                            idx,
                                            tx);
                                    }
                                };
                                return objectStore;
                            },
                            oncomplete: null,
                            onerror: null,
                            onabort: null,
                            abort: function () { _idbAbortTransaction(this); },
                            commit: function () {
                                _idbMaybeCompleteTransaction(this);
                            }
                        };
                        _idbInitEventTarget(tx);
                        // Version-change completion is coordinated by open().
                        if (txMode !== 'versionchange') _idbMaybeCompleteTransaction(tx);
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
                                var oldVersion = _idbVersions.hasOwnProperty(dbName) ? _idbVersions[dbName] : 0;
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
                                if (ver > oldVersion) {
                                    var upgradeTx = db.transaction([], 'versionchange');
                                    db._versionchangeTransaction = upgradeTx;
                                    request.transaction = upgradeTx;
                                    var upgradeEvent = {
                                        oldVersion: oldVersion,
                                        newVersion: ver
                                    };
                                    _idbDispatch(request, 'upgradeneeded', upgradeEvent);
                                    if (upgradeEvent._idbHadException) {
                                        _idbAbortTransaction(upgradeTx);
                                        request.error = { name: 'AbortError', message: 'The version change transaction was aborted' };
                                        request.readyState = 'done';
                                        _idbDispatch(request, 'error');
                                        return;
                                    }
                                    globalThis.setTimeout(function () {
                                        db._versionchangeTransaction = null;
                                        if (upgradeTx._aborted) {
                                            request.error = {
                                                name: 'AbortError',
                                                message: 'The version change transaction was aborted'
                                            };
                                            request.readyState = 'done';
                                            _idbDispatch(request, 'error');
                                            return;
                                        }
                                        if (upgradeTx._active) {
                                            upgradeTx._active = false;
                                            _idbDispatch(upgradeTx, 'complete');
                                        }
                                        _idbVersions[dbName] = ver;
                                        request.transaction = null;
                                        __fenLog('warn', '[IDB] open onsuccess fired for "' + dbName + '" v' + ver + ' (stores: ' + db.objectStoreNames.join(',') + ')');
                                        _idbDispatch(request, 'success');
                                    }, 0);
                                } else {
                                    __fenLog('warn', '[IDB] open onsuccess fired for "' + dbName + '" v' + ver + ' (stores: ' + db.objectStoreNames.join(',') + ')');
                                    _idbDispatch(request, 'success');
                                }
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
                            delete _idbVersions[dbName];
                            if (typeof __fenIdbDeleteDatabase === 'function') {
                                try { __fenIdbDeleteDatabase(dbName); } catch(e) {}
                            }
                            var request = new IDBOpenDBRequest();
                            globalThis.setTimeout(function () {
                                request.readyState = 'done';
                                _idbDispatch(request, 'success');
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

                // â”€â”€ navigator.serviceWorker â”€â”€
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

                // â”€â”€ BroadcastChannel â”€â”€ https://html.spec.whatwg.org/#broadcasting-to-other-browsing-contexts
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

                // â”€â”€ CacheStorage (caches) â”€â”€ https://w3c.github.io/ServiceWorker/#cachestorage
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

                // â”€â”€ navigator.storage â”€â”€ https://storage.spec.whatwg.org/
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
            "globalThis.crossOriginIsolated = " + crossOriginIsolatedLiteral + ";" +
            "globalThis.window.crossOriginIsolated = globalThis.crossOriginIsolated;" +
            // Diagnostic error overlay â€” surfaces unhandled JS errors visibly on the page
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

    // Cross-origin isolation state for the current document. This gates
    // SharedArrayBuffer, Atomics.wait, and measureUserAgentSpecificMemory.
    // The state is captured from the most recent navigation's COOP/COEP
    // response headers; absent explicit headers the default is not isolated.
    private bool IsCrossOriginIsolatedContext(Uri baseUri)
    {
        if (DocumentSecurityContext != null)
        {
            return DocumentSecurityContext.Allows(SandboxFeature.CrossOriginIsolated);
        }

        // Fallback: local documents (file:, localhost) are treated as isolated
        // for development parity, matching the "potentially trustworthy origin"
        // carve-out that browsers apply to isSecureContext.
        return IsPotentiallyTrustworthyOrigin(baseUri);
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
    // disjoint â€” so deferred callbacks (every SPA bootstrap, incl. x.com) could not see
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
        var callbackProvenance = CaptureCallbackProvenance(callback);
        var callbackSessionGeneration = _fenJsSessionGeneration;
        var callbackDocumentId = _currentDocumentId;

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
        var registration = new FenJsTimerRegistration
        {
            Callback = callback,
            Arguments = extraArgs,
            WindowContext = callbackContext
        };
        registration.Timer = new Timer(
            _ =>
            {
                if (Interlocked.Exchange(ref registration.CallbackPending, 1) != 0)
                {
                    return;
                }

                try
                {
                    InvokeFenJsCallbackSafely(
                        callback,
                        extraArgs,
                        repeat ? "setInterval" : "setTimeout",
                        id,
                        callbackContext,
                        callbackProvenance,
                        callbackSessionGeneration,
                        callbackDocumentId,
                        () =>
                        {
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
                        });
                }
                finally
                {
                    Volatile.Write(ref registration.CallbackPending, 0);
                    if (!repeat && _fenJsTimers.TryRemove(id, out var completed))
                    {
                        completed.Dispose();
                    }
                }
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _fenJsTimers[id] = registration;
        registration.Timer.Change(Math.Max(0, delayMs), period);
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
        var callbackProvenance = CaptureCallbackProvenance(callback);
        var callbackSessionGeneration = _fenJsSessionGeneration;
        var callbackDocumentId = _currentDocumentId;
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
        var registration = new FenJsTimerRegistration
        {
            Callback = callback,
            WindowContext = callbackContext
        };
        registration.Timer = new Timer(
            _ =>
            {
                try
                {
                    var timestamp = JsValue.FromNumber(_fenJsClock.Elapsed.TotalMilliseconds);
                    InvokeFenJsCallbackSafely(
                        callback,
                        new[] { timestamp },
                        "requestAnimationFrame",
                        id,
                        callbackContext,
                        callbackProvenance,
                        callbackSessionGeneration,
                        callbackDocumentId,
                        () =>
                        {
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
                        });
                }
                finally
                {
                    if (_fenJsTimers.TryRemove(id, out var completed))
                    {
                        completed.Dispose();
                    }
                }
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _fenJsTimers[id] = registration;
        registration.Timer.Change(16, Timeout.Infinite);
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

    private CallbackSourceProvenance CaptureCallbackProvenance(JsValue callback)
    {
        if (callback.Tag == JsValueTag.Object)
        {
            try
            {
                if (_interpreter.Heap.GetObject(callback.AsObjectHandle()) is JsFunctionObject function &&
                    _callbackFunctionProvenance.TryGetValue(function.Function, out var functionProvenance))
                {
                    return functionProvenance with
                    {
                        CallbackFunctionName = function.Function.Name ?? functionProvenance.CallbackFunctionName
                    };
                }
            }
            catch
            {
                // Fall through to the currently executing script record.
            }
        }

        var script = GetCurrentScriptRecord();
        return new CallbackSourceProvenance(
            ScriptId: script?.ScriptId ?? string.Empty,
            ScriptUrl: ResolveMissingApiScriptUrl(script, null, _currentBaseUri),
            ScriptSourceLabel: script?.SourceLabel ?? string.Empty,
            SourceLine: script?.SourceLine ?? 0,
            SourceColumn: script?.SourceColumn ?? 0,
            CallbackFunctionName: GetCallbackFunctionName(callback));
    }

    private void RegisterCallbackFunctionProvenance(BytecodeFunction function, BrowserScriptLoadingRecord script)
    {
        if (function == null || script == null)
        {
            return;
        }

        var provenance = new CallbackSourceProvenance(
            ScriptId: script.ScriptId ?? string.Empty,
            ScriptUrl: ResolveMissingApiScriptUrl(script, null, _currentBaseUri),
            ScriptSourceLabel: script.SourceLabel ?? string.Empty,
            SourceLine: script.SourceLine,
            SourceColumn: script.SourceColumn,
            CallbackFunctionName: function.Name ?? string.Empty);
        RegisterCallbackFunctionProvenance(function, provenance);
    }

    private void RegisterCallbackFunctionProvenance(
        BytecodeFunction function,
        CallbackSourceProvenance provenance)
    {
        _callbackFunctionProvenance[function] = provenance with
        {
            CallbackFunctionName = function.Name ?? provenance.CallbackFunctionName
        };
        foreach (var nested in function.NestedFunctions ?? Array.Empty<BytecodeFunction>())
        {
            RegisterCallbackFunctionProvenance(nested, provenance);
        }
    }

    private string GetCallbackFunctionName(JsValue callback)
    {
        if (callback.Tag != JsValueTag.Object)
        {
            return string.Empty;
        }

        try
        {
            var callbackObject = _interpreter.Heap.GetObject(callback.AsObjectHandle());
            return callbackObject switch
            {
                JsFunctionObject function => function.Function.Name ?? string.Empty,
                NativeFunctionObject native => native.Name ?? string.Empty,
                _ when callbackObject.TryGetOwnProperty("name", out var descriptor) &&
                       descriptor.Value.Tag == JsValueTag.String => descriptor.Value.AsString(),
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private BrowserCallbackFailureRecord RecordCallbackFailure(
        JsValue callback,
        JsValue receiver,
        IReadOnlyList<JsValue> args,
        string origin,
        long callbackId,
        CallbackSourceProvenance provenance,
        Exception exception,
        string eventType = "")
    {
        const int maxFailureRecords = 128;
        var callbackCategory = origin ?? string.Empty;
        var functionName = provenance?.CallbackFunctionName ?? GetCallbackFunctionName(callback);
        var (receiverRepresentation, receiverHostType) = DescribeCallbackReceiver(receiver);
        var (exceptionType, exceptionMessage, jsStack) = DescribeCallbackException(exception, functionName);
        var hostStackSource = string.IsNullOrWhiteSpace(exception?.StackTrace)
            ? Environment.StackTrace
            : exception.StackTrace;
        var hostStack = TruncateDiagnosticText(RedactPotentialSecrets(hostStackSource), 8192);
        var taskPrefix = callbackCategory switch
        {
            "setTimeout" or "setInterval" => "timer",
            "requestAnimationFrame" => "raf",
            "event-listener" => "event",
            "promise-rejection" => "promise",
            "microtask" => "microtask",
            _ => "callback"
        };
        var isTimer = callbackCategory is "setTimeout" or "setInterval";
        var readyState = GetDocumentReadyState();
        var record = new BrowserCallbackFailureRecord
        {
            Sequence = Interlocked.Increment(ref _callbackFailureSequence),
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            NavigationId = _currentNavigationId ?? LogContext.CurrentCorrelationId ?? string.Empty,
            DocumentId = _currentDocumentId ?? string.Empty,
            RealmId = "realm-0",
            TaskId = taskPrefix + "-" + callbackId.ToString(CultureInfo.InvariantCulture),
            CallbackId = "callback-" + callbackId.ToString(CultureInfo.InvariantCulture),
            CallbackCategory = callbackCategory,
            TimerId = isTimer ? callbackId : null,
            EventType = TruncateDiagnosticText(eventType, 256),
            ScriptId = TruncateDiagnosticText(provenance?.ScriptId, 256),
            ScriptUrl = TruncateDiagnosticText(provenance?.ScriptUrl, 2048),
            ScriptSourceLabel = TruncateDiagnosticText(provenance?.ScriptSourceLabel, 2048),
            SourceLine = provenance?.SourceLine ?? 0,
            SourceColumn = provenance?.SourceColumn ?? 0,
            CallbackFunctionName = TruncateDiagnosticText(functionName, 256),
            ReceiverRepresentation = TruncateDiagnosticText(receiverRepresentation, 512),
            ReceiverHostType = TruncateDiagnosticText(receiverHostType, 256),
            ReceiverJsType = receiver.Tag.ToString(),
            ArgumentTypeSummary = TruncateDiagnosticText(
                args == null ? string.Empty : string.Join(",", args.Select(static value => value.Tag.ToString())),
                1024),
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            JsStack = jsStack,
            HostStack = hostStack,
            DocumentReadyState = readyState,
            LifecycleMilestone = GetLifecycleMilestone(readyState),
            BlockedProgress = false,
            RedactionStatus = "metadata-only; secret-like stack lines redacted"
        };

        UpdateEventLoopSnapshot(snapshot =>
        {
            snapshot.CallbackFailures++;
            snapshot.LastCallbackOrigin = callbackCategory;
            snapshot.LastError = exceptionType + ": " + exceptionMessage;
            snapshot.CallbackFailureRecords.Add(record);
            if (snapshot.CallbackFailureRecords.Count > maxFailureRecords)
            {
                snapshot.CallbackFailureRecords.RemoveRange(
                    0,
                    snapshot.CallbackFailureRecords.Count - maxFailureRecords);
            }
        });

        return record;
    }

    private BrowserCallbackFailureRecord RecordDiagnosticCallbackFailure(
        JsValue callback,
        JsValue receiver,
        IReadOnlyList<JsValue> args,
        string category,
        string eventType,
        Exception exception)
    {
        var callbackId = Interlocked.Increment(ref _diagnosticCallbackIdSequence);
        var failure = RecordCallbackFailure(
            callback,
            receiver,
            args,
            category,
            callbackId,
            CaptureCallbackProvenance(callback),
            exception,
            eventType);
        LogDiagnosticCallbackFailure(failure, callbackId, category, eventType);
        return failure;
    }

    private void LogDiagnosticCallbackFailure(
        BrowserCallbackFailureRecord failure,
        long callbackId,
        string category,
        string eventType)
    {
        AddEventLoopRecord("CallbackFailed", category + ":" + eventType, callbackId);
        LogEventLoop(
            "TaskFailed",
            LogSeverity.Warn,
            "[FenJsBridge] diagnostic callback failed",
            new Dictionary<string, object>
            {
                ["origin"] = category,
                ["eventType"] = eventType ?? string.Empty,
                ["id"] = callbackId,
                ["taskId"] = failure.TaskId,
                ["callbackId"] = failure.CallbackId,
                ["scriptId"] = failure.ScriptId,
                ["scriptSourceLabel"] = failure.ScriptSourceLabel,
                ["scriptSourceLine"] = failure.SourceLine,
                ["scriptSourceColumn"] = failure.SourceColumn,
                ["callbackFunctionName"] = failure.CallbackFunctionName,
                ["receiverJsType"] = failure.ReceiverJsType,
                ["receiverHostType"] = failure.ReceiverHostType,
                ["errorType"] = failure.ExceptionType,
                ["error"] = failure.ExceptionMessage,
                ["jsStack"] = failure.JsStack,
                ["hostStack"] = failure.HostStack,
                ["redactionStatus"] = failure.RedactionStatus
            },
            LogMarker.EngineBug);
    }

    private void TrackPromiseRejection(JsValue promise, PromiseRejectionOperation operation)
    {
        if (operation == PromiseRejectionOperation.Handle)
        {
            _pendingPromiseRejectionDiagnostics.Remove(promise);
            return;
        }

        const int maxPendingRejections = 128;
        if (_pendingPromiseRejectionDiagnostics.Count >= maxPendingRejections &&
            !_pendingPromiseRejectionDiagnostics.ContainsKey(promise))
        {
            return;
        }

        var reason = JsValue.Undefined;
        try
        {
            if (promise.Tag == JsValueTag.Object &&
                _interpreter.Heap.GetObject(promise.AsObjectHandle()) is PromiseInstance promiseInstance)
            {
                reason = promiseInstance.Promise.GetResultUnchecked();
            }
        }
        catch
        {
            // Preserve the rejection observation even if its reason is no longer resolvable.
        }

        _pendingPromiseRejectionDiagnostics[promise] = new PendingPromiseRejectionDiagnostic(
            reason,
            CaptureCallbackProvenance(JsValue.Undefined));
    }

    private void RecordPromiseRejection(JsValue promise)
    {
        var pending = _pendingPromiseRejectionDiagnostics.TryGetValue(promise, out var captured)
            ? captured
            : new PendingPromiseRejectionDiagnostic(
                JsValue.Undefined,
                CaptureCallbackProvenance(JsValue.Undefined));
        _pendingPromiseRejectionDiagnostics.Remove(promise);

        var exception = new JsThrownException(pending.Reason)
        {
            Description = DescribePromiseRejectionReason(pending.Reason)
        };
        var callbackId = Interlocked.Increment(ref _diagnosticCallbackIdSequence);
        var failure = RecordCallbackFailure(
            JsValue.Undefined,
            promise,
            new[] { pending.Reason },
            "promise-rejection",
            callbackId,
            pending.Provenance,
            exception,
            "reject");
        LogDiagnosticCallbackFailure(failure, callbackId, "promise-rejection", "reject");
    }

    private (string Representation, string HostType) DescribeCallbackReceiver(JsValue receiver)
    {
        if (receiver.Tag == JsValueTag.HostObject)
        {
            var hostType = TryResolveHostObject(receiver, out var hostObject)
                ? hostObject.GetType().Name
                : "unresolved";
            return ("[host:" + hostType + "]", hostType);
        }

        if (receiver.Tag == JsValueTag.Object)
        {
            try
            {
                return ("[object:" + _interpreter.Heap.GetObject(receiver.AsObjectHandle()).GetType().Name + "]", string.Empty);
            }
            catch
            {
                return ("[object:unresolved]", string.Empty);
            }
        }

        return ("[" + receiver.Tag + "]", string.Empty);
    }

    private (string Type, string Message, string JsStack) DescribeCallbackException(
        Exception exception,
        string callbackFunctionName)
    {
        var type = exception?.GetType().Name ?? "Exception";
        var message = exception?.Message ?? string.Empty;
        var jsStack = string.Empty;

        if (exception is JsThrownException jsThrown)
        {
            var description = string.Empty;
            try
            {
                description = _interpreter.DescribeThrownValue(jsThrown.Value) ?? string.Empty;
            }
            catch
            {
                description = jsThrown.Description ?? string.Empty;
            }

            var separator = description.IndexOf(':');
            if (separator > 0)
            {
                type = description.Substring(0, separator).Trim();
                message = description.Substring(separator + 1).Trim();
            }
            else if (!string.IsNullOrWhiteSpace(description))
            {
                message = description;
            }

            jsStack = string.IsNullOrWhiteSpace(message)
                ? type
                : type + ": " + message;
        }

        if (!string.IsNullOrWhiteSpace(callbackFunctionName) &&
            jsStack.IndexOf(callbackFunctionName, StringComparison.Ordinal) < 0)
        {
            jsStack = string.IsNullOrWhiteSpace(jsStack)
                ? "at " + callbackFunctionName + " [callback entry]"
                : jsStack + Environment.NewLine + "    at " + callbackFunctionName + " [callback entry]";
        }

        return (
            TruncateDiagnosticText(type, 256),
            TruncateDiagnosticText(RedactPotentialSecrets(message), 2048),
            TruncateDiagnosticText(RedactPotentialSecrets(jsStack), 8192));
    }

    private string GetLifecycleMilestone(string readyState)
    {
        lock (_eventLoopLock)
        {
            if (_lastEventLoopSnapshot?.LoadFired == true)
            {
                return "load-fired";
            }

            if (_lastEventLoopSnapshot?.DomContentLoadedFired == true)
            {
                return "dom-content-loaded";
            }
        }

        return string.Equals(readyState, "interactive", StringComparison.OrdinalIgnoreCase)
            ? "interactive"
            : "loading";
    }

    private static string RedactPotentialSecrets(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lines = value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("bearer ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lines[i] = "[redacted secret-like diagnostic line]";
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string TruncateDiagnosticText(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value.Substring(0, maxLength) + "...";
    }

    private sealed record CallbackSourceProvenance(
        string ScriptId,
        string ScriptUrl,
        string ScriptSourceLabel,
        int SourceLine,
        int SourceColumn,
        string CallbackFunctionName);

    private sealed record PendingPromiseRejectionDiagnostic(
        JsValue Reason,
        CallbackSourceProvenance Provenance);

    // Invoke a FenJS callback from a host timer thread, holding the interpreter lock so
    // it can never race with page-script evaluation, then drain microtasks and request
    // a repaint so DOM mutations made by the callback become visible.
    private void InvokeFenJsCallbackSafely(
        JsValue callback,
        IReadOnlyList<JsValue> args,
        string origin,
        long callbackId = 0,
        FenJsWindowCallbackContext windowContext = null,
        CallbackSourceProvenance callbackProvenance = null,
        int expectedSessionGeneration = -1,
        string expectedDocumentId = null,
        Action onValidated = null)
    {
        var callbackThis = windowContext?.WindowTarget ?? _fenJsGlobalThis;
        callbackProvenance ??= CaptureCallbackProvenance(callback);
        try
        {
            RunFenJsWithLargeStack<object>(() =>
            {
                lock (_fenJsLock)
                {
                    if ((expectedSessionGeneration >= 0 &&
                         expectedSessionGeneration != _fenJsSessionGeneration) ||
                        (expectedDocumentId != null &&
                         !string.Equals(expectedDocumentId, _currentDocumentId, StringComparison.Ordinal)))
                    {
                        return null;
                    }

                    onValidated?.Invoke();
                    Exception attributedMicrotaskFailure = null;
                    try
                    {
                        var heap = _interpreter.Heap;
                        var rootMark = heap.RootCount;
                        try
                        {
                            if (callback.Tag == JsValueTag.Object)
                            {
                                heap.PushRoot(callback.AsObjectHandle());
                            }
                            if (callbackThis.Tag == JsValueTag.Object)
                            {
                                heap.PushRoot(callbackThis.AsObjectHandle());
                            }
                            foreach (var arg in args ?? Array.Empty<JsValue>())
                            {
                                if (arg.Tag == JsValueTag.Object)
                                {
                                    heap.PushRoot(arg.AsObjectHandle());
                                }
                            }
                            CollectFenJsHeapAtSafeBoundary();
                        }
                        finally
                        {
                            heap.PopRootsTo(rootMark);
                        }

                        using var callbackWindowScope = windowContext != null
                            ? ActivateWindowCallbackContext(windowContext.WindowTarget, windowContext.WindowListeners)
                            : null;
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

                        _interpreter.PumpMicrotasks((microtaskCallback, exception) =>
                        {
                            RecordDiagnosticCallbackFailure(
                                microtaskCallback,
                                JsValue.Undefined,
                                Array.Empty<JsValue>(),
                                "microtask",
                                string.Empty,
                                exception);
                            attributedMicrotaskFailure = exception;
                        });
                        RecordMicrotaskCheckpoint(origin);
                    }
                    catch (Exception ex)
                    {
                        if (ReferenceEquals(ex, attributedMicrotaskFailure))
                        {
                            return null;
                        }

                        var failure = RecordCallbackFailure(
                            callback,
                            callbackThis,
                            args,
                            origin,
                            callbackId,
                            callbackProvenance,
                            ex);
                        AddEventLoopRecord("CallbackFailed", origin ?? string.Empty, callbackId);
                        LogEventLoop(
                            "TaskFailed",
                            LogSeverity.Warn,
                            "[FenJsBridge] event-loop callback task failed",
                            new Dictionary<string, object>
                            {
                                ["origin"] = origin ?? string.Empty,
                                ["id"] = callbackId,
                                ["taskId"] = failure.TaskId,
                                ["callbackId"] = failure.CallbackId,
                                ["scriptId"] = failure.ScriptId,
                                ["scriptSourceLabel"] = failure.ScriptSourceLabel,
                                ["scriptSourceLine"] = failure.SourceLine,
                                ["scriptSourceColumn"] = failure.SourceColumn,
                                ["callbackFunctionName"] = failure.CallbackFunctionName,
                                ["receiverJsType"] = failure.ReceiverJsType,
                                ["receiverHostType"] = failure.ReceiverHostType,
                                ["errorType"] = failure.ExceptionType,
                                ["error"] = failure.ExceptionMessage,
                                ["jsStack"] = failure.JsStack,
                                ["hostStack"] = failure.HostStack,
                                ["redactionStatus"] = failure.RedactionStatus
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
            }, waitForWorkerMs: -1, instructionBudget: FenJsBrowserTaskInstructionBudget);
        }
        catch (Exception ex)
        {
            var failure = RecordCallbackFailure(
                callback,
                callbackThis,
                args,
                origin,
                callbackId,
                callbackProvenance,
                ex);
            AddEventLoopRecord("CallbackCrashed", origin ?? string.Empty, callbackId);
            LogEventLoop(
                "TaskFailed",
                LogSeverity.Warn,
                "[FenJsBridge] event-loop callback task crashed",
                new Dictionary<string, object>
                {
                    ["origin"] = origin ?? string.Empty,
                    ["id"] = callbackId,
                    ["taskId"] = failure.TaskId,
                    ["callbackId"] = failure.CallbackId,
                    ["scriptId"] = failure.ScriptId,
                    ["scriptSourceLabel"] = failure.ScriptSourceLabel,
                    ["scriptSourceLine"] = failure.SourceLine,
                    ["scriptSourceColumn"] = failure.SourceColumn,
                    ["callbackFunctionName"] = failure.CallbackFunctionName,
                    ["receiverJsType"] = failure.ReceiverJsType,
                    ["receiverHostType"] = failure.ReceiverHostType,
                    ["errorType"] = failure.ExceptionType,
                    ["error"] = failure.ExceptionMessage,
                    ["jsStack"] = failure.JsStack,
                    ["hostStack"] = failure.HostStack,
                    ["redactionStatus"] = failure.RedactionStatus
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

                var documentPositionConstants = {
                    DOCUMENT_POSITION_DISCONNECTED: 0x01,
                    DOCUMENT_POSITION_PRECEDING: 0x02,
                    DOCUMENT_POSITION_FOLLOWING: 0x04,
                    DOCUMENT_POSITION_CONTAINS: 0x08,
                    DOCUMENT_POSITION_CONTAINED_BY: 0x10,
                    DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC: 0x20
                };
                for (var documentPositionName in documentPositionConstants) {
                    Object.defineProperty(Node, documentPositionName, {
                        value: documentPositionConstants[documentPositionName],
                        enumerable: true,
                        configurable: true
                    });
                    Object.defineProperty(Node.prototype, documentPositionName, {
                        value: documentPositionConstants[documentPositionName],
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

                function createCollectionIterator() {
                    var collection = this;
                    var index = 0;
                    var iterator = {
                        next: function () {
                            if (index >= Number(collection.length)) {
                                return { value: undefined, done: true };
                            }

                            return { value: collection[index++], done: false };
                        }
                    };
                    Object.defineProperty(iterator, Symbol.iterator, {
                        value: function () { return this; },
                        configurable: true
                    });
                    return iterator;
                }

                var NodeList = defineCtor('NodeList', null, ['NodeList'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.namedItem === 'undefined';
                });

                var HTMLCollection = defineCtor('HTMLCollection', null, ['HTMLCollection'], function (candidate) {
                    return typeof candidate.length !== 'undefined' &&
                        typeof candidate.item === 'function' &&
                        typeof candidate.namedItem === 'function';
                });

                Object.defineProperty(NodeList.prototype, Symbol.iterator, {
                    value: createCollectionIterator,
                    writable: true,
                    configurable: true
                });
                Object.defineProperty(HTMLCollection.prototype, Symbol.iterator, {
                    value: createCollectionIterator,
                    writable: true,
                    configurable: true
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
                    'compareDocumentPosition',
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
                    'getElementById',
                    'querySelector',
                    'querySelectorAll'
                ].forEach(function (name) { defineHostMethod(DocumentFragment.prototype, name); });

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
            // [[Call]] â€” not construct; throw TypeError per spec Â§ "MutationObserver()"
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

        // â”€â”€ TextEncoder / TextDecoder â”€â”€
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

        // â”€â”€ crypto.subtle â”€â”€
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

        // â”€â”€ WebSocket native bridge â”€â”€
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
                // â”€â”€ Blob â”€â”€ https://w3c.github.io/FileAPI/#blob-section
                // Facebook uses new Blob([data], {type: ...}) with sendBeacon.
                function blobPartSize(part) {
                    if (part instanceof globalThis.Blob) return part.size;
                    if (part instanceof ArrayBuffer) return part.byteLength;
                    if (part && typeof part.byteLength === 'number') return part.byteLength;
                    var encoded = encodeURIComponent(String(part));
                    var bytes = 0;
                    for (var i = 0; i < encoded.length; i++) {
                        if (encoded.charAt(i) === '%' && i + 2 < encoded.length) i += 2;
                        bytes++;
                    }
                    return bytes;
                }
                globalThis.Blob = function Blob(parts, options) {
                    this._parts = parts || [];
                    this._type = String((options && options.type) || '').toLowerCase();
                    this.size = 0;
                    for (var i = 0; i < this._parts.length; i++) {
                        this.size += blobPartSize(this._parts[i]);
                    }
                    this.type = this._type;
                };
                globalThis.Blob.prototype.slice = function (start, end, contentType) {
                    return new globalThis.Blob(this._parts.slice(start || 0, end), { type: contentType || this._type });
                };
                globalThis.Blob.prototype.text = function () {
                    return Promise.resolve(this._parts.join(''));
                };
                globalThis.Blob.prototype.arrayBuffer = function () {
                    return Promise.resolve(new ArrayBuffer(0));
                };
                // â”€â”€ trustedTypes â”€â”€ https://w3c.github.io/trusted-types/dist/spec/
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

                // â”€â”€ IntersectionObserver â”€â”€ https://w3c.github.io/IntersectionObserver/
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

                // â”€â”€ ResizeObserver â”€â”€ https://drafts.csswg.org/resize-observer/
                // Delivery is coalesced onto the timer queue so callbacks run after
                // the style mutation that changed the observed box.
                globalThis.__fenResizeObservers = [];
                globalThis.__fenNotifyResizeObservers = function (target) {
                    for (var i = 0; i < globalThis.__fenResizeObservers.length; i++) {
                        var observer = globalThis.__fenResizeObservers[i];
                        if (observer._targets.indexOf(target) < 0 || observer._scheduled) continue;
                        observer._scheduled = true;
                        (function (current) {
                            setTimeout(function () {
                                current._scheduled = false;
                                var entries = [];
                                for (var j = 0; j < current._targets.length; j++) {
                                    var observed = current._targets[j];
                                    var rect = observed.getBoundingClientRect();
                                    entries.push({
                                        target: observed,
                                        contentRect: rect,
                                        borderBoxSize: [{ inlineSize: rect.width, blockSize: rect.height }],
                                        contentBoxSize: [{ inlineSize: rect.width, blockSize: rect.height }],
                                        devicePixelContentBoxSize: [{ inlineSize: rect.width, blockSize: rect.height }]
                                    });
                                }
                                if (entries.length) current._callback(entries, current);
                            }, 0);
                        })(observer);
                    }
                };
                globalThis.ResizeObserver = function ResizeObserver(callback) {
                    this._callback = callback;
                    this._targets = [];
                    this._scheduled = false;
                    globalThis.__fenResizeObservers.push(this);
                };
                ResizeObserver.prototype.observe = function (target, options) {
                    if (this._targets.indexOf(target) < 0) {
                        this._targets.push(target);
                    }
                    globalThis.__fenNotifyResizeObservers(target);
                };
                ResizeObserver.prototype.unobserve = function (target) {
                    var idx = this._targets.indexOf(target);
                    if (idx >= 0) { this._targets.splice(idx, 1); }
                };
                ResizeObserver.prototype.disconnect = function () {
                    this._targets.length = 0;
                };

                // â”€â”€ Event â”€â”€ https://dom.spec.whatwg.org/#interface-event
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

                // â”€â”€ CustomEvent â”€â”€ https://dom.spec.whatwg.org/#interface-customevent
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
                        this._fenWindow = window;
                    }

                    MessagePort.prototype.postMessage = function (data, transfer) {
                        if (typeof __fenPostMessagePort === 'function' &&
                            __fenPostMessagePort(this, data, transfer || [])) {
                            return;
                        }
                        var target = this._fenPeer;
                        if (!target || target._fenClosed) return;
                        if (transfer && transfer.length) {
                            for (var transferredIndex = 0; transferredIndex < transfer.length; transferredIndex++) {
                                if (transfer[transferredIndex]) transfer[transferredIndex]._fenWindow = target._fenWindow;
                            }
                        }
                        var event = new MessageEvent('message', { data: data, source: null, ports: transfer || [] });
                        event.target = target;
                        event.currentTarget = target;
                        var deliver = function () {
                            if (target._fenClosed) return;
                            if (typeof target.onmessage === 'function') {
                                __fenDispatchMessagePort(target._fenWindow, target.onmessage, target, event);
                            }
                            var listeners = target._fenListeners.slice();
                            for (var i = 0; i < listeners.length; i++) {
                                __fenDispatchMessagePort(target._fenWindow, listeners[i], target, event);
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
                        if (typeof __fenCloseMessagePort === 'function') {
                            __fenCloseMessagePort(this);
                        }
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
                        if (typeof __fenRegisterMessageChannel === 'function') {
                            __fenRegisterMessageChannel(this.port1, this.port2);
                        }
                    };
                })();

                // â”€â”€ XMLHttpRequest â”€â”€ https://xhr.spec.whatwg.org/
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
                        var isBinaryBody = body != null && typeof ArrayBuffer !== 'undefined' &&
                            (body instanceof ArrayBuffer || (typeof ArrayBuffer.isView === 'function' && ArrayBuffer.isView(body)));
                        var requestBody = body === undefined ? '' : (isBinaryBody ? body : String(body));
                        var result = __fenSyncXhr(self._method || 'GET', self._url || '', requestBody, self._requestHeaders || {}, isBinaryBody);
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

                // â”€â”€ AbortSignal / AbortController â”€â”€ https://dom.spec.whatwg.org/#abortcontroller
                // GitHub uses fetch() with { signal: AbortSignal.timeout(...) }.
                globalThis.AbortSignal = function AbortSignal() {
                    EventTarget.call(this);
                    this.aborted = false;
                    this.reason = undefined;
                    this.onabort = null;
                };
                AbortSignal.prototype = Object.create(EventTarget.prototype);
                Object.defineProperty(AbortSignal.prototype, 'constructor', {
                    value: AbortSignal,
                    writable: true,
                    configurable: true
       ×Ý6çfòµë(š+myÖ&–Æ—G’‚“°¢òÒö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ‡&W6öÇfRÂæWuµÒ²&W6öÇWF–öâÒÂ§5fÇVRåVæFVf–æVB“°¢&WGW&â&öÖ—6S°¢Ð ¢&—fFR§5fÇVR7&VFU&V¦V7FVE&öÖ—6R‡7G&–ærÖW76vRÂ7G&–æræÖR¢°¢f"‡&öÖ—6RÂòÂ&V¦V7B’Ò‚„”'V–ÇF–ä6öçFW‡B•ö–çFW'&WFW"’ä7&VFU&öÖ—6T6&–Æ—G’‚“°¢f"W'&÷$ö&¢Òö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&ÖW76vR%ÒÒ§5fÇVRäg&öÕ7G&–ær†ÖW76vRóò7G&–æräV×G’’À¢²&æÖR%ÒÒ§5fÇVRäg&öÕ7G&–ær†æÖRóò$W'&÷""¢Ò“°¢òÒö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ‡&V¦V7BÂæWuµÒ²W'&÷$ö&¢ÒÂ§5fÇVRåVæFVf–æVB“°¢&WGW&â&öÖ—6S°¢Ð ¢&—fFR§5fÇVR7&VFUf–WuG&ç6—F–öå&W7VÇB„§5fÇVRWFFT6ÆÆ&6²¢°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR‡WFFT6ÆÆ&6²’¢°¢G'¢°¢òÒö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ‡WFFT6ÆÆ&6²Â'&’äV×G“Ä§5fÇVSâ‚’Â§5fÇVRåVæFVf–æVB“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒ7F'Ef–WuG&ç6—F–öâ6ÆÆ&6²f–ÆVC¢¶W‚äÖW76vWÒ"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ð ¢f"&W7VÇBÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'&VG’%ÒÒ7&VFU&W6öÇfVE&öÖ—6R„§5fÇVRåVæFVf–æVB’À¢²'WFFT6ÆÆ&6´FöæR%ÒÒ7&VFU&W6öÇfVE&öÖ—6R„§5fÇVRåVæFVf–æVB’À¢²&f–æ—6†VB%ÒÒ7&VFU&W6öÇfVE&öÖ—6R„§5fÇVRåVæFVf–æVB¢Ò“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&W7VÇBÀ¢'6¶—G&ç6—F–öâ"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚'6¶—G&ç6—F–öâ"Â…òÂò’Óâ§5fÇVRåVæFVf–æVBÂÆVæwFƒ¢’À¢VçVÖW&&ÆS¢fÇ6R“°¢&WGW&â&W7VÇC°¢Ð ¢&—fFR§5fÇVR'6TfVä§5W&Â„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2¢°¢–b†&w2ÓÒçVÆÂÇÀ¢&w2ä6÷VçBÓÒÇÀ¢&w5³ÒåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢f"–çWBÒ6öW&6UFô†÷7E7G&–ær†&w5³Ò“°¢W&’&6UW&’ÒçVÆÃ°¢–b†&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFråVæFVf–æVB¢°¢f"&6UFW‡BÒ6öW&6UFô†÷7E7G&–ær†&w5³Ò“°¢–b‚W&’åG'”7&VFR†&6UFW‡BÂW&”¶–æBä'6öÇWFRÂ÷WB&6UW&’’¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢Ð¢VÇ6P¢°¢f"Fö7VÖVçBÒö7W'&VçDFöÕ&ö÷B2Fö7VÖVçBóòö7W'&VçDFöÕ&ö÷Còä÷væW$Fö7VÖVçC°¢&6UW&’Òö7W'&VçD&6UW&’óòG'”7&VFUW&’†Fö7VÖVçCòåU$Â“°¢Ð ¢–b‚W&’åG'”7&VFR†–çWBÂW&”¶–æBä'6öÇWFRÂ÷WBf"W&’’¢°¢–b†&6UW&’ÓÒçVÆÂÇÂW&’åG'”7&VFR†&6UW&’Â–çWBÂ÷WBW&’’¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢Ð ¢–b‡W&’ÓÒçVÆÂÇÂW&’ä—4'6öÇWFUW&’¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢f"÷'BÒW&’ä—4FVfVÇE÷'Bò7G&–æräV×G’¢W&’å÷'BåFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢f"†÷7BÒ7G&–ærä—4çVÆÄ÷$V×G’‡÷'B’òW&’ä†÷7B¢B'·W&’ä†÷7GÓ§·÷'GÒ#°¢f"÷&–v–âÐ¢7G&–æräWVÇ2‡W&’å66†VÖRÂW&’åW&•66†VÖT‡GGÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡W&’å66†VÖRÂW&’åW&•66†VÖT‡GG2Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R¢òW&’ävWDÆVgE'B…W&•'F–ÂäWF†÷&—G’¢¢&çVÆÂ#° ¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&‡&Vb%ÒÒ§5fÇVRäg&öÕ7G&–ær‡W&’ä'6öÇWFUW&’’À¢²&÷&–v–â%ÒÒ§5fÇVRäg&öÕ7G&–ær†÷&–v–â’À¢²'&÷Fö6öÂ%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–ærä—4çVÆÄ÷$V×G’‡W&’å66†VÖR’ò7G&–æräV×G’¢W&’å66†VÖR²#¢"’À¢²&†÷7B%ÒÒ§5fÇVRäg&öÕ7G&–ær††÷7Bóò7G&–æräV×G’’À¢²&†÷7FæÖR%ÒÒ§5fÇVRäg&öÕ7G&–ær‡W&’ä†÷7Bóò7G&–æräV×G’’À¢²'÷'B%ÒÒ§5fÇVRäg&öÕ7G&–ær‡÷'B’À¢²'F†æÖR%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–ærä—4çVÆÄ÷$V×G’‡W&’ä'6öÇWFUF‚’ò"ò"¢W&’ä'6öÇWFUF‚’À¢²'6V&6‚%ÒÒ§5fÇVRäg&öÕ7G&–ær‡W&’åVW'’óò7G&–æräV×G’’À¢²&†6‚%ÒÒ§5fÇVRäg&öÕ7G&–ær‡W&’äg&vÖVçBóò7G&–æräV×G’¢Ò“°¢Ð ¢&—fFRfö–BÇ”VÆVÖVçE&÷W'F–W2„VÆVÖVçBVÆVÖVçBÂ§5fÇVR&÷W'F–W5fÇVR¢°¢–b†VÆVÖVçBÓÒçVÆÂÇÂ&÷W'F–W5fÇVRåFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢f"&÷W'F–W4ö&¦V7BÒö–çFW'&WFW"ä†VävWDö&¦V7B‡&÷W'F–W5fÇVRä4ö&¦V7D†æFÆR‚’“°¢f"6öçFW‡BÒ„”'V–ÇF–ä6öçFW‡B•ö–çFW'&WFW#°¢f÷&V6‚‡f"&÷W'G’–â&÷W'F–W4ö&¦V7BäVçVÖW&FT÷vå&÷W'F–W2‚’¢°¢–b‚&÷W'G’åfÇVRäVçVÖW&&ÆRÇÀ¢7G&–ærä—4çVÆÄ÷%v†—FU76R‡&÷W'G’ä¶W’’ÇÀ¢6öçFW‡BåG'”vWE&÷W'G•fÇVR‡&÷W'F–W4ö&¦V7BÂ&÷W'F–W5fÇVRÂ&÷W'G’ä¶W’Â÷WBf"&÷W'G•fÇVR’¢°¢6öçF–çVS°¢Ð ¢Ç”VÆVÖVçE&÷W'G’†VÆVÖVçBÂ&÷W'G’ä¶W’Â&÷W'G•fÇVR“°¢Ð¢Ð ¢&—fFRfö–BVWVTg&ÖTÆöG4f÷%G&VR„æöFRæöFR¢°¢–b†æöFR—2æ÷BVÆVÖVçBVÆVÖVçB¢°¢&WGW&ã°¢Ð ¢VWVTg&ÖTVÆVÖVçDÆöB†VÆVÖVçB“° ¢f÷&V6‚‡f"FW66VæFçB–âVÆVÖVçBäFW66VæFçG2‚’äöeG—SÄVÆVÖVçCâ‚’¢°¢VWVTg&ÖTVÆVÖVçDÆöB†FW66VæFçB“°¢Ð¢Ð ¢&—fFR7FF–2&ööÂ—4”g&ÖTVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–g&ÖR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR‡GG6öçFVçB7&VFU&WVW7D6öçFVçB„§5fÇVR&öG•fÇVRÂ&ööÂ—4&–æ'”&öG’¢°¢–b†—4&–æ'”&öG’¢°¢&WGW&âæWr'—FT'&”6öçFVçB„W‡G&7D'—FW4g&öÔ'&”Æ–¶R†&öG•fÇVR’“°¢Ð ¢&WGW&âæWr7G&–æt6öçFVçB„6öW&6UFô†÷7E7G&–ær†&öG•fÇVR’ÂVæ6öF–æråUDc‚Â'FW‡B÷Æ–â"“°¢Ð ¢&—fFR7FF–2&ööÂ—46†V6¶&ÆT–çWDVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢–b‚7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âfÇ6S°¢Ð ¢f"G—RÒVÆVÖVçBävWDGG&–'WFR‚'G—R"“°¢&WGW&â7G&–æräWVÇ2‡G—RÂ&6†V6¶&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡G—RÂ'&F–ò"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–2&ööÂ—46†V6¶&÷„–çWDVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’b`¢7G&–æräWVÇ2†VÆVÖVçBävWDGG&–'WFR‚'G—R"’Â&6†V6¶&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFRfö–BVWVTg&ÖTVÆVÖVçDÆöB„VÆVÖVçBVÆVÖVçB¢°¢–b†VÆVÖVçBÓÒçVÆÂÇÀ¢—4”g&ÖTVÆVÖVçB†VÆVÖVçB’ÇÀ¢VÆVÖVçBä—46öææV7FVBÇÀ¢g&ÖTVÆVÖVçDÆöFW"ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢òòâ6†V6²f÷"7&6Fö2‡F¶W2&–÷&—G’÷fW"7&2W"…DÔÂ7V2¢f"7&6Fö2ÒVÆVÖVçBävWDGG&–'WFR‚'7&6Fö2"“°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R‡7&6Fö2’¢°¢f"W†—7F–ætFö2ÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ%õöfVäg&ÖU7&6Fö4†6‚"“°¢f"æWt†6‚Ò7&6Fö2ävWD†6„6öFR‚’åFõ7G&–ær‚'‚"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢–b†W†—7F–ætFö2åFrÒ§5fÇVUFråVæFVf–æVBb`¢7G&–æräWVÇ2„6öW&6UFô†÷7E7G&–ær†W†—7F–ætFö2’ÂæWt†6‚Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&ã²òòÇ&VG’ÆöFVBF†—27&6Fö26öçFVç@¢Ð ¢6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ%õöfVäg&ÖU7&6Fö4†6‚"Â§5fÇVRäg&öÕ7G&–ær†æWt†6‚’“° ¢òÒF6²å'Vâ†7–æ2‚’Óà¢°¢G'¢°¢v—BÆöDg&ÖU7&6Fö47–æ2†VÆVÖVçBÂ7&6Fö2’ä6öæf–wW&Tv—B†fÇ6R“°¢&WVW7E&VæFW#òä–çfö¶R‚“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢Væv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒ7&6Fö2g&ÖRÆöBf–ÆVBf÷"Æ–g&ÖSã¢¶W‚äÖW76vWÒ"À¢Æöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ò“°¢&WGW&ã°¢Ð ¢òò"â6†V6²f÷"7&2U$À¢f"7&2Ò&W6öÇfTVÆVÖVçEW&Å&÷W'G’†VÆVÖVçBÂ'7&2"“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡7&2’ÇÀ¢7G&–æräWVÇ2‡7&2Â&&÷WC¦&Ææ²"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7&2å7F'G5v—F‚‚&¦f67&—C¢"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7&2å7F'G5v—F‚‚&FF¢"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢W&’åG'”7&VFR‡7&2ÂW&”¶–æBä'6öÇWFRÂ÷WBf"g&ÖUW&’’¢°¢&WGW&ã°¢Ð ¢f"W†—7F–ærÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ%õöfVäg&ÖTÆöEW&Â"“°¢–b†W†—7F–æråFrÒ§5fÇVUFråVæFVf–æVBb`¢7G&–æräWVÇ2„6öW&6UFô†÷7E7G&–ær†W†—7F–ær’Âg&ÖUW&’ä'6öÇWFUW&’Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&ã°¢Ð ¢6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ%õöfVäg&ÖTÆöEW&Â"Â§5fÇVRäg&öÕ7G&–ær†g&ÖUW&’ä'6öÇWFUW&’’“° ¢òÒF6²å'Vâ†7–æ2‚’Óà¢°¢G'¢°¢v—Bg&ÖTVÆVÖVçDÆöFW"†VÆVÖVçBÂg&ÖUW&’’ä6öæf–wW&Tv—B†fÇ6R“°¢&WVW7E&VæFW#òä–çfö¶R‚“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒ–g&ÖRÆöBf–ÆVBf÷"w¶g&ÖUW&—Òs¢¶W‚äÖW76vWÒ"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ò“°¢Ð ¢òòòÇ7VÖÖ'“à¢òòò'6W27&6Fö2…DÔÂ6öçFVçBæBÆöG2—B2F†R–g&ÖRw27V&Fö7VÖVçBà¢òòòF†R7&6Fö26öçFVçB—2G&VFVB2â…DÔÂFö7VÖVçBv—F‚F†R&VçBvRw0¢òòò&6RU$’‡W"…DÔÂ7V2*sBã‚ãR(	B7&6Fö2Fö7VÖVçG2†fRF†R&VçBw2U$À¢òòòf÷"6ÖRÖ÷&–v–âW'÷6W2’à¢òòòÂ÷7VÖÖ'“à¢&—fFR7–æ2F6²ÆöDg&ÖU7&6Fö47–æ2„VÆVÖVçBg&ÖTVÆVÖVçBÂ7G&–ær7&6Fö4‡FÖÂ¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡7&6Fö4‡FÖÂ’ÇÂg&ÖTVÆVÖVçBÓÒçVÆÂÇÂg&ÖTVÆVÖVçBä—46öææV7FVB¢°¢&WGW&ã°¢Ð ¢G'¢°¢òò7&6Fö2Fö7VÖVçG2–æ†W&—BF†R7&VF÷"Fö7VÖVçBw2&6RU$À¢f"g&ÖUW&’Òö7W'&VçD&6UW&’óòæWrW&’‚&&÷WC§7&6Fö2"“°¢f"'6VDFö7VÖVçBÒ‡FÖÅ'6W"å'6TFö7VÖVçB€¢7&6Fö4‡FÖÂÀ¢æWr‡FÖÅ'6W$÷F–öç2²&6UW&’Òg&ÖUW&’Ò“° ¢f"'6VE&ö÷BÒ'6VDFö7VÖVçCòäFö7VÖVçDVÆVÖVçC°¢–b‡'6VE&ö÷BÓÒçVÆÂ¢°¢Væv–æTÆöt6ö×Båv&â€¢%´fVä§4'&–FvUÒ7&6Fö2'6R&öGV6VBæòFö7VÖVçBVÆVÖVçB"À¢Æöt6FVv÷'’ä¦f67&—B“°¢&WGW&ã°¢Ð ¢v†–ÆR†g&ÖTVÆVÖVçBäf—'7D6†–ÆBÒçVÆÂ¢°¢g&ÖTVÆVÖVçBå&VÖ÷fT6†–ÆB†g&ÖTVÆVÖVçBäf—'7D6†–ÆB“°¢Ð ¢g&ÖTVÆVÖVçBäVæD6†–ÆB‡'6VDFö7VÖVçB“° ¢òòv—&RWF†Rg&ÖRw2÷vâDôÒ6öçFW‡BæB67&—G2à¢òòF†R7V&Fö7VÖVçBvWG2—G2÷vâ¥2v–æF÷r&÷VæBFòF†Rg&ÖRVÆVÖVçBà¢vWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r†g&ÖTVÆVÖVçBÂ'6VDFö7VÖVçBÂg&ÖUW&’“°¢v—B6WE7V&Fö7VÖVçDFöÔ7–æ2‡'6VE&ö÷BÂg&ÖUW&’’ä6öæf–wW&Tv—B†fÇ6R“° ¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Bä–æfò€¢B%´fVä§4'&–FvUÒ7&6Fö2g&ÖRÆöFVB&ö÷CÒw·'6VE&ö÷BåFtæÖWÒr"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒ7&6Fö2g&ÖRÆöBf–ÆVC¢¶W‚äÖW76vWÒ"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ð ¢&—fFRfö–BÇ”VÆVÖVçE&÷W'G’„VÆVÖVçBVÆVÖVçBÂ7G&–ær&÷W'G’Â§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&6Æ74æÖR# ¢VÆVÖVçBä6Æ74æÖRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢'&V³°¢66R&–B# ¢VÆVÖVçBä–BÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢'&V³°¢66R'fÇVR# ¢VÆVÖVçBå6WDGG&–'WFR‚'fÇVR"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢–b‡7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ'FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢VÆVÖVçBåFW‡D6öçFVçBÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢Ð¢'&V³°¢66R&6†V6¶VB"v†Vâ—46†V6¶&ÆT–çWDVÆVÖVçB†VÆVÖVçB“ ¢VÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rå6WD6†V6¶VB†VÆVÖVçBÂ6öW&6UFô†÷7D&ööÆVâ‡fÇVR’“°¢'&V³°¢66R'G—R"v†Vâ7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“ ¢VÆVÖVçBå6WDGG&–'WFR‚'G—R"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢'&V³°¢66R'F$–æFW‚# ¢VÆVÖVçBå6WDGG&–'WFR€¢'F&–æFW‚"À¢‚†–çB”6öW&6UFôf–æ—FTçVÖ&W"‡fÇVRÂ’’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢'&V³°¢66R'v–GF‚"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢66R&†V–v‡B"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢VÆVÖVçBå6WDGG&–'WFR‡&÷W'G’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢æ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢'&V³°¢66R'7&2# ¢66R'7&6Fö2# ¢66R&‡&Vb# ¢66R&æöæ6R# ¢VÆVÖVçBå6WDGG&–'WFR‡&÷W'G’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢–b‡7G&–æräWVÇ2‡&÷W'G’Â'7&2"Â7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢—4”g&ÖTVÆVÖVçB†VÆVÖVçB’¢°¢VWVTg&ÖTVÆVÖVçDÆöB†VÆVÖVçB“°¢Ð¢–b‡7G&–æräWVÇ2‡&÷W'G’Â'7&6Fö2"Â7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢—4”g&ÖTVÆVÖVçB†VÆVÖVçB’¢°¢VWVTg&ÖTVÆVÖVçDÆöB†VÆVÖVçB“°¢Ð¢'&V³°¢66R&–ææW$…DÔÂ# ¢VÆVÖVçBä–ææW$…DÔÂÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢–b„W†V7WFT–æÆ–æU67&—G4öä–ææW$…DÔÂ¢°¢W†V7WFT–æÆ–æU67&—G4g&öÔVÆVÖVçB†VÆVÖVçB“°¢Ð¢'&V³°¢66R'FW‡D6öçFVçB# ¢VÆVÖVçBåFW‡D6öçFVçBÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢'&V³°¢FVfVÇC ¢6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ&÷W'G’ÂfÇVR“°¢'&V³°¢Ð¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFTFöÕFö¶VäÆ—7Ef–Wr„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢f"7F÷&RÒvWD†÷7E&÷W'G•7F÷&R‡Fö¶VäÆ—7B“°¢–b‡7F÷&RåG'”vWEfÇVR‚%õöfVäFöÕFö¶VäÆ—7Ef–Wr"Â÷WBf"66†VB’¢°¢&Vg&W6„FöÕFö¶Vä–æF–6W2†66†VBÂFö¶VäÆ—7B“°¢&WGW&â66†VC°¢Ð ¢òò7&VFRF†R&6Rf–Wrö&¦V7Bv—F‚ÖWF†öG2F†BFöâwBæVVB6VÆb×&VfW&Væ6Rà¢f"f–WrÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&—FVÒ%ÒÒ7&VFTFöÕFö¶VäÆ—7D—FVÔÖWF†öB‡Fö¶VäÆ—7B’À¢²&6öçF–ç2%ÒÒ7&VFTFöÕFö¶VäÆ—7D6öçF–ç4ÖWF†öB‡Fö¶VäÆ—7B’À¢²'7W÷'G2%ÒÒ7&VFTFöÕFö¶VäÆ—7E7W÷'G4ÖWF†öB‡Fö¶VäÆ—7B’À¢²'fÇVW2%ÒÒvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'fÇVW2"À¢…òÂó"’Óâ'V–ÆDFöÕFö¶Vä'&•fÇVR‡Fö¶VäÆ—7B’ÂÆVæwFƒ¢’À¢²&¶W—2%ÒÒvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&¶W—2"À¢…òÂó"’Óâ'V–ÆDFöÔ–æFW„'&’‡Fö¶VäÆ—7B’ÂÆVæwFƒ¢’À¢²&VçG&–W2%ÒÒvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&VçG&–W2"À¢…òÂó"’Óâ'V–ÆDFöÔVçG'”'&’‡Fö¶VäÆ—7B’ÂÆVæwFƒ¢’À¢²&f÷$V6‚%ÒÒvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&f÷$V6‚"À¢…òÂó"’Óâ§5fÇVRåVæFVf–æVBÂÆVæwFƒ¢’À¢²'Fõ7G&–ær%ÒÒvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'Fõ7G&–ær"À¢…òÂó"’Óâ§5fÇVRäg&öÕ7G&–ær‡Fö¶VäÆ—7BåfÇVRóò7G&–æräV×G’’ÂÆVæwFƒ¢’À¢Ò“° ¢òòæ÷rF†Bf–Wr—2FV6Æ&VBÂFBF†R×WFF–ærÖWF†öG2F†BæVVB6VÆb×&VfW&Væ6Rà¢f"f–Wtö&¢Òö–çFW'&WFW"ä†VävWDö&¦V7B‡f–Wrä4ö&¦V7D†æFÆR‚’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’‚&FB"ÂæWr§5&÷W'G”FW67&—F÷"€¢7&VFTFöÕFö¶VäÆ—7DFDÖWF†öB‡Fö¶VäÆ—7BÂf–Wr’Âw&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’‚'&VÖ÷fR"ÂæWr§5&÷W'G”FW67&—F÷"€¢7&VFTFöÕFö¶VäÆ—7E&VÖ÷fTÖWF†öB‡Fö¶VäÆ—7BÂf–Wr’Âw&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’‚'FövvÆR"ÂæWr§5&÷W'G”FW67&—F÷"€¢7&VFTFöÕFö¶VäÆ—7EFövvÆTÖWF†öB‡Fö¶VäÆ—7BÂf–Wr’Âw&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’‚'&WÆ6R"ÂæWr§5&÷W'G”FW67&—F÷"€¢7&VFTFöÕFö¶VäÆ—7E&WÆ6TÖWF†öB‡Fö¶VäÆ—7BÂf–Wr’Âw&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’€¢'fÇVR"À¢§5&÷W'G”FW67&—F÷"ä66W76÷"€¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&vWBfÇVR"À¢…òÂò’Óâ§5fÇVRäg&öÕ7G&–ær‡Fö¶VäÆ—7BåfÇVRóò7G&–æräV×G’’À¢ÆVæwFƒ¢’À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'6WBfÇVR"À¢…òÂ&w2’Óà¢°¢Fö¶VäÆ—7BåfÇVRÒ&w2ä6÷VçBâ ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢7G&–æräV×G“°¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’À¢VçVÖW&&ÆS¢G'VRÀ¢6öæf–wW&&ÆS¢G'VR’“° ¢òò6WBÆVæwF‚æBçVÖW&–2–æF–6W22&Vg&W6†VBFF&÷W'F–W2âfÇVV ¢òò&VÖ–ç2Æ—fR66W76÷"6òw&—FW2WFFRF†R76ö6–FVBDôÒGG&–'WFRà¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“° ¢òò–ç7FÆÂ7–Ö&öÂæ—FW&F÷"à¢f"—FW%7–Ô–BÒö–çFW'&WFW"ävWEvVÆÄ¶æ÷vå7–Ö&öÄ–B‚&—FW&F÷""“°¢–b†—FW%7–Ô–BÒ¢°¢f"—FW$fâÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢%µ7–Ö&öÂæ—FW&F÷%Ò"À¢‡F†—5fÇVRÂó"’Óà¢°¢f"ÆVâÒFö¶VäÆ—7BäÆVæwFƒ°¢f"—FV×2ÒæWrfVä'&÷w6W"ä§2å'VçF–ÖRä§5fÇVU¶ÆVåÓ°¢f÷"†–çB’Ò²’ÂÆVã²’²²¢°¢f"Fö¶VâÒFö¶VäÆ—7Bä—FVÒ†’“°¢—FV×5¶•ÒÒfVä'&÷w6W"ä§2å'VçF–ÖRä§5fÇVRäg&öÕ7G&–ær‡Fö¶Vâóò7G&–æräV×G’“°¢Ð¢f"–G‚Ò°¢f"—FW&F÷$VçG&–W2ÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&æW‡B%ÒÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&æW‡B"À¢…òÂó2’Óà¢°¢–b†–G‚ãÒÆVâ¢°¢f"FöæU&W7VÇBÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'fÇVR%ÒÒ§5fÇVRåVæFVf–æVBÀ¢²&FöæR%ÒÒ§5fÇVRäg&öÔ&ööÆVâ‡G'VR¢Ó°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†FöæU&W7VÇB“°¢Ð¢f"&W7VÇDF–7BÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'fÇVR%ÒÒ—FV×5¶–G‚²µÒÀ¢²&FöæR%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R¢Ó°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B‡&W7VÇDF–7B“°¢ÒÂÆVæwFƒ¢¢Ó°¢f"—FW$ö&¢Òö–çFW'&WFW"äÆÆö6FTö&¦V7B†—FW&F÷$VçG&–W2“°¢f"—FW$ö&¤†æFÆRÒ—FW$ö&¢ä4ö&¦V7D†æFÆR‚“°¢f"—FW$ö&¤æF—fRÒö–çFW'&WFW"ä†VävWDö&¦V7B†—FW$ö&¤†æFÆR“°¢—FW$ö&¤æF—fRäFVf–æT÷vå7–Ö&öÅ&÷W'G’†—FW%7–Ô–BÀ¢æWrfVä'&÷w6W"ä§2äö&¦V7G2ä§5&÷W'G”FW67&—F÷"€¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚%µ7–Ö&öÂæ—FW&F÷%Ò"À¢‡6VÆbÂó2’Óâ6VÆbÂÆVæwFƒ¢’À¢w&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢&WGW&â—FW$ö&£°¢ÒÀ¢ÆVæwFƒ¢“°¢f–Wtö&¢äFVf–æT÷vå7–Ö&öÅ&÷W'G’†—FW%7–Ô–BÀ¢æWrfVä'&÷w6W"ä§2äö&¦V7G2ä§5&÷W'G”FW67&—F÷"€¢—FW$fâÂw&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢Ð ¢GF6„fVä§5&÷F÷G—R‡f–WrÂ$DôÕFö¶VäÆ—7B"“°¢7F÷&U²%õöfVäFöÕFö¶VäÆ—7Ef–Wr%ÒÒf–Ws°¢&WGW&âf–Ws°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7D—FVÔÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&—FVÒ"À¢…òÂ&w2’Óà¢°¢f"–æFW‚Ò&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD–æFW‚¢ò'6VD–æFW‚¢Ó°¢f"—FVÒÒFö¶VäÆ—7Bä—FVÒ†–æFW‚“°¢&WGW&â—FVÒÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†—FVÒ“°¢ÒÂÆVæwFƒ¢“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7D6öçF–ç4ÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&6öçF–ç2"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7Bä6öçF–ç2‡Fö¶Vâ’“°¢ÒÂÆVæwFƒ¢“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7E7W÷'G4ÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'7W÷'G2"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7Bå7W÷'G2‡Fö¶Vâ’“°¢ÒÂÆVæwFƒ¢“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7DFDÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7BÂ§5fÇVRf–Wr¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ&FB"À¢…òÂ&w2’Óà¢°¢Fö¶VäÆ—7BäFB†&w2å6VÆV7B„6öW&6UFô†÷7E7G&–ær’åFô'&’‚’“°¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7E&VÖ÷fTÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7BÂ§5fÇVRf–Wr¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'&VÖ÷fR"À¢…òÂ&w2’Óà¢°¢Fö¶VäÆ—7Bå&VÖ÷fR†&w2å6VÆV7B„6öW&6UFô†÷7E7G&–ær’åFô'&’‚’“°¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7EFövvÆTÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7BÂ§5fÇVRf–Wr¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'FövvÆR"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&ööÃòf÷&6RÒçVÆÃ°¢–b†&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFråVæFVf–æVB¢f÷&6RÒ6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢f"&W7VÇBÒFö¶VäÆ—7BåFövvÆR‡Fö¶VâÂf÷&6R“°¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡&W7VÇB“°¢ÒÂÆVæwFƒ¢“°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕFö¶VäÆ—7E&WÆ6TÖWF†öB„DôÕFö¶VäÆ—7BFö¶VäÆ—7BÂ§5fÇVRf–Wr¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR‡Fö¶VäÆ—7BÂ'&WÆ6R"À¢…òÂ&w2’Óà¢°¢f"öÆEFö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"æWuFö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"&W7VÇBÒFö¶VäÆ—7Bå&WÆ6R†öÆEFö¶VâÂæWuFö¶Vâ“°¢&Vg&W6„FöÕFö¶Vä–æF–6W2‡f–WrÂFö¶VäÆ—7B“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡&W7VÇB“°¢ÒÂÆVæwFƒ¢"“°¢Ð ¢&—fFRfö–B&Vg&W6„FöÕFö¶Vä–æF–6W2„§5fÇVRf–WrÂDôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢f"f–Wtö&¢Òö–çFW'&WFW"ä†VävWDö&¦V7B‡f–Wrä4ö&¦V7D†æFÆR‚’“°¢òòWFFRF†RFW&—fVBÆVæwF‚æBçVÖW&–2–æF–6W2âF†RÆ—fRfÇVV ¢òò66W76÷"–ç7FÆÆVBBf–Wr7&VF–öâ÷vç276ö6–FVBÖGG&–'WFRw&—FW2à¢f–Wtö&¢äFVf–æT÷vå&÷W'G’‚&ÆVæwF‚"À¢æWr§5&÷W'G”FW67&—F÷"€¢§5fÇVRäg&öÔ–çC3"‡Fö¶VäÆ—7BäÆVæwF‚’À¢w&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢fÇ6RÂ6öæf–wW&&ÆS¢G'VR’“°¢òò6WBg&W6‚çVÖW&–2–æF–6W2g&öÒF†RÆ—fRFö¶VâÆ—7Bà¢f"Ö„öÆBÒ°¢f÷&V6‚‡f"·b–âf–Wtö&¢äVçVÖW&FT÷vå&÷W'F–W2‚’¢°¢–b†·bä¶W’äÆVæwF‚âbb·bä¶W•³ÒãÒsrbb·bä¶W•³ÒÃÒs’rb`¢–çBåG'•'6R†·bä¶W’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"â’¢°¢Ö„öÆBÒÖF‚äÖ‚†Ö„öÆBÂâ²“°¢Ð¢Ð¢òò6ÆV"7FÆR–æF–6W2&W–öæBF†R7W'&VçBÆVæwF‚à¢f÷"‡f"’ÒFö¶VäÆ—7BäÆVæwFƒ²’ÂÖ„öÆC²’²²¢°¢f–Wtö&¢äFVÆWFU&÷W'G’†’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢Ð¢òò6WB÷WFFR–æF–6W2f÷"7W'&VçBFö¶Vç2à¢f÷"‡f"’Ò²’ÂFö¶VäÆ—7BäÆVæwFƒ²’²²¢°¢f"—FVÒÒFö¶VäÆ—7Bä—FVÒ†’“°¢f–Wtö&¢äFVf–æT÷vå&÷W'G’€¢’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢æWr§5&÷W'G”FW67&—F÷"€¢§5fÇVRäg&öÕ7G&–ær†—FVÒóò7G&–æräV×G’’À¢w&—F&ÆS¢G'VRÂVçVÖW&&ÆS¢G'VRÂ6öæf–wW&&ÆS¢G'VR’“°¢Ð¢Ð ¢òò7&VFW2¥2'&’ÖÆ–¶Rö&¦V7Bg&öÒF†RFö¶Vç2à¢&—fFR§5fÇVR'V–ÆDFöÕFö¶Vä'&•fÇVR„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢f"VçG&–W2ÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‡Fö¶VäÆ—7BäÆVæwF‚²¢°¢²&ÆVæwF‚%ÒÒ§5fÇVRäg&öÔ–çC3"‡Fö¶VäÆ—7BäÆVæwF‚¢Ó°¢f÷"†–çB’Ò²’ÂFö¶VäÆ—7BäÆVæwFƒ²’²²¢°¢f"Fö¶VâÒFö¶VäÆ—7Bä—FVÒ†’“°¢VçG&–W5¶’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R•ÒÐ¢§5fÇVRäg&öÕ7G&–ær‡Fö¶Vâóò7G&–æräV×G’“°¢Ð¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†VçG&–W2“°¢Ð ¢&—fFR§5fÇVR'V–ÆDFöÔ–æFW„'&’„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢f"ÆVâÒFö¶VäÆ—7BäÆVæwFƒ°¢f"VçG&–W2ÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ†ÆVâ²¢°¢²&ÆVæwF‚%ÒÒ§5fÇVRäg&öÔ–çC3"†ÆVâ¢Ó°¢f÷"†–çB²Ò²²ÂÆVã²²²²¢VçG&–W5¶²åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R•ÒÒ§5fÇVRäg&öÔ–çC3"†²“°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†VçG&–W2“°¢Ð ¢&—fFR§5fÇVR'V–ÆDFöÔVçG'”'&’„DôÕFö¶VäÆ—7BFö¶VäÆ—7B¢°¢f"ÆVâÒFö¶VäÆ—7BäÆVæwFƒ°¢f"VçG&–W2ÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ†ÆVâ²¢°¢²&ÆVæwF‚%ÒÒ§5fÇVRäg&öÔ–çC3"†ÆVâ¢Ó°¢f÷"†–çB²Ò²²ÂÆVã²²²²¢°¢f"Fö¶VâÒFö¶VäÆ—7Bä—FVÒ†²“°¢f"—$VçG&–W2ÒæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâƒ2¢°¢²&ÆVæwF‚%ÒÒ§5fÇVRäg&öÔ–çC3"ƒ"’À¢²#%ÒÒ§5fÇVRäg&öÔ–çC3"†²’À¢²#%ÒÒ§5fÇVRäg&öÕ7G&–ær‡Fö¶Vâóò7G&–æräV×G’¢Ó°¢VçG&–W5¶²åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R•ÒÐ¢ö–çFW'&WFW"äÆÆö6FTö&¦V7B‡—$VçG&–W2“°¢Ð¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†VçG&–W2“°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFUFV×ÆFT6öçFVçB„VÆVÖVçBFV×ÆFR¢°¢f"66†VBÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB‡FV×ÆFRÂ%õöfVåFV×ÆFT6öçFVçB"“°¢–b†66†VBåFrÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&â66†VC°¢Ð ¢f"g&vÖVçBÒFV×ÆFRä÷væW$Fö7VÖVçCòä7&VFTFö7VÖVçDg&vÖVçB‚’óòæWrFö7VÖVçDg&vÖVçB‡FV×ÆFRä÷væW$Fö7VÖVçB“°¢v†–ÆR‡FV×ÆFRäf—'7D6†–ÆBÒçVÆÂ¢°¢g&vÖVçBäVæD6†–ÆB‡FV×ÆFRäf—'7D6†–ÆB“°¢Ð ¢f"fÇVRÒFô†÷7DæöFT÷$çVÆÂ†g&vÖVçB“°¢6WE7F÷&VD†÷7E&÷W'G’‡FV×ÆFRÂ%õöfVåFV×ÆFT6öçFVçB"ÂfÇVR“°¢&WGW&âfÇVS°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFT”g&ÖT6öçFVçDFö7VÖVçB„VÆVÖVçB–g&ÖR¢°¢f÷&V6‚‡f"6†–ÆB–â–g&ÖRä6†–ÆDæöFW2¢°¢–b†6†–ÆB—2Fö7VÖVçBg&ÖTFö7VÖVçB¢°¢f"g&ÖTFö7VÖVçEfÇVRÒFô†÷7D÷$çVÆÂ†g&ÖTFö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢6WE7F÷&VD†÷7E&÷W'G’†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçDFö7VÖVçB"Âg&ÖTFö7VÖVçEfÇVR“°¢&WGW&âg&ÖTFö7VÖVçEfÇVS°¢Ð ¢–b†6†–ÆB—2VÆVÖVçBg&ÖU&ö÷Bbbg&ÖU&ö÷Bä÷væW$Fö7VÖVçBÒçVÆÂ¢°¢f"g&ÖT÷væW$Fö7VÖVçEfÇVRÒFô†÷7D÷$çVÆÂ†g&ÖU&ö÷Bä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢6WE7F÷&VD†÷7E&÷W'G’†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçDFö7VÖVçB"Âg&ÖT÷væW$Fö7VÖVçEfÇVR“°¢&WGW&âg&ÖT÷væW$Fö7VÖVçEfÇVS°¢Ð¢Ð ¢f"66†VBÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçDFö7VÖVçB"“°¢–b†66†VBåFrÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&â66†VC°¢Ð ¢f"Fö7VÖVçBÒFö7VÖVçBä7&VFT‡FÖÄFö7VÖVçB‚“°¢f"W&ÂÒ&W6öÇfTVÆVÖVçEW&Å&÷W'G’†–g&ÖRÂ'7&2"“°¢Fö7VÖVçBåU$ÂÒ7G&–ærä—4çVÆÄ÷%v†—FU76R‡W&Â’ò&&÷WC¦&Ææ²"¢W&Ã°¢Fö7VÖVçBä&6UU$’ÒFö7VÖVçBåU$Ã° ¢–b†Fö7VÖVçBå&VçDæöFRÓÒçVÆÂ¢°¢–g&ÖRäVæD6†–ÆB†Fö7VÖVçB“°¢Ð ¢f"fÇVRÒFô†÷7D÷$çVÆÂ†Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢6WE7F÷&VD†÷7E&÷W'G’†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçDFö7VÖVçB"ÂfÇVR“°¢&WGW&âfÇVS°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r„VÆVÖVçB–g&ÖR¢°¢&WGW&âvWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r†–g&ÖRÂçVÆÂÂçVÆÂ“°¢Ð ¢&—fFR§5fÇVRvWD”g&ÖT6öçFVçEv–æF÷tf÷$7W'&VçD6öçFW‡B„VÆVÖVçB–g&ÖR¢°¢f"v–æF÷rÒvWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r†–g&ÖR“°¢–b„6ä7W'&VçD6öçFW‡D66W74”g&ÖTFö7VÖVçB†–g&ÖR’¢°¢&WGW&âv–æF÷s°¢Ð ¢&WGW&âvWD÷$7&VFT7&÷74÷&–v–ä6†–ÆEv–æF÷tf6FR†–g&ÖRÂv–æF÷r“°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r„VÆVÖVçB–g&ÖRÂFö7VÖVçBg&ÖTFö7VÖVçBÂW&’g&ÖUW&’¢°¢f"66†VBÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçEv–æF÷r"“°¢–b†66†VBåFrÒ§5fÇVUFråVæFVf–æVB¢°¢WFFT”g&ÖT6öçFVçEv–æF÷r†66†VBÂ–g&ÖRÂg&ÖTFö7VÖVçBÂg&ÖUW&’“°¢&WGW&â66†VC°¢Ð ¢f"Fö7VÖVçBÒg&ÖTFö7VÖVçBÓÒçVÆÀ¢òvWD÷$7&VFT”g&ÖT6öçFVçDFö7VÖVçB†–g&ÖR¢¢Fô†÷7D÷$çVÆÂ†g&ÖTFö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢f"‡&VbÒ&W6öÇfT”g&ÖUv–æF÷t‡&Vb†–g&ÖRÂg&ÖTFö7VÖVçBÂg&ÖUW&’“° ¢f"g&ÖUv–æF÷tÆ—7FVæW'2ÒvWD”g&ÖUv–æF÷tÆ—7FVæW'2†–g&ÖR“°¢f"v–æF÷rÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢f"Æö6F–öâÒ7&VFUÆ–äÆö6F–öäö&¦V7B†‡&Vb“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&Fö7VÖVçB"ÂFö7VÖVçB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&Æö6F–öâ"ÂÆö6F–öâ“°¢f"&VçD6ä66W74g&ÖRÒ—56ÖT÷&–v–äg&ÖT66W72†–g&ÖRÂ‡&VbÂvWE&VçDFö7VÖVçEW&’†–g&ÖR’“°¢f"g&ÖTVÆVÖVçEfÇVRÒFô†÷7D÷$çVÆÂ†–g&ÖRÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ%õöfVäg&ÖTVÆVÖVçB"Âg&ÖTVÆVÖVçEfÇVR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&g&ÖTVÆVÖVçB"Â&VçD6ä66W74g&ÖRòg&ÖTVÆVÖVçEfÇVR¢§5fÇVRäçVÆÂ“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'v–æF÷r"Âv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'6VÆb"Âv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&g&ÖW2"Âv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"ƒ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&6Æ÷6VB"Â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’“°¢6WD”g&ÖUf–Ww÷'E&÷W'F–W2‡v–æF÷rÂ–g&ÖR“°¢f"g&ÖTæÖRÒ–g&ÖRävWDGG&–'WFR‚&æÖR"’óò7G&–æräV×G“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&æÖR"Â§5fÇVRäg&öÕ7G&–ær†g&ÖTæÖR’“° ¢f"&VçBÒöfVä§4vÆö&ÅF†—2åFrÓÒ§5fÇVUFråVæFVf–æV@¢òv–æF÷p¢¢&VçD6ä66W74g&ÖP¢òvWD÷$7&VFU6ÖT÷&–v–åF÷v–æF÷tf6FR‚¢¢vWD÷$7&VFUF÷v–æF÷tf6FR‚“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'&VçB"Â&VçB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'F÷"Â&VçB“°¢f"W‡÷6VEv–æF÷rÒ&VçD6ä66W74g&ÖP¢òv–æF÷p¢¢vWD÷$7&VFT7&÷74÷&–v–ä6†–ÆEv–æF÷tf6FR†–g&ÖRÂv–æF÷r“°¢&Vv—7FW$6†–ÆDg&ÖTöå&VçB‡&VçBÂW‡÷6VEv–æF÷rÂg&ÖTæÖR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢v–æF÷rÀ¢'÷7DÖW76vR"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'÷7DÖW76vR"À¢…òÂ&w2’Óà¢°¢f"FFÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢f"F&vWD÷&–v–âÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢#°¢f"÷'G2Ò&w2ä6÷VçBâ"ò&w5³%Ò¢§5fÇVRåVæFVf–æVC°¢–b…ö–g&ÖU&VÆ×2åG'”vWEfÇVR†–g&ÖRÂ÷WBf"g&ÖU&VÆÒ’¢°¢g&ÖU&VÆÒåVWVTÖW76vTg&öÕ&VçB€¢6öçfW'D§5fÇVUFôö&¦V7B†FF’À¢vWD7W'&VçEv–æF÷t÷&–v–â‚’À¢F&vWD÷&–v–âÀ¢W‡G&7EG&ç6fW'&VDÖW76vU÷'G2‡÷'G2’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢VWVUv–æF÷tÖW76vR‡v–æF÷rÂg&ÖUv–æF÷tÆ—7FVæW'2ÂFFÂöfVä§4vÆö&ÅF†—2Â÷'G2ÂF&vWD÷&–v–â“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢v–æF÷rÀ¢&FDWfVçDÆ—7FVæW""À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&FDWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢FD'&÷w6W$WfVçDÆ—7FVæW"†g&ÖUv–æF÷tÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢v–æF÷rÀ¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢&VÖ÷fT'&÷w6W$WfVçDÆ—7FVæW"†g&ÖUv–æF÷tÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢v–æF÷rÀ¢&F—7F6„WfVçB"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&F—7F6„WfVçB"À¢…òÂ&w2’Óà¢°¢f"WfVçEfÇVRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ„F—7F6„g&ÖUv–æF÷t†÷7DWfVçB‡v–æF÷rÂg&ÖUv–æF÷tÆ—7FVæW'2ÂWfVçEfÇVR’“°¢ÒÀ¢ÆVæwFƒ¢’“° ¢òò)H)Hf÷'v&B7FæF&B'&÷w6W"vÆö&Ç2öçFòF†R–g&ÖR6öçFVçEv–æF÷r)H)H ¢òò–â&VÂ'&÷w6W"Âv–æF÷rÓÓÒvÆö&ÅF†—2Â6òv–æF÷rç6WEF–ÖV÷WBWF2âv÷&²à¢òòfVä'&÷w6W"W6W2Æ–â¥2ö&¦V7Bf÷"–g&ÖR6öçFVçEv–æF÷rÂ6òvR×W7@¢òòW‡Æ–6—FÇ’6÷’ÆÂ7FæF&B'&÷w6W"—2g&öÒF†R–çFW'&WFW"w2vÆö&Ç0¢òòFòÖ¶R67&—G2Æ–¶R&T4D4„‡v†–6‚66W72v–æF÷rç6WEF–ÖV÷WB’v÷&²à¢f"vÆö&Ç5Fôf÷'v&BÒæWuµÐ¢°¢òòF–ÖW'0¢'6WEF–ÖV÷WB"Â'6WD–çFW'fÂ"Â&6ÆV%F–ÖV÷WB"Â&6ÆV$–çFW'fÂ"À¢'&WVW7Dæ–ÖF–öäg&ÖR"Â&6æ6VÄæ–ÖF–öäg&ÖR"À¢òòVæ6öF–æp¢&Fö""Â&'Fö"À¢òòDôÒWF–Æ—F–W0¢&vWD6ö×WFVE7G–ÆR"Â&ÖF6„ÖVF–"Â'VWVTÖ–7&÷F6²"Â'7G'V7GW&VD6ÆöæR"À¢òòö'6W'fW'0¢$×WFF–öäö'6W'fW""Â$–çFW'6V7F–öäö'6W'fW""Â%&W6—¦Tö'6W'fW""Â%W&f÷&Öæ6Tö'6W'fW""À¢òòfWF6‚ò„… ¢&fWF6‚"Â%„ÔÄ‡GG&WVW7B"Â$&÷'D6öçG&öÆÆW""Â$&÷'E6–væÂ"À¢$†VFW'2"Â%&WVW7B"Â%&W7öç6R"À¢òò6öç6öÆP¢&6öç6öÆR"À¢òòæf–vF–öâö&¦V7G0¢&æf–vF÷""Â'W&f÷&Öæ6R"Â&7'—Fò"Â'67&VVâ"À¢òò6öç7G'V7F÷'0¢$WfVçB"Â$7W7FöÔWfVçB"Â$ÖW76vTWfVçB"Â%U$Â"Â%U$Å6V&6…&×2"À¢$DôÕ'6W""Â$f÷&ÔFF"Â$&Æö""Â$f–ÆR"À¢%FW‡DVæ6öFW""Â%FW‡DFV6öFW""À¢$Ö"Â%6WB"Â%vV´Ö"Â%vVµ6WB"Â%vVµ&Vb"À¢%&öÖ—6R"Â%&÷‡’"Â%7–Ö&öÂ"Â$–çFÂ"Â%&VfÆV7B"À¢òò6V7W&—G¢&—56V7W&T6öçFW‡B"Â&7&÷74÷&–v–ä—6öÆFVB"Â&÷&–v–â"À¢òòÖ—62'&÷w6W"—0¢$æ÷F–f–6F–öâ"Â'G'W7FVEG—W2"Â&6‡&öÖR"À¢&ÆW'B"Â&6öæf—&Ò"Â'&ö×B"À¢òòW'&÷"G—W0¢$W'&÷""Â%G—TW'&÷""Â%&ævTW'&÷""Â%&VfW&Væ6TW'&÷""Â%7–çF„W'&÷""Â%U$”W'&÷""Â$WfÄW'&÷""À¢òò6÷&R¥2†æVVFVBv†Vâ&T4D4„FöW2v–æF÷rä'&’WF2â¢$'&’"Â$ö&¦V7B"Â$gVæ7F–öâ"Â%7G&–ær"Â$çVÖ&W""Â$&ööÆVâ"Â%&VtW‡"Â$FFR"Â$ÖF‚"Â$¥4ôâ"À¢''6T–çB"Â''6TfÆöB"Â&—4æâ"Â&—4f–æ—FR"Â'VæFVf–æVB"Â$æâ"Â$–æf–æ—G’"À¢&Væ6öFUU$’"Â&Væ6öFUU$”6ö×öæVçB"Â&FV6öFUU$’"Â&FV6öFUU$”6ö×öæVçB"À¢$'&”'VffW""Â$FFf–Wr"Â$fÆöC3$'&’"Â$fÆöCcD'&’"À¢$–çC„'&’"Â$–çCd'&’"Â$–çC3$'&’"À¢%V–çC„'&’"Â%V–çC„6Æ×VD'&’"Â%V–çCd'&’"Â%V–çC3$'&’"À¢$&–t–çB"Â$&–t–çCcD'&’"Â$&–uV–çCcD'&’"À¢Ó°¢f÷&V6‚‡f"æÖR–âvÆö&Ç5Fôf÷'v&B¢°¢f"vÆö&ÅfÂÒö–çFW'&WFW"å&VDvÆö&ÅfÇVT÷%VæFVf–æVB†æÖR“°¢–b†vÆö&ÅfÂåFrÒ§5fÇVUFråVæFVf–æVB¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂæÖRÂvÆö&ÅfÂ“°¢Ð¢Ð ¢6WE7F÷&VD†÷7E&÷W'G’†–g&ÖRÂ%õöfVä–g&ÖT6öçFVçEv–æF÷r"Âv–æF÷r“°¢f"FVfVÇEf–WtFö7VÖVçBÒg&ÖTFö7VÖVçC°¢–b†FVfVÇEf–WtFö7VÖVçBÓÒçVÆÂ¢°¢f÷&V6‚‡f"6†–ÆB–â–g&ÖRä6†–ÆDæöFW2¢°¢–b†6†–ÆB—2Fö7VÖVçB6†–ÆDFö7VÖVçB¢°¢FVfVÇEf–WtFö7VÖVçBÒ6†–ÆDFö7VÖVçC°¢'&V³°¢Ð¢Ð¢Ð ¢–b†FVfVÇEf–WtFö7VÖVçBÒçVÆÂ¢°¢6WE7F÷&VD†÷7E&÷W'G’†FVfVÇEf–WtFö7VÖVçBÂ%õöfVäFVfVÇEf–Wr"Âv–æF÷r“°¢Ð ¢&WGW&âv–æF÷s°¢Ð ¢&—fFR§5fÇVR&Vv—7FW$ÖW76vT6†ææVÂ„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2¢°¢–b†&w2ÓÒçVÆÂÇÂ&w2ä6÷VçBÂ"ÇÀ¢&w5³ÒåFrÒ§5fÇVUFräö&¦V7BÇÂ&w5³ÒåFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R“°¢Ð ¢f"f—'7BÒæWrÖW76vU÷'DVæGö–çB‚“°¢f"6V6öæBÒæWrÖW76vU÷'DVæGö–çB‚“°¢f—'7BåVW"Ò6V6öæC°¢6V6öæBåVW"Òf—'7C°¢&–æDÖW76vU÷'B†f—'7BÂ&w5³Ò“°¢&–æDÖW76vU÷'B‡6V6öæBÂ&w5³Ò“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡G'VR“°¢Ð ¢&—fFR§5fÇVR÷7DÖW76vU÷'B„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2¢°¢–b†&w2ÓÒçVÆÂÇÂ&w2ä6÷VçBÓÒÇÀ¢G'”vWDÖW76vU÷'DVæGö–çB†&w5³ÒÂ÷WBf"6÷W&6R’¢°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R“°¢Ð ¢f"F&vWBÒ6÷W&6RåVW#°¢–b‡6÷W&6Rä6Æ÷6VBÇÂF&vWBÓÒçVÆÂÇÂF&vWBä6Æ÷6VBÇÂF&vWBä÷væW"ÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡G'VR“°¢Ð ¢F–væ÷7F–5F‡2äVæDÆöuFW‡B€¢'÷7FÖW76vU÷&ö&RçG‡B"À¢B'´FFUF–ÖTöfg6WBåWF4æ÷s¤÷Ò÷'B×6VæBFF×´FW67&–&U÷7DÖW76vUfÇVR†&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVB—Ò"°¢B'G&ç6fW$6÷VçC×²†&w2ä6÷VçBâ"ò&VD'&”Æ–¶TÆVæwF‚†&w5³%Ò’¢—×´Vçf—&öæÖVçBäæWtÆ–æWÒ"“°¢f"FFÒ&w2ä6÷VçBâò6öçfW'D§5fÇVUFôö&¦V7B†&w5³Ò’¢çVÆÃ°¢f"G&ç6fW'&VE÷'G2Ò&w2ä6÷VçBâ ¢òW‡G&7EG&ç6fW'&VDÖW76vU÷'G2†&w5³%Ò¢¢'&’äV×G“ÄÖW76vU÷'DVæGö–çCâ‚“°¢F&vWBä÷væW"åVWVTÖW76vU÷'B‡F&vWBÂFFÂG&ç6fW'&VE÷'G2“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡G'VR“°¢Ð ¢&—fFR§5fÇVR6Æ÷6TÖW76vU÷'B„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2¢°¢–b†&w2ÒçVÆÂbb&w2ä6÷VçBâbbG'”vWDÖW76vU÷'DVæGö–çB†&w5³ÒÂ÷WBf"VæGö–çB’¢°¢VæGö–çBä6Æ÷6VBÒG'VS°¢öÖW76vU÷'DVæGö–çG2å&VÖ÷fR†&w5³Òä4ö&¦V7D†æFÆR‚’åFô–çCcB‚’“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð ¢&—fFR&ööÂG'”vWDÖW76vU÷'DVæGö–çB„§5fÇVR÷'BÂ÷WBÖW76vU÷'DVæGö–çBVæGö–çB¢°¢–b‡÷'BåFrÓÒ§5fÇVUFräö&¦V7B¢°¢&WGW&âöÖW76vU÷'DVæGö–çG2åG'”vWEfÇVR‡÷'Bä4ö&¦V7D†æFÆR‚’åFô–çCcB‚’Â÷WBVæGö–çB“°¢Ð ¢VæGö–çBÒçVÆÃ°¢&WGW&âfÇ6S°¢Ð ¢&—fFRfö–B&–æDÖW76vU÷'B„ÖW76vU÷'DVæGö–çBVæGö–çBÂ§5fÇVR÷'B¢°¢VæGö–çBä÷væW"ÒF†—3°¢VæGö–çBå÷'BÒ÷'C°¢VæGö–çBä6Æ÷6VBÒfÇ6S°¢öÖW76vU÷'DVæGö–çG5·÷'Bä4ö&¦V7D†æFÆR‚’åFô–çCcB‚•ÒÒVæGö–çC°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡÷'BÂ%öfVåv–æF÷r"ÂöfVä§4vÆö&ÅF†—2“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡÷'BÂ%öfVä6Æ÷6VB"Â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’“°¢Ð ¢&—fFR•&VDöæÇ”Æ—7CÄÖW76vU÷'DVæGö–çCâW‡G&7EG&ç6fW'&VDÖW76vU÷'G2„§5fÇVR÷'G2¢°¢–b‡÷'G2åFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&â'&’äV×G“ÄÖW76vU÷'DVæGö–çCâ‚“°¢Ð ¢f"&W7VÇBÒæWrÆ—7CÄÖW76vU÷'DVæGö–çCâ‚“°¢f"ÆVæwF‚Ò&VD'&”Æ–¶TÆVæwF‚‡÷'G2“°¢f÷"‡f"–æFW‚Ò²–æFW‚ÂÆVæwFƒ²–æFW‚²²¢°¢f"÷'BÒ&VD§5&÷W'G’‡÷'G2Â–æFW‚åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢–b‚G'”vWDÖW76vU÷'DVæGö–çB‡÷'BÂ÷WBf"VæGö–çB’ÇÂ&W7VÇBä6öçF–ç2†VæGö–çB’¢°¢6öçF–çVS°¢Ð ¢öÖW76vU÷'DVæGö–çG2å&VÖ÷fR‡÷'Bä4ö&¦V7D†æFÆR‚’åFô–çCcB‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡÷'BÂ%öfVä6Æ÷6VB"Â§5fÇVRäg&öÔ&ööÆVâ‡G'VR’“°¢VæGö–çBä÷væW"ÒçVÆÃ°¢VæGö–çBå÷'BÒ§5fÇVRåVæFVf–æVC°¢&W7VÇBäFB†VæGö–çB“°¢Ð¢&WGW&â&W7VÇC°¢Ð ¢&—fFR§5fÇVR–×÷'EG&ç6fW'&VDÖW76vU÷'G2„•&VDöæÇ”Æ—7CÄÖW76vU÷'DVæGö–çCâVæGö–çG2¢°¢–b†VæGö–çG2ÓÒçVÆÂÇÂVæGö–çG2ä6÷VçBÓÒ¢°¢&WGW&âö–çFW'&WFW"äÆÆö6FT'&’„'&’äV×G“Ä§5fÇVSâ‚’“°¢Ð ¢f"÷'G2ÒæWr§5fÇVU¶VæGö–çG2ä6÷VçEÓ°¢f÷"‡f"–æFW‚Ò²–æFW‚ÂVæGö–çG2ä6÷VçC²–æFW‚²²¢°¢f"÷'BÒWfÇVFUv—F„fVä§5&r‚&æWrÖW76vU÷'B‚’"“°¢&–æDÖW76vU÷'B†VæGö–çG5¶–æFW…ÒÂ÷'B“°¢÷'G5¶–æFW…ÒÒ÷'C°¢Ð¢&WGW&âö–çFW'&WFW"äÆÆö6FT'&’‡÷'G2“°¢Ð ¢&—fFR–çB&VD'&”Æ–¶TÆVæwF‚„§5fÇVRfÇVR¢°¢f"ÆVæwF‚Ò&VD§5&÷W'G’‡fÇVRÂ&ÆVæwF‚"“°¢&WGW&âÆVæwF‚åFr7v—F6€¢°¢§5fÇVUFrä–çC3"ÓâÖF‚äÖ‚ƒÂÆVæwF‚ä4–çC3"‚’’À¢§5fÇVUFräçVÖ&W"ÓâÖF‚äÖ‚ƒÂ†–çB–ÆVæwF‚ä4çVÖ&W"‚’’À¢òÓâ ¢Ó°¢Ð ¢&—fFRfö–BVWVTÖW76vU÷'B€¢ÖW76vU÷'DVæGö–çBF&vWBÀ¢ö&¦V7BFFÀ¢•&VDöæÇ”Æ—7CÄÖW76vU÷'DVæGö–çCâG&ç6fW'&VDVæGö–çG2¢°¢f"6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öã°¢Æö6²…÷v–æF÷tÖW76vUVWVTÆö6²¢°¢÷v–æF÷tÖW76vTFVÆ—fW'•F–ÂÒ÷v–æF÷tÖW76vTFVÆ—fW'•F–Âä6öçF–çVUv—F‚€¢òÓà¢°¢–b‡6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öâÇÀ¢F&vWBä÷væW"ÒF†—2ÇÂF&vWBä6Æ÷6VBÇÂF&vWBå÷'BåFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢G'¢°¢'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢òò–×÷'BG&ç6fW'&VB÷'G2–âF†R&V6V—f–ær&VÆÒöæÇ’gFW ¢òò—G2VWVVBFVÆ—fW'’÷vç2F†R–çFW'&WFW"Æö6²â7V—&–ærF†P¢òò&V6V—fW"Æö6²7–æ6‡&öæ÷W6Ç’g&öÒF†R6VæF–ær&VÆÒFVFÆö6·0¢òòv†VâGvòÖW76vU÷'B†æFÆW'2÷7BFòV6‚÷F†W"6öæ7W'&VçFÇ’à¢f"G&ç6fW'&VE÷'G2Ò–×÷'EG&ç6fW'&VDÖW76vU÷'G2‡G&ç6fW'&VDVæGö–çG2“°¢f"WfVçEfÇVRÒ7&VFTÖW76vTWfVçEfÇVR€¢6öçfW'Dö&¦V7EFô§5fÇVR†FF’À¢7G&–æräV×G’À¢§5fÇVRäçVÆÂÀ¢öfVä§4vÆö&ÅF†—2À¢G&ç6fW'&VE÷'G2“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'F&vWB"ÂF&vWBå÷'B“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&7W'&VçEF&vWB"ÂF&vWBå÷'B“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'7&4VÆVÖVçB"ÂF&vWBå÷'B“° ¢W6–ær„7F—fFUv–æF÷t6ÆÆ&6´6öçFW‡B…öfVä§4vÆö&ÅF†—2Â÷v–æF÷tWfVçDÆ—7FVæW'2’¢°¢f"†æFÆW"Ò&VD§5&÷W'G’‡F&vWBå÷'BÂ&öæÖW76vR"“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"ÂF&vWBå÷'BÂWfVçEfÇVRÂ&ÖW76vR"“°¢Ð ¢f"Æ—7FVæW'2Ò&VD§5&÷W'G’‡F&vWBå÷'BÂ%öfVäÆ—7FVæW'2"“°¢f"Æ—7FVæW$6÷VçBÒ&VD'&”Æ–¶TÆVæwF‚†Æ—7FVæW'2“°¢f÷"‡f"–æFW‚Ò²–æFW‚ÂÆ—7FVæW$6÷VçC²–æFW‚²²¢°¢f"Æ—7FVæW"Ò&VD§5&÷W'G’†Æ—7FVæW'2Â–æFW‚åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR†Æ—7FVæW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²†Æ—7FVæW"ÂF&vWBå÷'BÂWfVçEfÇVRÂ&ÖW76vR"“°¢Ð¢Ð¢Ð¢ö–çFW'&WFW"åV×Ö–7&÷F6·2‚“°¢Ð¢&WGW&âçVÆÃ°¢ÒÂv—Df÷%v÷&¶W$×3¢ÓÂ–ç7G'V7F–öä'VFvWC¢fVä§4'&÷w6W%F6´–ç7G'V7F–öä'VFvWB“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒÖW76vU÷'BFVÆ—fW'’f–ÆVC¢¶W‡Ò"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð ¢G'’²&WVW7E&VæFW#òä–çfö¶R‚“²Ò6F6‚²Ð¢ÒÀ¢6æ6VÆÆF–öåFö¶VâäæöæRÀ¢F6´6öçF–çVF–öä÷F–öç2äæöæRÀ¢F6µ66†VGVÆW"äFVfVÇB“°¢Ð¢Ð ¢&—fFRfö–B6öæf–wW&TVÖ&VFFVE&VÆÔvÆö&Ç2‚¢°¢f"VÖ&VFF–ætg&ÖRÒöVÖ&VFF–ætg&ÖTVÆVÖVçC°¢–b†VÖ&VFF–ætg&ÖRÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"&VçDFö7VÖVçEW&’ÒvWE&VçDFö7VÖVçEW&’†VÖ&VFF–ætg&ÖR“°¢f"g&ÖUW&’Òö7W'&VçD&6UW&’óòG'”7&VFUW&’‚…ö7W'&VçDFöÕ&ö÷B2Fö7VÖVçBóòö7W'&VçDFöÕ&ö÷Còä÷væW$Fö7VÖVçB“òåU$Â“°¢f"6ÖT÷&–v–âÒ—56ÖT÷&–v–äg&ÖT66W72†VÖ&VFF–ætg&ÖRÂg&ÖUW&“òä'6öÇWFUW&’Â&VçDFö7VÖVçEW&’“°¢f"&VçE&÷‡’Òö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢öVÖ&VFFVE&VçEv–æF÷u&÷‡’Ò&VçE&÷‡“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â'v–æF÷r"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â'6VÆb"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â'&VçB"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â'F÷"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â&g&ÖW2"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"ƒ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Â#"ÂöfVä§4vÆö&ÅF†—2“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢&–ææW%v–GF‚"À¢§5fÇVRäg&öÔçVÖ&W"…÷&VçE&VÆÔ÷væW#òåv–æF÷uv–GF‚óòv–æF÷uv–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢&–ææW$†V–v‡B"À¢§5fÇVRäg&öÔçVÖ&W"…÷&VçE&VÆÔ÷væW#òåv–æF÷t†V–v‡Bóòv–æF÷t†V–v‡B’“°¢f"g&ÖTæÖRÒVÖ&VFF–ætg&ÖRävWDGG&–'WFR‚&æÖR"’óò7G&–æräV×G“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&æÖR"Â§5fÇVRäg&öÕ7G&–ær†g&ÖTæÖR’“°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†g&ÖTæÖR’¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçE&÷‡’Âg&ÖTæÖRÂöfVä§4vÆö&ÅF†—2“°¢Ð¢–b‡6ÖT÷&–v–âbbVÖ&VFF–ætg&ÖRä÷væW$Fö7VÖVçBÒçVÆÂ¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢&Fö7VÖVçB"À¢Fô†÷7D÷$çVÆÂ†VÖ&VFF–ætg&ÖRä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢&Æö6F–öâ"À¢7&VFUÆ–äÆö6F–öäö&¦V7B‡&VçDFö7VÖVçEW&“òä'6öÇWFUW&’óò&&÷WC¦&Ææ²"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢&FDWfVçDÆ—7FVæW""À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&FDWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢FD'&÷w6W$WfVçDÆ—7FVæW"…öVÖ&VFFVE&VçEv–æF÷tÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢&VÖ÷fT'&÷w6W$WfVçDÆ—7FVæW"…öVÖ&VFFVE&VçEv–æF÷tÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"’“°¢Ð¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢&VçE&÷‡’À¢'÷7DÖW76vR"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'÷7DÖW76vR"À¢…òÂ&w2’Óà¢°¢F–væ÷7F–5F‡2äVæDÆöuFW‡B€¢'÷7FÖW76vU÷&ö&RçG‡B"À¢B'´FFUF–ÖTöfg6WBåWF4æ÷s¤÷Òg&ÖR×Fò×&VçBFF×´FW67&–&U÷7DÖW76vUfÇVR†&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVB—Ò"°¢B'F&vWD÷&–v–ã×²†&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢"—Ò"°¢B'G&ç6fW$6÷VçC×²†&w2ä6÷VçBâ"ò&VD'&”Æ–¶TÆVæwF‚†&w5³%Ò’¢—×´Vçf—&öæÖVçBäæWtÆ–æWÒ"“°¢f"FFÒ&w2ä6÷VçBâò6öçfW'D§5fÇVUFôö&¦V7B†&w5³Ò’¢çVÆÃ°¢f"F&vWD÷&–v–âÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢#°¢f"÷'G2Ò&w2ä6÷VçBâ ¢òW‡G&7EG&ç6fW'&VDÖW76vU÷'G2†&w5³%Ò¢¢'&’äV×G“ÄÖW76vU÷'DVæGö–çCâ‚“°¢÷&VçE&VÆÔ÷væW#òåVWVTÖW76vTg&öÔg&ÖR€¢VÖ&VFF–ætg&ÖRÀ¢FFÀ¢F&vWD÷&–v–âÀ¢÷'G2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“° ¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'&VçB"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'F÷"Â&VçE&÷‡’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR€¢&g&ÖTVÆVÖVçB"À¢6ÖT÷&–v–à¢òFô†÷7D÷$çVÆÂ†VÖ&VFF–ætg&ÖRÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB¢¢§5fÇVRäçVÆÂ“°¢–ç7FÆÄ÷væVDg&ÖU67&öÆÄvÆö&Ç2‚“°¢Ð ¢&—fFR7G&–ærFW67&–&U÷7DÖW76vUfÇVR„§5fÇVRfÇVR¢°¢–b‡fÇVRåFrÓÒ§5fÇVUFrå7G&–ær¢°¢f"FW‡BÒfÇVRä57G&–ær‚’óò7G&–æräV×G“°¢&WGW&âB%7G&–ær‡·FW‡BäÆVæwF‡Ò“§·FW‡E²âäÖF‚äÖ–â‡FW‡BäÆVæwF‚Â#•×Ò#°¢Ð ¢–b‡fÇVRåFrÒ§5fÇVUFräö&¦V7BÇÂö–çFW'&WFW"ÓÒçVÆÂ¢°¢&WGW&âfÇVRåFråFõ7G&–ær‚“°¢Ð ¢G'¢°¢f"ö&¢Òö–çFW'&WFW"ä†VävWDö&¦V7B‡fÇVRä4ö&¦V7D†æFÆR‚’“°¢f"¶W—2Òö&£òäVçVÖW&FT÷vå&÷W'F–W2‚¢å6VÆV7B†VçG'’ÓâVçG'’ä¶W’¢åF¶Rƒ"¢åFô'&’‚’óò'&’äV×G“Ç7G&–æsâ‚“°¢&WGW&âB'¶ö&£òävWEG—R‚’äæÖRóò$ö&¦V7B'Ò¶W—3Õ··7G&–ærä¦ö–â‚"Â"Â¶W—2—ÕÒ#°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢&WGW&âB$ö&¦V7B‡Væf–Æ&ÆS§¶W‚ävWEG—R‚’äæÖWÒ’#°¢Ð¢Ð ¢&—fFRfö–Bæ÷F–g”VÖ&VFFVDg&ÖW4öe&VçEv–æF÷tWfVçB‡7G&–ærG—RÂ§5fÇVR6÷W&6TWfVçB¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡G—R’¢°¢&WGW&ã°¢Ð ¢ö&¦V7BFFÒçVÆÃ°¢7G&–ær÷&–v–âÒ7G&–æräV×G“°¢–b‡6÷W&6TWfVçBåFrÓÒ§5fÇVUFräö&¦V7B¢°¢f"FFfÇVRÒ&VD§5&÷W'G’‡6÷W&6TWfVçBÂ&FF"“°¢–b†FFfÇVRåFrÒ§5fÇVUFråVæFVf–æVB¢°¢FFÒ6öçfW'D§5fÇVUFôö&¦V7B†FFfÇVR“°¢Ð¢f"÷&–v–åfÇVRÒ&VD§5&÷W'G’‡6÷W&6TWfVçBÂ&÷&–v–â"“°¢–b†÷&–v–åfÇVRåFrÓÒ§5fÇVUFrå7G&–ær¢°¢÷&–v–âÒ÷&–v–åfÇVRä57G&–ær‚“°¢Ð¢Ð ¢f÷&V6‚‡f"g&ÖU&VÆÒ–âö–g&ÖU&VÆ×2å6VÆV7B†VçG'’ÓâVçG'’åfÇVR’äF—7F–æ7B‚’åFô'&’‚’¢°¢g&ÖU&VÆÒåVWVTVÖ&VFFVE&VçEv–æF÷tWfVçB‡G—RÂFFÂ÷&–v–â“°¢Ð¢Ð ¢&—fFRfö–BVWVTVÖ&VFFVE&VçEv–æF÷tWfVçB‡7G&–ærG—RÂö&¦V7BFFÂ7G&–ær÷&–v–â¢°¢–b…öVÖ&VFFVE&VçEv–æF÷tÆ—7FVæW'2ä6÷VçBÓÒÇÂöVÖ&VFFVE&VçEv–æF÷u&÷‡’åFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢òÒF6²äf7F÷'’å7F'DæWr€¢‚’Óâ'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢–b…öVÖ&VFFVE&VçEv–æF÷tÆ—7FVæW'2ä6÷VçBÓÒÇÀ¢öVÖ&VFFVE&VçEv–æF÷u&÷‡’åFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&âçVÆÃ°¢Ð ¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡G—R’À¢²&FF%ÒÒ6öçfW'Dö&¦V7EFô§5fÇVR†FF’À¢²&÷&–v–â%ÒÒ§5fÇVRäg&öÕ7G&–ær†÷&–v–âóò7G&–æräV×G’¢Ò“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂöVÖ&VFFVE&VçEv–æF÷u&÷‡’“°¢F—7F6„'&÷w6W$WfVçB€¢öVÖ&VFFVE&VçEv–æF÷tÆ—7FVæW'2À¢G—RÀ¢öVÖ&VFFVE&VçEv–æF÷u&÷‡’À¢WfVçEfÇVR“°¢&WGW&âçVÆÃ°¢Ð¢Ò’À¢6æ6VÆÆF–öåFö¶VâäæöæRÀ¢F6´7&VF–öä÷F–öç2äFVç”6†–ÆDGF6‚À¢F6µ66†VGVÆW"äFVfVÇB“°¢Ð ¢&—fFRfö–B–ç7FÆÄ÷væVDg&ÖU67&öÆÄvÆö&Ç2‚¢°¢f"–æ—F–ÂÒ&VD÷væVDg&ÖU67&öÆÂ‚“°¢6WD÷væVE67&öÆÄvÆö&Ç2†–æ—F–Âå‚Â–æ—F–Âå’“° ¢f"67&öÆÅFòÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'67&öÆÅFò"À¢…òÂ&w2’Óà¢°¢f"‡‚Â’’Ò&VE67&öÆÄ&wVÖVçG2†&w2Â&VÆF—fS¢fÇ6R“°¢67&öÆÄ÷væVDg&ÖUFò‡‚Â’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢f"67&öÆÄ'’Òö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'67&öÆÄ'’"À¢…òÂ&w2’Óà¢°¢f"‡‚Â’’Ò&VE67&öÆÄ&wVÖVçG2†&w2Â&VÆF—fS¢G'VR“°¢67&öÆÄ÷væVDg&ÖUFò‡‚Â’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'67&öÆÅFò"Â67&öÆÅFò“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'67&öÆÂ"Â67&öÆÅFò“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'67&öÆÄ'’"Â67&öÆÄ'’“°¢Ð ¢&—fFR†F÷V&ÆR‚ÂF÷V&ÆR’’&VE67&öÆÄ&wVÖVçG2„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2Â&ööÂ&VÆF—fR¢°¢f"7W'&VçBÒ&VD÷væVDg&ÖU67&öÆÂ‚“°¢f"‚Ò&VÆF—fRò7W'&VçBå‚¢C°¢f"’Ò&VÆF—fRò7W'&VçBå’¢C°¢–b†&w2ä6÷VçBâbb&w5³ÒåFrÓÒ§5fÇVUFräö&¦V7B¢°¢f"ÆVgBÒ&VD§5&÷W'G’†&w5³ÒÂ&ÆVgB"“°¢f"F÷Ò&VD§5&÷W'G’†&w5³ÒÂ'F÷"“°¢–b†ÆVgBåFrÒ§5fÇVUFråVæFVf–æVB¢°¢‚Ò‡&VÆF—fRò7W'&VçBå‚¢B’²6öW&6UFôf–æ—FTçVÖ&W"†ÆVgBÂ“°¢Ð¢–b‡F÷åFrÒ§5fÇVUFråVæFVf–æVB¢°¢’Ò‡&VÆF—fRò7W'&VçBå’¢B’²6öW&6UFôf–æ—FTçVÖ&W"‡F÷Â“°¢Ð¢&WGW&â‡‚Â’“°¢Ð ¢–b†&w2ä6÷VçBâ¢°¢‚Ò‡&VÆF—fRò7W'&VçBå‚¢B’²6öW&6UFôf–æ—FTçVÖ&W"†&w5³ÒÂ“°¢Ð¢–b†&w2ä6÷VçBâ¢°¢’Ò‡&VÆF—fRò7W'&VçBå’¢B’²6öW&6UFôf–æ—FTçVÖ&W"†&w5³ÒÂ“°¢Ð¢&WGW&â‡‚Â’“°¢Ð ¢&—fFR†F÷V&ÆR‚ÂF÷V&ÆR’’&VD÷væVDg&ÖU67&öÆÂ‚’Óà¢öVÖ&VFF–ætg&ÖTVÆVÖVçBÒçVÆÂbbg&ÖU67&öÆÅ&VFW"ÒçVÆÀ¢òg&ÖU67&öÆÅ&VFW"…öVÖ&VFF–ætg&ÖTVÆVÖVçB¢¢ƒBÂB“° ¢&—fFRfö–B67&öÆÄ÷væVDg&ÖUFò†F÷V&ÆR‚ÂF÷V&ÆR’¢°¢–b…öVÖ&VFF–ætg&ÖTVÆVÖVçBÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢g&ÖU67&öÆÅw&—FW#òä–çfö¶R…öVÖ&VFF–ætg&ÖTVÆVÖVçBÂÖF‚äÖ‚ƒÂ‚’ÂÖF‚äÖ‚ƒÂ’’“°¢f"7GVÂÒ&VD÷væVDg&ÖU67&öÆÂ‚“°¢WFFT÷væVE67&öÆÂ†7GVÂå‚Â7GVÂå’“°¢&WVW7E&VæFW#òä–çfö¶R‚“°¢Ð ¢&—fFRfö–BVWVTÖW76vTg&öÔg&ÖR€¢VÆVÖVçBg&ÖRÀ¢ö&¦V7BFFÀ¢7G&–ærF&vWD÷&–v–âÀ¢•&VDöæÇ”Æ—7CÄÖW76vU÷'DVæGö–çCâG&ç6fW'&VDVæGö–çG2¢°¢–b†g&ÖRÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢Æö6²…öfVä§4Æö6²¢°¢f"6÷W&6Uv–æF÷rÒvWD÷$7&VFT”g&ÖT6öçFVçEv–æF÷r†g&ÖR“°¢f"÷'G2Ò–×÷'EG&ç6fW'&VDÖW76vU÷'G2‡G&ç6fW'&VDVæGö–çG2“°¢VWVUv–æF÷tÖW76vR€¢öfVä§4vÆö&ÅF†—2À¢÷v–æF÷tWfVçDÆ—7FVæW'2À¢6öçfW'Dö&¦V7EFô§5fÇVR†FF’À¢6÷W&6Uv–æF÷rÀ¢÷'G2À¢F&vWD÷&–v–âÀ¢ö7W'&VçD&6UW&“òävWDÆVgE'B…W&•'F–ÂäWF†÷&—G’’“°¢Ð¢Ð ¢&—fFRfö–BVWVTÖW76vTg&öÕ&VçB€¢ö&¦V7BFFÀ¢7G&–ær6÷W&6T÷&–v–âÀ¢7G&–ærF&vWD÷&–v–âÀ¢•&VDöæÇ”Æ—7CÄÖW76vU÷'DVæGö–çCâG&ç6fW'&VDVæGö–çG2¢°¢Æö6²…öfVä§4Æö6²¢°¢f"÷'G2Ò–×÷'EG&ç6fW'&VDÖW76vU÷'G2‡G&ç6fW'&VDVæGö–çG2“°¢VWVUv–æF÷tÖW76vR€¢öfVä§4vÆö&ÅF†—2À¢÷v–æF÷tWfVçDÆ—7FVæW'2À¢6öçfW'Dö&¦V7EFô§5fÇVR†FF’À¢öVÖ&VFFVE&VçEv–æF÷u&÷‡’À¢÷'G2À¢F&vWD÷&–v–âÀ¢vWD7W'&VçEv–æF÷t÷&–v–â‚’À¢6÷W&6T÷&–v–â“°¢Ð¢Ð ¢&—fFRfö–B6÷”g&ÖU&VÆÔö'6W'f&ÆW2„fVä§4'&÷w6W%67&—DVæv–æR&VÆÒÂ§5fÇVR&÷‡’¢°¢f"g&ÖRÒ&VÆÒåöVÖ&VFF–ætg&ÖTVÆVÖVçC°¢f"6ÖT÷&–v–âÒ—56ÖT÷&–v–äg&ÖT66W72€¢g&ÖRÀ¢&VÆÒåö7W'&VçD&6UW&“òä'6öÇWFUW&’À¢vWE&VçDFö7VÖVçEW&’†g&ÖR’“°¢f÷&V6‚‡f"†æÖRÂfÇVR’–â&VÆÒä6GW&Tö'6W'f&ÆUv–æF÷u&÷W'F–W2‚’¢°¢f"6öçfW'FVBÒ6öçfW'Dö&¦V7EFô§5fÇVR‡fÇVR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&÷‡’ÂæÖRÂ6öçfW'FVB“°¢–b‡6ÖT÷&–v–â¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…öfVä§4vÆö&ÅF†—2ÂæÖRÂ6öçfW'FVB“°¢Ð¢Ð¢Ð ¢&—fFRF–7F–öæ'“Ç7G&–ærÂö&¦V7Câ6GW&Tö'6W'f&ÆUv–æF÷u&÷W'F–W2‚¢°¢f"&W7VÇBÒæWrF–7F–öæ'“Ç7G&–ærÂö&¦V7Câ…7G&–æt6ö×&W"ä÷&F–æÂ“°¢Æö6²…öfVä§4Æö6²¢°¢–b…öfVä§4vÆö&ÅF†—2åFrÒ§5fÇVUFräö&¦V7BÇÂö–çFW'&WFW"ÓÒçVÆÂ¢°¢&WGW&â&W7VÇC°¢Ð ¢f"vÆö&ÂÒö–çFW'&WFW"ä†VävWDö&¦V7B…öfVä§4vÆö&ÅF†—2ä4ö&¦V7D†æFÆR‚’“°¢f÷&V6‚‡f"&÷W'G’–âvÆö&ÂäVçVÖW&FT÷vå&÷W'F–W2‚’¢°¢–b‚&÷W'G’ä¶W’å7F'G5v—F‚‚%õò"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢&÷W'G’ä¶W’å7F'G5v—F‚‚%õöfVâ"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢6öçF–çVS°¢Ð¢f"fÇVRÒ&÷W'G’åfÇVRåfÇVS°¢&W7VÇE·&÷W'G’ä¶W•ÒÒ6öçfW'D§5fÇVUFôö&¦V7B‡fÇVR“°¢Ð¢Ð ¢&WGW&â&W7VÇC°¢Ð ¢&—fFR§5fÇVR6öçfW'Dö&¦V7EFô§5fÇVR†ö&¦V7BfÇVRÂ–çBFWF‚Ò¢°¢–b‡fÇVRÓÒçVÆÂÇÂFWF‚âb’&WGW&â§5fÇVRäçVÆÃ°¢&WGW&âfÇVR7v—F6€¢°¢&ööÂ&ööÆVâÓâ§5fÇVRäg&öÔ&ööÆVâ†&ööÆVâ’À¢'—FRçVÖ&W"Óâ§5fÇVRäg&öÔ–çC3"†çVÖ&W"’À¢6†÷'BçVÖ&W"Óâ§5fÇVRäg&öÔ–çC3"†çVÖ&W"’À¢–çBçVÖ&W"Óâ§5fÇVRäg&öÔ–çC3"†çVÖ&W"’À¢ÆöærçVÖ&W"v†VâçVÖ&W"—2ãÒ–çBäÖ–åfÇVRæBÃÒ–çBäÖ…fÇVRÓâ§5fÇVRäg&öÔ–çC3"‚†–çB–çVÖ&W"’À¢ÆöærçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"†çVÖ&W"’À¢fÆöBçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"†çVÖ&W"’À¢F÷V&ÆRçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"†çVÖ&W"’À¢FV6–ÖÂçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"‚†F÷V&ÆR–çVÖ&W"’À¢7G&–ærFW‡BÓâ§5fÇVRäg&öÕ7G&–ær‡FW‡B’À¢”F–7F–öæ'“Ç7G&–ærÂö&¦V7CâF–7F–öæ'’Óâö–çFW'&WFW"äÆÆö6FTö&¦V7B€¢F–7F–öæ'’åFôF–7F–öæ'’€¢VçG'’ÓâVçG'’ä¶W’À¢VçG'’Óâ6öçfW'Dö&¦V7EFô§5fÇVR†VçG'’åfÇVRÂFWF‚²’À¢7G&–æt6ö×&W"ä÷&F–æÂ’’À¢”VçVÖW&&ÆSÆö&¦V7Câ6WVVæ6RÓâ7&VFT§4'&’‡6WVVæ6RÂFWF‚²’À¢òÓâ§5fÇVRäg&öÕ7G&–ær‡fÇVRåFõ7G&–ær‚’óò7G&–æräV×G’¢Ó°¢Ð ¢&—fFR§5fÇVR7&VFT§4'&’„”VçVÖW&&ÆSÆö&¦V7Câ6WVVæ6RÂ–çBFWF‚¢°¢f"fÇVW2Ò6WVVæ6SòåFô'&’‚’óò'&’äV×G“Æö&¦V7Câ‚“°¢f"§5fÇVW2ÒæWr§5fÇVU·fÇVW2äÆVæwF…Ó°¢f÷"‡f"–æFW‚Ò²–æFW‚ÂfÇVW2äÆVæwFƒ²–æFW‚²²¢°¢§5fÇVW5¶–æFW…ÒÒ6öçfW'Dö&¦V7EFô§5fÇVR‡fÇVW5¶–æFW…ÒÂFWF‚“°¢Ð¢&WGW&âö–çFW'&WFW"äÆÆö6FT'&’†§5fÇVW2“°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFUF÷v–æF÷tf6FR‚¢°¢–b…öfVä§5F÷v–æF÷tf6FRåFrÓÒ§5fÇVUFräö&¦V7B¢°¢&WGW&âöfVä§5F÷v–æF÷tf6FS°¢Ð ¢f"f6FRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢öfVä§5F÷v–æF÷tf6FRÒf6FS°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'v–æF÷r"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'6VÆb"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'&VçB"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'F÷"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&g&ÖW2"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"ƒ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'÷7DÖW76vR"Â7&VFUF÷v–æF÷u÷7DÖW76vTgVæ7F–öâ‚’“°¢&WGW&âf6FS°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFU6ÖT÷&–v–åF÷v–æF÷tf6FR‚¢°¢–b…öfVä§56ÖT÷&–v–åF÷v–æF÷tf6FRåFrÓÒ§5fÇVUFräö&¦V7B¢°¢&WGW&âöfVä§56ÖT÷&–v–åF÷v–æF÷tf6FS°¢Ð ¢f"f6FRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢öfVä§56ÖT÷&–v–åF÷v–æF÷tf6FRÒf6FS°¢f÷&V6‚‡f"&÷W'G’–âæWuµÐ¢°¢&Fö7VÖVçB"Â&Æö6F–öâ"Â&æf–vF÷""Â&†—7F÷'’"À¢&–ææW%v–GF‚"Â&–ææW$†V–v‡B"Â&÷WFW%v–GF‚"Â&÷WFW$†V–v‡B"À¢&FWf–6U—†VÅ&F–ò"Â&F—7F6„WfVçB"Â&FDWfVçDÆ—7FVæW""Â'&VÖ÷fTWfVçDÆ—7FVæW" ¢Ò¢°¢f"fÇVRÒ&VD§5&÷W'G’…öfVä§4vÆö&ÅF†—2Â&÷W'G’“°¢–b‡fÇVRåFrÒ§5fÇVUFråVæFVf–æVB¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&÷W'G’ÂfÇVR“°¢Ð¢Ð ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'v–æF÷r"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'6VÆb"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'&VçB"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'F÷"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&g&ÖW2"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"ƒ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'÷7DÖW76vR"Â7&VFUF÷v–æF÷u÷7DÖW76vTgVæ7F–öâ‚’“°¢&WGW&âf6FS°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFT7&÷74÷&–v–ä6†–ÆEv–æF÷tf6FR„VÆVÖVçB–g&ÖRÂ§5fÇVRF&vWEv–æF÷r¢°¢f"66†VBÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†–g&ÖRÂ%õöfVä7&÷74÷&–v–ä6öçFVçEv–æF÷r"“°¢–b†66†VBåFrÓÒ§5fÇVUFräö&¦V7B¢°¢&WGW&â66†VC°¢Ð ¢f"f6FRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢f"&VçDf6FRÒvWD÷$7&VFUF÷v–æF÷tf6FR‚“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'v–æF÷r"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'6VÆb"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&g&ÖW2"Âf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'&VçB"Â&VçDf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ'F÷"Â&VçDf6FR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&6Æ÷6VB"Â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†f6FRÂ&ÆVæwF‚"Â&VD§5&÷W'G’‡F&vWEv–æF÷rÂ&ÆVæwF‚"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢f6FRÀ¢'÷7DÖW76vR"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'÷7DÖW76vR"À¢…òÂ&w2’Óà¢°¢f"FFÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢f"F&vWD÷&–v–âÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢#°¢f"÷'G2Ò&w2ä6÷VçBâ"ò&w5³%Ò¢§5fÇVRåVæFVf–æVC°¢–b…ö–g&ÖU&VÆ×2åG'”vWEfÇVR†–g&ÖRÂ÷WBf"g&ÖU&VÆÒ’¢°¢g&ÖU&VÆÒåVWVTÖW76vTg&öÕ&VçB€¢6öçfW'D§5fÇVUFôö&¦V7B†FF’À¢vWD7W'&VçEv–æF÷t÷&–v–â‚’À¢F&vWD÷&–v–âÀ¢W‡G&7EG&ç6fW'&VDÖW76vU÷'G2‡÷'G2’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢VWVUv–æF÷tÖW76vR€¢F&vWEv–æF÷rÀ¢vWD”g&ÖUv–æF÷tÆ—7FVæW'2†–g&ÖR’À¢FFÀ¢vWD7F—fUv–æF÷tWfVçEF&vWB‚’À¢÷'G2À¢F&vWD÷&–v–â“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“° ¢6WE7F÷&VD†÷7E&÷W'G’†–g&ÖRÂ%õöfVä7&÷74÷&–v–ä6öçFVçEv–æF÷r"Âf6FR“°¢&WGW&âf6FS°¢Ð ¢&—fFRfö–BWFFT”g&ÖT6öçFVçEv–æF÷r€¢§5fÇVRv–æF÷rÀ¢VÆVÖVçB–g&ÖRÀ¢Fö7VÖVçBg&ÖTFö7VÖVçBÀ¢W&’g&ÖUW&’¢°¢–b‡v–æF÷råFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢f"Fö7VÖVçBÒg&ÖTFö7VÖVçBÓÒçVÆÀ¢òvWD÷$7&VFT”g&ÖT6öçFVçDFö7VÖVçB†–g&ÖR¢¢Fô†÷7D÷$çVÆÂ†g&ÖTFö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢f"‡&VbÒ&W6öÇfT”g&ÖUv–æF÷t‡&Vb†–g&ÖRÂg&ÖTFö7VÖVçBÂg&ÖUW&’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&Fö7VÖVçB"ÂFö7VÖVçB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&Æö6F–öâ"Â7&VFUÆ–äÆö6F–öäö&¦V7B†‡&Vb’“°¢f"&VçD6ä66W74g&ÖRÒ—56ÖT÷&–v–äg&ÖT66W72†–g&ÖRÂ‡&VbÂvWE&VçDFö7VÖVçEW&’†–g&ÖR’“°¢f"g&ÖTVÆVÖVçEfÇVRÒFô†÷7D÷$çVÆÂ†–g&ÖRÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ%õöfVäg&ÖTVÆVÖVçB"Âg&ÖTVÆVÖVçEfÇVR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&g&ÖTVÆVÖVçB"Â&VçD6ä66W74g&ÖRòg&ÖTVÆVÖVçEfÇVR¢§5fÇVRäçVÆÂ“°¢6WD”g&ÖUf–Ww÷'E&÷W'F–W2‡v–æF÷rÂ–g&ÖR“°¢f"g&ÖTæÖRÒ–g&ÖRävWDGG&–'WFR‚&æÖR"’óò7G&–æräV×G“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&æÖR"Â§5fÇVRäg&öÕ7G&–ær†g&ÖTæÖR’“°¢f"&VçBÒ&VD§5&÷W'G’‡v–æF÷rÂ'&VçB"“°¢f"W‡÷6VEv–æF÷rÒ&VçD6ä66W74g&ÖP¢òv–æF÷p¢¢vWD÷$7&VFT7&÷74÷&–v–ä6†–ÆEv–æF÷tf6FR†–g&ÖRÂv–æF÷r“°¢&Vv—7FW$6†–ÆDg&ÖTöå&VçB‡&VçBÂW‡÷6VEv–æF÷rÂg&ÖTæÖR“° ¢–b†g&ÖTFö7VÖVçBÒçVÆÂ¢°¢6WE7F÷&VD†÷7E&÷W'G’†g&ÖTFö7VÖVçBÂ%õöfVäFVfVÇEf–Wr"Âv–æF÷r“°¢Ð¢Ð ¢&—fFR7G&–ær&W6öÇfT”g&ÖUv–æF÷t‡&Vb„VÆVÖVçB–g&ÖRÂFö7VÖVçBg&ÖTFö7VÖVçBÂW&’g&ÖUW&’¢°¢f"‡&VbÐ¢g&ÖUW&“òä'6öÇWFUW&’óð¢g&ÖTFö7VÖVçCòåU$Âóð¢g&ÖTFö7VÖVçCòäFö7VÖVçEU$’óð¢g&ÖTFö7VÖVçCòä&6UU$’óð¢&W6öÇfTVÆVÖVçEW&Å&÷W'G’†–g&ÖRÂ'7&2"“° ¢&WGW&â7G&–ærä—4çVÆÄ÷%v†—FU76R†‡&Vb’ò&&÷WC¦&Ææ²"¢‡&Vc°¢Ð ¢&—fFR7FF–2W&’vWE&VçDFö7VÖVçEW&’„VÆVÖVçB–g&ÖR¢°¢f"Fö7VÖVçBÒ–g&ÖSòä÷væW$Fö7VÖVçC°¢&WGW&âG'”7&VFUW&’†Fö7VÖVçCòåU$Â’óð¢G'”7&VFUW&’†Fö7VÖVçCòäFö7VÖVçEU$’’óð¢G'”7&VFUW&’†Fö7VÖVçCòä&6UU$’“°¢Ð ¢&—fFR7FF–2&ööÂ—56ÖT÷&–v–äg&ÖT66W72„VÆVÖVçB–g&ÖRÂ7G&–ærg&ÖT‡&VbÂW&’66W76÷%W&’¢°¢–b†–g&ÖRÓÒçVÆÂÇÂ66W76÷%W&’ÓÒçVÆÂ¢°¢&WGW&âfÇ6S°¢Ð ¢–b†–g&ÖRä†4GG&–'WFR‚'6æF&÷‚"’¢°¢f"fÆw2ÒfVä'&÷w6W"ä6÷&Rå6æF&÷…öÆ–7’å'6T–g&ÖU6æF&÷„fÆw2†–g&ÖRävWDGG&–'WFR‚'6æF&÷‚"’“°¢–b‚†fÆw2b–g&ÖU6æF&÷„fÆw2å6ÖT÷&–v–â’ÓÒ¢°¢&WGW&âfÇ6S°¢Ð¢Ð ¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†g&ÖT‡&Vb’ÇÀ¢7G&–æräWVÇ2†g&ÖT‡&VbÂ&&÷WC¦&Ææ²"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†g&ÖT‡&VbÂ&&÷WC§7&6Fö2"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âG'VS°¢Ð ¢&WGW&âW&’åG'”7&VFR†g&ÖT‡&VbÂW&”¶–æBä'6öÇWFRÂ÷WBf"g&ÖUW&’’b`¢6÷'4†æFÆW"ä—56ÖT÷&–v–â†66W76÷%W&’Âg&ÖUW&’“°¢Ð ¢&—fFR&ööÂ6ä7W'&VçD6öçFW‡D66W74”g&ÖTFö7VÖVçB„VÆVÖVçB–g&ÖR¢°¢f"‡&VbÒ&W6öÇfT”g&ÖUv–æF÷t‡&Vb†–g&ÖRÂçVÆÂÂçVÆÂ“°¢&WGW&â—56ÖT÷&–v–äg&ÖT66W72†–g&ÖRÂ‡&VbÂö7W'&VçD&6UW&’“°¢Ð ¢&—fFR§5fÇVRvWDÖW76vU6÷W&6Tf÷%F&vWB„§5fÇVR6÷W&6Uv–æF÷rÂ§5fÇVRF&vWEv–æF÷r¢°¢–b‡6÷W&6Uv–æF÷råFrÒ§5fÇVUFräö&¦V7BÇÂF&vWEv–æF÷råFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&â6÷W&6Uv–æF÷s°¢Ð ¢f"6÷W&6Tg&ÖRÒ&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄVÆVÖVçCâ…&VD§5&÷W'G’‡6÷W&6Uv–æF÷rÂ%õöfVäg&ÖTVÆVÖVçB"’“°¢–b‡6÷W&6Tg&ÖRÒçVÆÂbbF&vWEv–æF÷räWVÇ2…öfVä§4vÆö&ÅF†—2’¢°¢f"6÷W&6T‡&VbÒ&W6öÇfT”g&ÖUv–æF÷t‡&Vb‡6÷W&6Tg&ÖRÂçVÆÂÂçVÆÂ“°¢&WGW&â—56ÖT÷&–v–äg&ÖT66W72‡6÷W&6Tg&ÖRÂ6÷W&6T‡&VbÂvWE&VçDFö7VÖVçEW&’‡6÷W&6Tg&ÖR’¢ò6÷W&6Uv–æF÷p¢¢vWD÷$7&VFT7&÷74÷&–v–ä6†–ÆEv–æF÷tf6FR‡6÷W&6Tg&ÖRÂ6÷W&6Uv–æF÷r“°¢Ð ¢f"F&vWDg&ÖRÒ&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄVÆVÖVçCâ…&VD§5&÷W'G’‡F&vWEv–æF÷rÂ%õöfVäg&ÖTVÆVÖVçB"’“°¢–b‡F&vWDg&ÖRÒçVÆÂbb6÷W&6Uv–æF÷räWVÇ2…öfVä§4vÆö&ÅF†—2’¢°¢f"F&vWD‡&VbÒ&W6öÇfT”g&ÖUv–æF÷t‡&Vb‡F&vWDg&ÖRÂçVÆÂÂçVÆÂ“°¢&WGW&â—56ÖT÷&–v–äg&ÖT66W72‡F&vWDg&ÖRÂF&vWD‡&VbÂvWE&VçDFö7VÖVçEW&’‡F&vWDg&ÖR’¢òvWD÷$7&VFU6ÖT÷&–v–åF÷v–æF÷tf6FR‚¢¢vWD÷$7&VFUF÷v–æF÷tf6FR‚“°¢Ð ¢&WGW&â6÷W&6Uv–æF÷s°¢Ð ¢&—fFRfö–B6WD”g&ÖUf–Ww÷'E&÷W'F–W2„§5fÇVRv–æF÷rÂVÆVÖVçB–g&ÖR¢°¢f"v–GF‚ÒG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†–g&ÖRÂ'v–GF‚"Â÷WBf"FV6Æ&VEv–GF‚¢òFV6Æ&VEv–GF€¢¢v–æF÷uv–GFƒ°¢f"†V–v‡BÒG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†–g&ÖRÂ&†V–v‡B"Â÷WBf"FV6Æ&VD†V–v‡B¢òFV6Æ&VD†V–v‡@¢¢v–æF÷t†V–v‡C°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&–ææW%v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&–ææW$†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&÷WFW%v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&÷WFW$†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ&FWf–6U—†VÅ&F–ò"Â§5fÇVRäg&öÔçVÖ&W"ƒ’“°¢f"67&öÆÂÒg&ÖU67&öÆÅ&VFW#òä–çfö¶R†–g&ÖR’óòƒBÂB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'vU„öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡67&öÆÂä—FVÓ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'vU”öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡67&öÆÂä—FVÓ"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'67&öÆÅ‚"Â§5fÇVRäg&öÔçVÖ&W"‡67&öÆÂä—FVÓ’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'67&öÆÅ’"Â§5fÇVRäg&öÔçVÖ&W"‡67&öÆÂä—FVÓ"’“°¢Ð ¢&—fFRfö–B&Vv—7FW$6†–ÆDg&ÖTöå&VçB„§5fÇVR&VçBÂ§5fÇVRv–æF÷rÂ7G&–ærg&ÖTæÖR¢°¢–b‡&VçBåFrÒ§5fÇVUFräö&¦V7BÇÂv–æF÷råFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢f"g&ÖW2Ò&VD§5&÷W'G’‡&VçBÂ&g&ÖW2"“°¢–b†g&ÖW2åFrÒ§5fÇVUFräö&¦V7B¢°¢g&ÖW2Ò&VçC°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçBÂ&g&ÖW2"Âg&ÖW2“°¢Ð ¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†g&ÖTæÖR’¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†g&ÖW2Âg&ÖTæÖRÂv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡&VçBÂg&ÖTæÖRÂv–æF÷r“°¢–b…öfVä§4vÆö&ÅF†—2åFrÓÒ§5fÇVUFräö&¦V7Bbb&VçBäWVÇ2…öfVä§4vÆö&ÅF†—2’¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…öfVä§4vÆö&ÅF†—2Âg&ÖTæÖRÂv–æF÷r“°¢Ð¢Ð ¢f"g&ÖT–æFW‚Ò&VD§5&÷W'G’‡v–æF÷rÂ%õöfVäg&ÖT–æFW‚"“°¢–b†g&ÖT–æFW‚åFrÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&ã°¢Ð ¢f"&VçDÆVæwF‚Ò&VD§5&÷W'G’†g&ÖW2Â&ÆVæwF‚"“°¢f"–æFW‚Ò&VçDÆVæwF‚åFrÓÒ§5fÇVUFräçVÖ&W ¢òÖF‚äÖ‚ƒÂ†–çB—&VçDÆVæwF‚ä4çVÖ&W"‚’¢¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ%õöfVäg&ÖT–æFW‚"Â§5fÇVRäg&öÔ–çC3"†–æFW‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†g&ÖW2Â–æFW‚åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’Âv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†g&ÖW2Â&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"†–æFW‚²’“°¢–b…öfVä§4vÆö&ÅF†—2åFrÓÒ§5fÇVUFräö&¦V7Bbb&VçBäWVÇ2…öfVä§4vÆö&ÅF†—2’¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…öfVä§4vÆö&ÅF†—2Â–æFW‚åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’Âv–æF÷r“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…öfVä§4vÆö&ÅF†—2Â&ÆVæwF‚"Â§5fÇVRäg&öÔ–çC3"†–æFW‚²’“°¢Ð¢Ð ¢&—fFR§5fÇVR7&VFUÆ–äÆö6F–öäö&¦V7B‡7G&–ær‡&Vb¢°¢f"'6VBÒ'6TfVä§5W&Â†æWuµÒ²§5fÇVRäg&öÕ7G&–ær†‡&Vbóò7G&–æräV×G’’Ò“°¢–b‡'6VBåFrÓÒ§5fÇVUFräö&¦V7B¢°¢&WGW&â'6VC°¢Ð ¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&‡&Vb%ÒÒ§5fÇVRäg&öÕ7G&–ær†‡&Vbóò7G&–æräV×G’’À¢²&÷&–v–â%ÒÒ§5fÇVRäg&öÕ7G&–ær‚&çVÆÂ"’À¢²'&÷Fö6öÂ%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²&†÷7B%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²&†÷7FæÖR%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²'÷'B%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²'F†æÖR%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²'6V&6‚%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²&†6‚%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’¢Ò“°¢Ð ¢&—fFRfö–BVWVUv–æF÷tÖW76vR€¢§5fÇVRF&vWEv–æF÷rÀ¢Æ—7CÄ'&÷w6W$WfVçDÆ—7FVæW#âÆ—7FVæW'2À¢§5fÇVRFFÀ¢§5fÇVR6÷W&6Uv–æF÷rÀ¢§5fÇVR÷'G2À¢7G&–ærF&vWD÷&–v–âÀ¢7G&–ærF&vWEv–æF÷t÷&–v–ä÷fW'&–FRÒçVÆÂÀ¢7G&–ær6÷W&6T÷&–v–ä÷fW'&–FRÒçVÆÂ¢°¢–b‚6†÷VÆDFVÆ—fW%v–æF÷tÖW76vR‡F&vWEv–æF÷rÂF&vWD÷&–v–âÂF&vWEv–æF÷t÷&–v–ä÷fW'&–FR’¢°¢&WGW&ã°¢Ð ¢f"÷&–v–âÒ6÷W&6T÷&–v–ä÷fW'&–FRóòvWDÖW76vU6÷W&6T÷&–v–â‡6÷W&6Uv–æF÷r“°¢f"W‡÷6VE6÷W&6Uv–æF÷rÒvWDÖW76vU6÷W&6Tf÷%F&vWB‡6÷W&6Uv–æF÷rÂF&vWEv–æF÷r“°¢f"6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öã°¢Æö6²…÷v–æF÷tÖW76vUVWVTÆö6²¢°¢÷v–æF÷tÖW76vTFVÆ—fW'•F–ÂÒ÷v–æF÷tÖW76vTFVÆ—fW'•F–Âä6öçF–çVUv—F‚€¢òÓà¢°¢–b‡6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öâ¢°¢&WGW&ã°¢Ð ¢G'¢°¢'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢W6–ær„7F—fFUv–æF÷t6ÆÆ&6´6öçFW‡B‡F&vWEv–æF÷rÂÆ—7FVæW'2’¢°¢f"WfVçEfÇVRÒ7&VFTÖW76vTWfVçEfÇVR†FFÂ÷&–v–âÂW‡÷6VE6÷W&6Uv–æF÷rÂF&vWEv–æF÷rÂ÷'G2“°¢f"†æFÆW"Ò&VD§5&÷W'G’‡F&vWEv–æF÷rÂ&öæÖW76vR"“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"ÂF&vWEv–æF÷rÂWfVçEfÇVRÂ&ÖW76vR"“°¢Ð ¢F—7F6„'&÷w6W$WfVçB†Æ—7FVæW'2Â&ÖW76vR"ÂF&vWEv–æF÷rÂWfVçEfÇVR“°¢–b‡F&vWEv–æF÷räWVÇ2…öfVä§4vÆö&ÅF†—2’¢°¢æ÷F–g”VÖ&VFFVDg&ÖW4öe&VçEv–æF÷tWfVçB‚&ÖW76vR"ÂWfVçEfÇVR“°¢Ð¢ö–çFW'&WFW"åV×Ö–7&÷F6·2‚“°¢&V6÷&DÖ–7&÷F6´6†V6·ö–çB‚'÷7DÖW76vR"“°¢Ð¢Ð ¢&WGW&âçVÆÃ°¢ÒÂv—Df÷%v÷&¶W$×3¢ÓÂ–ç7G'V7F–öä'VFvWC¢fVä§4'&÷w6W%F6´–ç7G'V7F–öä'VFvWB“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒ÷7DÖW76vRFVÆ—fW'’f–ÆVC¢¶W‚äÖW76vWÒ"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð ¢G'’²&WVW7E&VæFW#òä–çfö¶R‚“²Ð¢6F6‚²ò¢&VæFW"&WVW7B—2&W7BÖVff÷'B¢òÐ¢ÒÀ¢6æ6VÆÆF–öåFö¶VâäæöæRÀ¢F6´6öçF–çVF–öä÷F–öç2äæöæRÀ¢F6µ66†VGVÆW"äFVfVÇB“°¢Ð¢Ð ¢&—fFR&ööÂF—7F6„g&ÖUv–æF÷t†÷7DWfVçB€¢§5fÇVRg&ÖUv–æF÷rÀ¢Æ—7CÄ'&÷w6W$WfVçDÆ—7FVæW#âÆ—7FVæW'2À¢§5fÇVRWfVçEfÇVR¢°¢f"G—RÒ&VDWfVçEG—R†WfVçEfÇVR“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂg&ÖUv–æF÷r“°¢F—7F6„'&÷w6W$WfVçB†Æ—7FVæW'2ÂG—RÂg&ÖUv–æF÷rÂWfVçEfÇVR“° ¢f"†æFÆW"Ò&VD§5&÷W'G’†g&ÖUv–æF÷rÂ&öâ"²G—R“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"Âg&ÖUv–æF÷rÂWfVçEfÇVRÂG—R“°¢Ð ¢&WGW&â&VD§4&ööÅ&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"“°¢Ð ¢&—fFR§5fÇVR7&VFTÖW76vTWfVçEfÇVR€¢§5fÇVRFFÀ¢7G&–ær÷&–v–âÀ¢§5fÇVR6÷W&6Uv–æF÷rÀ¢§5fÇVRF&vWEv–æF÷rÀ¢§5fÇVR÷'G2¢°¢F÷EG&ç6fW'&VDÖW76vU÷'G2‡÷'G2ÂF&vWEv–æF÷r“°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‚&ÖW76vR"’À¢²&FF%ÒÒFFÀ¢²&÷&–v–â%ÒÒ§5fÇVRäg&öÕ7G&–ær†÷&–v–âóò7G&–æräV×G’’À¢²&Æ7DWfVçD–B%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’’À¢²'6÷W&6R%ÒÒ6÷W&6Uv–æF÷råFrÓÒ§5fÇVUFråVæFVf–æVBò§5fÇVRäçVÆÂ¢6÷W&6Uv–æF÷rÀ¢²'÷'G2%ÒÒ÷'G2åFrÓÒ§5fÇVUFräö&¦V7@¢ò÷'G0¢¢ö–çFW'&WFW"äÆÆö6FT'&’„'&’äV×G“Ä§5fÇVSâ‚’’À¢²'F&vWB%ÒÒF&vWEv–æF÷rÀ¢²&7W'&VçEF&vWB%ÒÒF&vWEv–æF÷rÀ¢²'7&4VÆVÖVçB%ÒÒF&vWEv–æF÷rÀ¢²&'V&&ÆW2%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’À¢²&6æ6VÆ&ÆR%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’À¢²&FVfVÇE&WfVçFVB%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’À¢²'F–ÖU7F×%ÒÒ§5fÇVRäg&öÔçVÖ&W"…öfVä§46Æö6²äVÆ6VBåF÷FÄÖ–ÆÆ—6V6öæG2¢Ò“°¢Ð ¢&—fFR&ööÂ6†÷VÆDFVÆ—fW%v–æF÷tÖW76vR€¢§5fÇVRF&vWEv–æF÷rÀ¢7G&–ærF&vWD÷&–v–âÀ¢7G&–ærF&vWEv–æF÷t÷&–v–ä÷fW'&–FRÒçVÆÂ¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡F&vWD÷&–v–â’ÇÀ¢7G&–æräWVÇ2‡F&vWD÷&–v–âÂ"¢"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&âG'VS°¢Ð ¢f"W‡V7FVD÷&–v–âÒF&vWEv–æF÷t÷&–v–ä÷fW'&–FRóò&VEv–æF÷t÷&–v–â‡F&vWEv–æF÷r“°¢&WGW&â7G&–æräWVÇ2€¢æ÷&ÖÆ—¦U÷7DÖW76vT÷&–v–â‡F&vWD÷&–v–â’À¢W‡V7FVD÷&–v–âÀ¢7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7G&–ærvWDÖW76vU6÷W&6T÷&–v–â„§5fÇVR6÷W&6Uv–æF÷r¢°¢–b‡6÷W&6Uv–æF÷råFrÓÒ§5fÇVUFräö&¦V7B¢°¢f"÷&–v–âÒ&VEv–æF÷t÷&–v–â‡6÷W&6Uv–æF÷r“°¢–b‚7G&–ærä—4çVÆÄ÷$V×G’†÷&–v–â’¢°¢&WGW&â÷&–v–ã°¢Ð¢Ð ¢&WGW&âvWD7W'&VçEv–æF÷t÷&–v–â‚“°¢Ð ¢&—fFR7G&–ær&VEv–æF÷t÷&–v–â„§5fÇVRv–æF÷ufÇVR¢°¢f"Æö6F–öâÒ&VD§5&÷W'G’‡v–æF÷ufÇVRÂ&Æö6F–öâ"“°¢f"÷&–v–âÒ&VD§5&÷W'G’†Æö6F–öâÂ&÷&–v–â"“°¢–b†÷&–v–âåFrÒ§5fÇVUFråVæFVf–æVBbb÷&–v–âåFrÒ§5fÇVUFräçVÆÂ¢°¢f"÷&–v–åFW‡BÒ6öW&6UFô†÷7E7G&–ær†÷&–v–â“°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†÷&–v–åFW‡B’b`¢7G&–æräWVÇ2†÷&–v–åFW‡BÂ'VæFVf–æVB"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&â÷&–v–åFW‡C°¢Ð¢Ð ¢f"‡&VbÒ&VD§5&÷W'G’†Æö6F–öâÂ&‡&Vb"“°¢–b†‡&VbåFrÓÒ§5fÇVUFråVæFVf–æVBÇÂ‡&VbåFrÓÒ§5fÇVUFräçVÆÂ¢°¢&WGW&âçVÆÃ°¢Ð ¢f"‡&VeFW‡BÒ6öW&6UFô†÷7E7G&–ær†‡&Vb“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†‡&VeFW‡B’ÇÀ¢7G&–æräWVÇ2†‡&VeFW‡BÂ'VæFVf–æVB"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&âçVÆÃ°¢Ð ¢&WGW&âæ÷&ÖÆ—¦U÷7DÖW76vT÷&–v–â†‡&VeFW‡B“°¢Ð ¢&—fFR7G&–ærvWD7W'&VçEv–æF÷t÷&–v–â‚¢°¢–b…ö7W'&VçD&6UW&’ÒçVÆÂ¢°¢&WGW&âæ÷&ÖÆ—¦U÷7DÖW76vT÷&–v–â…ö7W'&VçD&6UW&’ä'6öÇWFUW&’“°¢Ð ¢&WGW&â&çVÆÂ#°¢Ð ¢&—fFR7FF–27G&–æræ÷&ÖÆ—¦U÷7DÖW76vT÷&–v–â‡7G&–ærfÇVR¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡fÇVR’¢°¢&WGW&â7G&–æräV×G“°¢Ð ¢–b…W&’åG'”7&VFR‡fÇVRÂW&”¶–æBä'6öÇWFRÂ÷WBf"W&’’b`¢‡7G&–æräWVÇ2‡W&’å66†VÖRÂW&’åW&•66†VÖT‡GGÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡W&’å66†VÖRÂW&’åW&•66†VÖT‡GG2Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’’¢°¢&WGW&âW&’ävWDÆVgE'B…W&•'F–ÂäWF†÷&—G’“°¢Ð ¢&WGW&âfÇVS°¢Ð ¢&—fFRfö–BÇ”†—7F÷'•W&Â„fVä§4Æö6F–öä†÷7BÆö6F–öâÂ7G&–ærW&Â¢°¢–b†Æö6F–öâÓÒçVÆÂÇÂ7G&–ærä—4çVÆÄ÷%v†—FU76R‡W&Â’¢°¢&WGW&ã°¢Ð ¢–b…W&’åG'”7&VFR‡W&ÂÂW&”¶–æBä'6öÇWFRÂ÷WBf"'6öÇWFR’¢°¢WFFTfVä§4Æö6F–öâ†Æö6F–öâÂ'6öÇWFR“°¢&WGW&ã°¢Ð ¢–b†Æö6F–öâåW&’ÒçVÆÂbbW&’åG'”7&VFR†Æö6F–öâåW&’ÂW&ÂÂ÷WBf"&W6öÇfVB’¢°¢WFFTfVä§4Æö6F–öâ†Æö6F–öâÂ&W6öÇfVB“°¢Ð¢Ð ¢&—fFRfö–BWFFTfVä§4Æö6F–öâ„fVä§4Æö6F–öä†÷7BÆö6F–öâÂW&’W&’¢°¢–b†Æö6F–öâÓÒçVÆÂÇÂW&’ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢Æö6F–öâåW&’ÒW&“°¢ö7W'&VçD&6UW&’ÒW&“°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFU7G–ÆTö&¦V7B„VÆVÖVçBVÆVÖVçB¢°¢f"7F÷&RÒö†÷7E&÷W'G•7F÷&RävWD÷$7&VFUfÇVR†VÆVÖVçB’óòæWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚“°¢ö†÷7E&÷W'G•7F÷&RäFD÷%WFFR†VÆVÖVçBÂ7F÷&R“°¢–b‡7F÷&RåG'”vWEfÇVR‚%õöfVä§57G–ÆR"Â÷WBf"66†VB’¢&WGW&â66†VC° ¢f"7G–ÆTö&¢Òö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢òò775FW‡BvWGFW"÷6WGFW"—2v—&VBf–ö&¦V7BæFVf–æU&÷W'G’&VÆ÷rà¢Ò“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡7G–ÆTö&¢Â'6WE&÷W'G’"ÂvWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÂ'7G–ÆRç6WE&÷W'G’"À¢…òÂ&w2’Óà¢°¢f"&÷Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"fÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢òò775FW‡B6VçF–æVÃ¢&WÆ6RF†RVçF—&R–æÆ–æR7G–ÆRà¢–b‡&÷ÓÒ%õö775FW‡Eõò"¢°¢f"W†—7F–ærÒVÆVÖVçBävWDGG&–'WFR‚'7G–ÆR"’óò7G&–æräV×G“°¢–b†W†—7F–ærÒfÂ¢°¢VÆVÖVçBå6WDGG&–'WFR‚'7G–ÆR"ÂfÂ“°¢æ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢òò&WÆ6R÷"VæBF†R&÷W'G’–âF†RW†—7F–ær7G–ÆR7G&–ærà¢òòæWfW"VæBGWÆ–6FRFV6Æ&F–öç2(	BFVGWÆ–6FR'’&÷W'G’æÖRà¢f"7G–ÆTGG"ÒVÆVÖVçBävWDGG&–'WFR‚'7G–ÆR"’óò7G&–æräV×G“°¢&ööÂ&WÆ6VBÒfÇ6S°¢&ööÂ6†ævVBÒfÇ6S°¢f"6"ÒæWr7—7FVÒåFW‡Bå7G&–æt'V–ÆFW"‚“°¢f÷&V6‚‡f"FV6Â–â7G–ÆTGG"å7Æ—B‚s²rÂ7G&–æu7Æ—D÷F–öç2å&VÖ÷fTV×G”VçG&–W2’¢°¢f"G&–ÖÖVBÒFV6ÂåG&–Ò‚“°¢–b‡G&–ÖÖVBäÆVæwF‚ÓÒ’6öçF–çVS°¢f"6öÆöä–G‚ÒG&–ÖÖVBä–æFW„öb‚s¢r“°¢–b†6öÆöä–G‚Â’6öçF–çVS°¢f"æÖRÒG&–ÖÖVBå7V'7G&–ærƒÂ6öÆöä–G‚’åG&–Ò‚“°¢–b‡7G&–æräWVÇ2†æÖRÂ&÷Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢òò&WÆ6RF†RW†—7F–ærFV6Æ&F–öâv—F‚F†RæWrfÇVRà¢f"öÆEfÂÒG&–ÖÖVBå7V'7G&–ær†6öÆöä–G‚²’åG&–Ò‚“°¢–b‚7G&–æräWVÇ2†öÆEfÂÂfÂÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢6†ævVBÒG'VS°¢6"äVæB‡&÷’äVæB‚s¢r’äVæB‡fÂ’äVæB‚s²r“°¢&WÆ6VBÒG'VS°¢Ð¢VÇ6P¢°¢6"äVæB‡G&–ÖÖVB’äVæB‚s²r“°¢Ð¢Ð¢–b‚&WÆ6VB¢°¢òòæWr&÷W'G’(	BÇv—26†ævRà¢6"äVæB‡&÷’äVæB‚s¢r’äVæB‡fÂ’äVæB‚s²r“°¢6†ævVBÒG'VS°¢Ð¢f"æWu7G–ÆRÒ6"åFõ7G&–ær‚“°¢–b†6†ævVB¢°¢VÆVÖVçBå6WDGG&–'WFR‚'7G–ÆR"ÂæWu7G–ÆR“°¢æ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"’ÂVçVÖW&&ÆS¢G'VR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡7G–ÆTö&¢Â&vWE&÷W'G•fÇVR"ÂvWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÂ'7G–ÆRævWE&÷W'G•fÇVR"À¢…òÂ&w2’Óà¢°¢f"&÷Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"7G–ÆTGG"ÒVÆVÖVçBävWDGG&–'WFR‚'7G–ÆR"’óò7G&–æräV×G“°¢òò775FW‡B6VçF–æVÃ¢&WGW&âF†RgVÆÂ–æÆ–æR7G–ÆR7G&–ærà¢–b‡&÷ÓÒ%õö775FW‡Eõò"¢&WGW&â§5fÇVRäg&öÕ7G&–ær‡7G–ÆTGG"“°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡7G–ÆTGG"’ÇÂ7G&–ærä—4çVÆÄ÷$V×G’‡&÷’¢&WGW&â§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’“°¢òò'6RF†R–æÆ–æR7G–ÆRFòf–æBF†R&WVW7FVB&÷W'G’fÇVRà¢f÷&V6‚‡f"FV6Â–â7G–ÆTGG"å7Æ—B‚s²rÂ7G&–æu7Æ—D÷F–öç2å&VÖ÷fTV×G”VçG&–W2’¢°¢f"6öÆöä–G‚ÒFV6Âä–æFW„öb‚s¢r“°¢–b†6öÆöä–G‚Â’6öçF–çVS°¢f"æÖRÒFV6Âå7V'7G&–ærƒÂ6öÆöä–G‚’åG&–Ò‚“°¢–b‡7G&–æräWVÇ2†æÖRÂ&÷Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢&WGW&â§5fÇVRäg&öÕ7G&–ær†FV6Âå7V'7G&–ær†6öÆöä–G‚²’åG&–Ò‚’“°¢Ð¢&WGW&â§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’“°¢ÒÀ¢ÆVæwFƒ¢’ÂVçVÖW&&ÆS¢G'VR“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡7G–ÆTö&¢Â'&VÖ÷fU&÷W'G’"ÂvWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÂ'7G–ÆRç&VÖ÷fU&÷W'G’"À¢…òÂ&w2’Óà¢°¢f"&÷Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"7G–ÆTGG"ÒVÆVÖVçBävWDGG&–'WFR‚'7G–ÆR"’óò7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡7G–ÆTGG"’ÇÂ7G&–ærä—4çVÆÄ÷$V×G’‡&÷’¢&WGW&â§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’“°¢òò&VÖ÷fRF†R&÷W'G’g&öÒF†R7G–ÆR7G&–æræB&WGW&â—G2öÆBfÇVRà¢7G&–æröÆEfÇVRÒ7G&–æräV×G“°¢f"&VÖ–æ–ærÒæWr7—7FVÒåFW‡Bå7G&–æt'V–ÆFW"‚“°¢f÷&V6‚‡f"FV6Â–â7G–ÆTGG"å7Æ—B‚s²rÂ7G&–æu7Æ—D÷F–öç2å&VÖ÷fTV×G”VçG&–W2’¢°¢f"6öÆöä–G‚ÒFV6Âä–æFW„öb‚s¢r“°¢–b†6öÆöä–G‚Â’²&VÖ–æ–æräVæB†FV6Â’äVæB‚s²r“²6öçF–çVS²Ð¢f"æÖRÒFV6Âå7V'7G&–ærƒÂ6öÆöä–G‚’åG&–Ò‚“°¢–b‡7G&–æräWVÇ2†æÖRÂ&÷Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢öÆEfÇVRÒFV6Âå7V'7G&–ær†6öÆöä–G‚²’åG&–Ò‚“°¢Ð¢VÇ6P¢°¢&VÖ–æ–æräVæB†FV6ÂåG&–Ò‚’’äVæB‚s²r“°¢Ð¢Ð¢VÆVÖVçBå6WDGG&–'WFR‚'7G–ÆR"Â&VÖ–æ–æråFõ7G&–ær‚’“°¢æ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢&WGW&â§5fÇVRäg&öÕ7G&–ær†öÆEfÇVR“°¢ÒÀ¢ÆVæwFƒ¢’ÂVçVÖW&&ÆS¢G'VR“° ¢òò&Vv—7FW"F†R&r7G–ÆRö&¦V7BVæFW"FV×÷&'’vÆö&Â6òF†R¥0¢òò6æ—WB&VÆ÷r6âw&—Bv—F‚&÷W'G’f÷'v&F–ærà¢f"FV×7G–ÆTæÖRÒ%õöfVå7G–ÆUF×"²–çFW&Æö6¶VBä–æ7&VÖVçB‡&Vb÷FV×÷&'”fVä§4vÆö&Ä6÷VçFW"¢åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‡FV×7G–ÆTæÖRÂ7G–ÆTö&¢“°¢G'¢°¢òòFVf–æRvWGFW"÷6WGFW"f÷'v&F–ærf÷"F†RÖ÷7B6öÖÖöâ550¢òò&÷W'F–W26òF†BVÂç7G–ÆRæF—7Æ’Ò&&Æö6²&æ@¢òòVÂç7G–ÆRæ÷6—G–v÷&²v—F†÷WBvö–ærF‡&÷Vv‚6WE&÷W'G’‚’à¢f"775&÷2ÒæWuµÐ¢°¢&F—7Æ’"Â&÷6—G’"Â'f—6–&–Æ—G’"Â'v–GF‚"Â&†V–v‡B"À¢&Ö–åv–GF‚"Â&Ö–ä†V–v‡B"Â&Ö…v–GF‚"Â&Ö„†V–v‡B"À¢&6öÆ÷""Â&&6¶w&÷VæD6öÆ÷""Â&&6¶w&÷VæB"Â&&6¶w&÷VæD–ÖvR"À¢'÷6—F–öâ"Â'F÷"Â'&–v‡B"Â&&÷GFöÒ"Â&ÆVgB"À¢&Ö&v–â"Â&Ö&v–åF÷"Â&Ö&v–å&–v‡B"Â&Ö&v–ä&÷GFöÒ"Â&Ö&v–äÆVgB"À¢'FF–ær"Â'FF–æuF÷"Â'FF–æu&–v‡B"Â'FF–æt&÷GFöÒ"Â'FF–ætÆVgB"À¢&&÷&FW""Â&&÷&FW%F÷"Â&&÷&FW%&–v‡B"Â&&÷&FW$&÷GFöÒ"Â&&÷&FW$ÆVgB"À¢&&÷&FW%v–GF‚"Â&&÷&FW$6öÆ÷""Â&&÷&FW%&F—W2"À¢&föçE6—¦R"Â&föçDfÖ–Ç’"Â&föçEvV–v‡B"Â&föçE7G–ÆR"À¢&Æ–æT†V–v‡B"Â'FW‡DÆ–vâ"Â'FW‡DFV6÷&F–öâ"Â'FW‡EG&ç6f÷&Ò"À¢'¤–æFW‚"Â&÷fW&fÆ÷r"Â&÷fW&fÆ÷u‚"Â&÷fW&fÆ÷u’"À¢'G&ç6f÷&Ò"Â'G&ç6—F–öâ"Â&æ–ÖF–öâ"À¢&7W'6÷""Â'ö–çFW$WfVçG2"Â'W6W%6VÆV7B"À¢&&÷…6†F÷r"Â&&÷…6—¦–ær"À¢&fÆW‚"Â&fÆW„F—&V7F–öâ"Â&fÆW…w&"Â&§W7F–g”6öçFVçB"Â&Æ–vä—FV×2"Â&Æ–vä6öçFVçB"À¢&w&–EFV×ÆFT6öÇVÖç2"Â&w&–EFV×ÆFU&÷w2"Â&v"Â'&÷tv"Â&6öÇVÖäv"À¢'v†—FU76R"Â'v÷&D'&V²"Â'v÷&Ew&"À¢'fW'F–6ÄÆ–vâ"Â&ö&¦V7Df—B"Â&ö&¦V7E÷6—F–öâ"À¢&÷WFÆ–æR"Â&÷WFÆ–æUv–GF‚"Â&÷WFÆ–æT6öÆ÷" ¢Ó°¢f"6WE&÷§2Ò7G&–ærä¦ö–â‚""À¢775&÷2å6VÆV7B‡Óà¢°¢f"6ÖVÂÒ6ÖVÅFô775&÷‡“°¢&WGW&â$ö&¦V7BæFVf–æU&÷W'G’†vÆö&ÅF†—2â"²FV×7G–ÆTæÖR²"Âr"²²"rÇ²"°¢&vWC¦gVæ7F–öâ‚—·&WGW&âF†—2ævWE&÷W'G•fÇVR‚r"²6ÖVÂ²"r“·ÒÂ"°¢'6WC¦gVæ7F–öâ‡b—·F†—2ç6WE&÷W'G’‚r"²6ÖVÂ²"rÂrr·b“·ÒÂ"°¢&VçVÖW&&ÆS§G'VRÆ6öæf–wW&&ÆS§G'VWÒ“²#°¢Ò’“°¢òò775FW‡BvWGFW"÷6WGFW#¢&VF–ær&WGW&ç2F†RgVÆÂ–æÆ–æR7G–ÆR7G&–ærÀ¢òòw&—F–ær&WÆ6W2F†RVçF—&R–æÆ–æR7G–ÆRf–6WDGG&–'WFR‚w7G–ÆRrÂb’à¢6WE&÷§2³Ò$ö&¦V7BæFVf–æU&÷W'G’†vÆö&ÅF†—2â"²FV×7G–ÆTæÖR²"Âv775FW‡BrÇ²"°¢&vWC¦gVæ7F–öâ‚—·&WGW&âF†—2ævWE&÷W'G•fÇVR‚uõö775FW‡Eõòr“·ÒÂ"°¢'6WC¦gVæ7F–öâ‡b—·F†—2ç6WE&÷W'G’‚uõö775FW‡EõòrÂrr·b“·ÒÂ"°¢&VçVÖW&&ÆS§G'VRÆ6öæf–wW&&ÆS§G'VWÒ“²#°¢WfÇVFUv—F„fVä§5&r‡6WE&÷§2“°¢Ð¢f–æÆÇ¢°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‡FV×7G–ÆTæÖRÂ§5fÇVRåVæFVf–æVB“°¢Ð ¢7F÷&U²%õöfVä§57G–ÆR%ÒÒ7G–ÆTö&£°¢&WGW&â7G–ÆTö&£°¢Ð ¢&—fFRfö–Bæ÷F–g•&W6—¦Tö'6W'fW'2„VÆVÖVçBVÆVÖVçB¢°¢&Vg&W6„g&ÖU&VÆÕf–Ww÷'B†VÆVÖVçB“°¢f"æ÷F–g’Ò&VDvÆö&ÅfÇVT÷%VæFVf–æVB‚%õöfVäæ÷F–g•&W6—¦Tö'6W'fW'2"“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR†æ÷F–g’’¢°¢ö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ€¢æ÷F–g’À¢æWuµÒ²Fô†÷7D÷$çVÆÂ†VÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB’ÒÀ¢öfVä§4vÆö&ÅF†—2“°¢Ð¢Ð ¢&—fFRfö–B&Vg&W6„g&ÖU&VÆÕf–Ww÷'B„VÆVÖVçBg&ÖTVÆVÖVçB¢°¢–b‚—4”g&ÖTVÆVÖVçB†g&ÖTVÆVÖVçB’ÇÀ¢ö–g&ÖU&VÆ×2åG'”vWEfÇVR†g&ÖTVÆVÖVçBÂ÷WBf"g&ÖU&VÆÒ’¢°¢&WGW&ã°¢Ð ¢f"v–GF‚Ò&W6öÇfTg&ÖUf–Ww÷'DF–ÖVç6–öâ†g&ÖTVÆVÖVçBÂ'v–GF‚"Âv–æF÷uv–GF‚“°¢f"†V–v‡BÒ&W6öÇfTg&ÖUf–Ww÷'DF–ÖVç6–öâ†g&ÖTVÆVÖVçBÂ&†V–v‡B"Âv–æF÷t†V–v‡B“°¢f"&÷‡’ÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†g&ÖTVÆVÖVçBÂ%õöfVä–g&ÖT6öçFVçEv–æF÷r"“°¢–b‡&÷‡’åFrÓÒ§5fÇVUFräö&¦V7B¢°¢6WD”g&ÖUf–Ww÷'E&÷W'F–W2‡&÷‡’Âg&ÖTVÆVÖVçB“°¢Ð¢g&ÖU&VÆÒåWFFT÷væVEf–Ww÷'B‡v–GF‚Â†V–v‡B“°¢Ð ¢&—fFRfö–BWFFT÷væVEf–Ww÷'B†F÷V&ÆRv–GF‚ÂF÷V&ÆR†V–v‡B¢°¢WFFUf–Ww÷'DF–ÖVç6–öç2‡v–GF‚Â†V–v‡B“°¢Ð ¢&—fFRfö–BWFFUf–Ww÷'DF–ÖVç6–öç2†F÷V&ÆRv–GF‚ÂF÷V&ÆR†V–v‡B¢°¢f"6†ævVBÒfÇ6S°¢f"F—7F6…&W6—¦RÒfÇ6S°¢Æö6²…öfVä§4Æö6²¢°¢6†ævVBÒÖF‚ä'2…÷v–æF÷uv–GF‚Òv–GF‚’âãÇÂÖF‚ä'2…÷v–æF÷t†V–v‡BÒ†V–v‡B’âã°¢÷v–æF÷uv–GF‚Òv–GFƒ°¢÷v–æF÷t†V–v‡BÒ†V–v‡C° ¢–b…ö–çFW'&WFW"ÓÒçVÆÂÇÂöfVä§4vÆö&ÅF†—2åFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&–ææW%v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&–ææW$†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&÷WFW%v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&÷WFW$†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&FWf–6U—†VÅ&F–ò"Â§5fÇVRäg&öÔçVÖ&W"ƒ’“°¢–b…÷f—7VÅf–Ww÷'BåFrÓÒ§5fÇVUFräö&¦V7B¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…÷f—7VÅf–Ww÷'BÂ'v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’…÷f—7VÅf–Ww÷'BÂ&†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢Ð ¢f"67&VVâÒ&VDvÆö&ÅfÇVT÷%VæFVf–æVB‚'67&VVâ"“°¢–b‡67&VVâåFrÓÒ§5fÇVUFräö&¦V7B¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡67&VVâÂ'v–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡67&VVâÂ&†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡67&VVâÂ&f–Åv–GF‚"Â§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡67&VVâÂ&f–Ä†V–v‡B"Â§5fÇVRäg&öÔçVÖ&W"††V–v‡B’“°¢f"÷&–VçFF–öâÒ&VD§5&÷W'G’‡67&VVâÂ&÷&–VçFF–öâ"“°¢–b†÷&–VçFF–öâåFrÓÒ§5fÇVUFräö&¦V7B¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢÷&–VçFF–öâÀ¢'G—R"À¢§5fÇVRäg&öÕ7G&–ær‡v–GF‚ãÒ†V–v‡Bò&ÆæG66R×&–Ö'’"¢'÷'G&—B×&–Ö'’"’“°¢Ð¢Ð ¢F—7F6…&W6—¦RÒ6†ævVBbbö7W'&VçDFöÕ&ö÷BÒçVÆÃ°¢Ð ¢–b†F—7F6…&W6—¦R¢°¢VWVT÷væVEv–æF÷tWfVçB‚'&W6—¦R"“°¢Ð¢Ð ¢V&Æ–2fö–Bæ÷F–g”g&ÖU67&öÆÄ6†ævVB„VÆVÖVçBg&ÖTVÆVÖVçB¢°¢–b‚—4”g&ÖTVÆVÖVçB†g&ÖTVÆVÖVçB’¢°¢&WGW&ã°¢Ð ¢–b‚ö–g&ÖU&VÆ×2åG'”vWEfÇVR†g&ÖTVÆVÖVçBÂ÷WBf"g&ÖU&VÆÒ’¢°¢–b…G'”vWDg&ÖU&VÆÒ†g&ÖTVÆVÖVçBä÷væW$Fö7VÖVçBÂ÷WBf"÷væ–æu&VÆÒ’b`¢&VfW&Væ6TWVÇ2†÷væ–æu&VÆÒÂF†—2’¢°¢÷væ–æu&VÆÒäæ÷F–g”g&ÖU67&öÆÄ6†ævVB†g&ÖTVÆVÖVçB“°¢Ð¢&WGW&ã°¢Ð ¢f"67&öÆÂÒg&ÖU67&öÆÅ&VFW#òä–çfö¶R†g&ÖTVÆVÖVçB’óòƒBÂB“°¢Æö6²…öfVä§4Æö6²¢°¢f"&÷‡’ÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†g&ÖTVÆVÖVçBÂ%õöfVä–g&ÖT6öçFVçEv–æF÷r"“°¢–b‡&÷‡’åFrÓÒ§5fÇVUFräö&¦V7B¢°¢6WEv–æF÷u67&öÆÅ&÷W'F–W2‡&÷‡’Â67&öÆÂä—FVÓÂ67&öÆÂä—FVÓ"“°¢Ð¢Ð¢g&ÖU&VÆÒåWFFT÷væVE67&öÆÂ‡67&öÆÂä—FVÓÂ67&öÆÂä—FVÓ"“°¢Ð ¢&—fFRfö–BWFFT÷væVE67&öÆÂ†F÷V&ÆR‚ÂF÷V&ÆR’¢°¢f"6†ævVBÒfÇ6S°¢Æö6²…öfVä§4Æö6²¢°¢f"&Wf–÷W5‚Ò&VDvÆö&ÅfÇVT÷%VæFVf–æVB‚'67&öÆÅ‚"“°¢f"&Wf–÷W5’Ò&VDvÆö&ÅfÇVT÷%VæFVf–æVB‚'67&öÆÅ’"“°¢6†ævVBÒ&Wf–÷W5‚åFrÓÒ§5fÇVUFråVæFVf–æVBÇÂ&Wf–÷W5’åFrÓÒ§5fÇVUFråVæFVf–æVBÇÀ¢ÖF‚ä'2„6öW&6UFôf–æ—FTçVÖ&W"‡&Wf–÷W5‚Â’Ò‚’âãÇÀ¢ÖF‚ä'2„6öW&6UFôf–æ—FTçVÖ&W"‡&Wf–÷W5’Â’Ò’’âã°¢6WD÷væVE67&öÆÄvÆö&Ç2‡‚Â’“°¢Ð ¢–b†6†ævVB¢°¢VWVT÷væVEv–æF÷tWfVçB‚'67&öÆÂ"“°¢Ð¢Ð ¢&—fFRfö–B6WD÷væVE67&öÆÄvÆö&Ç2†F÷V&ÆR‚ÂF÷V&ÆR’¢°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'vU„öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡‚’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'vU”öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡’’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'67&öÆÅ‚"Â§5fÇVRäg&öÔçVÖ&W"‡‚’“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚'67&öÆÅ’"Â§5fÇVRäg&öÔçVÖ&W"‡’’“°¢–b…öfVä§4vÆö&ÅF†—2åFrÓÒ§5fÇVUFräö&¦V7B¢°¢6WEv–æF÷u67&öÆÅ&÷W'F–W2…öfVä§4vÆö&ÅF†—2Â‚Â’“°¢Ð¢Ð ¢&—fFRfö–B6WEv–æF÷u67&öÆÅ&÷W'F–W2„§5fÇVRv–æF÷rÂF÷V&ÆR‚ÂF÷V&ÆR’¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'vU„öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'vU”öfg6WB"Â§5fÇVRäg&öÔçVÖ&W"‡’’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'67&öÆÅ‚"Â§5fÇVRäg&öÔçVÖ&W"‡‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡v–æF÷rÂ'67&öÆÅ’"Â§5fÇVRäg&öÔçVÖ&W"‡’’“°¢Ð ¢&—fFRfö–BVWVT÷væVEv–æF÷tWfVçB‡7G&–ærG—R¢°¢f"6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öã°¢Æö6²…÷v–æF÷tÖW76vUVWVTÆö6²¢°¢÷v–æF÷tÖW76vTFVÆ—fW'•F–ÂÒ÷v–æF÷tÖW76vTFVÆ—fW'•F–Âä6öçF–çVUv—F‚€¢òÓà¢°¢–b‡6W76–öävVæW&F–öâÒöfVä§56W76–öävVæW&F–öâ¢°¢&WGW&ã°¢Ð ¢'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡G—R¢Ò“°¢F—7F6…v–æF÷t†÷7DWfVçB†WfVçEfÇVR“°¢–b‡7G&–æräWVÇ2‡G—RÂ'&W6—¦R"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢f"f—7VÄWfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡G—R¢Ò“°¢F—7F6…f—7VÅf–Ww÷'DWfVçB‡f—7VÄWfVçEfÇVR“°¢Ð¢ö–çFW'&WFW"åV×Ö–7&÷F6·2‚“°¢Ð¢&WGW&âçVÆÃ°¢Ò“°¢G'’²&WVW7E&VæFW#òä–çfö¶R‚“²Ò6F6‚²Ð¢ÒÀ¢6æ6VÆÆF–öåFö¶VâäæöæRÀ¢F6´6öçF–çVF–öä÷F–öç2äæöæRÀ¢F6µ66†VGVÆW"äFVfVÇB“°¢Ð¢Ð ¢&—fFR&ööÂF—7F6…f—7VÅf–Ww÷'DWfVçB„§5fÇVRWfVçEfÇVR¢°¢–b…÷f—7VÅf–Ww÷'BåFrÒ§5fÇVUFräö&¦V7BÇÂWfVçEfÇVRåFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&âG'VS°¢Ð ¢f"G—RÒ&VDWfVçEG—R†WfVçEfÇVR“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂ÷f—7VÅf–Ww÷'B“°¢F—7F6„'&÷w6W$WfVçB…÷f—7VÅf–Ww÷'DWfVçDÆ—7FVæW'2ÂG—RÂ÷f—7VÅf–Ww÷'BÂWfVçEfÇVR“° ¢f"†æFÆW"Ò&VD§5&÷W'G’…÷f—7VÅf–Ww÷'BÂ&öâ"²G—R“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"Â÷f—7VÅf–Ww÷'BÂWfVçEfÇVRÂG—R“°¢Ð ¢&WGW&â&VD§4&ööÅ&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"“°¢Ð ¢&—fFRfö–BGF6„fVä§5&÷F÷G—R„§5fÇVRF&vWBÂ7G&–ær6öç7G'V7F÷$æÖR¢°¢f"FV×æÖRÒ%õöfVåF×"²–çFW&Æö6¶VBä–æ7&VÖVçB‡&Vb÷FV×÷&'”fVä§4vÆö&Ä6÷VçFW"’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‡FV×æÖRÂF&vWB“°¢G'¢°¢WfÇVFUv—F„fVä§5&r‚B$ö&¦V7Bç6WE&÷F÷G—Töb†vÆö&ÅF†—2ç·FV×æÖWÒÂ¶6öç7G'V7F÷$æÖWÒç&÷F÷G—R“²"“°¢Ð¢f–æÆÇ¢°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‡FV×æÖRÂ§5fÇVRåVæFVf–æVB“°¢WfÇVFUv—F„fVä§5&r‚B&FVÆWFRvÆö&ÅF†—2ç·FV×æÖWÓ²"“°¢Ð¢Ð ¢&—fFRfö–BF—7F6…v–æF÷tÆöD†æFÆW'2‚¢°¢f"v–æF÷ufÇVRÒWfÇVFUv—F„fVä§5&r‚'v–æF÷r"“°¢f"ÆöEWF2ÒFFUF–ÖTöfg6WBåWF4æ÷råFõ7G&–ær‚$ò"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢WFFTWfVçDÆö÷6æ6†÷B‡6æ6†÷BÓà¢°¢6æ6†÷BäÆöDf—&VBÒG'VS°¢6æ6†÷BäÆöEWF2ÒÆöEWF3°¢Ò“°¢FDWfVçDÆö÷&V6÷&B‚$ÆöDf—&VB"Â'v–æF÷r"“°¢ÆötWfVçDÆö÷€¢$ÆöDf—&VB"À¢Æöu6WfW&—G’ä–æfòÀ¢%´fVä§4'&–FvUÒÆöBF—7F6†VB"À¢æWrF–7F–öæ'“Ç7G&–ærÂö&¦V7Cà¢°¢²&Fö7VÖVçE&VG•7FFR%ÒÒ&6ö×ÆWFR ¢Ò“°¢F—7F6„'&÷w6W$WfVçB„vWD7F—fUv–æF÷tWfVçDÆ—7FVæW'2‚’Â&ÆöB"Âv–æF÷ufÇVR“° ¢–b…G'•&VEv–æF÷tWfVçD†æFÆW"‡v–æF÷ufÇVRÂ&ÆöB"Â÷WBf"öæÆöB’b`¢ö–çFW'&WFW"ä6ä6ÆÅfÇVR†öæÆöB’¢°¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‚&ÆöB"’À¢²'F&vWB%ÒÒv–æF÷ufÇVRÀ¢²&7W'&VçEF&vWB%ÒÒv–æF÷ufÇVP¢Ò“°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²†öæÆöBÂv–æF÷ufÇVRÂWfVçEfÇVRÂ&ÆöB"“°¢Ð¢æ÷F–g”VÖ&VFFVDg&ÖW4öe&VçEv–æF÷tWfVçB‚&ÆöB"Â§5fÇVRåVæFVf–æVB“°¢Ð ¢&—fFRfö–B–çfö¶T&öG”öæÆöDGG&–'WFR„Fö7VÖVçBFö7VÖVçB¢°¢f"&öG’ÒFö7VÖVçBä&öG“°¢f"öæÆöBÒ&öG“òävWDGG&–'WFR‚&öæÆöB"“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†öæÆöB’¢°¢&WGW&ã°¢Ð ¢WFFTWfVçDÆö÷6æ6†÷B‡6æ6†÷BÓâ6æ6†÷Bä&öG”öæÆöDGG&–'WFTW†V7WFVBÒG'VR“°¢FDWfVçDÆö÷&V6÷&B‚$&öG”öæÆöDGG&–'WFTW†V7WFVB"Â&&öG’"“°¢f"&öG•fÇVRÒFô†÷7D÷$çVÆÂ†&öG’Â†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‚&ÆöB"’À¢²'F&vWB%ÒÒ&öG•fÇVRÀ¢²&7W'&VçEF&vWB%ÒÒ&öG•fÇVP¢Ò“° ¢–çfö¶TfVä§4–æÆ–æUv—F„WfVçB†öæÆöBÂWfVçEfÇVR“°¢Ð ¢òòòÇ7VÖÖ'“à¢òòò66ç2ÆÂVÆVÖVçG2–âF†RDôÒf÷"…DÔÂ–æÆ–æRWfVçBÖ†æFÆW"GG&–'WFW0¢òòò†öæ6Æ–6²Âöæ6†ævRÂöç7V&Ö—BÂWF2â’æB6ö×–ÆW2F†VÒ–çFò¥2gVæ7F–öç0¢òòò7F÷&VB–âF†R†÷7B&÷W'G’7F÷&R6òF—7F6„WfVçDf÷$VÆVÖVçB6â–çfö¶P¢òòòF†VÒv†VâF†R6÷'&W7öæF–æræF—fRWfVçBf—&W2à¢òòòÂ÷7VÖÖ'“à¢&—fFRfö–Bv—&T–æÆ–æTWfVçD†æFÆW'2„æöFRFöÕ&ö÷B¢°¢–b†FöÕ&ö÷BÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢òòWfVçB†æFÆW"GG&–'WFRæÖW2F†B6÷'&W7öæBFòDôÒWfVçG2à¢òòW†6ÇVFW2öæÆöB††æFÆVB6W&FVÇ’'’–çfö¶T&öG”öæÆöDGG&–'WFR’à¢f"WfVçDæÖW2ÒæWuµÐ¢°¢&6Æ–6²"Â&F&Æ6Æ–6²"Â&6öçFW‡FÖVçR"À¢&Ö÷W6VF÷vâ"Â&Ö÷W6WW"Â&Ö÷W6V÷fW""Â&Ö÷W6V÷WB"Â&Ö÷W6VÖ÷fR"À¢&¶W–F÷vâ"Â&¶W—W"Â&¶W—&W72"À¢'7V&Ö—B"Â'&W6WB"Â&6†ævR"Â&–çWB"À¢&fö7W2"Â&&ÇW""Â&fö7W6–â"Â&fö7W6÷WB"À¢'67&öÆÂ"Â'v†VVÂ"À¢&W'&÷""Â&&÷'B"À¢'F÷V6‡7F'B"Â'F÷V6†VæB"Â'F÷V6†Ö÷fR"Â'F÷V6†6æ6VÂ ¢Ó° ¢f"VÆVÖVçG2ÒFöÕ&ö÷BäFW66VæFçG2‚’äöeG—SÄVÆVÖVçCâ‚“°¢f÷&V6‚‡f"VÆVÖVçB–âVÆVÖVçG2¢°¢f÷&V6‚‡f"WfVçDæÖR–âWfVçDæÖW2¢°¢f"GG$æÖRÒ&öâ"²WfVçDæÖS°¢f"GG%fÇVRÒVÆVÖVçBävWDGG&–'WFR†GG$æÖR“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†GG%fÇVR’¢°¢6öçF–çVS°¢Ð ¢G'¢°¢Æö6²…öfVä§4Æö6²¢°¢òòw&F†RGG&–'WFRfÇVR–âgVæ7F–öâF†B&V6V—fW0¢òòWfVçF2—G2&ÖWFW"(	BÖF6†W2v†B&VÂ'&÷w6W'0¢òòFòf÷"–æÆ–æR†æFÆW'2à¢f"6÷W&6RÒæWr6÷W&6UFW‡B€¢"†gVæ7F–öâ†WfVçB—²"²GG%fÇVR²%ÆçÒ’"À¢#ÆfVæ'&÷w6W"Ö–æÆ–æRÖ†æFÆW#¢"²GG$æÖR²#â"“°¢f"6ö×–ÆVBÒö6ö×–ÆW"ä6ö×–ÆU67&—B‡6÷W&6R“°¢æWr'—FV6öFUfW&–f–W"‚’åfW&–g’†6ö×–ÆVB“°¢f"†æFÆW"Òö–çFW'&WFW"äW†V7WFR†6ö×–ÆVB“°¢òò†æFÆW"—2æ÷r6ÆÆ&ÆRgVæ7F–öã²7F÷&R—B6òF†@¢òòF—7F6„WfVçDf÷$VÆVÖVçB6âf–æB—Bf–¢òòvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ&öâ"²G—R’à¢6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂGG$æÖRÂ†æFÆW"“°¢Ð¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVäÆövvW"åv&â€¢B%´fVä§4'&–FvUÒf–ÆVBFòv—&R–æÆ–æR¶GG$æÖWÒ†æFÆW"öâ"°¢B#Ç¶VÆVÖVçBäÆö6ÄæÖWÓã¢¶W‚äÖW76vWÒ"À¢Æöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ð¢Ð¢Ð ¢&—fFRfö–BF—7F6„'&÷w6W$WfVçB€¢Æ—7CÄ'&÷w6W$WfVçDÆ—7FVæW#âÆ—7FVæW'2À¢7G&–ærG—RÀ¢§5fÇVR7W'&VçEF&vWBÀ¢§5fÇVRWfVçEfÇVRÒFVfVÇBÀ¢'&÷w6W$FöÔWfVçDF—7F6…7FFRF—7F6…7FFRÒçVÆÂ¢°¢–b†Æ—7FVæW'2ä6÷VçBÓÒ¢°¢&WGW&ã°¢Ð ¢f"6ÆÆ&6·2ÒæWrÆ—7CÄ'&÷w6W$WfVçDÆ—7FVæW#â‚“°¢f÷"‡f"’Ò²’ÂÆ—7FVæW'2ä6÷VçC²’²²¢°¢f"Æ—7FVæW"ÒÆ—7FVæW'5¶•Ó°¢–b‡7G&–æräWVÇ2†Æ—7FVæW"åG—RÂG—RÂ7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢6ÆÆ&6·2äFB†Æ—7FVæW"“°¢Ð¢Ð ¢–b†6ÆÆ&6·2ä6÷VçBÓÒ¢°¢&WGW&ã°¢Ð ¢f"7&VFVDWfVçEfÇVRÒfÇ6S°¢–b†WfVçEfÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢WfVçEfÇVRÒ7&VFT'&÷w6W$FöÔWfVçEfÇVR†çVÆÂÂG—RÂçVÆÂÂ÷WBF—7F6…7FFR“°¢7&VFVDWfVçEfÇVRÒG'VS°¢Ð ¢–b†7&VFVDWfVçEfÇVR¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'F&vWB"Â7W'&VçEF&vWB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'7&4VÆVÖVçB"Â7W'&VçEF&vWB“°¢Ð¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&7W'&VçEF&vWB"Â7W'&VçEF&vWB“° ¢f÷&V6‚‡f"Æ—7FVæW"–â6ÆÆ&6·2¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²†Æ—7FVæW"ä6ÆÆ&6²Â7W'&VçEF&vWBÂWfVçEfÇVRÂG—R“°¢–b†Æ—7FVæW"äöæ6R¢°¢Æ—7FVæW'2å&VÖ÷fTÆÂ†W†—7F–ærÓà¢7G&–æräWVÇ2†W†—7F–æråG—RÂÆ—7FVæW"åG—RÂ7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢W†—7F–ærä6GW&RÓÒÆ—7FVæW"ä6GW&Rb`¢W†—7F–ærä6ÆÆ&6²äWVÇ2†Æ—7FVæW"ä6ÆÆ&6²’“°¢Ð¢Ð¢Ð ¢&—fFR&ööÂF—7F6„Fö7VÖVçD†÷7DWfVçB„Fö7VÖVçBFö7VÖVçBÂ§5fÇVRWfVçEfÇVR¢°¢–b†Fö7VÖVçBÓÒçVÆÂ¢°¢&WGW&âG'VS°¢Ð ¢f"G—RÒ&VDWfVçEG—R†WfVçEfÇVR“°¢f"F&vWBÒFô†÷7D÷$çVÆÂ†Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂF&vWB“°¢F—7F6„'&÷w6W$WfVçB…öFö7VÖVçDWfVçDÆ—7FVæW'2ÂG—RÂF&vWBÂWfVçEfÇVR“°¢&WGW&â&VD§4&ööÅ&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"“°¢Ð ¢&—fFR&ööÂF—7F6…v–æF÷t†÷7DWfVçB„§5fÇVRWfVçEfÇVR¢°¢f"G—RÒ&VDWfVçEG—R†WfVçEfÇVR“°¢f"F&vWBÒvWD7F—fUv–æF÷tWfVçEF&vWB‚“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂF&vWB“°¢F—7F6„'&÷w6W$WfVçB„vWD7F—fUv–æF÷tWfVçDÆ—7FVæW'2‚’ÂG—RÂF&vWBÂWfVçEfÇVR“°¢æ÷F–g”VÖ&VFFVDg&ÖW4öe&VçEv–æF÷tWfVçB‡G—RÂWfVçEfÇVR“° ¢–b…G'•&VEv–æF÷tWfVçD†æFÆW"‡F&vWBÂG—RÂ÷WBf"†æFÆW"’b`¢ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"ÂF&vWBÂWfVçEfÇVRÂG—R“°¢Ð ¢&WGW&â&VD§4&ööÅ&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"“°¢Ð ¢&—fFR&ööÂG'•&VEv–æF÷tWfVçD†æFÆW"„§5fÇVRF&vWBÂ7G&–ærG—RÂ÷WB§5fÇVR†æFÆW"¢°¢†æFÆW"Ò§5fÇVRåVæFVf–æVC°¢–b‡F&vWBåFrÓÒ§5fÇVUFräö&¦V7B¢°¢†æFÆW"Ò&VD§5&÷W'G’‡F&vWBÂ&öâ"²G—R“°¢–b††æFÆW"åFrÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&âG'VS°¢Ð¢Ð ¢&WGW&âö–çFW'&WFW"åG'•&VDvÆö&ÅfÇVR‚&öâ"²G—RÂ÷WB†æFÆW"“°¢Ð ¢&—fFR&ööÂF—7F6„VÆVÖVçD†÷7DWfVçB„VÆVÖVçBVÆVÖVçBÂ§5fÇVRWfVçEfÇVR¢°¢–b†VÆVÖVçBÓÒçVÆÂ¢°¢&WGW&âG'VS°¢Ð ¢f"G—RÒ&VDWfVçEG—R†WfVçEfÇVR“°¢f"F&vWBÒFô†÷7D÷$çVÆÂ†VÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂF&vWB“° ¢òòW6RgVÆÂF—7F6‚v—F‚6GW&Rö'V&&ÆR†6W2â72çVÆÂf÷"F—7F6…7FFP¢òò&V6W6R¥2ÖF—7F6†VBWfVçG2G&6²&÷vF–öâf–÷&÷vF–öå7F÷VBöà¢òòF†RWfVçBö&¦V7B—G6VÆb‡6WB'’WfVçBç&÷F÷G—Rç7F÷&÷vF–öâ’à¢&WGW&âF—7F6„VÆVÖVçDWfVçEv—F„7F—fF–öâ†VÆVÖVçBÂG—RÂWfVçEfÇVRÂF—7F6…7FFS¢çVÆÂ“°¢Ð ¢&—fFR&ööÂF—7F6„VÆVÖVçDWfVçEv—F„7F—fF–öâ€¢VÆVÖVçBVÆVÖVçBÀ¢7G&–ærG—RÀ¢§5fÇVRWfVçEfÇVRÀ¢'&÷w6W$FöÔWfVçDF—7F6…7FFRF—7F6…7FFR¢°¢–b‚7G&–æräWVÇ2‡G—RÂ&6Æ–6²"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢—46†V6¶&÷„–çWDVÆVÖVçB†VÆVÖVçB’ÇÀ¢VÆVÖVçBä†4GG&–'WFR‚&F—6&ÆVB"’¢°¢&WGW&âF—7F6„WfVçDgVÆÂ†VÆVÖVçBÂG—RÂWfVçEfÇVRÂF—7F6…7FFR“°¢Ð ¢f"&Wf–÷W46†V6¶VBÒVÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rä—46†V6¶VB†VÆVÖVçB“°¢VÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rå6WD6†V6¶VB†VÆVÖVçBÂ&Wf–÷W46†V6¶VB“° ¢f"FVfVÇDÆÆ÷vVBÒF—7F6„WfVçDgVÆÂ†VÆVÖVçBÂG—RÂWfVçEfÇVRÂF—7F6…7FFR“°¢–b‚FVfVÇDÆÆ÷vVB¢°¢VÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rå6WD6†V6¶VB†VÆVÖVçBÂ&Wf–÷W46†V6¶VB“°¢&WGW&âfÇ6S°¢Ð ¢F—7F6„6†V6¶&÷…7FFTWfVçB†VÆVÖVçBÂ&–çWB"“°¢F—7F6„6†V6¶&÷…7FFTWfVçB†VÆVÖVçBÂ&6†ævR"“°¢&WGW&âG'VS°¢Ð ¢&—fFRfö–BF—7F6„6†V6¶&÷…7FFTWfVçB„VÆVÖVçBVÆVÖVçBÂ7G&–ærG—R¢°¢f"WfVçEfÇVRÒ7&VFT'&÷w6W$FöÔWfVçEfÇVR€¢VÆVÖVçBÀ¢G—RÀ¢æWr'&÷w6W$FöÔWfVçD–æ—@¢°¢'V&&ÆW2ÒG'VRÀ¢6æ6VÆ&ÆRÒfÇ6RÀ¢6ö×÷6VBÒG'VRÀ¢—5G'W7FVBÒG'VP¢ÒÀ¢÷WBf"F—7F6…7FFR“°¢òÒF—7F6„WfVçDgVÆÂ†VÆVÖVçBÂG—RÂWfVçEfÇVRÂF—7F6…7FFR“°¢Ð ¢&—fFR7G&–ær&VDWfVçEG—R„§5fÇVRWfVçEfÇVR¢°¢–b†WfVçEfÇVRåFrÓÒ§5fÇVUFråVæFVf–æVBÇÂWfVçEfÇVRåFrÓÒ§5fÇVUFräçVÆÂ¢°¢F‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$f–ÆVBFòW†V7WFRvF—7F6„WfVçBs¢&ÖWFW"—2æ÷BöbG—RtWfVçBrâ"“°¢Ð ¢f"G—UfÇVRÒ&VD§5&÷W'G’†WfVçEfÇVRÂ'G—R"“°¢f"G—RÒG—UfÇVRåFrÓÒ§5fÇVUFråVæFVf–æVBÇÂG—UfÇVRåFrÓÒ§5fÇVUFräçVÆÀ¢ò7G&–æräV×G¢¢6öW&6UFô†÷7E7G&–ær‡G—UfÇVR“°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡G—R’¢°¢F‡&÷tFöÔW†6WF–öâ‚$–çfÆ–E7FFTW'&÷""Â$f–ÆVBFòW†V7WFRvF—7F6„WfVçBs¢WfVçBG—R—2V×G’â"“°¢Ð ¢&WGW&âG—S°¢Ð ¢&—fFRfö–B&W&TF—7F6†VDWfVçB„§5fÇVRWfVçEfÇVRÂ§5fÇVRF&vWB¢°¢–b†WfVçEfÇVRåFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢f"7W'&VçEF&vWBÒ&VD§5&÷W'G’†WfVçEfÇVRÂ'F&vWB"“°¢–b†7W'&VçEF&vWBåFrÓÒ§5fÇVUFråVæFVf–æVBÇÂ7W'&VçEF&vWBåFrÓÒ§5fÇVUFräçVÆÂ¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'F&vWB"ÂF&vWB“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'7&4VÆVÖVçB"ÂF&vWB“°¢Ð ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&7W'&VçEF&vWB"ÂF&vWB“°¢Ð ¢&—fFR§5fÇVR7&VFT'&÷w6W$FöÔWfVçEfÇVR€¢VÆVÖVçBVÆVÖVçBÀ¢7G&–ærG—RÀ¢'&÷w6W$FöÔWfVçD–æ—BWfVçD–æ—BÀ¢÷WB'&÷w6W$FöÔWfVçDF—7F6…7FFRF—7F6…7FFR¢°¢WfVçD–æ—BóóÒæWr'&÷w6W$FöÔWfVçD–æ—B‚“°¢f"7FFRÒæWr'&÷w6W$FöÔWfVçDF—7F6…7FFR‚“°¢f"F&vWBÒVÆVÖVçBÓÒçVÆÂò§5fÇVRäçVÆÂ¢Fô†÷7D÷$çVÆÂ†VÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢f"&VÆFVEF&vWBÒWfVçD–æ—Bå&VÆFVEF&vWBÓÒçVÆÀ¢ò§5fÇVRäçVÆÀ¢¢Fô†÷7D÷$çVÆÂ†WfVçD–æ—Bå&VÆFVEF&vWBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡G—Róò7G&–æräV×G’’À¢²'F&vWB%ÒÒF&vWBÀ¢²&7W'&VçEF&vWB%ÒÒF&vWBÀ¢²'7&4VÆVÖVçB%ÒÒF&vWBÀ¢²'&VÆFVEF&vWB%ÒÒ&VÆFVEF&vWBÀ¢²&'V&&ÆW2%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä'V&&ÆW2’À¢²&6æ6VÆ&ÆR%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä6æ6VÆ&ÆR’À¢²&6ö×÷6VB%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä6ö×÷6VB’À¢²&—5G'W7FVB%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä—5G'W7FVB’À¢²&FVfVÇE&WfVçFVB%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’À¢²&6Æ–VçE‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bä6Æ–VçE‚’À¢²&6Æ–VçE’%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bä6Æ–VçE’’À¢²'vU‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—BåvU‚’À¢²'vU’%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—BåvU’’À¢²'67&VVå‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bå67&VVå‚’À¢²'67&VVå’%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bå67&VVå’’À¢²'‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bä6Æ–VçE‚’À¢²'’%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bä6Æ–VçE’’À¢²&'WGFöâ%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Bä'WGFöâ’À¢²&'WGFöç2%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Bä'WGFöç2’À¢²&FVÇF‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—BäFVÇF‚’À¢²&FVÇF’%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—BäFVÇF’’À¢²&FVÇF¢%ÒÒ§5fÇVRäg&öÔçVÖ&W"ƒ’À¢²&FVÇFÖöFR%ÒÒ§5fÇVRäg&öÔ–çC3"ƒ’À¢²'ö–çFW$–B%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Båö–çFW$–B’À¢²'ö–çFW%G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡7G&–ærä—4çVÆÄ÷%v†—FU76R†WfVçD–æ—Båö–çFW%G—R’ò&Ö÷W6R"¢WfVçD–æ—Båö–çFW%G—R’À¢²'&W77W&R%ÒÒ§5fÇVRäg&öÔçVÖ&W"†WfVçD–æ—Bå&W77W&R’À¢²&—5&–Ö'’%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä—5&–Ö'’’À¢²&¶W’%ÒÒ§5fÇVRäg&öÕ7G&–ær†WfVçD–æ—Bä¶W’óò7G&–æräV×G’’À¢²&6öFR%ÒÒ§5fÇVRäg&öÕ7G&–ær†WfVçD–æ—Bä6öFRóò7G&–æräV×G’’À¢²'v†–6‚%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Bä¶W”6öFR’À¢²&¶W”6öFR%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Bä¶W”6öFR’À¢²&6†$6öFR%ÒÒ§5fÇVRäg&öÔ–çC3"†WfVçD–æ—Bä¶W”6öFR’À¢²&FF%ÒÒWfVçD–æ—BäFFÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†WfVçD–æ—BäFF’À¢²&–çWEG—R%ÒÒ§5fÇVRäg&öÕ7G&–ær†WfVçD–æ—Bä–çWEG—Róò7G&–æräV×G’’À¢²&—46ö×÷6–ær%ÒÒ§5fÇVRäg&öÔ&ööÆVâ†WfVçD–æ—Bä—46ö×÷6–ær’À¢²'F–ÖU7F×%ÒÒ§5fÇVRäg&öÔçVÖ&W"…öfVä§46Æö6²äVÆ6VBåF÷FÄÖ–ÆÆ—6V6öæG2¢Ò“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'&WfVçDFVfVÇB"Âö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'&WfVçDFVfVÇB"À¢…òÂò’Óà¢°¢–b†WfVçD–æ—Bä6æ6VÆ&ÆR¢°¢7FFRäFVfVÇE&WfVçFVBÒG'VS°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"Â§5fÇVRäg&öÔ&ööÆVâ‡G'VR’“°¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'7F÷&÷vF–öâ"Âö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'7F÷&÷vF–öâ"À¢…òÂò’Óà¢°¢7FFRå7F÷&÷vF–öâÒG'VS°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&6æ6VÄ'V&&ÆR"Â§5fÇVRäg&öÔ&ööÆVâ‡G'VR’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ'7F÷–ÖÖVF–FU&÷vF–öâ"Âö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'7F÷–ÖÖVF–FU&÷vF–öâ"À¢…òÂò’Óà¢°¢7FFRå7F÷&÷vF–öâÒG'VS°¢7FFRå7F÷–ÖÖVF–FU&÷vF–öâÒG'VS°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&6æ6VÄ'V&&ÆR"Â§5fÇVRäg&öÔ&ööÆVâ‡G'VR’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&6ö×÷6VEF‚"Âö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&6ö×÷6VEF‚"À¢…òÂò’Óâö–çFW'&WFW"äÆÆö6FT'&’„'V–ÆD6ö×÷6VEF…fÇVW2†VÆVÖVçB’’À¢ÆVæwFƒ¢’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†WfVçEfÇVRÂ&6æ6VÄ'V&&ÆR"Â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R’“° ¢F—7F6…7FFRÒ7FFS°¢&WGW&âWfVçEfÇVS°¢Ð ¢&—fFR§5fÇVUµÒ'V–ÆD6ö×÷6VEF…fÇVW2„VÆVÖVçBVÆVÖVçB¢°¢–b†VÆVÖVçBÓÒçVÆÂ¢°¢&WGW&â'&’äV×G“Ä§5fÇVSâ‚“°¢Ð ¢f"F‚ÒæWrÆ—7CÄ§5fÇVSâ‚“°¢f"7W'&VçBÒVÆVÖVçC°¢v†–ÆR†7W'&VçBÒçVÆÂ¢°¢F‚äFB…Fô†÷7D÷$çVÆÂ†7W'&VçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB’“°¢7W'&VçBÒ7W'&VçBå&VçDVÆVÖVçC°¢Ð ¢f"Fö7VÖVçBÒVÆVÖVçBä÷væW$Fö7VÖVçC°¢–b†Fö7VÖVçBÒçVÆÂ¢°¢F‚äFB…Fô†÷7D÷$çVÆÂ†Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB’“°¢Ð ¢f"v–æF÷uF&vWBÒvWD7F—fUv–æF÷tWfVçEF&vWB‚“°¢–b‡v–æF÷uF&vWBåFrÒ§5fÇVUFråVæFVf–æVB¢°¢F‚äFB‡v–æF÷uF&vWB“°¢Ð ¢&WGW&âF‚åFô'&’‚“°¢Ð ¢&—fFRfö–B–çfö¶TfVä§46ÆÆ&6²„§5fÇVR6ÆÆ&6²Â§5fÇVRF†—5fÇVRÂ§5fÇVRWfVçEfÇVR¢°¢'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢f"&Wf–÷W4WfVçBÒö–çFW'&WFW"åG'•&VDvÆö&ÅfÇVR‚&WfVçB"Â÷WBf"W†—7F–ætWfVçB¢òW†—7F–ætWfVç@¢¢§5fÇVRåVæFVf–æVC°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&WfVçB"ÂWfVçEfÇVR“°¢G'¢°¢öfVä§4WfÇVF–öä6÷VçB²³°¢òÒö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ†6ÆÆ&6²ÂæWuµÒ²WfVçEfÇVRÒÂF†—5fÇVR“°¢Ð¢f–æÆÇ¢°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&WfVçB"Â&Wf–÷W4WfVçB“°¢Ð¢Ð ¢&WGW&âçVÆÃ°¢Ò“°¢Ð ¢&—fFR&ööÂG'”–çfö¶TfVä§4WfVçD6ÆÆ&6²„§5fÇVR6ÆÆ&6²Â§5fÇVRF†—5fÇVRÂ§5fÇVRWfVçEfÇVRÂ7G&–ærWfVçEG—R¢°¢f"–çfö¶VD6ÆÆ&6²Ò6ÆÆ&6³°¢G'¢°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR†6ÆÆ&6²’¢°¢–çfö¶TfVä§46ÆÆ&6²†6ÆÆ&6²ÂF†—5fÇVRÂWfVçEfÇVR“°¢Ð¢VÇ6P¢°¢f"†æFÆTWfVçBÒ&VD§5&÷W'G’†6ÆÆ&6²Â&†æFÆTWfVçB"“°¢–b‚ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆTWfVçB’¢°¢&WGW&âfÇ6S°¢Ð ¢–çfö¶VD6ÆÆ&6²Ò†æFÆTWfVçC°¢–çfö¶TfVä§46ÆÆ&6²††æFÆTWfVçBÂ6ÆÆ&6²ÂWfVçEfÇVR“°¢Ð ¢&WGW&âG'VS°¢Ð¢6F6‚„§5F‡&÷väW†6WF–öâW‚¢°¢òÒ&V6÷&DF–væ÷7F–46ÆÆ&6´f–ÇW&R€¢–çfö¶VD6ÆÆ&6²À¢F†—5fÇVRÀ¢æWuµÒ²WfVçEfÇVRÒÀ¢&WfVçBÖÆ—7FVæW""À¢WfVçEG—RÀ¢W‚“°¢f"FW67&—F–öâÒW‚äFW67&—F–öã°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†FW67&—F–öâ’¢°¢G'’²FW67&—F–öâÒö–çFW'&WFW#òäFW67&–&UF‡&÷våfÇVR†W‚åfÇVR“²Ò6F6‚²Ð¢Ð ¢Væv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒWfVçBÆ—7FVæW"f÷"w¶WfVçEG—Róò7G&–æräV×G—Òrf–ÆVC¢¶FW67&—F–öâóòW‚äÖW76vWÒ"À¢Æöt6FVv÷'’ä¦f67&—B“°¢&WGW&âfÇ6S°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢òÒ&V6÷&DF–væ÷7F–46ÆÆ&6´f–ÇW&R€¢–çfö¶VD6ÆÆ&6²À¢F†—5fÇVRÀ¢æWuµÒ²WfVçEfÇVRÒÀ¢&WfVçBÖÆ—7FVæW""À¢WfVçEG—RÀ¢W‚“°¢Væv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒWfVçBÆ—7FVæW"f÷"w¶WfVçEG—Róò7G&–æräV×G—Òrf–ÆVC¢¶W‚ävWEG—R‚’äæÖWÓ¢¶W‚äÖW76vWÒ"À¢Æöt6FVv÷'’ä¦f67&—B“°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFRfö–B–çfö¶TfVä§4–æÆ–æUv—F„WfVçB‡7G&–ær67&—BÂ§5fÇVRWfVçEfÇVR¢°¢'VäfVä§5v—F„Æ&vU7F6³Æö&¦V7Câ‚‚’Óà¢°¢Æö6²…öfVä§4Æö6²¢°¢f"&Wf–÷W4WfVçBÒö–çFW'&WFW"åG'•&VDvÆö&ÅfÇVR‚&WfVçB"Â÷WBf"W†—7F–ætWfVçB¢òW†—7F–ætWfVç@¢¢§5fÇVRåVæFVf–æVC°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&WfVçB"ÂWfVçEfÇVR“°¢G'¢°¢öfVä§4WfÇVF–öä6÷VçB²³°¢f"gVæ7F–öâÒö6ö×–ÆW"ä6ö×–ÆU67&—B†æWr6÷W&6UFW‡B‡67&—BÂ#ÆfVæ'&÷w6W"ÖfVæ§2Ö–æÆ–æRÖ†æFÆW#â"’“°¢æWr'—FV6öFUfW&–f–W"‚’åfW&–g’†gVæ7F–öâ“°¢òÒö–çFW'&WFW"äW†V7WFR†gVæ7F–öâ“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢fVäÆövvW"åv&â€¢B%´fVä§4'&–FvUÒ–æÆ–æR¥2†æFÆW"f–ÆVC¢¶W‚ävWEG—R‚’äæÖWÓ¢¶W‚äÖW76vWÒ"À¢Æöt6FVv÷'’ä¦f67&—B“°¢Ð¢f–æÆÇ¢°¢ö–çFW'&WFW"å&Vv—7FW$vÆö&ÅfÇVR‚&WfVçB"Â&Wf–÷W4WfVçB“°¢Ð¢Ð ¢&WGW&âçVÆÃ°¢Ò“°¢Ð ¢&—fFRfö–B'6TWfVçDÆ—7FVæW$÷F–öç2„§5fÇVR÷F–öç2Â÷WB&ööÂ6GW&RÂ÷WB&ööÂöæ6R¢°¢6GW&RÒfÇ6S°¢öæ6RÒfÇ6S° ¢7v—F6‚†÷F–öç2åFr¢°¢66R§5fÇVUFråVæFVf–æVC ¢66R§5fÇVUFräçVÆÃ ¢&WGW&ã°¢66R§5fÇVUFrä&ööÆVã ¢6GW&RÒ÷F–öç2ä4&ööÆVâ‚“°¢&WGW&ã°¢66R§5fÇVUFrä–çC3# ¢6GW&RÒ÷F–öç2ä4–çC3"‚’Ò°¢&WGW&ã°¢66R§5fÇVUFräçVÖ&W# ¢6GW&RÒÖF‚ä'2†÷F–öç2ä4çVÖ&W"‚’’â°¢&WGW&ã°¢66R§5fÇVUFräö&¦V7C ¢6GW&RÒ&VD§4&ööÅ&÷W'G’†÷F–öç2Â&6GW&R"“°¢öæ6RÒ&VD§4&ööÅ&÷W'G’†÷F–öç2Â&öæ6R"“°¢&WGW&ã°¢Ð¢Ð ¢&—fFR§5fÇVRW†V7WFTG–æÖ–567&—DVÆVÖVçB„VÆVÖVçB67&—DVÆVÖVçB¢°¢–b‡67&—DVÆVÖVçBÓÒçVÆÂÇÀ¢7G&–æräWVÇ2‡67&—DVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð ¢f"67&—E&V6÷&BÒFDG–æÖ–567&—DÆöF–æu&V6÷&B‡67&—DVÆVÖVçB“°¢f"7&2Ò67&—DVÆVÖVçBävWDGG&–'WFR‚'7&2"“°¢f"F—66÷fW&VDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢F—66÷fW&VDf–VÆG5²&G–æÖ–2%ÒÒG'VS°¢Æöu67&—DÆöF–ær€¢%67&—DF—66÷fW&VB"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒG–æÖ–267&—BVÆVÖVçB6VVâ"À¢F—66÷fW&VDf–VÆG2“° ¢òò–æÆ–æR67&—C¢W†V7WFRFW‡D6öçFVçBF—&V7FÇ’à¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡7&2’¢°¢f"–æÆ–æT6öFRÒ67&—DVÆVÖVçBåFW‡D6öçFVçC°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†–æÆ–æT6öFR’¢°¢G&6U67&—E&VG’‡67&—E&V6÷&BÂ–æÆ–æT6öFRäÆVæwF‚Â&G–æÖ–2Ö–æÆ–æR"“°¢F—7F6…67&—DVÆVÖVçDWfVçB€¢67&—DVÆVÖVçBÀ¢W†V7WFTG–æÖ–567&—D6öFR‡67&—DVÆVÖVçBÂ67&—E&V6÷&BÂ–æÆ–æT6öFRÂ&G–æÖ–2Ö–æÆ–æR"ÂçVÆÂ’ò&ÆöB"¢&W'&÷""“°¢Ð¢VÇ6P¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂ&V×G’Ö–æÆ–æRÖ6öFR"“°¢Ð ¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢òòFF¢U$Ç26öçF–â–æÆ–æR6öFR(	BFV6öFRæBW†V7WFRF—&V7FÇ’à¢–b‡7&2å7F'G5v—F‚‚&FF¢"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢f"FF6öFRÒFV6öFTFFW&Â‡7&2“°¢–b†FF6öFRÒçVÆÂ¢°¢G&6U67&—E&VG’‡67&—E&V6÷&BÂFF6öFRäÆVæwF‚Â&G–æÖ–2ÖFF"“°¢F—7F6…67&—DVÆVÖVçDWfVçB€¢67&—DVÆVÖVçBÀ¢W†V7WFTG–æÖ–567&—D6öFR‡67&—DVÆVÖVçBÂ67&—E&V6÷&BÂFF6öFRÂ&G–æÖ–2ÖFF"ÂçVÆÂ’ò&ÆöB"¢&W'&÷""“°¢Ð¢VÇ6P¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂ&–çfÆ–BÖFF×W&Â"“°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢Ð ¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢òòW‡FW&æÂ67&—C¢fWF6‚æBW†V7WFRà¢–b‚ÆÆ÷tW‡FW&æÅ67&—G2ÇÂ6æF&÷‚äÆÆ÷w2…6æF&÷„fVGW&RäW‡FW&æÅ67&—G2’¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂB&W‡FW&æÂÖF—6&ÆVC¦ÆÆ÷tW‡FW&æÃ×´ÆÆ÷tW‡FW&æÅ67&—G7Ó·6æF&÷ƒ×µ6æF&÷‚äÆÆ÷w2…6æF&÷„fVGW&RäW‡FW&æÅ67&—G2—Ò"“°¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢f"&6UW&’Òö7W'&VçD&6UW&“°¢–b†&6UW&’ÓÒçVÆÂÇÂW&’åG'”7&VFR†&6UW&’Â7&2Â÷WBf"67&—EW&’’¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂB'Vç&W6öÇf&ÆR×7&3¦&6UW&“×¶&6UW&—Ò"“°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢–b…7V'&W6÷W&6TÆÆ÷vVBÒçVÆÂbb7V'&W6÷W&6TÆÆ÷vVB‡67&—EW&’Â'67&—B"’¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂB&77Ö&Æö6³§·67&—EW&—Ò"“°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢G'¢°¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäfWF6…7F'FVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bå&W6öÇfVEW&ÂÒ67&—EW&’ä'6öÇWFUW&“°¢&V6÷&Bå7FGW2Ò&fWF6‚×7F'FVB#°¢Ò“°¢f"fWF6…7F'FVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢fWF6…7F'FVDf–VÆG5²'W&Â%ÒÒ67&—EW&’ä'6öÇWFUW&“°¢fWF6…7F'FVDf–VÆG5²&&F6‚%ÒÒ&G–æÖ–2#°¢Æöu67&—DÆöF–ær€¢%67&—DfWF6…7F'FVB"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒG–æÖ–2W‡FW&æÂ67&—BfWF6‚7F'FVB"À¢fWF6…7F'FVDf–VÆG2“° ¢7G&–ær6öFS°¢–b„W‡FW&æÅ67&—DfWF6†W"ÒçVÆÂ¢°¢6öFRÒW‡FW&æÅ67&—DfWF6†W"‡67&—EW&’Â&6UW&’’ävWDv—FW"‚’ävWE&W7VÇB‚“°¢Ð¢VÇ6R–b„fWF6„÷fW'&–FRÒçVÆÂ¢°¢6öFRÒfWF6„÷fW'&–FR‡67&—EW&’’ävWDv—FW"‚’ävWE&W7VÇB‚“°¢Ð¢VÇ6P¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂ&fWF6†W"ÖÖ—76–ær"“°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäfWF6„6ö×ÆWFVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bä6öFTÆVæwF‚Ò6öFSòäÆVæwF‚óò°¢&V6÷&Bå7FGW2Ò'&VG’#°¢Ò“°¢f"fWF6„6ö×ÆWFVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢fWF6„6ö×ÆWFVDf–VÆG5²'W&Â%ÒÒ67&—EW&’ä'6öÇWFUW&“°¢fWF6„6ö×ÆWFVDf–VÆG5²&&F6‚%ÒÒ&G–æÖ–2#°¢fWF6„6ö×ÆWFVDf–VÆG5²&6öFTÆVæwF‚%ÒÒ6öFSòäÆVæwF‚óò°¢Æöu67&—DÆöF–ær€¢%67&—DfWF6„6ö×ÆWFVB"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒG–æÖ–2W‡FW&æÂ67&—BfWF6‚6ö×ÆWFVB"À¢fWF6„6ö×ÆWFVDf–VÆG2“° ¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†6öFR’¢°¢G&6U67&—E&VG’‡67&—E&V6÷&BÂ6öFRäÆVæwF‚Â&G–æÖ–2"“°¢–b‚W†V7WFTG–æÖ–567&—D6öFR‡67&—DVÆVÖVçBÂ67&—E&V6÷&BÂ6öFRÂ&G–æÖ–2"Â67&—EW&’’¢°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð¢Ð¢VÇ6P¢°¢Ö&µ67&—E6¶—VB‡67&—E&V6÷&BÂ&V×G’Ö6öFR"“°¢Ð ¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&ÆöB"“°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäfWF6„f–ÆVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bå7FGW2Ò&fWF6‚Öf–ÆVB#°¢&V6÷&Bäf–ÇW&RÒW‚ävWEG—R‚’äæÖR²#¢"²W‚äÖW76vS°¢&V6÷&Bä6ö×ÆWFVEWF2ÒFFUF–ÖTöfg6WBåWF4æ÷råFõ7G&–ær‚$ò"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢Ò“°¢f"fWF6„f–ÆVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢fWF6„f–ÆVDf–VÆG5²'W&Â%ÒÒ67&—EW&’ä'6öÇWFUW&“°¢fWF6„f–ÆVDf–VÆG5²&&F6‚%ÒÒ&G–æÖ–2#°¢fWF6„f–ÆVDf–VÆG5²&W'&÷%G—R%ÒÒW‚ävWEG—R‚’äæÖS°¢fWF6„f–ÆVDf–VÆG5²&W'&÷"%ÒÒW‚äÖW76vS°¢Æöu67&—DÆöF–ær€¢%67&—DfWF6„f–ÆVB"À¢Æöu6WfW&—G’åv&âÀ¢%´fVä§4'&–FvUÒG–æÖ–267&—BfWF6‚f–ÆVB"À¢fWF6„f–ÆVDf–VÆG2“°¢F—7F6…67&—DVÆVÖVçDWfVçB‡67&—DVÆVÖVçBÂ&W'&÷""“°¢Ð ¢&WGW&âFô†÷7DæöFT÷$çVÆÂ‡67&—DVÆVÖVçB“°¢Ð ¢&—fFRfö–BG&6U67&—E&VG’„'&÷w6W%67&—DÆöF–æu&V6÷&B67&—E&V6÷&BÂ–çB6öFTÆVæwF‚Â7G&–ær&F6„Æ&VÂ¢°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bä6öFTÆVæwF‚Ò6öFTÆVæwFƒ°¢&V6÷&Bå7FGW2Ò'&VG’#°¢Ò“°¢f"&VG”f–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢&VG”f–VÆG5²&&F6‚%ÒÒ&F6„Æ&VÂóò7G&–æräV×G“°¢&VG”f–VÆG5²&6öFTÆVæwF‚%ÒÒ6öFTÆVæwFƒ°¢Æöu67&—DÆöF–ær€¢%67&—E&VG’"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒ67&—B&VG’"À¢&VG”f–VÆG2“°¢Ð ¢&—fFR&ööÂW†V7WFTG–æÖ–567&—D6öFR€¢VÆVÖVçB67&—DVÆVÖVçBÀ¢'&÷w6W%67&—DÆöF–æu&V6÷&B67&—E&V6÷&BÀ¢7G&–ær6öFRÀ¢7G&–ær&F6„Æ&VÂÀ¢W&’ÖöGVÆUW&’¢°¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäW†V7WF–öå7F'FVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bä&F6‚Ò&F6„Æ&VÂóò7G&–æräV×G“°¢&V6÷&Bä6öFTÆVæwF‚Ò6öFSòäÆVæwF‚óò&V6÷&Bä6öFTÆVæwFƒ°¢&V6÷&Bå7FGW2Ò&W†V7WF–öâ×7F'FVB#°¢Ò“°¢f"W†V7WF–öå7F'FVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢W†V7WF–öå7F'FVDf–VÆG5²&&F6‚%ÒÒ&F6„Æ&VÂóò7G&–æräV×G“°¢W†V7WF–öå7F'FVDf–VÆG5²'6÷W&6R%ÒÒ67&—E&V6÷&Còå&W6öÇfVEW&Âóò67&—E&V6÷&Còå7&2óò&–æÆ–æR#°¢W†V7WF–öå7F'FVDf–VÆG5²&—4ÖöGVÆR%ÒÒ7G&–æräWVÇ2‡67&—E&V6÷&Còä¶–æBÂ&ÖöGVÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢W†V7WF–öå7F'FVDf–VÆG5²&6öFTÆVæwF‚%ÒÒ6öFSòäÆVæwF‚óò°¢Æöu67&—DÆöF–ær€¢%67&—DW†V7WF–öå7F'FVB"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒG–æÖ–267&—BW†V7WF–öâ7F'FVB"À¢W†V7WF–öå7F'FVDf–VÆG2“° ¢6WD7W'&VçE67&—DVÆVÖVçB‡67&—DVÆVÖVçBÂ67&—E&V6÷&B“°¢G'¢°¢–b‡7G&–æräWVÇ2‡67&—E&V6÷&Còä¶–æBÂ&ÖöGVÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢WfÇVFTÖöGVÆUv—F„fVä§2†6öFRÂÖöGVÆUW&’“°¢Ð¢VÇ6P¢°¢WfÇVFUv—F„fVä§5&r†6öFR“°¢Ð ¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäW†V7WF–öä6ö×ÆWFVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bå7FGW2Ò&W†V7WFVB#°¢&V6÷&Bä6ö×ÆWFVEWF2ÒFFUF–ÖTöfg6WBåWF4æ÷råFõ7G&–ær‚$ò"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢Ò“°¢f"W†V7WF–öä6ö×ÆWFVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢W†V7WF–öä6ö×ÆWFVDf–VÆG5²&&F6‚%ÒÒ&F6„Æ&VÂóò7G&–æräV×G“°¢W†V7WF–öä6ö×ÆWFVDf–VÆG5²'6÷W&6R%ÒÒ67&—E&V6÷&Còå&W6öÇfVEW&Âóò67&—E&V6÷&Còå7&2óò&–æÆ–æR#°¢W†V7WF–öä6ö×ÆWFVDf–VÆG5²&—4ÖöGVÆR%ÒÒ7G&–æräWVÇ2‡67&—E&V6÷&Còä¶–æBÂ&ÖöGVÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Æöu67&—DÆöF–ær€¢%67&—DW†V7WF–öä6ö×ÆWFVB"À¢Æöu6WfW&—G’äFV'VrÀ¢%´fVä§4'&–FvUÒG–æÖ–267&—BW†V7WF–öâ6ö×ÆWFVB"À¢W†V7WF–öä6ö×ÆWFVDf–VÆG2“°¢&WGW&âG'VS°¢Ð¢6F6‚„§5F‡&÷väW†6WF–öâ§FR¢°¢f"FW62Ò§FRäFW67&—F–öã°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†FW62’¢°¢G'’²FW62Òö–çFW'&WFW"äFW67&–&UF‡&÷våfÇVR†§FRåfÇVR“²Ò6F6‚²Ð¢Ð ¢G&6TG–æÖ–567&—DW†V7WF–öäf–ÇW&R‡67&—E&V6÷&BÂ&F6„Æ&VÂÂ$§5F‡&÷väW†6WF–öâ"ÂFW62óò§FRäÖW76vR“°¢&V6÷&DÖ—76–ætvÆö&Å&VfW&Væ6R†FW62óò§FRäÖW76vRÂ67&—E&V6÷&BÂö7W'&VçD&6UW&’“°¢&WGW&âfÇ6S°¢Ð¢6F6‚„W†6WF–öâW‚¢°¢G&6TG–æÖ–567&—DW†V7WF–öäf–ÇW&R‡67&—E&V6÷&BÂ&F6„Æ&VÂÂW‚ävWEG—R‚’äæÖRÂW‚äÖW76vR“°¢&WGW&âfÇ6S°¢Ð¢f–æÆÇ¢°¢6WD7W'&VçE67&—DVÆVÖVçB†çVÆÂ“°¢Ð¢Ð ¢&—fFRfö–BG&6TG–æÖ–567&—DW†V7WF–öäf–ÇW&R€¢'&÷w6W%67&—DÆöF–æu&V6÷&B67&—E&V6÷&BÀ¢7G&–ær&F6„Æ&VÂÀ¢7G&–ærW'&÷%G—RÀ¢7G&–ærW'&÷"¢°¢WFFU67&—DÆöF–æu6æ6†÷B‡6æ6†÷BÓâ6æ6†÷BäW†V7WF–öäf–ÆVB²²“°¢WFFU67&—DÆöF–æu&V6÷&B‡67&—E&V6÷&BÂ&V6÷&BÓà¢°¢&V6÷&Bå7FGW2Ò&W†V7WF–öâÖf–ÆVB#°¢&V6÷&Bäf–ÇW&RÒW'&÷"óò7G&–æräV×G“°¢&V6÷&Bä6ö×ÆWFVEWF2ÒFFUF–ÖTöfg6WBåWF4æ÷råFõ7G&–ær‚$ò"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢Ò“°¢f"67&—Df–ÆVDf–VÆG2Ò7&VFU67&—E&V6÷&Df–VÆG2‡67&—E&V6÷&B“°¢67&—Df–ÆVDf–VÆG5²&&F6‚%ÒÒ&F6„Æ&VÂóò7G&–æräV×G“°¢67&—Df–ÆVDf–VÆG5²&÷&–v–â%ÒÒ67&—E&V6÷&Còå7&2óò&–æÆ–æR#°¢67&—Df–ÆVDf–VÆG5²&W'&÷%G—R%ÒÒW'&÷%G—Róò7G&–æräV×G“°¢67&—Df–ÆVDf–VÆG5²&W'&÷"%ÒÒW'&÷"óò7G&–æräV×G“°¢Æöu67&—DÆöF–ær€¢%67&—DW†V7WF–öäf–ÆVB"À¢Æöu6WfW&—G’äW'&÷"À¢%´fVä§4'&–FvUÒG–æÖ–267&—BW'&÷""À¢67&—Df–ÆVDf–VÆG2À¢ÆötÖ&¶W"äVæv–æT'Vr“°¢Ð ¢&—fFRfö–BF—7F6…67&—DVÆVÖVçDWfVçB„VÆVÖVçB67&—DVÆVÖVçBÂ7G&–ærG—R¢°¢f"WfVçEfÇVRÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'G—R%ÒÒ§5fÇVRäg&öÕ7G&–ær‡G—R’À¢²'F&vWB%ÒÒFô†÷7D÷$çVÆÂ‡67&—DVÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB’À¢²&7W'&VçEF&vWB%ÒÒFô†÷7D÷$çVÆÂ‡67&—DVÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB¢Ò“° ¢f"†æFÆW"ÒvWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB‡67&—DVÆVÖVçBÂ&öâ"²G—R“°¢–b…ö–çFW'&WFW"ä6ä6ÆÅfÇVR††æFÆW"’¢°¢G'”–çfö¶TfVä§4WfVçD6ÆÆ&6²††æFÆW"ÂFô†÷7D÷$çVÆÂ‡67&—DVÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB’ÂWfVçEfÇVRÂG—R“°¢Ð¢Ð ¢&—fFRfö–BW†V7WFT–æÆ–æU67&—G4g&öÔVÆVÖVçB„VÆVÖVçB&ö÷DVÆVÖVçB¢°¢–b‡&ö÷DVÆVÖVçBÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f÷&V6‚‡f"67&—DVÆVÖVçB–â&ö÷DVÆVÖVçBå6VÆdæDFW66VæFçG2‚’äöeG—SÄVÆVÖVçCâ‚’¢°¢–b‚7G&–æräWVÇ2‡67&—DVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢6öçF–çVS°¢Ð ¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R‡67&—DVÆVÖVçBävWDGG&–'WFR‚'7&2"’’¢°¢6öçF–çVS°¢Ð ¢f"6öFRÒ6öÆÆV7E67&—EFW‡B‡67&—DVÆVÖVçB“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†6öFR’¢°¢6öçF–çVS°¢Ð ¢6WD7W'&VçE67&—DVÆVÖVçB‡67&—DVÆVÖVçB“°¢G'¢°¢WfÇVFUv—F„fVä§5&r†6öFR“°¢Ð¢f–æÆÇ¢°¢6WD7W'&VçE67&—DVÆVÖVçB†çVÆÂ“°¢Ð¢Ð¢Ð ¢&—fFRfö–B–ç6W'DF¦6VçD‡FÖÂ„VÆVÖVçBæ6†÷"Â7G&–ær÷6—F–öâÂ7G&–ær‡FÖÂ¢°¢–b†æ6†÷"ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"g&vÖVçBÒ‡FÖÅ'6W"å'6Tg&vÖVçB†æ6†÷"Â‡FÖÂóò7G&–æräV×G’Â÷F–öç3¢çVÆÂÂ÷WBò“°¢f"–ç6W'FVE&ö÷G2ÒæWrÆ—7CÄVÆVÖVçCâ‚“°¢f"æ÷&ÖÆ—¦VE÷6—F–öâÒ‡÷6—F–öâóò7G&–æräV×G’’åG&–Ò‚’åFôÆ÷vW$–çf&–çB‚“° ¢fö–BG&6´–ç6W'FVB„æöFRæöFR¢°¢–b†æöFR—2VÆVÖVçBVÆVÖVçB¢°¢–ç6W'FVE&ö÷G2äFB†VÆVÖVçB“°¢Ð¢Ð ¢7v—F6‚†æ÷&ÖÆ—¦VE÷6—F–öâ¢°¢66R&gFW&&Vv–â# ¢°¢f"&VçBÒæ6†÷"26öçF–æW$æöFS°¢f"f—'7BÒæ6†÷"äf—'7D6†–ÆC°¢v†–ÆR†g&vÖVçBäf—'7D6†–ÆBÒçVÆÂ¢°¢f"æöFRÒg&vÖVçBäf—'7D6†–ÆC°¢g&vÖVçBå&VÖ÷fT6†–ÆB†æöFR“°¢&VçCòä–ç6W'D&Vf÷&R†æöFRÂf—'7B“°¢G&6´–ç6W'FVB†æöFR“°¢Ð¢'&V³°¢Ð¢66R&&Vf÷&VVæB# ¢°¢f"&VçBÒæ6†÷"26öçF–æW$æöFS°¢v†–ÆR†g&vÖVçBäf—'7D6†–ÆBÒçVÆÂ¢°¢f"æöFRÒg&vÖVçBäf—'7D6†–ÆC°¢g&vÖVçBå&VÖ÷fT6†–ÆB†æöFR“°¢&VçCòäVæD6†–ÆB†æöFR“°¢G&6´–ç6W'FVB†æöFR“°¢Ð¢'&V³°¢Ð¢66R&&Vf÷&V&Vv–â# ¢°¢f"&VçBÒæ6†÷"å&VçDæöFR26öçF–æW$æöFS°¢v†–ÆR†g&vÖVçBäf—'7D6†–ÆBÒçVÆÂ¢°¢f"æöFRÒg&vÖVçBäf—'7D6†–ÆC°¢g&vÖVçBå&VÖ÷fT6†–ÆB†æöFR“°¢&VçCòä–ç6W'D&Vf÷&R†æöFRÂæ6†÷"“°¢G&6´–ç6W'FVB†æöFR“°¢Ð¢'&V³°¢Ð¢66R&gFW&VæB# ¢°¢f"&VçBÒæ6†÷"å&VçDæöFR26öçF–æW$æöFS°¢f"æW‡BÒæ6†÷"äæW‡E6–&Æ–æs°¢v†–ÆR†g&vÖVçBäf—'7D6†–ÆBÒçVÆÂ¢°¢f"æöFRÒg&vÖVçBäf—'7D6†–ÆC°¢g&vÖVçBå&VÖ÷fT6†–ÆB†æöFR“°¢&VçCòä–ç6W'D&Vf÷&R†æöFRÂæW‡B“°¢G&6´–ç6W'FVB†æöFR“°¢Ð¢'&V³°¢Ð¢FVfVÇC ¢&WGW&ã°¢Ð ¢–b„W†V7WFT–æÆ–æU67&—G4öä–ææW$…DÔÂ¢°¢f÷&V6‚‡f"–ç6W'FVE&ö÷B–â–ç6W'FVE&ö÷G2¢°¢W†V7WFT–æÆ–æU67&—G4g&öÔVÆVÖVçB†–ç6W'FVE&ö÷B“°¢Ð¢Ð¢Ð ¢&—fFRfö–B÷VäFö7VÖVçDf÷%w&—FR„Fö7VÖVçBFö7VÖVçB¢°¢–b†Fö7VÖVçCòä&öG’—2æ÷B6öçF–æW$æöFR&öG’¢°¢&WGW&ã°¢Ð ¢v†–ÆR†&öG’äf—'7D6†–ÆBÒçVÆÂ¢°¢&öG’å&VÖ÷fT6†–ÆB†&öG’äf—'7D6†–ÆB“°¢Ð¢Ð ¢&—fFRfö–Bw&—FTFö7VÖVçDÖ&·W„Fö7VÖVçBFö7VÖVçBÂ•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2Â&ööÂVæDæWtÆ–æR¢°¢–b†Fö7VÖVçBÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"‡FÖÂÒ7G&–ærä6öæ6B†&w2å6VÆV7B„6öW&6UFô†÷7E7G&–ær’“°¢–b†VæDæWtÆ–æR¢°¢‡FÖÂ³ÒVçf—&öæÖVçBäæWtÆ–æS°¢Ð ¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†‡FÖÂ’¢°¢&WGW&ã°¢Ð ¢f"6öçFW‡DVÆVÖVçBÒFö7VÖVçBä&öG’óòFö7VÖVçBäFö7VÖVçDVÆVÖVçC°¢f"g&vÖVçBÒ‡FÖÅ'6W"å'6Tg&vÖVçB€¢6öçFW‡DVÆVÖVçBÀ¢‡FÖÂÀ¢æWr‡FÖÅ'6W$÷F–öç2²&6UW&’Òö7W'&VçD&6UW&’ÒÀ¢÷WBò“° ¢–b†g&vÖVçBä6†–ÆDæöFW2ÓÒçVÆÂÇÂg&vÖVçBä6†–ÆDæöFW2äÆVæwF‚ÓÒ¢°¢&WGW&ã°¢Ð ¢f"–ç6W'F–öå&VçBÒ&W6öÇfTFö7VÖVçEw&—FT–ç6W'F–öå&VçB†Fö7VÖVçBÂ÷WBf"&VfW&Væ6TæöFR“°¢–b†–ç6W'F–öå&VçBÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f÷&V6‚‡f"6†–ÆB–âg&vÖVçBä6†–ÆDæöFW2åFô'&’‚’¢°¢–b‡&VfW&Væ6TæöFRÒçVÆÂ¢°¢–ç6W'F–öå&VçBä–ç6W'D&Vf÷&R†6†–ÆBÂ&VfW&Væ6TæöFR“°¢Ð¢VÇ6P¢°¢–ç6W'F–öå&VçBäVæD6†–ÆB†6†–ÆB“°¢Ð¢Ð¢Ð ¢&—fFR6öçF–æW$æöFR&W6öÇfTFö7VÖVçEw&—FT–ç6W'F–öå&VçB„Fö7VÖVçBFö7VÖVçBÂ÷WBæöFR&VfW&Væ6TæöFR¢°¢&VfW&Væ6TæöFRÒçVÆÃ° ¢f"7W'&VçE67&—BÒvWD7W'&VçE67&—DVÆVÖVçB‚“°¢–b…&VfW&Væ6TWVÇ2†7W'&VçE67&—Còä÷væW$Fö7VÖVçBÂFö7VÖVçB’b`¢7W'&VçE67&—Bå&VçDæöFR—26öçF–æW$æöFR67&—E&VçB¢°¢&VfW&Væ6TæöFRÒ7W'&VçE67&—BäæW‡E6–&Æ–æs°¢&WGW&â67&—E&VçC°¢Ð ¢–b†Fö7VÖVçBä&öG’—26öçF–æW$æöFR&öG’¢°¢&WGW&â&öG“°¢Ð ¢–b†Fö7VÖVçBäFö7VÖVçDVÆVÖVçB—26öçF–æW$æöFRFö7VÖVçDVÆVÖVçB¢°¢&WGW&âFö7VÖVçDVÆVÖVçC°¢Ð ¢&WGW&âFö7VÖVçC°¢Ð ¢&—fFR§5fÇVRvWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢ö&¦V7B&V6V—fW"À¢7G&–æræÖRÀ¢gVæ3Ä§5fÇVRÂ•&VDöæÇ”Æ—7CÄ§5fÇVSâÂ§5fÇVSâ6ÆÂÀ¢–çBÆVæwF‚Ò¢°¢f"ÖWF†öG2Òö†÷7D6ÆÆ&ÆT66†RävWD÷$7&VFUfÇVR‡&V6V—fW"“°¢–b†ÖWF†öG2åG'”vWEfÇVR†æÖRÂ÷WBf"W†—7F–ær’¢°¢&WGW&âW†—7F–æs°¢Ð ¢f"7&VFVBÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ†æÖRÂ6ÆÂÂÆVæwF‚“°¢ÖWF†öG5¶æÖUÒÒ7&VFVC°¢&WGW&â7&VFVC°¢Ð ¢&—fFR§5fÇVR7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR„æöFRæöFR¢°¢&WGW&âvWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æöFRÀ¢&6ö×&TFö7VÖVçE÷6—F–öâ"À¢…òÂ&w2’Óà¢°¢f"÷F†W"Ò&w2ä6÷VçBâò&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†÷F†W"ÓÒçVÆÂ¢°¢F‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRv6ö×&TFö7VÖVçE÷6—F–öâröâtæöFRs¢&ÖWFW"—2æ÷BöbG—RtæöFRrâ"“°¢Ð ¢&WGW&â§5fÇVRäg&öÔ–çC3"‚†–çB–æöFRä6ö×&TFö7VÖVçE÷6—F–öâ†÷F†W"’“°¢ÒÀ¢ÆVæwFƒ¢“°¢Ð ¢&—fFRö&¦V7B&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ„§5fÇVRfÇVR¢°¢–b‡fÇVRåFrÒ§5fÇVUFrä†÷7Dö&¦V7B¢°¢&WGW&âçVÆÃ°¢Ð ¢f"&W6öÇWF–öâÒö–çFW'&WFW"ä†÷7Dö&¦V7EF&ÆRå&W6öÇfR‡fÇVRä4†÷7Dö&¦V7D†æFÆR‚’Âö–çFW'&WFW"ä†÷7E&W6öÇfT6öçFW‡B“°¢&WGW&â&W6öÇWF–öâä—4ö²ò&W6öÇWF–öâä†÷7Dö&¦V7B¢çVÆÃ°¢Ð ¢V&Æ–2&ööÂG'•&W6öÇfT†÷7Dö&¦V7B„§5fÇVRfÇVRÂ÷WBö&¦V7B†÷7Dö&¦V7B¢°¢†÷7Dö&¦V7BÒ&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ‡fÇVR“°¢&WGW&â†÷7Dö&¦V7BÒçVÆÃ°¢Ð ¢òòòÇ7VÖÖ'“à¢òòò6öçfW'BfVä¥2§5fÇVRFòääUBö&¦V7B&V7W'6—fVÇ’Â7V—F&ÆRf÷ ¢òòòvV$G&—fW"6W&–Æ—6F–öââ&–Ö—F—fRfÇVW2ÖFòF†V—"ääUBWV—fÆVçG3°¢òòò'&—2&V6öÖRÆ—7BfÇC¶ö&¦V7BfwC³²Æ–âö&¦V7G2&V6öÖRF–7F–öæ'’fÇC·7G&–ærÆö&¦V7BfwC³°¢òòò†÷7Bö&¦V7G2&R&W6öÇfVBF‡&÷Vv‚F†R†÷7BÖö&¦V7BF&ÆRà¢òòòÂ÷7VÖÖ'“à¢V&Æ–2ö&¦V7B6öçfW'D§5fÇVUFôö&¦V7B„§5fÇVRfÇVR¢°¢&WGW&â6öçfW'D§5fÇVUFôö&¦V7D–×Â‡fÇVRÂæWr†6…6WCÆÆöæsâ‚’Â“°¢Ð ¢&—fFRö&¦V7B6öçfW'D§5fÇVUFôö&¦V7D–×Â„§5fÇVRfÇVRÂ†6…6WCÆÆöæsâf—6—FVBÂ–çBFWF‚¢°¢–b†FWF‚âb’&WGW&â%´Ö„FWF…Ò#° ¢7v—F6‚‡fÇVRåFr¢°¢66R§5fÇVUFråVæFVf–æVC ¢66R§5fÇVUFräçVÆÃ ¢&WGW&âçVÆÃ°¢66R§5fÇVUFrä&ööÆVã ¢&WGW&âfÇVRä4&ööÆVâ‚“°¢66R§5fÇVUFrä–çC3# ¢&WGW&âfÇVRä4–çC3"‚“°¢66R§5fÇVUFräçVÖ&W# ¢&WGW&âfÇVRä4çVÖ&W"‚“°¢66R§5fÇVUFrå7G&–æs ¢&WGW&âfÇVRä57G&–ær‚“°¢66R§5fÇVUFrå7–Ö&öÃ ¢&WGW&âfÇVRä57–Ö&öÄFW67&—F–öâ‚’óò%7–Ö&öÂ‚’#°¢66R§5fÇVUFrä&–t–çC ¢&WGW&âfÇVRä4&–t–çB‚’åFõ7G&–ær‚“°¢66R§5fÇVUFräö&¦V7C ¢°¢f"†æFÆRÒfÇVRä4ö&¦V7D†æFÆR‚“°¢f"ö&¤–BÒ†æFÆRåFô–çCcB‚“°¢–b‚f—6—FVBäFB†ö&¤–B’’&WGW&â%´6—&7VÆ%Ò#° ¢–b…ö–çFW'&WFW"ÓÒçVÆÂ’&WGW&â%¶ö&¦V7Bö&¦V7EÒ#° ¢G'¢°¢f"ö&¢Òö–çFW'&WFW"ä†VävWDö&¦V7B††æFÆR“°¢–b†ö&¢ÓÒçVÆÂ’&WGW&â%¶ö&¦V7Bö&¦V7EÒ#° ¢òòFWFV7B'&—2'’6†V6¶–ærf÷"æöâÖæVvF—fRvÆVæwF‚r&÷W'G¢–b†ö&¢åG'”vWD÷vå&÷W'G’‚&ÆVæwF‚"Â÷WBf"ÆVäFW62’¢°¢–çB'$ÆVâÒÓ°¢–b†ÆVäFW62åfÇVRåFrÓÒ§5fÇVUFrä–çC3"¢'$ÆVâÒÆVäFW62åfÇVRä4–çC3"‚“°¢VÇ6R–b†ÆVäFW62åfÇVRåFrÓÒ§5fÇVUFräçVÖ&W"¢°¢f"BÒÆVäFW62åfÇVRä4çVÖ&W"‚“°¢–b†BãÒbbBÃÒóbbBÓÒÖF‚åG'Væ6FR†B’¢'$ÆVâÒ†–çB–C°¢Ð ¢–b†'$ÆVâãÒ¢°¢f"Æ—7BÒæWrÆ—7CÆö&¦V7Câ„ÖF‚äÖ–â†'$ÆVâÂ’“°¢f÷"†–çB’Ò²’Â'$ÆVâbb’Â²’²²¢°¢f"¶W’Ò’åFõ7G&–ær…7—7FVÒävÆö&Æ—¦F–öâä7VÇGW&T–æfòä–çf&–çD7VÇGW&R“°¢–b†ö&¢åG'”vWD÷vå&÷W'G’†¶W’Â÷WBf"VÆVÔFW62’¢Æ—7BäFB„6öçfW'D§5fÇVUFôö&¦V7D–×Â†VÆVÔFW62åfÇVRÂf—6—FVBÂFWF‚²’“°¢VÇ6P¢Æ—7BäFB†çVÆÂ“°¢Ð¢f—6—FVBå&VÖ÷fR†ö&¤–B“°¢&WGW&âÆ—7C°¢Ð¢Ð ¢òòÆ–âö&¦V7B(i"F–7F–öæ'¢f"F–7BÒæWrF–7F–öæ'“Ç7G&–ærÂö&¦V7Câ…7G&–æt6ö×&W"ä÷&F–æÂ“°¢f÷&V6‚‡f"·b–âö&¢äVçVÖW&FT÷vå&÷W'F–W2‚’¢°¢–b†·bä¶W’ÓÒ&ÆVæwF‚"bb†·båfÇVRåfÇVRåFrÓÒ§5fÇVUFrä–çC3"ÇÂ·båfÇVRåfÇVRåFrÓÒ§5fÇVUFräçVÖ&W"’¢6öçF–çVS²òòÆVæwF‚öâ'&’ÖÆ–¶Rv2†æFÆVB&÷fP¢F–7E¶·bä¶W•ÒÒ6öçfW'D§5fÇVUFôö&¦V7D–×Â†·båfÇVRåfÇVRÂf—6—FVBÂFWF‚²“°¢Ð¢f—6—FVBå&VÖ÷fR†ö&¤–B“°¢&WGW&âF–7C°¢Ð¢6F6€¢°¢f—6—FVBå&VÖ÷fR†ö&¤–B“°¢&WGW&â%¶ö&¦V7Bö&¦V7EÒ#°¢Ð¢Ð¢66R§5fÇVUFrä†÷7Dö&¦V7C ¢°¢–b…G'•&W6öÇfT†÷7Dö&¦V7B‡fÇVRÂ÷WBf"†÷7Dö&¢’¢°¢&WGW&â†÷7Dö&£²òòÆWB'&÷w6W$’†æFÆRVÆVÖVçB&Vv—7G&F–öà¢Ð¢&WGW&â%¶ö&¦V7B†÷7Dö&¦V7EÒ#°¢Ð¢FVfVÇC ¢&WGW&â%µVæ¶æ÷våÒ#°¢Ð¢Ð ¢&—fFRB&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÅCâ„§5fÇVRfÇVR’v†W&RB¢6Æ70¢°¢&WGW&â&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ‡fÇVR’2C°¢Ð ¢òòòÇ7VÖÖ'“à¢òòò&VG2Æ–÷WBF–ÖVç6–öâ†öfg6WD†V–v‡BÂ6Æ–VçEv–GF‚ÂWF2â’f÷"âVÆVÖVç@¢òòò'’&W6öÇf–ær—G2Æ–÷WB&÷‚g&öÒF†R&VæFW&W"à¢òòòÂ÷7VÖÖ'“à¢&—fFR§5fÇVR&VDVÆVÖVçDÆ–÷WDF–ÖVç6–öâ„VÆVÖVçBVÆVÖVçBÂ7G&–ær&÷W'G’¢°¢fÇW6…VæF–ætÆ–÷WCòä–çfö¶R‚“°¢f"—5v–GF‚Ò&÷W'G’—2&öfg6WEv–GF‚"÷"&6Æ–VçEv–GF‚"÷"'67&öÆÅv–GF‚#°¢f"—4†V–v‡BÒ&÷W'G’—2&öfg6WD†V–v‡B"÷"&6Æ–VçD†V–v‡B"÷"'67&öÆÄ†V–v‡B#°¢–b‡&÷W'G’—2&6Æ–VçEv–GF‚"÷"&6Æ–VçD†V–v‡B"b`¢VÆVÖVçCòä÷væW$Fö7VÖVçCòäFö7VÖVçDVÆVÖVçBÓÒVÆVÖVçB¢°¢f"g&ÖTVÆVÖVçBÒG'”vWDg&ÖTVÆVÖVçDf÷$Fö7VÖVçB†VÆVÖVçBä÷væW$Fö7VÖVçB“°¢–b†g&ÖTVÆVÖVçBÒçVÆÂb`¢G'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ€¢g&ÖTVÆVÖVçBÀ¢&÷W'G’ÓÒ&6Æ–VçEv–GF‚"ò'v–GF‚"¢&†V–v‡B"À¢÷WBf"g&ÖUf–Ww÷'DF–ÖVç6–öâ’¢°¢&WGW&â§5fÇVRäg&öÔçVÖ&W"†g&ÖUf–Ww÷'DF–ÖVç6–öâ“°¢Ð ¢&WGW&â§5fÇVRäg&öÔçVÖ&W"‡&÷W'G’ÓÒ&6Æ–VçEv–GF‚"òv–æF÷uv–GF‚¢v–æF÷t†V–v‡B“°¢Ð ¢òò'&÷w6–ær6öçFW‡B6â&R&W6—¦VBæBVW&–VBv–â–âF†R6ÖR67&—@¢òòF6²âVçF–ÂF†R&VæFW&W"w27–æ6‡&öæ÷W2Æ–÷WB6F6†W2WÂF†RöÆB–g&ÖP¢òò&÷„ÖöFVÂ&VÆöæw2FòF†R&Wf–÷W2f–Ww÷'BæB×W7Bæ÷Bv–â÷fW"7W'&Vç@¢òòW‡Æ–6—B—†VÂ6—¦Rà¢–b„—4”g&ÖTVÆVÖVçB†VÆVÖVçB’b`¢†—5v–GF‚ÇÂ—4†V–v‡B’b`¢G'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ—5v–GF‚ò'v–GF‚"¢&†V–v‡B"Â÷WBf"–g&ÖTFV6Æ&VB’¢°¢&WGW&â§5fÇVRäg&öÔçVÖ&W"†–g&ÖTFV6Æ&VB“°¢Ð¢–b‚†—5v–GF‚ÇÂ—4†V–v‡B’b`¢G'•&VDW‡Æ–6—D–g&ÖT6†–ÆDF–ÖVç6–öâ†VÆVÖVçBÂ—5v–GF‚ò'v–GF‚"¢&†V–v‡B"Â÷WBf"6†–ÆDFV6Æ&VB’¢°¢&WGW&â§5fÇVRäg&öÔçVÖ&W"†6†–ÆDFV6Æ&VB“°¢Ð ¢f"&÷‚ÒÆ–÷WD&÷…&W6öÇfW#òä–çfö¶R†VÆVÖVçB’2&÷„ÖöFVÃ°¢–b†&÷‚ÓÒçVÆÂ¢°¢–b‚†—5v–GF‚ÇÂ—4†V–v‡B’bbG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ—5v–GF‚ò'v–GF‚"¢&†V–v‡B"Â÷WBf"FV6Æ&VB’¢°¢&WGW&â§5fÇVRäg&öÔçVÖ&W"†FV6Æ&VB“°¢Ð ¢&WGW&â§5fÇVRäg&öÔ–çC3"ƒ“°¢Ð ¢&WGW&â&÷W'G’7v—F6€¢°¢&öfg6WEv–GF‚"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚åv–GF‚’À¢&öfg6WD†V–v‡B"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚ä†V–v‡B’À¢&6Æ–VçEv–GF‚"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚åFF–æt&÷‚åv–GF‚’À¢&6Æ–VçD†V–v‡B"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚åFF–æt&÷‚ä†V–v‡B’À¢&öfg6WDÆVgB"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚äÆVgB’À¢&öfg6WEF÷"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚åF÷’À¢&6Æ–VçDÆVgB"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚äÆVgBÒ&÷‚åFF–æt&÷‚äÆVgB’À¢&6Æ–VçEF÷"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä&÷&FW$&÷‚åF÷Ò&÷‚åFF–æt&÷‚åF÷’À¢'67&öÆÅv–GF‚"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä6öçFVçD&÷‚åv–GF‚’À¢'67&öÆÄ†V–v‡B"Óâ§5fÇVRäg&öÔçVÖ&W"†&÷‚ä6öçFVçD&÷‚ä†V–v‡B’À¢'67&öÆÅF÷"Óâ§5fÇVRäg&öÔ–çC3"ƒ’À¢'67&öÆÄÆVgB"Óâ§5fÇVRäg&öÔ–çC3"ƒ’À¢òÓâ§5fÇVRäg&öÔ–çC3"ƒ¢Ó°¢Ð ¢òòòÇ7VÖÖ'“à¢òòò&WGW&ç2vWD&÷VæF–æt6Æ–VçE&V7B&W7VÇBf÷"âVÆVÖVçBg&öÒ—G2Æ–÷WB&÷‚à¢òòòÂ÷7VÖÖ'“à¢&—fFR§5fÇVR&VDVÆVÖVçD&÷VæF–æt6Æ–VçE&V7B„VÆVÖVçBVÆVÖVçB¢°¢fÇW6…VæF–ætÆ–÷WCòä–çfö¶R‚“°¢f"&÷‚ÒÆ–÷WD&÷…&W6öÇfW#òä–çfö¶R†VÆVÖVçB’2&÷„ÖöFVÃ°¢–b„—4”g&ÖTVÆVÖVçB†VÆVÖVçB’¢°¢f"†4FV6Æ&VEv–GF‚ÒG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ'v–GF‚"Â÷WBf"FV6Æ&VEv–GF‚“°¢f"†4FV6Æ&VD†V–v‡BÒG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ&†V–v‡B"Â÷WBf"FV6Æ&VD†V–v‡B“°¢–b††4FV6Æ&VEv–GF‚ÇÂ†4FV6Æ&VD†V–v‡B¢°¢f"7W'&VçBÒ&÷ƒòä&÷&FW$&÷‚óòFVfVÇC°¢&WGW&â7&VFTFöÕ&V7B€¢7W'&VçBäÆVgBÀ¢7W'&VçBåF÷À¢†4FV6Æ&VEv–GF‚òFV6Æ&VEv–GF‚¢7W'&VçBåv–GF‚À¢†4FV6Æ&VD†V–v‡BòFV6Æ&VD†V–v‡B¢7W'&VçBä†V–v‡B“°¢Ð¢Ð ¢–b†&÷‚ÓÒçVÆÂ¢°¢G'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ'v–GF‚"Â÷WBf"v–GF‚“°¢G'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†VÆVÖVçBÂ&†V–v‡B"Â÷WBf"†V–v‡B“°¢&WGW&â7&VFTFöÕ&V7BƒÂÂv–GF‚Â†V–v‡B“°¢Ð ¢f""Ò&÷‚ä&÷&FW$&÷ƒ°¢&WGW&â7&VFTFöÕ&V7B‡"äÆVgBÂ"åF÷Â"åv–GF‚Â"ä†V–v‡B“°¢Ð ¢&—fFR7FF–2&ööÂG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ„VÆVÖVçBVÆVÖVçBÂ7G&–ær&÷W'G’Â÷WBF÷V&ÆRfÇVR¢°¢fÇVRÒ°¢7G&–ær&rÒçVÆÃ°¢f÷&V6‚‡f"FV6Æ&F–öâ–â†VÆVÖVçCòävWDGG&–'WFR‚'7G–ÆR"’óò7G&–æräV×G’¢å7Æ—B‚s²rÂ7G&–æu7Æ—D÷F–öç2å&VÖ÷fTV×G”VçG&–W2’¢°¢f"6öÆöä–æFW‚ÒFV6Æ&F–öâä–æFW„öb‚s¢r“°¢–b†6öÆöä–æFW‚ÃÒÇÀ¢7G&–æräWVÇ2†FV6Æ&F–öå²âæ6öÆöä–æFW…ÒåG&–Ò‚’Â&÷W'G’Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢6öçF–çVS°¢Ð ¢&rÒFV6Æ&F–öå²†6öÆöä–æFW‚²’âåÒåG&–Ò‚“°¢Ð ¢&róóÒVÆVÖVçCòävWDGG&–'WFR‡&÷W'G’“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡&r’¢°¢&WGW&âfÇ6S°¢Ð ¢–b‡&räVæG5v—F‚‚'‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&rÒ&u²âåã%ÒåG&–ÔVæB‚“°¢Ð ¢&WGW&âF÷V&ÆRåG'•'6R‡&rÂçVÖ&W%7G–ÆW2äfÆöBÂ7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBfÇVR’b`¢F÷V&ÆRä—4f–æ—FR‡fÇVR“°¢Ð ¢&—fFR7FF–2&ööÂG'•&VDW‡Æ–6—D–g&ÖT6†–ÆDF–ÖVç6–öâ„VÆVÖVçBVÆVÖVçBÂ7G&–ær&÷W'G’Â÷WBF÷V&ÆRfÇVR¢°¢fÇVRÒ°¢–b†VÆVÖVçCòä6†–ÆDæöFW2ÓÒçVÆÂ¢°¢&WGW&âfÇ6S°¢Ð ¢f÷&V6‚‡f"6†–ÆB–âVÆVÖVçBä6†–ÆDæöFW2äöeG—SÄVÆVÖVçCâ‚’¢°¢–b„—4”g&ÖTVÆVÖVçB†6†–ÆB’bbG'•&VDFV6Æ&VE—†VÄF–ÖVç6–öâ†6†–ÆBÂ&÷W'G’Â÷WBfÇVR’¢°¢&WGW&âG'VS°¢Ð¢Ð ¢&WGW&âfÇ6S°¢Ð ¢&—fFR§5fÇVR7&VFTFöÕ&V7B†F÷V&ÆRÆVgBÂF÷V&ÆRF÷ÂF÷V&ÆRv–GF‚ÂF÷V&ÆR†V–v‡B¢°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"†ÆVgB’À¢²'’%ÒÒ§5fÇVRäg&öÔçVÖ&W"‡F÷’À¢²'v–GF‚%ÒÒ§5fÇVRäg&öÔçVÖ&W"‡v–GF‚’À¢²&†V–v‡B%ÒÒ§5fÇVRäg&öÔçVÖ&W"††V–v‡B’À¢²'F÷%ÒÒ§5fÇVRäg&öÔçVÖ&W"‡F÷’À¢²'&–v‡B%ÒÒ§5fÇVRäg&öÔçVÖ&W"†ÆVgB²v–GF‚’À¢²&&÷GFöÒ%ÒÒ§5fÇVRäg&öÔçVÖ&W"‡F÷²†V–v‡B’À¢²&ÆVgB%ÒÒ§5fÇVRäg&öÔçVÖ&W"†ÆVgB’À¢Ò“°¢Ð ¢&—fFR§5fÇVR7&VFTV×G”FöÕ&V7DÆ—7B‚¢°¢f"'&’Òö–çFW'&WFW"äÆÆö6FT'&’„'&’äV×G“Ä§5fÇVSâ‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’€¢'&’À¢&—FVÒ"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&—FVÒ"Â…òÂò’Óâ§5fÇVRäçVÆÂÂÆVæwFƒ¢’À¢VçVÖW&&ÆS¢fÇ6R“°¢&WGW&â'&“°¢Ð ¢&—fFRFö7VÖVçDg&vÖVçB7&VFT6öçFW‡GVÄg&vÖVçB„FöÕ&ævR&ævRÂ7G&–ær‡FÖÂ¢°¢f"6öçFW‡BÒ&W6öÇfU&ævT6öçFW‡DVÆVÖVçB‡&ævR“°¢–b†6öçFW‡BÓÒçVÆÂ¢°¢&WGW&â‡&ævSòä6öÖÖöäæ6W7F÷$6öçF–æW#òä÷væW$Fö7VÖVçBóòæWrFö7VÖVçB‚’’ä7&VFTFö7VÖVçDg&vÖVçB‚“°¢Ð ¢&WGW&â‡FÖÅ'6W"å'6Tg&vÖVçB€¢6öçFW‡BÀ¢‡FÖÂóò7G&–æräV×G’À¢æWr‡FÖÅ'6W$÷F–öç2²&6UW&’Òö7W'&VçD&6UW&’ÒÀ¢÷WBò“°¢Ð ¢&—fFR7FF–2VÆVÖVçB&W6öÇfU&ævT6öçFW‡DVÆVÖVçB„FöÕ&ævR&ævR¢°¢f÷"‡f"æöFRÒ&ævSòä6öÖÖöäæ6W7F÷$6öçF–æW#²æöFRÒçVÆÃ²æöFRÒæöFRå&VçDæöFR¢°¢–b†æöFR—2VÆVÖVçBVÆVÖVçB¢°¢&WGW&âVÆVÖVçC°¢Ð ¢–b†æöFR—2Fö7VÖVçBFö7VÖVçB¢°¢&WGW&âFö7VÖVçBä&öG’óòFö7VÖVçBäFö7VÖVçDVÆVÖVçC°¢Ð¢Ð ¢&WGW&âçVÆÃ°¢Ð ¢&—fFRfö–BF‡&÷t†–W&&6‡•&WVW7DW'&÷"‡7G&–ærÖW76vR¢°¢F‡&÷tFöÔW†6WF–öâ‚$†–W&&6‡•&WVW7DW'&÷""ÂÖW76vRóò$†–W&&6‡’&WVW7BW'&÷"â"“°¢Ð ¢&—fFRfö–BF‡&÷tFöÔW†6WF–öâ‡7G&–æræÖRÂ7G&–ærÖW76vR¢°¢f"W†6WF–öäæÖRÒ7G&–ærä—4çVÆÄ÷%v†—FU76R†æÖR’ò$W'&÷""¢æÖS°¢f"W†6WF–öäÖW76vRÒÖW76vRóò7G&–æräV×G“°¢f"6öçFW‡BÒ„fVä'&÷w6W"ä§2ä'V–ÇF–ç2ä”'V–ÇF–ä6öçFW‡B•ö–çFW'&WFW#°¢f"W'&÷%fÇVRÒ7G&–æräWVÇ2†W†6WF–öäæÖRÂ%G—TW'&÷""Â7G&–æt6ö×&—6öâä÷&F–æÂ¢ò6öçFW‡Bä7&VFUG—TW'&÷"†W†6WF–öäÖW76vR¢¢6öçFW‡Bä7&VFTW'&÷"†W†6WF–öäÖW76vR“°¢f"W'&÷"Òö–çFW'&WFW"ä†VävWDö&¦V7B†W'&÷%fÇVRä4ö&¦V7D†æFÆR‚’“°¢W'&÷"äFVf–æT÷vå&÷W'G’€¢&æÖR"À¢æWrfVä'&÷w6W"ä§2äö&¦V7G2ä§5&÷W'G”FW67&—F÷"€¢§5fÇVRäg&öÕ7G&–ær†W†6WF–öäæÖR’À¢w&—F&ÆS¢G'VRÀ¢VçVÖW&&ÆS¢fÇ6RÀ¢6öæf–wW&&ÆS¢G'VR’“° ¢F‡&÷ræWr§5F‡&÷väW†6WF–öâ†W'&÷%fÇVR“°¢Ð ¢òòòÇ7VÖÖ'“à¢òòòFV6öFW2FF¢U$Â–çFò—G26öçFVçB7G&–ærâ7W÷'G2FW‡B÷Æ–âæ@¢òòòFW‡Bö¦f67&—Bv—F‚÷F–öæÂ&6ScBVæ6öF–ærâ&WGW&ç2çVÆÂ–bF†RU$À¢òòò—2æ÷BfÆ–BFF¢U$Â÷"FV6öF–ærf–Ç2à¢òòòÂ÷7VÖÖ'“à¢&—fFR7FF–27G&–ærFV6öFTFFW&Â‡7G&–ærW&Â¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡W&Â’ÇÂW&Âå7F'G5v—F‚‚&FF¢"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âçVÆÃ°¢Ð ¢òòFF¥³ÆÖVF–G—SåÕ³¶&6ScEÒÃÆFFà¢f"6öÖÖ–G‚ÒW&Âä–æFW„öb‚rÂr“°¢–b†6öÖÖ–G‚Â¢°¢&WGW&âçVÆÃ°¢Ð ¢f"†VFW"ÒW&Âå7V'7G&–ærƒRÂ6öÖÖ–G‚ÒR“²òò6¶—&FF¢ ¢f"FFÒW&Âå7V'7G&–ær†6öÖÖ–G‚²“°¢f"—4&6ScBÒ†VFW"äVæG5v—F‚‚#¶&6ScB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“° ¢–b†—4&6ScB¢°¢G'¢°¢f"'—FW2Ò6öçfW'Bäg&öÔ&6ScE7G&–ær†FF“°¢&WGW&âVæ6öF–æråUDc‚ävWE7G&–ær†'—FW2“°¢Ð¢6F6€¢°¢&WGW&âçVÆÃ°¢Ð¢Ð ¢òòU$ÂÖVæ6öFVBFF¢G'¢°¢&WGW&âW&’åVæW66TFF7G&–ær†FF“°¢Ð¢6F6€¢°¢&WGW&âFF²òò&W7BÖVff÷'@¢Ð¢Ð ¢òòòÇ7VÖÖ'“à¢òòò6öçfW'G2¥26ÖVÄ66R&÷W'G’æÖRFò552¶V&"Ö66R&÷W'G’æÖRà¢òòòRærâ&&6¶w&÷VæD6öÆ÷""(i"&&6¶w&÷VæBÖ6öÆ÷""Â'¤–æFW‚"(i"'¢Ö–æFW‚"à¢òòòÂ÷7VÖÖ'“à¢&—fFR7FF–27G&–ær6ÖVÅFô775&÷‡7G&–ær6ÖVÂ¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†6ÖVÂ’¢°¢&WGW&â6ÖVÃ°¢Ð ¢f"6"ÒæWr7G&–æt'V–ÆFW"†6ÖVÂäÆVæwF‚²B“°¢f÷"†–çB’Ò²’Â6ÖVÂäÆVæwFƒ²’²²¢°¢f"6‚Ò6ÖVÅ¶•Ó°¢–b†6†"ä—5WW"†6‚’¢°¢6"äVæB‚rÒr“°¢6"äVæB†6†"åFôÆ÷vW$–çf&–çB†6‚’“°¢Ð¢VÇ6P¢°¢6"äVæB†6‚“°¢Ð¢Ð¢&WGW&â6"åFõ7G&–ær‚“°¢Ð ¢&—fFR7FF–27G&–ær775&÷Fô6ÖVÂ‡7G&–ær&÷W'G”æÖR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡&÷W'G”æÖR’ÇÂ&÷W'G”æÖRä–æFW„öb‚rÒr’Â¢°¢&WGW&â&÷W'G”æÖS°¢Ð ¢f"6"ÒæWr7G&–æt'V–ÆFW"‡&÷W'G”æÖRäÆVæwF‚“°¢f"WW&66TæW‡BÒfÇ6S°¢f÷"†–çB’Ò²’Â&÷W'G”æÖRäÆVæwFƒ²’²²¢°¢f"6‚Ò&÷W'G”æÖU¶•Ó°¢–b†6‚ÓÒrÒr¢°¢WW&66TæW‡BÒG'VS°¢6öçF–çVS°¢Ð ¢6"äVæB‡WW&66TæW‡Bò6†"åFõWW$–çf&–çB†6‚’¢6‚“°¢WW&66TæW‡BÒfÇ6S°¢Ð ¢&WGW&â6"åFõ7G&–ær‚“°¢Ð ¢&—fFR7FF–27G&–ær6öW&6UFô†÷7E7G&–ær„§5fÇVRfÇVR¢°¢&WGW&âfÇVRåFr7v—F6€¢°¢§5fÇVUFråVæFVf–æVBÓâ'VæFVf–æVB"À¢§5fÇVUFräçVÆÂÓâ&çVÆÂ"À¢§5fÇVUFrä&ööÆVâÓâfÇVRä4&ööÆVâ‚’ò'G'VR"¢&fÇ6R"À¢§5fÇVUFrä–çC3"ÓâfÇVRä4–çC3"‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢§5fÇVUFräçVÖ&W"ÓâfÇVRä4çVÖ&W"‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢§5fÇVUFrå7G&–ærÓâfÇVRä57G&–ær‚’À¢§5fÇVUFrå7–Ö&öÂÓâfÇVRä57–Ö&öÄFW67&—F–öâ‚’—27G&–ærBòB%7–Ö&öÂ‡¶GÒ’"¢%7–Ö&öÂ‚’"À¢§5fÇVUFrä&–t–çBÓâfÇVRä4&–t–çB‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢òÓâfÇVRåFõ7G&–ær‚¢Ó°¢Ð ¢&—fFR7FF–2&ööÂ6öW&6UFô†÷7D&ööÆVâ„§5fÇVRfÇVR¢°¢&WGW&âfÇVRåFr7v—F6€¢°¢§5fÇVUFrä&ööÆVâÓâfÇVRä4&ööÆVâ‚’À¢§5fÇVUFrä–çC3"ÓâfÇVRä4–çC3"‚’ÒÀ¢§5fÇVUFräçVÖ&W"ÓâÖF‚ä'2‡fÇVRä4çVÖ&W"‚’’âF÷V&ÆRäW6–ÆöâÀ¢§5fÇVUFrå7G&–ærÓâ7G&–ærä—4çVÆÄ÷$V×G’‡fÇVRä57G&–ær‚’’À¢§5fÇVUFräçVÆÂÓâfÇ6RÀ¢§5fÇVUFråVæFVf–æVBÓâfÇ6RÀ¢òÓâG'VP¢Ó°¢Ð ¢&—fFR7FF–2V–çB6öW&6UFô†÷7ET–çC3"„§5fÇVRfÇVRÂV–çBfÆÆ&6²¢°¢7v—F6‚‡fÇVRåFr¢°¢66R§5fÇVUFrä–çC3# ¢&WGW&âVæ6†V6¶VB‚‡V–çB—fÇVRä4–çC3"‚’“°¢66R§5fÇVUFräçVÖ&W# ¢f"çVÖ&W"ÒfÇVRä4çVÖ&W"‚“°¢–b†F÷V&ÆRä—4f–æ—FR†çVÖ&W"’¢°¢&WGW&âVæ6†V6¶VB‚‡V–çB–çVÖ&W"“°¢Ð¢'&V³°¢66R§5fÇVUFrå7G&–æs ¢–b‡V–çBåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VB’¢°¢&WGW&â'6VC°¢Ð¢–b†F÷V&ÆRåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2äfÆöBÂ7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VDF÷V&ÆR’b`¢F÷V&ÆRä—4f–æ—FR‡'6VDF÷V&ÆR’¢°¢&WGW&âVæ6†V6¶VB‚‡V–çB—'6VDF÷V&ÆR“°¢Ð¢'&V³°¢Ð ¢&WGW&âfÆÆ&6³°¢Ð ¢&—fFR7FF–2F÷V&ÆR6öW&6UFôf–æ—FTçVÖ&W"„§5fÇVRfÇVRÂF÷V&ÆRfÆÆ&6²¢°¢7v—F6‚‡fÇVRåFr¢°¢66R§5fÇVUFrä–çC3# ¢&WGW&âfÇVRä4–çC3"‚“°¢66R§5fÇVUFräçVÖ&W# ¢f"çVÖ&W"ÒfÇVRä4çVÖ&W"‚“°¢&WGW&âF÷V&ÆRä—4f–æ—FR†çVÖ&W"’òçVÖ&W"¢fÆÆ&6³°¢66R§5fÇVUFrå7G&–æs ¢&WGW&âF÷V&ÆRåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2äfÆöBÂ7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VB’b`¢F÷V&ÆRä—4f–æ—FR‡'6VB¢ò'6V@¢¢fÆÆ&6³°¢66R§5fÇVUFrä&ööÆVã ¢&WGW&âfÇVRä4&ööÆVâ‚’ò¢°¢66R§5fÇVUFräçVÆÃ ¢&WGW&â°¢FVfVÇC ¢&WGW&âfÆÆ&6³°¢Ð¢Ð ¢&—fFR7FF–2&ööÂG'”6öW&6T–æFW‚„§5fÇVRfÇVRÂ÷WB–çB–æFW‚¢°¢7v—F6‚‡fÇVRåFr¢°¢66R§5fÇVUFrä–çC3# ¢–æFW‚ÒfÇVRä4–çC3"‚“°¢&WGW&âG'VS°¢66R§5fÇVUFräçVÖ&W# ¢f"çVÖ&W"ÒfÇVRä4çVÖ&W"‚“°¢–b†F÷V&ÆRä—4f–æ—FR†çVÖ&W"’b`¢ÖF‚åG'Væ6FR†çVÖ&W"’ÓÒçVÖ&W"b`¢çVÖ&W"ãÒ–çBäÖ–åfÇVRb`¢çVÖ&W"ÃÒ–çBäÖ…fÇVR¢°¢–æFW‚Ò†–çB–çVÖ&W#°¢&WGW&âG'VS°¢Ð¢'&V³°¢66R§5fÇVUFrå7G&–æs ¢–b†–çBåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WB–æFW‚’¢°¢&WGW&âG'VS°¢Ð¢'&V³°¢Ð ¢–æFW‚ÒÓ°¢&WGW&âfÇ6S°¢Ð ¢&—fFR§5fÇVR7&VFTæöFT'&”Æ–¶R„”VçVÖW&&ÆSÄæöFSâæöFW2¢°¢f"—FV×2ÒæWrÆ—7CÄ§5fÇVSâ‚“°¢–b†æöFW2ÒçVÆÂ¢°¢f÷&V6‚‡f"æöFR–âæöFW2¢°¢—FV×2äFB…Fô†÷7DæöFT÷$çVÆÂ†æöFR’“°¢Ð¢Ð ¢f"'&’Òö–çFW'&WFW"äÆÆö6FT'&’†—FV×2“°¢f"—FVÔgVæ7F–öâÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&—FVÒ"À¢…òÂ&w2’Óà¢°¢f"–æFW‚ÒÓ°¢–b†&w2ä6÷VçBâ¢°¢7v—F6‚†&w5³ÒåFr¢°¢66R§5fÇVUFrä–çC3# ¢–æFW‚Ò&w5³Òä4–çC3"‚“°¢'&V³°¢66R§5fÇVUFräçVÖ&W# ¢f"çVÖ&W"Ò&w5³Òä4çVÖ&W"‚“°¢–b†F÷V&ÆRä—4f–æ—FR†çVÖ&W"’b`¢ÖF‚åG'Væ6FR†çVÖ&W"’ÓÒçVÖ&W"b`¢çVÖ&W"ãÒ–çBäÖ–åfÇVRb`¢çVÖ&W"ÃÒ–çBäÖ…fÇVR¢°¢–æFW‚Ò†–çB–çVÖ&W#°¢Ð¢'&V³°¢66R§5fÇVUFrå7G&–æs ¢–b†–çBåG'•'6R†&w5³Òä57G&–ær‚’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VD–æFW‚’¢°¢–æFW‚Ò'6VD–æFWƒ°¢Ð¢'&V³°¢Ð¢Ð ¢–b†–æFW‚ÂÇÂ–æFW‚ãÒ—FV×2ä6÷VçB¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢&WGW&â—FV×5¶–æFW…Ó°¢ÒÀ¢ÆVæwFƒ¢“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†'&’Â&—FVÒ"Â—FVÔgVæ7F–öâÂVçVÖW&&ÆS¢fÇ6R“°¢&WGW&â'&“°¢Ð ¢&—fFR§5fÇVR7&VFTæöFTf–ÇFW$6öç7FçG4ö&¦V7B‚¢°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²$d”ÅDU%ô44UB%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%&W7VÇBä66WB’À¢²$d”ÅDU%õ$T¤T5B%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%&W7VÇBå&V¦V7B’À¢²$d”ÅDU%õ4´•%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%&W7VÇBå6¶—’À¢²%4„õuôÄÂ%ÒÒ§5fÇVRäg&öÔçVÖ&W"„æöFTf–ÇFW%6†÷räÆÂ’À¢²%4„õuôTÄTÔTåB%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räVÆVÖVçB’À¢²%4„õuôEE$”%UDR%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räGG&–'WFR’À¢²%4„õuõDU…B%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷råFW‡B’À¢²%4„õuô4DDõ4T5D”ôâ%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷rä4FF6V7F–öâ’À¢²%4„õuôTåD•E•õ$TdU$Tä4R%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räVçF—G•&VfW&Væ6R’À¢²%4„õuôTåD•E’%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räVçF—G’’À¢²%4„õuõ$ô4U54”äuô”å5E%T5D”ôâ%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷rå&ö6W76–æt–ç7G'V7F–öâ’À¢²%4„õuô4ôÔÔTåB%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷rä6öÖÖVçB’À¢²%4„õuôDô5TÔTåB%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räFö7VÖVçB’À¢²%4„õuôDô5TÔTåEõE•R%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räFö7VÖVçEG—R’À¢²%4„õuôDô5TÔTåEôe$tÔTåB%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räFö7VÖVçDg&vÖVçB’À¢²%4„õuôäõDD”ôâ%ÒÒ§5fÇVRäg&öÔ–çC3"‚†–çB”æöFTf–ÇFW%6†÷räæ÷FF–öâ¢Ò“°¢Ð ¢&—fFRfö–Bæf–vFT÷væ–æt'&÷w6–æt6öçFW‡B…W&’F&vWEW&’¢°¢–b‡F&vWEW&’ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢–b…÷&VçE&VÆÔ÷væW"ÒçVÆÂbböVÖ&VFF–ætg&ÖTVÆVÖVçBÒçVÆÂ¢°¢÷&VçE&VÆÔ÷væW"åVWVTg&ÖTæf–vF–öâ…öVÖ&VFF–ætg&ÖTVÆVÖVçBÂF&vWEW&’“°¢&WGW&ã°¢Ð ¢ö†÷7Bäæf–vFR‡F&vWEW&’“°¢Ð ¢&—fFRfö–BVWVTg&ÖTæf–vF–öâ„VÆVÖVçBg&ÖTVÆVÖVçBÂW&’F&vWEW&’¢°¢–b†g&ÖTVÆVÖVçBÓÒçVÆÂÇÂF&vWEW&’ÓÒçVÆÂÇÂg&ÖTVÆVÖVçBä—46öææV7FVB¢°¢&WGW&ã°¢Ð ¢g&ÖTVÆVÖVçBå6WDGG&–'WFR‚'7&2"ÂF&vWEW&’ä'6öÇWFUW&’“°¢6WE7F÷&VD†÷7E&÷W'G’†g&ÖTVÆVÖVçBÂ%õöfVäg&ÖTÆöEW&Â"Â§5fÇVRåVæFVf–æVB“°¢VWVTg&ÖTVÆVÖVçDÆöB†g&ÖTVÆVÖVçB“°¢Ð ¢&—fFRfö–BF÷EG&ç6fW'&VDÖW76vU÷'G2„§5fÇVR÷'G2Â§5fÇVRF&vWEv–æF÷r¢°¢–b‡÷'G2åFrÒ§5fÇVUFräö&¦V7BÇÂF&vWEv–æF÷råFrÒ§5fÇVUFräö&¦V7B¢°¢&WGW&ã°¢Ð ¢f"ÆVæwF…fÇVRÒ&VD§5&÷W'G’‡÷'G2Â&ÆVæwF‚"“°¢f"ÆVæwF‚ÒÆVæwF…fÇVRåFrÓÒ§5fÇVUFräçVÖ&W ¢òÖF‚äÖ‚ƒÂ†–çB–ÆVæwF…fÇVRä4çVÖ&W"‚’¢¢°¢f÷"‡f"–æFW‚Ò²–æFW‚ÂÆVæwFƒ²–æFW‚²²¢°¢f"÷'BÒ&VD§5&÷W'G’‡÷'G2Â–æFW‚åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢–b‡÷'BåFrÓÒ§5fÇVUFräö&¦V7B¢°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’‡÷'BÂ%öfVåv–æF÷r"ÂF&vWEv–æF÷r“°¢Ð¢Ð¢Ð ¢&—fFR§5fÇVR7&VFT774vÆö&Äö&¦V7B‚¢°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²'7W÷'G2%ÒÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢'7W÷'G2"À¢…òÂ&w2’Óâ§5fÇVRäg&öÔ&ööÆVâ„7757W÷'G2†&w2’’À¢ÆVæwFƒ¢’À¢²&W66R%ÒÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&W66R"À¢…òÂ&w2’Óâ§5fÇVRäg&öÕ7G&–ær„774W66R†&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G’’’À¢ÆVæwFƒ¢¢Ò“°¢Ð ¢&—fFR§5fÇVR7&VFTV×G•7G–ÆU6†VWDÆ—7Dö&¦V7B‚¢°¢&WGW&âö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSà¢°¢²&ÆVæwF‚%ÒÒ§5fÇVRäg&öÔ–çC3"ƒ’À¢²&—FVÒ%ÒÒö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ€¢&—FVÒ"À¢…òÂò’Óâ§5fÇVRäçVÆÂÀ¢ÆVæwFƒ¢¢Ò“°¢Ð ¢&—fFR7FF–2&ööÂ7757W÷'G2„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2¢°¢–b†&w2ä6÷VçBÓÒ¢°¢&WGW&âfÇ6S°¢Ð ¢–b†&w2ä6÷VçBÓÒ¢°¢&WGW&â—5ÆW6–&ÆT7757W÷'G46öæF—F–öâ„6öW&6UFô†÷7E7G&–ær†&w5³Ò’“°¢Ð ¢f"&÷W'G’Ò6öW&6UFô†÷7E7G&–ær†&w5³Ò’åG&–Ò‚“°¢f"fÇVRÒ6öW&6UFô†÷7E7G&–ær†&w5³Ò’åG&–Ò‚“°¢&WGW&â—5ÆW6–&ÆT775&÷W'G”æÖR‡&÷W'G’’b`¢7G&–ærä—4çVÆÄ÷%v†—FU76R‡fÇVR’b`¢6öçF–ç4775'6T'&V¶W"‡fÇVR’b`¢†4&Ææ6VD774w&÷W–ær‡fÇVR“°¢Ð ¢&—fFR7FF–2&ööÂ—5ÆW6–&ÆT7757W÷'G46öæF—F–öâ‡7G&–ær6öæF—F–öâ¢°¢f"FW‡BÒ6öæF—F–öãòåG&–Ò‚“°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡FW‡B’ÇÂ6öçF–ç4775'6T'&V¶W"‡FW‡B’¢°¢&WGW&âfÇ6S°¢Ð ¢&WGW&â†4&Ææ6VD774w&÷W–ær‡FW‡B“°¢Ð ¢&—fFR7FF–2&ööÂ—5ÆW6–&ÆT775&÷W'G”æÖR‡7G&–ær&÷W'G’¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡&÷W'G’’¢°¢&WGW&âfÇ6S°¢Ð ¢–b‡&÷W'G’å7F'G5v—F‚‚"ÒÒ"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢&WGW&â&÷W'G’äÆVæwF‚â"bb&÷W'G’äÆÂ†6‚Óâ6†"ä—4ÆWGFW$÷$F–v—B†6‚’ÇÂ6‚ÓÒrÒrÇÂ6‚ÓÒuòr“°¢Ð ¢&WGW&â&÷W'G’äÆÂ†6‚Óâ6†"ä—4ÆWGFW$÷$F–v—B†6‚’ÇÂ6‚ÓÒrÒr“°¢Ð ¢&—fFR7FF–2&ööÂ6öçF–ç4775'6T'&V¶W"‡7G&–ærFW‡B¢ÓâFW‡Bä–æFW„öb‚w²r’ãÒÇÂFW‡Bä–æFW„öb‚wÒr’ãÒÇÂFW‡Bä–æFW„öb‚s²r’ãÒ° ¢&—fFR7FF–2&ööÂ†4&Ææ6VD774w&÷W–ær‡7G&–ærFW‡B¢°¢f"&VçF†W6W2Ò°¢f"'&6¶WG2Ò°¢f"–å6–ævÆUV÷FRÒfÇ6S°¢f"–äF÷V&ÆUV÷FRÒfÇ6S°¢f"W66–ærÒfÇ6S° ¢f÷&V6‚‡f"6‚–âFW‡B¢°¢–b†W66–ær¢°¢W66–ærÒfÇ6S°¢6öçF–çVS°¢Ð ¢–b†6‚ÓÒuÅÂr¢°¢W66–ærÒG'VS°¢6öçF–çVS°¢Ð ¢–b†–å6–ævÆUV÷FR¢°¢–b†6‚ÓÒuÂrr¢°¢–å6–ævÆUV÷FRÒfÇ6S°¢Ð ¢6öçF–çVS°¢Ð ¢–b†–äF÷V&ÆUV÷FR¢°¢–b†6‚ÓÒr"r¢°¢–äF÷V&ÆUV÷FRÒfÇ6S°¢Ð ¢6öçF–çVS°¢Ð ¢7v—F6‚†6‚¢°¢66RuÂrs ¢–å6–ævÆUV÷FRÒG'VS°¢'&V³°¢66Rr"s ¢–äF÷V&ÆUV÷FRÒG'VS°¢'&V³°¢66Rr‚s ¢&VçF†W6W2²³°¢'&V³°¢66Rr’s ¢–b‚Ò×&VçF†W6W2Â¢°¢&WGW&âfÇ6S°¢Ð ¢'&V³°¢66Ru²s ¢'&6¶WG2²³°¢'&V³°¢66RuÒs ¢–b‚ÒÖ'&6¶WG2Â¢°¢&WGW&âfÇ6S°¢Ð ¢'&V³°¢Ð¢Ð ¢&WGW&â&VçF†W6W2ÓÒbb'&6¶WG2ÓÒbb–å6–ævÆUV÷FRbb–äF÷V&ÆUV÷FRbbW66–æs°¢Ð ¢&—fFR7FF–27G&–ær774W66R‡7G&–ærfÇVR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡fÇVR’¢°¢&WGW&â7G&–æräV×G“°¢Ð ¢f"6"ÒæWr7G&–æt'V–ÆFW"‡fÇVRäÆVæwF‚“°¢f÷"‡f"’Ò²’ÂfÇVRäÆVæwFƒ²’²²¢°¢f"6‚ÒfÇVU¶•Ó°¢–b†6‚ÓÒuÃr¢°¢6"äVæB‚uÇTdddBr“°¢6öçF–çVS°¢Ð ¢–b‚†’ÓÒbb6†"ä—4F–v—B†6‚’’ÇÀ¢†’ÓÒbbfÇVU³ÒÓÒrÒrbb6†"ä—4F–v—B†6‚’’¢°¢6"äVæB‚uÅÂr“°¢6"äVæB‚‚†–çB–6‚’åFõ7G&–ær‚'‚"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢6"äVæB‚rr“°¢6öçF–çVS°¢Ð ¢–b†’ÓÒbb6‚ÓÒrÒrbbfÇVRäÆVæwF‚ÓÒ¢°¢6"äVæB‚%ÅÂÒ"“°¢6öçF–çVS°¢Ð ¢–b†6‚ãÒƒƒÇÂ6‚ÓÒrÒrÇÂ6‚ÓÒuòrÇÂ6†"ä—4ÆWGFW$÷$F–v—B†6‚’¢°¢6"äVæB†6‚“°¢6öçF–çVS°¢Ð ¢6"äVæB‚uÅÂr“°¢6"äVæB†6‚“°¢Ð ¢&WGW&â6"åFõ7G&–ær‚“°¢Ð ¢&—fFRæöFTf–ÇFW"7&VFUG&VUvÆ¶W$f–ÇFW"„§5fÇVRf–ÇFW%fÇVR¢°¢–b†f–ÇFW%fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVBÇÂf–ÇFW%fÇVRåFrÓÒ§5fÇVUFräçVÆÂ¢°¢&WGW&âçVÆÃ°¢Ð ¢f"6ÆÆ&6²Òf–ÇFW%fÇVS°¢f"F†—5fÇVRÒ§5fÇVRåVæFVf–æVC°¢–b‚ö–çFW'&WFW"ä6ä6ÆÅfÇVR†6ÆÆ&6²’¢°¢6ÆÆ&6²Ò&VD§5&÷W'G’†f–ÇFW%fÇVRÂ&66WDæöFR"“°¢F†—5fÇVRÒf–ÇFW%fÇVS°¢Ð ¢–b‚ö–çFW'&WFW"ä6ä6ÆÅfÇVR†6ÆÆ&6²’¢°¢&WGW&âçVÆÃ°¢Ð ¢&WGW&âæöFRÓà¢°¢f"&W7VÇBÒö–çFW'&WFW"ä–çfö¶TgVæ7F–öâ†6ÆÆ&6²ÂæWuµÒ²Fô†÷7DæöFT÷$çVÆÂ†æöFR’ÒÂF†—5fÇVR“°¢&WGW&âFôæöFTf–ÇFW%&W7VÇB‡&W7VÇB“°¢Ó°¢Ð ¢&—fFR7FF–2æöFTf–ÇFW%&W7VÇBFôæöFTf–ÇFW%&W7VÇB„§5fÇVRfÇVR¢°¢f"çVÖW&–2ÒfÇVRåFr7v—F6€¢°¢§5fÇVUFrä–çC3"ÓâfÇVRä4–çC3"‚’À¢§5fÇVUFräçVÖ&W"ÓâF÷V&ÆRä—4f–æ—FR‡fÇVRä4çVÖ&W"‚’’ò†–çB—fÇVRä4çVÖ&W"‚’¢†–çB”æöFTf–ÇFW%&W7VÇBä66WBÀ¢§5fÇVUFrå7G&–ærv†Vâ–çBåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VB’Óâ'6VBÀ¢òÓâ†–çB”æöFTf–ÇFW%&W7VÇBä66W@¢Ó° ¢&WGW&âçVÖW&–27v—F6€¢°¢†–çB”æöFTf–ÇFW%&W7VÇBå&V¦V7BÓâæöFTf–ÇFW%&W7VÇBå&V¦V7BÀ¢†–çB”æöFTf–ÇFW%&W7VÇBå6¶—ÓâæöFTf–ÇFW%&W7VÇBå6¶—À¢òÓâæöFTf–ÇFW%&W7VÇBä66W@¢Ó°¢Ð ¢&—fFR6VÆVB6Æ72fVä§4‡FÖÄ6öÆÆV7F–öä†÷7@¢°¢V&Æ–2fVä§4‡FÖÄ6öÆÆV7F–öä†÷7B„…DÔÄ6öÆÆV7F–öâ6öÆÆV7F–öâ¢°¢6öÆÆV7F–öâÒ6öÆÆV7F–öâóòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†6öÆÆV7F–öâ’“°¢Ð ¢V&Æ–2…DÔÄ6öÆÆV7F–öâ6öÆÆV7F–öâ²vWC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§5G&VUvÆ¶W$†÷7@¢°¢V&Æ–2fVä§5G&VUvÆ¶W$†÷7B…G&VUvÆ¶W"G&VUvÆ¶W"¢°¢G&VUvÆ¶W"ÒG&VUvÆ¶W"óòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb‡G&VUvÆ¶W"’“°¢Ð ¢V&Æ–2G&VUvÆ¶W"G&VUvÆ¶W"²vWC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§4æ–ÖF–öä†÷7@¢°¢V&Æ–2fVä§4æ–ÖF–öä†÷7B„VÆVÖVçBF&vWBÂ§5fÇVR¶W–g&ÖW2Â§5fÇVR÷F–öç2¢°¢F&vWBÒF&vWBóòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb‡F&vWB’“°¢¶W–g&ÖW2Ò¶W–g&ÖW3°¢÷F–öç2Ò÷F–öç3°¢Ð ¢V&Æ–2VÆVÖVçBF&vWB²vWC²Ð¢V&Æ–2§5fÇVR¶W–g&ÖW2²vWC²Ð¢V&Æ–2§5fÇVR÷F–öç2²vWC²Ð¢V&Æ–27G&–ær–B²vWC²6WC²ÒÒ7G&–æräV×G“°¢V&Æ–27G&–ærÆ•7FFR²vWC²6WC²ÒÒ&–FÆR#°¢V&Æ–2F÷V&ÆSò7F'EF–ÖR²vWC²6WC²Ð¢V&Æ–2F÷V&ÆSò7W'&VçEF–ÖR²vWC²6WC²ÒÒ°¢V&Æ–2F÷V&ÆRÆ–&6µ&FR²vWC²6WC²ÒÒ°¢V&Æ–2&ööÂVæF–ær²vWC²6WC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§4FöÕ7G&–ætÖ†÷7@¢°¢V&Æ–2fVä§4FöÕ7G&–ætÖ†÷7B„VÆVÖVçBVÆVÖVçB¢°¢VÆVÖVçBÒVÆVÖVçBóòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†VÆVÖVçB’“°¢Ð ¢V&Æ–2VÆVÖVçBVÆVÖVçB²vWC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§4FöÔ–×ÆVÖVçFF–öä†÷7@¢°¢V&Æ–2fVä§4FöÔ–×ÆVÖVçFF–öä†÷7B„Fö7VÖVçB÷væW$Fö7VÖVçB¢°¢÷væW$Fö7VÖVçBÒ÷væW$Fö7VÖVçBóòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†÷væW$Fö7VÖVçB’“°¢Ð ¢V&Æ–2Fö7VÖVçB÷væW$Fö7VÖVçB²vWC²Ð¢Ð ¢òòòÇ7VÖÖ'“à¢òòò7&VFW2¥2†÷7Bö&¦V7B&W&W6VçF–ær÷Wv–æF÷r7&VFVB'’v–æF÷ræ÷Vâ‚’à¢òòòF†R†æFÆR—2â÷VRö&¦V7B&÷f–FVB'’F†R†÷7Bw2÷Våv–æF÷rFVÆVvFRà¢òòòÂ÷7VÖÖ'“à¢òòòÇ7VÖÖ'“à¢òòòÖ&²âVÆVÖVçB27G–ÆRÂÆ–÷WBÂæB–çBF—'G’âÖ&´F—'G¢òòòWFöÖF–6ÆÇ’&÷vFW26†–ÆB¤F—'G’fÆw2WFòF†R&ö÷BæBæ÷F–f–W0¢òòòF†RFö7VÖVçBÂ6òF†RæW‡B6¶–FöÕ&VæFW&W"å&VæFW"‚’6ÆÂ&V6ö×WFW0¢òòò7G–ÆRÂ&RÖÆ—2÷WBÂæB&V'V–ÆG2F†R–çBG&VRà¢òòò&WV—&VBgFW"DôÒ×WFF–öç2F†BffV7B&VæFW&–ær'WBFöâwBvòF‡&÷Vv€¢òòòF†Ræ÷&ÖÂF—'G’ÖfÆrF‚†RærâF–Æörç6†÷tÖöFÂö6Æ÷6R’à¢òòòÂ÷7VÖÖ'“à¢&—fFR7FF–2fö–B–çfÆ–FFU–çDf÷$VÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢–b†VÆVÖVçBÓÒçVÆÂ’&WGW&ã° ¢òòÖ&²F†RVÆVÖVçBF—'G’6òF†R&VæFW&W"w2F—'G’ÖfÆr6†V6·2G&–vvW"¢òò–çB×G&VR&V'V–ÆBà¢VÆVÖVçBäÖ&´F—'G’€¢fVä'&÷w6W"ä6÷&RäFöÒåc"ä–çfÆ–FF–öä¶–æBå7G–ÆRÀ¢fVä'&÷w6W"ä6÷&RäFöÒåc"ä–çfÆ–FF–öä¶–æBäÆ–÷WBÀ¢fVä'&÷w6W"ä6÷&RäFöÒåc"ä–çfÆ–FF–öä¶–æBå–çB“° ¢òòæ÷F–g’F†R552Væv–æRF†BF†—2VÆVÖVçBw27FFR6†ævVB6ò—@¢òò&RÖWfÇVFW26ö×WFVB7G–ÆW2âF†RF–Æörw2F—7Æ’6†ævW2g&öÐ¢òò&æöæR"Fò&&Æö6²"&6VBöâF†R¶÷VåÒGG&–'WFR6†V6¶VB'¢òòT7G–ÆU&÷f–FW"âv—F†÷WB&V666FRÂÆ7D6ö×WFVE7G–ÆW2&WF–ç0¢òòF†RöÆB&F—7Æ“¢æöæR"æBF†RÆ–÷WBVæv–æRæWfW"7&VFW2&÷‚à¢fVä'&÷w6W"äfVäVæv–æRå&VæFW&–æräVÆVÖVçE7FFTÖævW"ä–ç7Fæ6Räæ÷F–g•7FFT6†ævVB†VÆVÖVçB“°¢Ð ¢òòòÇ7VÖÖ'“à¢òòòf—&RÖæBÖf÷&vWB÷7BöbÖöFÂF–ÆörFòF†RT’F‡&VBà¢òòòF†RF–ÆörV'2f—7VÆÇ’'WB¥2W†V7WF–öâ6öçF–çVW2v—F†÷WBv—F–æp¢òòòf÷"F†RW6W"FòF—6Ö—72—BâF†—2fö–G2F‡&VBÖFVFÆö6²&WGvVVâF†P¢òòòfVä¥2v÷&¶W"ÂF†RVæv–æRÆö÷ÂæBF†R6–Æ²ääUBvÖRÆö÷à¢òòòÂ÷7VÖÖ'“à¢&—fFR7FF–2fö–B÷7DF–Æöt7–æ2‡7G&–ærG—RÂ7G&–ærÖW76vRÂ7G&–ærFVfVÇEfÇVR¢°¢f"'&–FvRÒ§4F–Æöt'&–FvRå6†÷tF–Æös°¢–b†'&–FvRÓÒçVÆÂ¢°¢fVäÆövvW"åv&â‚B%··G—WÕÒ§4F–Æöt'&–FvRæ÷B–ç7FÆÆVB(	BF–Æör7W&W76VB"ÂÆöt6FVv÷'’ä¦f67&—B“°¢&WGW&ã°¢Ð ¢òòf—&RF†R'&–FvRöâF‡&VB×ööÂF‡&VB6òF†R6ÆÆW"‡F†R¥2v÷&¶W"¢òò&WGW&ç2–ÖÖVF–FVÇ’âF†R'&–FvR—G6VÆbv–ÆÂ÷7BF†RF–Æörv–FvW@¢òò7&VF–öâFòF†RÖ–âF‡&VBæB&Æö6²F†RööÂF‡&VBÂæ÷BF†R¥2v÷&¶W"à¢7—7FVÒåF‡&VF–æråF‡&VEööÂåVWVUW6W%v÷&´—FVÒ…òÓà¢°¢G'’²'&–FvR‡G—RÂÖW76vRÂFVfVÇEfÇVR“²Ð¢6F6‚„W†6WF–öâW‚¢°¢fVäÆövvW"äW'&÷"‚B%µ÷7DF–Æöt7–æ5Ò·G—WÒF–Æörf–ÆVC¢¶W‚äÖW76vWÒ"ÂÆöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ò“°¢Ð ¢&—fFR7FF–27G&–ærFôF–Æöu7G&–ær„§5fÇVRfÇVR¢°¢&WGW&âfÇVRåFr7v—F6€¢°¢§5fÇVUFråVæFVf–æVB÷"§5fÇVUFräçVÆÂÓâ7G&–æräV×G’À¢§5fÇVUFrå7G&–ærÓâfÇVRä57G&–ær‚’À¢§5fÇVUFrä&ööÆVâÓâfÇVRä4&ööÆVâ‚’ò'G'VR"¢&fÇ6R"À¢§5fÇVUFrä–çC3"ÓâfÇVRä4–çC3"‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢§5fÇVUFräçVÖ&W"ÓâfÇVRä4çVÖ&W"‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢§5fÇVUFrä&–t–çBÓâfÇVRä4&–t–çB‚’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’À¢§5fÇVUFrå7–Ö&öÂÓâfÇVRä57–Ö&öÄFW67&—F–öâ‚’óò%7–Ö&öÂ‚’"À¢òÓâfÇVRåFõ7G&–ær‚¢Ó°¢Ð ¢&—fFR§5fÇVR7&VFU÷Wv–æF÷t†÷7Dö&¦V7B†ö&¦V7B†æFÆRÂ7G&–æræÖRÂ7G&–ærW&Â¢°¢f"ö&¢Òö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“° ¢òò6Æ÷6VB(	B&VBÖöæÇ’vWGFW ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&6Æ÷6VB"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&vWB6Æ÷6VB"Â…òÂó"’Óà¢°¢f"6†V6²Ò§4F–Æöt'&–FvRä—5÷Wv–æF÷t6Æ÷6VC°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†6†V6²ÒçVÆÂbb6†V6²††æFÆR’“°¢ÒÂÆVæwFƒ¢’“° ¢òòæÖP¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&æÖR"Â§5fÇVRäg&öÕ7G&–ær†æÖRóò""’“° ¢òòÆö6F–öâæ‡&V`¢f"Æö6F–öâÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Æö6F–öâÂ&‡&Vb"Â§5fÇVRäg&öÕ7G&–ær‡W&Âóò&&÷WC¦&Ææ²"’“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&Æö6F–öâ"ÂÆö6F–öâ“° ¢òòfö7W2‚¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&fö7W2"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&fö7W2"Â…òÂó"’Óâ§5fÇVRåVæFVf–æVBÂÆVæwFƒ¢’“° ¢òò6Æ÷6R‚¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&6Æ÷6R"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&6Æ÷6R"Â…òÂó"’Óà¢°¢f"6Æ÷6W"Ò§4F–Æöt'&–FvRä6Æ÷6U÷Wv–æF÷s°¢6Æ÷6W#òä–çfö¶R††æFÆR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢’“° ¢òòFö7VÖVçB(	B÷WFö7VÖVçBv—F‚÷Vâ÷w&—FR÷w&—FVÆâö6Æ÷6P¢f"Fö7VÖVçBÒö–çFW'&WFW"äÆÆö6FTö&¦V7B†æWrF–7F–öæ'“Ç7G&–ærÂ§5fÇVSâ‚’“°¢f"‡FÖÄ'VffW"ÒæWr7—7FVÒåFW‡Bå7G&–æt'V–ÆFW"‚“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Fö7VÖVçBÂ'&VG•7FFR"À¢§5fÇVRäg&öÕ7G&–ær‚&6ö×ÆWFR"’“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Fö7VÖVçBÂ&÷Vâ"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&÷Vâ"Â…òÂó"’Óà¢°¢‡FÖÄ'VffW"ä6ÆV"‚“°¢&WGW&âFö7VÖVçC°¢ÒÂÆVæwFƒ¢’“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Fö7VÖVçBÂ'w&—FR"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚'w&—FR"Â…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâ¢‡FÖÄ'VffW"äVæB†&w5³ÒåFõ7G&–ær‚’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢’“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Fö7VÖVçBÂ'w&—FVÆâ"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚'w&—FVÆâ"Â…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâ¢‡FÖÄ'VffW"äVæB†&w5³ÒåFõ7G&–ær‚’“°¢‡FÖÄ'VffW"äVæB‚uÆâr“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢’“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†Fö7VÖVçBÂ&6Æ÷6R"À¢ö–çFW'&WFW"äÆÆö6FTæF—fTgVæ7F–öâ‚&6Æ÷6R"Â…òÂó"’Óà¢°¢f"f–æÆ—¦W"Ò§4F–Æöt'&–FvRäf–æÆ—¦U÷WFö7VÖVçC°¢–b†f–æÆ—¦W"ÒçVÆÂbb‡FÖÄ'VffW"äÆVæwF‚â¢°¢f–æÆ—¦W"††æFÆRÂ‡FÖÄ'VffW"åFõ7G&–ær‚’“°¢‡FÖÄ'VffW"ä6ÆV"‚“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢’“° ¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â&Fö7VÖVçB"ÂFö7VÖVçB“° ¢òò6VÆbòv–æF÷r6—&7VÆ"&VfW&Væ6W0¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â'v–æF÷r"Âö&¢“°¢ö–çFW'&WFW"å6WDö&¦V7E&÷W'G’†ö&¢Â'6VÆb"Âö&¢“° ¢&WGW&âö&£°¢Ð ¢&—fFR6VÆVB&V6÷&B'&÷w6W$WfVçDÆ—7FVæW"‡7G&–ærG—RÂ§5fÇVR6ÆÆ&6²Â&ööÂ6GW&RÂ&ööÂöæ6R“° ¢&—fFR6VÆVB6Æ72'&÷w6W$FöÔWfVçDF—7F6…7FFP¢°¢V&Æ–2&ööÂFVfVÇE&WfVçFVB²vWC²6WC²Ð¢V&Æ–2&ööÂ7F÷&÷vF–öâ²vWC²6WC²Ð¢V&Æ–2&ööÂ7F÷–ÖÖVF–FU&÷vF–öâ²vWC²6WC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§4Æö6F–öä†÷7@¢°¢V&Æ–2fVä§4Æö6F–öä†÷7B…W&’W&’¢°¢W&’ÒW&“°¢Ð ¢V&Æ–2W&’W&’²vWC²6WC²Ð¢Ð ¢&—fFR6VÆVB6Æ72fVä§4†—7F÷'”†÷7@¢°¢V&Æ–2fVä§4†—7F÷'”†÷7B„fVä§4Æö6F–öä†÷7BÆö6F–öâ¢°¢Æö6F–öâÒÆö6F–öâóòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†Æö6F–öâ’“°¢Ð ¢V&Æ–2fVä§4Æö6F–öä†÷7BÆö6F–öâ²vWC²Ð¢V&Æ–2–çBÆVæwF‚²vWC²6WC²ÒÒ°¢V&Æ–2§5fÇVR7FFR²vWC²6WC²ÒÒ§5fÇVRäçVÆÃ°¢Ð ¢&—fFR6VÆVB6Æ72'&÷w6W$fVä§4†÷7D†öö·2¢”†÷7D†öö·0¢°¢&—fFR6öç7B–çBgVæ7F–öå&÷F÷G—U&÷W'G”Æ–Ö—BÒ#Cƒ°¢&—fFR6öç7B–çBgVæ7F–öå&÷F÷G—U&÷W'G”ÆVæwF„Æ–Ö—BÒ#Sc°¢&—fFR&VFöæÇ’†6…6WCÇ7G&–æsâögVæ7F–öå&÷F÷G—U&÷W'F–W2ÒæWr…7G&–æt6ö×&W"ä÷&F–æÂ“°¢&—fFRfVä§4'&÷w6W%67&—DVæv–æRö÷væW#°¢&—fFRFö7VÖVçBöFö7VÖVçC°¢&—fFR'&÷w6W%7W&f6U&öf–ÆRöæf–vF÷#°¢&—fFRfVä§4Æö6F–öä†÷7BöÆö6F–öã°¢&—fFRW&’ö&6UW&“° ¢V&Æ–2fö–B&W6WB‚¢°¢ögVæ7F–öå&÷F÷G—U&÷W'F–W2ä6ÆV"‚“°¢ö÷væW"ÒçVÆÃ°¢öFö7VÖVçBÒçVÆÃ°¢öæf–vF÷"ÒçVÆÃ°¢öÆö6F–öâÒçVÆÃ°¢ö&6UW&’ÒçVÆÃ°¢Ð ¢V&Æ–2fö–B&–æB€¢fVä§4'&÷w6W%67&—DVæv–æR÷væW"À¢Fö7VÖVçBFö7VÖVçBÀ¢'&÷w6W%7W&f6U&öf–ÆRæf–vF÷"À¢fVä§4Æö6F–öä†÷7BÆö6F–öâÀ¢W&’&6UW&’¢°¢ögVæ7F–öå&÷F÷G—U&÷W'F–W2ä6ÆV"‚“°¢ö÷væW"Ò÷væW#°¢öFö7VÖVçBÒFö7VÖVçC°¢öæf–vF÷"Òæf–vF÷#°¢öÆö6F–öâÒÆö6F–öã°¢ö&6UW&’Ò&6UW&“°¢Ð ¢V&Æ–2fö–BVçVWVU&öÖ—6T¦ö"…&öÖ—6T¦ö"¦ö"¢°¢ö÷væW#òåö–çFW'&WFW"äVçVWVT†÷7FVE&öÖ—6T¦ö"†¦ö"“°¢Ð ¢V&Æ–2fö–B&W÷'E&öÖ—6U&V¦V7F–öâ„§5fÇVR&öÖ—6RÂ&öÖ—6U&V¦V7F–öä÷W&F–öâ÷W&F–öâ¢°¢–b†÷W&F–öâÒ&öÖ—6U&V¦V7F–öä÷W&F–öâå&V¦V7BÇÂö÷væW"ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"&V6öâÒ#ÇVæ¶æ÷vãâ#°¢G'¢°¢–b‡&öÖ—6RåFrÓÒ§5fÇVUFräö&¦V7Bb`¢ö÷væW"åö–çFW'&WFW"ä†VävWDö&¦V7B‡&öÖ—6Rä4ö&¦V7D†æFÆR‚’’—2&öÖ—6T–ç7Fæ6R&öÖ—6T–ç7Fæ6R¢°¢&V6öâÒö÷væW"äFW67&–&U&öÖ—6U&V¦V7F–öå&V6öâ‡&öÖ—6T–ç7Fæ6Rå&öÖ—6RävWE&W7VÇEVæ6†V6¶VB‚’“°¢Ð¢Ð¢6F6€¢°¢&V6öâÒ#ÇVæf–Æ&ÆSâ#°¢Ð ¢fVä'&÷w6W"ä6÷&RäVæv–æTÆöt6ö×Båv&â€¢B%´fVä§4'&–FvUÒVæ†æFÆVB&öÖ—6R&V¦V7F–öã¢·&V6öçÒ"À¢fVä'&÷w6W"ä6÷&RäÆövv–æräÆöt6FVv÷'’ä¦f67&—B“°¢Ð ¢V&Æ–2fö–Bö'6W'fTgVæ7F–öå&÷F÷G—U&÷W'G”FVf–æ—F–öâ‡7G&–ær&÷W'G’Â§5fÇVRfÇVR¢°¢–b‡fÇVRåFrÒ§5fÇVUFrä&ööÆVâÇÀ¢fÇVRä4&ööÆVâ‚’ÇÀ¢7G&–ærä—4çVÆÄ÷$V×G’‡&÷W'G’’ÇÀ¢&÷W'G’äÆVæwF‚âgVæ7F–öå&÷F÷G—U&÷W'G”ÆVæwF„Æ–Ö—BÇÀ¢ögVæ7F–öå&÷F÷G—U&÷W'F–W2ä6÷VçBãÒgVæ7F–öå&÷F÷G—U&÷W'G”Æ–Ö—B¢°¢&WGW&ã°¢Ð ¢ögVæ7F–öå&÷F÷G—U&÷W'F–W2äFB‡&÷W'G’“°¢Ð ¢V&Æ–2&ööÂG'”vWD†÷7E&÷W'G’„†÷7Dö&¦V7D†æFÆR†æFÆRÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢ÓâG'”vWD†÷7E&÷W'G’††æFÆRÂ&÷W'G’Â†÷7E&÷W'G”66W74¶–æBå&VBÂ÷WBfÇVR“° ¢V&Æ–2&ööÂG'”vWD†÷7E&÷W'G’€¢†÷7Dö&¦V7D†æFÆR†æFÆRÀ¢7G&–ær&÷W'G’À¢†÷7E&÷W'G”66W74¶–æB66W74¶–æBÀ¢÷WB§5fÇVRfÇVR¢°¢–b…ö÷væW"ÓÒçVÆÂÇÂö÷væW"åö–çFW'&WFW"ÓÒçVÆÂ¢°¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢f"&W6öÇWF–öâÒö÷væW"åö–çFW'&WFW"ä†÷7Dö&¦V7EF&ÆRå&W6öÇfR††æFÆRÂö÷væW"åö–çFW'&WFW"ä†÷7E&W6öÇfT6öçFW‡B“°¢–b‚&W6öÇWF–öâä—4ö²¢°¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð ¢f"†÷7Dö&¦V7BÒ&W6öÇWF–öâä†÷7Dö&¦V7C°¢f"÷væW$æÖRÒvWD†÷7D”÷væW$æÖR††÷7Dö&¦V7B“°¢f"f÷VæBÒG'”vWD†÷7Dö&¦V7DFVf–æVE&÷W'G’††÷7Dö&¦V7BÂ&÷W'G’Â÷WBfÇVR“° ¢–b‚f÷VæB¢°¢&V6÷&DÖ—76–æt†÷7D’††÷7Dö&¦V7BÂ÷væW$æÖRÂ&÷W'G’ÂFôÖ—76–æt”÷W&F–öä¶–æB†66W74¶–æB’“°¢Ð ¢&WGW&âf÷VæC°¢Ð ¢V&Æ–2fö–Bö'6W'fTÖ—76–æt†÷7E&÷W'G”÷W&F–öâ€¢†÷7Dö&¦V7D†æFÆR†æFÆRÀ¢7G&–ær&÷W'G’À¢†÷7E&÷W'G”66W74¶–æB66W74¶–æB¢°¢–b…ö÷væW"ÓÒçVÆÂÇÂö÷væW"åö–çFW'&WFW"ÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"&W6öÇWF–öâÒö÷væW"åö–çFW'&WFW"ä†÷7Dö&¦V7EF&ÆRå&W6öÇfR€¢†æFÆRÀ¢ö÷væW"åö–çFW'&WFW"ä†÷7E&W6öÇfT6öçFW‡B“°¢–b‚&W6öÇWF–öâä—4ö²ÇÂ&W6öÇWF–öâä†÷7Dö&¦V7BÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢f"†÷7Dö&¦V7BÒ&W6öÇWF–öâä†÷7Dö&¦V7C°¢&V6÷&DÖ—76–æt†÷7D’€¢†÷7Dö&¦V7BÀ¢vWD†÷7D”÷væW$æÖR††÷7Dö&¦V7B’À¢&÷W'G’À¢FôÖ—76–æt”÷W&F–öä¶–æB†66W74¶–æB’“°¢Ð ¢&—fFR&ööÂG'”vWD†÷7Dö&¦V7DFVf–æVE&÷W'G’†ö&¦V7B†÷7Dö&¦V7BÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢&ööÂf÷VæC°¢7v—F6‚††÷7Dö&¦V7B¢°¢66RFö7VÖVçBFö7VÖVçC ¢f÷VæBÒG'”vWDFö7VÖVçE&÷W'G’†Fö7VÖVçBÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RVÆVÖVçBVÆVÖVçC ¢f÷VæBÒG'”vWDVÆVÖVçE&÷W'G’†VÆVÖVçBÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4FöÔ–×ÆVÖVçFF–öä†÷7B–×ÆVÖVçFF–öã ¢f÷VæBÒG'”vWDFöÔ–×ÆVÖVçFF–öå&÷W'G’†–×ÆVÖVçFF–öâÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RGG"GG# ¢f÷VæBÒG'”vWDGG%&÷W'G’†GG"Â&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RæÖVDæöFTÖæÖVDæöFTÖ ¢f÷VæBÒG'”vWDæÖVDæöFTÖ&÷W'G’†æÖVDæöFTÖÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RDôÕFö¶VäÆ—7BFö¶VäÆ—7C ¢f÷VæBÒG'”vWDFöÕFö¶VäÆ—7E&÷W'G’‡Fö¶VäÆ—7BÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66R6†&7FW$FF6†&7FW$FF ¢f÷VæBÒG'”vWD6†&7FW$FF&÷W'G’†6†&7FW$FFÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RFö7VÖVçDg&vÖVçBg&vÖVçC ¢f÷VæBÒG'”vWDFö7VÖVçDg&vÖVçE&÷W'G’†g&vÖVçBÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RæöFRæöFS ¢f÷VæBÒG'”vWDæöFU&÷W'G’†æöFRÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RFöÕ&ævR&ævS ¢f÷VæBÒG'”vWE&ævU&÷W'G’‡&ævRÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4‡FÖÄ6öÆÆV7F–öä†÷7B‡FÖÄ6öÆÆV7F–öã ¢f÷VæBÒG'”vWD‡FÖÄ6öÆÆV7F–öå&÷W'G’†‡FÖÄ6öÆÆV7F–öâÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§5G&VUvÆ¶W$†÷7BG&VUvÆ¶W# ¢f÷VæBÒG'”vWEG&VUvÆ¶W%&÷W'G’‡G&VUvÆ¶W"Â&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öã ¢f÷VæBÒG'”vWDæ–ÖF–öå&÷W'G’†æ–ÖF–öâÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4FöÕ7G&–ætÖ†÷7BFöÕ7G&–ætÖ ¢f÷VæBÒG'”vWDFöÕ7G&–ætÖ&÷W'G’†FöÕ7G&–ætÖÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVå7F÷&vT&V†÷7B7F÷&vT&V ¢f÷VæBÒG'”vWE7F÷&vU&÷W'G’‡7F÷&vT&VÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66R'&÷w6W%7W&f6U&öf–ÆRæf–vF÷# ¢–b…G'”vWDæf–vF÷%&÷W'G’†æf–vF÷"Â&÷W'G’Â÷WBfÇVR’¢°¢&WGW&âG'VS°¢Ð¢òòfÆÂ&6²FòW6W"Ö76–væVB&÷W'F–W27F÷&VBf–G'•6WD†÷7E&÷W'G¢òò†RærâvöövÆR7GV'2æf–vF÷"ç6VæD&V6öâ’à¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†æf–vF÷"Â&÷W'G’“°¢f÷VæBÒfÇVRåFrÒ§5fÇVUFråVæFVf–æVC°¢'&V³°¢66RfVä§4Æö6F–öä†÷7BÆö6F–öã ¢f÷VæBÒG'”vWDÆö6F–öå&÷W'G’†Æö6F–öâÂ&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4†—7F÷'”†÷7B†—7F÷'“ ¢f÷VæBÒG'”vWD†—7F÷'•&÷W'G’††—7F÷'’Â&÷W'G’Â÷WBfÇVR“°¢'&V³°¢66RfVä§4×WFF–öäö'6W'fW$†÷7B×WFF–öäö'6W'fW# ¢f÷VæBÒö÷væW"åG'”vWD×WFF–öäö'6W'fW%&÷W'G’†×WFF–öäö'6W'fW"Â&÷W'G’Â÷WBfÇVR“°¢'&V³°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢f÷VæBÒfÇ6S°¢'&V³°¢Ð ¢&WGW&âf÷VæC°¢Ð ¢&—fFR7FF–27G&–ærvWD†÷7D”÷væW$æÖR†ö&¦V7B†÷7Dö&¦V7B¢°¢&WGW&â†÷7Dö&¦V7B7v—F6€¢°¢Fö7VÖVçBÓâ$Fö7VÖVçB"À¢VÆVÖVçBVÆVÖVçBÓâ‡FÖÄVÆVÖVçD–çFW&f6T6FÆörå&W6öÇfT–çFW&f6TæÖR€¢VÆVÖVçBäÆö6ÄæÖRÀ¢VÆVÖVçBäæÖW76UW&’’óò$VÆVÖVçB"À¢fVä§4FöÔ–×ÆVÖVçFF–öä†÷7BÓâ$DôÔ–×ÆVÖVçFF–öâ"À¢GG"Óâ$GG""À¢æÖVDæöFTÖÓâ$æÖVDæöFTÖ"À¢DôÕFö¶VäÆ—7BÓâ$DôÕFö¶VäÆ—7B"À¢6†&7FW$FFÓâ$6†&7FW$FF"À¢6†F÷u&ö÷BÓâ%6†F÷u&ö÷B"À¢Fö7VÖVçDg&vÖVçBÓâ$Fö7VÖVçDg&vÖVçB"À¢FöÕ&ævRÓâ%&ævR"À¢fVä§4‡FÖÄ6öÆÆV7F–öä†÷7BÓâ$…DÔÄ6öÆÆV7F–öâ"À¢fVä§5G&VUvÆ¶W$†÷7BÓâ%G&VUvÆ¶W""À¢fVä§4æ–ÖF–öä†÷7BÓâ$æ–ÖF–öâ"À¢fVä§4FöÕ7G&–ætÖ†÷7BÓâ$DôÕ7G&–ætÖ"À¢'&÷w6W%7W&f6U&öf–ÆRÓâ$æf–vF÷""À¢fVä§4Æö6F–öä†÷7BÓâ$Æö6F–öâ"À¢fVä§4†—7F÷'”†÷7BÓâ$†—7F÷'’"À¢fVä§4×WFF–öäö'6W'fW$†÷7BÓâ$×WFF–öäö'6W'fW""À¢òÓâ†÷7Dö&¦V7CòävWEG—R‚’äæÖRóò$†÷7Dö&¦V7B ¢Ó°¢Ð ¢&—fFRfö–B&V6÷&DÖ—76–æt†÷7D’€¢ö&¦V7B&V6V—fW"À¢7G&–ær÷væW$æÖRÀ¢7G&–ær&÷W'G’À¢Ö—76–æt”÷W&F–öä¶–æB÷W&F–öä¶–æBÒÖ—76–æt”÷W&F–öä¶–æBå&VB¢°¢–b‚6†÷VÆE&V6÷&DÖ—76–æt†÷7D’†÷væW$æÖRÂ&÷W'G’’¢°¢&WGW&ã°¢Ð ¢ö÷væW#òå&V6÷&DÖ—76–æt†÷7E&÷W'G’€¢&V6V—fW"À¢÷væW$æÖRÀ¢&÷W'G’À¢ö&6UW&’À¢ögVæ7F–öå&÷F÷G—U&÷W'F–W2ä6öçF–ç2‡&÷W'G’’À¢÷W&F–öä¶–æB“°¢Ð ¢&—fFR7FF–2Ö—76–æt”÷W&F–öä¶–æBFôÖ—76–æt”÷W&F–öä¶–æB„†÷7E&÷W'G”66W74¶–æB66W74¶–æB¢Óâ66W74¶–æB7v—F6€¢°¢†÷7E&÷W'G”66W74¶–æBä–ä6†V6²ÓâÖ—76–æt”÷W&F–öä¶–æBä–ä6†V6²À¢†÷7E&÷W'G”66W74¶–æBäFW67&—F÷$÷W&F–öâÓâÖ—76–æt”÷W&F–öä¶–æBäFW67&—F÷$÷W&F–öâÀ¢†÷7E&÷W'G”66W74¶–æBå&÷F÷G—T66W72ÓâÖ—76–æt”÷W&F–öä¶–æBå&÷F÷G—T66W72À¢òÓâÖ—76–æt”÷W&F–öä¶–æBå&V@¢Ó° ¢&—fFRfö–B&V6÷&D76–væVD†÷7D’†ö&¦V7B&V6V—fW"Â7G&–ær÷væW$æÖRÂ7G&–ær&÷W'G’¢°¢–b‚6†÷VÆE&V6÷&DÖ—76–æt†÷7D’†÷væW$æÖRÂ&÷W'G’’¢°¢&WGW&ã°¢Ð ¢ö÷væW#òå&V6÷&D†÷7E&÷W'G”76–væÖVçB‡&V6V—fW"Â÷væW$æÖRÂ&÷W'G’Âö&6UW&’“°¢Ð ¢&—fFR7FF–2&ööÂ6†÷VÆE&V6÷&DÖ—76–æt†÷7D’‡7G&–ær÷væW$æÖRÂ7G&–ær&÷W'G’¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†÷væW$æÖR’ÇÂ7G&–ærä—4çVÆÄ÷%v†—FU76R‡&÷W'G’’¢°¢&WGW&âfÇ6S°¢Ð ¢–b‡&÷W'G’ÓÒ'F†Vâ"ÇÀ¢&÷W'G’ÓÒ&6öç7G'V7F÷""ÇÀ¢&÷W'G’ÓÒ'&÷F÷G—R"ÇÀ¢&÷W'G’ÓÒ%õ÷&÷Fõõò"ÇÀ¢&÷W'G’å7F'G5v—F‚‚%õò"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢&÷W'G’å7F'G5v—F‚‚%ò"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢&÷W'G’å7F'G5v—F‚‚&öâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âfÇ6S°¢Ð ¢&WGW&â&÷W'G’äÆÂ†6†"ä—4F–v—B“°¢Ð ¢V&Æ–2&ööÂG'•6WD†÷7E&÷W'G’„†÷7Dö&¦V7D†æFÆR†æFÆRÂ7G&–ær&÷W'G’Â§5fÇVRfÇVR¢°¢–b…ö÷væW"ÓÒçVÆÂÇÂö÷væW"åö–çFW'&WFW"ÓÒçVÆÂ¢°¢&WGW&âfÇ6S°¢Ð¢f"&W6öÇWF–öâÒö÷væW"åö–çFW'&WFW"ä†÷7Dö&¦V7EF&ÆRå&W6öÇfR††æFÆRÂö÷væW"åö–çFW'&WFW"ä†÷7E&W6öÇfT6öçFW‡B“°¢–b‚&W6öÇWF–öâä—4ö²¢°¢&WGW&âfÇ6S°¢Ð ¢7v—F6‚‡&W6öÇWF–öâä†÷7Dö&¦V7B¢°¢66RFö7VÖVçBFö7VÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&föçG2"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢òòÆÆ÷rvöövÆRföçBÆöF–ær“¢Fö7VÖVçBæföçG2Ò²âââÒà¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†Fö7VÖVçBÂ&föçG2"ÂfÇVR“°¢&WGW&âG'VS°¢66RFö7VÖVçBFö7VÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'F—FÆR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢Fö7VÖVçBåF—FÆRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RFö7VÖVçBFö7VÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&6öö¶–R"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢f"6öö¶–U7G"Ò6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢Fö7VÖVçBä6öö¶–RÒ6öö¶–U7G#°¢òòW'6—7B6öö¶–W2F‡&÷Vv‚F†R†÷7B'&–FvR6òF†W’7W'f—fP¢òòæf–vF–öç2†7&—F–6Âf÷"tb6†ÆÆVævRFö¶Vç2’à¢–b…ö÷væW"ä6öö¶–Uw&—FT'&–FvRÒçVÆÂbbö÷væW"åö7W'&VçD&6UW&’ÒçVÆÂ¢°¢ö÷væW"ä6öö¶–Uw&—FT'&–FvR…ö÷væW"åö7W'&VçD&6UW&’Â6öö¶–U7G"“°¢–b†6öö¶–U7G"å7F'G5v—F‚‚%4uõ53Ò"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢f"6GW&T–BÒÔ–çFW&Æö6¶VBä–æ7&VÖVçB‡&Vbö÷væW"åöF–væ÷7F–46öö¶–T6GW&T6÷VçFW"“°¢ö÷væW"ä6GW&Tæf–vF–öävÆö&Ç2…ö÷væW"åö7W'&VçD&6UW&’Â6GW&T–B“°¢Ð¢Ð¢&WGW&âG'VS°¢66RFö7VÖVçBFö7VÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&FöÖ–â"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†Fö7VÖVçBÂ&FöÖ–â"Â§5fÇVRäg&öÕ7G&–ær„6öW&6UFô†÷7E7G&–ær‡fÇVR’’“°¢&WGW&âG'VS°¢66RFö7VÖVçBFö7VÖVçC ¢òò6F6‚ÖÆÂf÷"&&—G&'’Fö7VÖVçB&÷W'F–W2†RærâvöövÆP¢òò6WG2õöwv'Âõö§6ÂÂæB÷F†W"–çFW&æÂ&öö¶¶VW–ær’à¢&V6÷&D76–væVD†÷7D’†Fö7VÖVçBÂ$Fö7VÖVçB"Â&÷W'G’“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†Fö7VÖVçBÂ&÷W'G’ÂfÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&6Æ74æÖR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBä6Æ74æÖRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&–B"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBä–BÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'fÇVR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢6WDVÆVÖVçEfÇVR†VÆVÖVçBÂ6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Và¢7G&–æräWVÇ2‡&÷W'G’Â'G—R"Â7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“ ¢VÆVÖVçBå6WDGG&–'WFR‚'G—R"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Và¢7G&–æräWVÇ2‡&÷W'G’Â&6†V6¶VB"Â7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢—46†V6¶&ÆT–çWDVÆVÖVçB†VÆVÖVçB“ ¢VÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rå6WD6†V6¶VB†VÆVÖVçBÂ6öW&6UFô†÷7D&ööÆVâ‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'F$–æFW‚"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR€¢'F&–æFW‚"À¢‚†–çB”6öW&6UFôf–æ—FTçVÖ&W"‡fÇVRÂ’’åFõ7G&–ær„7VÇGW&T–æfòä–çf&–çD7VÇGW&R’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'F—FÆR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚'F—FÆR"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&æÖR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚&æÖR"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&6öçFVçB"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚&6öçFVçB"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'7&2"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚'7&2"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&‡&Vb"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚&‡&Vb"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&æöæ6R"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚&æöæ6R"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB’b`¢7G&–æräWVÇ2‡&÷W'G’Â'6æF&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚'6æF&÷‚"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB’b`¢7G&–æräWVÇ2‡&÷W'G’Â'67&öÆÆ–ær"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚'67&öÆÆ–ær"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—4ÖWFVÆVÖVçB†VÆVÖVçB’b`¢7G&–æräWVÇ2‡&÷W'G’Â&‡GGWV—b"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBå6WDGG&–'WFR‚&‡GGÖWV—b"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—567&—DVÆVÖVçB†VÆVÖVçB’b`¢7G&–æräWVÇ2‡&÷W'G’Â&7–æ2"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢6WD&ööÆVäGG&–'WFR†VÆVÖVçBÂ&7–æ2"Â6öW&6UFô†÷7D&ööÆVâ‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—567&—DVÆVÖVçB†VÆVÖVçB’b`¢‡7G&–æräWVÇ2‡&÷W'G’Â'G—R"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢7G&–æräWVÇ2‡&÷W'G’Â&6†'6WB"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢7G&–æräWVÇ2‡&÷W'G’Â&–çFVw&—G’"Â7G&–æt6ö×&—6öâä÷&F–æÂ’“ ¢VÆVÖVçBå6WDGG&–'WFR‡&÷W'G’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—567&—DVÆVÖVçB†VÆVÖVçB’b`¢7G&–æräWVÇ2‡&÷W'G’Â&7&÷74÷&–v–â"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢–b‡fÇVRåFrÓÒ§5fÇVUFräçVÆÂ¢°¢VÆVÖVçBå&VÖ÷fTGG&–'WFR‚&7&÷76÷&–v–â"“°¢Ð¢VÇ6P¢°¢VÆVÖVçBå6WDGG&–'WFR‚&7&÷76÷&–v–â"Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢Ð¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ&÷W'G’å7F'G5v—F‚‚&öâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“ ¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ&÷W'G’åFôÆ÷vW$–çf&–çB‚’ÂfÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&–ææW$…DÔÂ"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBä–ææW$…DÔÂÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢–b…ö÷væW"äW†V7WFT–æÆ–æU67&—G4öä–ææW$…DÔÂ¢°¢ö÷væW"äW†V7WFT–æÆ–æU67&—G4g&öÔVÆVÖVçB†VÆVÖVçB“°¢Ð¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'FW‡D6öçFVçB"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢VÆVÖVçBåFW‡D6öçFVçBÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&öæÆöB"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ&öæÆöB"ÂfÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&öæW'&÷""Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ&öæW'&÷""ÂfÇVR“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçBv†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB’b`¢‡7G&–æräWVÇ2‡&÷W'G’Â'v–GF‚"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢7G&–æräWVÇ2‡&÷W'G’Â&†V–v‡B"Â7G&–æt6ö×&—6öâä÷&F–æÂ’“ ¢VÆVÖVçBå6WDGG&–'WFR‡&÷W'G’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢ö÷væW"äæ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢&WGW&âG'VS°¢66RVÆVÖVçBVÆVÖVçC ¢òò6F6‚ÖÆÂf÷"&&—G&'’VÆVÖVçB&÷W'F–W2†RærâvöövÆR6WG0¢òòõöwv'Âõö§6ÂÂæB÷F†W"–çFW&æÂ&öö¶¶VW–ær&÷W'F–W2öà¢òòDôÒVÆVÖVçG2’â7F÷&Rf÷"ÆFW"&WG&–WfÂf–G'”vWDVÆVÖVçE&÷W'G’à¢&V6÷&D76–væVD†÷7D’†VÆVÖVçBÂvWD†÷7D”÷væW$æÖR†VÆVÖVçB’Â&÷W'G’“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ&÷W'G’ÂfÇVR“°¢&WGW&âG'VS°¢66RGG"GG"v†Vâ7G&–æräWVÇ2‡&÷W'G’Â'fÇVR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢GG"åfÇVRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RGG"GG"v†Vâ7G&–æräWVÇ2‡&÷W'G’Â&æöFUfÇVR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢GG"åfÇVRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RGG"GG"v†Vâ7G&–æräWVÇ2‡&÷W'G’Â'FW‡D6öçFVçB"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢GG"åfÇVRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66R6†&7FW$FF6†&7FW$FFv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&FF"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢6†&7FW$FFäFFÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66R6†&7FW$FF6†&7FW$FFv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&æöFUfÇVR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢6†&7FW$FFäæöFUfÇVRÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66R6†&7FW$FF6†&7FW$FFv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'FW‡D6öçFVçB"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢6†&7FW$FFåFW‡D6öçFVçBÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RfVä§4FöÕ7G&–ætÖ†÷7BFöÕ7G&–ætÖ ¢FöÕ7G&–ætÖäVÆVÖVçBå6WDGG&–'WFR…&÷W'G”æÖUFôFF6WDGG&–'WFR‡&÷W'G’’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢&WGW&âG'VS°¢66RfVå7F÷&vT&V†÷7B7F÷&vT&V ¢òòW"7V2Â7F÷&vU¶¶W•ÒÒfÇVR—2WV—fÆVçBFò7F÷&vRç6WD—FVÒ†¶W’ÂfÇVR’à¢f"6WDW'&÷"Ò7F÷&vT&Vå6WD—FVÒ‡&÷W'G’Â6öW&6UFô†÷7E7G&–ær‡fÇVR’“°¢–b‡6WDW'&÷"ÒçVÆÂ¢°¢òòV÷FW†6VVFVDW'&÷"(	BF†R¥26–FR6†÷VÆBF‡&÷rà¢òòf÷"æ÷rvR6–ÆVçFÇ’–væ÷&RV÷FW'&÷'3²gVÆÀ¢òò–×ÆVÖVçFF–öâv÷VÆBF‡&÷rDôÔW†6WF–öâà¢Ð¢&WGW&âG'VS°¢66RfVä§5G&VUvÆ¶W$†÷7BG&VUvÆ¶W"v†Vâ7G&–æräWVÇ2‡&÷W'G’Â&7W'&VçDæöFR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢f"7W'&VçDæöFRÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ‡fÇVR“°¢–b†7W'&VçDæöFRÓÒçVÆÂ¢°¢&WGW&âfÇ6S°¢Ð ¢G&VUvÆ¶W"åG&VUvÆ¶W"ä7W'&VçDæöFRÒ7W'&VçDæöFS°¢&WGW&âG'VS°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öâv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&–B"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢æ–ÖF–öâä–BÒ6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢&WGW&âG'VS°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öâv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&7W'&VçEF–ÖR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢æ–ÖF–öâä7W'&VçEF–ÖRÒfÇVRåFrÓÒ§5fÇVUFräçVÆÂòçVÆÂ¢6öW&6UFôf–æ—FTçVÖ&W"‡fÇVRÂ“°¢&WGW&âG'VS°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öâv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'7F'EF–ÖR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢æ–ÖF–öâå7F'EF–ÖRÒfÇVRåFrÓÒ§5fÇVUFräçVÆÂòçVÆÂ¢6öW&6UFôf–æ—FTçVÖ&W"‡fÇVRÂ“°¢&WGW&âG'VS°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öâv†Vâ7G&–æräWVÇ2‡&÷W'G’Â'Æ–&6µ&FR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢æ–ÖF–öâåÆ–&6µ&FRÒ6öW&6UFôf–æ—FTçVÖ&W"‡fÇVRÂ“°¢&WGW&âG'VS°¢66RfVä§4æ–ÖF–öä†÷7Bæ–ÖF–öã ¢&V6÷&D76–væVD†÷7D’†æ–ÖF–öâÂ$æ–ÖF–öâ"Â&÷W'G’“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†æ–ÖF–öâÂ&÷W'G’ÂfÇVR“°¢&WGW&âG'VS°¢66RfVä§4Æö6F–öä†÷7BÆö6F–öâv†Vâ7G&–æräWVÇ2‡&÷W'G’Â&‡&Vb"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢f"‡&Ve7G"Ò6öW&6UFô†÷7E7G&–ær‡fÇVR“°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R†‡&Ve7G"’bbW&’åG'”7&VFR†Æö6F–öâåW&’Â‡&Ve7G"Â÷WBf"æeW&’’¢ö÷væW"äæf–vFT÷væ–æt'&÷w6–æt6öçFW‡B†æeW&’“°¢&WGW&âG'VS°¢66RfVä§4†—7F÷'”†÷7B†—7F÷'¢v†Vâ7G&–æräWVÇ2‡&÷W'G’Â'W6…7FFR"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢7G&–æräWVÇ2‡&÷W'G’Â'&WÆ6U7FFR"Â7G&–æt6ö×&—6öâä÷&F–æÂ“ ¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’††—7F÷'’Â&÷W'G’ÂfÇVR“°¢&WGW&âG'VS°¢66R'&÷w6W%7W&f6U&öf–ÆRæf–vF÷# ¢òòÆÆ÷r67&—G2Fò6WB&&—G&'’&÷W'F–W2öâæf–vF÷"†Rærà¢òòvöövÆR7GV'2æf–vF÷"ç6VæD&V6öâ’â7F÷&Rf÷"ÆFW"&WG&–WfÂà¢&V6÷&D76–væVD†÷7D’†æf–vF÷"Â$æf–vF÷""Â&÷W'G’“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†æf–vF÷"Â&÷W'G’ÂfÇVR“°¢&WGW&âG'VS°¢FVfVÇC ¢&WGW&âfÇ6S°¢Ð¢Ð ¢V&Æ–2§5fÇVR6ÆÄ†÷7DgVæ7F–öâ†–çBgVæ7F–öä–BÂ§5fÇVRF†—5fÇVRÂ&VDöæÇ•7ãÄ§5fÇVSâ&w2¢°¢òò†÷7BgVæ7F–öâ–çfö6F–öâæ÷B–WBv—&VB(	BF†R–çFW'&WFW"FöW2æ÷@¢òò7W'&VçFÇ’VÖ—B6ÆÄ†÷7DgVæ7F–öâ÷6öFW2âv†Vâ—BFöW2ÂÖgVæ7F–öä–@¢òòFò&Vv—7FW&VB†÷7B6ÆÆ&ÆRæB–çfö¶R—B†W&Rà¢òÒgVæ7F–öä–C°¢òÒF†—5fÇVS°¢òÒ&w3°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð ¢V&Æ–2&ööÂG'”6öçfW'D†÷7Dö&¦V7EFõ&–Ö—F—fR€¢†÷7Dö&¦V7D†æFÆR†æFÆRÀ¢7G&–ær†–çBÀ¢÷WB§5fÇVRfÇVR¢°¢–b…ö÷væW#òåö–çFW'&WFW"ÓÒçVÆÂ¢°¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð ¢f"&W6öÇWF–öâÒö÷væW"åö–çFW'&WFW"ä†÷7Dö&¦V7EF&ÆRå&W6öÇfR€¢†æFÆRÀ¢ö÷væW"åö–çFW'&WFW"ä†÷7E&W6öÇfT6öçFW‡B“°¢–b‡&W6öÇWF–öâä—4ö²bb&W6öÇWF–öâä†÷7Dö&¦V7B—2fVä§4Æö6F–öä†÷7BÆö6F–öâ¢°¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†Æö6F–öâåW&“òä'6öÇWFUW&’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢Ð ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð ¢&—fFR&ööÂG'”vWDFö7VÖVçE&÷W'G’„Fö7VÖVçBFö7VÖVçBÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚"6Fö7VÖVçB"“°¢&WGW&âG'VS°¢66R&æöFUG—R# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‚†–çB–Fö7VÖVçBäæöFUG—R“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢66R&÷væW$Fö7VÖVçB# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBäf—'7D6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBäÆ7D6†–ÆB“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&6†–ÆDæöFW2# ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†Fö7VÖVçBä6†–ÆDæöFW2åFô'&’‚’“°¢&WGW&âG'VS°¢66R&–B# ¢òòFö7VÖVçBæöFW2†fRæò–BGG&–'WFS²&WGW&âV×G’7G&–ærW"6‡&öÖR&V†f–÷"à¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&Æö6ÄæÖR# ¢òòFö7VÖVçBæöFW2†fRæòÆö6ÄæÖRW"DôÒ7V2à¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R'&VG•7FFR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…ö÷væW"ävWDFö7VÖVçE&VG•7FFR‚’“°¢&WGW&âG'VS°¢66R&6ö×DÖöFR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚$5536ö×B"“°¢&WGW&âG'VS°¢66R'7G–ÆU6†VWG2# ¢fÇVRÒö÷væW"ä7&VFTV×G•7G–ÆU6†VWDÆ—7Dö&¦V7B‚“°¢&WGW&âG'VS°¢66R'&W&VæFW&–ær# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†fÇ6R“°¢&WGW&âG'VS°¢66R&föçG2# ¢òò552föçBÆöF–ær“¢&WGW&âF†RW6W"Ö76–væVBföçG2ö&¦V7@¢òò‡6WB'’vöövÆRw2–æÆ–æR67&—C¢Fö7VÖVçBæföçG2Ò²ÆöC¢âââÂ&VG“¢âââÒ’à¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†Fö7VÖVçBÂ&föçG2"“°¢&WGW&âfÇVRåFrÒ§5fÇVUFråVæFVf–æVC°¢66R%U$Â# ¢66R&Fö7VÖVçEU$’# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†Fö7VÖVçBåU$Âóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&&6UU$’# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær€¢Fö7VÖVçBä&6UU$’óð¢Fö7VÖVçBäFö7VÖVçEU$’óð¢Fö7VÖVçBåU$Âóð¢ö&6UW&“òä'6öÇWFUW&’óð¢7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&FöÖ–â# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†Fö7VÖVçBÂ&FöÖ–â"“°¢–b‡fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…G'”vWDFö7VÖVçEW&’†Fö7VÖVçB“òä†÷7Bóòö&6UW&“òä†÷7Bóò7G&–æräV×G’“°¢Ð¢&WGW&âG'VS°¢66R&Æö6F–öâ# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ…öÆö6F–öâÂ†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢66R&6öçFVçEG—R# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†Fö7VÖVçBä6öçFVçEG—Róò'FW‡Bö‡FÖÂ"“°¢&WGW&âG'VS°¢66R'f—6–&–Æ—G•7FFR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†Fö7VÖVçBä†–FFVâò&†–FFVâ"¢'f—6–&ÆR"“°¢&WGW&âG'VS°¢66R&†–FFVâ# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†Fö7VÖVçBä†–FFVâ“°¢&WGW&âG'VS°¢66R'F—FÆR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†Fö7VÖVçBåF—FÆRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&6öö¶–R# ¢òòÖW&vR†÷7B×W'6—7FVB6öö¶–W2v—F‚ç’DôÒÖÆWfVÂ6öö¶–W0¢òò6òF†Rtb6†ÆÆVævR6â&VB&6²6öö¶–W2—B&Wf–÷W6Ç’6WBà¢f"†÷7D6öö¶–W2Òö÷væW"ä6öö¶–U&VD'&–FvRÒçVÆÂbbö÷væW"åö7W'&VçD&6UW&’ÒçVÆÀ¢ò…ö÷væW"ä6öö¶–U&VD'&–FvR…ö÷væW"åö7W'&VçD&6UW&’’óò7G&–æräV×G’¢¢7G&–æräV×G“°¢f"FöÔ6öö¶–W2ÒFö7VÖVçBä6öö¶–Róò7G&–æräV×G“°¢f"ÖW&vVBÒ7G&–ærä—4çVÆÄ÷$V×G’††÷7D6öö¶–W2’òFöÔ6öö¶–W0¢¢7G&–ærä—4çVÆÄ÷$V×G’†FöÔ6öö¶–W2’ò†÷7D6öö¶–W0¢¢†÷7D6öö¶–W2²#²"²FöÔ6öö¶–W3°¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†ÖW&vVB“°¢&WGW&âG'VS°¢66R&&öG’# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBä&öG’Â†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&†VB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBä†VBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&Fö7VÖVçDVÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBäFö7VÖVçDVÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R'67&öÆÆ–ætVÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBäFö7VÖVçDVÆVÖVçBóòFö7VÖVçBä&öG’Â†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&7W'&VçE67&—B# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ…ö÷væW"ävWD7W'&VçE67&—DVÆVÖVçB‚’Â†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&7F—fTVÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBä7F—fTVÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&FVfVÇEf–Wr# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†Fö7VÖVçBÂ%õöfVäFVfVÇEf–Wr"“°¢–b‡fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢fÇVRÒö÷væW"äWfÇVFUv—F„fVä§5&r‚'v–æF÷r"“°¢Ð¢&WGW&âG'VS°¢66R&–×ÆVÖVçFF–öâ# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†æWrfVä§4FöÔ–×ÆVÖVçFF–öä†÷7B†Fö7VÖVçB’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢66R&vWE6VÆV7F–öâ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&vWE6VÆV7F–öâ"À¢…òÂò’Óâö÷væW"ä7&VFU6VÆV7F–öåfÇVR‚’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçD'”–B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&vWDVÆVÖVçD'”–B"À¢…òÂ&w2’Óà¢°¢f"–BÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBävWDVÆVÖVçD'”–B†–B’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'VW'•6VÆV7F÷""À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBåVW'•6VÆV7F÷"‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷$ÆÂ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'VW'•6VÆV7F÷$ÆÂ"À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡6VÆV7F÷"’¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð ¢G'¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R†Fö7VÖVçBåVW'•6VÆV7F÷$ÆÂ‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFTVÆVÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTVÆVÖVçB"À¢…òÂ&w2’Óà¢°¢f"Æö6ÄæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢&F—b#°¢&WGW&âö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB€¢ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä7&VFTVÆVÖVçB†Æö6ÄæÖR’’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFTVÆVÖVçDå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTVÆVÖVçDå2"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâb`¢&w5³ÒåFrÒ§5fÇVUFräçVÆÂb`¢&w5³ÒåFrÒ§5fÇVUFråVæFVf–æV@¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"VÆ–f–VDæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB€¢ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä7&VFTVÆVÖVçDå2†æÖW76UW&’ÂVÆ–f–VDæÖR’’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&7&VFUFW‡DæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFUFW‡DæöFR"À¢…òÂ&w2’Óà¢°¢f"FFÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä7&VFUFW‡DæöFR†FF’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFT6öÖÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFT6öÖÖVçB"À¢…òÂ&w2’Óà¢°¢f"FFÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä7&VFT6öÖÖVçB†FF’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFTFö7VÖVçDg&vÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTFö7VÖVçDg&vÖVçB"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä7&VFTFö7VÖVçDg&vÖVçB‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–×÷'DæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&–×÷'DæöFR"À¢…òÂ&w2’Óà¢°¢f"æöFRÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†æöFRÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRv–×÷'DæöFRs¢&ÖWFW"—2æ÷BöbG—RtæöFRrâ"“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð ¢f"FVWÒ&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä–×÷'DæöFR†æöFRÂFVW’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFU&ævR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFU&ævR"À¢…òÂò’Óâö÷væW"åFô†÷7D÷$çVÆÂ†æWrFöÕ&ævR†Fö7VÖVçB’Â†÷7Dö&¦V7D¶–æBä÷F†W"’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFTGG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTGG&–'WFR"À¢…òÂ&w2’Óà¢°¢f"Æö6ÄæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBä7&VFTGG&–'WFR†Æö6ÄæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFTGG&–'WFTå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTGG&–'WFTå2"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"VÆ–f–VDæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBä7&VFTGG&–'WFTå2†æÖW76UW&’ÂVÆ–f–VDæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&7&VFTWfVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFTWfVçB"À¢…òÂ&w2’Óà¢°¢f"–çFW&f6TæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢$WfVçB#°¢&WGW&âö÷væW"ä7&VFTÆVv7”FöÔWfVçB†–çFW&f6TæÖR“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFUG&VUvÆ¶W"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&7&VFUG&VUvÆ¶W""À¢…òÂ&w2’Óà¢°¢f"&ö÷BÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b‡&ö÷BÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRv7&VFUG&VUvÆ¶W"s¢&ÖWFW"—2æ÷BöbG—RtæöFRrâ"“°¢Ð ¢f"v†EFõ6†÷rÒ&w2ä6÷VçBâ¢ò6öW&6UFô†÷7ET–çC3"†&w5³ÒÂæöFTf–ÇFW%6†÷räÆÂ¢¢æöFTf–ÇFW%6†÷räÆÃ°¢f"f–ÇFW"Ò&w2ä6÷VçBâ ¢òö÷væW"ä7&VFUG&VUvÆ¶W$f–ÇFW"†&w5³%Ò¢¢çVÆÃ°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢æWrfVä§5G&VUvÆ¶W$†÷7B†Fö7VÖVçBä7&VFUG&VUvÆ¶W"‡&ö÷BÂv†EFõ6†÷rÂf–ÇFW"’’À¢†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&†57F÷&vT66W72# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&†57F÷&vT66W72"À¢…òÂò’Óâö÷væW"äWfÇVFUv—F„fVä§5&r‚%&öÖ—6Rç&W6öÇfR‡G'VR’"’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WVW7E7F÷&vT66W72# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'&WVW7E7F÷&vT66W72"À¢…òÂò’Óâö÷væW"äWfÇVFUv—F„fVä§5&r‚%&öÖ—6Rç&W6öÇfR‚’"’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçG4'•FtæÖR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&vWDVÆVÖVçG4'•FtæÖR"À¢…òÂ&w2’Óà¢°¢f"VÆ–f–VDæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢#°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢æWrfVä§4‡FÖÄ6öÆÆV7F–öä†÷7B†Fö7VÖVçBävWDVÆVÖVçG4'•FtæÖR‡VÆ–f–VDæÖR’’À¢†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆöæTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&6ÆöæTæöFR"À¢…òÂ&w2’Óà¢°¢f"FVWÒ&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBä6ÆöæTæöFR†FVW’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&VæD6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&VæD6†–ÆB"À¢…òÂ&w2’Óà¢°¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ†&w5³Ò’2æöFR¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚$†–W&&6‡•&WVW7DW'&÷""Â$Fö7VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢&WGW&âö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB€¢ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBäVæD6†–ÆB†6†–ÆB’’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÖ÷fT6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'&VÖ÷fT6†–ÆB"À¢…òÂ&w2’Óà¢°¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ†&w5³Ò’2æöFR¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$Fö7VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†Fö7VÖVçBå&VÖ÷fT6†–ÆB†6†–ÆB’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WÆ6T6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'&WÆ6T6†–ÆB"À¢…òÂ&w2’Óà¢°¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ†&w5³Ò’2æöFR¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$Fö7VÖVçB&WÆ6VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢f"öÆD6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÂ†&w5³Ò’2æöFR¢çVÆÃ°¢–b†öÆD6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$Fö7VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢f"&WÆ6VBÒFö7VÖVçBå&WÆ6T6†–ÆB†6†–ÆBÂöÆD6†–ÆB“°¢ö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†–ÆB’“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&WÆ6VB“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&÷Vâ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&÷Vâ"À¢…òÂò’Óà¢°¢ö÷væW"ä÷VäFö7VÖVçDf÷%w&—FR†Fö7VÖVçB“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢Ò“°¢&WGW&âG'VS°¢66R'w&—FR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'w&—FR"À¢…òÂ&w2’Óà¢°¢ö÷væW"åw&—FTFö7VÖVçDÖ&·W†Fö7VÖVçBÂ&w2ÂVæDæWtÆ–æS¢fÇ6R“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R'w&—FVÆâ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'w&—FVÆâ"À¢…òÂ&w2’Óà¢°¢ö÷væW"åw&—FTFö7VÖVçDÖ&·W†Fö7VÖVçBÂ&w2ÂVæDæWtÆ–æS¢G'VR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R&6Æ÷6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&6Æ÷6R"À¢…òÂò’Óâ§5fÇVRåVæFVf–æVB“°¢&WGW&âG'VS°¢66R&FDWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&FDWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"äFD'&÷w6W$WfVçDÆ—7FVæW"…ö÷væW"åöFö7VÖVçDWfVçDÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fTWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"å&VÖ÷fT'&÷w6W$WfVçDÆ—7FVæW"…ö÷væW"åöFö7VÖVçDWfVçDÆ—7FVæW'2Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&F—7F6„WfVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&F—7F6„WfVçB"À¢…òÂ&w2’Óà¢°¢f"WfVçEfÇVRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ…ö÷væW"äF—7F6„Fö7VÖVçD†÷7DWfVçB†Fö7VÖVçBÂWfVçEfÇVR’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'7F'Ef–WuG&ç6—F–öâ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'7F'Ef–WuG&ç6—F–öâ"À¢…òÂ&w2’Óâö÷væW"ä7&VFUf–WuG&ç6—F–öå&W7VÇB€¢&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVB’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÆV6T6GW&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢'&VÆV6T6GW&R"À¢…òÂò’Óâ§5fÇVRåVæFVf–æVBÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6öçF–ç2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&6öçF–ç2"À¢…òÂ&w2’Óà¢°¢f"÷F†W"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†÷F†W"ÒçVÆÂbbFö7VÖVçBä6öçF–ç2†÷F†W"’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ö×&TFö7VÖVçE÷6—F–öâ# ¢fÇVRÒö÷væW"ä7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR†Fö7VÖVçB“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçG4'”æÖR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&vWDVÆVÖVçG4'”æÖR"À¢…òÂ&w2’Óà¢°¢f"æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†æÖR’¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢f"&W7VÇG2ÒæWrÆ—7CÄVÆVÖVçCâ‚“°¢6öÆÆV7DVÆVÖVçG4'”æÖR†Fö7VÖVçBäFö7VÖVçDVÆVÖVçBÂæÖRÂ&W7VÇG2“°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R‡&W7VÇG2ä67CÄæöFSâ‚’åFô'&’‚’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçG4'”6Æ74æÖR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö7VÖVçBÀ¢&vWDVÆVÖVçG4'”6Æ74æÖR"À¢…òÂ&w2’Óà¢°¢f"6Æ74æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R†6Æ74æÖR’¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æWrfVä§4‡FÖÄ6öÆÆV7F–öä†÷7B†Fö7VÖVçBävWDVÆVÖVçG4'”6Æ74æÖR†6Æ74æÖR’’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢òòfÆÂ&6²FòW6W"Ö76–væVB&÷W'F–W2†RærâFö7VÖVçBæföçG2À¢òòFö7VÖVçBåõöwv'6WB'’vöövÆR67&—G2’à¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†Fö7VÖVçBÂ&÷W'G’“°¢&WGW&âfÇVRåFrÒ§5fÇVUFråVæFVf–æVC°¢Ð¢Ð ¢&—fFR7FF–2W&’G'”vWDFö7VÖVçEW&’„Fö7VÖVçBFö7VÖVçB¢°¢–b†Fö7VÖVçBÓÒçVÆÂ¢°¢&WGW&âçVÆÃ°¢Ð ¢f"&rÐ¢Fö7VÖVçBåU$Âóð¢Fö7VÖVçBäFö7VÖVçEU$’óð¢Fö7VÖVçBä&6UU$“° ¢&WGW&âW&’åG'”7&VFR‡&rÂW&”¶–æBä'6öÇWFRÂ÷WBf"W&’’òW&’¢çVÆÃ°¢Ð ¢&—fFR&ööÂG'”vWDæöFU&÷W'G’„æöFRæöFRÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æöFRäæöFTæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&æöFUG—R# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‚†–çB–æöFRäæöFUG—R“°¢&WGW&âG'VS°¢66R&æöFUfÇVR# ¢fÇVRÒæöFRäæöFUfÇVRÓÒçVÆÀ¢ò§5fÇVRäçVÆÀ¢¢§5fÇVRäg&öÕ7G&–ær†æöFRäæöFUfÇVR“°¢&WGW&âG'VS°¢66R'FW‡D6öçFVçB# ¢fÇVRÒæöFRåFW‡D6öçFVçBÓÒçVÆÀ¢ò§5fÇVRäçVÆÀ¢¢§5fÇVRäg&öÕ7G&–ær†æöFRåFW‡D6öçFVçB“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†æöFRå&VçDæöFR“°¢&WGW&âG'VS°¢66R&÷væW$Fö7VÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†æöFRä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†æöFRäf—'7D6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†æöFRäÆ7D6†–ÆB“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†æöFRå&Wf–÷W56–&Æ–ær“°¢&WGW&âG'VS°¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†æöFRäæW‡E6–&Æ–ær“°¢&WGW&âG'VS°¢66R&6†–ÆDæöFW2# ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†æöFRä6†–ÆDæöFW2åFô'&’‚’“°¢&WGW&âG'VS°¢66R&6ö×&TFö7VÖVçE÷6—F–öâ# ¢fÇVRÒö÷væW"ä7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR†æöFR“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDVÆVÖVçE&÷W'G’„VÆVÖVçBVÆVÖVçBÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&–B# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBä–Bóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&6Æ74æÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBä6Æ74æÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&æÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚&æÖR"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'FtæÖR# ¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBåFtæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&Æö6ÄæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBäÆö6ÄæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&‡&Vb# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…&W6öÇfTVÆVÖVçEW&Å&÷W'G’†VÆVÖVçBÂ&‡&Vb"’“°¢&WGW&âG'VS°¢66R'7&2# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…&W6öÇfTVÆVÖVçEW&Å&÷W'G’†VÆVÖVçBÂ'7&2"’“°¢&WGW&âG'VS°¢66R&æöæ6R# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚&æöæ6R"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'F—FÆR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚'F—FÆR"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'fÇVR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…&VDVÆVÖVçEfÇVR†VÆVÖVçB’“°¢&WGW&âG'VS°¢66R'G—R"v†Vâ7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“ ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær…&VD–çWEG—R†VÆVÖVçB’“°¢&WGW&âG'VS°¢66R&6†V6¶VB"v†Vâ—46†V6¶&ÆT–çWDVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ„VÆVÖVçE7FFTÖævW"ä–ç7Fæ6Rä—46†V6¶VB†VÆVÖVçB’“°¢&WGW&âG'VS°¢66R'F$–æFW‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"…&VDVÆVÖVçEF$–æFW‚†VÆVÖVçB’“°¢&WGW&âG'VS°¢66R&6öçFVçEv–æF÷r"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWD”g&ÖT6öçFVçEv–æF÷tf÷$7W'&VçD6öçFW‡B†VÆVÖVçB“°¢&WGW&âG'VS°¢66R&6öçFVçDFö7VÖVçB"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ä6ä7W'&VçD6öçFW‡D66W74”g&ÖTFö7VÖVçB†VÆVÖVçB¢òö÷væW"ävWD÷$7&VFT”g&ÖT6öçFVçDFö7VÖVçB†VÆVÖVçB¢¢§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R'6æF&÷‚"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒVÆVÖVçBäæÖW76UW&’ÓÒ&‡GG¢ò÷wwrçs2æ÷&ró““’÷†‡FÖÂ ¢òö÷væW"ävWD÷$7&VFTFöÕFö¶VäÆ—7Ef–Wr†VÆVÖVçBå6æF&÷„Æ—7B¢¢§5fÇVRåVæFVf–æVC°¢&WGW&âG'VS°¢66R'67&öÆÆ–ær"v†Vâ—4”g&ÖTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚'67&öÆÆ–ær"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&‡GGWV—b"v†Vâ—4ÖWFVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚&‡GGÖWV—b"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&7–æ2"v†Vâ—567&—DVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBä†4GG&–'WFR‚&7–æ2"’“°¢&WGW&âG'VS°¢66R'G—R"v†Vâ—567&—DVÆVÖVçB†VÆVÖVçB“ ¢66R&6†'6WB"v†Vâ—567&—DVÆVÖVçB†VÆVÖVçB“ ¢66R&–çFVw&—G’"v†Vâ—567&—DVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‡&÷W'G’’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&7&÷74÷&–v–â"v†Vâ—567&—DVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒVÆVÖVçBä†4GG&–'WFR‚&7&÷76÷&–v–â"¢ò§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚&7&÷76÷&–v–â"’óò7G&–æräV×G’¢¢§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&6öçFVçB"v†Vâ—5FV×ÆFTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWD÷$7&VFUFV×ÆFT6öçFVçB†VÆVÖVçB“°¢&WGW&âG'VS°¢66R&6öçFVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBävWDGG&–'WFR‚&6öçFVçB"’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'6†F÷u&ö÷B# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå6†F÷u&ö÷B“°¢&WGW&âG'VS°¢66R&6ö×ÆWFR"v†Vâ—4–ÖvTVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ‡G'VR“°¢&WGW&âG'VS°¢66R&÷Vâ"v†Vâ—4F–ÆötVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBä†4GG&–'WFR‚&÷Vâ"’“°¢&WGW&âG'VS°¢66R'6†÷tÖöFÂ"v†Vâ—4F–ÆötVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6†÷tÖöFÂ"À¢…òÂò’Óà¢°¢VÆVÖVçBå6WDGG&–'WFR‚&÷Vâ"Â""“°¢VÆVÖVçBå6WDGG&–'WFR‚&FF×F÷ÖÆ–W""Â&ÖöFÂ"“°¢òò÷væW$Fö7VÖVçBvÆ·2W÷&VçDæöFRFòf–æBF†RFö7VÖVçBà¢òò–bF†RVÆVÖVçBv2ö'F–æVBf–vWDVÆVÖVçD'”–B–âfVä¥0¢òò†÷7BÖö&¦V7B6öçFW‡BÂF†R4Å"&VçB6†–â—2–çF7Bæ@¢òò÷væW$Fö7VÖVçB—2æöâÖçVÆÂâ–b—B—2çVÆÂ†RærâFWF6†V@¢òòVÆVÖVçB’Â6¶—F†RF÷Æ–W"F‚(	BF†RF–Æörv–ÆÂ7F–ÆÀ¢òò&VæFW"2æ÷&ÖÂ÷6—F–öæVBVÆVÖVçBf–552à¢f"Fö2ÒVÆVÖVçBä÷væW$Fö7VÖVçC°¢–b†Fö2ÒçVÆÂ¢°¢–b‚Fö2åF÷Æ–W"ä6öçF–ç2†VÆVÖVçB’¢Fö2åF÷Æ–W"äFB†VÆVÖVçB“°¢Ð¢òòÖ&²7G–ÆRöÆ–÷WB÷–çBF—'G’6òF†R&VæFW&W"&V'V–ÆG0¢òòF†R–çBG&VRæB–6·2WF†RF÷Æ–W"6†ævRà¢fVä§4'&÷w6W%67&—DVæv–æRä–çfÆ–FFU–çDf÷$VÆVÖVçB†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6†÷r"v†Vâ—4F–ÆötVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6†÷r"À¢…òÂò’Óà¢°¢VÆVÖVçBå6WDGG&–'WFR‚&÷Vâ"Â""“°¢fVä§4'&÷w6W%67&—DVæv–æRä–çfÆ–FFU–çDf÷$VÆVÖVçB†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6Æ÷6R"v†Vâ—4F–ÆötVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&6Æ÷6R"À¢…òÂ&w2’Óà¢°¢VÆVÖVçBå&VÖ÷fTGG&–'WFR‚&÷Vâ"“°¢VÆVÖVçBå&VÖ÷fTGG&–'WFR‚&FF×F÷ÖÆ–W""“°¢VÆVÖVçBä÷væW$Fö7VÖVçCòåF÷Æ–W"å&VÖ÷fR†VÆVÖVçB“°¢–b†&w2ä6÷VçBâ¢°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†VÆVÖVçBÂ'&WGW&åfÇVR"Â&w5³Ò“°¢Ð ¢ö÷væW"äF—7F6„WfVçDf÷$VÆVÖVçB†VÆVÖVçBÂ&6Æ÷6R"“°¢fVä§4'&÷w6W%67&—DVæv–æRä–çfÆ–FFU–çDf÷$VÆVÖVçB†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WGW&åfÇVR"v†Vâ—4F–ÆötVÆVÖVçB†VÆVÖVçB“ ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ'&WGW&åfÇVR"“°¢–b‡fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡7G&–æräV×G’“°¢Ð¢&WGW&âG'VS°¢66R'FW‡D6öçFVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBåFW‡D6öçFVçBóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäæW‡E6–&Æ–ær“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå&Wf–÷W56–&Æ–ær“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå&VçDæöFR“°¢&WGW&âG'VS°¢66R&–ææW$…DÔÂ# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†VÆVÖVçBä–ææW$…DÔÂóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&öæÆöB# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ&öæÆöB"“°¢&WGW&âG'VS°¢66R&öæW'&÷"# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ&öæW'&÷""“°¢&WGW&âG'VS°¢66R&f—'7DVÆVÖVçD6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäf—'7DVÆVÖVçD6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7DVÆVÖVçD6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäÆ7DVÆVÖVçD6†–ÆB“°¢&WGW&âG'VS°¢66R&6†–ÆDVÆVÖVçD6÷VçB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"†VÆVÖVçBä6†–ÆDVÆVÖVçD6÷VçB“°¢&WGW&âG'VS°¢66R'&VçDVÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå&VçDVÆVÖVçB“°¢&WGW&âG'VS°¢66R&÷væW$Fö7VÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢&WGW&âG'VS°¢66R&GG&–'WFW2# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBäGG&–'WFW2Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢66R&6Æ74Æ—7B# ¢fÇVRÒö÷væW"ävWD÷$7&VFTFöÕFö¶VäÆ—7Ef–Wr†VÆVÖVçBä6Æ74Æ—7B“°¢&WGW&âG'VS°¢66R&FF6WB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†æWrfVä§4FöÕ7G&–ætÖ†÷7B†VÆVÖVçB’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢66R&FDWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&FDWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"äFD'&÷w6W$WfVçDÆ—7FVæW"…ö÷væW"ävWDVÆVÖVçDÆ—7FVæW'2†VÆVÖVçB’Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fTWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"å&VÖ÷fT'&÷w6W$WfVçDÆ—7FVæW"…ö÷væW"ävWDVÆVÖVçDÆ—7FVæW'2†VÆVÖVçB’Â&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&F—7F6„WfVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&F—7F6„WfVçB"À¢…òÂ&w2’Óà¢°¢f"WfVçEfÇVRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ…ö÷væW"äF—7F6„VÆVÖVçD†÷7DWfVçB†VÆVÖVçBÂWfVçEfÇVR’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6Æ–6²# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&6Æ–6²"À¢…òÂò’Óà¢°¢–b†VÆVÖVçBä†4GG&–'WFR‚&F—6&ÆVB"’¢°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð ¢f"WfVçEfÇVRÒö÷væW"ä7&VFT'&÷w6W$FöÔWfVçEfÇVR€¢VÆVÖVçBÀ¢&6Æ–6²"À¢æWr'&÷w6W$FöÔWfVçD–æ—@¢°¢'V&&ÆW2ÒG'VRÀ¢6æ6VÆ&ÆRÒG'VRÀ¢6ö×÷6VBÒG'VRÀ¢—5G'W7FVBÒfÇ6P¢ÒÀ¢÷WBf"F—7F6…7FFR“°¢òÒö÷væW"äF—7F6„VÆVÖVçDWfVçEv—F„7F—fF–öâ€¢VÆVÖVçBÀ¢&6Æ–6²"À¢WfVçEfÇVRÀ¢F—7F6…7FFR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WVW7DgVÆÇ67&VVâ# ¢66R'vV&¶—E&WVW7DgVÆÇ67&VVâ# ¢66R&Ö÷¥&WVW7DgVÆÅ67&VVâ# ¢66R&×5&WVW7DgVÆÇ67&VVâ# ¢°¢òòW&Ö—76–öç2ÕöÆ–7’Væf÷&6VÖVçC¢&WVW7DgVÆÇ67&VVâ—2¢òòöÆ–7’Ö6öçG&öÆÆVBfVGW&R†FVfVÇBÆÆ÷vÆ—7Bw6VÆbr’à¢òòv†Vâ†VFW"öÆ–7’—2&W6VçBæBFVæ–W2—BÂF†P¢òò&öÖ—6R×W7B&V¦V7B…G—TW'&÷"’–ç7FVBöb&W6öÇf–ærà¢f"÷&–v–âÒö÷væW"åG'•&W6öÇfT7W'&VçD÷&–v–â†VÆVÖVçB“°¢–b‚ö÷væW"ä—4fVGW&TÆÆ÷vVD'•öÆ–7’„fVä'&÷w6W"ä6÷&Rå6V7W&—G’åöÆ–7”6öçG&öÆÆVDfVGW&RägVÆÇ67&VVâÂ÷&–v–â’¢°¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&÷W'G’À¢…òÂò’Óâö÷væW"ä7&VFU&V¦V7FVE&öÖ—6R‚%W&Ö—76–öâFVæ–VB'’W&Ö—76–öç2ÕöÆ–7’"Â%6V7W&—G”W'&÷""’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢Ð ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&÷W'G’À¢…òÂò’Óâö÷væW"ä7&VFU&W6öÇfVE&öÖ—6R„§5fÇVRåVæFVf–æVB’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢Ð¢66R'6WD6GW&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6WD6GW&R"À¢…òÂò’Óâ§5fÇVRåVæFVf–æVBÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WE&÷W'F–W2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6WE&÷W'F–W2"À¢‡F†—5fÇVRÂ&w2’Óà¢°¢f"F&vWBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄVÆVÖVçCâ‡F†—5fÇVR’óòVÆVÖVçC°¢ö÷væW"äÇ”VÆVÖVçE&÷W'F–W2‡F&vWBÂ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&æ–ÖFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&æ–ÖFR"À¢…òÂ&w2’Óà¢°¢f"¶W–g&ÖW2Ò&w2ä6÷VçBâò&w5³Ò¢§5fÇVRäçVÆÃ°¢f"÷F–öç2Ò&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æWrfVä§4æ–ÖF–öä†÷7B†VÆVÖVçBÂ¶W–g&ÖW2Â÷F–öç2’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&fö7W2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&fö7W2"À¢…òÂò’Óà¢°¢ö÷væW"äfö7W4VÆVÖVçB†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R&&ÇW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&&ÇW""À¢…òÂò’Óà¢°¢ö÷væW"ä&ÇW$VÆVÖVçB†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R'7V&Ö—B# ¢òò…DÔÄf÷&ÔVÆVÖVçBç7V&Ö—B‚’(	B&T4D4„6ÆÇ2f÷&Òç7V&Ö—B‚’&öw&ÖÖF–6ÆÇ’à¢–b‡7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ$dõ$Ò"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'7V&Ö—B"À¢…òÂò’Óà¢°¢ö÷væW"å7V&Ö—Df÷&Ôg&öÕ67&—B†VÆVÖVçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢Ð¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢66R&vWDGG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&vWDGG&–'WFR"À¢‡F†—5fÇVRÂ&w2’Óà¢°¢f"F&vWBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄVÆVÖVçCâ‡F†—5fÇVR“°¢–b‡F&vWBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRvvWDGG&–'WFRröâtVÆVÖVçBs¢–ÆÆVvÂ–çfö6F–öâ"“°¢Ð ¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"GG&–'WFUfÇVRÒF&vWBävWDGG&–'WFR†GG&–'WFTæÖR“°¢&WGW&âGG&–'WFUfÇVRÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†GG&–'WFUfÇVR“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&†4GG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&†4GG&–'WFR"À¢…òÂ&w2’Óà¢°¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBä†4GG&–'WFR†GG&–'WFTæÖR’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WDGG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6WDGG&–'WFR"À¢…òÂ&w2’Óà¢°¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"GG&–'WFUfÇVRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢VÆVÖVçBå6WDGG&–'WFR†GG&–'WFTæÖRÂGG&–'WFUfÇVR“°¢–b‚‡7G&–æräWVÇ2†GG&–'WFTæÖRÂ'7&2"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†GG&–'WFTæÖRÂ'7&6Fö2"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’’b`¢—4”g&ÖTVÆVÖVçB†VÆVÖVçB’¢°¢ö÷væW"åVWVTg&ÖTVÆVÖVçDÆöB†VÆVÖVçB“°¢Ð¢–b„—4”g&ÖTVÆVÖVçB†VÆVÖVçB’b`¢‡7G&–æräWVÇ2†GG&–'WFTæÖRÂ'v–GF‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†GG&–'WFTæÖRÂ&†V–v‡B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†GG&–'WFTæÖRÂ'7G–ÆR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’’¢°¢ö÷væW"äæ÷F–g•&W6—¦Tö'6W'fW'2†VÆVÖVçB“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&vWDGG&–'WFTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&vWDGG&–'WFTæöFR"À¢…òÂ&w2’Óà¢°¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBävWDGG&–'WFTæöFR†GG&–'WFTæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDGG&–'WFTæöFTå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&vWDGG&–'WFTæöFTå2"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"Æö6ÄæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBävWDGG&–'WFTæöFTå2†æÖW76UW&’ÂÆö6ÄæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fTGG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&VÖ÷fTGG&–'WFR"À¢…òÂ&w2’Óà¢°¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢VÆVÖVçBå&VÖ÷fTGG&–'WFR†GG&–'WFTæÖR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WDGG&–'WFTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'6WDGG&–'WFTæöFR"À¢…òÂ&w2’Óà¢°¢f"GG"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’¢çVÆÃ°¢–b†GG"ÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBå6WDGG&–'WFTæöFR†GG"’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÖ÷fTGG&–'WFTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&VÖ÷fTGG&–'WFTæöFR"À¢…òÂ&w2’Óà¢°¢f"GG"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’¢çVÆÃ°¢–b†GG"ÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†VÆVÖVçBå&VÖ÷fTGG&–'WFTæöFR†GG"’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'FövvÆTGG&–'WFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'FövvÆTGG&–'WFR"À¢…òÂ&w2’Óà¢°¢f"GG&–'WFTæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&ööÃòf÷&6RÒçVÆÃ°¢–b†&w2ä6÷VçBâ¢°¢f÷&6RÒ&w5³ÒåFr7v—F6€¢°¢§5fÇVUFrä&ööÆVâÓâ&w5³Òä4&ööÆVâ‚’À¢§5fÇVUFråVæFVf–æVBÓâçVÆÂÀ¢§5fÇVUFräçVÆÂÓâfÇ6RÀ¢òÓâ7G&–æräWVÇ2„6öW&6UFô†÷7E7G&–ær†&w5³Ò’Â&fÇ6R"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R¢Ó°¢Ð ¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBåFövvÆTGG&–'WFR†GG&–'WFTæÖRÂf÷&6R’“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&†4GG&–'WFW2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&†4GG&–'WFW2"À¢…òÂò’Óâ§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBä†4GG&–'WFW2‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&GF6…6†F÷r# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&GF6…6†F÷r"À¢…òÂ&w2’Óà¢°¢f"–æ—BÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢f"ÖöFUfÇVRÒö÷væW"å&VD§5&÷W'G’†–æ—BÂ&ÖöFR"“°¢f"ÖöFUFW‡BÒÖöFUfÇVRåFrÓÒ§5fÇVUFråVæFVf–æV@¢ò7G&–æräV×G¢¢6öW&6UFô†÷7E7G&–ær†ÖöFUfÇVR“°¢f"ÖöFRÒ7G&–æräWVÇ2†ÖöFUFW‡BÂ&6Æ÷6VB"Â7G&–æt6ö×&—6öâä÷&F–æÂ¢ò6†F÷u&ö÷DÖöFRä6Æ÷6V@¢¢7G&–æräWVÇ2†ÖöFUFW‡BÂ&÷Vâ"Â7G&–æt6ö×&—6öâä÷&F–æÂ¢ò6†F÷u&ö÷DÖöFRä÷Và¢¢…6†F÷u&ö÷DÖöFSò–çVÆÃ° ¢–b†ÖöFRÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRvGF6…6†F÷rröâtVÆVÖVçBs¢ÖöFR×W7B&Rv÷Vâr÷"v6Æ÷6VBrâ"“°¢Ð ¢f"6Æ÷D76–væÖVçEfÇVRÒö÷væW"å&VD§5&÷W'G’†–æ—BÂ'6Æ÷D76–væÖVçB"“°¢f"6Æ÷D76–væÖVçEFW‡BÒ6Æ÷D76–væÖVçEfÇVRåFrÓÒ§5fÇVUFråVæFVf–æV@¢ò&æÖVB ¢¢6öW&6UFô†÷7E7G&–ær‡6Æ÷D76–væÖVçEfÇVR“°¢f"6Æ÷D76–væÖVçBÒ7G&–æräWVÇ2‡6Æ÷D76–væÖVçEFW‡BÂ&ÖçVÂ"Â7G&–æt6ö×&—6öâä÷&F–æÂ¢ò6Æ÷D76–væÖVçDÖöFRäÖçVÀ¢¢6Æ÷D76–væÖVçDÖöFRäæÖVC° ¢G'¢°¢f"6†F÷u&ö÷BÒVÆVÖVçBäGF6…6†F÷r†æWr6†F÷u&ö÷D–æ—@¢°¢ÖöFRÒÖöFRåfÇVRÀ¢FVÆVvFW4fö7W2Òö÷væW"å&VD§4&ööÅ&÷W'G’†–æ—BÂ&FVÆVvFW4fö7W2"’À¢6Æ÷D76–væÖVçBÒ6Æ÷D76–væÖVç@¢Ò“°¢f"6†F÷u&ö÷EfÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡6†F÷u&ö÷B“°¢f"Ww&FTg&vÖVçEfÇVRÒö÷væW"å&VD§5&÷W'G’†–æ—BÂ'6†G•Ww&FTg&vÖVçB"“°¢ö÷væW"åö–çFW'&WFW"ä6÷”†÷7Dö&¦V7D÷vå&÷W'F–W2‡Ww&FTg&vÖVçEfÇVRÂ6†F÷u&ö÷EfÇVR“°¢&WGW&â6†F÷u&ö÷EfÇVS°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&VæD6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&VæD6†–ÆB"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢Væv–æTÆöt6ö×Båv&â€¢B%´fVä§5&ö&UÒVÆVÖVçBæVæD6†–ÆBVçFW&–ær&VçC×¶VÆVÖVçBäæöFTæÖWÒ7¶VÆVÖVçBä–GÒ6†–ÆC×¶6†–ÆBäæöFTæÖWÒG—S×¶6†–ÆBäæöFUG—WÒ6†–ÆE&VçC×¶6†–ÆBå&VçDæöFSòäæöFTæÖRóò&çVÆÂ'Ò6†–ÆD6÷VçC×²†6†–ÆB—26öçF–æW$æöFR6†–ÆD6öçF–æW"ò6†–ÆD6öçF–æW"ä6†–ÆDæöFW2äÆVæwF‚¢—Ò"À¢Æöt6FVv÷'’ä¦f67&—B“°¢f"VæFVBÒVÆVÖVçBäVæD6†–ÆB†6†–ÆB“°¢Væv–æTÆöt6ö×Båv&â€¢B%´fVä§5&ö&UÒVÆVÖVçBæVæD6†–ÆB&WGW&æVB&VçC×¶VÆVÖVçBäæöFTæÖWÒ7¶VÆVÖVçBä–GÒVæFVC×¶VæFVBäæöFTæÖWÒ6†–ÆD6÷VçC×²†6†–ÆB—26öçF–æW$æöFRgFW$6†–ÆD6öçF–æW"ògFW$6†–ÆD6öçF–æW"ä6†–ÆDæöFW2äÆVæwF‚¢—Ò"À¢Æöt6FVv÷'’ä¦f67&—B“°¢ö÷væW"åVWVTg&ÖTÆöG4f÷%G&VR†VæFVB“°¢–b†6†–ÆB—2VÆVÖVçB6†–ÆDVÆVÖVçBb`¢7G&–æräWVÇ2†6†–ÆDVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢ö÷væW"äW†V7WFTG–æÖ–567&—DVÆVÖVçB†6†–ÆDVÆVÖVçB“°¢Ð ¢&WGW&âö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VæFVB’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&VæB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&VæB"À¢…òÂ&w2’Óà¢°¢f÷&V6‚‡f"&r–â&w2¢°¢æöFR6†–ÆBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&r“°¢–b†6†–ÆBÓÒçVÆÂ¢°¢6†–ÆBÒVÆVÖVçBä÷væW$Fö7VÖVçCòä7&VFUFW‡DæöFR„6öW&6UFô†÷7E7G&–ær†&r’“°¢Ð ¢–b†6†–ÆBÓÒçVÆÂ¢°¢6öçF–çVS°¢Ð ¢VÆVÖVçBäVæD6†–ÆB†6†–ÆB“°¢ö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†–ÆB’“°¢ö÷væW"åVWVTg&ÖTÆöG4f÷%G&VR†6†–ÆB“°¢–b†6†–ÆB—2VÆVÖVçB6†–ÆDVÆVÖVçBb`¢7G&–æräWVÇ2†6†–ÆDVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢ö÷væW"äW†V7WFTG–æÖ–567&—DVÆVÖVçB†6†–ÆDVÆVÖVçB“°¢Ð¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WVæB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&WVæB"À¢…òÂ&w2’Óà¢°¢f"&VfW&Væ6TæöFRÒVÆVÖVçBäf—'7D6†–ÆC°¢f÷&V6‚‡f"&r–â&w2¢°¢–b…ö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&r’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢æöFR6†–ÆBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&r“°¢–b†6†–ÆBÓÒçVÆÂ¢°¢6†–ÆBÒVÆVÖVçBä÷væW$Fö7VÖVçCòä7&VFUFW‡DæöFR„6öW&6UFô†÷7E7G&–ær†&r’“°¢Ð ¢–b†6†–ÆBÓÒçVÆÂ¢°¢6öçF–çVS°¢Ð ¢f"67&—D6æF–FFW2Ò6†–ÆB—2Fö7VÖVçDg&vÖVçBg&vÖVç@¢òg&vÖVçBä6†–ÆDæöFW2äöeG—SÄVÆVÖVçCâ‚’åFô'&’‚¢¢6†–ÆB—2VÆVÖVçB6†–ÆDVÆVÖVç@¢òæWuµÒ²6†–ÆDVÆVÖVçBÐ¢¢'&’äV×G“ÄVÆVÖVçCâ‚“° ¢VÆVÖVçBä–ç6W'D&Vf÷&R†6†–ÆBÂ&VfW&Væ6TæöFR“°¢ö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†–ÆB’“°¢ö÷væW"åVWVTg&ÖTÆöG4f÷%G&VR†6†–ÆB“°¢f÷&V6‚‡f"67&—DVÆVÖVçB–â67&—D6æF–FFW2¢°¢–b‡7G&–æräWVÇ2‡67&—DVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢ö÷væW"äW†V7WFTG–æÖ–567&—DVÆVÖVçB‡67&—DVÆVÖVçB“°¢Ð¢Ð¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆöæTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&6ÆöæTæöFR"À¢…òÂ&w2’Óà¢°¢f"FVWÒ&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBä6ÆöæTæöFR†FVW’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–ç6W'D&Vf÷&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&–ç6W'D&Vf÷&R"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢f"&VfW&Væ6TæöFRÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢f"–ç6W'FVBÒVÆVÖVçBä–ç6W'D&Vf÷&R†6†–ÆBÂ&VfW&Væ6TæöFR“°¢ö÷væW"åVWVTg&ÖTÆöG4f÷%G&VR†–ç6W'FVB“°¢–b†6†–ÆB—2VÆVÖVçB6†–ÆDVÆVÖVçBb`¢7G&–æräWVÇ2†6†–ÆDVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢ö÷væW"äW†V7WFTG–æÖ–567&—DVÆVÖVçB†6†–ÆDVÆVÖVçB“°¢Ð ¢&WGW&âö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†–ç6W'FVB’“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fT6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&VÖ÷fT6†–ÆB"À¢…òÂ&w2’Óà¢°¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå&VÖ÷fT6†–ÆB†6†–ÆB’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WÆ6T6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&WÆ6T6†–ÆB"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â%&WÆ6VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢f"öÆD6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†öÆD6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢f"&WÆ6VBÒVÆVÖVçBå&WÆ6T6†–ÆB†6†–ÆBÂöÆD6†–ÆB“°¢–b†6†–ÆB—2VÆVÖVçB6†–ÆDVÆVÖVçBb`¢7G&–æräWVÇ2†6†–ÆDVÆVÖVçBåFtæÖRÂ%45$•B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢ö÷væW"äW†V7WFTG–æÖ–567&—DVÆVÖVçB†6†–ÆDVÆVÖVçB“°¢Ð ¢ö÷væW"åWw&FT7W7FöÔVÆVÖVçEG&VT–dFVf–æVB…ö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†–ÆB’“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&WÆ6VB“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'&VÖ÷fR"À¢…òÂò’Óà¢°¢–b†VÆVÖVçBå&VçDæöFR—26öçF–æW$æöFR&VçB¢°¢&VçBå&VÖ÷fT6†–ÆB†VÆVÖVçB“°¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&ÖF6†W2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&ÖF6†W2"À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBäÖF6†W2‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†fÇ6R“°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6Æ÷6W7B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&6Æ÷6W7B"À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBä6Æ÷6W7B‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'VW'•6VÆV7F÷""À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBåVW'•6VÆV7F÷"‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷$ÆÂ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢'VW'•6VÆV7F÷$ÆÂ"À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡6VÆV7F÷"’¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð ¢G'¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R†VÆVÖVçBåVW'•6VÆV7F÷$ÆÂ‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçG4'•FtæÖR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&vWDVÆVÖVçG4'•FtæÖR"À¢…òÂ&w2’Óà¢°¢f"VÆ–f–VDæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢"¢#°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢æWrfVä§4‡FÖÄ6öÆÆV7F–öä†÷7B†VÆVÖVçBävWDVÆVÖVçG4'•FtæÖR‡VÆ–f–VDæÖR’’À¢†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçG4'”6Æ74æÖR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&vWDVÆVÖVçG4'”6Æ74æÖR"À¢…òÂ&w2’Óà¢°¢f"6Æ74æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢æWrfVä§4‡FÖÄ6öÆÆV7F–öä†÷7B†VÆVÖVçBävWDVÆVÖVçG4'”6Æ74æÖR†6Æ74æÖR’’À¢†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–ç6W'DF¦6VçD…DÔÂ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÀ¢&–ç6W'DF¦6VçD…DÔÂ"À¢…òÂ&w2’Óà¢°¢f"÷6—F–öâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"‡FÖÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢ö÷væW"ä–ç6W'DF¦6VçD‡FÖÂ†VÆVÖVçBÂ÷6—F–öâÂ‡FÖÂ“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢òòÒÒÒvVöÖWG'’òÆ–÷WBÒÒÐ¢66R&æöFUG—R# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‚†–çB–VÆVÖVçBäæöFUG—R“°¢&WGW&âG'VS°¢66R&—46öææV7FVB# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†VÆVÖVçBä—46öææV7FVB“°¢&WGW&âG'VS°¢66R&6†–ÆDæöFW2# ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†VÆVÖVçBä6†–ÆDæöFW2åFô'&’‚’“°¢&WGW&âG'VS°¢66R&6†–ÆG&Vâ# ¢°¢f"¶–G2ÒæWrÆ—7CÄæöFSâ‚“°¢f÷"†–çB’Ò²’ÂVÆVÖVçBä6†–ÆDæöFW2äÆVæwFƒ²’²²¢–b†VÆVÖVçBä6†–ÆDæöFW5¶•Ò—2VÆVÖVçB’¶–G2äFB†VÆVÖVçBä6†–ÆDæöFW5¶•Ò“°¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†¶–G2“°¢Ð¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäf—'7D6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäÆ7D6†–ÆB“°¢&WGW&âG'VS°¢66R&æW‡DVÆVÖVçE6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBäæW‡DVÆVÖVçE6–&Æ–ær“°¢&WGW&âG'VS°¢66R'&Wf–÷W4VÆVÖVçE6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†VÆVÖVçBå&Wf–÷W4VÆVÖVçE6–&Æ–ær“°¢&WGW&âG'VS°¢66R&6öçF–ç2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÂ&6öçF–ç2"À¢…òÂ&w2’Óà¢°¢f"÷F†W"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†÷F†W"ÒçVÆÂbbVÆVÖVçBä6öçF–ç2†÷F†W"’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ö×&TFö7VÖVçE÷6—F–öâ# ¢fÇVRÒö÷væW"ä7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR†VÆVÖVçB“°¢&WGW&âG'VS°¢66R&öfg6WEv–GF‚# ¢66R&öfg6WD†V–v‡B# ¢66R&öfg6WDÆVgB# ¢66R&öfg6WEF÷# ¢66R&6Æ–VçEv–GF‚# ¢66R&6Æ–VçD†V–v‡B# ¢66R&6Æ–VçDÆVgB# ¢66R&6Æ–VçEF÷# ¢66R'67&öÆÅv–GF‚# ¢66R'67&öÆÄ†V–v‡B# ¢66R'67&öÆÅF÷# ¢66R'67&öÆÄÆVgB# ¢fÇVRÒö÷væW"å&VDVÆVÖVçDÆ–÷WDF–ÖVç6–öâ†VÆVÖVçBÂ&÷W'G’“°¢&WGW&âG'VS°¢66R&vWD&÷VæF–æt6Æ–VçE&V7B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢VÆVÖVçBÂ&vWD&÷VæF–æt6Æ–VçE&V7B"À¢…òÂò’Óâö÷væW"å&VDVÆVÖVçD&÷VæF–æt6Æ–VçE&V7B†VÆVÖVçB’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'7G–ÆR# ¢fÇVRÒö÷væW"ävWD÷$7&VFU7G–ÆTö&¦V7B†VÆVÖVçB“°¢&WGW&âG'VS°¢66R&7W'&VçE7G–ÆR# ¢fÇVRÒö÷væW"ä7&VFT6ö×WFVE7G–ÆTö&¦V7Df÷$VÆVÖVçB†VÆVÖVçB“°¢&WGW&âG'VS°¢FVfVÇC ¢òòfÆÂ&6²FòW6W"Ö76–væVB&÷W'F–W2†RærâvöövÆR6WG0¢òòõöwv'Âõö§6ÂöâVÆVÖVçG2f÷"–çFW&æÂ&öö¶¶VW–ær’à¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†VÆVÖVçBÂ&÷W'G’“°¢&WGW&âfÇVRåFrÒ§5fÇVUFråVæFVf–æVC°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDFöÔ–×ÆVÖVçFF–öå&÷W'G’„fVä§4FöÔ–×ÆVÖVçFF–öä†÷7B–×ÆVÖVçFF–öâÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&†4fVGW&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢–×ÆVÖVçFF–öâÀ¢&†4fVGW&R"À¢…òÂò’Óâ§5fÇVRäg&öÔ&ööÆVâ‡G'VR’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&7&VFTFö7VÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢–×ÆVÖVçFF–öâÀ¢&7&VFTFö7VÖVçB"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"VÆ–f–VDæÖRÒ&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢Fö7VÖVçBä7&VFU†ÖÄFö7VÖVçB†æÖW76UW&’ÂVÆ–f–VDæÖR’À¢†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&7&VFT…DÔÄFö7VÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢–×ÆVÖVçFF–öâÀ¢&7&VFT…DÔÄFö7VÖVçB"À¢…òÂ&w2’Óà¢°¢f"F—FÆRÒ&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ€¢Fö7VÖVçBä7&VFT‡FÖÄFö7VÖVçB‡F—FÆR’À¢†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDæÖVDæöFTÖ&÷W'G’„æÖVDæöFTÖæÖVDæöFTÖÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"†æÖVDæöFTÖäÆVæwF‚“°¢&WGW&âG'VS°¢66R&—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢&—FVÒ"À¢…òÂ&w2’Óà¢°¢f"–æFW‚Ò&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD–æFW‚¢ò'6VD–æFW€¢¢Ó°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖä—FVÒ†–æFW‚’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDæÖVD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢&vWDæÖVD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖävWDæÖVD—FVÒ†æÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDæÖVD—FVÔå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢&vWDæÖVD—FVÔå2"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"Æö6ÄæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖävWDæÖVD—FVÔå2†æÖW76UW&’ÂÆö6ÄæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'6WDæÖVD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢'6WDæÖVD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"GG"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’¢çVÆÃ°¢–b†GG"ÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖå6WDæÖVD—FVÒ†GG"’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WDæÖVD—FVÔå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢'6WDæÖVD—FVÔå2"À¢…òÂ&w2’Óà¢°¢f"GG"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’¢çVÆÃ°¢–b†GG"ÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖå6WDæÖVD—FVÔå2†GG"’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÖ÷fTæÖVD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢'&VÖ÷fTæÖVD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖå&VÖ÷fTæÖVD—FVÒ†æÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÖ÷fTæÖVD—FVÔå2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æÖVDæöFTÖÀ¢'&VÖ÷fTæÖVD—FVÔå2"À¢…òÂ&w2’Óà¢°¢f"æÖW76UW&’Ò&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFräçVÆÀ¢ò6öW&6UFô†÷7E7G&–ær†&w5³Ò¢¢çVÆÃ°¢f"Æö6ÄæÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖå&VÖ÷fTæÖVD—FVÔå2†æÖW76UW&’ÂÆö6ÄæÖR’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢FVfVÇC ¢–b†–çBåG'•'6R‡&÷W'G’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"–æFW‚’¢°¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†æÖVDæöFTÖä—FVÒ†–æFW‚’Â†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢Ð ¢f"æÖVBÒæÖVDæöFTÖävWDæÖVD—FVÒ‡&÷W'G’“°¢–b†æÖVBÒçVÆÂ¢°¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†æÖVBÂ†÷7Dö&¦V7D¶–æBä÷F†W"“°¢&WGW&âG'VS°¢Ð ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDFöÕFö¶VäÆ—7E&÷W'G’„DôÕFö¶VäÆ—7BFö¶VäÆ—7BÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‡Fö¶VäÆ—7BäÆVæwF‚“°¢&WGW&âG'VS°¢66R'fÇVR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡Fö¶VäÆ—7BåfÇVRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢&—FVÒ"À¢…òÂ&w2’Óà¢°¢f"–æFW‚Ò&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD–æFW‚¢ò'6VD–æFW€¢¢Ó°¢f"—FVÒÒFö¶VäÆ—7Bä—FVÒ†–æFW‚“°¢&WGW&â—FVÒÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†—FVÒ“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6öçF–ç2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢&6öçF–ç2"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7Bä6öçF–ç2‡Fö¶Vâ’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&FB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢&FB"À¢…òÂ&w2’Óà¢°¢Fö¶VäÆ—7BäFB†&w2å6VÆV7B„6öW&6UFô†÷7E7G&–ær’åFô'&’‚’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R'&VÖ÷fR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢'&VÖ÷fR"À¢…òÂ&w2’Óà¢°¢Fö¶VäÆ—7Bå&VÖ÷fR†&w2å6VÆV7B„6öW&6UFô†÷7E7G&–ær’åFô'&’‚’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò“°¢&WGW&âG'VS°¢66R'FövvÆR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢'FövvÆR"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&ööÃòf÷&6RÒçVÆÃ°¢–b†&w2ä6÷VçBâbb&w5³ÒåFrÒ§5fÇVUFråVæFVf–æVB¢°¢f÷&6RÒ6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢Ð ¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7BåFövvÆR‡Fö¶VâÂf÷&6R’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WÆ6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢'&WÆ6R"À¢…òÂ&w2’Óà¢°¢f"öÆEFö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"æWuFö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7Bå&WÆ6R†öÆEFö¶VâÂæWuFö¶Vâ’“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'7W÷'G2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Fö¶VäÆ—7BÀ¢'7W÷'G2"À¢…òÂ&w2’Óà¢°¢f"Fö¶VâÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ‡Fö¶VäÆ—7Bå7W÷'G2‡Fö¶Vâ’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢–b†–çBåG'•'6R‡&÷W'G’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"–æFW‚’¢°¢f"—FVÒÒFö¶VäÆ—7Bä—FVÒ†–æFW‚“°¢fÇVRÒ—FVÒÓÒçVÆÂò§5fÇVRåVæFVf–æVB¢§5fÇVRäg&öÕ7G&–ær†—FVÒ“°¢&WGW&âG'VS°¢Ð ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDGG%&÷W'G’„GG"GG"Â7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&æÖR# ¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†GG"äæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&Æö6ÄæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†GG"äÆö6ÄæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'&Vf—‚# ¢fÇVRÒGG"å&Vf—‚ÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†GG"å&Vf—‚“°¢&WGW&âG'VS°¢66R&æÖW76UU$’# ¢fÇVRÒGG"äæÖW76UW&’ÓÒçVÆÂò§5fÇVRäçVÆÂ¢§5fÇVRäg&öÕ7G&–ær†GG"äæÖW76UW&’“°¢&WGW&âG'VS°¢66R'fÇVR# ¢66R&æöFUfÇVR# ¢66R'FW‡D6öçFVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†GG"åfÇVRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&÷væW$VÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†GG"ä÷væW$VÆVÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R'7V6–f–VB# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†GG"å7V6–f–VB“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWD6†&7FW$FF&÷W'G’„6†&7FW$FF6†&7FW$FFÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&FF# ¢66R&æöFUfÇVR# ¢66R'FW‡D6öçFVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†6†&7FW$FFäFFóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"†6†&7FW$FFäÆVæwF‚“°¢&WGW&âG'VS°¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†6†&7FW$FFäæöFTæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&æöFUG—R# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‚†–çB–6†&7FW$FFäæöFUG—R“°¢&WGW&âG'VS°¢66R&–B# ¢66R&Æö6ÄæÖR# ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âG'VS°¢66R&÷væW$Fö7VÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†6†&7FW$FFä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†&7FW$FFå&VçDæöFR“°¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢66R&Æ7D6†–ÆB# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&6†–ÆDæöFW2# ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†6†&7FW$FFä6†–ÆDæöFW2åFô'&’‚’“°¢&WGW&âG'VS°¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†&7FW$FFäæW‡E6–&Æ–ær“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†&7FW$FFå&Wf–÷W56–&Æ–ær“°¢&WGW&âG'VS°¢66R&VæDFF# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢&VæDFF"À¢…òÂ&w2’Óà¢°¢6†&7FW$FFäVæDFF†&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–ç6W'DFF# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢&–ç6W'DFF"À¢…òÂ&w2’Óà¢°¢f"öfg6WBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD–æFW‚’ò'6VD–æFW‚¢°¢f"FFÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢6†&7FW$FFä–ç6W'DFF†öfg6WBÂFF“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&FVÆWFTFF# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢&FVÆWFTFF"À¢…òÂ&w2’Óà¢°¢f"öfg6WBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VDöfg6WB’ò'6VDöfg6WB¢°¢f"6÷VçBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD6÷VçB’ò'6VD6÷VçB¢°¢6†&7FW$FFäFVÆWFTFF†öfg6WBÂ6÷VçB“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&WÆ6TFF# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢'&WÆ6TFF"À¢…òÂ&w2’Óà¢°¢f"öfg6WBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VDöfg6WB’ò'6VDöfg6WB¢°¢f"6÷VçBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD6÷VçB’ò'6VD6÷VçB¢°¢f"FFÒ&w2ä6÷VçBâ"ò6öW&6UFô†÷7E7G&–ær†&w5³%Ò’¢7G&–æräV×G“°¢6†&7FW$FFå&WÆ6TFF†öfg6WBÂ6÷VçBÂFF“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢2“°¢&WGW&âG'VS°¢66R'7V'7G&–ætFF# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢'7V'7G&–ætFF"À¢…òÂ&w2’Óà¢°¢f"öfg6WBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VDöfg6WB’ò'6VDöfg6WB¢°¢f"6÷VçBÒ&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD6÷VçB’ò'6VD6÷VçB¢°¢&WGW&â§5fÇVRäg&öÕ7G&–ær†6†&7FW$FFå7V'7G&–ætFF†öfg6WBÂ6÷VçB’“°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢'&VÖ÷fR"À¢…òÂò’Óà¢°¢6†&7FW$FFå&VÖ÷fR‚“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆöæTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢6†&7FW$FFÀ¢&6ÆöæTæöFR"À¢…òÂ&w2’Óà¢°¢f"FVWÒ&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6†&7FW$FFä6ÆöæTæöFR†FVW’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ö×&TFö7VÖVçE÷6—F–öâ# ¢fÇVRÒö÷væW"ä7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR†6†&7FW$FF“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDFö7VÖVçDg&vÖVçE&÷W'G’„Fö7VÖVçDg&vÖVçBg&vÖVçBÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&æöFTæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†g&vÖVçBäæöFTæÖRóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&æöFUG—R# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‚†–çB–g&vÖVçBäæöFUG—R“°¢&WGW&âG'VS°¢66R&æöFUfÇVR# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R'FW‡D6öçFVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†g&vÖVçBåFW‡D6öçFVçBóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&—46öææV7FVB# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†g&vÖVçBä—46öææV7FVB“°¢&WGW&âG'VS°¢66R&†÷7B"v†Vâg&vÖVçB—26†F÷u&ö÷B6†F÷u&ö÷C ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ‡6†F÷u&ö÷Bä†÷7BÂ†÷7Dö&¦V7D¶–æBäFöÔVÆVÖVçB“°¢&WGW&âG'VS°¢66R&ÖöFR"v†Vâg&vÖVçB—26†F÷u&ö÷B6†F÷u&ö÷C ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡6†F÷u&ö÷BäÖöFRÓÒ6†F÷u&ö÷DÖöFRä6Æ÷6VBò&6Æ÷6VB"¢&÷Vâ"“°¢&WGW&âG'VS°¢66R&FVÆVvFW4fö7W2"v†Vâg&vÖVçB—26†F÷u&ö÷B6†F÷u&ö÷C ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ‡6†F÷u&ö÷BäFVÆVvFW4fö7W2“°¢&WGW&âG'VS°¢66R'6Æ÷D76–væÖVçB"v†Vâg&vÖVçB—26†F÷u&ö÷B6†F÷u&ö÷C ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡6†F÷u&ö÷Bå6Æ÷D76–væÖVçBÓÒ6Æ÷D76–væÖVçDÖöFRäÖçVÂò&ÖçVÂ"¢&æÖVB"“°¢&WGW&âG'VS°¢66R&÷væW$Fö7VÖVçB# ¢fÇVRÒö÷væW"åFô†÷7D÷$çVÆÂ†g&vÖVçBä÷væW$Fö7VÖVçBÂ†÷7Dö&¦V7D¶–æBäFöÔFö7VÖVçB“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBå&VçDæöFR“°¢&WGW&âG'VS°¢66R'&VçDVÆVÖVçB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBå&VçDæöFR2VÆVÖVçB“°¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäf—'7D6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7D6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäÆ7D6†–ÆB“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBå&Wf–÷W56–&Æ–ær“°¢&WGW&âG'VS°¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäæW‡E6–&Æ–ær“°¢&WGW&âG'VS°¢66R&6†–ÆDæöFW2# ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†g&vÖVçBä6†–ÆDæöFW2åFô'&’‚’“°¢&WGW&âG'VS°¢66R&f—'7DVÆVÖVçD6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäf—'7DVÆVÖVçD6†–ÆB“°¢&WGW&âG'VS°¢66R&Æ7DVÆVÖVçD6†–ÆB# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäÆ7DVÆVÖVçD6†–ÆB“°¢&WGW&âG'VS°¢66R&6†–ÆDVÆVÖVçD6÷VçB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"†g&vÖVçBä6†–ÆDVÆVÖVçD6÷VçB“°¢&WGW&âG'VS°¢66R&6†–ÆG&Vâ# ¢°¢f"6†–ÆG&VâÒæWrÆ—7CÄæöFSâ‚“°¢f÷"‡f"’Ò²’Âg&vÖVçBä6†–ÆDæöFW2äÆVæwFƒ²’²²¢°¢–b†g&vÖVçBä6†–ÆDæöFW5¶•Ò—2VÆVÖVçB¢°¢6†–ÆG&VâäFB†g&vÖVçBä6†–ÆDæöFW5¶•Ò“°¢Ð¢Ð ¢fÇVRÒö÷væW"ä7&VFTæöFT'&”Æ–¶R†6†–ÆG&Vâ“°¢Ð¢&WGW&âG'VS°¢66R&FDWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&FDWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"äFD'&÷w6W$WfVçDÆ—7FVæW"€¢ö÷væW"åöVÆVÖVçDWfVçDÆ—7FVæW'2ävWD÷$7&VFUfÇVR†g&vÖVçB’À¢&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fTWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'&VÖ÷fTWfVçDÆ—7FVæW""À¢…òÂ&w2’Óà¢°¢ö÷væW"å&VÖ÷fT'&÷w6W$WfVçDÆ—7FVæW"€¢ö÷væW"åöVÆVÖVçDWfVçDÆ—7FVæW'2ävWD÷$7&VFUfÇVR†g&vÖVçB’À¢&w2“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&F—7F6„WfVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&F—7F6„WfVçB"À¢…òÂ&w2’Óà¢°¢f"WfVçEfÇVRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRåVæFVf–æVC°¢f"G—RÒö÷væW"å&VDWfVçEG—R†WfVçEfÇVR“°¢f"F&vWBÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçB“°¢ö÷væW"å&W&TF—7F6†VDWfVçB†WfVçEfÇVRÂF&vWB“°¢ö÷væW"äF—7F6„'&÷w6W$WfVçB€¢ö÷væW"åöVÆVÖVçDWfVçDÆ—7FVæW'2ävWD÷$7&VFUfÇVR†g&vÖVçB’À¢G—RÀ¢F&vWBÀ¢WfVçEfÇVR“°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ€¢ö÷væW"å&VD§4&ööÅ&÷W'G’†WfVçEfÇVRÂ&FVfVÇE&WfVçFVB"’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&VæD6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&VæD6†–ÆB"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBäVæD6†–ÆB†6†–ÆB’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–ç6W'D&Vf÷&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&–ç6W'D&Vf÷&R"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð ¢f"&VfW&Væ6TæöFRÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBä–ç6W'D&Vf÷&R†6†–ÆBÂ&VfW&Væ6TæöFR’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&6ÆöæTæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&6ÆöæTæöFR"À¢…òÂ&w2’Óà¢°¢f"FVWÒ&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBä6ÆöæTæöFR†FVW’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÖ÷fT6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'&VÖ÷fT6†–ÆB"À¢…òÂ&w2’Óà¢°¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBå&VÖ÷fT6†–ÆB†6†–ÆB’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WÆ6T6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'&WÆ6T6†–ÆB"À¢…òÂ&w2’Óà¢°¢–b†&w2ä6÷VçBâbbö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&w5³Ò’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢f"6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â%&WÆ6VÖVçB6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢f"öÆD6†–ÆBÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢–b†öÆD6†–ÆBÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""Â$6†–ÆB×W7B&RDôÒæöFRâ"“°¢Ð ¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBå&WÆ6T6†–ÆB†6†–ÆBÂöÆD6†–ÆB’“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&†46†–ÆDæöFW2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&†46†–ÆDæöFW2"À¢…òÂò’Óâ§5fÇVRäg&öÔ&ööÆVâ†g&vÖVçBä†46†–ÆDæöFW2’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6öçF–ç2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&6öçF–ç2"À¢…òÂ&w2’Óà¢°¢f"÷F†W"Ò&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5³Ò’¢çVÆÃ°¢&WGW&â§5fÇVRäg&öÔ&ööÆVâ†÷F†W"ÒçVÆÂbbg&vÖVçBä6öçF–ç2†÷F†W"’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ö×&TFö7VÖVçE÷6—F–öâ# ¢fÇVRÒö÷væW"ä7&VFT6ö×&TFö7VÖVçE÷6—F–öä6ÆÆ&ÆR†g&vÖVçB“°¢&WGW&âG'VS°¢66R&VæB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&VæB"À¢…òÂ&w2’Óà¢°¢f÷&V6‚‡f"&r–â&w2¢°¢æöFR6†–ÆBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&r“°¢–b†6†–ÆBÓÒçVÆÂ¢°¢6†–ÆBÒg&vÖVçBä÷væW$Fö7VÖVçCòä7&VFUFW‡DæöFR„6öW&6UFô†÷7E7G&–ær†&r’“°¢Ð ¢–b†6†–ÆBÒçVÆÂ¢°¢g&vÖVçBäVæD6†–ÆB†6†–ÆB“°¢Ð¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WVæB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'&WVæB"À¢…òÂ&w2’Óà¢°¢f"&VfW&Væ6TæöFRÒg&vÖVçBäf—'7D6†–ÆC°¢f÷&V6‚‡f"&r–â&w2¢°¢–b…ö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄGG#â†&r’ÒçVÆÂ¢°¢ö÷væW"åF‡&÷t†–W&&6‡•&WVW7DW'&÷"‚$GG&–'WFW26ææ÷B&R–ç6W'FVB26†–ÆBæöFW2â"“°¢Ð ¢æöFR6†–ÆBÒö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&r“°¢–b†6†–ÆBÓÒçVÆÂ¢°¢6†–ÆBÒg&vÖVçBä÷væW$Fö7VÖVçCòä7&VFUFW‡DæöFR„6öW&6UFô†÷7E7G&–ær†&r’“°¢Ð ¢–b†6†–ÆBÒçVÆÂ¢°¢g&vÖVçBä–ç6W'D&Vf÷&R†6†–ÆBÂ&VfW&Væ6TæöFR“°¢Ð¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWDVÆVÖVçD'”–B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢&vWDVÆVÖVçD'”–B"À¢…òÂ&w2’Óà¢°¢f"–BÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBävWDVÆVÖVçD'”–B†–B’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'VW'•6VÆV7F÷""À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢G'¢°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†g&vÖVçBåVW'•6VÆV7F÷"‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&â§5fÇVRäçVÆÃ°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'VW'•6VÆV7F÷$ÆÂ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢g&vÖVçBÀ¢'VW'•6VÆV7F÷$ÆÂ"À¢…òÂ&w2’Óà¢°¢f"6VÆV7F÷"Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡6VÆV7F÷"’¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð ¢G'¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R†g&vÖVçBåVW'•6VÆV7F÷$ÆÂ‡6VÆV7F÷"’“°¢Ð¢6F6‚„FöÔW†6WF–öâ¢°¢&WGW&âö÷væW"ä7&VFTæöFT'&”Æ–¶R„'&’äV×G“ÄæöFSâ‚’“°¢Ð¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWE&ævU&÷W'G’„FöÕ&ævR&ævRÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R%5D%EõDõõ5D%B# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"ƒ“°¢&WGW&âG'VS°¢66R%5D%EõDõôTäB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"ƒ“°¢&WGW&âG'VS°¢66R$TäEõDõôTäB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"ƒ"“°¢&WGW&âG'VS°¢66R$TäEõDõõ5D%B# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"ƒ2“°¢&WGW&âG'VS°¢66R'7F'D6öçF–æW"# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&ævRå7F'D6öçF–æW"“°¢&WGW&âG'VS°¢66R'7F'Döfg6WB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‡&ævRå7F'Döfg6WB“°¢&WGW&âG'VS°¢66R&VæD6öçF–æW"# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&ævRäVæD6öçF–æW"“°¢&WGW&âG'VS°¢66R&VæDöfg6WB# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‡&ævRäVæDöfg6WB“°¢&WGW&âG'VS°¢66R&6öÆÆ6VB# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ‡&ævRä6öÆÆ6VB“°¢&WGW&âG'VS°¢66R&6öÖÖöäæ6W7F÷$6öçF–æW"# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&ævRä6öÖÖöäæ6W7F÷$6öçF–æW"“°¢&WGW&âG'VS°¢66R'6WE7F'B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WE7F'B"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WE7F'B€¢&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WE7F'B"’À¢6öW&6U&ævTöfg6WD&wVÖVçB†&w2Â’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'6WDVæB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WDVæB"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WDVæB€¢&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WDVæB"’À¢6öW&6U&ævTöfg6WD&wVÖVçB†&w2Â’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'6WE7F'D&Vf÷&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WE7F'D&Vf÷&R"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WE7F'D&Vf÷&R…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WE7F'D&Vf÷&R"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WE7F'DgFW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WE7F'DgFW""À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WE7F'DgFW"…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WE7F'DgFW""’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WDVæD&Vf÷&R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WDVæD&Vf÷&R"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WDVæD&Vf÷&R…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WDVæD&Vf÷&R"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WDVæDgFW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6WDVæDgFW""À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6WDVæDgFW"…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6WDVæDgFW""’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6öÆÆ6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&6öÆÆ6R"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRä6öÆÆ6R†&w2ä6÷VçBâbb6öW&6UFô†÷7D&ööÆVâ†&w5³Ò’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6VÆV7DæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6VÆV7DæöFR"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6VÆV7DæöFR…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6VÆV7DæöFR"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6VÆV7DæöFT6öçFVçG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'6VÆV7DæöFT6öçFVçG2"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå6VÆV7DæöFT6öçFVçG2…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'6VÆV7DæöFT6öçFVçG2"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ö×&T&÷VæF'•ö–çG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&6ö×&T&÷VæF'•ö–çG2"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢f"†÷rÒ‡W6†÷'B”6öW&6U&ævTöfg6WD&wVÖVçB†&w2Â“°¢f"÷F†W%&ævRÒ&w2ä6÷VçBâòö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄFöÕ&ævSâ†&w5³Ò’¢çVÆÃ°¢–b†÷F†W%&ævRÓÒçVÆÂ¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢$f–ÆVBFòW†V7WFRv6ö×&T&÷VæF'•ö–çG2röâu&ævRs¢&ÖWFW""—2æ÷BöbG—Ru&ævRrâ"“°¢Ð ¢&WGW&â§5fÇVRäg&öÔ–çC3"‡&ævRä6ö×&T&÷VæF'•ö–çG2††÷rÂ÷F†W%&ævR’“°¢Ò’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&FVÆWFT6öçFVçG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&FVÆWFT6öçFVçG2"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRäFVÆWFT6öçFVçG2‚“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&W‡G&7D6öçFVçG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&W‡G&7D6öçFVçG2"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&ævRäW‡G&7D6öçFVçG2‚’’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆöæT6öçFVçG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&6ÆöæT6öçFVçG2"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡&ævRä6ÆöæT6öçFVçG2‚’’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&–ç6W'DæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&–ç6W'DæöFR"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRä–ç6W'DæöFR…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ&–ç6W'DæöFR"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'7W'&÷VæD6öçFVçG2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'7W'&÷VæD6öçFVçG2"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRå7W'&÷VæD6öçFVçG2…&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ'7W'&÷VæD6öçFVçG2"’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆöæU&ævR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&6ÆöæU&ævR"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óâö÷væW"åFô†÷7D÷$çVÆÂ‡&ævRä6ÆöæU&ævR‚’Â†÷7Dö&¦V7D¶–æBä÷F†W"’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&FWF6‚# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&FWF6‚"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óà¢°¢&ævRäFWF6‚‚“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&—5ö–çD–å&ævR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&—5ö–çD–å&ævR"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óâ§5fÇVRäg&öÔ&ööÆVâ‡&ævRä—5ö–çD–å&ævR€¢&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ&—5ö–çD–å&ævR"’À¢6öW&6U&ævTöfg6WD&wVÖVçB†&w2Â’’’’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&6ö×&Uö–çB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&6ö×&Uö–çB"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óâ§5fÇVRäg&öÔ–çC3"‡&ævRä6ö×&Uö–çB€¢&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ&6ö×&Uö–çB"’À¢6öW&6U&ævTöfg6WD&wVÖVçB†&w2Â’’’’À¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&–çFW'6V7G4æöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&–çFW'6V7G4æöFR"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óâ§5fÇVRäg&öÔ&ööÆVâ‡&ævRä–çFW'6V7G4æöFR€¢&WV—&U&ævTæöFT&wVÖVçB†&w2ÂÂ&–çFW'6V7G4æöFR"’’’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&7&VFT6öçFW‡GVÄg&vÖVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&7&VFT6öçFW‡GVÄg&vÖVçB"À¢…òÂ&w2’Óâ–çfö¶U&ævR‚‚’Óà¢°¢f"‡FÖÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ…ö÷væW"ä7&VFT6öçFW‡GVÄg&vÖVçB‡&ævRÂ‡FÖÂ’“°¢Ò’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWD&÷VæF–æt6Æ–VçE&V7B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&vWD&÷VæF–æt6Æ–VçE&V7B"À¢…òÂò’Óâö÷væW"ä7&VFTFöÕ&V7BƒÂÂÂ’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWD6Æ–VçE&V7G2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢&vWD6Æ–VçE&V7G2"À¢…òÂò’Óâö÷væW"ä7&VFTV×G”FöÕ&V7DÆ—7B‚’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'Fõ7G&–ær# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢&ævRÀ¢'Fõ7G&–ær"À¢…òÂò’Óâ–çfö¶U&ævR‚‚’Óâ§5fÇVRäg&öÕ7G&–ær‡&ævRåFõ7G&–ær‚’’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR§5fÇVR–çfö¶U&ævR„gVæ3Ä§5fÇVSâ7F–öâ¢°¢G'¢°¢&WGW&â7F–öâ‚“°¢Ð¢6F6‚„FöÔW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ†W‚äæÖRÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢6F6‚„&wVÖVçDW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚%G—TW'&÷""ÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢6F6‚„–çfÆ–D÷W&F–öäW†6WF–öâW‚¢°¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ‚$–çfÆ–E7FFTW'&÷""ÂW‚äÖW76vR“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢Ð¢Ð ¢&—fFRæöFR&WV—&U&ævTæöFT&wVÖVçB„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2Â–çB–æFW‚Â7G&–ærÖWF†öDæÖR¢°¢f"æöFRÒ&w2ä6÷VçBâ–æFW‚òö÷væW"å&W6öÇfT†÷7Dö&¦V7D÷$çVÆÃÄæöFSâ†&w5¶–æFW…Ò’¢çVÆÃ°¢–b†æöFRÒçVÆÂ¢°¢&WGW&âæöFS°¢Ð ¢ö÷væW"åF‡&÷tFöÔW†6WF–öâ€¢%G—TW'&÷""À¢B$f–ÆVBFòW†V7WFRw¶ÖWF†öDæÖWÒröâu&ævRs¢&ÖWFW"¶–æFW‚²Ò—2æ÷BöbG—RtæöFRrâ"“°¢&WGW&âçVÆÃ°¢Ð ¢&—fFR7FF–2–çB6öW&6U&ævTöfg6WD&wVÖVçB„•&VDöæÇ”Æ—7CÄ§5fÇVSâ&w2Â–çB–æFW‚¢°¢&WGW&â&w2ä6÷VçBâ–æFW‚bbG'”6öW&6T–æFW‚†&w5¶–æFW…ÒÂ÷WBf"öfg6WB¢òöfg6W@¢¢°¢Ð ¢&—fFR&ööÂG'”vWD‡FÖÄ6öÆÆV7F–öå&÷W'G’„fVä§4‡FÖÄ6öÆÆV7F–öä†÷7B‡FÖÄ6öÆÆV7F–öâÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢f"6öÆÆV7F–öâÒ‡FÖÄ6öÆÆV7F–öâä6öÆÆV7F–öã°¢7v—F6‚‡&÷W'G’¢°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"†6öÆÆV7F–öâäÆVæwF‚“°¢&WGW&âG'VS°¢66R&—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢‡FÖÄ6öÆÆV7F–öâÀ¢&—FVÒ"À¢…òÂ&w2’Óà¢°¢f"–æFW‚Ò&w2ä6÷VçBâbbG'”6öW&6T–æFW‚†&w5³ÒÂ÷WBf"'6VD–æFW‚¢ò'6VD–æFW€¢¢Ó°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6öÆÆV7F–öå¶–æFW…Ò“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&æÖVD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢‡FÖÄ6öÆÆV7F–öâÀ¢&æÖVD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"æÖRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢&WGW&âö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6öÆÆV7F–öâäæÖVD—FVÒ†æÖR’“°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢–b†–çBåG'•'6R‡&÷W'G’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"–æFW‚’¢°¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ†6öÆÆV7F–öå¶–æFW…Ò“°¢&WGW&âG'VS°¢Ð ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWEG&VUvÆ¶W%&÷W'G’„fVä§5G&VUvÆ¶W$†÷7BG&VUvÆ¶W$†÷7BÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢f"G&VUvÆ¶W"ÒG&VUvÆ¶W$†÷7BåG&VUvÆ¶W#°¢7v—F6‚‡&÷W'G’¢°¢66R'&ö÷B# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"å&ö÷B“°¢&WGW&âG'VS°¢66R'v†EFõ6†÷r# ¢fÇVRÒ§5fÇVRäg&öÔçVÖ&W"‡G&VUvÆ¶W"åv†EFõ6†÷r“°¢&WGW&âG'VS°¢66R&f–ÇFW"# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&7W'&VçDæöFR# ¢fÇVRÒö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"ä7W'&VçDæöFR“°¢&WGW&âG'VS°¢66R'&VçDæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢'&VçDæöFR"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"å&VçDæöFR‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&f—'7D6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢&f—'7D6†–ÆB"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"äf—'7D6†–ÆB‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&Æ7D6†–ÆB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢&Æ7D6†–ÆB"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"äÆ7D6†–ÆB‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&Wf–÷W56–&Æ–ær# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢'&Wf–÷W56–&Æ–ær"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"å&Wf–÷W56–&Æ–ær‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&æW‡E6–&Æ–ær# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢&æW‡E6–&Æ–ær"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"äæW‡E6–&Æ–ær‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&Wf–÷W4æöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢'&Wf–÷W4æöFR"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"å&Wf–÷W4æöFR‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&æW‡DæöFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢G&VUvÆ¶W$†÷7BÀ¢&æW‡DæöFR"À¢…òÂò’Óâö÷væW"åFô†÷7DæöFT÷$çVÆÂ‡G&VUvÆ¶W"äæW‡DæöFR‚’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDæ–ÖF–öå&÷W'G’„fVä§4æ–ÖF–öä†÷7Bæ–ÖF–öâÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&–B# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æ–ÖF–öâä–B“°¢&WGW&âG'VS°¢66R&VffV7B# ¢fÇVRÒæ–ÖF–öâä¶W–g&ÖW2åFrÓÒ§5fÇVUFråVæFVf–æVBò§5fÇVRäçVÆÂ¢æ–ÖF–öâä¶W–g&ÖW3°¢&WGW&âG'VS°¢66R'F–ÖVÆ–æR# ¢fÇVRÒ§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R'7F'EF–ÖR# ¢fÇVRÒæ–ÖF–öâå7F'EF–ÖRä†5fÇVRò§5fÇVRäg&öÔçVÖ&W"†æ–ÖF–öâå7F'EF–ÖRåfÇVR’¢§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R&7W'&VçEF–ÖR# ¢fÇVRÒæ–ÖF–öâä7W'&VçEF–ÖRä†5fÇVRò§5fÇVRäg&öÔçVÖ&W"†æ–ÖF–öâä7W'&VçEF–ÖRåfÇVR’¢§5fÇVRäçVÆÃ°¢&WGW&âG'VS°¢66R'Æ–&6µ&FR# ¢fÇVRÒ§5fÇVRäg&öÔçVÖ&W"†æ–ÖF–öâåÆ–&6µ&FR“°¢&WGW&âG'VS°¢66R'Æ•7FFR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æ–ÖF–öâåÆ•7FFR“°¢&WGW&âG'VS°¢66R'VæF–ær# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†æ–ÖF–öâåVæF–ær“°¢&WGW&âG'VS°¢66R'&WÆ6U7FFR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚&7F—fR"“°¢&WGW&âG'VS°¢66R'&VG’# ¢66R&f–æ—6†VB# ¢fÇVRÒö÷væW"ä7&VFU&W6öÇfVE&öÖ—6R…ö÷væW"åFô†÷7D÷$çVÆÂ†æ–ÖF–öâÂ†÷7Dö&¦V7D¶–æBä÷F†W"’“°¢&WGW&âG'VS°¢66R&6æ6VÂ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢&6æ6VÂ"À¢…òÂò’Óà¢°¢æ–ÖF–öâåÆ•7FFRÒ&–FÆR#°¢æ–ÖF–öâåVæF–ærÒfÇ6S°¢æ–ÖF–öâä7W'&VçEF–ÖRÒ°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&f–æ—6‚# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢&f–æ—6‚"À¢…òÂò’Óà¢°¢æ–ÖF–öâåÆ•7FFRÒ&f–æ—6†VB#°¢æ–ÖF–öâåVæF–ærÒfÇ6S°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'Æ’# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢'Æ’"À¢…òÂò’Óà¢°¢æ–ÖF–öâåÆ•7FFRÒ''Vææ–ær#°¢æ–ÖF–öâåVæF–ærÒfÇ6S°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'W6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢'W6R"À¢…òÂò’Óà¢°¢æ–ÖF–öâåÆ•7FFRÒ'W6VB#°¢æ–ÖF–öâåVæF–ærÒfÇ6S°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WfW'6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢'&WfW'6R"À¢…òÂò’Óà¢°¢æ–ÖF–öâåÆ–&6µ&FRÒæ–ÖF–öâåÆ–&6µ&FRÓÒòÓ¢Öæ–ÖF–öâåÆ–&6µ&FS°¢æ–ÖF–öâåÆ•7FFRÒ''Vææ–ær#°¢æ–ÖF–öâåVæF–ærÒfÇ6S°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'WFFUÆ–&6µ&FR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢'WFFUÆ–&6µ&FR"À¢…òÂ&w2’Óà¢°¢æ–ÖF–öâåÆ–&6µ&FRÒ&w2ä6÷VçBâ ¢ò6öW&6UFôf–æ—FTçVÖ&W"†&w5³ÒÂæ–ÖF–öâåÆ–&6µ&FR¢¢æ–ÖF–öâåÆ–&6µ&FS°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'W'6—7B# ¢66R&6öÖÖ—E7G–ÆW2# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢&÷W'G’À¢…òÂò’Óâ§5fÇVRåVæFVf–æVBÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&FDWfVçDÆ—7FVæW"# ¢66R'&VÖ÷fTWfVçDÆ—7FVæW"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢&÷W'G’À¢…òÂò’Óâ§5fÇVRåVæFVf–æVBÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&F—7F6„WfVçB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢æ–ÖF–öâÀ¢&F—7F6„WfVçB"À¢…òÂò’Óâ§5fÇVRäg&öÔ&ööÆVâ‡G'VR’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†æ–ÖF–öâÂ&÷W'G’“°¢&WGW&âfÇVRåFrÒ§5fÇVUFråVæFVf–æVC°¢Ð¢Ð ¢&—fFR&ööÂG'”vWDFöÕ7G&–ætÖ&÷W'G’„fVä§4FöÕ7G&–ætÖ†÷7BFöÕ7G&–ætÖÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢f"GG&–'WFTæÖRÒ&÷W'G”æÖUFôFF6WDGG&–'WFR‡&÷W'G’“°¢f"GG&–'WFUfÇVRÒFöÕ7G&–ætÖäVÆVÖVçBävWDGG&–'WFR†GG&–'WFTæÖR“°¢fÇVRÒGG&–'WFUfÇVRÓÒçVÆÂò§5fÇVRåVæFVf–æVB¢§5fÇVRäg&öÕ7G&–ær†GG&–'WFUfÇVR“°¢&WGW&âG'VS°¢Ð ¢&—fFR7FF–27G&–ær&÷W'G”æÖUFôFF6WDGG&–'WFR‡7G&–ær&÷W'G”æÖR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡&÷W'G”æÖR’¢°¢&WGW&â&FFÒ#°¢Ð ¢f"'V–ÆFW"ÒæWr7G&–æt'V–ÆFW"‚&FFÒ"“°¢f÷"‡f"’Ò²’Â&÷W'G”æÖRäÆVæwFƒ²’²²¢°¢f"6‚Ò&÷W'G”æÖU¶•Ó°¢–b†6†"ä—5WW"†6‚’¢°¢'V–ÆFW"äVæB‚rÒr“°¢'V–ÆFW"äVæB†6†"åFôÆ÷vW$–çf&–çB†6‚’“°¢Ð¢VÇ6P¢°¢'V–ÆFW"äVæB†6‚“°¢Ð¢Ð ¢&WGW&â'V–ÆFW"åFõ7G&–ær‚“°¢Ð ¢&—fFR7FF–2&ööÂG'”6öW&6T–æFW‚„§5fÇVRfÇVRÂ÷WB–çB–æFW‚¢°¢7v—F6‚‡fÇVRåFr¢°¢66R§5fÇVUFrä–çC3# ¢–æFW‚ÒfÇVRä4–çC3"‚“°¢&WGW&âG'VS°¢66R§5fÇVUFräçVÖ&W# ¢f"çVÖ&W"ÒfÇVRä4çVÖ&W"‚“°¢–b†F÷V&ÆRä—4f–æ—FR†çVÖ&W"’bbÖF‚åG'Væ6FR†çVÖ&W"’ÓÒçVÖ&W"bbçVÖ&W"ãÒ–çBäÖ–åfÇVRbbçVÖ&W"ÃÒ–çBäÖ…fÇVR¢°¢–æFW‚Ò†–çB–çVÖ&W#°¢&WGW&âG'VS°¢Ð¢'&V³°¢66R§5fÇVUFrå7G&–æs ¢–b†–çBåG'•'6R‡fÇVRä57G&–ær‚’ÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WB–æFW‚’¢°¢&WGW&âG'VS°¢Ð¢'&V³°¢Ð ¢–æFW‚ÒÓ°¢&WGW&âfÇ6S°¢Ð ¢&—fFR&ööÂG'”vWDæf–vF÷%&÷W'G’„'&÷w6W%7W&f6U&öf–ÆRæf–vF÷"Â7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R'W6W$vVçB# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æf–vF÷"åW6W$vVçBóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'ÆFf÷&Ò# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æf–vF÷"åÆFf÷&ÕFö¶Vâóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'fVæF÷"# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æf–vF÷"åfVæF÷"óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&6öö¶–TVæ&ÆVB# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ†æf–vF÷"ä6öö¶–TVæ&ÆVB“°¢&WGW&âG'VS°¢66R&†&Gv&T6öæ7W'&Væ7’# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"„ÖF‚äÖ‚ƒÂVçf—&öæÖVçBå&ö6W76÷$6÷VçB’“°¢&WGW&âG'VS°¢66R&Ö…F÷V6…ö–çG2# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"ƒ“°¢&WGW&âG'VS°¢66R&FWf–6TÖVÖ÷'’# ¢fÇVRÒ§5fÇVRäg&öÔçVÖ&W"ƒB“²òò6öÖÖöâFVfVÇ@¢&WGW&âG'VS°¢66R&ÆæwVvR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†æf–vF÷"äÆæwVvRóò7—7FVÒävÆö&Æ—¦F–öâä7VÇGW&T–æfòä7W'&VçD7VÇGW&RåGvôÆWGFW$•4ôÆæwVvTæÖR“°¢&WGW&âG'VS°¢66R&ÆæwVvW2# ¢òò×W7B&WGW&âg&÷¦Vâ'&’W"7V2â&÷BFWFV7F–öâ6†V6·2'&’æ—4'&’†æf–vF÷"æÆæwVvW2’à¢°¢f"ÆærÒæf–vF÷"äÆæwVvRóò&VâÕU2#°¢f"6†÷'DÆærÒÆærä6öçF–ç2‚"Ò"’òÆærå7V'7G&–ærƒÂÆærä–æFW„öb‚rÒr’’¢Ææs°¢fÇVRÒö÷væW"åö–çFW'&WFW"äÆÆö6FT'&’†æWuµÐ¢°¢§5fÇVRäg&öÕ7G&–ær†Æær’À¢§5fÇVRäg&öÕ7G&–ær‡6†÷'DÆær¢Ò“°¢Ð¢&WGW&âG'VS°¢66R&öäÆ–æR# ¢fÇVRÒ§5fÇVRäg&öÔ&ööÆVâ‡G'VR“°¢&WGW&âG'VS°¢66R&æÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚$æWG66R"“°¢&WGW&âG'VS°¢66R&fW'6–öâ# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚#Rã"“°¢&WGW&âG'VS°¢66R'&öGV7B# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‚$vV6¶ò"“°¢&WGW&âG'VS°¢66R'6W'f–6Uv÷&¶W"# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†æf–vF÷"Â'6W'f–6Uv÷&¶W""“°¢–b‡fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢fÇVRÒö÷væW"ä7&VFTFVfVÇE6W'f–6Uv÷&¶W%7GV"‚“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†æf–vF÷"Â'6W'f–6Uv÷&¶W""ÂfÇVR“°¢Ð¢&WGW&âG'VS°¢66R'7F÷&vR# ¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB†æf–vF÷"Â'7F÷&vR"“°¢–b‡fÇVRåFrÓÒ§5fÇVUFråVæFVf–æVB¢°¢fÇVRÒö÷væW"ä7&VFTFVfVÇE7F÷&vU7GV"‚“°¢ö÷væW"å6WE7F÷&VD†÷7E&÷W'G’†æf–vF÷"Â'7F÷&vR"ÂfÇVR“°¢Ð¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWE7F÷&vU&÷W'G’„fVå7F÷&vT&V†÷7B7F÷&vT&VÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢7v—F6‚‡&÷W'G’¢°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"‡7F÷&vT&VäÆVæwF‚“°¢&WGW&âG'VS°¢66R&¶W’# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢7F÷&vT&VÂ&¶W’"À¢…òÂ&w2’Óà¢°¢f"–æFW‚Ò&w2ä6÷VçBâò†–çB”6öW&6UFôf–æ—FTçVÖ&W"†&w5³ÒÂ’¢°¢f"&W7VÇBÒ7F÷&vT&Vä¶W’†–æFW‚“°¢&WGW&â&W7VÇBÒçVÆÂò§5fÇVRäg&öÕ7G&–ær‡&W7VÇB’¢§5fÇVRäçVÆÃ°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&vWD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢7F÷&vT&VÂ&vWD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"¶W’Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"&W7VÇBÒ7F÷&vT&VävWD—FVÒ†¶W’“°¢&WGW&â&W7VÇBÒçVÆÂò§5fÇVRäg&öÕ7G&–ær‡&W7VÇB’¢§5fÇVRäçVÆÃ°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'6WD—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢7F÷&vT&VÂ'6WD—FVÒ"À¢…òÂ&w2’Óà¢°¢f"¶W’Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"fÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢7F÷&vT&Vå6WD—FVÒ†¶W’ÂfÂ“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&VÖ÷fT—FVÒ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢7F÷&vT&VÂ'&VÖ÷fT—FVÒ"À¢…òÂ&w2’Óà¢°¢f"¶W’Ò&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢7F÷&vT&Vå&VÖ÷fT—FVÒ†¶W’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&6ÆV"# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢7F÷&vT&VÂ&6ÆV""À¢…òÂò’Óà¢°¢7F÷&vT&Vä6ÆV"‚“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢òòW"7V2Â7F÷&vU¶¶W•Ò—2WV—fÆVçBFò7F÷&vRævWD—FVÒ†¶W’’à¢f"—FVÒÒ7F÷&vT&VävWD—FVÒ‡&÷W'G’“°¢–b†—FVÒÒçVÆÂ¢°¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†—FVÒ“°¢&WGW&âG'VS°¢Ð¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR&ööÂG'”vWD†—7F÷'•&÷W'G’„fVä§4†—7F÷'”†÷7B†—7F÷'’Â7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢–b‡7G&–æräWVÇ2‡&÷W'G’Â'W6…7FFR"Â7G&–æt6ö×&—6öâä÷&F–æÂ’ÇÀ¢7G&–æräWVÇ2‡&÷W'G’Â'&WÆ6U7FFR"Â7G&–æt6ö×&—6öâä÷&F–æÂ’¢°¢fÇVRÒö÷væW"ävWE7F÷&VD†÷7E&÷W'G”÷%VæFVf–æVB††—7F÷'’Â&÷W'G’“°¢–b‡fÇVRåFrÒ§5fÇVUFråVæFVf–æVB¢°¢&WGW&âG'VS°¢Ð¢Ð ¢7v—F6‚‡&÷W'G’¢°¢66R&ÆVæwF‚# ¢fÇVRÒ§5fÇVRäg&öÔ–çC3"…ö÷væW"åö†—7F÷'”'&–FvSòäÆVæwF‚óò†—7F÷'’äÆVæwF‚“°¢&WGW&âG'VS°¢66R'7FFR# ¢fÇVRÒö÷væW"åö†—7F÷'”'&–FvRÒçVÆÀ¢ò6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR¢¢†—7F÷'’å7FFS°¢&WGW&âG'VS°¢66R'W6…7FFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢†—7F÷'’À¢'W6…7FFR"À¢…òÂ&w2’Óà¢°¢f"7FFRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRäçVÆÃ°¢f"F—FÆRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"W&ÂÒ&w2ä6÷VçBâ"bb&w5³%ÒåFrÒ§5fÇVUFråVæFVf–æV@¢ò6öW&6UFô†÷7E7G&–ær†&w5³%Ò¢¢7G&–æräV×G“° ¢–b…ö÷væW"åö†—7F÷'”'&–FvRÒçVÆÂ¢°¢ö÷væW"åö†—7F÷'”'&–FvRåW6…7FFR‡7FFRÂF—FÆRÂW&Â“°¢†—7F÷'’äÆVæwF‚Òö÷væW"åö†—7F÷'”'&–FvRäÆVæwFƒ°¢†—7F÷'’å7FFRÒ6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR“°¢ö÷væW"åWFFTfVä§4Æö6F–öâ††—7F÷'’äÆö6F–öâÂö÷væW"åö†—7F÷'”'&–FvRä7W'&VçEW&Â“°¢Ð¢VÇ6P¢°¢†—7F÷'’å7FFRÒ7FFS°¢†—7F÷'’äÆVæwF‚ÒÖF‚äÖ‚ƒÂ†—7F÷'’äÆVæwF‚²“°¢ö÷væW"äÇ”†—7F÷'•W&Â††—7F÷'’äÆö6F–öâÂW&Â“°¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R'&WÆ6U7FFR# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢†—7F÷'’À¢'&WÆ6U7FFR"À¢…òÂ&w2’Óà¢°¢f"7FFRÒ&w2ä6÷VçBâò&w5³Ò¢§5fÇVRäçVÆÃ°¢f"F—FÆRÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢7G&–æräV×G“°¢f"W&ÂÒ&w2ä6÷VçBâ"bb&w5³%ÒåFrÒ§5fÇVUFråVæFVf–æV@¢ò6öW&6UFô†÷7E7G&–ær†&w5³%Ò¢¢7G&–æräV×G“° ¢–b…ö÷væW"åö†—7F÷'”'&–FvRÒçVÆÂ¢°¢ö÷væW"åö†—7F÷'”'&–FvRå&WÆ6U7FFR‡7FFRÂF—FÆRÂW&Â“°¢†—7F÷'’äÆVæwF‚Òö÷væW"åö†—7F÷'”'&–FvRäÆVæwFƒ°¢†—7F÷'’å7FFRÒ6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR“°¢ö÷væW"åWFFTfVä§4Æö6F–öâ††—7F÷'’äÆö6F–öâÂö÷væW"åö†—7F÷'”'&–FvRä7W'&VçEW&Â“°¢Ð¢VÇ6P¢°¢†—7F÷'’å7FFRÒ7FFS°¢ö÷væW"äÇ”†—7F÷'•W&Â††—7F÷'’äÆö6F–öâÂW&Â“°¢Ð ¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢"“°¢&WGW&âG'VS°¢66R&vò# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢†—7F÷'’À¢&vò"À¢…òÂ&w2’Óà¢°¢f"FVÇFÒ&w2ä6÷VçBâò†–çB”6öW&6UFôf–æ—FTçVÖ&W"†&w5³ÒÂ’¢°¢ö÷væW"åö†—7F÷'”'&–FvSòävò†FVÇF“°¢–b…ö÷væW"åö†—7F÷'”'&–FvRÒçVÆÂ¢°¢†—7F÷'’äÆVæwF‚Òö÷væW"åö†—7F÷'”'&–FvRäÆVæwFƒ°¢†—7F÷'’å7FFRÒ6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR“°¢ö÷væW"åWFFTfVä§4Æö6F–öâ††—7F÷'’äÆö6F–öâÂö÷væW"åö†—7F÷'”'&–FvRä7W'&VçEW&Â“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&&6²# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢†—7F÷'’À¢&&6²"À¢…òÂò’Óà¢°¢ö÷væW"åö†—7F÷'”'&–FvSòävò‚Ó“°¢–b…ö÷væW"åö†—7F÷'”'&–FvRÒçVÆÂ¢°¢†—7F÷'’äÆVæwF‚Òö÷væW"åö†—7F÷'”'&–FvRäÆVæwFƒ°¢†—7F÷'’å7FFRÒ6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR“°¢ö÷væW"åWFFTfVä§4Æö6F–öâ††—7F÷'’äÆö6F–öâÂö÷væW"åö†—7F÷'”'&–FvRä7W'&VçEW&Â“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&f÷'v&B# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢†—7F÷'’À¢&f÷'v&B"À¢…òÂò’Óà¢°¢ö÷væW"åö†—7F÷'”'&–FvSòävòƒ“°¢–b…ö÷væW"åö†—7F÷'”'&–FvRÒçVÆÂ¢°¢†—7F÷'’äÆVæwF‚Òö÷væW"åö†—7F÷'”'&–FvRäÆVæwFƒ°¢†—7F÷'’å7FFRÒ6öW&6T'&–FvT†—7F÷'•7FFR…ö÷væW"åö†—7F÷'”'&–FvRå7FFR“°¢ö÷væW"åWFFTfVä§4Æö6F–öâ††—7F÷'’äÆö6F–öâÂö÷væW"åö†—7F÷'”'&–FvRä7W'&VçEW&Â“°¢Ð¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR7FF–2§5fÇVR6öW&6T'&–FvT†—7F÷'•7FFR†ö&¦V7B7FFR¢°¢&WGW&â7FFR7v—F6€¢°¢çVÆÂÓâ§5fÇVRäçVÆÂÀ¢§5fÇVR§5fÇVRÓâ§5fÇVRÀ¢7G&–ærFW‡BÓâ§5fÇVRäg&öÕ7G&–ær‡FW‡B’À¢&ööÂ&ööÆVâÓâ§5fÇVRäg&öÔ&ööÆVâ†&ööÆVâ’À¢–çB–çC3"Óâ§5fÇVRäg&öÔ–çC3"†–çC3"’À¢F÷V&ÆRçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"†çVÖ&W"’À¢fÆöBçVÖ&W"Óâ§5fÇVRäg&öÔçVÖ&W"†çVÖ&W"’À¢Æöær–çFVvW"Óâ§5fÇVRäg&öÔçVÖ&W"†–çFVvW"’À¢òÓâ§5fÇVRäçVÆÀ¢Ó°¢Ð ¢&—fFR&ööÂG'”vWDÆö6F–öå&÷W'G’„fVä§4Æö6F–öä†÷7BÆö6F–öâÂ7G&–ær&÷W'G’Â÷WB§5fÇVRfÇVR¢°¢f"W&’ÒÆö6F–öâåW&“°¢f"'6öÇWFRÒW&“òä'6öÇWFUW&’óò7G&–æräV×G“°¢7v—F6‚‡&÷W'G’¢°¢66R&‡&Vb# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær†'6öÇWFR“°¢&WGW&âG'VS°¢66R&÷&–v–â# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òävWDÆVgE'B…W&•'F–ÂäWF†÷&—G’’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'&÷Fö6öÂ# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òå66†VÖR—27G&–ær66†VÖRbb66†VÖRäÆVæwF‚âò66†VÖR²#¢"¢7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&†÷7B# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òä—4FVfVÇE÷'BÓÒfÇ6RòB'·W&’ä†÷7GÓ§·W&’å÷'GÒ"¢W&“òä†÷7Bóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&†÷7FæÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òä†÷7Bóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'F†æÖR# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òä'6öÇWFUF‚óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'6V&6‚# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òåVW'’óò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R&†6‚# ¢fÇVRÒ§5fÇVRäg&öÕ7G&–ær‡W&“òäg&vÖVçBóò7G&–æräV×G’“°¢&WGW&âG'VS°¢66R'Fõ7G&–ær# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Æö6F–öâÀ¢'Fõ7G&–ær"À¢…òÂò’Óâ§5fÇVRäg&öÕ7G&–ær†Æö6F–öâåW&“òä'6öÇWFUW&’óò7G&–æräV×G’’À¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&VÆöB# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Æö6F–öâÂ'&VÆöB"À¢…òÂó"’Óâ²ö÷væW"äæf–vFT÷væ–æt'&÷w6–æt6öçFW‡B‡W&’“²&WGW&â§5fÇVRåVæFVf–æVC²ÒÀ¢ÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R'&WÆ6R# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Æö6F–öâÂ'&WÆ6R"À¢…òÂ&w2’Óà¢°¢f"W&ÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢çVÆÃ°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R‡W&Â’bbW&’åG'”7&VFR‡W&’ÂW&ÂÂ÷WBf"æeW&’’¢ö÷væW"äæf–vFT÷væ–æt'&÷w6–æt6öçFW‡B†æeW&’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢“°¢&WGW&âG'VS°¢66R&76–vâ# ¢fÇVRÒö÷væW"ävWD÷$7&VFT†÷7D6ÆÆ&ÆR€¢Æö6F–öâÂ&76–vâ"À¢…òÂ&w2’Óà¢°¢f"W&ÂÒ&w2ä6÷VçBâò6öW&6UFô†÷7E7G&–ær†&w5³Ò’¢çVÆÃ°¢–b‚7G&–ærä—4çVÆÄ÷%v†—FU76R‡W&Â’bbW&’åG'”7&VFR‡W&’ÂW&ÂÂ÷WBf"æeW&’’¢ö÷væW"äæf–vFT÷væ–æt'&÷w6–æt6öçFW‡B†æeW&’“°¢&WGW&â§5fÇVRåVæFVf–æVC°¢ÒÂÆVæwFƒ¢“°¢&WGW&âG'VS°¢FVfVÇC ¢fÇVRÒ§5fÇVRåVæFVf–æVC°¢&WGW&âfÇ6S°¢Ð¢Ð ¢&—fFR7FF–27G&–ær&W6öÇfTVÆVÖVçEW&Å&÷W'G’„VÆVÖVçBVÆVÖVçBÂ7G&–ærGG&–'WFTæÖR¢°¢f"&rÒVÆVÖVçCòävWDGG&–'WFR†GG&–'WFTæÖR’óò7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡&r’¢°¢&WGW&â7G&–æräV×G“°¢Ð ¢–b…W&’åG'”7&VFR‡&rÂW&”¶–æBä'6öÇWFRÂ÷WBf"'6öÇWFR’¢°¢&WGW&â'6öÇWFRä'6öÇWFUW&“°¢Ð ¢f"÷væW$Fö7VÖVçBÒVÆVÖVçBä÷væW$Fö7VÖVçC°¢f"&6U&rÐ¢÷væW$Fö7VÖVçCòä&6UU$’óð¢÷væW$Fö7VÖVçCòäFö7VÖVçEU$’óð¢÷væW$Fö7VÖVçCòåU$Ã° ¢–b…W&’åG'”7&VFR†&6U&rÂW&”¶–æBä'6öÇWFRÂ÷WBf"&6UW&’’b`¢W&’åG'”7&VFR†&6UW&’Â&rÂ÷WBf"&W6öÇfVB’¢°¢&WGW&â&W6öÇfVBä'6öÇWFUW&“°¢Ð ¢&WGW&â&s°¢Ð ¢&—fFR7FF–2&ööÂ—4–ÖvTVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–Ör"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–ÖvR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–2&ööÂ—4ÖWFVÆVÖVçB„VÆVÖVçBVÆVÖVçB’Óà¢7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&ÖWF"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“° ¢&—fFR7FF–2&ööÂ—567&—DVÆVÖVçB„VÆVÖVçBVÆVÖVçB’Óà¢7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ'67&—B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“° ¢&—fFR7FF–2fö–B6WD&ööÆVäGG&–'WFR„VÆVÖVçBVÆVÖVçBÂ7G&–ærGG&–'WFTæÖRÂ&ööÂVæ&ÆVB¢°¢–b†Væ&ÆVB¢°¢VÆVÖVçBå6WDGG&–'WFR†GG&–'WFTæÖRÂ7G&–æräV×G’“°¢Ð¢VÇ6P¢°¢VÆVÖVçBå&VÖ÷fTGG&–'WFR†GG&–'WFTæÖR“°¢Ð¢Ð ¢&—fFR7FF–2&ööÂ—4F–ÆötVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&F–Æör"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–2&ööÂ—5FV×ÆFTVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ'FV×ÆFR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–2&ööÂ—4”g&ÖTVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢&WGW&â7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–g&ÖR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–2&ööÂ—46†V6¶&ÆT–çWDVÆVÖVçB„VÆVÖVçBVÆVÖVçB¢°¢–b‚7G&–æräWVÇ2†VÆVÖVçCòåFtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âfÇ6S°¢Ð ¢f"G—RÒVÆVÖVçBävWDGG&–'WFR‚'G—R"“°¢&WGW&â7G&–æräWVÇ2‡G—RÂ&6†V6¶&÷‚"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡G—RÂ'&F–ò"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ð ¢&—fFR7FF–27G&–ær&VDVÆVÖVçEfÇVR„VÆVÖVçBVÆVÖVçB¢°¢–b†VÆVÖVçBÓÒçVÆÂ¢°¢&WGW&â7G&–æräV×G“°¢Ð ¢f"GG%fÇVRÒVÆVÖVçBävWDGG&–'WFR‚'fÇVR"“°¢–b†GG%fÇVRÒçVÆÂ¢°¢&WGW&âGG%fÇVS°¢Ð ¢–b‡7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ'FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ&÷F–öâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âVÆVÖVçBåFW‡D6öçFVçBóò7G&–æräV×G“°¢Ð ¢&WGW&â7G&–æräV×G“°¢Ð ¢&—fFR7FF–27G&–ær&VD–çWEG—R„VÆVÖVçBVÆVÖVçB¢°¢f"G—RÒ†VÆVÖVçCòävWDGG&–'WFR‚'G—R"’óò7G&–æräV×G’’åG&–Ò‚’åFôÆ÷vW$–çf&–çB‚“°¢&WGW&âG—R7v—F6€¢°¢&†–FFVâ"÷"'FW‡B"÷"'6V&6‚"÷"'FVÂ"÷"'W&Â"÷"&VÖ–Â"÷"'77v÷&B"÷ ¢&FFR"÷"&ÖöçF‚"÷"'vVV²"÷"'F–ÖR"÷"&FFWF–ÖRÖÆö6Â"÷"&çVÖ&W""÷ ¢'&ævR"÷"&6öÆ÷""÷"&6†V6¶&÷‚"÷"'&F–ò"÷"&f–ÆR"÷"'7V&Ö—B"÷"&–ÖvR"÷ ¢'&W6WB"÷"&'WGFöâ"ÓâG—RÀ¢òÓâ'FW‡B ¢Ó°¢Ð ¢&—fFR7FF–2fö–B6WDVÆVÖVçEfÇVR„VÆVÖVçBVÆVÖVçBÂ7G&–ærfÇVR¢°¢–b†VÆVÖVçBÓÒçVÆÂ¢°¢&WGW&ã°¢Ð ¢fÇVRóóÒ7G&–æräV×G“°¢VÆVÖVçBå6WDGG&–'WFR‚'fÇVR"ÂfÇVR“°¢–b‡7G&–æräWVÇ2†VÆVÖVçBåFtæÖRÂ'FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢VÆVÖVçBåFW‡D6öçFVçBÒfÇVS°¢Ð¢Ð ¢&—fFR7FF–2–çB&VDVÆVÖVçEF$–æFW‚„VÆVÖVçBVÆVÖVçB¢°¢f"&rÒVÆVÖVçCòävWDGG&–'WFR‚'F&–æFW‚"“°¢–b†–çBåG'•'6R‡&rÂçVÖ&W%7G–ÆW2ä–çFVvW"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂ÷WBf"'6VB’¢°¢&WGW&â'6VC°¢Ð ¢–b†VÆVÖVçBÓÒçVÆÂ¢°¢&WGW&âÓ°¢Ð ¢f"FtæÖRÒVÆVÖVçBåFtæÖRóò7G&–æräV×G“°¢–b‡7G&–æräWVÇ2‡FtæÖRÂ&"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&âVÆVÖVçBä†4GG&–'WFR‚&‡&Vb"’ò¢Ó°¢Ð ¢&WGW&à¢7G&–æräWVÇ2‡FtæÖRÂ&'WGFöâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡FtæÖRÂ&–çWB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡FtæÖRÂ'6VÆV7B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡FtæÖRÂ'FW‡F&V"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀ¢7G&–æräWVÇ2‡FtæÖRÂ&–g&ÖR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R¢ò ¢¢Ó°¢Ð¢Ð§Ð ¢òòòÇ7VÖÖ'“à¢òòò6VçG&Æ—¦VB'VçF–ÖR6VÆV7F–öâf÷"'&÷w6W"67&—BW†V7WF–öâà¢òòòF†R'&÷w6W"æ÷r'Vç2öâF†RfVä¥2Ö&6¶VB'VçF–ÖR'’FVfVÇC²F†RÆVv7¢òòòfVäVæv–æR'VçF–ÖR&VÖ–ç2&V6†&ÆRöæÇ’2âW‡Æ–6—BW66R†F6‚f–¢òòòÇ7VÖÖ'“à¢òòò7FF–2f7F÷'’f÷"'&÷w6W"67&—BVæv–æR–ç7Fæ6W2à¢òòòfVä¥2—2F†RöæÇ’'VçF–ÖR(	BæòÆVv7’fÆÆ&6²à¢òòòÂ÷7VÖÖ'“à§V&Æ–27FF–26Æ72'&÷w6W%67&—DVæv–æU'VçF–ÖP§°¢&—fFR7FF–2gVæ3Ä”§4†÷7BÂ”'&÷w6W%67&—DVæv–æSâöf7F÷'’Ò7&VFT6öæf–wW&VDFVfVÇC° ¢V&Æ–27FF–2gVæ3Ä”§4†÷7BÂ”'&÷w6W%67&—DVæv–æSâf7F÷'¢°¢vWBÓâöf7F÷'“°¢6WBÓâöf7F÷'’ÒfÇVRóò7&VFT6öæf–wW&VDFVfVÇC°¢Ð ¢V&Æ–27FF–2”'&÷w6W%67&—DVæv–æR7&VFR„”§4†÷7B†÷7B¢°¢&WGW&âöf7F÷'’††÷7B“°¢Ð ¢V&Æ–27FF–2fö–B&W6WB‚¢°¢öf7F÷'’Ò7&VFT6öæf–wW&VDFVfVÇC°¢Ð ¢&—fFR7FF–2”'&÷w6W%67&—DVæv–æR7&VFT6öæf–wW&VDFVfVÇB„”§4†÷7B†÷7B¢°¢&WGW&âæWrfVä§4'&÷w6W%67&—DVæv–æR††÷7B“°¢Ð§Ð ¢òòòÇ7VÖÖ'“à¢òòòF–væ÷7F–2&öÖ—6R&V¦V7F–öâG&6¶W"F†BFVfW'2&W÷'F–ærVçF–ÂF†P¢òòòÖ–7&÷F6²6†V6·ö–çB6ò6ÖR×GW&â†æFÆW'2Fòæ÷B&öGV6RfÇ6Rf–ÇW&W2à¢òòòFVÆVvFW2FòF†R–ææW"G&6¶W"f÷"æ÷&ÖÂ÷W&F–öâà¢òòòÂ÷7VÖÖ'“à¦–çFW&æÂ6VÆVB6Æ72fVä§4F–væ÷7F–5&öÖ—6U&V¦V7F–öåG&6¶W"¢”†÷7E&öÖ—6U&V¦V7F–öåG&6¶W §°¢&—fFR&VFöæÇ’”†÷7E&öÖ—6U&V¦V7F–öåG&6¶W"ö–ææW#°¢&—fFR&VFöæÇ’7F–öãÄ§5fÇVRÂ&öÖ—6U&V¦V7F–öä÷W&F–öãâöÆ–fV7–6ÆTö'6W'fW#°¢&—fFR&VFöæÇ’7F–öãÄ§5fÇVSâ÷Væ†æFÆVDö'6W'fW#°¢&—fFR&VFöæÇ’ö&¦V7B÷7–æ2ÒæWr‚“°¢&—fFR&VFöæÇ’Æ—7CÄ§5fÇVSâ÷VæF–ærÒæWr‚“°¢&—fFR–çB÷&V¦V7FVD6÷VçC°¢&—fFR–çBö†æFÆVD6÷VçC° ¢V&Æ–2fVä§4F–væ÷7F–5&öÖ—6U&V¦V7F–öåG&6¶W"€¢”†÷7E&öÖ—6U&V¦V7F–öåG&6¶W"–ææW"À¢7F–öãÄ§5fÇVRÂ&öÖ—6U&V¦V7F–öä÷W&F–öãâÆ–fV7–6ÆTö'6W'fW"À¢7F–öãÄ§5fÇVSâVæ†æFÆVDö'6W'fW"¢°¢ö–ææW"Ò–ææW"óòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†–ææW"’“°¢öÆ–fV7–6ÆTö'6W'fW"ÒÆ–fV7–6ÆTö'6W'fW"óòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb†Æ–fV7–6ÆTö'6W'fW"’“°¢÷Væ†æFÆVDö'6W'fW"ÒVæ†æFÆVDö'6W'fW"óòF‡&÷ræWr&wVÖVçDçVÆÄW†6WF–öâ†æÖVöb‡Væ†æFÆVDö'6W'fW"’“°¢Ð ¢V&Æ–2fö–BG&6²„§5fÇVR&öÖ—6RÂ&öÖ—6U&V¦V7F–öä÷W&F–öâ÷W&F–öâ¢°¢ö–ææW"åG&6²‡&öÖ—6RÂ÷W&F–öâ“°¢f"ö'6W'fVBÒfÇ6S°¢Æö6²…÷7–æ2¢°¢–b†÷W&F–öâÓÒ&öÖ—6U&V¦V7F–öä÷W&F–öâå&V¦V7B¢°¢–b…÷VæF–ærä6÷VçBÂ#‚ÇÂ÷VæF–ærä6öçF–ç2‡&öÖ—6R’¢°¢–b‚÷VæF–ærä6öçF–ç2‡&öÖ—6R’¢°¢÷VæF–æräFB‡&öÖ—6R“°¢Ð¢ö'6W'fVBÒG'VS°¢Ð¢Ð¢VÇ6R–b†÷W&F–öâÓÒ&öÖ—6U&V¦V7F–öä÷W&F–öâä†æFÆR¢°¢÷VæF–ærå&VÖ÷fR‡&öÖ—6R“°¢–çFW&Æö6¶VBä–æ7&VÖVçB‡&Vbö†æFÆVD6÷VçB“°¢ö'6W'fVBÒG'VS°¢Ð¢Ð ¢–b†ö'6W'fVB¢°¢G'¢°¢öÆ–fV7–6ÆTö'6W'fW"‡&öÖ—6RÂ÷W&F–öâ“°¢Ð¢6F6€¢°¢òòF–væ÷7F–72×W7Bæ÷B6†ævR&öÖ—6R6WGFÆVÖVçB÷"†æFÆW"&V†f–÷"à¢Ð¢Ð¢Ð ¢V&Æ–2fö–BfÇW6…VæF–ær‚¢°¢§5fÇVUµÒVæF–æs°¢Æö6²…÷7–æ2¢°¢–b…÷VæF–ærä6÷VçBÓÒ¢°¢&WGW&ã°¢Ð ¢VæF–ærÒ÷VæF–æråFô'&’‚“°¢÷VæF–ærä6ÆV"‚“°¢Ð ¢f÷&V6‚‡f"&öÖ—6R–âVæF–ær¢°¢f"6÷VçBÒ–çFW&Æö6¶VBä–æ7&VÖVçB‡&Vb÷&V¦V7FVD6÷VçB“°¢G'¢°¢÷Væ†æFÆVDö'6W'fW"‡&öÖ—6R“°¢Ð¢6F6€¢°¢òòF–væ÷7F–72×W7Bæ÷B6†ævR&öÖ—6R6WGFÆVÖVçB÷"†æFÆW"&V†f–÷"à¢Ð ¢–b†6÷VçBÃÒ#¢°¢fVäÆövvW"åv&â€¢B%µ&öÖ—6U&V¦V7F–öåÒVæ†æFÆVB&V¦V7F–öâ7¶6÷VçGÒFWFV7FVB"À¢Æöt6FVv÷'’ä¦f67&—B“°¢Ð¢Ð¢Ð ¢V&Æ–2fö–B&W6WB‚¢°¢Æö6²…÷7–æ2¢°¢÷VæF–ærä6ÆV"‚“°¢Ð¢–çFW&Æö6¶VBäW†6†ævR‡&Vb÷&V¦V7FVD6÷VçBÂ“°¢–çFW&Æö6¶VBäW†6†ævR‡&Vbö†æFÆVD6÷VçBÂ“°¢Ð§Ð