YªçŠx-®éÜj×¢ëiºÚ+Š§j[h‘éÜ¢éíßOz×Ý|÷Ý4o+^²‰¢¶×using System;
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
        var policy = PermissionsPolicyProvider?.Invoke();
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
        var type = scrÛ]µçkh‘éì¶»§q«^t€€€€€€€€€€€€€€€€€€€Ù…È½™™Í•Ð€ô…ÉÌ¹½Õ¹Ð€ø€À€˜˜QÉå½•É•%¹‘•à¡…ÉÍlÁt°½ÕÐÙ…ÈÁ…ÉÍ•‘=™™Í•Ð¤€üÁ…ÉÍ•‘=™™Í•Ð€è€Àì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È½Õ¹Ð€ô…ÉÌ¹½Õ¹Ð€ø€Ä€˜˜QÉå½•É•%¹‘•à¡…ÉÍlÅt°½ÕÐÙ…ÈÁ…ÉÍ•‘½Õ¹Ð¤€üÁ…ÉÍ•‘½Õ¹Ð€è€Àì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¡…É…Ñ•É…Ñ„¹MÕ‰ÍÑÉ¥¹…Ñ„¡½™™Í•Ð°½Õ¹Ð¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•µ½Ù”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡…É…Ñ•É…Ñ„°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•µ½Ù”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¡…É…Ñ•É…Ñ„¹I•µ½Ù” ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±½¹•9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡…É…Ñ•É…Ñ„°(€€€€€€€€€€€€€€€€€€€€€€€€‰±½¹•9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È‘••À€ô…ÉÌ¹½Õ¹Ð€ø€À€˜˜½•É•Q½!½ÍÑ	½½±•…¸¡…ÉÍlÁt¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡¡…É…Ñ•É…Ñ„¹±½¹•9½‘”¡‘••À¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½µÁ…É•½Õµ•¹ÑA½Í¥Ñ¥½¸ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ•½µÁ…É•½Õµ•¹ÑA½Í¥Ñ¥½¹…±±…‰±”¡¡…É…Ñ•É…Ñ„¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ½Õµ•¹ÑÉ…µ•¹ÑAÉ½Á•ÉÑä¡½Õµ•¹ÑÉ…µ•¹Ð™É…µ•¹Ð°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰¹½‘•9…µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡™É…µ•¹Ð¹9½‘•9…µ”€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹½‘•QåÁ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ ¡¥¹Ð¥™É…µ•¹Ð¹9½‘•QåÁ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹½‘•Y…±Õ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ñ•áÑ½¹Ñ•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡™É…µ•¹Ð¹Q•áÑ½¹Ñ•¹Ð€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥Í½¹¹•Ñ•ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡™É…µ•¹Ð¹%Í½¹¹•Ñ•¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡½ÍÐˆÝ¡•¸™É…µ•¹Ð¥ÌM¡…‘½ÝI½½ÐÍ¡…‘½ÝI½½Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ=É9Õ±°¡Í¡…‘½ÝI½½Ð¹!½ÍÐ°!½ÍÑ=‰©•Ñ-¥¹¹½µ±•µ•¹Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰µ½‘”ˆÝ¡•¸™É…µ•¹Ð¥ÌM¡…‘½ÝI½½ÐÍ¡…‘½ÝI½½Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡Í¡…‘½ÝI½½Ð¹5½‘”€ôôM¡…‘½ÝI½½Ñ5½‘”¹±½Í•€ü€‰±½Í•ˆ€è€‰½Á•¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘•±•…Ñ•Í½ÕÌˆÝ¡•¸™É…µ•¹Ð¥ÌM¡…‘½ÝI½½ÐÍ¡…‘½ÝI½½Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡Í¡…‘½ÝI½½Ð¹•±•…Ñ•Í½ÕÌ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í±½ÑÍÍ¥¹µ•¹ÐˆÝ¡•¸™É…µ•¹Ð¥ÌM¡…‘½ÝI½½ÐÍ¡…‘½ÝI½½Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡Í¡…‘½ÝI½½Ð¹M±½ÑÍÍ¥¹µ•¹Ð€ôôM±½ÑÍÍ¥¹µ•¹Ñ5½‘”¹5…¹Õ…°€ü€‰µ…¹Õ…°ˆ€è€‰¹…µ•ˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½Ý¹•É½Õµ•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ=É9Õ±°¡™É…µ•¹Ð¹=Ý¹•É½Õµ•¹Ð°!½ÍÑ=‰©•Ñ-¥¹¹½µ½Õµ•¹Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á…É•¹Ñ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹A…É•¹Ñ9½‘”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á…É•¹Ñ±•µ•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹A…É•¹Ñ9½‘”…Ì±•µ•¹Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™¥ÉÍÑ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹¥ÉÍÑ¡¥±¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±…ÍÑ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹1…ÍÑ¡¥±¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ•Ù¥½ÕÍM¥‰±¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹AÉ•Ù¥½ÕÍM¥‰±¥¹œ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹•áÑM¥‰±¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹9•áÑM¥‰±¥¹œ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡¥±‘9½‘•Ìˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ•9½‘•ÉÉ…å1¥­”¡™É…µ•¹Ð¹¡¥±‘9½‘•Ì¹Q½ÉÉ…ä ¤¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™¥ÉÍÑ±•µ•¹Ñ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹¥ÉÍÑ±•µ•¹Ñ¡¥±¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±…ÍÑ±•µ•¹Ñ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹1…ÍÑ±•µ•¹Ñ¡¥±¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡¥±‘±•µ•¹Ñ½Õ¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡™É…µ•¹Ð¹¡¥±‘±•µ•¹Ñ½Õ¹Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡¥±‘É•¸ˆè(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡¥±‘É•¸€ô¹•Ü1¥ÍÐñ9½‘”ø ¤ì(€€€€€€€€€€€€€€€€€€€€€€€™½È€¡Ù…È¤€ô€Àì¤€ð™É…µ•¹Ð¹¡¥±‘9½‘•Ì¹1•¹Ñ ì¤¬¬¤(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡™É…µ•¹Ð¹¡¥±‘9½‘•Ím¥t¥Ì±•µ•¹Ð¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥±‘É•¸¹‘¡™É…µ•¹Ð¹¡¥±‘9½‘•Ím¥t¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ•9½‘•ÉÉ…å1¥­”¡¡¥±‘É•¸¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…‘‘Ù•¹Ñ1¥ÍÑ•¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰…‘‘Ù•¹Ñ1¥ÍÑ•¹•Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹‘‘	É½ÝÍ•ÉÙ•¹Ñ1¥ÍÑ•¹•È (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}•±•µ•¹ÑÙ•¹Ñ1¥ÍÑ•¹•ÉÌ¹•Ñ=ÉÉ•…Ñ•Y…±Õ”¡™É…µ•¹Ð¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€…ÉÌ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•µ½Ù•Ù•¹Ñ1¥ÍÑ•¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•µ½Ù•Ù•¹Ñ1¥ÍÑ•¹•Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹I•µ½Ù•	É½ÝÍ•ÉÙ•¹Ñ1¥ÍÑ•¹•È (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}•±•µ•¹ÑÙ•¹Ñ1¥ÍÑ•¹•ÉÌ¹•Ñ=ÉÉ•…Ñ•Y…±Õ”¡™É…µ•¹Ð¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€…ÉÌ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘¥ÍÁ…Ñ¡Ù•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰‘¥ÍÁ…Ñ¡Ù•¹Ðˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È•Ù•¹ÑY…±Õ”€ô…ÉÌ¹½Õ¹Ð€ø€À€ü…ÉÍlÁt€è)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÑåÁ”€ô}½Ý¹•È¹I•…‘Ù•¹ÑQåÁ”¡•Ù•¹ÑY…±Õ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÑ…É•Ð€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹AÉ•Á…É•¥ÍÁ…Ñ¡•‘Ù•¹Ð¡•Ù•¹ÑY…±Õ”°Ñ…É•Ð¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹¥ÍÁ…Ñ¡	É½ÝÍ•ÉÙ•¹Ð (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}•±•µ•¹ÑÙ•¹Ñ1¥ÍÑ•¹•ÉÌ¹•Ñ=ÉÉ•…Ñ•Y…±Õ”¡™É…µ•¹Ð¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑåÁ”°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€Ñ…É•Ð°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€•Ù•¹ÑY…±Õ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹É½µ	½½±•…¸ (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€…}½Ý¹•È¹I•…‘)Í	½½±AÉ½Á•ÉÑä¡•Ù•¹ÑY…±Õ”°€‰‘•™…Õ±ÑAÉ•Ù•¹Ñ•ˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…ÁÁ•¹‘¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰…ÁÁ•¹‘¡¥±ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡…ÉÌ¹½Õ¹Ð€ø€À€˜˜}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñÑÑÈø¡…ÉÍlÁt¤€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý!¥•É…É¡åI•ÅÕ•ÍÑÉÉ½È ‰ÑÑÉ¥‰ÕÑ•Ì…¹¹½Ð‰”¥¹Í•ÉÑ•…Ì¡¥±¹½‘•Ì¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡¥±€ô…ÉÌ¹½Õ¹Ð€ø€À€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹ÁÁ•¹‘¡¥±¡¡¥±¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥¹Í•ÉÑ	•™½É”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰¥¹Í•ÉÑ	•™½É”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡…ÉÌ¹½Õ¹Ð€ø€À€˜˜}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñÑÑÈø¡…ÉÍlÁt¤€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý!¥•É…É¡åI•ÅÕ•ÍÑÉÉ½È ‰ÑÑÉ¥‰ÕÑ•Ì…¹¹½Ð‰”¥¹Í•ÉÑ•…Ì¡¥±¹½‘•Ì¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡¥±€ô…ÉÌ¹½Õ¹Ð€ø€À€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÉ•™•É•¹•9½‘”€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÅt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑÉä(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹%¹Í•ÉÑ	•™½É”¡¡¥±°É•™•É•¹•9½‘”¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸•à¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸¡•à¹9…µ”°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±½¹•9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰±½¹•9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È‘••À€ô…ÉÌ¹½Õ¹Ð€ø€À€˜˜½•É•Q½!½ÍÑ	½½±•…¸¡…ÉÍlÁt¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹±½¹•9½‘”¡‘••À¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•µ½Ù•¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•µ½Ù•¡¥±ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡¥±€ô…ÉÌ¹½Õ¹Ð€ø€À€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ ‰QåÁ•ÉÉ½Èˆ°€‰¡¥±µÕÍÐ‰”„=4¹½‘”¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑÉä(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹I•µ½Ù•¡¥±¡¡¥±¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸•à¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸¡•à¹9…µ”°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•Á±…•¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•Á±…•¡¥±ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡…ÉÌ¹½Õ¹Ð€ø€À€˜˜}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñÑÑÈø¡…ÉÍlÁt¤€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý!¥•É…É¡åI•ÅÕ•ÍÑÉÉ½È ‰ÑÑÉ¥‰ÕÑ•Ì…¹¹½Ð‰”¥¹Í•ÉÑ•…Ì¡¥±¹½‘•Ì¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡¥±€ô…ÉÌ¹½Õ¹Ð€ø€À€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ ‰QåÁ•ÉÉ½Èˆ°€‰I•Á±…•µ•¹Ð¡¥±µÕÍÐ‰”„=4¹½‘”¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È½±‘¡¥±€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÅt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡½±‘¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ ‰QåÁ•ÉÉ½Èˆ°€‰¡¥±µÕÍÐ‰”„=4¹½‘”¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑÉä(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹I•Á±…•¡¥±¡¡¥±°½±‘¡¥±¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸•à¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸¡•à¹9…µ”°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡…Í¡¥±‘9½‘•Ìˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰¡…Í¡¥±‘9½‘•Ìˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø)ÍY…±Õ”¹É½µ	½½±•…¸¡™É…µ•¹Ð¹!…Í¡¥±‘9½‘•Ì¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½¹Ñ…¥¹Ìˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰½¹Ñ…¥¹Ìˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È½Ñ¡•È€ô…ÉÌ¹½Õ¹Ð€ø€À€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹É½µ	½½±•…¸¡½Ñ¡•È€„ô¹Õ±°€˜˜™É…µ•¹Ð¹½¹Ñ…¥¹Ì¡½Ñ¡•È¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½µÁ…É•½Õµ•¹ÑA½Í¥Ñ¥½¸ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ•½µÁ…É•½Õµ•¹ÑA½Í¥Ñ¥½¹…±±…‰±”¡™É…µ•¹Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…ÁÁ•¹ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰…ÁÁ•¹ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€™½É•… €¡Ù…È…Éœ¥¸…ÉÌ¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€9½‘”¡¥±€ô}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…Éœ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥±€ô™É…µ•¹Ð¹=Ý¹•É½Õµ•¹Ðü¹É•…Ñ•Q•áÑ9½‘”¡½•É•Q½!½ÍÑMÑÉ¥¹œ¡…Éœ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð¹ÁÁ•¹‘¡¥±¡¡¥±¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ•Á•¹ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÁÉ•Á•¹ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÉ•™•É•¹•9½‘”€ô™É…µ•¹Ð¹¥ÉÍÑ¡¥±ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€™½É•… €¡Ù…È…Éœ¥¸…ÉÌ¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñÑÑÈø¡…Éœ¤€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý!¥•É…É¡åI•ÅÕ•ÍÑÉÉ½È ‰ÑÑÉ¥‰ÕÑ•Ì…¹¹½Ð‰”¥¹Í•ÉÑ•…Ì¡¥±¹½‘•Ì¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€9½‘”¡¥±€ô}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…Éœ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥±€ô™É…µ•¹Ð¹=Ý¹•É½Õµ•¹Ðü¹É•…Ñ•Q•áÑ9½‘”¡½•É•Q½!½ÍÑMÑÉ¥¹œ¡…Éœ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡¡¥±€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð¹%¹Í•ÉÑ	•™½É”¡¡¥±°É•™•É•¹•9½‘”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•Ñ±•µ•¹Ñ	å%ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰•Ñ±•µ•¹Ñ	å%ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¥€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹•Ñ±•µ•¹Ñ	å%¡¥¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÅÕ•ÉåM•±•Ñ½Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÅÕ•ÉåM•±•Ñ½Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÍ•±•Ñ½È€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑÉä(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡™É…µ•¹Ð¹EÕ•ÉåM•±•Ñ½È¡Í•±•Ñ½È¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÅÕ•ÉåM•±•Ñ½É±°ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€™É…µ•¹Ð°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÅÕ•ÉåM•±•Ñ½É±°ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÍ•±•Ñ½È€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡Í•±•Ñ½È¤¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹É•…Ñ•9½‘•ÉÉ…å1¥­”¡ÉÉ…ä¹µÁÑäñ9½‘”ø ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€ÑÉä(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹É•…Ñ•9½‘•ÉÉ…å1¥­”¡™É…µ•¹Ð¹EÕ•ÉåM•±•Ñ½É±°¡Í•±•Ñ½È¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹É•…Ñ•9½‘•ÉÉ…å1¥­”¡ÉÉ…ä¹µÁÑäñ9½‘”ø ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•ÑI…¹•AÉ½Á•ÉÑä¡½µI…¹”É…¹”°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰MQIQ}Q=}MQIPˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰MQIQ}Q=}9ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰9}Q=}9ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰9}Q=}MQIPˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ Ì¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÑ…ÉÑ½¹Ñ…¥¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡É…¹”¹MÑ…ÉÑ½¹Ñ…¥¹•È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÑ…ÉÑ=™™Í•Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡É…¹”¹MÑ…ÉÑ=™™Í•Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•¹‘½¹Ñ…¥¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡É…¹”¹¹‘½¹Ñ…¥¹•È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•¹‘=™™Í•Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡É…¹”¹¹‘=™™Í•Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½±±…ÁÍ•ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡É…¹”¹½±±…ÁÍ•¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½µµ½¹¹•ÍÑ½É½¹Ñ…¥¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡É…¹”¹½µµ½¹¹•ÍÑ½É½¹Ñ…¥¹•È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•ÑMÑ…ÉÐˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•ÑMÑ…ÉÐˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•ÑMÑ…ÉÐ (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•ÑMÑ…ÉÐˆ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡…ÉÌ°€Ä¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•Ñ¹ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•Ñ¹ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•Ñ¹ (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•Ñ¹ˆ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡…ÉÌ°€Ä¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•ÑMÑ…ÉÑ	•™½É”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•ÑMÑ…ÉÑ	•™½É”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•ÑMÑ…ÉÑ	•™½É”¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•ÑMÑ…ÉÑ	•™½É”ˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•ÑMÑ…ÉÑ™Ñ•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•ÑMÑ…ÉÑ™Ñ•Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•ÑMÑ…ÉÑ™Ñ•È¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•ÑMÑ…ÉÑ™Ñ•Èˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•Ñ¹‘	•™½É”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•Ñ¹‘	•™½É”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•Ñ¹‘	•™½É”¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•Ñ¹‘	•™½É”ˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•Ñ¹‘™Ñ•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•Ñ¹‘™Ñ•Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•Ñ¹‘™Ñ•È¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•Ñ¹‘™Ñ•Èˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½±±…ÁÍ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰½±±…ÁÍ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹½±±…ÁÍ”¡…ÉÌ¹½Õ¹Ð€ø€À€˜˜½•É•Q½!½ÍÑ	½½±•…¸¡…ÉÍlÁt¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•±•Ñ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•±•Ñ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•±•Ñ9½‘”¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•±•Ñ9½‘”ˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•±•Ñ9½‘•½¹Ñ•¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Í•±•Ñ9½‘•½¹Ñ•¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹M•±•Ñ9½‘•½¹Ñ•¹ÑÌ¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰Í•±•Ñ9½‘•½¹Ñ•¹ÑÌˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½µÁ…É•	½Õ¹‘…ÉåA½¥¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰½µÁ…É•	½Õ¹‘…ÉåA½¥¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡½Ü€ô€¡ÕÍ¡½ÉÐ¥½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡…ÉÌ°€À¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È½Ñ¡•ÉI…¹”€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ½µI…¹”ø¡…ÉÍlÅt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡½Ñ¡•ÉI…¹”€ôô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€‰QåÁ•ÉÉ½Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€‰…¥±•Ñ¼•á•ÕÑ”€½µÁ…É•	½Õ¹‘…ÉåA½¥¹ÑÌœ½¸€I…¹”œèÁ…É…µ•Ñ•È€È¥Ì¹½Ð½˜ÑåÁ”€I…¹”œ¸ˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡É…¹”¹½µÁ…É•	½Õ¹‘…ÉåA½¥¹ÑÌ¡¡½Ü°½Ñ¡•ÉI…¹”¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘•±•Ñ•½¹Ñ•¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰‘•±•Ñ•½¹Ñ•¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹•±•Ñ•½¹Ñ•¹ÑÌ ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•áÑÉ…Ñ½¹Ñ•¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰•áÑÉ…Ñ½¹Ñ•¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡É…¹”¹áÑÉ…Ñ½¹Ñ•¹ÑÌ ¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±½¹•½¹Ñ•¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰±½¹•½¹Ñ•¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡É…¹”¹±½¹•½¹Ñ•¹ÑÌ ¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥¹Í•ÉÑ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰¥¹Í•ÉÑ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹%¹Í•ÉÑ9½‘”¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰¥¹Í•ÉÑ9½‘”ˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÕÉÉ½Õ¹‘½¹Ñ•¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÍÕÉÉ½Õ¹‘½¹Ñ•¹ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹MÕÉÉ½Õ¹‘½¹Ñ•¹ÑÌ¡I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰ÍÕÉÉ½Õ¹‘½¹Ñ•¹ÑÌˆ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±½¹•I…¹”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰±½¹•I…¹”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø}½Ý¹•È¹Q½!½ÍÑ=É9Õ±°¡É…¹”¹±½¹•I…¹” ¤°!½ÍÑ=‰©•Ñ-¥¹¹=Ñ¡•È¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘•Ñ… ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰‘•Ñ… ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É…¹”¹•Ñ…  ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥ÍA½¥¹Ñ%¹I…¹”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰¥ÍA½¥¹Ñ%¹I…¹”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø)ÍY…±Õ”¹É½µ	½½±•…¸¡É…¹”¹%ÍA½¥¹Ñ%¹I…¹” (€€€€€€€€€€€€€€€€€€€€€€€€€€€I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰¥ÍA½¥¹Ñ%¹I…¹”ˆ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡…ÉÌ°€Ä¤¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½µÁ…É•A½¥¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰½µÁ…É•A½¥¹Ðˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡É…¹”¹½µÁ…É•A½¥¹Ð (€€€€€€€€€€€€€€€€€€€€€€€€€€€I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰½µÁ…É•A½¥¹Ðˆ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡…ÉÌ°€Ä¤¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥¹Ñ•ÉÍ•ÑÍ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰¥¹Ñ•ÉÍ•ÑÍ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø)ÍY…±Õ”¹É½µ	½½±•…¸¡É…¹”¹%¹Ñ•ÉÍ•ÑÍ9½‘” (€€€€€€€€€€€€€€€€€€€€€€€€€€€I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡…ÉÌ°€À°€‰¥¹Ñ•ÉÍ•ÑÍ9½‘”ˆ¤¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•…Ñ•½¹Ñ•áÑÕ…±É…µ•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•…Ñ•½¹Ñ•áÑÕ…±É…µ•¹Ðˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø%¹Ù½­•I…¹”  ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¡Ñµ°€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡}½Ý¹•È¹É•…Ñ•½¹Ñ•áÑÕ…±É…µ•¹Ð¡É…¹”°¡Ñµ°¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•Ñ	½Õ¹‘¥¹±¥•¹ÑI•Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰•Ñ	½Õ¹‘¥¹±¥•¹ÑI•Ðˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹É•…Ñ•½µI•Ð À°€À°€À°€À¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•Ñ±¥•¹ÑI•ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰•Ñ±¥•¹ÑI•ÑÌˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹É•…Ñ•µÁÑå½µI•Ñ1¥ÍÐ ¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ñ½MÑÉ¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€É…¹”°(€€€€€€€€€€€€€€€€€€€€€€€€‰Ñ½MÑÉ¥¹œˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø%¹Ù½­•I…¹”  ¤€ôø)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡É…¹”¹Q½MÑÉ¥¹œ ¤¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”)ÍY…±Õ”%¹Ù½­•I…¹”¡Õ¹Œñ)ÍY…±Õ”ø…Ñ¥½¸¤(€€€€€€€ì(€€€€€€€€€€€ÑÉä(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸…Ñ¥½¸ ¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€…Ñ €¡½µá•ÁÑ¥½¸•à¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸¡•à¹9…µ”°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€ô(€€€€€€€€€€€…Ñ €¡ÉÕµ•¹Ñá•ÁÑ¥½¸•à¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ ‰QåÁ•ÉÉ½Èˆ°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€ô(€€€€€€€€€€€…Ñ €¡%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸•à¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ ‰%¹Ù…±¥‘MÑ…Ñ•ÉÉ½Èˆ°•à¹5•ÍÍ…”¤ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”9½‘”I•ÅÕ¥É•I…¹•9½‘•ÉÕµ•¹Ð¡%I•…‘=¹±å1¥ÍÐñ)ÍY…±Õ”ø…ÉÌ°¥¹Ð¥¹‘•à°ÍÑÉ¥¹œµ•Ñ¡½‘9…µ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…È¹½‘”€ô…ÉÌ¹½Õ¹Ð€ø¥¹‘•à€ü}½Ý¹•È¹I•Í½±Ù•!½ÍÑ=‰©•Ñ=É9Õ±°ñ9½‘”ø¡…ÉÍm¥¹‘•át¤€è¹Õ±°ì(€€€€€€€€€€€¥˜€¡¹½‘”€„ô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸¹½‘”ì(€€€€€€€€€€€ô((€€€€€€€€€€€}½Ý¹•È¹Q¡É½Ý½µá•ÁÑ¥½¸ (€€€€€€€€€€€€€€€€‰QåÁ•ÉÉ½Èˆ°(€€€€€€€€€€€€€€€€‰…¥±•Ñ¼•á•ÕÑ”€íµ•Ñ¡½‘9…µ•ôœ½¸€I…¹”œèÁ…É…µ•Ñ•Èí¥¹‘•à€¬€Åô¥Ì¹½Ð½˜ÑåÁ”€9½‘”œ¸ˆ¤ì(€€€€€€€€€€€É•ÑÕÉ¸¹Õ±°ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ¥¹Ð½•É•I…¹•=™™Í•ÑÉÕµ•¹Ð¡%I•…‘=¹±å1¥ÍÐñ)ÍY…±Õ”ø…ÉÌ°¥¹Ð¥¹‘•à¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸…ÉÌ¹½Õ¹Ð€ø¥¹‘•à€˜˜QÉå½•É•%¹‘•à¡…ÉÍm¥¹‘•át°½ÕÐÙ…È½™™Í•Ð¤(€€€€€€€€€€€€€€€€ü½™™Í•Ð(€€€€€€€€€€€€€€€€è€Àì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ!Ñµ±½±±•Ñ¥½¹AÉ½Á•ÉÑä¡•¹)Í!Ñµ±½±±•Ñ¥½¹!½ÍÐ¡Ñµ±½±±•Ñ¥½¸°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…È½±±•Ñ¥½¸€ô¡Ñµ±½±±•Ñ¥½¸¹½±±•Ñ¥½¸ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰±•¹Ñ ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡½±±•Ñ¥½¸¹1•¹Ñ ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¥Ñ•´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡Ñµ±½±±•Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰¥Ñ•´ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¥¹‘•à€ô…ÉÌ¹½Õ¹Ð€ø€À€˜˜QÉå½•É•%¹‘•à¡…ÉÍlÁt°½ÕÐÙ…ÈÁ…ÉÍ•‘%¹‘•à¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€üÁ…ÉÍ•‘%¹‘•à(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€è€´Äì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡½±±•Ñ¥½¹m¥¹‘•át¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹…µ•‘%Ñ•´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡Ñµ±½±±•Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰¹…µ•‘%Ñ•´ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¹…µ”€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡½±±•Ñ¥½¸¹9…µ•‘%Ñ•´¡¹…µ”¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€¥˜€¡¥¹Ð¹QÉåA…ÉÍ”¡ÁÉ½Á•ÉÑä°9Õµ‰•ÉMÑå±•Ì¹%¹Ñ••È°Õ±ÑÕÉ•%¹™¼¹%¹Ù…É¥…¹ÑÕ±ÑÕÉ”°½ÕÐÙ…È¥¹‘•à¤¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡½±±•Ñ¥½¹m¥¹‘•át¤ì(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•ÑQÉ••]…±­•ÉAÉ½Á•ÉÑä¡•¹)ÍQÉ••]…±­•É!½ÍÐÑÉ••]…±­•É!½ÍÐ°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÑÉ••]…±­•È€ôÑÉ••]…±­•É!½ÍÐ¹QÉ••]…±­•Èì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰É½½Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹I½½Ð¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ý¡…ÑQ½M¡½Üˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ9Õµ‰•È¡ÑÉ••]…±­•È¹]¡…ÑQ½M¡½Ü¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™¥±Ñ•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÕÉÉ•¹Ñ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹ÕÉÉ•¹Ñ9½‘”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á…É•¹Ñ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰Á…É•¹Ñ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹A…É•¹Ñ9½‘” ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™¥ÉÍÑ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰™¥ÉÍÑ¡¥±ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹¥ÉÍÑ¡¥± ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±…ÍÑ¡¥±ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰±…ÍÑ¡¥±ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹1…ÍÑ¡¥± ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ•Ù¥½ÕÍM¥‰±¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÁÉ•Ù¥½ÕÍM¥‰±¥¹œˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹AÉ•Ù¥½ÕÍM¥‰±¥¹œ ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹•áÑM¥‰±¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰¹•áÑM¥‰±¥¹œˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹9•áÑM¥‰±¥¹œ ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ•Ù¥½ÕÍ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÁÉ•Ù¥½ÕÍ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹AÉ•Ù¥½ÕÍ9½‘” ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¹•áÑ9½‘”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÑÉ••]…±­•É!½ÍÐ°(€€€€€€€€€€€€€€€€€€€€€€€€‰¹•áÑ9½‘”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø}½Ý¹•È¹Q½!½ÍÑ9½‘•=É9Õ±°¡ÑÉ••]…±­•È¹9•áÑ9½‘” ¤¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ¹¥µ…Ñ¥½¹AÉ½Á•ÉÑä¡•¹)Í¹¥µ…Ñ¥½¹!½ÍÐ…¹¥µ…Ñ¥½¸°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰¥ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡…¹¥µ…Ñ¥½¸¹%¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•™™•Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô…¹¥µ…Ñ¥½¸¹-•å™É…µ•Ì¹Q…œ€ôô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•€ü)ÍY…±Õ”¹9Õ±°€è…¹¥µ…Ñ¥½¸¹-•å™É…µ•Ìì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ñ¥µ•±¥¹”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÑ…ÉÑQ¥µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô…¹¥µ…Ñ¥½¸¹MÑ…ÉÑQ¥µ”¹!…ÍY…±Õ”€ü)ÍY…±Õ”¹É½µ9Õµ‰•È¡…¹¥µ…Ñ¥½¸¹MÑ…ÉÑQ¥µ”¹Y…±Õ”¤€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÕÉÉ•¹ÑQ¥µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô…¹¥µ…Ñ¥½¸¹ÕÉÉ•¹ÑQ¥µ”¹!…ÍY…±Õ”€ü)ÍY…±Õ”¹É½µ9Õµ‰•È¡…¹¥µ…Ñ¥½¸¹ÕÉÉ•¹ÑQ¥µ”¹Y…±Õ”¤€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á±…å‰…­I…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ9Õµ‰•È¡…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á±…åMÑ…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á•¹‘¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•Á±…•MÑ…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ ‰…Ñ¥Ù”ˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•…‘äˆè(€€€€€€€€€€€€€€€…Í”€‰™¥¹¥Í¡•ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ•I•Í½±Ù•‘AÉ½µ¥Í”¡}½Ý¹•È¹Q½!½ÍÑ=É9Õ±°¡…¹¥µ…Ñ¥½¸°!½ÍÑ=‰©•Ñ-¥¹¹=Ñ¡•È¤¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…¹•°ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰…¹•°ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”€ô€‰¥‘±”ˆì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹ÕÉÉ•¹ÑQ¥µ”€ô€Àì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™¥¹¥Í ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰™¥¹¥Í ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”€ô€‰™¥¹¥Í¡•ˆì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á±…äˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰Á±…äˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”€ô€‰ÉÕ¹¹¥¹œˆì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á…ÕÍ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰Á…ÕÍ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”€ô€‰Á…ÕÍ•ˆì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•Ù•ÉÍ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•Ù•ÉÍ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”€ô…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”€ôô€À€ü€´Ä€è€µ…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…åMÑ…Ñ”€ô€‰ÉÕ¹¹¥¹œˆì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A•¹‘¥¹œ€ô™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÕÁ‘…Ñ•A±…å‰…­I…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÕÁ‘…Ñ•A±…å‰…­I…Ñ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”€ô…ÉÌ¹½Õ¹Ð€ø€À(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ü½•É•Q½¥¹¥Ñ•9Õµ‰•È¡…ÉÍlÁt°…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€è…¹¥µ…Ñ¥½¸¹A±…å‰…­I…Ñ”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á•ÉÍ¥ÍÐˆè(€€€€€€€€€€€€€€€…Í”€‰½µµ¥ÑMÑå±•Ìˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€ÁÉ½Á•ÉÑä°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø)ÍY…±Õ”¹U¹‘•™¥¹•°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…‘‘Ù•¹Ñ1¥ÍÑ•¹•Èˆè(€€€€€€€€€€€€€€€…Í”€‰É•µ½Ù•Ù•¹Ñ1¥ÍÑ•¹•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€ÁÉ½Á•ÉÑä°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø)ÍY…±Õ”¹U¹‘•™¥¹•°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘¥ÍÁ…Ñ¡Ù•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€…¹¥µ…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰‘¥ÍÁ…Ñ¡Ù•¹Ðˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø)ÍY…±Õ”¹É½µ	½½±•…¸¡ÑÉÕ”¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑå=ÉU¹‘•™¥¹•¡…¹¥µ…Ñ¥½¸°ÁÉ½Á•ÉÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸Ù…±Õ”¹Q…œ€„ô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ½µMÑÉ¥¹5…ÁAÉ½Á•ÉÑä¡•¹)Í½µMÑÉ¥¹5…Á!½ÍÐ‘½µMÑÉ¥¹5…À°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•9…µ”€ôAÉ½Á•ÉÑå9…µ•Q½…Ñ…Í•ÑÑÑÉ¥‰ÕÑ”¡ÁÉ½Á•ÉÑä¤ì(€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•Y…±Õ”€ô‘½µMÑÉ¥¹5…À¹±•µ•¹Ð¹•ÑÑÑÉ¥‰ÕÑ”¡…ÑÑÉ¥‰ÕÑ•9…µ”¤ì(€€€€€€€€€€€Ù…±Õ”€ô…ÑÑÉ¥‰ÕÑ•Y…±Õ”€ôô¹Õ±°€ü)ÍY…±Õ”¹U¹‘•™¥¹•€è)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”¤ì(€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œAÉ½Á•ÉÑå9…µ•Q½…Ñ…Í•ÑÑÑÉ¥‰ÕÑ”¡ÍÑÉ¥¹œÁÉ½Á•ÉÑå9…µ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡ÁÉ½Á•ÉÑå9…µ”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸€‰‘…Ñ„´ˆì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È‰Õ¥±‘•È€ô¹•ÜMÑÉ¥¹	Õ¥±‘•È ‰‘…Ñ„´ˆ¤ì(€€€€€€€€€€€™½È€¡Ù…È¤€ô€Àì¤€ðÁÉ½Á•ÉÑå9…µ”¹1•¹Ñ ì¤¬¬¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Ù…È €ôÁÉ½Á•ÉÑå9…µ•m¥tì(€€€€€€€€€€€€€€€¥˜€¡¡…È¹%ÍUÁÁ•È¡ ¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€‰Õ¥±‘•È¹ÁÁ•¹ œ´œ¤ì(€€€€€€€€€€€€€€€€€€€‰Õ¥±‘•È¹ÁÁ•¹¡¡…È¹Q½1½Ý•É%¹Ù…É¥…¹Ð¡ ¤¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€•±Í”(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€‰Õ¥±‘•È¹ÁÁ•¹¡ ¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô((€€€€€€€€€€€É•ÑÕÉ¸‰Õ¥±‘•È¹Q½MÑÉ¥¹œ ¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°QÉå½•É•%¹‘•à¡)ÍY…±Õ”Ù…±Õ”°½ÕÐ¥¹Ð¥¹‘•à¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡Ù…±Õ”¹Q…œ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”)ÍY…±Õ•Q…œ¹%¹ÐÌÈè(€€€€€€€€€€€€€€€€€€€¥¹‘•à€ôÙ…±Õ”¹Í%¹ÐÌÈ ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”)ÍY…±Õ•Q…œ¹9Õµ‰•Èè(€€€€€€€€€€€€€€€€€€€Ù…È¹Õµ‰•È€ôÙ…±Õ”¹Í9Õµ‰•È ¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡‘½Õ‰±”¹%Í¥¹¥Ñ”¡¹Õµ‰•È¤€˜˜5…Ñ ¹QÉÕ¹…Ñ”¡¹Õµ‰•È¤€ôô¹Õµ‰•È€˜˜¹Õµ‰•È€øô¥¹Ð¹5¥¹Y…±Õ”€˜˜¹Õµ‰•È€ðô¥¹Ð¹5…áY…±Õ”¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€¥¹‘•à€ô€¡¥¹Ð¥¹Õµ‰•Èì(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€…Í”)ÍY…±Õ•Q…œ¹MÑÉ¥¹œè(€€€€€€€€€€€€€€€€€€€¥˜€¡¥¹Ð¹QÉåA…ÉÍ”¡Ù…±Õ”¹ÍMÑÉ¥¹œ ¤°9Õµ‰•ÉMÑå±•Ì¹%¹Ñ••È°Õ±ÑÕÉ•%¹™¼¹%¹Ù…É¥…¹ÑÕ±ÑÕÉ”°½ÕÐ¥¹‘•à¤¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥¹‘•à€ô€´Äì(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ9…Ù¥…Ñ½ÉAÉ½Á•ÉÑä¡	É½ÝÍ•ÉMÕÉ™…•AÉ½™¥±”¹…Ù¥…Ñ½È°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰ÕÍ•É•¹Ðˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¹…Ù¥…Ñ½È¹UÍ•É•¹Ð€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á±…Ñ™½É´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¹…Ù¥…Ñ½È¹A±…Ñ™½ÉµQ½­•¸€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ù•¹‘½Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¹…Ù¥…Ñ½È¹Y•¹‘½È€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½½­¥•¹…‰±•ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡¹…Ù¥…Ñ½È¹½½­¥•¹…‰±•¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡…É‘Ý…É•½¹ÕÉÉ•¹äˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡5…Ñ ¹5…à Ä°¹Ù¥É½¹µ•¹Ð¹AÉ½•ÍÍ½É½Õ¹Ð¤¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰µ…áQ½Õ¡A½¥¹ÑÌˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‘•Ù¥•5•µ½Éäˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ9Õµ‰•È Ð¤ì€¼¼½µµ½¸‘•™…Õ±Ð(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±…¹Õ…”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¹…Ù¥…Ñ½È¹1…¹Õ…”€üüMåÍÑ•´¹±½‰…±¥é…Ñ¥½¸¹Õ±ÑÕÉ•%¹™¼¹ÕÉÉ•¹ÑÕ±ÑÕÉ”¹QÝ½1•ÑÑ•É%M=1…¹Õ…•9…µ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±…¹Õ…•Ìˆè(€€€€€€€€€€€€€€€€€€€€¼¼5ÕÍÐÉ•ÑÕÉ¸„™É½é•¸…ÉÉ…äÁ•ÈÍÁ•Œ¸	½Ð‘•Ñ•Ñ¥½¸¡•­ÌÉÉ…ä¹¥ÍÉÉ…ä¡¹…Ù¥…Ñ½È¹±…¹Õ…•Ì¤¸(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…È±…¹œ€ô¹…Ù¥…Ñ½È¹1…¹Õ…”€üü€‰•¸µULˆì(€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÍ¡½ÉÑ1…¹œ€ô±…¹œ¹½¹Ñ…¥¹Ì ˆ´ˆ¤€ü±…¹œ¹MÕ‰ÍÑÉ¥¹œ À°±…¹œ¹%¹‘•á=˜ œ´œ¤¤€è±…¹œì(€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹}¥¹Ñ•ÉÁÉ•Ñ•È¹±±½…Ñ•ÉÉ…ä¡¹•Ýmt(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡±…¹œ¤°(€€€€€€€€€€€€€€€€€€€€€€€€€€€)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡Í¡½ÉÑ1…¹œ¤(€€€€€€€€€€€€€€€€€€€€€€€ô¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½¹1¥¹”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ	½½±•…¸¡ÑÉÕ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…ÁÁ9…µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ ‰9•ÑÍ…Á”ˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…ÁÁY•ÉÍ¥½¸ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ ˆÔ¸Àˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ½‘ÕÐˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ ‰•­¼ˆ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•ÉÙ¥•]½É­•Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑå=ÉU¹‘•™¥¹•¡¹…Ù¥…Ñ½È°€‰Í•ÉÙ¥•]½É­•Èˆ¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡Ù…±Õ”¹Q…œ€ôô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ••™…Õ±ÑM•ÉÙ¥•]½É­•ÉMÑÕˆ ¤ì(€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹M•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑä¡¹…Ù¥…Ñ½È°€‰Í•ÉÙ¥•]½É­•Èˆ°Ù…±Õ”¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÑ½É…”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑå=ÉU¹‘•™¥¹•¡¹…Ù¥…Ñ½È°€‰ÍÑ½É…”ˆ¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡Ù…±Õ”¹Q…œ€ôô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹É•…Ñ••™…Õ±ÑMÑ½É…•MÑÕˆ ¤ì(€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹M•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑä¡¹…Ù¥…Ñ½È°€‰ÍÑ½É…”ˆ°Ù…±Õ”¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•ÑMÑ½É…•AÉ½Á•ÉÑä¡•¹MÑ½É…•É•…!½ÍÐÍÑ½É…•É•„°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰±•¹Ñ ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡ÍÑ½É…•É•„¹1•¹Ñ ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰­•äˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„°€‰­•äˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È¥¹‘•à€ô…ÉÌ¹½Õ¹Ð€ø€À€ü€¡¥¹Ð¥½•É•Q½¥¹¥Ñ•9Õµ‰•È¡…ÉÍlÁt°€À¤€è€Àì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÉ•ÍÕ±Ð€ôÍÑ½É…•É•„¹-•ä¡¥¹‘•à¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸É•ÍÕ±Ð€„ô¹Õ±°€ü)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡É•ÍÕ±Ð¤€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰•Ñ%Ñ•´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„°€‰•Ñ%Ñ•´ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È­•ä€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÉ•ÍÕ±Ð€ôÍÑ½É…•É•„¹•Ñ%Ñ•´¡­•ä¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸É•ÍÕ±Ð€„ô¹Õ±°€ü)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡É•ÍÕ±Ð¤€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•Ñ%Ñ•´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„°€‰Í•Ñ%Ñ•´ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È­•ä€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÙ…°€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÅt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„¹M•Ñ%Ñ•´¡­•ä°Ù…°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•µ½Ù•%Ñ•´ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„°€‰É•µ½Ù•%Ñ•´ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È­•ä€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„¹I•µ½Ù•%Ñ•´¡­•ä¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰±•…Èˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„°€‰±•…Èˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ÍÑ½É…•É•„¹±•…È ¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€€¼¼A•ÈÍÁ•Œ°ÍÑ½É…•m­•åt¥Ì•ÅÕ¥Ù…±•¹ÐÑ¼ÍÑ½É…”¹•Ñ%Ñ•´¡­•ä¤¸(€€€€€€€€€€€€€€€€€€€Ù…È¥Ñ•´€ôÍÑ½É…•É•„¹•Ñ%Ñ•´¡ÁÉ½Á•ÉÑä¤ì(€€€€€€€€€€€€€€€€€€€¥˜€¡¥Ñ•´€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡¥Ñ•´¤ì(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ!¥ÍÑ½ÉåAÉ½Á•ÉÑä¡•¹)Í!¥ÍÑ½Éå!½ÍÐ¡¥ÍÑ½Éä°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÁÉ½Á•ÉÑä°€‰ÁÕÍ¡MÑ…Ñ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÁÉ½Á•ÉÑä°€‰É•Á±…•MÑ…Ñ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•ÑMÑ½É•‘!½ÍÑAÉ½Á•ÉÑå=ÉU¹‘•™¥¹•¡¡¥ÍÑ½Éä°ÁÉ½Á•ÉÑä¤ì(€€€€€€€€€€€€€€€¥˜€¡Ù…±Õ”¹Q…œ€„ô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô((€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰±•¹Ñ ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”ü¹1•¹Ñ €üü¡¥ÍÑ½Éä¹1•¹Ñ ¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÍÑ…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°(€€€€€€€€€€€€€€€€€€€€€€€€ü½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤(€€€€€€€€€€€€€€€€€€€€€€€€è¡¥ÍÑ½Éä¹MÑ…Ñ”ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÕÍ¡MÑ…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä°(€€€€€€€€€€€€€€€€€€€€€€€€‰ÁÕÍ¡MÑ…Ñ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÍÑ…Ñ”€ô…ÉÌ¹½Õ¹Ð€ø€À€ü…ÉÍlÁt€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÑ¥Ñ±”€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÅt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÕÉ°€ô…ÉÌ¹½Õ¹Ð€ø€È€˜˜…ÉÍlÉt¹Q…œ€„ô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÉt¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€èÍÑÉ¥¹œ¹µÁÑäì((€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹AÕÍ¡MÑ…Ñ”¡ÍÑ…Ñ”°Ñ¥Ñ±”°ÕÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹1•¹Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ô½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹UÁ‘…Ñ••¹)Í1½…Ñ¥½¸¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹ÕÉÉ•¹ÑUÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€•±Í”(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ôÍÑ…Ñ”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô5…Ñ ¹5…à Ä°¡¥ÍÑ½Éä¹1•¹Ñ €¬€Ä¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹ÁÁ±å!¥ÍÑ½ÉåUÉ°¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°ÕÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•Á±…•MÑ…Ñ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä°(€€€€€€€€€€€€€€€€€€€€€€€€‰É•Á±…•MÑ…Ñ”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÍÑ…Ñ”€ô…ÉÌ¹½Õ¹Ð€ø€À€ü…ÉÍlÁt€è)ÍY…±Õ”¹9Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÑ¥Ñ±”€ô…ÉÌ¹½Õ¹Ð€ø€Ä€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÅt¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÕÉ°€ô…ÉÌ¹½Õ¹Ð€ø€È€˜˜…ÉÍlÉt¹Q…œ€„ô)ÍY…±Õ•Q…œ¹U¹‘•™¥¹•(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÉt¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€èÍÑÉ¥¹œ¹µÁÑäì((€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹I•Á±…•MÑ…Ñ”¡ÍÑ…Ñ”°Ñ¥Ñ±”°ÕÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹1•¹Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ô½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹UÁ‘…Ñ••¹)Í1½…Ñ¥½¸¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹ÕÉÉ•¹ÑUÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€•±Í”(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ôÍÑ…Ñ”ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹ÁÁ±å!¥ÍÑ½ÉåUÉ°¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°ÕÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€È¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¼ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä°(€€€€€€€€€€€€€€€€€€€€€€€€‰¼ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…È‘•±Ñ„€ô…ÉÌ¹½Õ¹Ð€ø€À€ü€¡¥¹Ð¥½•É•Q½¥¹¥Ñ•9Õµ‰•È¡…ÉÍlÁt°€À¤€è€Àì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”ü¹¼¡‘•±Ñ„¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹1•¹Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ô½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹UÁ‘…Ñ••¹)Í1½…Ñ¥½¸¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹ÕÉÉ•¹ÑUÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰‰…¬ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä°(€€€€€€€€€€€€€€€€€€€€€€€€‰‰…¬ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”ü¹¼ ´Ä¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹1•¹Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ô½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹UÁ‘…Ñ••¹)Í1½…Ñ¥½¸¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹ÕÉÉ•¹ÑUÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰™½ÉÝ…Éˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä°(€€€€€€€€€€€€€€€€€€€€€€€€‰™½ÉÝ…Éˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”ü¹¼ Ä¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”€„ô¹Õ±°¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹1•¹Ñ €ô}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹1•¹Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€¡¥ÍÑ½Éä¹MÑ…Ñ”€ô½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹MÑ…Ñ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹UÁ‘…Ñ••¹)Í1½…Ñ¥½¸¡¡¥ÍÑ½Éä¹1½…Ñ¥½¸°}½Ý¹•È¹}¡¥ÍÑ½Éå	É¥‘”¹ÕÉÉ•¹ÑUÉ°¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ)ÍY…±Õ”½•É•	É¥‘•!¥ÍÑ½ÉåMÑ…Ñ”¡½‰©•ÐÍÑ…Ñ”¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑ…Ñ”ÍÝ¥Ñ (€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¹Õ±°€ôø)ÍY…±Õ”¹9Õ±°°(€€€€€€€€€€€€€€€)ÍY…±Õ”©ÍY…±Õ”€ôø©ÍY…±Õ”°(€€€€€€€€€€€€€€€ÍÑÉ¥¹œÑ•áÐ€ôø)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡Ñ•áÐ¤°(€€€€€€€€€€€€€€€‰½½°‰½½±•…¸€ôø)ÍY…±Õ”¹É½µ	½½±•…¸¡‰½½±•…¸¤°(€€€€€€€€€€€€€€€¥¹Ð¥¹ÐÌÈ€ôø)ÍY…±Õ”¹É½µ%¹ÐÌÈ¡¥¹ÐÌÈ¤°(€€€€€€€€€€€€€€€‘½Õ‰±”¹Õµ‰•È€ôø)ÍY…±Õ”¹É½µ9Õµ‰•È¡¹Õµ‰•È¤°(€€€€€€€€€€€€€€€™±½…Ð¹Õµ‰•È€ôø)ÍY…±Õ”¹É½µ9Õµ‰•È¡¹Õµ‰•È¤°(€€€€€€€€€€€€€€€±½¹œ¥¹Ñ••È€ôø)ÍY…±Õ”¹É½µ9Õµ‰•È¡¥¹Ñ••È¤°(€€€€€€€€€€€€€€€|€ôø)ÍY…±Õ”¹9Õ±°(€€€€€€€€€€€ôì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”‰½½°QÉå•Ñ1½…Ñ¥½¹AÉ½Á•ÉÑä¡•¹)Í1½…Ñ¥½¹!½ÍÐ±½…Ñ¥½¸°ÍÑÉ¥¹œÁÉ½Á•ÉÑä°½ÕÐ)ÍY…±Õ”Ù…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÕÉ¤€ô±½…Ñ¥½¸¹UÉ¤ì(€€€€€€€€€€€Ù…È…‰Í½±ÕÑ”€ôÕÉ¤ü¹‰Í½±ÕÑ•UÉ¤€üüÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€ÍÝ¥Ñ €¡ÁÉ½Á•ÉÑä¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€…Í”€‰¡É•˜ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡…‰Í½±ÕÑ”¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰½É¥¥¸ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹•Ñ1•™ÑA…ÉÐ¡UÉ¥A…ÉÑ¥…°¹ÕÑ¡½É¥Ñä¤€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰ÁÉ½Ñ½½°ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹M¡•µ”¥ÌÍÑÉ¥¹œÍ¡•µ”€˜˜Í¡•µ”¹1•¹Ñ €ø€À€üÍ¡•µ”€¬€ˆèˆ€èÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡½ÍÐˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹%Í•™…Õ±ÑA½ÉÐ€ôô™…±Í”€ü€‰íÕÉ¤¹!½ÍÑôéíÕÉ¤¹A½ÉÑôˆ€èÕÉ¤ü¹!½ÍÐ€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡½ÍÑ¹…µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹!½ÍÐ€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Á…Ñ¡¹…µ”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹‰Í½±ÕÑ•A…Ñ €üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Í•…É ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹EÕ•Éä€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰¡…Í ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡ÕÉ¤ü¹É…µ•¹Ð€üüÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰Ñ½MÑÉ¥¹œˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€±½…Ñ¥½¸°(€€€€€€€€€€€€€€€€€€€€€€€€‰Ñ½MÑÉ¥¹œˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|¤€ôø)ÍY…±Õ”¹É½µMÑÉ¥¹œ¡±½…Ñ¥½¸¹UÉ¤ü¹‰Í½±ÕÑ•UÉ¤€üüÍÑÉ¥¹œ¹µÁÑä¤°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€À¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•±½…ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€±½…Ñ¥½¸°€‰É•±½…ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°|È¤€ôøì}½Ý¹•È¹9…Ù¥…Ñ•=Ý¹¥¹	É½ÝÍ¥¹½¹Ñ•áÐ¡ÕÉ¤¤ìÉ•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ìô°(€€€€€€€€€€€€€€€€€€€€€€€±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰É•Á±…”ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€±½…Ñ¥½¸°€‰É•Á±…”ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÕÉ°€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡ÕÉ°¤€˜˜UÉ¤¹QÉåÉ•…Ñ”¡ÕÉ¤°ÕÉ°°½ÕÐÙ…È¹…ÙUÉ¤¤¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹9…Ù¥…Ñ•=Ý¹¥¹	É½ÝÍ¥¹½¹Ñ•áÐ¡¹…ÙUÉ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€…Í”€‰…ÍÍ¥¸ˆè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô}½Ý¹•È¹•Ñ=ÉÉ•…Ñ•!½ÍÑ…±±…‰±” (€€€€€€€€€€€€€€€€€€€€€€€±½…Ñ¥½¸°€‰…ÍÍ¥¸ˆ°(€€€€€€€€€€€€€€€€€€€€€€€€¡|°…ÉÌ¤€ôø(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€Ù…ÈÕÉ°€ô…ÉÌ¹½Õ¹Ð€ø€À€ü½•É•Q½!½ÍÑMÑÉ¥¹œ¡…ÉÍlÁt¤€è¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡ÕÉ°¤€˜˜UÉ¤¹QÉåÉ•…Ñ”¡ÕÉ¤°ÕÉ°°½ÕÐÙ…È¹…ÙUÉ¤¤¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€}½Ý¹•È¹9…Ù¥…Ñ•=Ý¹¥¹	É½ÝÍ¥¹½¹Ñ•áÐ¡¹…ÙUÉ¤¤ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€€€€€ô°±•¹Ñ è€Ä¤ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸ÑÉÕ”ì(€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€Ù…±Õ”€ô)ÍY…±Õ”¹U¹‘•™¥¹•ì(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œI•Í½±Ù•±•µ•¹ÑUÉ±AÉ½Á•ÉÑä¡±•µ•¹Ð•±•µ•¹Ð°ÍÑÉ¥¹œ…ÑÑÉ¥‰ÕÑ•9…µ”¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÉ…Ü€ô•±•µ•¹Ðü¹•ÑÑÑÉ¥‰ÕÑ”¡…ÑÑÉ¥‰ÕÑ•9…µ”¤€üüÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡É…Ü¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡UÉ¤¹QÉåÉ•…Ñ”¡É…Ü°UÉ¥-¥¹¹‰Í½±ÕÑ”°½ÕÐÙ…È…‰Í½±ÕÑ”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸…‰Í½±ÕÑ”¹‰Í½±ÕÑ•UÉ¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È½Ý¹•É½Õµ•¹Ð€ô•±•µ•¹Ð¹=Ý¹•É½Õµ•¹Ðì(€€€€€€€€€€€Ù…È‰…Í•I…Ü€ô(€€€€€€€€€€€€€€€½Ý¹•É½Õµ•¹Ðü¹	…Í•UI$€üü(€€€€€€€€€€€€€€€½Ý¹•É½Õµ•¹Ðü¹½Õµ•¹ÑUI$€üü(€€€€€€€€€€€€€€€½Ý¹•É½Õµ•¹Ðü¹UI0ì((€€€€€€€€€€€¥˜€¡UÉ¤¹QÉåÉ•…Ñ”¡‰…Í•I…Ü°UÉ¥-¥¹¹‰Í½±ÕÑ”°½ÕÐÙ…È‰…Í•UÉ¤¤€˜˜(€€€€€€€€€€€€€€€UÉ¤¹QÉåÉ•…Ñ”¡‰…Í•UÉ¤°É…Ü°½ÕÐÙ…ÈÉ•Í½±Ù•¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸É•Í½±Ù•¹‰Í½±ÕÑ•UÉ¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€É•ÑÕÉ¸É…Üì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í%µ…•±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰¥µœˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰¥µ…”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í5•Ñ…±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤€ôø(€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰µ•Ñ„ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%ÍMÉ¥ÁÑ±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤€ôø(€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰ÍÉ¥ÁÐˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥M•Ñ	½½±•…¹ÑÑÉ¥‰ÕÑ”¡±•µ•¹Ð•±•µ•¹Ð°ÍÑÉ¥¹œ…ÑÑÉ¥‰ÕÑ•9…µ”°‰½½°•¹…‰±•¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡•¹…‰±•¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•±•µ•¹Ð¹M•ÑÑÑÉ¥‰ÕÑ”¡…ÑÑÉ¥‰ÕÑ•9…µ”°ÍÑÉ¥¹œ¹µÁÑä¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€•±Í”(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•±•µ•¹Ð¹I•µ½Ù•ÑÑÉ¥‰ÕÑ”¡…ÑÑÉ¥‰ÕÑ•9…µ”¤ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í¥…±½±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰‘¥…±½œˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%ÍQ•µÁ±…Ñ•±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰Ñ•µÁ±…Ñ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í%É…µ•±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰¥™É…µ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í¡•­…‰±•%¹ÁÕÑ±•µ•¹Ð¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ðü¹Q…9…µ”°€‰¥¹ÁÕÐˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…ÈÑåÁ”€ô•±•µ•¹Ð¹•ÑÑÑÉ¥‰ÕÑ” ‰ÑåÁ”ˆ¤ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÑåÁ”°€‰¡•­‰½àˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÑåÁ”°€‰É…‘¥¼ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œI•…‘±•µ•¹ÑY…±Õ”¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡•±•µ•¹Ð€ôô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È…ÑÑÉY…±Õ”€ô•±•µ•¹Ð¹•ÑÑÑÉ¥‰ÕÑ” ‰Ù…±Õ”ˆ¤ì(€€€€€€€€€€€¥˜€¡…ÑÑÉY…±Õ”€„ô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸…ÑÑÉY…±Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ð¹Q…9…µ”°€‰Ñ•áÑ…É•„ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ð¹Q…9…µ”°€‰½ÁÑ¥½¸ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸•±•µ•¹Ð¹Q•áÑ½¹Ñ•¹Ð€üüÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€ô((€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œI•…‘%¹ÁÕÑQåÁ”¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÑåÁ”€ô€¡•±•µ•¹Ðü¹•ÑÑÑÉ¥‰ÕÑ” ‰ÑåÁ”ˆ¤€üüÍÑÉ¥¹œ¹µÁÑä¤¹QÉ¥´ ¤¹Q½1½Ý•É%¹Ù…É¥…¹Ð ¤ì(€€€€€€€€€€€É•ÑÕÉ¸ÑåÁ”ÍÝ¥Ñ (€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€‰¡¥‘‘•¸ˆ½È€‰Ñ•áÐˆ½È€‰Í•…É ˆ½È€‰Ñ•°ˆ½È€‰ÕÉ°ˆ½È€‰•µ…¥°ˆ½È€‰Á…ÍÍÝ½Éˆ½È(€€€€€€€€€€€€€€€€‰‘…Ñ”ˆ½È€‰µ½¹Ñ ˆ½È€‰Ý••¬ˆ½È€‰Ñ¥µ”ˆ½È€‰‘…Ñ•Ñ¥µ”µ±½…°ˆ½È€‰¹Õµ‰•Èˆ½È(€€€€€€€€€€€€€€€€‰É…¹”ˆ½È€‰½±½Èˆ½È€‰¡•­‰½àˆ½È€‰É…‘¥¼ˆ½È€‰™¥±”ˆ½È€‰ÍÕ‰µ¥Ðˆ½È€‰¥µ…”ˆ½È(€€€€€€€€€€€€€€€€‰É•Í•Ðˆ½È€‰‰ÕÑÑ½¸ˆ€ôøÑåÁ”°(€€€€€€€€€€€€€€€|€ôø€‰Ñ•áÐˆ(€€€€€€€€€€€ôì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥M•Ñ±•µ•¹ÑY…±Õ”¡±•µ•¹Ð•±•µ•¹Ð°ÍÑÉ¥¹œÙ…±Õ”¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡•±•µ•¹Ð€ôô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…±Õ”€üüôÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€•±•µ•¹Ð¹M•ÑÑÑÉ¥‰ÕÑ” ‰Ù…±Õ”ˆ°Ù…±Õ”¤ì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•±•µ•¹Ð¹Q…9…µ”°€‰Ñ•áÑ…É•„ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•±•µ•¹Ð¹Q•áÑ½¹Ñ•¹Ð€ôÙ…±Õ”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ¥¹ÐI•…‘±•µ•¹ÑQ…‰%¹‘•à¡±•µ•¹Ð•±•µ•¹Ð¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÉ…Ü€ô•±•µ•¹Ðü¹•ÑÑÑÉ¥‰ÕÑ” ‰Ñ…‰¥¹‘•àˆ¤ì(€€€€€€€€€€€¥˜€¡¥¹Ð¹QÉåA…ÉÍ”¡É…Ü°9Õµ‰•ÉMÑå±•Ì¹%¹Ñ••È°Õ±ÑÕÉ•%¹™¼¹%¹Ù…É¥…¹ÑÕ±ÑÕÉ”°½ÕÐÙ…ÈÁ…ÉÍ•¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸Á…ÉÍ•ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡•±•µ•¹Ð€ôô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸€´Äì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…ÈÑ…9…µ”€ô•±•µ•¹Ð¹Q…9…µ”€üüÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰„ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸•±•µ•¹Ð¹!…ÍÑÑÉ¥‰ÕÑ” ‰¡É•˜ˆ¤€ü€À€è€´Äì(€€€€€€€€€€€ô((€€€€€€€€€€€É•ÑÕÉ¸(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰‰ÕÑÑ½¸ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰¥¹ÁÕÐˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰Í•±•Ðˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰Ñ•áÑ…É•„ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ñð(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡Ñ…9…µ”°€‰¥™É…µ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤(€€€€€€€€€€€€€€€€€€€€ü€À(€€€€€€€€€€€€€€€€€€€€è€´Äì(€€€€€€€ô(€€€ô)ô((¼¼¼€ñÍÕµµ…Éäø(¼¼¼•¹ÑÉ…±¥é•ÉÕ¹Ñ¥µ”Í•±•Ñ¥½¸™½È‰É½ÝÍ•ÈÍÉ¥ÁÐ•á•ÕÑ¥½¸¸(¼¼¼Q¡”‰É½ÝÍ•È¹½ÜÉÕ¹Ì½¸Ñ¡”•¹)Lµ‰…­•ÉÕ¹Ñ¥µ”‰ä‘•™…Õ±ÐìÑ¡”±•…ä(¼¼¼•¹¹¥¹”ÉÕ¹Ñ¥µ”É•µ…¥¹ÌÉ•…¡…‰±”½¹±ä…Ì…¸•áÁ±¥¥Ð•Í…Á”¡…Ñ Ù¥„(¼¼¼€ñÍÕµµ…Éäø(¼¼¼MÑ…Ñ¥Œ™…Ñ½Éä™½È‰É½ÝÍ•ÈÍÉ¥ÁÐ•¹¥¹”¥¹ÍÑ…¹•Ì¸(¼¼¼•¹)L¥ÌÑ¡”½¹±äÉÕ¹Ñ¥µ”ƒŠP¹¼±•…ä™…±±‰…¬¸(¼¼¼€ð½ÍÕµµ…Éäø)ÁÕ‰±¥ŒÍÑ…Ñ¥Œ±…ÍÌ	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹•IÕ¹Ñ¥µ”)ì(€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÕ¹Œñ%)Í!½ÍÐ°%	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹”ø}™…Ñ½Éä€ôÉ•…Ñ•½¹™¥ÕÉ•‘•™…Õ±Ðì((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒÕ¹Œñ%)Í!½ÍÐ°%	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹”ø…Ñ½Éä(€€€ì(€€€€€€€•Ð€ôø}™…Ñ½Éäì(€€€€€€€Í•Ð€ôø}™…Ñ½Éä€ôÙ…±Õ”€üüÉ•…Ñ•½¹™¥ÕÉ•‘•™…Õ±Ðì(€€€ô((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥Œ%	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹”É•…Ñ”¡%)Í!½ÍÐ¡½ÍÐ¤(€€€ì(€€€€€€€É•ÑÕÉ¸}™…Ñ½Éä¡¡½ÍÐ¤ì(€€€ô((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒÙ½¥I•Í•Ð ¤(€€€ì(€€€€€€€}™…Ñ½Éä€ôÉ•…Ñ•½¹™¥ÕÉ•‘•™…Õ±Ðì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ%	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹”É•…Ñ•½¹™¥ÕÉ•‘•™…Õ±Ð¡%)Í!½ÍÐ¡½ÍÐ¤(€€€ì(€€€€€€€É•ÑÕÉ¸¹•Ü•¹)Í	É½ÝÍ•ÉMÉ¥ÁÑ¹¥¹”¡¡½ÍÐ¤ì(€€€ô)ô((¼¼¼€ñÍÕµµ…Éäø(¼¼¼¥…¹½ÍÑ¥ŒAÉ½µ¥Í”É•©•Ñ¥½¸ÑÉ…­•ÈÑ¡…Ð‘•™•ÉÌÉ•Á½ÉÑ¥¹œÕ¹Ñ¥°Ñ¡”(¼¼¼µ¥É½Ñ…Í¬¡•­Á½¥¹ÐÍ¼Í…µ”µÑÕÉ¸¡…¹‘±•ÉÌ‘¼¹½ÐÁÉ½‘Õ”™…±Í”™…¥±ÕÉ•Ì¸(¼¼¼•±•…Ñ•ÌÑ¼Ñ¡”¥¹¹•ÈÑÉ…­•È™½È¹½Éµ…°½Á•É…Ñ¥½¸¸(¼¼¼€ð½ÍÕµµ…Éäø)¥¹Ñ•É¹…°Í•…±•±…ÍÌ•¹)Í¥…¹½ÍÑ¥AÉ½µ¥Í•I•©•Ñ¥½¹QÉ…­•È€è%!½ÍÑAÉ½µ¥Í•I•©•Ñ¥½¹QÉ…­•È)ì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±ä%!½ÍÑAÉ½µ¥Í•I•©•Ñ¥½¹QÉ…­•È}¥¹¹•Èì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±äÑ¥½¸ñ)ÍY…±Õ”°AÉ½µ¥Í•I•©•Ñ¥½¹=Á•É…Ñ¥½¸ø}±¥™•å±•=‰Í•ÉÙ•Èì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±äÑ¥½¸ñ)ÍY…±Õ”ø}Õ¹¡…¹‘±•‘=‰Í•ÉÙ•Èì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±ä½‰©•Ð}Íå¹Œ€ô¹•Ü ¤ì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±ä1¥ÍÐñ)ÍY…±Õ”ø}Á•¹‘¥¹œ€ô¹•Ü ¤ì(€€€ÁÉ¥Ù…Ñ”¥¹Ð}É•©•Ñ•‘½Õ¹Ðì(€€€ÁÉ¥Ù…Ñ”¥¹Ð}¡…¹‘±•‘½Õ¹Ðì((€€€ÁÕ‰±¥Œ•¹)Í¥…¹½ÍÑ¥AÉ½µ¥Í•I•©•Ñ¥½¹QÉ…­•È (€€€€€€€%!½ÍÑAÉ½µ¥Í•I•©•Ñ¥½¹QÉ…­•È¥¹¹•È°(€€€€€€€Ñ¥½¸ñ)ÍY…±Õ”°AÉ½µ¥Í•I•©•Ñ¥½¹=Á•É…Ñ¥½¸ø±¥™•å±•=‰Í•ÉÙ•È°(€€€€€€€Ñ¥½¸ñ)ÍY…±Õ”øÕ¹¡…¹‘±•‘=‰Í•ÉÙ•È¤(€€€ì(€€€€€€€}¥¹¹•È€ô¥¹¹•È€üüÑ¡É½Ü¹•ÜÉÕµ•¹Ñ9Õ±±á•ÁÑ¥½¸¡¹…µ•½˜¡¥¹¹•È¤¤ì(€€€€€€€}±¥™•å±•=‰Í•ÉÙ•È€ô±¥™•å±•=‰Í•ÉÙ•È€üüÑ¡É½Ü¹•ÜÉÕµ•¹Ñ9Õ±±á•ÁÑ¥½¸¡¹…µ•½˜¡±¥™•å±•=‰Í•ÉÙ•È¤¤ì(€€€€€€€}Õ¹¡…¹‘±•‘=‰Í•ÉÙ•È€ôÕ¹¡…¹‘±•‘=‰Í•ÉÙ•È€üüÑ¡É½Ü¹•ÜÉÕµ•¹Ñ9Õ±±á•ÁÑ¥½¸¡¹…µ•½˜¡Õ¹¡…¹‘±•‘=‰Í•ÉÙ•È¤¤ì(€€€ô((€€€ÁÕ‰±¥ŒÙ½¥QÉ…¬¡)ÍY…±Õ”ÁÉ½µ¥Í”°AÉ½µ¥Í•I•©•Ñ¥½¹=Á•É…Ñ¥½¸½Á•É…Ñ¥½¸¤(€€€ì(€€€€€€€}¥¹¹•È¹QÉ…¬¡ÁÉ½µ¥Í”°½Á•É…Ñ¥½¸¤ì(€€€€€€€Ù…È½‰Í•ÉÙ•€ô™…±Í”ì(€€€€€€€±½¬€¡}Íå¹Œ¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡½Á•É…Ñ¥½¸€ôôAÉ½µ¥Í•I•©•Ñ¥½¹=Á•É…Ñ¥½¸¹I•©•Ð¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥˜€¡}Á•¹‘¥¹œ¹½Õ¹Ð€ð€ÄÈàñð}Á•¹‘¥¹œ¹½¹Ñ…¥¹Ì¡ÁÉ½µ¥Í”¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€¥˜€ …}Á•¹‘¥¹œ¹½¹Ñ…¥¹Ì¡ÁÉ½µ¥Í”¤¤(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€}Á•¹‘¥¹œ¹‘¡ÁÉ½µ¥Í”¤ì(€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€½‰Í•ÉÙ•€ôÑÉÕ”ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô(€€€€€€€€€€€•±Í”¥˜€¡½Á•É…Ñ¥½¸€ôôAÉ½µ¥Í•I•©•Ñ¥½¹=Á•É…Ñ¥½¸¹!…¹‘±”¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}Á•¹‘¥¹œ¹I•µ½Ù”¡ÁÉ½µ¥Í”¤ì(€€€€€€€€€€€€€€€%¹Ñ•É±½­•¹%¹É•µ•¹Ð¡É•˜}¡…¹‘±•‘½Õ¹Ð¤ì(€€€€€€€€€€€€€€€½‰Í•ÉÙ•€ôÑÉÕ”ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€¥˜€¡½‰Í•ÉÙ•¤(€€€€€€€ì(€€€€€€€€€€€ÑÉä(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}±¥™•å±•=‰Í•ÉÙ•È¡ÁÉ½µ¥Í”°½Á•É…Ñ¥½¸¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€…Ñ (€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€¼¼¥…¹½ÍÑ¥ÌµÕÍÐ¹½Ð¡…¹”AÉ½µ¥Í”Í•ÑÑ±•µ•¹Ð½È¡…¹‘±•È‰•¡…Ù¥½È¸(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÕ‰±¥ŒÙ½¥±ÕÍ¡A•¹‘¥¹œ ¤(€€€ì(€€€€€€€)ÍY…±Õ•mtÁ•¹‘¥¹œì(€€€€€€€±½¬€¡}Íå¹Œ¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡}Á•¹‘¥¹œ¹½Õ¹Ð€ôô€À¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€€€€€ô((€€€€€€€€€€€Á•¹‘¥¹œ€ô}Á•¹‘¥¹œ¹Q½ÉÉ…ä ¤ì(€€€€€€€€€€€}Á•¹‘¥¹œ¹±•…È ¤ì(€€€€€€€ô((€€€€€€€™½É•… €¡Ù…ÈÁÉ½µ¥Í”¥¸Á•¹‘¥¹œ¤(€€€€€€€ì(€€€€€€€€€€€Ù…È½Õ¹Ð€ô%¹Ñ•É±½­•¹%¹É•µ•¹Ð¡É•˜}É•©•Ñ•‘½Õ¹Ð¤ì(€€€€€€€€€€€ÑÉä(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}Õ¹¡…¹‘±•‘=‰Í•ÉÙ•È¡ÁÉ½µ¥Í”¤ì(€€€€€€€€€€€ô(€€€€€€€€€€€…Ñ (€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€¼¼¥…¹½ÍÑ¥ÌµÕÍÐ¹½Ð¡…¹”AÉ½µ¥Í”Í•ÑÑ±•µ•¹Ð½È¡…¹‘±•È‰•¡…Ù¥½È¸(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡½Õ¹Ð€ðô€ÈÀ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•¹1½•È¹]…É¸ (€€€€€€€€€€€€€€€€€€€€‰mAÉ½µ¥Í•I•©•Ñ¥½¹tU¹¡…¹‘±•É•©•Ñ¥½¸€í½Õ¹Ñô‘•Ñ•Ñ•ˆ°(€€€€€€€€€€€€€€€€€€€1½…Ñ•½Éä¹)…Ù…MÉ¥ÁÐ¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÕ‰±¥ŒÙ½¥I•Í•Ð ¤(€€€ì(€€€€€€€±½¬€¡}Íå¹Œ¤(€€€€€€€ì(€€€€€€€€€€€}Á•¹‘¥¹œ¹±•…È ¤ì(€€€€€€€ô(€€€€€€€%¹Ñ•É±½­•¹á¡…¹”¡É•˜}É•©•Ñ•‘½Õ¹Ð°€À¤ì(€€€€€€€%¹Ñ•É±½­•¹á¡…¹”¡É•˜}¡…¹‘±•‘½Õ¹Ð°€À¤ì(€€€ô)ô(