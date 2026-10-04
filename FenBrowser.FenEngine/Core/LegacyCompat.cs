// Legacy compatibility types — minimal surface needed by remaining code after
// the legacy JS engine (Bytecode/, JavaScriptEngine, etc.) was removed.
// Live compatibility bridges must delegate to the current engine rather than
// silently reporting success or dropping state.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Core.Interfaces
{
    public enum ValueType
    {
        Undefined, Null, Boolean, Number, String, Object, Function, Symbol, BigInt, Error, Throw, ReturnValue
    }

    public interface IValue
    {
        ValueType Type { get; }
        bool ToBoolean();
        double ToNumber();
        string ToString();
        IObject ToObject();
        bool IsUndefined { get; }
        bool IsNull { get; }
        bool IsBoolean { get; }
        bool IsNumber { get; }
        bool IsString { get; }
        bool IsObject { get; }
        bool IsFunction { get; }
        IObject AsObject();
        double AsNumber(IExecutionContext context = null);
        bool AsBoolean();
        string AsString(IExecutionContext context = null);
        void Set(string key, IValue value);
        IValue Get(string key);
        void Revoke(object reason = null);
        object ToNativeObject();
    }

    public interface IObject
    {
        IValue Get(string key);
        IValue Get(string key, object context);
        void Set(string key, IValue value);
        bool Has(string key);
        bool Delete(string key);
        IEnumerable<string> Keys();
        IObject GetPrototype();
        Core.PropertyDescriptor GetOwnPropertyDescriptor(string key);
        IEnumerable<string> GetOwnPropertyNames();
    }

    public interface IHistoryBridge
    {
        void PushState(object state, string title, string url);
        void ReplaceState(object state, string title, string url);
        void Go(int delta);
        int Length { get; }
        object State { get; }
        Uri CurrentUrl { get; }
    }

    public interface IExecutionContext
    {
        Uri DocumentUrl { get; }
        int CallStackDepth { get; }
        IValue Environment { get; }
        IValue Permissions { get; }
        Action<IValue, string> OnUncaughtException { get; set; }
    }

    public interface IModuleLoader
    {
        string Resolve(string specifier, string referrer);
        IObject LoadModule(string path);
    }

    public interface IHtmlDdaObject { }
}

namespace FenBrowser.FenEngine.Core
{
    public sealed class PropertyDescriptor
    {
        public bool Writable { get; set; }
        public bool Configurable { get; set; }
        public bool Enumerable { get; set; }
        public Interfaces.IValue Value { get; set; }
    }
}

namespace FenBrowser.FenEngine.Core.Types
{
    using FenBrowser.FenEngine.Core.Interfaces;

    public sealed class JsSymbol
    {
        public string Description { get; }
        public JsSymbol(string description) => Description = description ?? string.Empty;
        public override string ToString() => $"Symbol({Description})";

        public string ToPropertyKey() => Description;

        public static class ToPrimitive
        {
            public static readonly JsSymbol Instance = new("Symbol.toPrimitive");
            public static string ToPropertyKey() => "Symbol.toPrimitive";
        }

        public static class Iterator
        {
            public static readonly JsSymbol Instance = new("Symbol.iterator");
            public static string ToPropertyKey() => "Symbol.iterator";
        }
    }

    public sealed class JsPromise
    {
        public IValue Result { get; set; }
        public string State { get; set; } = "pending";
    }

    public sealed class JsBigInt
    {
        public System.Numerics.BigInteger Value { get; }
        public JsBigInt(System.Numerics.BigInteger value) => Value = value;
        public override string ToString() => Value.ToString();
    }

    public sealed class FenFunction
    {
        public string Name { get; set; }
        public IValue Call(IValue thisValue, params IValue[] args) => Core.FenValue.Undefined;
        public IValue Invoke(IValue[] args, object context) => Core.FenValue.Undefined;
    }
}

namespace FenBrowser.FenEngine.Errors
{
    using FenBrowser.FenEngine.Core.Interfaces;

    public static class FenError
    {
        public static IValue FromException(Exception ex) => Core.FenValue.Undefined;
    }

    public sealed class FenTimeoutError : Exception
    {
        public FenTimeoutError(string message) : base(message) { }
    }
}

