using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
// using FenBrowser.Engine;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Globalization;
using Math = System.Math;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Security;
using Avalonia;
using Avalonia.VisualTree;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// JavaScriptEngine - powered by FenEngine
    /// Provides JavaScript execution with DOM/Web APIs support
    /// </summary>
    public sealed partial class JavaScriptEngine
    {
        public Func<Uri, Task<string>> FetchOverride { get; set; }

        // FenEngine runtime
        private FenRuntime _fenRuntime;

#if USE_ECMA_EXPERIMENTAL
        private JsInterpreter _exp;
        public bool UseExperimentalEcmaEngine { get; set; } = false;
#endif
        private readonly IJsHost _host;
        // private MiniJs.Engine _mini;      // MiniJS interpreter instance - DISABLED
        private JsContext _ctx;

        public JavaScriptEngine(IJsHost host)
        {
            _host = host;
            _ctx = new JsContext();
            var permissions = new PermissionManager(JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(permissions);

            // Configure function execution delegate
            context.ExecuteFunction = (fn, args) => 
            {
                var interpreter = new FenBrowser.FenEngine.Core.Interpreter();
                return interpreter.ApplyFunction(fn, new System.Collections.Generic.List<FenBrowser.FenEngine.Core.Interfaces.IValue>(args), context);
            };
            
            // Configure callbacks to run on UI thread
            context.ScheduleCallback = (action, delay) => 
            {
                FenLogger.Debug($"[ScheduleCallback] Scheduled for {delay}ms", LogCategory.JavaScript);
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(delay);
                        FenLogger.Debug("[ScheduleCallback] Woke up. Dispatching to UI thread...", LogCategory.JavaScript);
                        
                        // Ensure the callback runs on the UI thread as JS expects single-threaded access to DOM
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                        {
                            FenLogger.Debug("[ScheduleCallback] Executing action on UI thread.", LogCategory.JavaScript);
                            try { action(); }
                            catch (Exception ex) 
                            { 
                                FenLogger.Error($"[Timer Action Error] {ex.Message}", LogCategory.JavaScript, ex);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[ScheduleCallback Error] {ex.Message}\r\n"); } catch { }
                    }
                });
            };

            _fenRuntime = new FenRuntime(context);
            context.OnMutation = RecordMutation;
            
            if (_host != null)
            {
                _fenRuntime.SetAlert(msg => _host.Alert(msg));
            }
            
            SetupMutationObserver();
            // _mini = new MiniJs.Engine();
        }

        // timers
        private readonly Dictionary<int, System.Threading.Timer> _timers = new Dictionary<int, System.Threading.Timer>();
        private int _nextTimerId = 0;

        private void SetupMutationObserver()
        {
            var moConstructor = new FenFunction("MutationObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                    return new ErrorValue("MutationObserver constructor requires a callback function");

                var callback = args[0].AsFunction();
                var instance = new FenObject();
                
                // Store callback in the instance (hidden property)
                instance.Set("__callback", FenValue.FromFunction(callback));
                
                // observe(target, options)
                instance.Set("observe", FenValue.FromFunction(new FenFunction("observe", (obsArgs, obsThis) =>
                {
                    // Register this observer
                    lock (_mutationLock)
                    {
                        if (!_fenMutationObservers.Contains(callback))
                            _fenMutationObservers.Add(callback);
                    }
                    return FenValue.Undefined;
                })));
                
                // disconnect()
                instance.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (obsArgs, obsThis) =>
                {
                    lock (_mutationLock)
                    {
                        _fenMutationObservers.Remove(callback);
                    }
                    return FenValue.Undefined;
                })));
                
                // takeRecords()
                instance.Set("takeRecords", FenValue.FromFunction(new FenFunction("takeRecords", (obsArgs, obsThis) =>
                {
                    // Return empty array for now as we process mutations immediately
                    var arr = new FenObject();
                    arr.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(arr);
                })));

                return FenValue.FromObject(instance);
            });

            _fenRuntime.SetGlobal("MutationObserver", FenValue.FromFunction(moConstructor));
        }


        // XHR state
        // (XhrState is defined later)
        private readonly Dictionary<string, XhrState> _xhr = new Dictionary<string, XhrState>(StringComparer.Ordinal);
        private readonly object _xhrLock = new object();

        // flags
        private bool _allowExternalScripts;
        private bool _executeInlineScriptsOnInnerHTML;
        private SandboxPolicy _sandbox = SandboxPolicy.AllowAll;

        public SandboxPolicy Sandbox
        {
            get { return _sandbox; }
            set { _sandbox = value ?? SandboxPolicy.AllowAll; }
        }

        public bool AllowExternalScripts
        {
            get { return _allowExternalScripts && _sandbox.Allows(SandboxFeature.ExternalScripts); }
            set { _allowExternalScripts = value; }
        }

        public bool ExecuteInlineScriptsOnInnerHTML
        {
            get { return _executeInlineScriptsOnInnerHTML && _sandbox.Allows(SandboxFeature.InlineScripts); }
            set { _executeInlineScriptsOnInnerHTML = value; }
        }

        public bool UseMiniPrattEngine { get; set; } = true;
        
        public Action RequestRender
        {
            get => _fenRuntime?.RequestRender;
            set { if (_fenRuntime != null) _fenRuntime.RequestRender = value; }
        }
        
        // Document ready state (for document.readyState property)
        private string _readyState = "loading";
        public JsVal OnPopState { get; set; }
        public JsVal OnHashChange { get; set; }
        
        // Subresource validation delegate (optional)
        public Func<Uri, string, bool> SubresourceAllowed { get; set; }

        // Canvas persistence
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiteElement, Avalonia.Media.Imaging.WriteableBitmap> _canvasBitmaps 
            = new System.Runtime.CompilerServices.ConditionalWeakTable<LiteElement, Avalonia.Media.Imaging.WriteableBitmap>();

        public static void RegisterCanvasBitmap(LiteElement element, Avalonia.Media.Imaging.WriteableBitmap bitmap)
        {
            if (element == null || bitmap == null) return;
            // ConditionalWeakTable doesn't have indexer setter, use Remove/Add or GetValue to set
            _canvasBitmaps.Remove(element);
            _canvasBitmaps.Add(element, bitmap);
        }

        public static Avalonia.Media.Imaging.WriteableBitmap GetCanvasBitmap(LiteElement element)
        {
            if (element == null) return null;
            _canvasBitmaps.TryGetValue(element, out var bitmap);
            return bitmap;
        }
        public int CallStackDepth; // Recursion limit counter

        internal bool SandboxAllows(SandboxFeature feature, string detail = null)
        {
            if (_sandbox.Allows(feature)) return true;
            RecordSandboxBlock(feature, detail);
            return false;
        }

        private void RecordSandboxBlock(SandboxFeature feature, string detail)
        {
            var messageDetail = detail ?? string.Empty;
            try { TraceFeatureGap("Sandbox", feature.ToString(), messageDetail); } catch { }
            try
            {
                var status = string.IsNullOrWhiteSpace(messageDetail)
                    ? "[Sandbox] Blocked " + feature
                    : "[Sandbox] Blocked " + feature + " : " + messageDetail;
                _host?.SetStatus(status);
            }
            catch { }

            lock (_sandboxLogLock)
            {
                if (_sandboxBlocks.Count >= SandboxBlockCapacity) _sandboxBlocks.Dequeue();
                _sandboxBlocks.Enqueue(new SandboxBlockRecord
                {
                    Feature = feature,
                    Detail = messageDetail,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        public SandboxBlockRecord[] GetSandboxBlocksSnapshot()
        {
            lock (_sandboxLogLock) { return _sandboxBlocks.ToArray(); }
        }

        public void ClearSandboxBlockLog()
        {
            lock (_sandboxLogLock) { _sandboxBlocks.Clear(); }
        }

        // in JavaScriptEngine fields
        private readonly HashSet<string> _script404 = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _script404Lock = new object();

        // element-level listeners: id -> (event -> [fnName])
        private readonly Dictionary<string, Dictionary<string, List<string>>> _evtEl =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        // Optional bridge supplied by host to provide a CookieContainer for managed HttpClient fallbacks.
        public Func<Uri, System.Net.CookieContainer> CookieBridge { get; set; }
        
        // --- lightweight fields that may be missing in some merge states ---
        // DOM visual registry
        private static readonly System.Collections.Generic.Dictionary<LiteElement, System.WeakReference> _visualMap =
            new System.Collections.Generic.Dictionary<LiteElement, System.WeakReference>(System.Collections.Generic.EqualityComparer<LiteElement>.Default);
        private static System.WeakReference _visualRoot;

        // DOM root exposed to the engine
        private LiteElement _domRoot;

        private readonly object _sandboxLogLock = new object();
        private readonly Queue<SandboxBlockRecord> _sandboxBlocks = new Queue<SandboxBlockRecord>();
        private const int SandboxBlockCapacity = 32;

        public struct SandboxBlockRecord
        {
            public SandboxFeature Feature;
            public string Detail;
            public DateTime Timestamp;
        }

    // Microtask queue
    private readonly System.Collections.Generic.Queue<System.Action> _microtasks = new System.Collections.Generic.Queue<System.Action>();
    private readonly object _microtaskLock = new object();
    private bool _microtaskPumpScheduled = false;

    // Macro-task queue (setTimeout, setInterval, etc.)
    private readonly System.Collections.Generic.Queue<System.Action> _macroTasks = new System.Collections.Generic.Queue<System.Action>();
    private readonly object _macroTaskLock = new object();
    private bool _macroPumpScheduled = false;

    // Feature gap tracing throttling
    private readonly object _featureTraceLock = new object();
    private string _lastFeatureTraceKey;
    private System.DateTime _lastFeatureTraceTime = System.DateTime.MinValue;

        // Response registry (tokenized large response bodies)
        private readonly System.Collections.Generic.Dictionary<string, JavaScriptEngine.ResponseEntry> _responseRegistry =
            new System.Collections.Generic.Dictionary<string, JavaScriptEngine.ResponseEntry>(System.StringComparer.Ordinal);
        private readonly System.Collections.Generic.LinkedList<string> _responseLru = new System.Collections.Generic.LinkedList<string>();
        private readonly object _responseLock = new object();
        private System.TimeSpan _responseTtl = System.TimeSpan.FromMinutes(5);
        private int _responseCapacity = 64;
        private volatile bool _responseCleanupRunning = false;

        // Inline thresholds / repaint flags
        private int _inlineThreshold = 1024;
        private volatile bool _repaintRequested = false;

        // --- Mobile-oriented JS limits ---
        // Soft cap on total bytes of script source executed per page. This is
        // intended to keep mobile-class devices responsive by skipping
        // extremely large desktop-style bundles while still running typical
        // mobile-sized scripts.
        private int _pageScriptByteBudget = 10 * 1024 * 1024; // 10 MB default for modern sites
        private int _pageScriptBytesUsed = 0;

        // Small allowance for very tiny inline handlers (e.g., "return false")
        // that should work even when the main script budget is exhausted.
        private const int TinyInlineFreeThreshold = 256;

        // Optional external script fetcher (e.g., wired to ResourceManager.FetchTextAsync)
        // Signature: (uri, referer) => script text or null.
        public Func<Uri, Uri, Task<string>> ExternalScriptFetcher { get; set; }

        // Small in-memory LRU cache for script text, keyed by absolute URL.
        private sealed class ScriptCacheEntry { public string Body; }
        private readonly Dictionary<string, LinkedListNode<Tuple<string, ScriptCacheEntry>>> _scriptMap =
            new Dictionary<string, LinkedListNode<Tuple<string, ScriptCacheEntry>>>(StringComparer.Ordinal);
        private readonly LinkedList<Tuple<string, ScriptCacheEntry>> _scriptLru =
            new LinkedList<Tuple<string, ScriptCacheEntry>>();
        private readonly int _scriptCap = 64;

        /// <summary>
        /// Gets or sets the approximate per-page script byte budget. When the
        /// total executed script source exceeds this value, subsequent
        /// external/inline scripts are skipped for performance. Set to 0 or a
        /// negative value to disable the budget.
        /// </summary>
        public int PageScriptByteBudget
        {
            get { return _pageScriptByteBudget; }
            set { _pageScriptByteBudget = value; }
        }

        // ECMAScript modules
        private readonly ModuleLoader _moduleLoader;

        // Mutation observers / pending mutations
        private readonly System.Collections.Generic.List<string> _mutationObservers = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<FenFunction> _fenMutationObservers = new System.Collections.Generic.List<FenFunction>();
        private readonly object _mutationLock = new object();
        private readonly System.Collections.Generic.List<MutationRecord> _pendingMutations = new System.Collections.Generic.List<MutationRecord>();
        private string _docTitle = string.Empty;
        
        // --- DOM visual registry for approximate layout metrics ---
        
        public static void RegisterDomVisual(LiteElement node, Avalonia.Controls.Control fe)
        {
            try { if (node == null || fe == null) return; lock (_visualMap) _visualMap[node] = new System.WeakReference(fe); }
            catch { }
        }

        public static Avalonia.Controls.Control GetControlForElement(LiteElement node)
        {
            try
            {
                System.WeakReference wr;
                lock (_visualMap)
                {
                    if (!_visualMap.TryGetValue(node, out wr)) return null;
                    return wr != null ? wr.Target as Avalonia.Controls.Control : null;
                }
            }
            catch { return null; }
        }

        public static bool TryGetVisualRect(LiteElement node, out double x, out double y, out double w, out double h)
        {
            x = y = 0; w = h = 0;
            try
            {
                System.WeakReference wr; Avalonia.Controls.Control fe = null;
                lock (_visualMap)
                {
                    if (!_visualMap.TryGetValue(node, out wr)) return false;
                    fe = wr != null ? wr.Target as Avalonia.Controls.Control : null;
                }
                if (fe == null) return false;
                w = fe.Bounds.Width; h = fe.Bounds.Height;
                Avalonia.Visual root = null;
                try { var wrs = _visualRoot; if (wrs != null) root = wrs.Target as Avalonia.Visual; } catch { }
                if (root == null)
                    root = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                
                if (root != null && fe.IsVisible)
                {
                    var p = fe.TranslatePoint(new Avalonia.Point(0, 0), root);
                    if (p.HasValue)
                    {
                        x = p.Value.X; y = p.Value.Y; return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static Avalonia.Controls.Control GetVisual(LiteElement node)
        {
            try
            {
                System.WeakReference wr;
                lock (_visualMap)
                {
                    if (_visualMap.TryGetValue(node, out wr))
                        return wr?.Target as Avalonia.Controls.Control;
                }
            }
            catch { }
            return null;
        }
        public static void RegisterVisualRoot(Avalonia.Visual root)
        {
            try { _visualRoot = (root != null ? new System.WeakReference(root) : null); } catch { }
        }
        
        // ---- Phase 1/2/3 state ----
        private readonly Dictionary<string, List<string>> _evtDoc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _evtWin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Mini interpreter event listeners - DISABLED
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> _miniEvtDoc = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase);
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> _miniEvtWin = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase);
        // private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>> _miniEvtEl = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>>(System.StringComparer.Ordinal);

    // Track stopPropagation requests inside a single handler execution (JS-0 allowlist)
    private volatile bool _stopPropagationRequested;
    // Track preventDefault requests surfaced via runtime event wrappers
    private volatile bool _preventDefaultRequested;

        private void RegisterListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName)) return;
            List<string> list;
            if (!bag.TryGetValue(evt, out list) || list == null) { list = new List<string>(); bag[evt] = list; }
            if (!list.Contains(fnName)) list.Add(fnName);
        }

        private void RemoveListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            List<string> list; if (!bag.TryGetValue(evt, out list) || list == null) return;
            list.Remove(fnName);
        }

        private void FireDocumentEvent(string evt)
        {
            try
            {
                List<string> list; if (_evtDoc.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        EnqueueMicrotask(() => { try { RunInline(fn + "({ type:'" + evt + "', target:'document' })", _ctx, evt, "document"); } catch { } });
                    }
                }
            }
            catch { }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> mlist; if (_miniEvtDoc.TryGetValue(evt, out mlist) && mlist != null)
                {
                    foreach (var fn in mlist.ToArray())
                    {
                        try { var args = new System.Collections.Generic.List<MiniJs.JsValue>(); var ev = MiniEvent("document", evt); args.Add(ev); _mini?.Invoke(fn, args); } catch { }
                    }
                }
            }
            catch { }
            */
        }

        private void FireWindowEvent(string evt)
        {
            try
            {
                List<string> list; if (_evtWin.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        EnqueueMicrotask(() => { try { RunInline(fn + "({ type:'" + evt + "', target:'window' })", _ctx, evt, "window"); } catch { } });
                    }
                }
            }
            catch { }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> mlist; if (_miniEvtWin.TryGetValue(evt, out mlist) && mlist != null)
                {
                    foreach (var fn in mlist.ToArray())
                    {
                        try { var args = new System.Collections.Generic.List<MiniJs.JsValue>(); var ev = MiniEvent("window", evt); args.Add(ev); _mini?.Invoke(fn, args); } catch { }
                    }
                }
            }
            catch { }
            */
        }

        // intervals & rAF
        private readonly Dictionary<int, Timer> _intervals = new Dictionary<int, Timer>();
        private int _nextIntervalId;
        private readonly Dictionary<int, Timer> _rafs = new Dictionary<int, Timer>();
        private int _nextRafId;

        // storage (origin-scoped, in-memory)
        private readonly Dictionary<string, Dictionary<string, string>> _localStorageMap =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _sessionStorageMap =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _storageLock = new object();
        private const string LocalStorageFile = "lb_localstorage.txt"; // tab-separated: origin\tkey\tvalue per line
        private int _cbCounter;

        // loader/event firing
        private int _pendingAsyncScripts = 0;
        private volatile bool _domContentLoadedFired = false;
        private volatile bool _windowLoadFired = false;

        // lightweight navigation history
        private readonly List<Uri> _history = new List<Uri>();
        private int _historyIndex = -1;

        // Enqueue a microtask to run after current synchronous work
        private void EnqueueMicrotask(Action a)
        {
            EnqueueMicrotaskInternal(a);
        }
        private void RegisterElementListener(string id, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName)) return;
            Dictionary<string, List<string>> byEvt;
            if (!_evtEl.TryGetValue(id, out byEvt))
            {
                byEvt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _evtEl[id] = byEvt;
            }
            List<string> list;
            if (!byEvt.TryGetValue(evt, out list)) { list = new List<string>(); byEvt[evt] = list; }
            if (!list.Contains(fnName)) list.Add(fnName);
        }

        private void RemoveElementListener(string id, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName))
                return;

            try
            {
                Dictionary<string, List<string>> byEvt;
                if (!_evtEl.TryGetValue(id, out byEvt) || byEvt == null)
                    return;

                List<string> list;
                if (!byEvt.TryGetValue(evt, out list) || list == null)
                    return;

                list.Remove(fnName);

                // cleanup empty collections to keep the structure tidy
                if (list.Count == 0)
                    byEvt.Remove(evt);

                if (byEvt.Count == 0)
                    _evtEl.Remove(id);
            }
            catch
            {
                // swallow errors to match existing style
            }
        }


        // MiniJs listener methods - DISABLED
        /*
        public void AddMiniDocumentListener(string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(evt) || fn == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtDoc.TryGetValue(evt, out list) || list == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); _miniEvtDoc[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniDocumentListener(string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtDoc.TryGetValue(evt, out list) || list == null) return; list.Remove(fn);
        }
        public void AddMiniWindowListener(string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(evt) || fn == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtWin.TryGetValue(evt, out list) || list == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); _miniEvtWin[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniWindowListener(string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!_miniEvtWin.TryGetValue(evt, out list) || list == null) return; list.Remove(fn);
        }
        public void AddMiniElementListener(string id, string evt, MiniJs.JsFunction fn)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(evt) || fn == null) return;
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> byEvt; if (!_miniEvtEl.TryGetValue(id, out byEvt) || byEvt == null) { byEvt = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>>(System.StringComparer.OrdinalIgnoreCase); _miniEvtEl[id] = byEvt; }
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!byEvt.TryGetValue(evt, out list) || list == null) { list = new System.Collections.Generic.List<MiniJs.JsFunction>(); byEvt[evt] = list; }
            if (!list.Contains(fn)) list.Add(fn);
        }
        public void RemoveMiniElementListener(string id, string evt, MiniJs.JsFunction fn)
        {
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MiniJs.JsFunction>> byEvt; if (!_miniEvtEl.TryGetValue(id, out byEvt) || byEvt == null) return;
            System.Collections.Generic.List<MiniJs.JsFunction> list; if (!byEvt.TryGetValue(evt, out list) || list == null) return; list.Remove(fn);
        }
        */

        /// <summary>
        /// Raise an event on an element (asynchronous, DOM-triggered).
        /// Supports optional value and checked state for form controls.
        /// </summary>
        public void RaiseElementEvent(string id, string evt, string value = null, bool? isChecked = null)
        {
            if (string.IsNullOrWhiteSpace(evt)) return; // Allow empty ID for bubbling
            try
            {
                // For JS-0 style inline handlers (avoid C# 7 'out var' for WP8.1 toolchain)
                List<string> list = null; Dictionary<string, List<string>> byEvt = null;
                if (!string.IsNullOrWhiteSpace(id) && _evtEl.TryGetValue(id, out byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        try
                        {
                            RunInline(fn + "({ type:'" + evt + "', target:'" + id + "' })", _ctx, evt, id);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            
            // Dispatch to FenRuntime (simulating bubbling to window)
            FenLogger.Debug($"[RaiseElementEvent] Check _fenRuntime: {(_fenRuntime == null ? "NULL" : "OK")}", LogCategory.Events);
            if (_fenRuntime != null)
            {
                try
                {
                    var eventObj = new FenBrowser.FenEngine.Core.FenObject();
                    eventObj.Set("type", FenValue.FromString(evt));
                    eventObj.Set("timeStamp", FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
                    
                    var targetObj = new FenBrowser.FenEngine.Core.FenObject();
                    targetObj.Set("id", FenValue.FromString(id ?? ""));
                    if (value != null) targetObj.Set("value", FenValue.FromString(value));
                    if (isChecked.HasValue) targetObj.Set("checked", FenValue.FromBoolean(isChecked.Value));
                    
                    eventObj.Set("target", FenValue.FromObject(targetObj));
                    
                    _fenRuntime.DispatchEvent(evt, eventObj);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Event Propagation Error] {ex.Message}\r\n"); } catch { }
                }
            }

            // MiniJs event support - DISABLED
            /*
            try
            {
                System.Collections.Generic.List<MiniJs.JsFunction> list; if (_miniEvtEl.TryGetValue(id, out var byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        try
                        {
                            var args = new System.Collections.Generic.List<MiniJs.JsValue>();
                            var ev = MiniEvent(id, evt);
                            if (evt == "change" || evt == "input")
                            {
                                if (!string.IsNullOrEmpty(value)) ev.Obj["value"] = MiniJs.JsValue.From(value);
                                if (isChecked.HasValue) ev.Obj["checked"] = MiniJs.JsValue.From(isChecked.Value);
                            }
                            args.Add(ev);
                            _mini?.Invoke(fn, args);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            */
        }

        /// <summary>
        /// Raise an event on an element synchronously (returns bool for preventDefault check).
        /// Supports additional optional parameters for position/properties.
        /// </summary>
        public bool RaiseElementEventSync(string id, string evt, string value = null, bool? isChecked = null, double? posX = null, double? posY = null)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(evt)) return false;
            _stopPropagationRequested = false;
            try
            {
                // JS-0 style handlers (avoid C# 7 'out var')
                List<string> list = null; Dictionary<string, List<string>> byEvt = null;
                if (_evtEl.TryGetValue(id, out byEvt) && byEvt != null && byEvt.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fnName in list.ToArray())
                    {
                        try
                        {
                            RunInline(fnName + "({ type:'" + evt + "', target:'" + id + "' })", _ctx, evt, id);
                            if (_stopPropagationRequested) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return _stopPropagationRequested;
        }

        /// <summary>
        /// Raise an event by element ID (alternative entry point).
        /// </summary>
        public void RaiseElementEventById(string id, string evt)
        {
            RaiseElementEvent(id, evt);
        }

        // MiniJs.JsValue MiniEvent method - DISABLED
        /*
        private MiniJs.JsValue MiniEvent(string target, string type)
        {
            var e = MiniJs.JsValue.ObjLit();
            e.Obj["target"] = MiniJs.JsValue.From(target ?? "");
            e.Obj["type"] = MiniJs.JsValue.From(type ?? "");
            bool canceled = false; bool stopped = false;
            e.Obj["preventDefault"] = MiniJs.JsValue.Func(new MiniJs.JsFunction { Native = _ => { canceled = true; return MiniJs.JsValue.Undefined(); } });
            e.Obj["stopPropagation"] = MiniJs.JsValue.Func(new MiniJs.JsFunction { Native = _ => { stopped = true; return MiniJs.JsValue.Undefined(); } });
            return e;
        }
        */


        // ---------------- Intervals ----------------
        private int ScheduleInterval(string codeOrFn, int ms, bool isFnName)
        {
            if (ms < 0) ms = 0;
            var id = Interlocked.Increment(ref _nextIntervalId);
            Timer t = null;
            var repaintHost = _host as IJsHostRepaint;
            TimerCallback tick = _ =>
            {
                try
                {
                    Action run = () => {
                        try
                        {
                            if (isFnName) RunInline(codeOrFn + "()", _ctx);
                            else RunInline(codeOrFn, _ctx);
                        }
                        catch { }
                    };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch { }
            };
            t = new Timer(tick, null, ms, ms <= 0 ? 1 : ms);
            lock (_intervals) _intervals[id] = t;
            return id;
        }

        private void ClearInterval(int id)
        {
            lock (_intervals)
            {
                Timer t; if (_intervals.TryGetValue(id, out t))
                {
                    try { t.Dispose(); } catch { }
                    _intervals.Remove(id);
                }
            }
        }

        // ---------------- rAF (approx 60 FPS using timeout ~16ms) ----------------
        private int RequestAnimationFrame(string fnName)
        {
            var id = Interlocked.Increment(ref _nextRafId);
            var repaintHost = _host as IJsHostRepaint;
            Timer t = new Timer(_ =>
            {
                try
                {
                    Action run = () => { try { RunInline(fnName + "(Date.now&&Date.now()||0)", _ctx); } catch { } };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch { }
                finally { CancelAnimationFrame(id); }
            }, null, 16, Timeout.Infinite);
            lock (_rafs) _rafs[id] = t;
            return id;
        }

        private void CancelAnimationFrame(int id)
        {
            lock (_rafs)
            {
                Timer t; if (_rafs.TryGetValue(id, out t))
                {
                    try { t.Dispose(); } catch { }
                    _rafs.Remove(id);
                }
            }
        }

        // ---------------- Storage ----------------
        private static string OriginKey(Uri u)
        {
            if (u == null) return "null://";
            var port = u.IsDefaultPort ? "" : (":" + u.Port);
            return (u.Scheme ?? "http") + "://" + (u.Host ?? "localhost") + port;
        }

        private Dictionary<string, string> GetLocalStorageFor(Uri baseUri)
        {
            var key = OriginKey(baseUri);
            lock (_storageLock)
            {
                Dictionary<string, string> bag;
                if (!_localStorageMap.TryGetValue(key, out bag))
                {
                    bag = new Dictionary<string, string>(StringComparer.Ordinal);
                    _localStorageMap[key] = bag;
                }
                return bag;
            }
        }

        private Dictionary<string, string> GetSessionStorageFor(Uri baseUri)
        {
            var key = OriginKey(baseUri);
            lock (_storageLock)
            {
                Dictionary<string, string> bag;
                if (!_sessionStorageMap.TryGetValue(key, out bag))
                {
                    bag = new Dictionary<string, string>(StringComparer.Ordinal);
                    _sessionStorageMap[key] = bag;
                }
                return bag;
            }
        }

        // Persist localStorage to disk (best-effort)
        private async Task SaveLocalStorageAsync()
        {
            /*
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(LocalStorageFile, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var sb = new StringBuilder();
                lock (_storageLock)
                {
                    foreach (var origin in _localStorageMap)
                    {
                        if (origin.Value == null) continue;
                        foreach (var kv in origin.Value)
                        {
                            var line = (origin.Key ?? "") + "\t" + (kv.Key ?? "") + "\t" + (kv.Value ?? "");
                            sb.AppendLine(line);
                        }
                    }
                }
                await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch { }
            */
        }

        private async Task RestoreLocalStorageAsync()
        {
            /*
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                // Avoid first-chance FileNotFound by probing existence first
                var item = await folder.GetItemAsync(LocalStorageFile) as Windows.Storage.StorageFile;
                if (item == null) return;
                var text = await Windows.Storage.FileIO.ReadTextAsync(item);
                if (string.IsNullOrWhiteSpace(text)) return;
                lock (_storageLock)
                {
                    foreach (var ln in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var parts = ln.Split('\t');
                            if (parts.Length < 3) continue;
                            Dictionary<string, string> bag;
                            if (!_localStorageMap.TryGetValue(parts[0], out bag) || bag == null)
                            {
                                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                                _localStorageMap[parts[0]] = bag;
                            }
                            bag[parts[1]] = parts[2];
                        }
                        catch { }
                    }
                }
            }
            catch { }
            */
        }

        // ---------------- Cookies (best-effort via CookieBridge) ----------------
        private void SetCookieString(Uri scope, string cookieString)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie set")) return;
            try
            {
                if (CookieBridge == null || scope == null || string.IsNullOrWhiteSpace(cookieString)) return;
                var jar = CookieBridge(scope);
                if (jar == null) return;
                jar.SetCookies(scope, cookieString);
            }
            catch { }
        }

        private string GetCookieString(Uri scope)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie get")) return string.Empty;
            try
            {
                if (CookieBridge == null || scope == null) return "";
                var jar = CookieBridge(scope);
                if (jar == null) return "";
                var coll = jar.GetCookies(scope);
                if (coll == null || coll.Count == 0) return "";

                var sb = new StringBuilder();
                bool first = true;

                foreach (System.Net.Cookie cookie in coll)
                {
                    if (!first) sb.Append("; ");
                    first = false;

                    sb.Append(cookie.Name)
                      .Append('=')
                      .Append(cookie.Value ?? string.Empty);
                }

                return sb.ToString();

            }
            catch { return ""; }
        }

        // ---------------- History ----------------
        private static string BaseWithoutFragment(Uri u)
        {
            try
            {
                if (u == null) return null;
                return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
            }
            catch { return u != null ? ((u.Scheme ?? "") + "://" + (u.Host ?? "") + (u.PathAndQuery ?? "")) : null; }
        }

        private void HistoryPush(Uri u)
        {
            if (u == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.pushState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            _history.Add(u);
            _historyIndex = _history.Count - 1;
            try { if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal)) FireWindowEvent("hashchange"); } catch { }
        }

        private void HistoryReplace(Uri u)
        {
            if (u == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.replaceState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex < 0) { _history.Add(u); _historyIndex = _history.Count - 1; }
            else _history[_historyIndex] = u;
            try { if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal)) FireWindowEvent("hashchange"); } catch { }
        }

        private void HistoryGo(int delta)
        {
            var target = _historyIndex + delta;
            if (target < 0 || target >= _history.Count) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.go(" + delta + ")")) return;
            _historyIndex = target;
            try { _host.Navigate(_history[_historyIndex]); } catch { }
            FireWindowEvent("popstate");
        }
        // Handles Phase 1/2/3 builtins; returns true if the line was handled.
        private bool HandlePhase123Builtins(string line, JsContext ctx)
        {
            // ---------- addEventListener / removeEventListener ----------
            var mAddEvt = Regex.Match(line, @"^(?<tgt>document|window)\s*\.addEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mAddEvt.Success)
            {
                var tgt = mAddEvt.Groups["tgt"].Value.ToLowerInvariant();
                var evt = mAddEvt.Groups["evt"].Value;
                var fn = mAddEvt.Groups["fn"].Value;
                if (tgt == "document") RegisterListener(_evtDoc, evt, fn); else RegisterListener(_evtWin, evt, fn);
                return true;
            }
            var mRemEvt = Regex.Match(line, @"^(?<tgt>document|window)\s*\.removeEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRemEvt.Success)
            {
                var tgt = mRemEvt.Groups["tgt"].Value.ToLowerInvariant();
                var evt = mRemEvt.Groups["evt"].Value;
                var fn = mRemEvt.Groups["fn"].Value;
                if (tgt == "document") RemoveListener(_evtDoc, evt, fn); else RemoveListener(_evtWin, evt, fn);
                return true;
            }

            // ---------- setInterval / clearInterval ----------
            var mSI = Regex.Match(line, @"^\s*setInterval\s*\(\s*(?:['""](?<code>.*?)['""]|(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*))\s*,\s*(?<ms>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSI.Success)
            {
                var ms = 0; int.TryParse(mSI.Groups["ms"].Value, out ms);
                var code = mSI.Groups["code"].Success ? mSI.Groups["code"].Value : null;
                var fn = mSI.Groups["fn"].Success ? mSI.Groups["fn"].Value : null;
                var id = ScheduleInterval(code ?? fn, ms, isFnName: fn != null);
                try { _host.SetStatus("setInterval id=" + id); } catch { }
                return true;
            }
            var mCI = Regex.Match(line, @"^\s*clearInterval\s*\(\s*(?<id>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCI.Success) { int id; if (int.TryParse(mCI.Groups["id"].Value, out id)) ClearInterval(id); return true; }

            // ---------- requestAnimationFrame / cancelAnimationFrame ----------
            var mRaf = Regex.Match(line, @"^\s*requestAnimationFrame\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRaf.Success) { var id = RequestAnimationFrame(mRaf.Groups["fn"].Value); try { _host.SetStatus("rAF id=" + id); } catch { } return true; }
            var mCRaf = Regex.Match(line, @"^\s*cancelAnimationFrame\s*\(\s*(?<id>\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCRaf.Success) { int id; if (int.TryParse(mCRaf.Groups["id"].Value, out id)) CancelAnimationFrame(id); return true; }

            // ---------- localStorage ----------
            var mLSset = Regex.Match(line, @"^\s*localStorage\s*\.setItem\s*\(\s*['""](?<k>.+?)['""]\s*,\s*['""](?<v>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSset.Success)
            {
                var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bag[mLSset.Groups["k"].Value] = mLSset.Groups["v"].Value;
                return true;
            }
            var mLSgetCb = Regex.Match(line, @"^\s*localStorage\s*\.getItem\s*\(\s*['""](?<k>.+?)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSgetCb.Success)
            {
                var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                string val = null; lock (_storageLock) bag.TryGetValue(mLSgetCb.Groups["k"].Value, out val);
                var esc = JsEscape(val ?? "", '\''); EnqueueMicrotask(() => { try { RunInline(mLSgetCb.Groups["fn"].Value + "('" + esc + "')", _ctx); } catch { } });
                return true;
            }
            var mLSrem = Regex.Match(line, @"^\s*localStorage\s*\.removeItem\s*\(\s*['""](?<k>.+?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mLSrem.Success)
            {
                var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bag.Remove(mLSrem.Groups["k"].Value);
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*localStorage\s*\.clear\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase))
            {
                var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bag.Clear();
                try { var _ = SaveLocalStorageAsync(); } catch { }
                return true;
            }

            // ---------- sessionStorage (in-memory only) ----------
            var mSSset = Regex.Match(line, @"^\s*sessionStorage\s*\.setItem\s*\(\s*['\""](?<k>.+?)['\""]\s*,\s*['\""](?<v>.*?)['\""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSset.Success)
            {
                var bagSS = GetSessionStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bagSS[mSSset.Groups["k"].Value] = mSSset.Groups["v"].Value;
                return true;
            }
            var mSSget = Regex.Match(line, @"^\s*sessionStorage\s*\.getItem\s*\(\s*['\""](?<k>.+?)['\""]\s*,\s*(?<fn>[A-ZaZ_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSget.Success)
            {
                var bagSS = GetSessionStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                string vSS = null; lock (_storageLock) bagSS.TryGetValue(mSSget.Groups["k"].Value, out vSS);
                var escSS = JsEscape(vSS ?? "", '\''); EnqueueMicrotask(() => { try { RunInline(mSSget.Groups["fn"].Value + "('" + escSS + "')", _ctx); } catch { } });
                return true;
            }
            var mSSrem = Regex.Match(line, @"^\s*sessionStorage\s*\.removeItem\s*\(\s*['\""](?<k>.+?)['\""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mSSrem.Success)
            {
                var bagSS = GetSessionStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bagSS.Remove(mSSrem.Groups["k"].Value);
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*sessionStorage\s*\.clear\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase))
            {
                var bagSS = GetSessionStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri);
                lock (_storageLock) bagSS.Clear();
                return true;
            }

            // ---------- document.cookie ----------
            var mCset = Regex.Match(line, @"^\s*document\s*\.cookie\s*=\s*['""](?<c>.+?)['""]\s*;?$", RegexOptions.IgnoreCase);
            if (mCset.Success) { SetCookieString(ctx?.BaseUri ?? _ctx?.BaseUri, mCset.Groups["c"].Value); return true; }
            var mCget = Regex.Match(line, @"^\s*__getCookie\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$", RegexOptions.IgnoreCase); // host helper
            if (mCget.Success)
            {
                var s = GetCookieString(ctx?.BaseUri ?? _ctx?.BaseUri);
                var esc = JsEscape(s ?? "", '\'');
                EnqueueMicrotask(() => { try { RunInline(mCget.Groups["fn"].Value + "('" + esc + "')", _ctx); } catch { } });
                return true;
            }

            // ---------- classList on element by id ----------
            var mCls = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.classList\s*\.(?<op>add|remove|toggle)\s*\(\s*['""](?<cls>.+?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mCls.Success && _domRoot != null)
            {
                var id = mCls.Groups["id"].Value; var op = mCls.Groups["op"].Value; var cls = mCls.Groups["cls"].Value;
                var doc = new JsDocument(this, _domRoot);
                var el = doc.getElementById(id) as JsDomElement;
                if (el != null)
                {
                    var classes = el.getAttribute("class") ?? "";
                    var set = new HashSet<string>((classes ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                    if (op == "add") set.Add(cls);
                    else if (op == "remove") set.Remove(cls);
                    else if (op == "toggle") { if (!set.Add(cls)) set.Remove(cls); }
                    el.setAttribute("class", string.Join(" ", set.ToArray()));
                }
                return true;
            }

            // ---------- history ----------
            var mPush = Regex.Match(line, @"^\s*history\s*\.pushState\s*\(\s*.*?,\s*['""](?<url>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mPush.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mPush.Groups["url"].Value);
                if (u != null) { HistoryPush(u); try { _host.SetStatus("pushState -> " + u); } catch { } }
                return true;
            }
            var mRep = Regex.Match(line, @"^\s*history\s*\.replaceState\s*\(\s*.*?,\s*['""](?<url>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mRep.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mRep.Groups["url"].Value);
                if (u != null) { HistoryReplace(u); try { _host.SetStatus("replaceState -> " + u); } catch { } }
                return true;
            }
            if (Regex.IsMatch(line, @"^\s*history\s*\.back\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase)) { HistoryGo(-1); return true; }
            if (Regex.IsMatch(line, @"^\s*history\s*\.forward\s*\(\s*\)\s*;?$", RegexOptions.IgnoreCase)) { HistoryGo(1); return true; }
            var mGo = Regex.Match(line, @"^\s*history\s*\.go\s*\(\s*(?<n>-?\d+)\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mGo.Success) { int n; if (int.TryParse(mGo.Groups["n"].Value, out n)) HistoryGo(n); return true; }

            // ---------- new Audio("url").play() stub (no-op) ----------
            var mAudioNewPlay = System.Text.RegularExpressions.Regex.Match(
                line,
                "^\\s*new\\s+Audio\\s*\\(\\s*(['\"'])(?<url>.*?)\\1\\s*\\)\\s*\\.\\s*play\\s*\\(\\s*\\)\\s*;?\\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mAudioNewPlay.Success)
            {
                try
                {
                    TraceFeatureGap("Audio", "new Audio().play", mAudioNewPlay.Groups["url"].Value ?? string.Empty);
                }
                catch { }
                return true;
            }

            // ---------- atob / btoa for innerText ----------
            var mAtob = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.innerText\s*=\s*atob\s*\(\s*['""](?<b64>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mAtob.Success && _domRoot != null)
            {
                try
                {
                    var id = mAtob.Groups["id"].Value; var b = mAtob.Groups["b64"].Value;
                    var bytes = Convert.FromBase64String(b);
                    var txt = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                    var doc = new JsDocument(this, _domRoot);
                    var el = doc.getElementById(id) as JsDomElement;
                    if (el != null) { el.innerText = txt; RequestRepaint(); }
                }
                catch { }
                return true;
            }
            // document.getElementById('id').addEventListener('evt', fn)
            var mElAdd = Regex.Match(line,
                @"^\s*document\s*\.getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.addEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$",
                RegexOptions.IgnoreCase);
            if (mElAdd.Success)
            {
                RegisterElementListener(mElAdd.Groups["id"].Value, mElAdd.Groups["evt"].Value, mElAdd.Groups["fn"].Value);
                return true;
            }

            // document.getElementById('id').removeEventListener('evt', fn)
            var mElRem = Regex.Match(line,
                @"^\s*document\s*\.getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.removeEventListener\s*\(\s*['""](?<evt>[^'""]+)['""]\s*,\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?$",
                RegexOptions.IgnoreCase);
            if (mElRem.Success)
            {
                RemoveElementListener(mElRem.Groups["id"].Value, mElRem.Groups["evt"].Value, mElRem.Groups["fn"].Value);
                return true;
            }
            var mBtoa = Regex.Match(line, @"^\s*document\s*\.\s*getElementById\s*\(\s*['""](?<id>.+?)['""]\s*\)\s*\.innerText\s*=\s*btoa\s*\(\s*['""](?<txt>.*?)['""]\s*\)\s*;?$", RegexOptions.IgnoreCase);
            if (mBtoa.Success && _domRoot != null)
            {
                try
                {
                    var id = mBtoa.Groups["id"].Value; var t = mBtoa.Groups["txt"].Value;
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(t ?? ""));
                    var doc = new JsDocument(this, _domRoot);
                    var el = doc.getElementById(id) as JsDomElement;
                    if (el != null) { el.innerText = b64; RequestRepaint(); }
                }
                catch { }
                return true;
            }

            // ---------- fetch(url).then(fn) ----------
            var mFetch = Regex.Match(line, @"^\s*fetch\s*\(\s*(['""])(?<url>.*?)\1\s*\)\s*\.then\s*\(\s*(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*)\s*\)\s*;?\s*$", RegexOptions.IgnoreCase);
            if (mFetch.Success)
            {
                var url = mFetch.Groups["url"].Value;
                var fn = mFetch.Groups["fn"].Value;
                var uri = Resolve(_ctx?.BaseUri, url);
                if (uri != null)
                {
                    var pageOrigin = _ctx?.BaseUri;
                    Task.Run(async () =>
                    {
                        try
                        {
                            using (var client = new System.Net.Http.HttpClient(CreateManagedHandler()))
                            {
                                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 FenBrowser");
                                
                                // Add Origin header for CORS
                                if (pageOrigin != null)
                                {
                                    var originStr = $"{pageOrigin.Scheme}://{pageOrigin.Host}";
                                    if (!pageOrigin.IsDefaultPort && pageOrigin.Port != -1)
                                        originStr += $":{pageOrigin.Port}";
                                    client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", originStr);
                                }
                                
                                var response = await client.GetAsync(uri);
                                var text = await response.Content.ReadAsStringAsync();
                                
                                // CORS check for cross-origin requests
                                bool corsOk = true;
                                if (pageOrigin != null && !FenBrowser.Core.Network.Handlers.CorsHandler.IsSameOrigin(uri, pageOrigin))
                                {
                                    corsOk = FenBrowser.Core.Network.Handlers.CorsHandler.IsCorsAllowed(response, uri, pageOrigin);
                                }
                                
                                if (corsOk && text != null)
                                {
                                    var token = RegisterResponseBody(text);
                                    EnqueueMicrotask(() =>
                                    {
                                        try { RunInline(fn + "({ ok:true, status:200, text:function(){ return '" + JsEscape(text) + "'; }, json:function(){ return JSON.parse('" + JsEscape(text) + "'); } })", _ctx); } catch { }
                                    });
                                }
                                else if (!corsOk)
                                {
                                    // CORS blocked
                                    EnqueueMicrotask(() =>
                                    {
                                        try { RunInline(fn + "({ ok:false, status:0, statusText:'CORS error' })", _ctx); } catch { }
                                    });
                                }
                            }
                        }
                        catch { }
                    });
                }
                return true;
            }

// ---------- setTimeout / clearTimeout ----------
var mST = System.Text.RegularExpressions.Regex.Match(line, @"^\s*setTimeout\s*\(\s*(?:['""](?<code>.*?)['""]|(?<fn>[A-Za-z_$][A-ZaZ0-9_$]*))\s*,\s*(?<ms>\d+)\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mST.Success)
            {
                var ms = 0; int.TryParse(mST.Groups["ms"].Value, out ms);
                var code = mST.Groups["code"].Success ? mST.Groups["code"].Value : null;
                var fn = mST.Groups["fn"].Success ? mST.Groups["fn"].Value : null;
                var id = System.Threading.Interlocked.Increment(ref _nextTimerId);
                System.Threading.Timer t = null;
                System.Threading.TimerCallback fire = _ =>
                {
                    try
                    {
                        var repaintHost = _host as IJsHostRepaint;
                        System.Action run = () => {
                            try
                            {
                                if (fn != null) RunInline(fn + "()", _ctx);
                                else if (!string.IsNullOrEmpty(code)) RunInline(code, _ctx);
                            }
                            catch { }
                        };
                        if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                    }
                    catch { }
                    finally
                    {
                        lock (_timers) { try { if (_timers.ContainsKey(id)) { _timers[id].Dispose(); } } catch { } _timers.Remove(id); }
                    }
                };
                t = new System.Threading.Timer(fire, null, ms, System.Threading.Timeout.Infinite);
                lock (_timers) _timers[id] = t;
                try { _host.SetStatus("setTimeout id=" + id); } catch { }
                return true;
            }
            var mCT = System.Text.RegularExpressions.Regex.Match(line, @"^\s*clearTimeout\s*\(\s*(?<id>\d+)\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mCT.Success)
            {
                int id; if (int.TryParse(mCT.Groups["id"].Value, out id))
                {
                    lock (_timers)
                    {
                        System.Threading.Timer t; if (_timers.TryGetValue(id, out t)) { try { t.Dispose(); } catch { } _timers.Remove(id); }
                    }
                }
                return true;
            }

            // ---------- console.log / console.error ----------
            var mLog = System.Text.RegularExpressions.Regex.Match(line, @"^\s*console\s*\.\s*(?<kind>log|error|warn)\s*\(\s*['""](?<msg>.*?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mLog.Success)
            {
                try { _host.SetStatus((mLog.Groups["kind"].Value.ToLowerInvariant()) + ": " + mLog.Groups["msg"].Value); } catch { }
                return true;
            }

            // ---------- location.assign / replace / href= ----------
            var mAssign = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*assign\s*\(\s*['""](?<u>.+?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mAssign.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mAssign.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }
            var mReplace = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*replace\s*\(\s*['""](?<u>.+?)['""]\s*\)\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mReplace.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mReplace.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }
            var mHrefSet = System.Text.RegularExpressions.Regex.Match(line, @"^\s*location\s*\.\s*href\s*=\s*['""](?<u>.+?)['""]\s*;?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mHrefSet.Success)
            {
                var u = Resolve(ctx?.BaseUri ?? _ctx?.BaseUri, mHrefSet.Groups["u"].Value);
                if (u != null) { try { _host.Navigate(u); } catch { } }
                return true;
            }

            // ---------- document.title = "..." ----------
            var mTitle = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^\s*document\s*\.\s*title\s*=\s*['""](?<t>.*?)['""]\s*;?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (mTitle.Success)
            {
                try
                {
                    string tval = "";
                    if (mTitle.Groups["t"] != null && mTitle.Groups["t"].Value != null)
                        tval = mTitle.Groups["t"].Value;
                    _host.SetTitle(tval);
                }
                catch { }
                return true;
            }

            return false;
        }

        public object Evaluate(string script)
        {
#if USE_NILJS
            if (_nil != null)
            {
                try
                {
                    var result = _nil.Eval(script);
                    if (result == null) return null;
                    if (result.IsNumber) return (double)result;
                    if (result.ValueType == JSValueType.String) return result.ToString();
                    if (result.ValueType == JSValueType.Boolean) return (bool)result;
                    return result.ToString();
                }
                catch (Exception ex)
                {
                    return "Error: " + ex.Message;
                }
            }
#endif
            
            // Use FenEngine for evaluation
            if (_fenRuntime != null)
            {
                try
                {
                    var rawResult = _fenRuntime.ExecuteSimple(script);
                    
                    // Unwrap ReturnValue wrapper (from return statements)
                    FenBrowser.FenEngine.Core.Interfaces.IValue result = rawResult;
                    while (result is FenBrowser.FenEngine.Core.ReturnValue retVal)
                    {
                        result = retVal.Value;
                    }
                    
                    // Now result is the actual value (FenValue)
                    if (result is FenBrowser.FenEngine.Core.FenValue fv)
                    {
                        if (fv.IsNumber) return fv.ToNumber();
                        if (fv.IsString) return fv.ToString();
                        if (fv.IsBoolean) return fv.ToBoolean();
                        if (fv.IsNull) return null;
                        if (fv.IsUndefined) return null;
                        // Return FenValue for objects/arrays so ToNativeObject can convert them
                        return fv;
                    }
                    
                    // Fallback for non-FenValue IValue types
                    if (result.IsNumber) return result.ToNumber();
                    if (result.IsString) return result.ToString();
                    if (result.IsBoolean) return result.ToBoolean();
                    if (result.IsNull) return null;
                    if (result.IsUndefined) return null;
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FenEngine] Evaluate error: {ex.Message}");
                    return null;
                }
            }
            
            return null;
        }









        // Legacy JSON-based localStorage persistence (no longer used; kept for compatibility reference)
        // The engine now uses a simple line-based format via SaveLocalStorageAsync/RestoreLocalStorageAsync
        // against the _localStorageMap dictionary. This stub remains to avoid breaking older call sites.
        private async void PersistLocalStorage()
        {
            try
            {
                await SaveLocalStorageAsync().ConfigureAwait(false);
            }
            catch { }
        }

    public void LocalStorageSet(string key, string value, JsContext ctx) { try { if (!SandboxAllows(SandboxFeature.Storage, "localStorage.setItem")) return; var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri); lock(_storageLock) bag[key] = value ?? ""; PersistLocalStorage(); } catch { } }
    public string LocalStorageGet(string key, JsContext ctx) { try { if (!SandboxAllows(SandboxFeature.Storage, "localStorage.getItem")) return null; var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri); string v=null; lock(_storageLock) bag.TryGetValue(key, out v); return v; } catch { return null; } }
    public void LocalStorageRemove(string key, JsContext ctx) { try { if (!SandboxAllows(SandboxFeature.Storage, "localStorage.removeItem")) return; var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri); lock(_storageLock) bag.Remove(key); PersistLocalStorage(); } catch { } }
    public void LocalStorageClear(JsContext ctx) { try { if (!SandboxAllows(SandboxFeature.Storage, "localStorage.clear")) return; var bag = GetLocalStorageFor(ctx?.BaseUri ?? _ctx?.BaseUri); lock(_storageLock) bag.Clear(); PersistLocalStorage(); } catch { } }

    public void Reset(JsContext ctx)
        {
            _ctx = ctx ?? new JsContext();
            ClearSandboxBlockLog();

            // Recreate MiniJS and (re)bootstrap the environment - DISABLED
            /*
            try
            {
                _mini = new MiniJs.Engine();
            }
            catch { _mini = null; }
            */

            // try { EnsureMiniEnvironment(); } catch { }
        }
        
        // MiniJs environment setup - DISABLED
        /*
        private void EnsureMiniEnvironment()
        {
            try
            {
                if (_mini == null) return;

                // Wire standard environment & bridges
                MiniJs.Bootstrap.InitEnvironment(
                    _mini,
                    _host,
                    FetchTextSync,
                    // setTimeout
                    (code, ms) =>
                    {
                        try
                        {
                            int id = Interlocked.Increment(ref _nextTimerId);
                            System.Threading.Timer t = null;
                            System.Threading.TimerCallback fire = _ =>
                            {
                                try
                                {
                                    if (_mini != null) _mini.Execute(code ?? "");
                                }
                                catch { }
                                finally
                                {
                                    lock (_timers)
                                    {
                                        try { if (_timers.ContainsKey(id)) _timers[id].Dispose(); } catch { }
                                        _timers.Remove(id);
                                    }
                                }
                            };
                            t = new System.Threading.Timer(fire, null, ms, Timeout.Infinite);
                            lock (_timers) _timers[id] = t;
                            return id;
                        }
                        catch { }
                        return -1;
                    },
                    // clearTimeout
                    (id) =>
                    {
                        try
                        {
                            lock (_timers)
                            {
                                System.Threading.Timer t;
                                if (_timers.TryGetValue(id, out t))
                                {
                                    try { t.Dispose(); } catch { }
                                    _timers.Remove(id);
                                }
                            }
                        }
                        catch { }
                    }
                );
            }
            catch { }
        }
        */
        private string FetchTextSync(Uri uri)
        {
            try
            {
                using (var hc = new HttpClient())
                {
                    try { hc.DefaultRequestHeaders.UserAgent.ParseAdd("MiniJs/1.0"); } catch { }
                    return hc.GetStringAsync(uri).GetAwaiter().GetResult();
                }
            }
            catch { return ""; }
        }

        // Request the host to re-render if available (non-breaking: optional interface)
        private void RequestRepaint()
        {
            // Coalesce frequent requests
            if (_repaintRequested) return;
            _repaintRequested = true;
            var repaint = _host as IJsHostRepaint;
            if (repaint != null)
            {
                try { repaint.RequestRender(); }
                catch { /* swallow */ }
            }
            else
            {
                // fallback: set status so developer sees mutations
                try { _host.SetStatus("[DOM mutated]"); } catch { }
            }
            // after the host is requested to repaint, schedule any MutationObserver callbacks
            try { InvokeMutationObservers(); } catch { }
            _repaintRequested = false;
        }

        // When the DOM changes, invoke any registered MutationObserver callbacks as microtasks
        private void InvokeMutationObservers()
        {
            List<MutationRecord> mutations = null;
            lock (_mutationLock)
            {
                if (_pendingMutations.Count > 0)
                {
                    mutations = new List<MutationRecord>(_pendingMutations);
                    _pendingMutations.Clear();
                }
            }

            if (mutations == null || mutations.Count == 0) return;

            // 1. Legacy string-based observers
            try
            {
                string[] legacyObservers;
                lock (_mutationObservers) legacyObservers = _mutationObservers.ToArray();

                if (legacyObservers.Length > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append("[");
                    bool first = true;
                    foreach (var mr in mutations)
                    {
                        if (!first) sb.Append(",");
                        sb.Append("{");
                        sb.Append($"'type':'{mr.Type}'");
                        sb.Append("}");
                        first = false;
                    }
                    sb.Append("]");
                    var json = sb.ToString();
                    
                    foreach (var fn in legacyObservers)
                    {
                        EnqueueMicrotask(() => { try { RunInline(fn + "(" + json + ")", _ctx); } catch { } });
                    }
                }
            }
            catch { }

            // 2. New FenFunction observers
            try
            {
                FenFunction[] fenObservers;
                lock (_mutationLock) fenObservers = _fenMutationObservers.ToArray();

                if (fenObservers.Length > 0)
                {
                    var fenArray = new FenObject();
                    int idx = 0;
                    foreach (var mr in mutations)
                    {
                        if (mr.Type == "attributes")
                        {
                             var rec = new FenObject();
                             rec.Set("type", FenValue.FromString("attributes"));
                             if (mr.AttributeName != null)
                                rec.Set("attributeName", FenValue.FromString(mr.AttributeName));
                             
                             fenArray.Set(idx.ToString(), FenValue.FromObject(rec));
                             idx++;
                        }
                        else
                        {
                            var rec = new FenObject();
                            rec.Set("type", FenValue.FromString(mr.Type));
                            
                            // Added nodes
                            if (mr.AddedNodes != null && mr.AddedNodes.Count > 0)
                            {
                                var added = new FenObject();
                                for(int i=0; i<mr.AddedNodes.Count; i++) 
                                {
                                    var nodeObj = new FenObject();
                                    nodeObj.Set("nodeName", FenValue.FromString(mr.AddedNodes[i].Tag));
                                    added.Set(i.ToString(), FenValue.FromObject(nodeObj));
                                }
                                added.Set("length", FenValue.FromNumber(mr.AddedNodes.Count));
                                rec.Set("addedNodes", FenValue.FromObject(added));
                            }
                            else
                            {
                                 var empty = new FenObject(); empty.Set("length", FenValue.FromNumber(0));
                                 rec.Set("addedNodes", FenValue.FromObject(empty));
                            }

                            // Removed nodes
                            if (mr.RemovedNodes != null && mr.RemovedNodes.Count > 0)
                            {
                                var removed = new FenObject();
                                for(int i=0; i<mr.RemovedNodes.Count; i++) 
                                {
                                    var nodeObj = new FenObject();
                                    nodeObj.Set("nodeName", FenValue.FromString(mr.RemovedNodes[i].Tag));
                                    removed.Set(i.ToString(), FenValue.FromObject(nodeObj));
                                }
                                removed.Set("length", FenValue.FromNumber(mr.RemovedNodes.Count));
                                rec.Set("removedNodes", FenValue.FromObject(removed));
                            }
                            else
                            {
                                 var empty = new FenObject(); empty.Set("length", FenValue.FromNumber(0));
                                 rec.Set("removedNodes", FenValue.FromObject(empty));
                            }
                            
                            fenArray.Set(idx.ToString(), FenValue.FromObject(rec));
                            idx++;
                        }
                    }
                    fenArray.Set("length", FenValue.FromNumber(idx));
                    var args = new[] { FenValue.FromObject(fenArray), FenValue.Undefined };

                    foreach (var obs in fenObservers)
                    {
                        EnqueueMicrotask(() => 
                        {
                            try { _fenRuntime.ExecuteFunction(obs, args); } catch { }
                        });
                    }
                }
            }
            catch { }
        }

        private void RecordMutation(MutationRecord record)
        {
            lock (_mutationLock)
            {
                _pendingMutations.Add(record);
            }
            // Schedule microtask to deliver mutations
            EnqueueMicrotask(InvokeMutationObservers);
        }

        // ---- XHR shim state ----
        private sealed class XhrState
        {
            public string Id;
            public string Method;
            public Uri Url;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body;
            public string OnLoadFn;
            public string OnErrorFn;
        }

        private System.Net.Http.HttpMessageHandler CreateManagedHandler(Uri uri = null, System.Net.CookieContainer cookies = null)
        {
            var handler = new System.Net.Http.HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
                handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            
            if (cookies == null && uri != null && CookieBridge != null)
                cookies = CookieBridge(uri);

            if (cookies != null && handler.SupportsRedirectConfiguration)
                handler.CookieContainer = cookies;
            return handler;
        }

        private async Task<string> FetchAsync(Uri uri)
        {
            try
            {
                using (var client = new System.Net.Http.HttpClient(CreateManagedHandler()))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows Phone 8.1; ARM; Trident/7.0; Touch; rv:11.0; IEMobile/11.0; NOKIA; Lumia 520) like Gecko");
                    return await client.GetStringAsync(uri);
                }
            }
            catch { return null; }
        }

        public class HostWindow
        {
            private JavaScriptEngine _engine;
            public HostWindow(JavaScriptEngine engine) { _engine = engine; }
            public void alert(string msg) { _engine._host.SetStatus("[Alert] " + msg); _engine._host.Alert(msg); }
            
            public object document => new HostDocument(_engine);
            public object location => new HostLocation(_engine);
            public object history => new HostHistory(_engine);
            public object navigator => new HostNavigator(_engine);
            public object console => new HostConsole(_engine);
            public object self => this;
            public object window => this;
            public object top => this;
            public object parent => this;

            public int setTimeout(string code, int ms) => _engine.ScheduleTimeout(code, ms);
            public void clearTimeout(int id) => _engine.ClearTimeout(id);
            public int setInterval(string code, int ms) => _engine.ScheduleInterval(code, ms, false);
            public void clearInterval(int id) => _engine.ClearTimeout(id);

            public JsVal onpopstate
            {
                get { return _engine.OnPopState ?? JsVal.Undefined; }
                set { _engine.OnPopState = value; }
            }

            public JsVal onhashchange
            {
                get { return _engine.OnHashChange ?? JsVal.Undefined; }
                set { _engine.OnHashChange = value; }
            }
        }

        public class HostDocument
        {
            private JavaScriptEngine _engine;
            public HostDocument(JavaScriptEngine engine) { _engine = engine; }
            
            public object getElementById(string id) 
            { 
                var el = _engine._domRoot?.QueryByTag("*").FirstOrDefault(e => e.Attr != null && e.Attr.ContainsKey("id") && e.Attr["id"] == id);
                return el != null ? new JsDomElement(_engine, el) : null;
            }

            public object body => _engine._domRoot != null ? new JsDomElement(_engine, _engine._domRoot.FindById("body") ?? _engine._domRoot.QueryByTag("body").FirstOrDefault() ?? _engine._domRoot) : null;
            
            public object head => _engine._domRoot != null ? new JsDomElement(_engine, _engine._domRoot.QueryByTag("head").FirstOrDefault()) : null;

            public string title 
            { 
                get => _engine._docTitle; 
                set { /* _engine.SetTitle(value); */ } 
            }

            public string cookie 
            { 
                get => _engine.GetCookieString(_engine._ctx?.BaseUri); 
                set => _engine.SetCookieString(_engine._ctx?.BaseUri, value); 
            }

            public object createElement(string tagName)
            {
                return new JsDomElement(_engine, new LiteElement(tagName));
            }

            public object createTextNode(string data)
            {
                var t = new LiteElement("#text");
                t.Text = data;
                return new JsDomText(_engine, t);
            }

            public object querySelector(string sel)
            {
                if (_engine._domRoot == null) return null;
                return new JsDocument(_engine, _engine._domRoot).querySelector(sel);
            }

            public object[] querySelectorAll(string sel)
            {
                if (_engine._domRoot == null) return new object[0];
                return new JsDocument(_engine, _engine._domRoot).querySelectorAll(sel);
            }
        }

        public class HostConsole
        {
            private JavaScriptEngine _engine;
            public HostConsole(JavaScriptEngine engine) { _engine = engine; }
            public void log(string msg) { System.Diagnostics.Debug.WriteLine(msg); }
        }

        public class HostNavigator
        {
            private JavaScriptEngine _engine;
            public HostNavigator(JavaScriptEngine engine) { _engine = engine; }
            public string userAgent => BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
            public string appName => "Fenbrowser";
            public string appVersion => "1.0.0";
            public string product => "FenEngine";
            public string vendor => "Fenbrowser Project";
            public object userAgentData => new Dictionary<string, object>
            {
                { "brands", new [] { new { brand = "Fenbrowser", version = "1.0" }, new { brand = "FenEngine", version = "1.0" } } },
                { "mobile", false },
                { "platform", "Windows" }
            };
        }

        public sealed class JsVal
        {
            public double? Num; public string Str; public bool? Bool; public object Obj;
            public static JsVal FromNum(double n) { var v = new JsVal(); v.Num = n; return v; }
            public static JsVal FromStr(string s) { var v = new JsVal(); v.Str = s; return v; }
            public static JsVal FromBool(bool b) { var v = new JsVal(); v.Bool = b; return v; }
            public static JsVal Null() { return new JsVal(); }
            public override string ToString() { if (Str != null) return Str; if (Num.HasValue) return Num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); if (Bool.HasValue) return Bool.Value ? "true" : "false"; return "null"; }
            public bool Truthy() { if (Bool.HasValue) return Bool.Value; if (Num.HasValue) return Math.Abs(Num.Value) > 1e-9; if (Str != null) return Str.Length > 0; return false; }
            public static JsVal Marshal(object o) { return new JsVal { Obj = o }; }
            public static JsVal Undefined => new JsVal();
        }

        public class HostHistory
        {
            private JavaScriptEngine _engine;
            public HostHistory(JavaScriptEngine engine) { _engine = engine; }
            
            public void pushState(JsVal state, string title, string url) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] pushState url={url}");
                // In a real implementation, we would update the browser history stack
            }

            public void replaceState(JsVal state, string title, string url) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] replaceState url={url}");
            }

            public void go(int delta) 
            { 
                System.Diagnostics.Debug.WriteLine($"[History] go({delta})");
                TriggerPopState();
            }
            public void back() 
            { 
                System.Diagnostics.Debug.WriteLine("[History] back()");
                TriggerPopState();
            }
            public void forward() 
            { 
                System.Diagnostics.Debug.WriteLine("[History] forward()");
                TriggerPopState();
            }
            
            private void TriggerPopState()
            {
#if USE_NILJS
                /*
                if (_engine.OnPopState != null && _engine.OnPopState.ValueType == JsValType.Function)
                {
                    var evt = JsVal.Marshal(new { state = JsVal.Null });
                    (_engine.OnPopState as Function)?.Call(JsVal.Undefined, new Arguments { evt });
                }
                */
#endif
            }

            public int length => 1;
            public JsVal state => JsVal.Undefined;
        }

        public class HostLocation
        {
            private JavaScriptEngine _engine;
            public HostLocation(JavaScriptEngine engine) { _engine = engine; }
            public string href => _engine._ctx?.BaseUri?.ToString() ?? "";
        }

        public class HostLocalStorage
        {
            private JavaScriptEngine _engine;
            private bool _session;
            public HostLocalStorage(JavaScriptEngine engine, bool session) { _engine = engine; _session = session; }
            public string getItem(string key) { return _engine.LocalStorageGet(key, _engine._ctx); }
            public void setItem(string key, string value) { _engine.LocalStorageSet(key, value, _engine._ctx); }
            public void removeItem(string key) { _engine.LocalStorageRemove(key, _engine._ctx); }
            public void clear() { _engine.LocalStorageClear(_engine._ctx); }
        }

        private sealed class JsFuncDef
        {
            public List<string> Params = new List<string>();
            public List<JsFuncParam> Parameters = new List<JsFuncParam>();
            public string Body;        // block body source
            public string Expr;        // expression body source (arrow)
        }

#if USE_NILJS
        private void _nilInit()
        {
            _nil = new Context();

            // Expose standard globals
            _nil.DefineVariable("window").Assign(_nil.GlobalContext.ProxyValue(new HostWindow(this)));
            _nil.DefineVariable("document").Assign(_nil.GlobalContext.ProxyValue(new HostDocument(this)));
            _nil.DefineVariable("console").Assign(_nil.GlobalContext.ProxyValue(new HostConsole(this)));
            _nil.DefineVariable("navigator").Assign(_nil.GlobalContext.ProxyValue(new HostNavigator(this)));
            _nil.DefineVariable("location").Assign(_nil.GlobalContext.ProxyValue(new HostLocation(this)));
            _nil.DefineVariable("history").Assign(_nil.GlobalContext.ProxyValue(new HostHistory(this)));
            _nil.DefineVariable("localStorage").Assign(_nil.GlobalContext.ProxyValue(new HostLocalStorage(this, false)));
            _nil.DefineVariable("sessionStorage").Assign(_nil.GlobalContext.ProxyValue(new HostLocalStorage(this, true)));
        }

        private void _nilSyncDocument()
        {
            // Stub
        }

        // Host Classes for NiL.JS

        public class HostCanvas
        {
            public int width { get; set; } = 300;
            public int height { get; set; } = 150;

            public HostContext2D getContext(string type)
            {
                if (type == "2d") return new HostContext2D();
                return null;
            }
        }

        public class HostContext2D
        {
            public string fillStyle { get; set; } = "#000000";
            public string strokeStyle { get; set; } = "#000000";
            public double lineWidth { get; set; } = 1.0;
            public string font { get; set; } = "10px sans-serif";

            public void fillRect(double x, double y, double w, double h) 
            { 
                System.Diagnostics.Debug.WriteLine($"[Canvas] fillRect({x},{y},{w},{h}) style={fillStyle}");
            }

            public void strokeRect(double x, double y, double w, double h) 
            { 
                System.Diagnostics.Debug.WriteLine($"[Canvas] strokeRect({x},{y},{w},{h}) style={strokeStyle}");
            }

            public void clearRect(double x, double y, double w, double h) 
            { 
                System.Diagnostics.Debug.WriteLine($"[Canvas] clearRect({x},{y},{w},{h})");
            }

            public void fillText(string text, double x, double y) 
            { 
                System.Diagnostics.Debug.WriteLine($"[Canvas] fillText('{text}',{x},{y}) font={font} style={fillStyle}");
            }

            public JsVal measureText(string text)
            {
                // Basic approximation: 6px per char
                var width = (text ?? "").Length * 6.0;
                return JsVal.Marshal(new { width = width });
            }

            public void beginPath() { System.Diagnostics.Debug.WriteLine("[Canvas] beginPath"); }
            public void closePath() { System.Diagnostics.Debug.WriteLine("[Canvas] closePath"); }
            public void moveTo(double x, double y) { System.Diagnostics.Debug.WriteLine($"[Canvas] moveTo({x},{y})"); }
            public void lineTo(double x, double y) { System.Diagnostics.Debug.WriteLine($"[Canvas] lineTo({x},{y})"); }
            public void stroke() { System.Diagnostics.Debug.WriteLine($"[Canvas] stroke style={strokeStyle}"); }
            public void fill() { System.Diagnostics.Debug.WriteLine($"[Canvas] fill style={fillStyle}"); }
            
            public void drawImage(JsVal image, double x, double y) 
            { 
                System.Diagnostics.Debug.WriteLine($"[Canvas] drawImage({image},{x},{y})");
            }
        }

        public class HostAudio
        {
            public string src { get; set; }
            public double currentTime { get; set; }
            public double duration { get; set; }
            public bool paused { get; set; } = true;

            public HostAudio(string src)
            {
                this.src = src;
            }

            public void play() 
            { 
                paused = false; 
                // Trigger host audio playback if possible
            }
            
            public void pause() 
            { 
                paused = true; 
            }
        }
#endif

        private abstract class JsBindingPattern
        {
            public string DefaultExpr;
        }

        private sealed class JsIdentifierPattern : JsBindingPattern
        {
            public string Name;
        }

        private sealed class JsObjectPattern : JsBindingPattern
        {
            public sealed class PropertyBinding
            {
                public string Key;
                public JsBindingPattern Target;
            }

            public List<PropertyBinding> Properties = new List<PropertyBinding>();
            public string RestIdentifier;
        }

        private sealed class JsArrayPattern : JsBindingPattern
        {
            public sealed class ElementBinding
            {
                public bool IsHole;
                public JsBindingPattern Target;
            }

            public List<ElementBinding> Elements = new List<ElementBinding>();
            public JsIdentifierPattern RestTarget;
        }

        private sealed class JsFuncParam
        {
            public string Raw;
            public JsBindingPattern Pattern;
            public bool IsRest;
        }

        private static JsFuncParam CreateIdentifierParam(string name, bool isRest = false)
        {
            return new JsFuncParam
            {
                Raw = name ?? string.Empty,
                Pattern = new JsIdentifierPattern { Name = name },
                IsRest = isRest
            };
        }

        private readonly Dictionary<string, JsFuncDef> _userFunctionsEx = new Dictionary<string, JsFuncDef>(StringComparer.Ordinal);

        /// <summary>Expose current DOM to the engine (for document.* bridge).</summary>
        public void SetDom(LiteElement domRoot, Uri baseUri = null)
        {
            _domRoot = domRoot;
            
            // Initialize FenEngine with DOM
            if (_fenRuntime != null)
            {
                try
                {
                    _fenRuntime.SetDom(domRoot);
                    try { System.IO.File.AppendAllText("debug_log.txt", "[JavaScriptEngine] SetDom called on FenRuntime\r\n"); } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FenEngine] Error setting DOM: {ex.Message}");
                    try { System.IO.File.AppendAllText("debug_log.txt", $"[FenEngine] Error setting DOM: {ex.Message}\r\n"); } catch { }
                }
                
                // Execute inline scripts with FenEngine
                try
                {
                    try { System.IO.File.AppendAllText("debug_log.txt", "[JavaScriptEngine] Starting inline script execution\r\n"); } catch { }
                    
                    foreach (var s in _domRoot.SelfAndDescendants())
                    {
                        if (string.Equals(s.Tag, "script", StringComparison.OrdinalIgnoreCase))
                        {
                            string code = null;

                            // External script
                            if (s.Attr != null && s.Attr.ContainsKey("src")) 
                            {
                                if (!SandboxAllows(SandboxFeature.ExternalScripts, "script src")) continue;

                                var src = s.Attr["src"];
                                if (!string.IsNullOrEmpty(src) && baseUri != null)
                                {
                                    try 
                                    {
                                        var scriptUri = new Uri(baseUri, src);
                                        
                                        // Check SubresourceAllowed delegate
                                        if (SubresourceAllowed != null && !SubresourceAllowed(scriptUri, "script"))
                                        {
                                             try { System.IO.File.AppendAllText("debug_log.txt", $"[JavaScriptEngine] Blocked script {scriptUri}\r\n"); } catch { }
                                             continue;
                                        }

                                        FenLogger.Debug($"[JavaScriptEngine] Fetching external script: {scriptUri}", LogCategory.JavaScript);
                                        
                                        // Use ExternalScriptFetcher if available (uses ResourceManager/Cache)
                                        if (ExternalScriptFetcher != null)
                                        {
                                             code = ExternalScriptFetcher(scriptUri, baseUri).Result;
                                        }
                                        else
                                        {
                                            // Fallback synchronous fetch
                                            using (var client = new System.Net.Http.HttpClient())
                                            {
                                                client.Timeout = TimeSpan.FromSeconds(5);
                                                code = client.GetStringAsync(scriptUri).Result;
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        try { System.IO.File.AppendAllText("debug_log.txt", $"[JavaScriptEngine] Failed to fetch script {src}: {ex.Message}\r\n"); } catch { }
                                    }
                                }
                            }
                            else
                            {
                                // Inline script
                                if (!SandboxAllows(SandboxFeature.InlineScripts, "inline script")) continue;
                                code = CollectScriptText(s);
                            }
                            
                            if (!string.IsNullOrWhiteSpace(code))
                            {
                                FenLogger.Debug($"[JavaScriptEngine] Executing script ({code.Length} chars)", LogCategory.JavaScript);
                                _fenRuntime.ExecuteSimple(code);
                            }
                        }
                    }
                    
                    FenLogger.Debug("[JavaScriptEngine] Inline script execution complete", LogCategory.JavaScript);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[JavaScriptEngine] Script execution error: {ex.Message}", LogCategory.JavaScript, ex);
                }
            }
            
            // JS is "enabled" in this app; hide server noscript overlays & flip no-js ? js
            this.SanitizeForScriptingEnabled(domRoot);
#if USE_NILJS
            try { _nilSyncDocument(); } catch { }
            
            // Execute inline scripts
            try
            {
                foreach (var s in _domRoot.SelfAndDescendants())
                {
                    if (string.Equals(s.Tag, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        if (s.Attr != null && s.Attr.ContainsKey("src")) continue; // Skip external for now
                        var code = CollectScriptText(s);
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            RunGlobalScript(code);
                        }
                    }
                }
            }
            catch { }
#endif
        }

        private void RunGlobalScript(string js)
        {
            if (string.IsNullOrWhiteSpace(js)) return;
#if USE_NILJS
            try
            {
                if (_nil != null)
                {
                    _nil.Eval(js);
                }
            }
            catch { }
#endif
        }

        private static string CollectScriptText(LiteElement n)
        {
            if (n == null) return "";
            if (n.IsText) return n.Text ?? "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Children.Count; i++) sb.Append(CollectScriptText(n.Children[i]));
            return sb.ToString();
        }

        #region JS enabled sanitizer
        // INSIDE: public sealed class JavaScriptEngine { ... }
        private void SanitizeForScriptingEnabled(LiteElement rootArg = null)
        {
            var root = rootArg ?? _domRoot;          // OK in instance method
            if (root == null) return;

            // flip no-js ? js on <html>/<body>
            Action<LiteElement> flipClass = n =>
            {
                if (n == null) return;
                var attrs = n.Attr;
                if (attrs == null) return;

                string cls;
                if (!attrs.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls)) return;

                var parts = new HashSet<string>(
                    cls.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);

                var changed = false;
                if (parts.Remove("no-js")) changed = true;
                if (!parts.Contains("js")) { parts.Add("js"); changed = true; }
                if (changed) attrs["class"] = string.Join(" ", parts.ToArray());
            };

            try
            {
                var html = (root.QueryByTag("html") ?? Enumerable.Empty<LiteElement>()).FirstOrDefault();
                if (html != null) flipClass(html);
                var body = (root.QueryByTag("body") ?? Enumerable.Empty<LiteElement>()).FirstOrDefault();
                if (body != null) flipClass(body);
            }
            catch { }

            try
            {
                var toRemove = new List<LiteElement>();
                foreach (var n in root.Descendants())
                    if (string.Equals(n.Tag, "noscript", StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(n);
                foreach (var n in toRemove) n.Parent?.Children.Remove(n);
            }
            catch { }

            // Do NOT remove <noscript> on this platform; we are a limited JS renderer
            // and <noscript> often contains the only usable fallback content.
            // Leave nodes intact so the renderer can show them.

            this.RequestRepaint();                    // OK in instance method
        }

        #endregion


        void TryUpdateClassList(string id, string op, string cls)
        {
            try
            {
                if (_domRoot == null || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) return;
                var doc = new JsDocument(this, _domRoot);
                var el = doc.getElementById(id) as JsDomElement; if (el == null) return;
                var cur = el.getAttribute("class") ?? string.Empty;
                var set = new HashSet<string>(cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                bool changed = false;
                if (op == "add") { if (!set.Contains(cls)) { set.Add(cls); changed = true; } }
                else if (op == "remove") { if (set.Remove(cls)) changed = true; }
                else if (op == "toggle") { if (!set.Remove(cls)) { set.Add(cls); } changed = true; }
                if (changed) el.setAttribute("class", string.Join(" ", set.ToArray()));
            }
            catch { }
        }
        internal sealed class ResponseEntry
        {
            public string Body;
            public DateTime Ts;

            public ResponseEntry(string body, DateTime ts)
            {
                Body = body ?? string.Empty;
                Ts = ts;
            }
        }
    }

    // ------------------- Host interface + context (stable) -------------------

    public interface IJsHost
    {
        void Navigate(Uri target);
        void PostForm(Uri target, string body);
        void SetStatus(string s);

        void SetTitle(string tval);
        void Alert(string msg);
        void ScrollToElement(LiteElement element);
    }

    // Optional host interface: if implemented, JavaScriptEngine will call RequestRender() when DOM changes
    public interface IJsHostRepaint
    {
        // Request the host to schedule a UI re-render. Implementations should marshal to UI thread.
        void RequestRender();

        // Optional: host can provide a helper to invoke code on the UI thread. Timers and
        // other background callbacks should use this to safely mutate UI-bound data.
        void InvokeOnUiThread(Action action);
    }

    public sealed class JsContext
    {
        public Uri BaseUri { get; set; }
    }

    /// <summary>Convenience adapter so callers can pass delegates.</summary>
    public sealed class JsHostAdapter : IJsHost, IJsHostRepaint
    {
    private readonly Action<Uri> _navigate;
    private readonly Action<Uri, string> _post;
    private readonly Action<string> _status;
    private readonly Action _requestRender;
    private readonly Action<Action> _invokeOnUiThread;
    private readonly Action<string> _setTitle;
    private readonly Action<string> _alert;
    private readonly Action<LiteElement> _scrollToElement;

        public JsHostAdapter(Action<Uri> navigate, Action<Uri, string> post, Action<string> status, Action requestRender = null, Action<Action> invokeOnUiThread = null, Action<string> setTitle = null, Action<string> alert = null, Action<LiteElement> scrollToElement = null)
        {
            _navigate = navigate ?? (_ => { });
            _post = post ?? ((_, __) => { });
            _status = status ?? (_ => { });
            _requestRender = requestRender ?? (() => { try { _status("[DOM mutated]"); } catch { } });
            _invokeOnUiThread = invokeOnUiThread ?? (a => { try { a(); } catch { } });
            _setTitle = setTitle ?? (_ => { });
            _alert = alert ?? (_ => { });
            _scrollToElement = scrollToElement ?? (_ => { });
        }

        public void Navigate(Uri target) => _navigate(target);
        public void PostForm(Uri target, string body) => _post(target, body);
        public void SetStatus(string s) => _status(s);

        // IJsHostRepaint implementation
        public void RequestRender() { try { _requestRender(); } catch { } }
        public void InvokeOnUiThread(Action action) { if (action == null) return; try { _invokeOnUiThread(action); } catch { } }

        public void SetTitle(string tval)
        {
            try { _setTitle(tval ?? string.Empty); }
            catch { }
        }


        
        public void Alert(string msg) => _alert(msg);
        public void ScrollToElement(LiteElement element) => _scrollToElement(element);
    }
}