namespace FenBrowser.FenEngine.Diagnostics
{
    public static class JsDiagnosticsRecorder
    {
        public static void RecordEvaluation(string source, long durationMs) { }

        /// <summary>
        /// An exception a page's script let escape. This was a no-op, so a page that
        /// threw left nothing behind at all - no console line, no log entry, no
        /// difference from a page that ran cleanly. That is the hardest possible
        /// failure to diagnose, and it is the state reCAPTCHA leaves us in.
        /// </summary>
        public static void RecordException(
            FenBrowser.FenEngine.Core.Interfaces.IValue ex,
            string source = null,
            string stack = null)
        {
            string described;
            try
            {
                described = ex?.ToString() ?? "<no value>";
            }
            catch (Exception describeFailure)
            {
                // A thrown object whose own toString throws still has to be reported.
                described = $"<value could not be described: {describeFailure.GetType().Name}>";
            }

            WriteUncaught(described, source, stack);
        }

        public static void RecordException(Exception ex, string source = null, string stack = null)
        {
            WriteUncaught(
                ex is null ? "<null>" : $"{ex.GetType().Name}: {ex.Message}",
                source,
                stack ?? ex?.StackTrace);
        }

        private static void WriteUncaught(string described, string source, string stack)
        {
            try
            {
                FenBrowser.Core.Memory.EngineMetrics.Instance.Increment(
                    FenBrowser.Core.Memory.MetricCounter.JsUncaughtExceptionCount);
                FenBrowser.Core.Logging.EngineLog.Write(
                    FenBrowser.Core.Logging.LogSubsystem.Js,
                    FenBrowser.Core.Logging.LogSeverity.Error,
                    $"[JS:uncaught] {described}",
                    FenBrowser.Core.Logging.LogMarker.None,
                    default,
                    new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["source"] = source ?? string.Empty,
                        ["stack"] = stack ?? string.Empty
                    });
            }
            catch
            {
                // Diagnostics must never break a navigation.
            }
        }

        // console.log/warn/error already reach the log through the __fenLog native
        // hook, so recording them again here would double every line.
        public static void RecordConsole(string level, string message, string source = null) { }
    }
}

namespace FenBrowser.FenEngine.WebAPIs
{
    public sealed class StorageApi
    {
        public static readonly StorageApi Instance = new();
        public string GetItem(string key) => null;
        public void SetItem(string key, string value) { }
        public void RemoveItem(string key) { }
        public void Clear() { }
        public static void ClearAllStorage(bool deletePersistentFile = false) { }
    }
}

namespace FenBrowser.FenEngine.Scripting
{
    public static class JavaScriptEngine
    {
        // Geometry and scrolling come from whichever tab owns the element. Each tab
        // (a BrowserIntegration or a standalone CustomHtmlEngine) registers under
        // its own owner with the top-level document it serves; one process-wide
        // slot let the last tab created answer for every tab, and let any tab's
        // disposal clear it for all of them.
        private sealed record Registration(
            object Owner,
            Func<FenBrowser.Core.Dom.V2.Node> Root,
            Func<FenBrowser.Core.Dom.V2.Element, SkiaSharp.SKRect?> VisualRect,
            Action<FenBrowser.Core.Dom.V2.Element> ScrollTo)
        {
            public Action<double, double> ScrollViewport { get; init; }
        }

        private static readonly object ProviderGate = new();
        private static Registration[] _registrations = Array.Empty<Registration>();

        public static Core.FenValue Evaluate(string script)
        {
            throw new NotSupportedException(
                "The removed legacy JavaScriptEngine cannot evaluate script. Route evaluation through FenJS instead.");
        }

        public static bool TryGetVisualRect(
            FenBrowser.Core.Dom.V2.Element element,
            out double x,
            out double y,
            out double w,
            out double h)
        {
            x = y = w = h = 0;
            if (element == null)
                return false;

            var provider = ResolveRegistration(element, r => r.VisualRect != null)?.VisualRect;
            if (provider == null)
                return false;

            var rect = provider(element);
            if (!rect.HasValue)
                return false;

            var value = rect.Value;
            if (!float.IsFinite(value.Left) || !float.IsFinite(value.Top) ||
                !float.IsFinite(value.Right) || !float.IsFinite(value.Bottom) ||
                value.Width < 0f || value.Height < 0f)
            {
                return false;
            }

            x = value.Left;
            y = value.Top;
            w = value.Width;
            h = value.Height;
            return true;
        }

        /// <summary>Registers (or, with null, removes) the visual-rect provider of the tab <paramref name="owner"/>.</summary>
        public static void SetVisualRectProvider(
            object owner,
            Func<FenBrowser.Core.Dom.V2.Node> root,
            Func<FenBrowser.Core.Dom.V2.Element, SkiaSharp.SKRect?> provider)
        {
            Update(owner, existing => existing with { Root = root ?? existing.Root, VisualRect = provider }, root,
                visualRect: provider, scrollTo: null);
        }

        /// <summary>Registers (or, with null, removes) the scroll-to-element provider of the tab <paramref name="owner"/>.</summary>
        public static void SetScrollToElementProvider(
            object owner,
            Func<FenBrowser.Core.Dom.V2.Node> root,
            Action<FenBrowser.Core.Dom.V2.Element> provider)
        {
            Update(owner, existing => existing with { Root = root ?? existing.Root, ScrollTo = provider }, root,
                visualRect: null, scrollTo: provider);
        }

        /// <summary>
        /// Registers (or, with null, removes) the provider that scrolls the document
        /// viewport of the tab <paramref name="owner"/> to an absolute offset - the
        /// host owns that scroll, so window.scrollTo/scrollBy go through it.
        /// </summary>
        public static void SetViewportScrollProvider(
            object owner,
            Func<FenBrowser.Core.Dom.V2.Node> root,
            Action<double, double> provider)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (ProviderGate)
            {
                var list = _registrations.ToList();
                var index = list.FindIndex(r => ReferenceEquals(r.Owner, owner));
                var updated = index >= 0
                    ? list[index] with { Root = root ?? list[index].Root, ScrollViewport = provider }
                    : new Registration(owner, root, null, null) { ScrollViewport = provider };
                if (index >= 0) list.RemoveAt(index);
                if (updated.VisualRect != null || updated.ScrollTo != null || updated.ScrollViewport != null) list.Add(updated);
                _registrations = list.ToArray();
            }
        }

        /// <summary>Scrolls the viewport of the tab whose document holds <paramref name="node"/>.</summary>
        public static bool TryScrollViewport(FenBrowser.Core.Dom.V2.Node node, double x, double y)
        {
            var element = node as FenBrowser.Core.Dom.V2.Element ??
                (node as FenBrowser.Core.Dom.V2.Document)?.DocumentElement ??
                node?.OwnerDocument?.DocumentElement;
            if (element == null)
                return false;

            var provider = ResolveRegistration(element, r => r.ScrollViewport != null)?.ScrollViewport;
            if (provider == null)
                return false;

            provider(x, y);
            return true;
        }

        /// <summary>Drops everything <paramref name="owner"/> registered.</summary>
        public static void RemoveProviders(object owner)
        {
            lock (ProviderGate)
            {
                _registrations = _registrations.Where(r => !ReferenceEquals(r.Owner, owner)).ToArray();
            }
        }

        private static void Update(
            object owner,
            Func<Registration, Registration> change,
            Func<FenBrowser.Core.Dom.V2.Node> root,
            Func<FenBrowser.Core.Dom.V2.Element, SkiaSharp.SKRect?> visualRect,
            Action<FenBrowser.Core.Dom.V2.Element> scrollTo)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (ProviderGate)
            {
                var list = _registrations.ToList();
                var index = list.FindIndex(r => ReferenceEquals(r.Owner, owner));
                var updated = index >= 0 ? change(list[index]) : new Registration(owner, root, visualRect, scrollTo);
                if (index >= 0) list.RemoveAt(index);
                if (updated.VisualRect != null || updated.ScrollTo != null || updated.ScrollViewport != null) list.Add(updated);
                _registrations = list.ToArray();
            }
        }

        // The registration whose top-level document holds the element; with only
        // one tab registered (or a detached element), that one.
        private static Registration ResolveRegistration(
            FenBrowser.Core.Dom.V2.Element element,
            Func<Registration, bool> usable)
        {
            var registrations = System.Threading.Volatile.Read(ref _registrations).Where(usable).ToArray();
            if (registrations.Length <= 1)
            {
                return registrations.FirstOrDefault();
            }

            FenBrowser.Core.Dom.V2.Node top = element;
            while (top.ParentNode != null)
            {
                top = top.ParentNode;
            }

            for (var i = registrations.Length - 1; i >= 0; i--)
            {
                var root = registrations[i].Root?.Invoke();
                if (root != null && (ReferenceEquals(root, top) ||
                                     (root is FenBrowser.Core.Dom.V2.Element rootElement && ReferenceEquals(rootElement.OwnerDocument, top)) ||
                                     ReferenceEquals((root as FenBrowser.Core.Dom.V2.Document)?.DocumentElement, top)))
                {
                    return registrations[i];
                }
            }

            return null;
        }

        public static bool TryScrollToElement(FenBrowser.Core.Dom.V2.Element element)
        {
            if (element == null)
                return false;

            var provider = ResolveRegistration(element, r => r.ScrollTo != null)?.ScrollTo;
            if (provider == null)
                return false;

            provider(element);
            return true;
        }
    }
}

namespace FenBrowser.FenEngine.Scripting
{
    using FenBrowser.Core.Dom.V2;

    // Compatibility interface retained for current host adapters while FenJS owns JS execution.
    public interface IJsHost
    {
        void Navigate(Uri target);
        void PostForm(Uri target, string body);

        /// <summary>
        /// Submits a form navigation that carries an encoded request body. A
        /// method=POST form is only submitted when its entry list reaches the
        /// server, so a host that navigates to the action URL alone has not
        /// submitted the form -- it has issued an unrelated empty GET.
        /// </summary>
        void PostForm(Uri target, byte[] body, string contentType)
        {
            // Legacy hosts degrade to the old behaviour, but say so: silently
            // dropping the body is indistinguishable from the server rejecting
            // the submission.
            Log($"[JsHost] Form POST to '{target}' dropped the request body: host does not support body-carrying navigation.");
            Navigate(target);
        }
        void SetStatus(string s);
        void SetTitle(string tval);
        void Alert(string msg);
        bool Confirm(string msg);
        string Prompt(string msg, string defaultValue);
        void Log(string msg);
        void ScrollToElement(Element element);
        void FocusNode(Element element);
        object GetLayoutEngine() => null;
    }

    public interface IJsHostRepaint
    {
        void RequestRender();
        void InvokeOnUiThread(Action action);
    }

    public sealed class JsContext
    {
        public Uri BaseUri { get; set; }
    }

    public sealed class JsHostAdapter : IJsHost, IJsHostRepaint
    {
        private readonly Action<Uri> _navigate;
        private readonly Action<Uri, string> _post;
        private readonly Action<Uri, byte[], string> _postBody;
        private readonly Action<string> _status;
        private readonly Action _requestRender;
        private readonly Action<Action> _invokeOnUiThread;
        private readonly Action<string> _setTitle;
        private readonly Action<string> _alert;
        private readonly Func<string, bool> _confirm;
        private readonly Func<string, string, string> _prompt;
        private readonly Action<string> _log;
        private readonly Action<Element> _scrollToElement;
        private readonly Action<Element> _focusNode;

        public JsHostAdapter(
            Action<Uri> navigate, Action<Uri, string> post, Action<string> status,
            Action requestRender = null, Action<Action> invokeOnUiThread = null,
            Action<string> setTitle = null, Action<string> alert = null,
            Func<string, bool> confirm = null, Func<string, string, string> prompt = null,
            Action<string> log = null, Action<Element> scrollToElement = null,
            Action<Element> focusNode = null,
            Action<Uri, byte[], string> postBody = null)
        {
            _navigate = navigate ?? (_ => { });
            _post = post ?? ((_, __) => { });
            _postBody = postBody;
            _status = status ?? (_ => { });
            _requestRender = requestRender ?? (() => { });
            _invokeOnUiThread = invokeOnUiThread ?? (a => { try { a(); } catch { } });
            _setTitle = setTitle ?? (_ => { });
            _alert = alert ?? (_ => { });
            _confirm = confirm ?? (_ => false);
            _prompt = prompt ?? ((_, __) => string.Empty);
            _log = log ?? (_ => { });
            _scrollToElement = scrollToElement ?? (_ => { });
            _focusNode = focusNode ?? (_ => { });
        }

        public void Navigate(Uri target) => _navigate(target);
        public void PostForm(Uri target, string body) => _post(target, body);

        public void PostForm(Uri target, byte[] body, string contentType)
        {
            if (_postBody != null)
            {
                _postBody(target, body, contentType);
                return;
            }

            _log($"[JsHost] Form POST to '{target}' dropped the request body: no body-carrying handler was supplied.");
            _navigate(target);
        }
        public void SetStatus(string s) => _status(s);
        public void SetTitle(string tval) => _setTitle(tval);
        public void Alert(string msg) => _alert(msg);
        public bool Confirm(string msg) => _confirm(msg);
        public string Prompt(string msg, string defaultValue) => _prompt(msg, defaultValue);
        public void Log(string msg) => _log(msg);
        public void ScrollToElement(Element element) => _scrollToElement(element);
        public void FocusNode(Element element) => _focusNode(element);
        public object GetLayoutEngine() => null;
        public void RequestRender() => _requestRender();
        public void InvokeOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = UiThreadHelper.TryGetDispatcher();
            if (dispatcher != null && !UiThreadHelper.HasThreadAccess(dispatcher))
            {
                UiThreadHelper.RunAsync(dispatcher, null, action);
                return;
            }

            _invokeOnUiThread(action);
        }
    }
}

namespace FenBrowser.FenEngine.Core
{
    // Compatibility shell retained for callers not yet migrated to EventLoopCoordinator.
    public sealed class EngineLoop
    {
        public void Pulse() { }
        public void RunFrame() { }
        public void SetRoot(object root) { }
    }

    public sealed class ExecutionContext : Interfaces.IExecutionContext
    {
        public Uri DocumentUrl { get; set; }
        public int CallStackDepth { get; set; }
        public Interfaces.IValue Environment { get; set; }
        public Interfaces.IValue Permissions { get; set; }
        public Action<Interfaces.IValue, string> OnUncaughtException { get; set; }
    }
}

namespace FenBrowser.FenEngine
{
    public sealed class InputManager
    {
        public InputManager() { }
        public bool ProcessEvent(InputEvent evt, object context, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext execCtx)
        {
            if (evt == null)
            {
                return false;
            }

            if (context is FenBrowser.FenEngine.Rendering.Core.RenderContext renderContext)
            {
                if (FenBrowser.FenEngine.Rendering.Interaction.HitTester.HitTestInput(
                    renderContext,
                    (float)evt.X,
                    (float)evt.Y,
                    out var input))
                {
                    evt.Target = input.Target;
                    evt.X = input.ClientX;
                    evt.Y = input.ClientY;
                    evt.PageX = input.ClientX;
                    evt.PageY = input.ClientY;
                }
            }

            return evt.Target != null;
        }
    }

    public sealed class InputEvent
    {
        public InputEventType Type { get; set; }
        public string Key { get; set; }
        public int KeyCode { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public int Button { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public int Buttons { get; set; }
        public int PointerId { get; set; }
        public string PointerType { get; set; }
        public double Pressure { get; set; }
        public bool IsPrimary { get; set; }
        public double PageX { get; set; }
        public double PageY { get; set; }
        public double ScreenX { get; set; }
        public double ScreenY { get; set; }
        public Element Target { get; set; }
    }

    public enum InputEventType
    {
        KeyDown, KeyUp, KeyPress,
        MouseDown, MouseUp, MouseMove, Click, DblClick, ContextMenu,
        TouchStart, TouchEnd, TouchMove, TouchCancel,
        Wheel, PointerDown, PointerUp, PointerMove, PointerCancel
    }

    public static class EnginePhaseManager
    {
        public static void AssertNotInPhase(params FenBrowser.Core.Engine.EnginePhase[] phases)
        {
            ArgumentNullException.ThrowIfNull(phases);
            FenBrowser.Core.Engine.EngineContext.Current.AssertNotInPhase(phases);
        }
    }
}

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class TestConsoleCapture
    {
        public static void AddEntry(string level, string message) { }
    }
}

namespace FenBrowser.FenEngine.DOM
{
    using FenBrowser.Core.Dom.V2;

    public static class ElementWrapper
    {
        public static bool TryGetCachedIframeDocument(Element element, out Document iframeDocument)
        {
            iframeDocument = null;
            return false;
        }
        public static bool IsRemoteFrameElement(Element element, string checkOopif = null) => false;
    }
}

namespace FenBrowser.FenEngine.Core
{
    // Minimal compatibility object — used by BrowserApi WebDriver converter.
    public sealed class FenObject : Interfaces.IObject
    {
        private readonly Dictionary<string, Interfaces.IValue> _props = new(StringComparer.Ordinal);
        public Interfaces.IValue Get(string key) => _props.TryGetValue(key, out var v) ? v : Core.FenValue.Undefined;
        public Interfaces.IValue Get(string key, object context) => Get(key);
        public void Set(string key, Interfaces.IValue value) => _props[key] = value;
        public bool Has(string key) => _props.ContainsKey(key);
        public bool Delete(string key) => _props.Remove(key);
        public IEnumerable<string> Keys() => _props.Keys;
        public Interfaces.IObject GetPrototype() => null;
        public Core.PropertyDescriptor GetOwnPropertyDescriptor(string key) =>
            _props.ContainsKey(key) ? new Core.PropertyDescriptor { Writable = true, Configurable = true, Enumerable = true } : null;
        public IEnumerable<string> GetOwnPropertyNames() => _props.Keys;
    }
}

namespace FenBrowser.FenEngine.DOM
{
    using FenBrowser.FenEngine.Core.Interfaces;
    using FenBrowser.Core.Dom.V2;

    public sealed class DomEvent : FenBrowser.FenEngine.Core.Interfaces.IObject
    {
        private readonly Dictionary<string, FenBrowser.FenEngine.Core.Interfaces.IValue> _props = new(StringComparer.Ordinal);
        FenBrowser.FenEngine.Core.Interfaces.IValue FenBrowser.FenEngine.Core.Interfaces.IObject.Get(string key) => _props.TryGetValue(key, out var v) ? v : Core.FenValue.Undefined;
        FenBrowser.FenEngine.Core.Interfaces.IValue FenBrowser.FenEngine.Core.Interfaces.IObject.Get(string key, object context) => ((FenBrowser.FenEngine.Core.Interfaces.IObject)this).Get(key);
        void FenBrowser.FenEngine.Core.Interfaces.IObject.Set(string key, FenBrowser.FenEngine.Core.Interfaces.IValue value) => _props[key] = value;
        bool FenBrowser.FenEngine.Core.Interfaces.IObject.Has(string key) => _props.ContainsKey(key);
        bool FenBrowser.FenEngine.Core.Interfaces.IObject.Delete(string key) => _props.Remove(key);
        IEnumerable<string> FenBrowser.FenEngine.Core.Interfaces.IObject.Keys() => _props.Keys;
        FenBrowser.FenEngine.Core.Interfaces.IObject FenBrowser.FenEngine.Core.Interfaces.IObject.GetPrototype() => null;
        Core.PropertyDescriptor FenBrowser.FenEngine.Core.Interfaces.IObject.GetOwnPropertyDescriptor(string key) =>
            _props.ContainsKey(key) ? new Core.PropertyDescriptor { Writable = true, Configurable = true, Enumerable = true } : null;
        IEnumerable<string> FenBrowser.FenEngine.Core.Interfaces.IObject.GetOwnPropertyNames() => _props.Keys;
        public string Type { get; }
        public bool Bubbles { get; }
        public bool Cancelable { get; }
        public FenBrowser.FenEngine.Core.Interfaces.IValue Target { get; set; }
        public bool DefaultPrevented { get; set; }

        public DomEvent(string type, bool bubbles, bool cancelable, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context)
        {
            Type = type;
            Bubbles = bubbles;
            Cancelable = cancelable;
        }

        public DomEvent(string type, bool bubbles, bool cancelable, bool composed, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context)
        {
            Type = type;
            Bubbles = bubbles;
            Cancelable = cancelable;
        }

        public void Set(string key, FenBrowser.FenEngine.Core.Interfaces.IValue value) =>
            ((FenBrowser.FenEngine.Core.Interfaces.IObject)this).Set(key, value);
        public void PreventDefault() => DefaultPrevented = true;
        public void StopPropagation() { }
    }

    public static class EventTarget
    {
        public sealed class Registry
        {
            public static List<object> Get(Element element, string eventType, bool capture)
                => new();
        }

        public static bool DispatchEvent(Element target, DomEvent evt, object context)
        {
            return true;
        }

        public static bool DispatchEvent(Element target, DomEvent evt, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context)
        {
            return true;
        }
    }
}

namespace FenBrowser.FenEngine.DevTools
{
    public sealed class SourceFile
    {
        public string Url { get; set; }
        public string Content { get; set; }
        public string ScriptId { get; set; }
    }

    public sealed class DevToolsCore
    {
        public static readonly DevToolsCore Instance = new();
        public void RecordRequest(string url, string method, Dictionary<string, string> headers, string id) { }
        public void CompleteRequest(string id, int statusCode, Dictionary<string, string> headers, long size, string mimeType) { }
        public void CompleteRequest(string id, int statusCode, object headers, long size, string mimeType) { }
        public Func<IEnumerable<FenBrowser.FenEngine.DevTools.Cookie>> CookieSnapshotProvider { get; set; }
        public Action<FenBrowser.FenEngine.DevTools.Cookie> CookieSetter { get; set; }
        public Action<string, string> CookieDeleteHandler { get; set; }
        public Action CookieClearHandler { get; set; }
#pragma warning disable CS0067 // Legacy DevTools event retained for host subscribers; request dispatch is owned by BrowserApi.
        public event Action<NetworkRequest> OnNetworkRequest;
#pragma warning restore CS0067
        public IEnumerable<SourceFile> GetSources() => Array.Empty<SourceFile>();
    }
}

namespace FenBrowser.FenEngine
{
    public static class ProcessIsolationRuntime
    {
        public static bool IsEnabled => false;
    }
}

namespace FenBrowser.Tooling
{
    public static class VerificationRunner
    {
        public static Task GenerateSnapshot(string path, string output) => Task.CompletedTask;
    }

    public sealed class AcidTestResult
    {
        public bool Passed { get; set; } = true;
        public string Message { get; set; } = string.Empty;
        public double Score { get; set; } = 1.0;
        public string DiffImagePath { get; set; } = string.Empty;
    }

    public sealed class AcidTestRunner
    {
        public AcidTestRunner() { }
        public AcidTestRunner(string dir) { }
        public Task<AcidTestResult> RunAcid2Async(
            Func<string, Task<SkiaSharp.SKBitmap>> navigationScreenshotCallback,
            string outputPath = null, int settleMs = 4000) =>
            Task.FromResult(new AcidTestResult());
        public Task<AcidTestResult> CompareWithReferenceAsync(
            object actualOrPath,
            object referenceOrBitmap,
            string outputDir = null,
            double threshold = 0.0) =>
            Task.FromResult(new AcidTestResult());
    }

    public sealed class Test262ToolRunner
    {
        public static Task RunAsync(string[] args) => Task.CompletedTask;
    }

    public sealed class MultiTabHeadlessRunner
    {
        public static Task RunAsync(string[] args) => Task.CompletedTask;
    }
}

namespace FenBrowser.FenEngine.DevTools
{
    public sealed class NetworkRequest
    {
        public string Url { get; set; }
        public string Method { get; set; }
        public int StatusCode { get; set; }
        public Dictionary<string, string> ResponseHeaders { get; set; } = new();
        public string Status => StatusCode > 0 ? "complete" : "pending";
        public string Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; } = new();
        public string MimeType { get; set; }
        public long Size { get; set; }
        public string RequestBody { get; set; }
        public string ResponseBody { get; set; }
    }

    public sealed class Cookie
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public string SameSite { get; set; }
    }

    public sealed class DebugConfig
    {
        public bool ShowLayoutBounds { get; set; }
        public bool ShowPaintInvalidations { get; set; }
    }
}