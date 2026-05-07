// SpecRef: ECMA-262 (runtime semantics and abrupt completions), WHATWG HTML scripting integration
// CapabilityId: JS-RUNTIME-EXCEPTION-01
// Determinism: strict
// FallbackPolicy: spec-defined
using FenBrowser.Core.Dom.V2;
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
using FenBrowser.FenEngine.Core.Interfaces;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType; // Added for IExecutionContext
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.FenEngine.Compatibility;
using SkiaSharp;
using FenBrowser.FenEngine.DOM;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// JavaScriptEngine - powered by FenEngine
    /// Provides JavaScript execution with DOM/Web APIs support
    /// </summary>
    public sealed partial class JavaScriptEngine : FenBrowser.FenEngine.Core.IDomBridge
    {
        public string SessionStoragePartitionId => _sessionStoragePartitionId;
        public Func<Uri, Task<string>> FetchOverride { get; set; }
        public Func<Uri, string, bool> SubresourceAllowed { get; set; }
        public Func<string, bool> NonceAllowed { get; set; }
        
        // Permission Request Event
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested;

        // FenEngine runtime
        private FenRuntime _fenRuntime;

#if USE_ECMA_EXPERIMENTAL
        private JsInterpreter _exp;
        public bool UseExperimentalEcmaEngine { get; set; } = false;
#endif
        private readonly IJsHost _host;
        private readonly FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        // Legacy mini runtime instance removed.
        public IExecutionContext GlobalContext => _fenRuntime?.Context;
        private JsContext _ctx;

        public JavaScriptEngine(IJsHost host)
        {
            TryLogDebug("[JavaScriptEngine] Constructor Start");
            _host = host;
            _storageBackend = new FenBrowser.FenEngine.Storage.FileStorageBackend();

            InitRuntime();
            // Service workers share the same storage and centralized network path as the runtime.
            FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.Initialize(
                _storageBackend,
                FetchThroughNetworkHandlerAsync,
                IsWorkerScriptUriAllowed);
            DocumentWrapper.CookieReadBridge = scope => GetCookieString(scope);
            DocumentWrapper.CookieWriteBridge = (scope, cookieString) => SetCookieString(scope, cookieString);
            ElementWrapper.EventDispatchBridge = (element, eventName) =>
            {
                if (element == null || string.IsNullOrWhiteSpace(eventName) || _fenRuntime == null)
                {
                    return;
                }

                DispatchEvent(element, eventName, new DomEvent(eventName, false, false, false, _fenRuntime.Context));
            };
            TryLogDebug("[JavaScriptEngine] Constructor: InitRuntime Done");
            SetupMutationObserver();
            // Legacy mini runtime removed.
        }

        public Func<System.Net.Http.HttpRequestMessage, System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>> FetchHandler { get; set; }



        private FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge _historyBridge;
        
        public void SetHistoryBridge(FenBrowser.FenEngine.Core.Interfaces.IHistoryBridge bridge)
        {
            _historyBridge = bridge;
            if (_fenRuntime != null) _fenRuntime.SetHistoryBridge(bridge);
        }

        public void NotifyPopState(object state)
        {
             _fenRuntime?.NotifyPopState(state);
        }

        private void InitRuntime()
        {
            _ctx = _ctx ?? new JsContext();
            var permissions = CreatePermissionManagerForProfile();
            
            // Wire up the permission handler
            permissions.PermissionRequestedHandler = async (origin, perm) =>
            {
                if (PermissionRequested != null)
                {
                    // Dispatch to UI thread if needed? The handler in MainWindow is likely UI bound.
                    // But here we are in JS thread context potentially.
                    // The event handler in MainWindow should handle thread safety.
                    return await PermissionRequested(origin, perm);
                }
                return false; // Default deny if no handler
            };

            var context = new FenBrowser.FenEngine.Core.ExecutionContext(
                permissions,
                CreateResourceLimitsForProfile());

            // Configure layout engine provider
            context.LayoutEngineProvider = () => _host?.GetLayoutEngine();

            // Configure function execution delegate
            context.ExecuteFunction = (fn, args) => 
            {
                if (!fn.IsFunction)
                {
                    return FenBrowser.FenEngine.Core.FenValue.FromError("Bytecode-only mode: attempted to execute non-function.");
                }

                var function = fn.AsFunction();
                if (function == null)
                {
                    return FenBrowser.FenEngine.Core.FenValue.FromError("Bytecode-only mode: function handle is invalid.");
                }

                return function.Invoke(args, context);
            };
            
            // Configure callbacks to run via EventLoop
            context.ScheduleCallback = (action, delay) =>
            {
                EngineLogCompat.Debug($"[ScheduleCallback] Scheduled for {delay}ms", LogCategory.JavaScript);
                var safeDelay = Math.Max(0, delay);
                FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.ScheduleDelayedTask(
                    () =>
                    {
                        EngineLogCompat.Debug("[EventLoop] Executing scheduled callback", LogCategory.JavaScript);
                        action?.Invoke();
                    },
                    safeDelay,
                    FenBrowser.FenEngine.Core.EventLoop.TaskSource.Timer,
                    "JavaScriptEngine.ScheduleCallback");

                _ = ObserveBackgroundTaskFailureAsync(
                    WakeScheduledCallbackAsync(safeDelay),
                    message => EngineLogCompat.Warn($"[JavaScriptEngine] ScheduleCallbackAsync failed: {message}", LogCategory.JavaScript));
            };

            // Configure Microtasks (Promises)
            context.ScheduleMicrotask = (action) =>
            {
                FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.EnqueueMicrotask(() => action?.Invoke());
            };



            context.OnUncaughtException = (errVal, srcUrl) =>
            {
                try
                {
                    var evt = new FenObject();
                    evt.Set("type", FenValue.FromString("error"));
                    evt.Set("error", errVal);
                    var msg = errVal.IsObject ? errVal.AsObject().Get("message").ToString() : errVal.ToString();
                    if (string.IsNullOrWhiteSpace(msg) && errVal.IsObject) msg = "Error";
                    evt.Set("message", FenValue.FromString(msg));
                    evt.Set("filename", FenValue.FromString(srcUrl ?? string.Empty));
                    evt.Set("lineno", FenValue.FromNumber(1));
                    evt.Set("colno", FenValue.FromNumber(1));

                    var win = _fenRuntime?.GetGlobal("window") ?? FenValue.Undefined;
                    if (win.IsObject)
                    {
                        var winObj = win.AsObject();
                        DispatchEvent(winObj, "error", evt);
                    }
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Failed to dispatch global error event: {ex.Message}", LogCategory.JavaScript);
                }
            };

            context.OnUnhandledRejection = (reason, promise) =>
            {
                try
                {
                    var evt = new FenObject();
                    evt.Set("type", FenValue.FromString("unhandledrejection"));
                    evt.Set("reason", reason);
                    evt.Set("promise", FenValue.FromObject(promise));
                    evt.Set("cancelable", FenValue.True);

                    var win = _fenRuntime?.GetGlobal("window") ?? FenValue.Undefined;
                    if (win.IsObject)
                    {
                        var winObj = win.AsObject();
                        // WPT relies heavily on window.onunhandledrejection
                        DispatchEvent(winObj, "unhandledrejection", evt);
                    }
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Failed to dispatch global unhandledrejection event: {ex.Message}", LogCategory.JavaScript);
                }
            };

            TryLogDebug("[JavaScriptEngine] InitRuntime: Creating FenRuntime...");
            _fenRuntime = new FenRuntime(context, _storageBackend, this);
            _fenRuntime.NetworkFetchHandler = async (req) =>
            {
                if (FetchHandler == null) throw new InvalidOperationException("FetchHandler not configured on engine");
                return await FetchHandler(req).ConfigureAwait(false);
            };
            _fenRuntime.NavigationRequested = uri => TryNavigate(uri);
            TryLogDebug("[JavaScriptEngine] InitRuntime: FenRuntime Created");
            // Connect console messages to BrowserHost
            _fenRuntime.OnConsoleMessage = msg => 
            {
                EngineLogCompat.Debug($"[JavaScriptEngine] Received console message from runtime: {msg}", LogCategory.JavaScript);
                // Console messages are logged via FenLogger and passed to the host
                try { _host?.Log(msg); } catch (Exception ex) { EngineLogCompat.Error($"[JavaScriptEngine] Host log error: {ex}", LogCategory.JavaScript); }
            };

            context.OnMutation = RecordMutation;
            
            if (_host != null)
            {
                _fenRuntime.SetAlert(msg => _host.Alert(msg));
                _fenRuntime.SetConfirmPrompt(
                    msg => _host.Confirm(msg),
                    (msg, defaultValue) => _host.Prompt(msg, defaultValue));
            }
            
            SetupPermissions();
            SetupWindowEvents();
            SetupModernAPIs();
            ApplyRuntimeProfilePostInitialization();
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
                    EngineLogCompat.Warn($"[JavaScriptEngine] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private static Task RunDetached(Action operation)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    operation();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Detached operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        private static async Task ObserveBackgroundTaskFailureAsync(
            Task task,
            Action<string> logMessage,
            Action<Exception> logException = null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logException != null)
                {
                    logException(ex);
                    return;
                }

                logMessage?.Invoke(ex.GetBaseException().Message);
            }
        }

        private static async Task WakeScheduledCallbackAsync(int delay)
        {
            if (delay > 0)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            var eventLoop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;

            void DrainReadyWork()
            {
                while (eventLoop.HasPendingTasks || eventLoop.HasPendingMicrotasks)
                {
                    if (eventLoop.HasPendingTasks)
                    {
                        eventLoop.ProcessNextTask();
                        continue;
                    }

                    eventLoop.PerformMicrotaskCheckpoint();
                }
            }

            // Hostless focused tests still need a wake-up after the delay expires.
            // The callback itself stays owned by the coordinator's timer queue.
            var wakeResult = eventLoop.ProcessNextTaskDetailed();
            DrainReadyWork();

            // Some hostless paths still have older queued work ahead of timer delivery.
            // Give the wake one bounded second chance only when the first turn did not
            // actually process timer work and a delayed task is still pending.
            if (eventLoop.HasPendingDelayedTasks && (!wakeResult.Processed || wakeResult.Source != FenBrowser.FenEngine.Core.EventLoop.TaskSource.Timer))
            {
                await Task.Delay(1).ConfigureAwait(false);
                eventLoop.ProcessNextTask();
                DrainReadyWork();
            }
        }
        // IDomBridge Implementation
        public FenValue GetElementById(string id)
        {
            if (_domRoot  == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var result = doc.getElementById(id);
            return result is IObject obj ? FenValue.FromObject(obj) : FenValue.Null;
        }

        public FenValue QuerySelector(string selector)
        {
             if (_domRoot  == null) return FenValue.Null;
             var doc = new JsDocument(this, _domRoot);
             var result = doc.querySelector(selector);
             return result is IObject obj ? FenValue.FromObject(obj) : FenValue.Null;
        }


        public FenValue GetElementsByTagName(string tagName)
        {
            if (_domRoot == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var results = (doc.getElementsByTagName(tagName) ?? Enumerable.Empty<Element>()).ToArray();
            var arr = new FenObject();
            for (int i = 0; i < results.Length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromObject(new JsDomElement(this, results[i])));
            }
            arr.Set("length", FenValue.FromNumber(results.Length));
            return FenValue.FromObject(arr);
        }

        public FenValue GetElementsByClassName(string classNames)
        {
            if (_domRoot == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var results = (doc.getElementsByClassName(classNames) ?? Enumerable.Empty<Element>()).ToArray();
            var arr = new FenObject();
            for (int i = 0; i < results.Length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromObject(new JsDomElement(this, results[i])));
            }
            arr.Set("length", FenValue.FromNumber(results.Length));
            return FenValue.FromObject(arr);
        }

        public FenValue CreateElementNS(string namespaceUri, string qualifiedName)
        {
            if (_domRoot == null) return FenValue.Null;
            // Namespace URI is currently ignored by this bridge path; DOM wrapper handles qualified name creation.
            return CreateElement(qualifiedName);
        }
        public void AddEventListener(string elementId, string eventName, FenValue callback)
        {
             var elVal = GetElementById(elementId);
             if (elVal.IsObject)
             {
                 AddEventListenerNative(
                     new FenValue[] { FenValue.FromString(eventName), callback },
                     elVal);
             }
        }

        public FenValue CreateElement(string tagName)
        {
            if (_domRoot  == null) return FenValue.Null;
            var doc = new JsDocument(this, _domRoot);
            var el = doc.createElement(tagName) as IObject;
            return el != null ? FenValue.FromObject(el) : FenValue.Null;
        }

        public FenValue CreateTextNode(string text)
        {
             if (_domRoot  == null) return FenValue.Null;
             var doc = new JsDocument(this, _domRoot);
             var txt = doc.createTextNode(text);
             if (txt is IObject obj) return FenValue.FromObject(obj);
             return FenValue.Null; 
        }

        public void AppendChild(FenValue parent, FenValue child)
        {
            if (parent.IsObject && child.IsObject)
            {
                var pObj = parent.AsObject();
                if (pObj is JsDomElement el)
                {
                     el.appendChild(child.AsObject());
                }
            }
        }

        public void SetAttribute(FenValue element, string name, string value)
        {
            if (element.IsObject)
            {
                var obj = element.AsObject();
                if (obj is JsDomElement el)
                {
                    el.setAttribute(name, value);
                }
            }
        }

        private void SetupModernAPIs()
        {
            try 
            {
                EngineLogCompat.Debug("[JavaScriptEngine] Setting up Modern APIs (Proxy, Reflect)...", LogCategory.JavaScript);
                var existingProxy = _fenRuntime.GetGlobal("Proxy");
                if (!existingProxy.IsFunction && !existingProxy.IsObject)
                {
                    _fenRuntime.SetGlobal("Proxy", FenBrowser.FenEngine.Scripting.ProxyAPI.CreateProxyConstructor());
                }

                var existingReflect = _fenRuntime.GetGlobal("Reflect");
                if (!existingReflect.IsFunction && !existingReflect.IsObject)
                {
                    _fenRuntime.SetGlobal("Reflect", FenValue.FromObject(FenBrowser.FenEngine.Scripting.ReflectAPI.CreateReflectObject()));
                }
            }
            catch (Exception ex)
            {
                 EngineLogCompat.Error($"[JavaScriptEngine] Modern API Setup Failed: {ex.Message}", LogCategory.JavaScript, ex);
            }
        }

        public FenValue AddEventListenerNative(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var evt = args[0].ToString();

            var callback = args[1];
            var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
            if (!callbackIsValid || callback.IsUndefined || callback.IsNull || string.IsNullOrWhiteSpace(evt)) return FenValue.Undefined;

            bool capture = false;
            bool once = false;
            bool passive = false;
            ParseListenerOptions(args, 2, ref capture, ref once, ref passive);

            try { EngineLogCompat.Debug($"[JS_API] addEventListener called for '{evt}' on {thisVal.ToString()} capture={capture} once={once}", LogCategory.JavaScript); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

            var key = NormalizeEventTargetKey(thisVal.IsObject ? thisVal.AsObject() : null);
            if (key == null) return FenValue.Undefined;

            if (!_objectEventListeners.TryGetValue(key, out var listeners))
            {
                listeners = new Dictionary<string, List<ObjectEventListener>>(StringComparer.OrdinalIgnoreCase);
                _objectEventListeners.Add(key, listeners);
            }

            if (!listeners.TryGetValue(evt, out var list))
            {
                list = new List<ObjectEventListener>();
                listeners[evt] = list;
            }

            var exists = list.Any(l => l.Callback.Equals(callback) && l.Capture == capture);
            if (!exists)
            {
                list.Add(new ObjectEventListener
                {
                    Callback = callback,
                    Capture = capture,
                    Once = once,
                    Passive = passive
                });
            }

            return FenValue.Undefined;
        }

        public FenValue RemoveEventListenerNative(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var evt = args[0].ToString();

            var callback = args[1];
            var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
            if (!callbackIsValid || callback.IsUndefined || callback.IsNull || string.IsNullOrWhiteSpace(evt)) return FenValue.Undefined;

            bool capture = false;
            bool once = false;
            bool passive = false;
            ParseListenerOptions(args, 2, ref capture, ref once, ref passive);

            var key = NormalizeEventTargetKey(thisVal.IsObject ? thisVal.AsObject() : null);
            if (key == null) return FenValue.Undefined;

            if (_objectEventListeners.TryGetValue(key, out var listeners) && listeners.TryGetValue(evt, out var list))
            {
                list.RemoveAll(l => l.Callback.Equals(callback) && l.Capture == capture);
            }

            return FenValue.Undefined;
        }

        /// <summary>
        /// Dispatch a mechanism event to stored listeners on the target.
        /// </summary>
        public void DispatchEvent(object target, string eventName, FenObject eventArgs = null)
        {
            if (target == null || string.IsNullOrEmpty(eventName)) return;

            object key = NormalizeEventTargetKey(target);
            if (key == null) return;

            if (eventArgs is DomEvent domEvent && _fenRuntime?.Context != null)
            {
                InvokeObjectListenersForDomEvent(target, domEvent, _fenRuntime.Context, isCapturePhase: true, atTargetPhase: true);
                if (!domEvent.ImmediatePropagationStopped)
                {
                    InvokeObjectListenersForDomEvent(target, domEvent, _fenRuntime.Context, isCapturePhase: false, atTargetPhase: true);
                }
                InvokeEventPropertyHandler(target, eventName, domEvent);
                InvokeInlineEventAttributeHandler(target, eventName, domEvent);
                return;
            }

            if (_objectEventListeners.TryGetValue(key, out var listeners) && listeners.TryGetValue(eventName, out var list))
            {
                var snapshot = list.ToArray();

                var thisBinding = target as IObject;
                if (thisBinding == null && key is Element domEl)
                {
                    thisBinding = new JsDomElement(this, domEl);
                }

                var args = new FenValue[]
                {
                    eventArgs != null ? FenValue.FromObject(eventArgs) : FenValue.Undefined
                };

                foreach (var listener in snapshot)
                {
                    try
                    {
                        var thisArg = thisBinding != null ? FenValue.FromObject(thisBinding) : FenValue.Undefined;
                        FenFunction callbackFn = null;
                        var callbackThis = thisArg;
                        if (listener.Callback.IsFunction)
                        {
                            callbackFn = listener.Callback.AsFunction() as FenFunction;
                        }
                        else if (listener.Callback.IsObject)
                        {
                            var handleEvent = listener.Callback.AsObject()?.Get("handleEvent") ?? FenValue.Undefined;
                            if (handleEvent.IsFunction)
                            {
                                callbackFn = handleEvent.AsFunction() as FenFunction;
                                callbackThis = listener.Callback;
                            }
                        }

                        if (callbackFn == null)
                        {
                            continue;
                        }

                        callbackFn.Invoke(args, _fenRuntime.Context, callbackThis);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Error($"[DispatchEvent] Error in handler for {eventName}: {ex}", LogCategory.JavaScript);
                    }
                }
            }

            InvokeEventPropertyHandler(target, eventName, eventArgs);
            InvokeInlineEventAttributeHandler(target, eventName, eventArgs);
        }

        private void InvokeEventPropertyHandler(object target, string eventName, FenObject eventArgs = null)
        {
            if (_fenRuntime == null || string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            IObject thisBinding = target as IObject;
            if (thisBinding == null && target is Node domNode)
            {
                var wrappedTarget = DomWrapperFactory.Wrap(domNode, _fenRuntime.Context);
                if (wrappedTarget.IsObject)
                {
                    thisBinding = wrappedTarget.AsObject();
                }
            }

            if (thisBinding == null)
            {
                return;
            }

            var propertyName = "on" + eventName.ToLowerInvariant();
            var handler = thisBinding.Get(propertyName, _fenRuntime.Context);
            if (!handler.IsFunction)
            {
                return;
            }

            var domEvent = eventArgs as DomEvent ?? new DomEvent(eventName, false, false, false, _fenRuntime.Context);
            SetEventCurrentTargetForObject(domEvent, thisBinding, target, _fenRuntime.Context);

            try
            {
                handler.AsFunction().Invoke(new[] { FenValue.FromObject(domEvent) }, _fenRuntime.Context, FenValue.FromObject(thisBinding));
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[DispatchEvent] Error in property handler for {eventName}: {ex}", LogCategory.JavaScript);
            }
        }

        private void InvokeInlineEventAttributeHandler(object target, string eventName, FenObject eventArgs = null)
        {
            if (_fenRuntime == null || string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            var normalizedTarget = NormalizeEventTargetKey(target);
            if (normalizedTarget is not Element element)
            {
                return;
            }

            var inlineHandler = element.GetAttribute("on" + eventName.ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(inlineHandler))
            {
                return;
            }

            var targetValue = DomWrapperFactory.Wrap(element, _fenRuntime.Context);
            var previousEvent = _fenRuntime.GetGlobal("event");
            var previousThis = _fenRuntime.GetGlobal("__fen_inline_this");
            var eventObject = eventArgs as DomEvent ?? new DomEvent(eventName, false, false, false, _fenRuntime.Context);

            try
            {
                if (targetValue.IsObject)
                {
                    eventObject.Target = element;
                    eventObject.CurrentTarget = element;
                    eventObject.UpdateJsProperties(_fenRuntime.Context);
                }

                _fenRuntime.SetGlobal("event", FenValue.FromObject(eventObject));
                _fenRuntime.SetGlobal("__fen_inline_this", targetValue.IsUndefined ? FenValue.Null : targetValue);
                RunInline($"(function(){{{inlineHandler}}}).call(__fen_inline_this);", _ctx, eventName, element.TagName?.ToLowerInvariant() ?? "element");
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[InlineEvent] Failed to execute inline {eventName} handler on <{element.TagName}>: {ex.Message}", LogCategory.JavaScript);
            }
            finally
            {
                _fenRuntime.SetGlobal("event", previousEvent is FenValue previousEventValue ? previousEventValue : FenValue.Undefined);
                _fenRuntime.SetGlobal("__fen_inline_this", previousThis is FenValue previousThisValue ? previousThisValue : FenValue.Undefined);
            }
        }

        private void InvokeObjectListenersForDomEvent(object target, DomEvent evt, IExecutionContext context, bool isCapturePhase, bool atTargetPhase)
        {
            if (target == null || evt == null || string.IsNullOrWhiteSpace(evt.Type)) return;
            if (evt.ImmediatePropagationStopped) return;

            var key = NormalizeEventTargetKey(target);
            if (key == null) return;

            var thisBinding = ResolveEventCurrentTargetBinding(target, key, context);

            SetEventCurrentTargetForObject(evt, thisBinding, target, context);

            if (_objectEventListeners.TryGetValue(key, out var listeners) && listeners.TryGetValue(evt.Type, out var list) && list.Count > 0)
            {
                var snapshot = list.ToArray();
                foreach (var listener in snapshot)
                {
                    if (evt.ImmediatePropagationStopped) break;
                    if (listener.Capture != isCapturePhase) continue;

                    try
                    {
                        var thisArg = thisBinding != null ? FenValue.FromObject(thisBinding) : FenValue.Undefined;
                        FenFunction callbackFn = null;
                        var callbackThis = thisArg;
                        if (listener.Callback.IsFunction)
                        {
                            callbackFn = listener.Callback.AsFunction() as FenFunction;
                        }
                        else if (listener.Callback.IsObject)
                        {
                            var handleEvent = listener.Callback.AsObject()?.Get("handleEvent") ?? FenValue.Undefined;
                            if (handleEvent.IsFunction)
                            {
                                callbackFn = handleEvent.AsFunction() as FenFunction;
                                callbackThis = listener.Callback;
                            }
                        }

                        if (callbackFn == null)
                        {
                            continue;
                        }

                        callbackFn.Invoke(new[] { FenValue.FromObject(evt) }, context, callbackThis);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Error($"[DispatchEvent] Error in DOM handler for {evt.Type}: {ex}", LogCategory.JavaScript);
                    }

                    if (listener.Once)
                    {
                        list.RemoveAll(l => l.Callback.Equals(listener.Callback) && l.Capture == listener.Capture);
                    }
                }
            }

            InvokeFenTargetListenersForDomEvent(thisBinding, evt, context, isCapturePhase);
        }

        private IObject ResolveEventCurrentTargetBinding(object target, object normalizedKey, IExecutionContext context)
        {
            if (target is IObject directTarget)
            {
                return directTarget;
            }

            if (normalizedKey is Node domNode)
            {
                var wrapped = DomWrapperFactory.Wrap(domNode, context);
                if (wrapped.IsObject)
                {
                    return wrapped.AsObject();
                }
            }

            if (normalizedKey is Element domElement)
            {
                return new JsDomElement(this, domElement);
            }

            return null;
        }

        private void InvokeFenTargetListenersForDomEvent(IObject targetObj, DomEvent evt, IExecutionContext context, bool isCapturePhase)
        {
            if (targetObj == null || evt == null) return;

            var listenersVal = targetObj.Get("__fen_listeners__");
            if (!listenersVal.IsObject) return;

            var listenersObj = listenersVal.AsObject() as FenObject;
            if (listenersObj == null) return;

            var arrVal = listenersObj.Get(evt.Type);
            if (!arrVal.IsObject) return;

            var arr = arrVal.AsObject() as FenObject;
            if (arr == null) return;

            int len = (int)arr.Get("length").ToNumber();
            for (int i = 0; i < len; i++)
            {
                if (evt.ImmediatePropagationStopped) break;

                var entryVal = arr.Get(i.ToString());
                if (!entryVal.IsObject) continue;

                var entry = entryVal.AsObject();
                var callback = entry.Get("callback");
                FenFunction callbackFn = null;
                var callbackThis = FenValue.FromObject(targetObj);
                if (callback.IsFunction)
                {
                    callbackFn = callback.AsFunction() as FenFunction;
                }
                else if (callback.IsObject)
                {
                    var handleEvent = callback.AsObject()?.Get("handleEvent") ?? FenValue.Undefined;
                    if (handleEvent.IsFunction)
                    {
                        callbackFn = handleEvent.AsFunction() as FenFunction;
                        callbackThis = callback;
                    }
                }

                if (callbackFn == null) continue;

                var captureVal = entry.Get("capture");
                var capture = captureVal.IsBoolean && captureVal.ToBoolean();
                if (capture != isCapturePhase) continue;

                try
                {
                    callbackFn.Invoke(new[] { FenValue.FromObject(evt) }, context, callbackThis);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Error($"[DispatchEvent] Error in __fen_listeners__ handler for {evt.Type}: {ex}", LogCategory.JavaScript);
                }

                var onceVal = entry.Get("once");
                if (onceVal.IsBoolean && onceVal.ToBoolean())
                {
                    // Compact array by shifting left.
                    for (int j = i + 1; j < len; j++)
                    {
                        arr.Set((j - 1).ToString(), arr.Get(j.ToString()));
                    }

                    len--;
                    arr.Delete(len.ToString());
                    arr.Set("length", FenValue.FromNumber(len));
                    i--;
                }
            }
        }

        private void SetEventCurrentTargetForObject(DomEvent evt, IObject thisBinding, object target, IExecutionContext context)
        {
            try
            {
                if (thisBinding != null)
                {
                    evt.Set("currentTarget", FenValue.FromObject(thisBinding));
                    return;
                }

                if (target is IObject obj)
                {
                    evt.Set("currentTarget", FenValue.FromObject(obj));
                    return;
                }

                evt.Set("currentTarget", FenValue.Null);
                evt.UpdateJsProperties(context);
            }
            catch
            {
            }
        }

        private object ResolveDocumentEventTarget(Element target)
        {
            var envDoc = _fenRuntime?.Context?.Environment?.Get("document") ?? FenValue.Undefined;
            var sourceDocument = target?.OwnerDocument;
            if (sourceDocument != null)
            {
                if (envDoc.IsObject && envDoc.AsObject() is DocumentWrapper envDocumentWrapper &&
                    ReferenceEquals(envDocumentWrapper.Node, sourceDocument))
                {
                    return envDoc.AsObject();
                }

                if (_fenRuntime?.Context != null)
                {
                    var wrappedDocument = DomWrapperFactory.Wrap(sourceDocument, _fenRuntime.Context);
                    if (wrappedDocument.IsObject)
                    {
                        return wrappedDocument.AsObject();
                    }
                }
            }

            if (envDoc.IsObject)
            {
                return envDoc.AsObject();
            }

            var globalDoc = _fenRuntime?.GetGlobal("document") ?? FenValue.Undefined;
            return globalDoc.IsObject ? globalDoc.AsObject() : null;
        }

        private object ResolveWindowEventTarget(Element target)
        {
            var envWindow = _fenRuntime?.Context?.Environment?.Get("window") ?? FenValue.Undefined;
            if (envWindow.IsObject)
            {
                return envWindow.AsObject();
            }

            var globalWindow = _fenRuntime?.GetGlobal("window") ?? FenValue.Undefined;
            return globalWindow.IsObject ? globalWindow.AsObject() : null;
        }

        private static object NormalizeEventTargetKey(object target)
        {
            if (target == null) return null;
            if (target is JsDomElement elWrapper) return elWrapper._node;
            if (target is JsDomText textWrapper) return textWrapper._node;
            return target;
        }

        private static void ParseListenerOptions(FenValue[] args, int optionIndex, ref bool capture, ref bool once, ref bool passive)
        {
            if (args.Length <= optionIndex) return;
            var options = args[optionIndex];

            if (options.IsBoolean)
            {
                capture = options.ToBoolean();
                return;
            }

            if (!options.IsObject) return;
            var opts = options.AsObject() as FenObject;
            if (opts == null) return;

            var cVal = opts.Get("capture");
            if (cVal.IsBoolean) capture = cVal.ToBoolean();

            var oVal = opts.Get("once");
            if (oVal.IsBoolean) once = oVal.ToBoolean();

            var pVal = opts.Get("passive");
            if (pVal.IsBoolean) passive = pVal.ToBoolean();
        }

        /// <summary>
        /// Dispatches an event with bubbling (Element -> Parent -> ... -> Document -> Window)
        /// </summary>
        public void DispatchEventForElement(Element el, string eventName)
        {
            if (el == null) return;

            try
            {
                // Create Event Object
                // In a perfect world we reuse the same object and update currentTarget.
                var jsEl = new JsDomElement(this, el);

                var evtObj = new FenBrowser.FenEngine.Core.FenObject();
                evtObj.Set("type", FenValue.FromString(eventName));
                evtObj.Set("target", FenValue.FromObject(jsEl));
                evtObj.Set("timeStamp", FenValue.FromNumber((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
                evtObj.Set("bubbles", FenValue.FromBoolean(true));

                // 1. Capture Phase (skipped for now)

                // 2. Target Phase & 3. Bubble Phase
                var current = el;
                while (current != null)
                {
                    // Update currentTarget if we were reusing event object
                    // For now, simple dispatch
                    DispatchEvent(current, eventName, evtObj);
                    current = current.ParentElement;
                }

                // 4. Document & Window
                var doc = _fenRuntime.GetGlobal("document");
                if (doc.IsObject)
                {
                    var dObj = doc.AsObject();
                    object dNative = dObj;
                    if (dObj is FenObject fo) dNative = fo.NativeObject;
                    else if (dObj is JsDocument jd) dNative = jd.NativeObject;
                    DispatchEvent(dNative ?? dObj, eventName, evtObj);
                }

                var win = _fenRuntime.GetGlobal("window");
                if (win.IsObject)
                {
                    var wObj = win.AsObject();
                    object wNative = wObj;
                    if (wObj is FenObject fo) wNative = fo.NativeObject;
                    DispatchEvent(wNative ?? wObj, eventName, evtObj);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[DispatchEventForElement] Failed: {ex}", LogCategory.JavaScript);
            }
        }

        private sealed class ObjectEventListener
        {
            public FenValue Callback;
            public bool Capture;
            public bool Once;
            public bool Passive;
        }

        private void SetupWindowEvents()
        {
            try { EngineLogCompat.Debug("[SetupWindowEvents] Configuring window/document events", LogCategory.JavaScript); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            
            // Ensure 'window' exists
            var win = _fenRuntime.GetGlobal("window");
            FenBrowser.FenEngine.Core.FenObject winObj;
            if (win.IsObject) winObj = (FenBrowser.FenEngine.Core.FenObject)win.AsObject();
            else 
            {
                winObj = new FenBrowser.FenEngine.Core.FenObject();
                _fenRuntime.SetGlobal("window", FenValue.FromObject(winObj));
            }
            // Alias self -> window (Critical for WPT testharness.js)
            _fenRuntime.SetGlobal("self", FenValue.FromObject(winObj));
            
            // Preserve runtime EventTarget methods if already present.
            var docValue = _fenRuntime.GetGlobal("document");
            FenValue docFenValue;
            if (!docValue.IsObject)
            {
                var docWrapper = new JsDocument(this, null); // Fallback only
                docFenValue = FenValue.FromObject(docWrapper);
                _fenRuntime.SetGlobal("document", docFenValue);
            }
            else
            {
                docFenValue = FenValue.FromObject(docValue.AsObject());
            }
            winObj.Set("document", docFenValue);

            // [Compliance] Window Dimensions
            winObj.Set("innerWidth", FenValue.FromNumber(WindowWidth));
            winObj.Set("innerHeight", FenValue.FromNumber(WindowHeight));
            winObj.Set("outerWidth", FenValue.FromNumber(WindowWidth)); // Simplified
            winObj.Set("outerHeight", FenValue.FromNumber(WindowHeight));
            winObj.Set("screenX", FenValue.FromNumber(0));
            winObj.Set("screenY", FenValue.FromNumber(0));
            _fenRuntime.SetGlobal("innerWidth", FenValue.FromNumber(WindowWidth));
            _fenRuntime.SetGlobal("innerHeight", FenValue.FromNumber(WindowHeight));
            _fenRuntime.SetGlobal("outerWidth", FenValue.FromNumber(WindowWidth));
            _fenRuntime.SetGlobal("outerHeight", FenValue.FromNumber(WindowHeight));
            
            // [Compliance] Screen Object
            var screenObj = new FenBrowser.FenEngine.Core.FenObject();
            screenObj.Set("width", FenValue.FromNumber(ScreenWidth));
            screenObj.Set("height", FenValue.FromNumber(ScreenHeight));
            screenObj.Set("availWidth", FenValue.FromNumber(ScreenWidth));
            screenObj.Set("availHeight", FenValue.FromNumber(ScreenHeight - 40)); // Taskbar?
            screenObj.Set("colorDepth", FenValue.FromNumber(24));
            screenObj.Set("pixelDepth", FenValue.FromNumber(24));
            
            winObj.Set("screen", FenValue.FromObject(screenObj));
            _fenRuntime.SetGlobal("screen", FenValue.FromObject(screenObj));
            ApplyBrowserSurfaceToRuntime();
            InstallWimbCapabilities(winObj);
            InstallClipboardJsStub(winObj);
            
            var notificationGlobal = _fenRuntime.GetGlobal("Notification");
            if (notificationGlobal is FenValue notificationGlobalValue &&
                (notificationGlobalValue.IsFunction || notificationGlobalValue.IsObject))
            {
                winObj.Set("Notification", notificationGlobalValue);
            }

            var intersectionObserverGlobal = _fenRuntime.GetGlobal("IntersectionObserver");
            if (intersectionObserverGlobal is FenValue intersectionObserverGlobalValue &&
                (intersectionObserverGlobalValue.IsFunction || intersectionObserverGlobalValue.IsObject))
            {
                winObj.Set("IntersectionObserver", intersectionObserverGlobalValue);
            }

            var resizeObserverGlobal = _fenRuntime.GetGlobal("ResizeObserver");
            if (resizeObserverGlobal is FenValue resizeObserverGlobalValue &&
                (resizeObserverGlobalValue.IsFunction || resizeObserverGlobalValue.IsObject))
            {
                winObj.Set("ResizeObserver", resizeObserverGlobalValue);
            }
            // Bridge object listeners into DOM event flow for window/document and native object listeners.
            FenBrowser.FenEngine.DOM.EventTarget.ExternalListenerInvoker = InvokeObjectListenersForDomEvent;
            FenBrowser.FenEngine.DOM.EventTarget.ResolveDocumentTarget = ResolveDocumentEventTarget;
            FenBrowser.FenEngine.DOM.EventTarget.ResolveWindowTarget = ResolveWindowEventTarget;

             // Note: DocumentWrapper now exposes addEventListener natively via Get/Has/Keys.
             // We don't need to overwrite it here.
        }

        private void InstallWimbCapabilities(FenObject winObj)
        {
            if (winObj == null || _fenRuntime == null)
            {
                return;
            }

            var capabilities = new FenObject();
            MergeBrowserCapabilitiesSnapshot(capabilities);

            var wimbCapabilities = new FenObject();
            wimbCapabilities.Set("capabilities", FenValue.FromObject(capabilities));

            FenValue addFnValue = FenValue.Undefined;
            addFnValue = FenValue.FromFunction(new FenFunction("add", (args, ctx) =>
            {
                MergeBrowserCapabilitiesSnapshot(capabilities);
                if (args.Length >= 2)
                {
                    var name = args[0].ToString()?.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        capabilities.Set(name, FenValue.FromString(NormalizeCapabilityValue(args[1])));
                    }
                }

                return FenValue.Undefined;
            }));

            wimbCapabilities.Set("add", addFnValue);
            wimbCapabilities.Set("add_update", addFnValue);
            wimbCapabilities.Set("refresh", FenValue.FromFunction(new FenFunction("refresh", (args, ctx) =>
            {
                MergeBrowserCapabilitiesSnapshot(capabilities);
                return FenValue.FromObject(wimbCapabilities);
            })));
            wimbCapabilities.Set("get_as_json_string", FenValue.FromFunction(new FenFunction("get_as_json_string", (args, ctx) =>
            {
                MergeBrowserCapabilitiesSnapshot(capabilities);
                return FenValue.FromString(Uri.EscapeDataString(JsonSerializer.Serialize(ReadCapabilityMap(capabilities))));
            })));

            var wimbValue = FenValue.FromObject(wimbCapabilities);
            winObj.Set("WIMB_CAPABILITIES", wimbValue);
            _fenRuntime.SetGlobal("WIMB_CAPABILITIES", wimbValue);
        }

        private void InstallClipboardJsStub(FenObject winObj)
        {
            if (winObj == null || _fenRuntime == null)
            {
                return;
            }

            var clipboardCtor = new FenFunction("ClipboardJS", (args, ctx) =>
            {
                var instance = new FenObject();
                instance.Set("on", FenValue.FromFunction(new FenFunction("on", (listenerArgs, listenerCtx) => FenValue.FromObject(instance))));
                instance.Set("destroy", FenValue.FromFunction(new FenFunction("destroy", (listenerArgs, listenerCtx) => FenValue.Undefined)));
                return FenValue.FromObject(instance);
            });
            clipboardCtor.Set("isSupported", FenValue.FromFunction(new FenFunction("isSupported", (args, ctx) => FenValue.FromBoolean(false))));

            var clipboardValue = FenValue.FromFunction(clipboardCtor);
            winObj.Set("ClipboardJS", clipboardValue);
            _fenRuntime.SetGlobal("ClipboardJS", clipboardValue);
        }

        private void MergeBrowserCapabilitiesSnapshot(FenObject capabilities)
        {
            if (capabilities == null)
            {
                return;
            }

            foreach (var entry in BuildBrowserCapabilitiesSnapshot())
            {
                capabilities.Set(entry.Key, FenValue.FromString(entry.Value));
            }
        }

        private Dictionary<string, string> BuildBrowserCapabilitiesSnapshot()
        {
            var surface = BuildBrowserSurface();

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["javascript"] = "1",
                ["cookies"] = "1",
                ["window_width"] = FormatCapabilityNumber(WindowWidth),
                ["window_height"] = FormatCapabilityNumber(WindowHeight),
                ["screen_width"] = FormatCapabilityNumber(ScreenWidth),
                ["screen_height"] = FormatCapabilityNumber(ScreenHeight),
                ["device_pixel_ratio"] = FormatCapabilityNumber(surface.Viewport.DevicePixelRatio),
                ["local_storage"] = "1",
                ["session_storage"] = "1",
                ["java"] = "0",
                ["language"] = surface.Language,
                ["platform"] = surface.PlatformToken,
                ["user_agent"] = surface.UserAgent,
            };
        }

        private BrowserSurfaceProfile BuildBrowserSurface()
        {
            var metrics = BrowserViewportMetrics.Create(
                WindowWidth,
                WindowHeight,
                ScreenWidth,
                ScreenHeight,
                ScreenWidth,
                ScreenHeight > 40 ? ScreenHeight - 40 : ScreenHeight,
                devicePixelRatio: 1);

            return BrowserSettings.GetBrowserSurface(BrowserSettings.Instance.SelectedUserAgent, metrics);
        }

        private void ApplyBrowserSurfaceToRuntime()
        {
            if (_fenRuntime == null)
            {
                return;
            }

            _fenRuntime.ApplyBrowserSurface(BuildBrowserSurface());
        }

        private static Dictionary<string, string> ReadCapabilityMap(FenObject capabilities)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (capabilities == null)
            {
                return map;
            }

            foreach (var key in capabilities.GetOwnPropertyNames())
            {
                var value = capabilities.Get(key);
                if (value.IsFunction || value.IsObject)
                {
                    continue;
                }

                map[key] = NormalizeCapabilityValue(value);
            }

            return map;
        }

        private static string NormalizeCapabilityValue(FenValue value)
        {
            if (value.IsBoolean)
            {
                return value.ToBoolean() ? "1" : "0";
            }

            if (value.IsNumber)
            {
                return FormatCapabilityNumber(value.ToNumber());
            }

            if (value.IsNull || value.IsUndefined)
            {
                return string.Empty;
            }

            return value.ToString() ?? string.Empty;
        }

        private static string FormatCapabilityNumber(double value)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }

        private double _windowWidth = 1280;
        private double _windowHeight = 720;
        private double _screenWidth = 1920;
        private double _screenHeight = 1080;

        public double WindowWidth
        {
            get => _windowWidth;
            set
            {
                _windowWidth = value;
                ApplyBrowserSurfaceToRuntime();
            }
        }

        public double WindowHeight
        {
            get => _windowHeight;
            set
            {
                _windowHeight = value;
                ApplyBrowserSurfaceToRuntime();
            }
        }

        public double ScreenWidth
        {
            get => _screenWidth;
            set
            {
                _screenWidth = value;
                ApplyBrowserSurfaceToRuntime();
            }
        }

        public double ScreenHeight
        {
            get => _screenHeight;
            set
            {
                _screenHeight = value;
                ApplyBrowserSurfaceToRuntime();
            }
        }


        // timers
        private readonly Dictionary<int, System.Threading.Timer> _timers = new Dictionary<int, System.Threading.Timer>();
        // fields restored
        private int _nextTimerId = 1;
        private readonly Dictionary<int, CancellationTokenSource> _geolocationWatches = new Dictionary<int, CancellationTokenSource>();
        private readonly object _geolocationWatchLock = new object();
        private int _nextGeolocationWatchId = 0;
        private List<MutationObserverWrapper> _fenMutationObservers = new List<MutationObserverWrapper>();
        private List<string> _mutationObservers = new List<string>(); // Legacy observers
        private List<MutationRecord> _pendingMutations = new List<MutationRecord>();
        private bool _repaintRequested = false;
        private List<Uri> _history = new List<Uri>();
        private int _historyIndex = -1;
        private string _docTitle = "";
        
        // script cache
        private object _responseLock = new object();

        private Dictionary<string, ResponseEntry> _responseRegistry = new Dictionary<string, ResponseEntry>();
        private int _responseCapacity = 100;
        private LinkedList<string> _responseLru = new LinkedList<string>();
        // private int _scriptCap = 0; // Removing unused
        private int _inlineThreshold = 1024;
        

        private void SetupMutationObserver()
        {
            var moConstructor = new FenFunction("MutationObserver", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                    return FenValue.FromError("MutationObserver constructor requires a callback function");

                // Use the new DOM-compliant wrapper
                var callback = args[0].AsFunction();
                var wrapper = new MutationObserverWrapper(callback);

                lock (_mutationLock)
                {
                    _fenMutationObservers.Add(wrapper);
                }

                // Return the FenObject interface provided by the wrapper
                return FenValue.FromObject(wrapper.ToFenObject(_fenRuntime.Context));
            });

            _fenRuntime.SetGlobal("MutationObserver", FenValue.FromFunction(moConstructor));
        }

        private void SetupPermissions()
        {
            // 1. navigator.permissions
            var permissionsObj = new FenBrowser.FenEngine.Core.FenObject();
            permissionsObj.Set("query", FenValue.FromFunction(new FenFunction("query", (args, thisVal) =>
            {
                string ResolvePermissionState(string permissionName, out bool supported)
                {
                    supported = true;
                    var originKey = OriginKey(_ctx?.BaseUri);

                    JsPermissions permission = permissionName switch
                    {
                        "geolocation" => JsPermissions.Geolocation,
                        "notifications" => JsPermissions.Notifications,
                        "camera" => JsPermissions.Camera,
                        "microphone" => JsPermissions.Camera,
                        "serial" => JsPermissions.Serial,
                        "clipboard-read" => JsPermissions.None,
                        "clipboard-write" => JsPermissions.None,
                        _ => JsPermissions.None
                    };

                    if (permission != JsPermissions.None)
                    {
                        return PermissionStore.Instance.GetState(originKey, permission).ToString().ToLowerInvariant();
                    }

                    if (permissionName == "clipboard-read" || permissionName == "clipboard-write")
                    {
                        return "prompt";
                    }

                    supported = false;
                    return "denied";
                }

                FenObject CreatePermissionStatusObject(string permissionName, string state)
                {
                    var statusObj = new FenBrowser.FenEngine.Core.FenObject();
                    statusObj.Set("name", FenValue.FromString(permissionName));
                    statusObj.Set("state", FenValue.FromString(state));
                    statusObj.Set("onchange", FenValue.Null);
                    statusObj.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (listenerArgs, listenerThis) => FenValue.Undefined)));
                    statusObj.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (listenerArgs, listenerThis) => FenValue.Undefined)));
                    statusObj.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (listenerArgs, listenerThis) => FenValue.FromBoolean(false))));
                    return statusObj;
                }

                // Returns a Thenable (Promise-like)
                var thenable = new FenBrowser.FenEngine.Core.FenObject();
                FenValue resolvedValue = FenValue.Undefined;
                FenValue rejectedValue = FenValue.Undefined;
                var settled = false;
                var fulfilled = false;

                void ResolveThenable(FenValue value)
                {
                    settled = true;
                    fulfilled = true;
                    resolvedValue = value;
                }

                void RejectThenable(FenValue value)
                {
                    settled = true;
                    fulfilled = false;
                    rejectedValue = value;
                }

                thenable.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                {
                    var onFulfilled = ((thenArgs != null && thenArgs.Length > 0 && thenArgs[0].IsFunction) ? thenArgs[0].AsFunction() : null);
                    var onRejected = ((thenArgs != null && thenArgs.Length > 1 && thenArgs[1].IsFunction) ? thenArgs[1].AsFunction() : null);
                    if (settled)
                    {
                        if (fulfilled)
                        {
                            if (onFulfilled != null)
                            {
                                _fenRuntime.Context.ScheduleCallback(() =>
                                {
                                    TryInvokeFunction(onFulfilled, new[] { resolvedValue }, _fenRuntime.Context, "permissions.query.then");
                                }, 0);
                            }
                        }
                        else if (onRejected != null)
                        {
                            _fenRuntime.Context.ScheduleCallback(() =>
                            {
                                TryInvokeFunction(onRejected, new[] { rejectedValue }, _fenRuntime.Context, "permissions.query.then.reject");
                            }, 0);
                        }

                        return FenValue.FromObject(thenable);
                    }

                    var desc = (args.Length > 0 && args[0].IsObject) ? args[0].AsObject() : null;
                    _ = RunDetached(() =>
                    {
                        try
                        {
                            if (desc == null)
                            {
                                RejectThenable(FenValue.FromError("TypeError: Permission descriptor object is required"));
                            }
                            else
                            {
                                var nameValue = desc.Get("name");
                                if (nameValue.IsUndefined || nameValue.IsNull)
                                {
                                    RejectThenable(FenValue.FromError("TypeError: Permission descriptor name is required"));
                                }
                                else
                                {
                                    var name = nameValue.ToString().Trim().ToLowerInvariant();
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        RejectThenable(FenValue.FromError("TypeError: Permission descriptor name is required"));
                                    }
                                    else
                                    {
                                        var state = ResolvePermissionState(name, out var supported);
                                        if (!supported)
                                        {
                                            RejectThenable(FenValue.FromError($"NotSupportedError: Permission '{name}' is not supported"));
                                        }
                                        else
                                        {
                                            ResolveThenable(FenValue.FromObject(CreatePermissionStatusObject(name, state)));
                                        }
                                    }
                                }
                            }

                            _fenRuntime.Context.ScheduleCallback(() =>
                            {
                                if (fulfilled)
                                {
                                    if (onFulfilled != null)
                                    {
                                        TryInvokeFunction(onFulfilled, new[] { resolvedValue }, _fenRuntime.Context, "permissions.query.then");
                                    }
                                }
                                else if (onRejected != null)
                                {
                                    TryInvokeFunction(onRejected, new[] { rejectedValue }, _fenRuntime.Context, "permissions.query.then.reject");
                                }
                            }, 0);
                        }
                        catch (Exception ex)
                        {
                            RejectThenable(FenValue.FromError($"Error: {ex.Message}"));
                            if (onRejected != null)
                            {
                                _fenRuntime.Context.ScheduleCallback(() =>
                                {
                                    TryInvokeFunction(onRejected, new[] { rejectedValue }, _fenRuntime.Context, "permissions.query.catch");
                                }, 0);
                            }

                            EngineLogCompat.Warn($"[JavaScriptEngine] permissions.query async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                    });

                    return FenValue.FromObject(thenable);
                })));
                thenable.Set("catch", FenValue.FromFunction(new FenFunction("catch", (catchArgs, catchThis) =>
                {
                    var onRejected = (catchArgs != null && catchArgs.Length > 0 && catchArgs[0].IsFunction) ? catchArgs[0].AsFunction() : null;
                    if (onRejected == null || !settled || fulfilled)
                    {
                        return FenValue.FromObject(thenable);
                    }

                    _fenRuntime.Context.ScheduleCallback(() =>
                    {
                        TryInvokeFunction(onRejected, new[] { rejectedValue }, _fenRuntime.Context, "permissions.query.catch");
                    }, 0);
                    return FenValue.FromObject(thenable);
                })));
                return FenValue.FromObject(thenable);
            })));

            // 2. navigator.geolocation
            var geoObj = new FenBrowser.FenEngine.Core.FenObject();
            geoObj.Set("getCurrentPosition", FenValue.FromFunction(new FenFunction("getCurrentPosition", (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined;
                var successCb = args[0].AsFunction();
                var errorCb = (args.Length > 1 && args[1].IsFunction) ? args[1].AsFunction() : null;
                var origin = OriginKey(_ctx?.BaseUri);

                _ = RunDetachedAsync(async () =>
                {
                    bool granted = false;
                    try
                    {
                        granted = await _fenRuntime.Context.Permissions.RequestPermissionAsync(JsPermissions.Geolocation, origin).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Warn($"[JavaScriptEngine] geolocation permission request failed: {ex.Message}", LogCategory.JavaScript);
                    }

                    if (granted)
                    {
                        var pos = CreateGeolocationPosition();
                        _fenRuntime.Context.ScheduleCallback(() =>
                        {
                            TryInvokeFunction(successCb, new FenValue[] { FenValue.FromObject(pos) }, _fenRuntime.Context, "geolocation.getCurrentPosition.success");
                        }, 0);
                    }
                    else
                    {
                        ScheduleGeolocationError(errorCb, "User denied Geolocation", "geolocation.getCurrentPosition.error");
                    }
                });
                return FenValue.Undefined;
            })));
            geoObj.Set("watchPosition", FenValue.FromFunction(new FenFunction("watchPosition", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction)
                    return FenValue.FromNumber(0);

                var successCb = args[0].AsFunction();
                var errorCb = (args.Length > 1 && args[1].IsFunction) ? args[1].AsFunction() : null;
                var intervalMs = ParseGeolocationWatchInterval(args.Length > 2 ? args[2] : FenValue.Undefined);
                var watchId = RegisterGeolocationWatch(successCb, errorCb, intervalMs, OriginKey(_ctx?.BaseUri));
                return FenValue.FromNumber(watchId);
            })));
            geoObj.Set("clearWatch", FenValue.FromFunction(new FenFunction("clearWatch", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    ClearGeolocationWatch((int)args[0].ToNumber(), disposeCts: true);
                }
                return FenValue.Undefined;
            })));

            // Check if 'navigator' exists, create or extend
            var existingNav = _fenRuntime.GetGlobal("navigator");
            FenBrowser.FenEngine.Core.FenObject navObj = null;
            if (existingNav.IsObject) navObj = (FenBrowser.FenEngine.Core.FenObject)existingNav.AsObject();
            else 
            {
                navObj = new FenBrowser.FenEngine.Core.FenObject();
                _fenRuntime.SetGlobal("navigator", FenValue.FromObject(navObj));
            }

            navObj.Set("permissions", FenValue.FromObject(permissionsObj));
            navObj.Set("geolocation", FenValue.FromObject(geoObj));
            var shareObj = FenBrowser.FenEngine.WebAPIs.WebShareAPI.CreateShareObject(_fenRuntime.Context);
            navObj.Set("share", shareObj.Get("share"));
            navObj.Set("canShare", shareObj.Get("canShare"));
            navObj.Set("storage", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.StorageManagerAPI.CreateStorageManagerObject(
                () => OriginKey(_ctx?.BaseUri),
                () => FenBrowser.FenEngine.WebAPIs.StorageApi.BuildSessionScope(_sessionStoragePartitionId, OriginKey(_ctx?.BaseUri)),
                _fenRuntime.Context)));
            
            var surface = BuildBrowserSurface();

            // Basic navigator properties for detection
            navObj.Set("javaEnabled", FenValue.FromFunction(new FenFunction("javaEnabled", (args, ctx) => FenValue.FromBoolean(false))));
            navObj.Set("cookieEnabled", FenValue.FromBoolean(surface.CookieEnabled));
            navObj.Set("userAgent", FenValue.FromString(surface.UserAgent));
            navObj.Set("appName", FenValue.FromString(surface.AppName));
            navObj.Set("appVersion", FenValue.FromString(surface.AppVersion));
            navObj.Set("platform", FenValue.FromString(surface.PlatformToken));
            navObj.Set("vendor", FenValue.FromString(surface.Vendor));
            navObj.Set("product", FenValue.FromString(surface.Product));
            navObj.Set("language", FenValue.FromString(surface.Language));

            var langsObj = new FenBrowser.FenEngine.Core.FenObject();
            for (int index = 0; index < surface.Languages.Count; index++)
            {
                langsObj.Set(index.ToString(), FenValue.FromString(surface.Languages[index]));
            }

            langsObj.Set("length", FenValue.FromNumber(surface.Languages.Count));
            navObj.Set("languages", FenValue.FromObject(langsObj));

            navObj.Set("hardwareConcurrency", FenValue.FromNumber(surface.HardwareConcurrency));
            navObj.Set("deviceMemory", FenValue.FromNumber(surface.DeviceMemory));
            navObj.Set("onLine", FenValue.FromBoolean(surface.Online));
            navObj.Set("pdfViewerEnabled", FenValue.FromBoolean(surface.PdfViewerEnabled));
            navObj.Set("webdriver", FenValue.FromBoolean(surface.WebDriver)); 

            navObj.Set("userAgentData", _fenRuntime.GetGlobal("navigator").IsObject
                ? _fenRuntime.GetGlobal("navigator").AsObject().Get("userAgentData")
                : FenValue.Undefined);
            HostApiSurfaceCatalog.TraceUsage("navigator.userAgentData");

            // [Compliance] Log Client-Side Identity
            try
            {
                var ua = navObj.Get("userAgent").AsString();
                var platform = navObj.Get("platform").AsString();
                var vendor = navObj.Get("vendor").AsString();
                var cookie = navObj.Get("cookieEnabled").AsBoolean();
                EngineLogCompat.Debug($"[Compliance] JS Navigator: UA='{ua}' Platform='{platform}' Vendor='{vendor}' CookieEnabled={cookie}", LogCategory.JavaScript);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

            // Service Workers API - navigator.serviceWorker
            // Service Workers API - navigator.serviceWorker
            var swOrigin = OriginKey(_ctx?.BaseUri);
            navObj.Set("serviceWorker", FenValue.FromObject(new FenBrowser.FenEngine.Workers.ServiceWorkerContainer(swOrigin, _fenRuntime.Context)));
            
            // Clipboard API - navigator.clipboard
            navObj.Set("clipboard", FenValue.FromObject(FenBrowser.FenEngine.WebAPIs.ClipboardAPI.CreateClipboardObject(_fenRuntime.Context)));
            // Observer APIs - IntersectionObserver / ResizeObserver constructors
            var intersectionObserverCtor = FenBrowser.FenEngine.WebAPIs.IntersectionObserverAPI.CreateConstructor();
            if (intersectionObserverCtor is FenFunction intersectionObserverFn)
            {
                _fenRuntime.SetGlobal("IntersectionObserver", FenValue.FromFunction(intersectionObserverFn));
            }
            else
            {
                _fenRuntime.SetGlobal("IntersectionObserver", FenValue.FromObject(intersectionObserverCtor));
            }

            var resizeObserverCtor = FenBrowser.FenEngine.WebAPIs.ResizeObserverAPI.CreateConstructor();
            if (resizeObserverCtor is FenFunction resizeObserverFn)
            {
                _fenRuntime.SetGlobal("ResizeObserver", FenValue.FromFunction(resizeObserverFn));
            }
            else
            {
                _fenRuntime.SetGlobal("ResizeObserver", FenValue.FromObject(resizeObserverCtor));
            }

            // Notifications API - Notification constructor
            var notificationCtor = FenBrowser.FenEngine.WebAPIs.NotificationsAPI.CreateNotificationConstructor(_fenRuntime.Context);
            if (notificationCtor is FenFunction notificationFn)
            {
                _fenRuntime.SetGlobal("Notification", FenValue.FromFunction(notificationFn));
            }
            else
            {
                _fenRuntime.SetGlobal("Notification", FenValue.FromObject(notificationCtor));
            }

            ApplyBrowserSurfaceToRuntime();
        }

        private async Task<string> FetchThroughNetworkHandlerAsync(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (FetchHandler == null) throw new InvalidOperationException("FetchHandler not configured on engine");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await FetchHandler(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        private bool IsWorkerScriptUriAllowed(Uri scriptUri)
        {
            if (scriptUri == null) return false;
            if (!(scriptUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  scriptUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (SubresourceAllowed != null)
            {
                try
                {
                    if (!SubresourceAllowed(scriptUri, "worker"))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            var baseUri = _ctx?.BaseUri;
            if (baseUri != null && !CorsHandler.IsSameOrigin(scriptUri, baseUri))
                return false;

            return true;
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

        public Action RequestRender
        {
            get => _fenRuntime?.RequestRender;
            set { if (_fenRuntime != null) _fenRuntime.RequestRender = value; }
        }
        
        /// <summary>Get the DOM root that JS is using (for repaint sync)</summary>
        public Node DomRoot => _domRoot;
        
        // Document ready state (for document.readyState property)

        public JsVal OnPopState { get; set; }
        public JsVal OnHashChange { get; set; }
        


        // Canvas persistence
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Element, SKBitmap> _canvasBitmaps 
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Element, SKBitmap>();

        public static void RegisterCanvasBitmap(Element element, SKBitmap bitmap)
        {
            if (element  == null || bitmap  == null) return;
            // ConditionalWeakTable doesn't have indexer setter, use Remove/Add or GetValue to set
            _canvasBitmaps.Remove(element);
            _canvasBitmaps.Add(element, bitmap);
        }

        public static SKBitmap GetCanvasBitmap(Element element)
        {
            if (element  == null) return null;
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
            try { TraceFeatureGap("Sandbox", feature.ToString(), messageDetail); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            try
            {
                var status = string.IsNullOrWhiteSpace(messageDetail)
                    ? "[Sandbox] Blocked " + feature
                    : "[Sandbox] Blocked " + feature + " : " + messageDetail;
                _host?.SetStatus(status);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

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

        // element-level listeners - REPLACED with generic object storage
        // private readonly Dictionary<string, Dictionary<string, List<string>>> _evtEl = ...

        // Centralized event listener storage for all objects (Window, Document, Element)
        // Key: The target object (Element, or FenObject for Window/Document)
        // Value: Dictionary of EventName -> List of Handlers
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, List<ObjectEventListener>>> _objectEventListeners =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, List<ObjectEventListener>>>();

        private readonly Dictionary<string, Dictionary<string, List<string>>> _evtEl =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        // Optional bridge supplied by host to provide a CookieContainer for managed HttpClient fallbacks.
        public Func<Uri, System.Net.CookieContainer> CookieBridge { get; set; }
        public Func<Uri, string> CookieReadBridge { get; set; }
        public Action<Uri, string> CookieWriteBridge { get; set; }
        
        // --- lightweight fields that may be missing in some merge states ---
        // DOM visual registry
        private static readonly System.Collections.Generic.Dictionary<Element, System.WeakReference> _visualMap =
            new System.Collections.Generic.Dictionary<Element, System.WeakReference>(System.Collections.Generic.EqualityComparer<Element>.Default);
        private static System.WeakReference _visualRoot;

        // DOM root exposed to the engine
        private Node _domRoot;

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
    
    // Feature gap tracing throttling
    private readonly object _featureTraceLock = new object();
    private string _lastFeatureTraceKey;
    private System.DateTime _lastFeatureTraceTime = System.DateTime.MinValue;

        // Response registry (tokenized large response bodies)
        

        // Inline thresholds / repaint flags

        

        // --- Mobile-oriented JS limits ---
        // Soft cap on total bytes of script source executed per page. This is
        // intended to keep mobile-class devices responsive by skipping
        // extremely large desktop-style bundles while still running typical
        // mobile-sized scripts.
        private int _pageScriptByteBudget = 10 * 1024 * 1024; // 10 MB default for modern sites
        

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

        // Prefetched module source cache keyed by absolute module URI.
        // This avoids sync-blocking network bridges in module-loader hot paths.
        private readonly Dictionary<string, string> _prefetchedModuleSource =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _prefetchedModuleSourceLock = new object();
        private const int MaxModulePrefetchDepth = 32;

        private static readonly Regex ModuleImportFromRegex = new Regex(
            @"\b(?:import|export)\b[\s\S]*?\bfrom\s*['""](?<spec>[^'""]+)['""]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ModuleImportSideEffectRegex = new Regex(
            @"\bimport\s*['""](?<spec>[^'""]+)['""]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ModuleDynamicImportLiteralRegex = new Regex(
            @"\bimport\s*\(\s*['""](?<spec>[^'""]+)['""]\s*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);


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
        

        // Mutation observers / pending mutations
        
        private readonly object _mutationLock = new object();
        
        // --- DOM visual registry for approximate layout metrics ---
        // [MIGRATION] Avalonia Visual Registry removed. Layout metrics should be retrieved from SkiaDomRenderer.
        private static Func<Element, SKRect?> _visualRectProvider;

        public static void RegisterDomVisual(Element node, object fe)
        {
            // No-op (legacy API retained for compatibility)
        }

        public static object GetControlForElement(Element node)
        {
            return null;
        }

        public static void SetVisualRectProvider(Func<Element, SKRect?> provider)
        {
            Volatile.Write(ref _visualRectProvider, provider);
        }

        public static bool TryGetVisualRect(Element node, out double x, out double y, out double w, out double h)
        {
            x = y = w = h = 0;
            if (node == null)
            {
                return false;
            }

            var provider = Volatile.Read(ref _visualRectProvider);
            if (provider == null)
            {
                return false;
            }

            try
            {
                var rect = provider(node);
                if (!rect.HasValue)
                {
                    return false;
                }

                x = rect.Value.Left;
                y = rect.Value.Top;
                w = rect.Value.Width;
                h = rect.Value.Height;
                return true;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] TryGetVisualRect provider failed: {ex.Message}", LogCategory.JavaScript);
                return false;
            }
        }

        internal static object GetVisual(Element node)
        {
            return null;
        }
        public static void RegisterVisualRoot(object root)
        {
            // No-op (legacy API retained for compatibility)
        }
        // ---- Phase 1/2/3 state ----
        private readonly Dictionary<string, List<string>> _evtDoc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _evtWin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Legacy mini runtime event listeners removed.

    // Track stopPropagation requests inside a single handler execution (JS-0 allowlist)
    private volatile bool _stopPropagationRequested;
    // Track preventDefault requests surfaced via runtime event wrappers
    

        private void RegisterListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            if (string.IsNullOrWhiteSpace(evt) || string.IsNullOrWhiteSpace(fnName)) return;
            List<string> list;
            if (!bag.TryGetValue(evt, out list) || list  == null) { list = new List<string>(); bag[evt] = list; }
            if (!list.Contains(fnName)) list.Add(fnName);
        }

        private void RemoveListener(Dictionary<string, List<string>> bag, string evt, string fnName)
        {
            List<string> list; if (!bag.TryGetValue(evt, out list) || list  == null) return;
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
                        EnqueueMicrotask(() => { TryRunInline(fn + "({ type:'" + evt + "', target:'document' })", _ctx, evt, "document"); });
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

        }

        private void FireWindowEvent(string evt)
        {
            try
            {
                List<string> list; if (_evtWin.TryGetValue(evt, out list) && list != null)
                {
                    foreach (var fn in list.ToArray())
                    {
                        EnqueueMicrotask(() => { TryRunInline(fn + "({ type:'" + evt + "', target:'window' })", _ctx, evt, "window"); });
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

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
        private readonly string _sessionStoragePartitionId = Guid.NewGuid().ToString("N");
        

        // loader/event firing



        // lightweight navigation history
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
                if (!_evtEl.TryGetValue(id, out byEvt) || byEvt  == null)
                    return;

                List<string> list;
                if (!byEvt.TryGetValue(evt, out list) || list  == null)
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
                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            
            // Dispatch to FenRuntime (simulating bubbling to window)
            EngineLogCompat.Debug($"[RaiseElementEvent] Check _fenRuntime: {(_fenRuntime  == null ? "NULL" : "OK")}", LogCategory.Events);
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
                    /* [PERF-REMOVED] */
                }
            }
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
                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            return _stopPropagationRequested;
        }

        /// <summary>
        /// Raise an event by element ID (alternative entry point).
        /// </summary>
        public void RaiseElementEventById(string id, string evt)
        {
            RaiseElementEvent(id, evt);
        }
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
                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                    };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Feature-gap trace failed: {ex.Message}", LogCategory.JavaScript);
                }
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
                    TryDisposeTimer(t);
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
                    Action run = () => { TryRunInline(fnName + "(Date.now&&Date.now()||0)", _ctx); };
                    if (repaintHost != null) repaintHost.InvokeOnUiThread(run); else run();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Feature-gap trace failed: {ex.Message}", LogCategory.JavaScript);
                }
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
                    TryDisposeTimer(t);
                    _rafs.Remove(id);
                }
            }
        }

        // ---------------- Storage ----------------
        private static string OriginKey(Uri u)
        {
            if (u  == null) return "null://";
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
            await Task.CompletedTask;
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
                        if (origin.Value  == null) continue;
                        foreach (var kv in origin.Value)
                        {
                            var line = (origin.Key ?? "") + "\t" + (kv.Key ?? "") + "\t" + (kv.Value ?? "");
                            sb.AppendLine(line);
                        }
                    }
                }
                await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            */
        }

        private async Task RestoreLocalStorageAsync()
        {
            await Task.CompletedTask;
            /*
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                // Avoid first-chance FileNotFound by probing existence first
                var item = await folder.GetItemAsync(LocalStorageFile) as Windows.Storage.StorageFile;
                if (item  == null) return;
                var text = await Windows.Storage.FileIO.ReadTextAsync(item);
                if (string == nullOrWhiteSpace(text)) return;
                lock (_storageLock)
                {
                    foreach (var ln in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var parts = ln.Split('\t');
                            if (parts.Length < 3) continue;
                            Dictionary<string, string> bag;
                            if (!_localStorageMap.TryGetValue(parts[0], out bag) || bag  == null)
                            {
                                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                                _localStorageMap[parts[0]] = bag;
                            }
                            bag[parts[1]] = parts[2];
                        }
                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            */
        }

        // ---------------- Cookies (best-effort via CookieBridge) ----------------
        private void SetCookieString(Uri scope, string cookieString)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie set")) return;
            try
            {
                if (CookieWriteBridge != null && scope != null && !string.IsNullOrWhiteSpace(cookieString))
                {
                    CookieWriteBridge(scope, cookieString);
                    return;
                }

                if (CookieBridge  == null || scope  == null || string.IsNullOrWhiteSpace(cookieString)) return;
                var jar = CookieBridge(scope);
                if (jar  == null) return;
                jar.SetCookies(scope, cookieString);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
        }

        private string GetCookieString(Uri scope)
        {
            if (!SandboxAllows(SandboxFeature.Storage, "document.cookie get")) return string.Empty;
            try
            {
                if (CookieReadBridge != null && scope != null)
                {
                    return CookieReadBridge(scope) ?? string.Empty;
                }

                if (CookieBridge  == null || scope  == null) return "";
                var jar = CookieBridge(scope);
                if (jar  == null) return "";
                var coll = jar.GetCookies(scope);
                if (coll  == null || coll.Count == 0) return "";

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
                if (u  == null) return null;
                return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
            }
            catch { return u != null ? ((u.Scheme ?? "") + "://" + (u.Host ?? "") + (u.PathAndQuery ?? "")) : null; }
        }

        private static void TryLogDebug(string message)
        {
            try
            {
                EngineLogCompat.Debug(message, LogCategory.JavaScript);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Debug log failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static void TryInvokeFunction(FenFunction callback, FenValue[] args, IExecutionContext context, string operation)
        {
            if (callback == null || context == null) return;
            try
            {
                callback.Invoke(args, context);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Callback '{operation}' failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void TryExecuteFunction(FenFunction callback, FenValue[] args, string operation)
        {
            if (callback == null || _fenRuntime == null) return;
            try
            {
                _fenRuntime.ExecuteFunction(callback, args);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] ExecuteFunction '{operation}' failed: {ex.Message}", LogCategory.JavaScript);
            }
        }
        private void TrySetStatus(string message)
        {
            try
            {
                _host?.SetStatus(message);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Host SetStatus failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void TryNavigate(Uri uri)
        {
            if (uri == null) return;
            try
            {
                _host?.Navigate(uri);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Host Navigate failed for '{uri}': {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void TryRunInline(string code, JsContext ctx)
        {
            try
            {
                RunInline(code, ctx);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] RunInline failed in callback: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void TryRunInline(string code, JsContext ctx, string eventType, string eventTarget)
        {
            try
            {
                RunInline(code, ctx, eventType, eventTarget);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] RunInline failed for event '{eventType}' on '{eventTarget}': {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static void TryDisposeTimer(System.Threading.Timer timer)
        {
            if (timer == null) return;
            try
            {
                timer.Dispose();
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Timer dispose failed: {ex.Message}", LogCategory.JavaScript);
            }
        }


        private void HistoryPush(Uri u)
        {
            if (u  == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.pushState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            _history.Add(u);
            _historyIndex = _history.Count - 1;
            if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal))
            {
                try { FireWindowEvent("hashchange"); }
                catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] hashchange dispatch failed: {ex.Message}", LogCategory.JavaScript); }
            }
        }

        private void HistoryReplace(Uri u)
        {
            if (u  == null) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.replaceState -> " + (u?.AbsoluteUri ?? ""))) return;
            Uri prev = null; if (_historyIndex >= 0 && _historyIndex < _history.Count) prev = _history[_historyIndex];
            if (_historyIndex < 0) { _history.Add(u); _historyIndex = _history.Count - 1; }
            else _history[_historyIndex] = u;
            if (prev != null && string.Equals(BaseWithoutFragment(prev), BaseWithoutFragment(u), StringComparison.OrdinalIgnoreCase) && !string.Equals(prev.Fragment ?? "", u.Fragment ?? "", StringComparison.Ordinal))
            {
                try { FireWindowEvent("hashchange"); }
                catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] hashchange dispatch failed: {ex.Message}", LogCategory.JavaScript); }
            }
        }

        private void HistoryGo(int delta)
        {
            var target = _historyIndex + delta;
            if (target < 0 || target >= _history.Count) return;
            if (!SandboxAllows(SandboxFeature.Navigation, "history.go(" + delta + ")")) return;
            _historyIndex = target;
            TryNavigate(_history[_historyIndex]);
            FireWindowEvent("popstate");
        }
        public object Evaluate(string script)
        {
            try { EngineLogCompat.Debug($"[JavaScriptEngine] Evaluate called with script length: {script?.Length ?? 0}", LogCategory.JavaScript); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

            var execution = ExecuteRuntimeScript(script, JavaScriptExecutionKind.Eval, "eval.js");
            if (execution.Exception != null)
            {
                EngineLogCompat.Error($"[JS] Evaluate error: {execution.Exception.Message}", LogCategory.JavaScript, execution.Exception);
                return "Error: " + execution.Exception.Message;
            }

            return ConvertExecutionResultToHostValue(execution.Value);
        }









        // Legacy JSON-based localStorage persistence (no longer used; kept for compatibility reference)
        // The engine now uses a simple line-based format via SaveLocalStorageAsync/RestoreLocalStorageAsync
        // against the _localStorageMap dictionary.
        private Task PersistLocalStorageAsync()
        {
            return SaveLocalStorageAsync();
        }

        public void LocalStorageSet(string key, string value, JsContext ctx)
        {
            try
            {
                if (!SandboxAllows(SandboxFeature.Storage, "localStorage.setItem")) return;
                StorageApi.SetLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key, value);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] localStorage.setItem failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public string LocalStorageGet(string key, JsContext ctx)
        {
            try
            {
                if (!SandboxAllows(SandboxFeature.Storage, "localStorage.getItem")) return null;
                return StorageApi.GetLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] localStorage.getItem failed: {ex.Message}", LogCategory.JavaScript);
                return null;
            }
        }

        public void LocalStorageRemove(string key, JsContext ctx)
        {
            try
            {
                if (!SandboxAllows(SandboxFeature.Storage, "localStorage.removeItem")) return;
                StorageApi.RemoveLocalStorageItem(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri), key);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] localStorage.removeItem failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public void LocalStorageClear(JsContext ctx)
        {
            try
            {
                if (!SandboxAllows(SandboxFeature.Storage, "localStorage.clear")) return;
                StorageApi.ClearLocalStorage(OriginKey(ctx?.BaseUri ?? _ctx?.BaseUri));
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] localStorage.clear failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

    public void Reset(JsContext ctx)
        {
            _ctx = ctx ?? new JsContext();
            ClearSandboxBlockLog();
            ClearGeolocationWatches();
            
            InitRuntime();

        }

        private FenBrowser.FenEngine.Core.FenObject CreateGeolocationPosition()
        {
            var pos = new FenBrowser.FenEngine.Core.FenObject();
            var coords = new FenBrowser.FenEngine.Core.FenObject();
            coords.Set("latitude", FenValue.FromNumber(37.422));
            coords.Set("longitude", FenValue.FromNumber(-122.084));
            coords.Set("accuracy", FenValue.FromNumber(100));
            coords.Set("altitude", FenValue.Null);
            coords.Set("altitudeAccuracy", FenValue.Null);
            coords.Set("heading", FenValue.Null);
            coords.Set("speed", FenValue.Null);
            pos.Set("coords", FenValue.FromObject(coords));
            pos.Set("timestamp", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return pos;
        }

        private void ScheduleGeolocationError(FenFunction errorCb, string message, string operation)
        {
            if (errorCb == null)
            {
                return;
            }

            var err = new FenBrowser.FenEngine.Core.FenObject();
            err.Set("code", FenValue.FromNumber(1));
            err.Set("message", FenValue.FromString(message));
            _fenRuntime.Context.ScheduleCallback(() =>
            {
                TryInvokeFunction(errorCb, new FenValue[] { FenValue.FromObject(err) }, _fenRuntime.Context, operation);
            }, 0);
        }

        private int RegisterGeolocationWatch(FenFunction successCb, FenFunction errorCb, int intervalMs, string origin)
        {
            var watchId = Interlocked.Increment(ref _nextGeolocationWatchId);
            var cts = new CancellationTokenSource();
            lock (_geolocationWatchLock)
            {
                _geolocationWatches[watchId] = cts;
            }

            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    bool granted = false;
                    try
                    {
                        granted = await _fenRuntime.Context.Permissions.RequestPermissionAsync(JsPermissions.Geolocation, origin).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Warn($"[JavaScriptEngine] geolocation watch permission request failed: {ex.Message}", LogCategory.JavaScript);
                    }

                    if (!granted)
                    {
                        ScheduleGeolocationError(errorCb, "User denied Geolocation", "geolocation.watchPosition.error");
                        return;
                    }

                    while (!cts.Token.IsCancellationRequested)
                    {
                        var pos = CreateGeolocationPosition();
                        _fenRuntime.Context.ScheduleCallback(() =>
                        {
                            TryInvokeFunction(successCb, new FenValue[] { FenValue.FromObject(pos) }, _fenRuntime.Context, "geolocation.watchPosition.success");
                        }, 0);

                        await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    ClearGeolocationWatch(watchId, disposeCts: true);
                }
            });

            return watchId;
        }

        private int ParseGeolocationWatchInterval(FenValue options)
        {
            const int defaultIntervalMs = 1000;
            const int minIntervalMs = 250;
            if (!options.IsObject)
            {
                return defaultIntervalMs;
            }

            var optionsObject = options.AsObject();
            var intervalCandidate = optionsObject?.Get("timeout") ?? FenValue.Undefined;
            if (!intervalCandidate.IsNumber)
            {
                intervalCandidate = optionsObject?.Get("maximumAge") ?? FenValue.Undefined;
            }

            if (!intervalCandidate.IsNumber)
            {
                return defaultIntervalMs;
            }

            var parsed = (int)Math.Round(intervalCandidate.ToNumber());
            if (parsed <= 0)
            {
                return defaultIntervalMs;
            }

            return Math.Max(minIntervalMs, parsed);
        }

        private void ClearGeolocationWatch(int watchId, bool disposeCts = false)
        {
            CancellationTokenSource cts = null;
            lock (_geolocationWatchLock)
            {
                if (_geolocationWatches.TryGetValue(watchId, out cts))
                {
                    _geolocationWatches.Remove(watchId);
                }
            }

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (disposeCts)
            {
                cts.Dispose();
            }
        }

        private void ClearGeolocationWatches()
        {
            List<CancellationTokenSource> watches;
            lock (_geolocationWatchLock)
            {
                watches = new List<CancellationTokenSource>(_geolocationWatches.Values);
                _geolocationWatches.Clear();
            }

            foreach (var watch in watches)
            {
                try
                {
                    watch.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    watch.Dispose();
                }
            }
        }

        private void ResetPrefetchedModuleSource()
        {
            lock (_prefetchedModuleSourceLock)
            {
                _prefetchedModuleSource.Clear();
            }
        }

        private void CachePrefetchedModuleSource(Uri uri, string content)
        {
            if (uri == null || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            lock (_prefetchedModuleSourceLock)
            {
                _prefetchedModuleSource[uri.AbsoluteUri] = content;
            }
        }

        private bool TryGetPrefetchedModuleSource(Uri uri, out string content)
        {
            content = null;
            if (uri == null)
            {
                return false;
            }

            lock (_prefetchedModuleSourceLock)
            {
                return _prefetchedModuleSource.TryGetValue(uri.AbsoluteUri, out content);
            }
        }

        private async Task<string> FetchModuleTextForLoaderAsync(Uri uri, Uri referer)
        {
            if (uri == null) return string.Empty;
            try
            {
                if (FetchOverride != null)
                {
                    var viaOverride = await FetchOverride(uri).ConfigureAwait(false);
                    if (viaOverride != null) return viaOverride;
                }

                if (ExternalScriptFetcher != null)
                {
                    var viaExternal = await ExternalScriptFetcher(uri, referer ?? _ctx?.BaseUri).ConfigureAwait(false);
                    if (viaExternal != null) return viaExternal;
                }

                if (uri.IsFile && File.Exists(uri.LocalPath))
                {
                    return await File.ReadAllTextAsync(uri.LocalPath).ConfigureAwait(false);
                }

                EngineLogCompat.Warn($"[JavaScriptEngine] Blocked module fetch for unsupported URI without browser fetch pipeline: {uri}", LogCategory.Network);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Module fetch failed for '{uri}': {ex.Message}", LogCategory.Network);
            }

            return string.Empty;
        }

        private IEnumerable<string> ExtractModuleSpecifiers(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                yield break;
            }

            foreach (Match match in ModuleImportFromRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }

            foreach (Match match in ModuleImportSideEffectRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }

            foreach (Match match in ModuleDynamicImportLiteralRegex.Matches(source))
            {
                var spec = match.Groups["spec"]?.Value;
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    yield return spec.Trim();
                }
            }
        }

        private async Task PrefetchModuleGraphAsync(
            FenBrowser.FenEngine.Core.ModuleLoader moduleLoader,
            Uri moduleUri,
            Uri referrerUri,
            HashSet<string> visited,
            int depth = 0)
        {
            if (moduleLoader == null || moduleUri == null || visited == null)
            {
                return;
            }

            if (depth > MaxModulePrefetchDepth)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] Module prefetch depth limit reached at {moduleUri}", LogCategory.JavaScript);
                return;
            }

            var moduleKey = moduleUri.AbsoluteUri;
            if (!visited.Add(moduleKey))
            {
                return;
            }

            if (!IsModuleUriAllowed(moduleUri))
            {
                return;
            }

            var source = await FetchModuleTextForLoaderAsync(moduleUri, referrerUri).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            CachePrefetchedModuleSource(moduleUri, source);

            foreach (var specifier in ExtractModuleSpecifiers(source))
            {
                string resolved;
                try
                {
                    resolved = moduleLoader.Resolve(specifier, moduleKey);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var dependencyUri))
                {
                    continue;
                }

                if (!IsModuleUriAllowed(dependencyUri))
                {
                    continue;
                }

                await PrefetchModuleGraphAsync(moduleLoader, dependencyUri, moduleUri, visited, depth + 1).ConfigureAwait(false);
            }
        }

        private string BuildInlineModulePseudoPath(Uri baseUri)
        {
            if (baseUri != null &&
                baseUri.IsAbsoluteUri &&
                (baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 baseUri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)))
            {
                return new Uri(baseUri, $".fen-inline/module-{Guid.NewGuid():N}.js").AbsoluteUri;
            }

            return $"https://fen.invalid/.fen-inline/module-{Guid.NewGuid():N}.js";
        }

        private string FetchTextSync(Uri uri)
        {
            if (uri == null) return "";
            try
            {
                if (TryGetPrefetchedModuleSource(uri, out var prefetched))
                {
                    return prefetched;
                }

                if (uri.IsFile && File.Exists(uri.LocalPath))
                {
                    return File.ReadAllText(uri.LocalPath);
                }

                EngineLogCompat.Warn($"[JavaScriptEngine] Missing prefetched module source for '{uri}'. Module graph must be prefetched asynchronously.", LogCategory.Network);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[JavaScriptEngine] FetchTextSync failed for '{uri}': {ex.Message}", LogCategory.Network);
            }
            return "";
        }

        private bool IsModuleUriAllowed(Uri uri)
        {
            if (uri == null) return false;

            if (SubresourceAllowed != null)
            {
                try
                {
                    if (!SubresourceAllowed(uri, "script"))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            var scheme = (uri.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme == "http" || scheme == "https")
            {
                var baseUri = _ctx?.BaseUri;
                if (baseUri != null &&
                    (string.Equals(baseUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
                    !CorsHandler.IsSameOrigin(uri, baseUri))
                {
                    EngineLogCompat.Warn($"[JavaScriptEngine] Blocked cross-origin module without explicit CORS pipeline: {uri}", LogCategory.Network);
                    return false;
                }
            }

            return scheme == "https" || scheme == "http" || scheme == "file" || scheme == "data";
        }

        private void ApplyImportMapsFromDom(FenBrowser.FenEngine.Core.ModuleLoader moduleLoader, Uri baseUri)
        {
            if (moduleLoader == null || _domRoot == null)
            {
                return;
            }

            var imports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in _domRoot.SelfAndDescendants())
            {
                if (node is not Element element)
                {
                    continue;
                }

                if (!string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = element.GetAttribute("type")?.Trim();
                if (!string.Equals(type, "importmap", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var json = element.TextContent ?? string.Empty;
                TryReadImportMapEntries(json, imports);
            }

            if (imports.Count == 0)
            {
                return;
            }

            moduleLoader.SetImportMap(imports, baseUri);
            EngineLogCompat.Info($"[JavaScriptEngine] Applied import map entries: {imports.Count}", LogCategory.JavaScript);
        }

        private static void TryReadImportMapEntries(string json, Dictionary<string, string> target)
        {
            if (target == null || string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("imports", out var importsElement) ||
                    importsElement.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var entry in importsElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var key = entry.Name?.Trim();
                    var value = entry.Value.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    target[key] = value;
                }
            }
            catch
            {
                // Import map parse failure is non-fatal for script execution.
            }
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
                try { _host.SetStatus("[DOM mutated]"); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            }
            // after the host is requested to repaint, schedule any MutationObserver callbacks
            try { InvokeMutationObservers(); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
            _repaintRequested = false;
        }

        // When the DOM changes, invoke any registered MutationObserver callbacks as microtasks
        private void InvokeMutationObservers()
        {
            // 1. Legacy string-based observers (keep for backward compatibility if any)
            try
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

                if (mutations != null && mutations.Count > 0)
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
                            EnqueueMicrotask(() => { TryRunInline(fn + "(" + json + ")", _ctx); });
                        }
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

            // 2. New Wrapper-based observers (Standard MutationObserver)
            try
            {
                MutationObserverWrapper[] wrappers;
                lock (_mutationLock) wrappers = _fenMutationObservers.ToArray();

                foreach (var wrapper in wrappers)
                {
                    if (wrapper.HasPendingRecords)
                    {
                        // Safely get context
                        var context = _fenRuntime?.Context;
                        if (context  == null) continue;

                        // Get records as FenObjects, correctly serialized with ElementWrappers
                        var recordObjs = wrapper.TakeRecords(context);
                        
                        // Create JS Array
                        var jsArray = new FenObject();
                        jsArray.Set("length", FenValue.FromNumber(recordObjs.Length));
                        for (int i = 0; i < recordObjs.Length; i++)
                        {
                            jsArray.Set(i.ToString(), FenValue.FromObject(recordObjs[i]));
                        }

                        // Call callback: callback(mutations, observer)
                        // Note: We don't have the observer fen object handy here implies 'this' binding might be loose,
                        // but 2nd arg is passed.
                        var args = new[] { FenValue.FromObject(jsArray), FenValue.Undefined };

                        // Invoke immediately inside the active mutation-observer delivery pass.
                        // Re-queuing here requires a second checkpoint and drops same-turn delivery.
                        TryExecuteFunction(wrapper.Callback, args, "mutation-observer");
                    }
                }
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
        }

        private void RecordMutation(MutationRecord record)
        {
            // Dynamically inserted scripts must run through the same execution path
            // as parser-discovered scripts so runtime and tooling stay aligned.
            if (record.AddedNodes != null && record.AddedNodes.Count > 0)
            {
                foreach (var node in record.AddedNodes)
                {
                    if (node is Element el && string.Equals(el.TagName, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleDynamicScript(el);
                    }
                }
            }

            // 1. Dispatch to wrappers immediately (they filter internally and queue records)
            lock (_mutationLock)
            {
                foreach (var wrapper in _fenMutationObservers)
                {
                    string typeString = record.Type switch
                    {
                        MutationRecordType.ChildList => "childList",
                        MutationRecordType.Attributes => "attributes",
                        MutationRecordType.CharacterData => "characterData",
                        _ => record.Type.ToString().ToLowerInvariant()
                    };
                    wrapper.RecordMutation(record.Target, typeString, record.AttributeName, record.OldValue, record.AddedNodes?.ToList(), record.RemovedNodes?.ToList());
                }
            }
            
            // 2. Keep queue for legacy string observers (only if any exist to save memory)
            lock (_mutationObservers) 
            {
                if (_mutationObservers.Count > 0)
                {
                    lock (_mutationLock) _pendingMutations.Add(record);
                }
            }

            // WHATWG 4.7.3: Schedule MutationObserver delivery via EventLoopCoordinator batch queue
            // instead of a plain microtask, so delivery is batched and runs after microtask drain.
            FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance
                .QueueMutationObserverMicrotask(InvokeMutationObservers);
        }

        private void HandleDynamicScript(Element scriptEl)
        {
            if (scriptEl  == null) return;

            // Check src and integrity
            string src = scriptEl.GetAttribute("src");
            string sriIntegrity = scriptEl.GetAttribute("integrity");

            if (!string.IsNullOrWhiteSpace(src))
            {
                // External Script
                var baseUri = _ctx?.BaseUri;
                var uri = Resolve(baseUri, src);
                
                if (uri != null && (AllowExternalScripts || SandboxAllows(SandboxFeature.ExternalScripts)))
                {
                    // Run fetch-and-execute on background, then post back to main loop
                    _ = RunDetachedAsync(async () => 
                    {
                        try
                        {
                            string content = null;
                            
                            // Use the host/tooling fetch override when provided.
                            if (FetchOverride != null)
                                content = await FetchOverride(uri);
                            
                            // Legacy generic fetcher
                            if (content  == null && ExternalScriptFetcher != null) 
                                content = await ExternalScriptFetcher(uri, baseUri);
                            
                            // Fallback to internal fetch
                            if (content  == null) 
                                content = await FetchAsync(uri, baseUri);

                            var eventLoop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
                            eventLoop.ScheduleTask(() =>
                            {
                                if (content != null)
                                {
                                    // SRI check for dynamically-inserted external scripts
                                    if (!VerifySriIntegrity(content, sriIntegrity))
                                    {
                                        EngineLogCompat.Warn($"[SRI] Blocked dynamic script (integrity mismatch): {uri}", LogCategory.JavaScript);
                                        DispatchEvent(scriptEl, "error");
                                        return;
                                    }
                                    try
                                    {
                                        ResetExecutionBudgetForHostBookkeeping();
                                        var previousCurrentScript = GetCurrentScriptValue();
                                        SetCurrentScriptElement(scriptEl);
                                        try
                                        {
                                            Evaluate(content);
                                        }
                                        finally
                                        {
                                            SetCurrentScriptValue(previousCurrentScript.IsUndefined ? FenValue.Null : previousCurrentScript);
                                        }
                                        DispatchEvent(scriptEl, "load");
                                    }
                                    catch (Exception ex)
                                    {
                                        EngineLogCompat.Error($"[DynamicScript] Exec failed: {ex.Message}", LogCategory.JavaScript);
                                        DispatchEvent(scriptEl, "error");
                                    }
                                }
                                else
                                {
                                    EngineLogCompat.Error($"[DynamicScript] Fetch failed for {uri}", LogCategory.JavaScript);
                                    DispatchEvent(scriptEl, "error");
                                }
                            }, FenBrowser.FenEngine.Core.EventLoop.TaskSource.Networking, "dynamic-script");

                            // Timer callbacks already self-pump in hostless focused tests; dynamic network
                            // script execution needs the same affordance so async DOM inserts stay observable.
                            eventLoop.ProcessNextTask();
                        }
                        catch (Exception ex)
                        {
                             EngineLogCompat.Error($"[DynamicScript] Background error: {ex.Message}", LogCategory.JavaScript);
                             var eventLoop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
                             eventLoop.ScheduleTask(() => DispatchEvent(scriptEl, "error"), FenBrowser.FenEngine.Core.EventLoop.TaskSource.Networking, "dynamic-script-error");
                             eventLoop.ProcessNextTask();
                        }
                    });
                }
            }
            else
            {
                // Inline Script
                if (ExecuteInlineScriptsOnInnerHTML || SandboxAllows(SandboxFeature.InlineScripts))
                {
                    var text = scriptEl.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Evaluate(text);
                    }
                }
            }
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

        private async Task<HttpResponseMessage> SendThroughNetworkHandlerAsync(Uri uri, Uri referer = null)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (FetchHandler == null) throw new InvalidOperationException("FetchHandler not configured on engine");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (referer != null)
            {
                request.Headers.Referrer = referer;
                if (!CorsHandler.IsSameOrigin(uri, referer))
                {
                    var origin = CorsHandler.SerializeOrigin(new UriBuilder(referer.Scheme, referer.Host, referer.IsDefaultPort ? -1 : referer.Port).Uri);
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", origin);
                    }
                }
            }

            return await FetchHandler(request).ConfigureAwait(false);
        }

        private async Task<string> FetchAsync(Uri uri, Uri referer = null)
        {
            try
            {
                using var response = await SendThroughNetworkHandlerAsync(uri, referer).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
            
            public object crypto => new JsCrypto();
            public object performance => new JsPerformance();

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
                var el = _engine._domRoot?.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == id);
                return el != null ? new JsDomElement(_engine, el) : null;
            }

            public object body => _engine._domRoot != null ? new JsDomElement(_engine, (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.GetAttribute("id") == "body") ?? (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("body", StringComparison.OrdinalIgnoreCase)) ?? _engine._domRoot as Element) : null;
            
            public object head => _engine._domRoot != null ? new JsDomElement(_engine, (_engine._domRoot as Element)?.Descendants().OfType<Element>().FirstOrDefault(e => e.TagName.Equals("head", StringComparison.OrdinalIgnoreCase))) : null;

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
                return new JsDomElement(_engine, new Element(tagName));
            }

            public object createTextNode(string data)
            {
                var t = new Element("#text");
                t.Text = data;
                return new JsDomText(_engine, t);
            }

            public object querySelector(string sel)
            {
                if (_engine._domRoot  == null) return null;
                return new JsDocument(_engine, _engine._domRoot).querySelector(sel);
            }

            public object[] querySelectorAll(string sel)
            {
                if (_engine._domRoot  == null) return new object[0];
                return new JsDocument(_engine, _engine._domRoot).querySelectorAll(sel);
            }
        }

        public class HostConsole
        {
            private JavaScriptEngine _engine;
            public HostConsole(JavaScriptEngine engine) { _engine = engine; }
            public void log(string msg) { LogToFile("INFO", msg); }
            public void error(string msg) { LogToFile("ERROR", msg); }
            public void warn(string msg) { LogToFile("WARN", msg); }
            public void info(string msg) { LogToFile("INFO", msg); }
            public void debug(string msg) { LogToFile("DEBUG", msg); }
            
            private void LogToFile(string level, string msg)
            {
                DiagnosticPaths.AppendRootText("js_debug.log", $"[Console:{level}] {msg}\n");
                try { _engine._host.Log($"[{level}] {msg}"); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Host console forwarding failed: {ex.Message}", LogCategory.JavaScript); }
            }
        }

        public class HostNavigator
        {
            private JavaScriptEngine _engine;
            public HostNavigator(JavaScriptEngine engine) { _engine = engine; }
            private BrowserSurfaceProfile Surface => _engine.BuildBrowserSurface();
            public string userAgent => Surface.UserAgent;
            public string appName => "Fenbrowser";
            public string appVersion => "1.0.0";
            public string product => "FenEngine";
            public string vendor => Surface.Vendor;
            public object userAgentData => new Dictionary<string, object>
            {
                { "brands", Surface.UserAgentData.Brands.Select(brand => new { brand = brand.Brand, version = brand.Version }).ToArray() },
                { "mobile", Surface.UserAgentData.Mobile },
                { "platform", Surface.UserAgentData.Platform }
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
                // Pop state handling (currently not implemented)
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

        // NilJS host classes removed

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

        /// <summary>
        /// Synchronize document/window bindings for a new DOM without executing page scripts.
        /// This keeps the runtime bridge aligned for first paint while the full script pass
        /// remains in the bounded SetDomAsync execution path.
        /// </summary>
        public void SyncDomContext(Node domRoot, Uri baseUri = null)
        {
            /* [PERF-REMOVED] */
            ClearGeolocationWatches();
            _domRoot = domRoot;
            if (_ctx != null)
            {
                _ctx.BaseUri = baseUri;
            }

            if (_fenRuntime == null)
            {
                return;
            }

            try
            {
                /* [PERF-REMOVED] */
                _fenRuntime.SetDom(domRoot, baseUri);
                /* [PERF-REMOVED] */
                SetupPermissions();
                /* [PERF-REMOVED] */
                SetupWindowEvents();
                /* [PERF-REMOVED] */
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FenEngine] Error syncing DOM context: {ex.Message}");
                EngineLogCompat.Warn($"[JavaScriptEngine] SyncDomContext failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        /// <summary>Expose current DOM to the engine (for document.* bridge).</summary>
        public async Task SetDomAsync(Node domRoot, Uri baseUri = null)
        {
            SyncDomContext(domRoot, baseUri);

            if (_fenRuntime != null)
            {
                // Execute scripts (Inline + External + Modules)
                try
                {
                    /* [PERF-REMOVED] */
                    
                    ResetPrefetchedModuleSource();

                    // Create ModuleLoader if needed (lazy init)
                    var moduleLoader = new FenBrowser.FenEngine.Core.ModuleLoader(
                        _fenRuntime.GlobalEnv, 
                        _fenRuntime.Context,
                        uri => FetchTextSync(uri),
                        uri => IsModuleUriAllowed(uri)
                    );
                    ApplyImportMapsFromDom(moduleLoader, baseUri);
                    _fenRuntime.SetModuleLoader(moduleLoader);

                    // Inject global error handler
                    try
                    {
                        var errorHandler = "window.onerror = function(msg, url, line, col, error) { console.error('GLOBAL JS ERROR: ' + msg + ' at ' + url + ':' + line + ':' + col); if (error && error.stack) console.error(error.stack); };";
                        _fenRuntime.ExecuteSimple(errorHandler, "debug-handler");
                    }
                    catch (Exception ex) { DiagnosticPaths.AppendRootText("js_debug.log", $"[SetupError] {ex}\n"); }

                    int scriptIndex = 0;
                    SetCurrentScriptValue(FenValue.Null);
                    var scriptTraversalRoot = _domRoot;
                    if (scriptTraversalRoot == null)
                    {
                        DiagnosticPaths.AppendRootText("js_debug.log", "[ScriptSkip] DOM root missing; skipping script walk.\n");
                    }
                    else
                    {
                        foreach (var s in scriptTraversalRoot.SelfAndDescendants())
                        {
                            if (s is Element el)
                            {
                                string tagName = el.TagName?.ToLowerInvariant() ?? "";
                                if (string.Equals(tagName, "script", StringComparison.OrdinalIgnoreCase))
                                {
                                    DiagnosticPaths.AppendRootText("js_debug.log", "[ScriptFound] Found script tag.\n");
                                    scriptIndex++;
                                    string code = null;
                                    string srcInfo = "inline";

                                // Attribute checks
                                string type = el.GetAttribute("type")?.ToLowerInvariant() ?? "";
                                string src = el.GetAttribute("src");
                                string nonce = el.GetAttribute("nonce");
                                string integrity = el.GetAttribute("integrity"); // SRI
                                bool isModule = type == "module";
                            
                                // Filter invalid types
                                if (!string.IsNullOrEmpty(type) && 
                                    type != "text/javascript" && 
                                    type != "application/javascript" && 
                                    type != "module")
                                {
                                    continue;
                                }
                                
                                if (el.HasAttribute("nomodule")) continue;

                                // CSP Check (Enhanced with Nonce)
                                if (SubresourceAllowed != null) 
                                {
                                    Uri checkUri = null;
                                    if (!string.IsNullOrEmpty(src) && baseUri != null) Uri.TryCreate(baseUri, src, out checkUri);
                                    
                                    if (NonceAllowed != null)
                                    {
                                        bool isAllowed = NonceAllowed(nonce);

                                        if (string.IsNullOrEmpty(src))
                                        {
                                             // Inline Script: Check nonce
                                             if (!isAllowed) continue;
                                        }
                                        else
                                        {
                                            // External Script: If nonce check PASSED, we allow it immediately.
                                            // If failed or missing, we fall back to URL whitelist.
                                            if (!isAllowed || string.IsNullOrEmpty(nonce)) 
                                            {
                                                // Fallback to URL check
                                                 if (!string.IsNullOrEmpty(src) && checkUri != null && !SubresourceAllowed(checkUri, "script")) 
                                                 {
                                                     continue;
                                                 }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // URL Check (External Only) if NonceAllowed not available
                                        if (!string.IsNullOrEmpty(src) && checkUri != null && !SubresourceAllowed(checkUri, "script")) 
                                        {
                                            continue;
                                        }
                                    }
                                }

                                // 1. External Script
                                if (!string.IsNullOrEmpty(src)) 
                                {
                                    if (!SandboxAllows(SandboxFeature.ExternalScripts, "script src")) continue;

                                    if (baseUri != null)
                                    {
                                        try 
                                        {
                                            var scriptUri = new Uri(baseUri, src);
                                            srcInfo = scriptUri.ToString();
                                            
                                            if (isModule)
                                            {
                                                try
                                                {
                                                    await PrefetchModuleGraphAsync(
                                                        moduleLoader,
                                                        scriptUri,
                                                        baseUri,
                                                        new HashSet<string>(StringComparer.Ordinal))
                                                        .ConfigureAwait(false);
                                                    moduleLoader.LoadModule(scriptUri.AbsoluteUri);
                                                }
                                                catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                                                continue;
                                            }

                                            if (ExternalScriptFetcher != null)
                                            {
                                                code = await ExternalScriptFetcher(scriptUri, baseUri).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                code = await FetchAsync(scriptUri, baseUri).ConfigureAwait(false);
                                            }
                                        }
                                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                            // WHATWG HTML 4.12.1.1: network fetch error fires error on element
                            DispatchEvent(el, "error");
                        }
                                    }
                                }
                                // 2. Inline Script
                                else
                                {
                                    if (!SandboxAllows(SandboxFeature.InlineScripts, "inline script")) continue;
                                    code = CollectScriptText(s);
                                    
                                    if (isModule && !string.IsNullOrWhiteSpace(code))
                                    {
                                        try
                                        {
                                            var inlineModulePath = BuildInlineModulePseudoPath(baseUri);
                                            foreach (var specifier in ExtractModuleSpecifiers(code))
                                            {
                                                string resolved;
                                                try
                                                {
                                                    resolved = moduleLoader.Resolve(specifier, inlineModulePath);
                                                }
                                                catch
                                                {
                                                    continue;
                                                }

                                                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var dependencyUri))
                                                {
                                                    continue;
                                                }

                                                await PrefetchModuleGraphAsync(
                                                    moduleLoader,
                                                    dependencyUri,
                                                    new Uri(inlineModulePath),
                                                    new HashSet<string>(StringComparer.Ordinal))
                                                    .ConfigureAwait(false);
                                            }

                                            moduleLoader.LoadModuleSrc(code, inlineModulePath);
                                        }
                                        catch (Exception ex)
                        {
                            EngineLogCompat.Warn($"[JavaScriptEngine] fetch().then async bridge failed: {ex.Message}", LogCategory.JavaScript);
                        }
                                        continue; 
                                    }
                                }
                                
                                if (!string.IsNullOrWhiteSpace(code))
                                {
                                    if (!string.IsNullOrEmpty(src) &&
                                        RuntimeProfile.DeferOversizedExternalPageScripts &&
                                        RuntimeProfile.OversizedExternalPageScriptBytes > 0 &&
                                        code.Length >= RuntimeProfile.OversizedExternalPageScriptBytes)
                                    {
                                        EngineLogCompat.Warn(
                                            $"[JS-EXEC] Deferred oversized external script during initial load: source={srcInfo} length={code.Length}",
                                            LogCategory.JsExecution);
                                        DiagnosticPaths.AppendRootText(
                                            "js_debug.log",
                                            $"[ScriptDeferred] Oversized external page script deferred: Length={code.Length}, Info={srcInfo}\n");
                                        DispatchEvent(el, "load");
                                        continue;
                                    }
                                    // SRI check ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â external scripts with an integrity attr must match before execution
                                    if (!string.IsNullOrEmpty(src) && !VerifySriIntegrity(code, integrity))
                                    {
                                        DiagnosticPaths.AppendRootText("js_debug.log", $"[SRI] Blocked script (hash mismatch): {srcInfo}\n");
                                        EngineLogCompat.Warn($"[SRI] Blocked external script due to integrity mismatch: {srcInfo}", LogCategory.JavaScript);
                                        // WHATWG HTML 4.12.1.1: SRI mismatch fires error event
                                        DispatchEvent(el, "error");
                                        continue;
                                    }
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[ScriptRun] Executing script: Length={code.Length}, Info={srcInfo}\n");
                                    ResetExecutionBudgetForHostBookkeeping();
                                    var previousCurrentScript = GetCurrentScriptValue();
                                    SetCurrentScriptElement(el);
                                    try
                                    {
                                        var scriptExecution = ExecuteRuntimeScript(
                                            code,
                                            JavaScriptExecutionKind.PageScript,
                                            srcInfo);
                                        var scriptFenValue = scriptExecution.Value;
                                        if (scriptExecution.Exception != null ||
                                            (scriptFenValue.Type == JsValueType.Error || scriptFenValue.Type == JsValueType.Throw))
                                        {
                                            var diagnosticPreview = BuildScriptDiagnosticPreview(code);
                                            DiagnosticPaths.AppendRootText(
                                                "js_debug.log",
                                                $"[ScriptRunError] Info={srcInfo}; Error={scriptFenValue}; Preview={diagnosticPreview}\n");
                                            EngineLogCompat.Warn($"[ScriptRunError] {srcInfo}: {scriptFenValue}", LogCategory.JavaScript);
                                            // WHATWG HTML 4.12.1.1: script execution error fires error on element
                                            DispatchEvent(el, "error");
                                        }
                                        else
                                        {
                                            // WHATWG HTML 4.12.1.1: successful execution fires load event
                                            DispatchEvent(el, "load");
                                        }

                                        if (!string.IsNullOrEmpty(srcInfo) &&
                                            (srcInfo.IndexOf("/vendor.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             srcInfo.IndexOf("/main.", StringComparison.OrdinalIgnoreCase) >= 0))
                                        {
                                            LogXBootstrapState($"after-script:{srcInfo}", baseUri);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var diagnosticPreview = BuildScriptDiagnosticPreview(code);
                                        DiagnosticPaths.AppendRootText(
                                            "js_debug.log",
                                            $"[StaticScriptError] Info={srcInfo}; Error={ex.GetBaseException().Message}; Preview={diagnosticPreview}\n");
                                        EngineLogCompat.Warn($"[StaticScript] Exec failed: {srcInfo}: {ex.Message}", LogCategory.JavaScript);
                                        // WHATWG HTML 4.12.1.1: uncaught error fires error on element
                                        DispatchEvent(el, "error");
                                    }
                                    finally
                                    {
                                        SetCurrentScriptValue(previousCurrentScript.IsUndefined ? FenValue.Null : previousCurrentScript);
                                    }
                                }
                                else
                                {
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[ScriptSkip] Code empty or skipped. Type={type}, Src={src}\n");
                                }
                            }
                        }
                    }
                    
                    }

                    EngineLogCompat.Debug("[JavaScriptEngine] Inline script execution complete", LogCategory.JavaScript);
                }
                catch (Exception ex)
                {
                    DiagnosticPaths.AppendRootText("js_debug.log", $"[JSExecError] {ex}\n");
                    EngineLogCompat.Error($"[JavaScriptEngine] Script execution error: {ex.Message}", LogCategory.JavaScript, ex);
                }
            }
            
            // JS is "enabled" in this app; hide server noscript overlays & flip no-js ? js
            SetCurrentScriptValue(FenValue.Null);
            this.SanitizeForScriptingEnabled(domRoot);

            // 7. Fire LifeCycle Events (Spec Compliant)
            if (_fenRuntime != null)
            {
                var docIVal = _fenRuntime.GetGlobal("document");
                if (docIVal.IsObject)
                {
                    var docObj = docIVal.AsObject();
                    DocumentWrapper docWrapper = null;
                    if (docObj is DocumentWrapper dw) docWrapper = dw;
                    // Note: If document is a FenObject wrapping DocumentWrapper, we might need to check NativeObject
                    // but FenRuntime.SetDom sets it directly as the object.

                    if (docWrapper != null)
                    {
                        // DOMContentLoaded phase
                        docWrapper.SetReadyState("interactive");
                        DispatchEvent(docObj, "DOMContentLoaded", new DomEvent("DOMContentLoaded", bubbles: true));
                        
                        // load phase
                        docWrapper.SetReadyState("complete");
                        var window = _fenRuntime.GetGlobal("window").AsObject();
                        InvokeDocumentBodyInlineLoadHandler(docWrapper);
                        DispatchInitialIframeLoadEvents(domRoot);
                        DispatchEvent(window, "load", new DomEvent("load"));
                    }
                }
            }

            // Initial document boot can queue MutationObserver delivery without any
            // subsequent host task pump. Flush one checkpoint here so startup DOM
            // mutations become observable in focused runtime tests and simple hosts.
            FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            LogXBootstrapState("after-load-checkpoint", baseUri);
        }

        private DocumentWrapper GetActiveDocumentWrapper()
        {
            if (_fenRuntime == null)
            {
                return null;
            }

            var documentValue = _fenRuntime.GetGlobal("document");
            if (!documentValue.IsObject)
            {
                return null;
            }

            return documentValue.AsObject() as DocumentWrapper;
        }

        private FenValue GetCurrentScriptValue()
        {
            var documentWrapper = GetActiveDocumentWrapper();
            if (documentWrapper == null)
            {
                return FenValue.Null;
            }

            var currentScript = documentWrapper.Get("currentScript", _fenRuntime?.Context);
            return currentScript.IsUndefined ? FenValue.Null : currentScript;
        }

        private void ResetExecutionBudgetForHostBookkeeping()
        {
            if (_fenRuntime?.Context is FenBrowser.FenEngine.Core.ExecutionContext executionContext)
            {
                executionContext.Reset();
            }
        }

        private static string BuildScriptDiagnosticPreview(string code, int maxChars = 220)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return "<empty>";
            }

            var preview = code
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();

            while (preview.Contains("  ", StringComparison.Ordinal))
            {
                preview = preview.Replace("  ", " ", StringComparison.Ordinal);
            }

            if (preview.Length <= maxChars)
            {
                return preview;
            }

            return preview.Substring(0, maxChars) + "...";
        }

        private void LogXBootstrapState(string phase, Uri baseUri)
        {
            try
            {
                var host = baseUri?.Host ?? _ctx?.BaseUri?.Host ?? string.Empty;
                if (!host.EndsWith("x.com", StringComparison.OrdinalIgnoreCase) &&
                    !host.EndsWith("twitter.com", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_fenRuntime == null)
                {
                    return;
                }

                var diagResult = _fenRuntime.ExecuteSimple(@"
(() => {
    try {
        var loaded = window.__SCRIPTS_LOADED__ || {};
        var placeholder = document.getElementById('placeholder');
        var failure = document.getElementById('ScriptLoadFailure');
        var main = document.querySelector('[role=""main""]') || document.querySelector('main');
        return [
            'runtime=' + (!!loaded.runtime),
            'vendor=' + (!!loaded.vendor),
            'i18n=' + (!!loaded.i18n),
            'main=' + (!!loaded.main),
            'placeholder=' + (placeholder ? ((placeholder.style && placeholder.style.display) || '(visible)') : 'missing'),
            'failure=' + (failure ? ((failure.style && failure.style.display) || '(visible)') : 'missing'),
            'bodyChildren=' + (document.body && document.body.children ? document.body.children.length : -1),
            'mainNode=' + (main ? main.tagName : 'missing')
        ].join(';');
    } catch (e) {
        return 'diag-error:' + e;
    }
})()
", $"{host}::{phase}");

                var diagText = diagResult is FenValue diagFen
                    ? diagFen.ToString()
                    : diagResult?.ToString() ?? "null";

                var eventLoop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
                EngineLogCompat.Warn(
                    $"[XDiag] {phase}: {diagText};tasksPending={(eventLoop.HasPendingTasks ? 1 : 0)};microtasksPending={(eventLoop.HasPendingMicrotasks ? 1 : 0)}",
                    LogCategory.JavaScript);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[XDiag] {phase} failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void SetCurrentScriptElement(Element scriptElement)
        {
            if (scriptElement == null)
            {
                SetCurrentScriptValue(FenValue.Null);
                return;
            }

            SetCurrentScriptValue(DomWrapperFactory.Wrap(scriptElement, _fenRuntime?.Context));
        }

        private void SetCurrentScriptValue(FenValue value)
        {
            var documentWrapper = GetActiveDocumentWrapper();
            if (documentWrapper == null)
            {
                return;
            }

            documentWrapper.Set("currentScript", value.IsUndefined ? FenValue.Null : value, _fenRuntime?.Context);
        }

        private void InvokeDocumentBodyInlineLoadHandler(DocumentWrapper documentWrapper)
        {
            if (documentWrapper == null || _fenRuntime == null)
            {
                return;
            }

            var bodyValue = documentWrapper.Get("body", _fenRuntime.Context);
            if (!bodyValue.IsObject)
            {
                return;
            }

            var bodyWrapper = bodyValue.AsObject() as ElementWrapper;
            var bodyElement = bodyWrapper?.Element;
            if (bodyElement == null)
            {
                return;
            }

            InvokeInlineEventAttributeHandler(bodyElement, "load", new DomEvent("load", false, false, false, _fenRuntime.Context));
        }

        private void DispatchInitialIframeLoadEvents(Node domRoot)
        {
            if (domRoot == null)
            {
                return;
            }

            foreach (var iframe in domRoot.Descendants().OfType<Element>()
                         .Where(node => string.Equals(node.TagName, "iframe", StringComparison.OrdinalIgnoreCase)))
            {
                DispatchEvent(iframe, "load", new DomEvent("load", false, false, false, _fenRuntime.Context));
            }
        }

        // Backward compatibility wrapper (deprecated)
        public void SetDom(Node domRoot, Uri baseUri = null)
        {
             _ = ObserveBackgroundTaskFailureAsync(
                 SetDomAsync(domRoot, baseUri),
                 message => EngineLogCompat.Error($"[JavaScriptEngine] Deprecated SetDom bridge failed: {message}", LogCategory.JavaScript),
                 ex => EngineLogCompat.Error($"[JavaScriptEngine] Deprecated SetDom bridge failed: {ex.GetBaseException().Message}", LogCategory.JavaScript, ex));
        }

        private void RunGlobalScript(string js)
        {
            if (string.IsNullOrWhiteSpace(js)) return;
            // No longer used - FenEngine handles script execution in SetDom
        }

        private static string CollectScriptText(Node n, int depth = 0)
        {
            if (n  == null) return "";
            if (depth > 200) // Safety break
            {
                /* [PERF-REMOVED] */
                return "";
            }

            if (n.IsText())
            {
                var tel = n as Element;
                if (tel != null) return tel.Text ?? "";
                
                // If Node.NodeValue is available directly or cast to Text node
                if (n is Text t) return t.Data ?? "";
                return n.NodeValue ?? "";
            }
            
            var sb = new System.Text.StringBuilder();
            if (n.Children != null)
            {
                for (int i = 0; i < n.Children.Count; i++) sb.Append(CollectScriptText(n.Children[i], depth + 1));
            }
            return sb.ToString();
        }

        #region JS enabled sanitizer
        // INSIDE: public sealed class JavaScriptEngine { ... }
        private void SanitizeForScriptingEnabled(Node rootArg = null)
        {
            var root = rootArg ?? _domRoot;          // OK in instance method
            if (root  == null) return;

            // flip no-js ? js on <html>/<body>
            Action<Element> flipClass = n =>
            {
                if (n  == null) return;
                var attrs = n.Attr;
                if (attrs  == null) return;

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
                var html = (root as ContainerNode)?.GetElementsByTagName("html").FirstOrDefault();
                if (html != null) flipClass(html);
                var body = (root as ContainerNode)?.GetElementsByTagName("body").FirstOrDefault();
                if (body != null) flipClass(body);
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

            try
            {
                var toRemove = new List<Element>();
                foreach (var n in root.Descendants().OfType<Element>())
                    if (string.Equals(n.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(n);
                foreach (var n in toRemove) n.Remove();
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }

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
                if (_domRoot  == null || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) return;
                var doc = new JsDocument(this, _domRoot);
                var el = doc.getElementById(id) as JsDomElement; if (el  == null) return;
                var cur = el.getAttribute("class") ?? string.Empty;
                var set = new HashSet<string>(cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                bool changed = false;
                if (op == "add") { if (!set.Contains(cls)) { set.Add(cls); changed = true; } }
                else if (op == "remove") { if (set.Remove(cls)) changed = true; }
                else if (op == "toggle") { if (!set.Remove(cls)) { set.Add(cls); } changed = true; }
                if (changed) el.setAttribute("class", string.Join(" ", set.ToArray()));
            }
            catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); }
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

        /// <summary>
        /// Verifies Subresource Integrity (SRI) for a fetched resource.
        /// Returns true if <paramref name="integrity"/> is absent/empty (no check required) or
        /// if at least one hash token in the space-separated list matches the content.
        /// Returns false if tokens are present and none match ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â caller must block the resource.
        /// Supported algorithms: sha256, sha384, sha512.
        /// </summary>
        private static bool VerifySriIntegrity(string content, string integrity)
        {
            if (string.IsNullOrWhiteSpace(integrity)) return true;
            var tokens = integrity.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return true;

            var bytes = System.Text.Encoding.UTF8.GetBytes(content ?? "");
            foreach (var token in tokens)
            {
                var dash = token.IndexOf('-');
                if (dash < 0) continue;
                var algo = token.Substring(0, dash).ToLowerInvariant();
                var expectedB64 = token.Substring(dash + 1);

                byte[] hash;
                try
                {
                    using var alg = algo switch
                    {
                        "sha256" => (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA256.Create(),
                        "sha384" => System.Security.Cryptography.SHA384.Create(),
                        "sha512" => System.Security.Cryptography.SHA512.Create(),
                        _ => null
                    };
                    if (alg == null) continue; // Unknown algorithm ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â skip token
                    hash = alg.ComputeHash(bytes);
                }
                catch { continue; }

                if (Convert.ToBase64String(hash) == expectedB64) return true;
            }
            // No matching token found ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â block the resource
            return false;
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
        bool Confirm(string msg);
        string Prompt(string msg, string defaultValue);
        void Log(string msg);
        void ScrollToElement(Element element);
        void FocusNode(Element element);

        /// <summary>Returns the active layout engine, if available.</summary>
        FenBrowser.FenEngine.Rendering.Core.ILayoutEngine GetLayoutEngine() => null;
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
    private readonly Func<string, bool> _confirm;
    private readonly Func<string, string, string> _prompt;
    private readonly Action<string> _log;
    private readonly Action<Element> _scrollToElement;
    private readonly Action<Element> _focusNode;

        public JsHostAdapter(Action<Uri> navigate, Action<Uri, string> post, Action<string> status, Action requestRender = null, Action<Action> invokeOnUiThread = null, Action<string> setTitle = null, Action<string> alert = null, Func<string, bool> confirm = null, Func<string, string, string> prompt = null, Action<string> log = null, Action<Element> scrollToElement = null, Action<Element> focusNode = null)
        {
            _navigate = navigate ?? (_ => { });
            _post = post ?? ((_, __) => { });
            _status = status ?? (_ => { });
            _requestRender = requestRender ?? (() => { try { _status("[DOM mutated]"); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); } });
            _invokeOnUiThread = invokeOnUiThread ?? (a => { try { a(); } catch (Exception ex) { EngineLogCompat.Warn($"[JavaScriptEngine] Non-fatal operation failed: {ex.Message}", LogCategory.JavaScript); } });
            _setTitle = setTitle ?? (_ => { });
            _alert = alert ?? (_ => { });
            _confirm = confirm ?? (_ => true);
            _prompt = prompt ?? ((_, defaultValue) => defaultValue ?? string.Empty);
            _log = log ?? (_ => { });
            _scrollToElement = scrollToElement ?? (_ => { });
            _focusNode = focusNode ?? (_ => { });
        }

        // ... IJsHost implementation ...
        public void Navigate(Uri target) => _navigate(target);
        public void PostForm(Uri target, string body) => _post(target, body);
        public void SetStatus(string s) => _status(s);
        public void RequestRender() => _requestRender();
        public void InvokeOnUiThread(Action action) => _invokeOnUiThread(action);
        public void SetTitle(string t) => _setTitle(t);
        public void Alert(string msg) => _alert(msg);
        public bool Confirm(string msg) => _confirm(msg);
        public string Prompt(string msg, string defaultValue) => _prompt(msg, defaultValue);
        public void Log(string msg) => _log(msg);
        public void ScrollToElement(Element e) => _scrollToElement(e);
        public void FocusNode(Element e) => _focusNode(e);
    }

    public class JsCrypto : FenBrowser.FenEngine.Core.FenObject
    {
        private sealed class LegacyCryptoKeyState
        {
            public string AlgorithmName { get; set; } = string.Empty;
            public string HashName { get; set; } = string.Empty;
            public string NamedCurve { get; set; } = string.Empty;
            public string KeyType { get; set; } = string.Empty;
            public int KeyLengthBits { get; set; }
            public bool Extractable { get; set; }
            public HashSet<string> Usages { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public byte[] SecretKey { get; set; } = Array.Empty<byte>();
            public System.Security.Cryptography.RSA RsaKey { get; set; }
            public System.Security.Cryptography.ECDsa EcdsaKey { get; set; }
            public System.Security.Cryptography.ECDiffieHellman EcdhKey { get; set; }
            public bool IsPrivateKey { get; set; }
        }

        private static readonly HashSet<string> HmacAllowedUsages =
            new HashSet<string>(new[] { "sign", "verify" }, StringComparer.Ordinal);

        private static readonly HashSet<string> RsaPrivateAllowedUsages =
            new HashSet<string>(new[] { "sign" }, StringComparer.Ordinal);

        private static readonly HashSet<string> RsaPublicAllowedUsages =
            new HashSet<string>(new[] { "verify" }, StringComparer.Ordinal);

        private static readonly HashSet<string> RsaOaepPrivateAllowedUsages =
            new HashSet<string>(new[] { "decrypt", "unwrapkey" }, StringComparer.Ordinal);

        private static readonly HashSet<string> RsaOaepPublicAllowedUsages =
            new HashSet<string>(new[] { "encrypt", "wrapkey" }, StringComparer.Ordinal);

        private static readonly HashSet<string> RsaOaepGenerateAllowedUsages =
            new HashSet<string>(new[] { "encrypt", "decrypt", "wrapkey", "unwrapkey" }, StringComparer.Ordinal);

        private static readonly HashSet<string> AesAllowedUsages =
            new HashSet<string>(new[] { "encrypt", "decrypt", "wrapkey", "unwrapkey" }, StringComparer.Ordinal);

        private static readonly HashSet<string> Pbkdf2AllowedUsages =
            new HashSet<string>(new[] { "derivekey", "derivebits" }, StringComparer.Ordinal);

        private static readonly HashSet<string> HkdfAllowedUsages =
            new HashSet<string>(new[] { "derivekey", "derivebits" }, StringComparer.Ordinal);

        private static readonly HashSet<string> EcdhPrivateAllowedUsages =
            new HashSet<string>(new[] { "derivekey", "derivebits" }, StringComparer.Ordinal);

        public JsCrypto()
        {
            Set("getRandomValues", FenValue.FromFunction(new FenFunction("getRandomValues", GetRandomValues)));
            Set("randomUUID", FenValue.FromFunction(new FenFunction("randomUUID", (args, thisVal) =>
                FenValue.FromString(Guid.NewGuid().ToString()))));
            Set("subtle", FenValue.FromObject(CreateSubtleCryptoObject()));
            HostApiSurfaceCatalog.TraceUsage("crypto.subtle");
        }

        private FenValue GetRandomValues(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1 || !args[0].IsObject)
            {
                throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: getRandomValues requires an integer typed array argument");
            }

            var input = args[0];
            var target = input.AsObject();
            if (target == null)
            {
                throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: getRandomValues requires an integer typed array argument");
            }

            const int maxBytes = 65536;
            if (target is FenBrowser.FenEngine.Core.Types.JsTypedArray typedArray)
            {
                if (typedArray is FenBrowser.FenEngine.Core.Types.JsFloat32Array
                    || typedArray is FenBrowser.FenEngine.Core.Types.JsFloat64Array)
                {
                    throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: getRandomValues requires an integer typed array argument");
                }

                var byteLen = typedArray.Length * typedArray.BytesPerElement;
                if (byteLen > maxBytes)
                {
                    throw new FenBrowser.FenEngine.Errors.FenResourceError("QuotaExceededError: Max 65536 bytes");
                }

                var bytes = new byte[byteLen];
                using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
                rng.GetBytes(bytes);
                Array.Copy(bytes, 0, typedArray.Buffer.Data, typedArray.ByteOffset, byteLen);
                return input;
            }

            throw new FenBrowser.FenEngine.Errors.FenTypeError("TypeError: getRandomValues requires an integer typed array argument");
        }

        private static FenObject CreateSubtleCryptoObject()
        {
            var subtle = new FenObject();
            subtle.Set("digest", FenValue.FromFunction(new FenFunction("digest", Digest)));
            subtle.Set("generateKey", FenValue.FromFunction(new FenFunction("generateKey", GenerateKey)));
            subtle.Set("importKey", FenValue.FromFunction(new FenFunction("importKey", ImportKey)));
            subtle.Set("exportKey", FenValue.FromFunction(new FenFunction("exportKey", ExportKey)));
            subtle.Set("encrypt", FenValue.FromFunction(new FenFunction("encrypt", Encrypt)));
            subtle.Set("decrypt", FenValue.FromFunction(new FenFunction("decrypt", Decrypt)));
            subtle.Set("wrapKey", FenValue.FromFunction(new FenFunction("wrapKey", WrapKey)));
            subtle.Set("unwrapKey", FenValue.FromFunction(new FenFunction("unwrapKey", UnwrapKey)));
            subtle.Set("deriveBits", FenValue.FromFunction(new FenFunction("deriveBits", DeriveBits)));
            subtle.Set("deriveKey", FenValue.FromFunction(new FenFunction("deriveKey", DeriveKey)));
            subtle.Set("sign", FenValue.FromFunction(new FenFunction("sign", Sign)));
            subtle.Set("verify", FenValue.FromFunction(new FenFunction("verify", Verify)));
            return subtle;
        }

        private static FenValue GenerateKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 3)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: generateKey requires algorithm, extractable, and keyUsages"));
            }

            try
            {
                if (!TryNormalizeAlgorithmName(args[0], out var algorithmName))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!TryParseKeyUsages(args[2], out var usages))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: keyUsages must be an array-like of strings"));
                }

                var extractable = args[1].ToBoolean();
                switch (algorithmName)
                {
                    case "HMAC":
                        return GenerateHmacKey(args[0], extractable, usages);
                    case "RSASSAPKCS1V15":
                        return GenerateRsaSsaKey(args[0], extractable, usages);
                    case "RSAPSS":
                        return GenerateRsaPssKey(args[0], extractable, usages);
                    case "RSAOAEP":
                        return GenerateRsaOaepKey(args[0], extractable, usages);
                    case "ECDSA":
                        return GenerateEcdsaKey(args[0], extractable, usages);
                    case "ECDH":
                        return GenerateEcdhKey(args[0], extractable, usages);
                    case "AESGCM":
                        return GenerateAesGcmKey(args[0], extractable, usages);
                    case "AESCBC":
                        return GenerateAesCbcKey(args[0], extractable, usages);
                    case "AESCTR":
                        return GenerateAesCtrKey(args[0], extractable, usages);
                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue ImportKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 5)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: importKey requires format, keyData, algorithm, extractable, and keyUsages"));
            }

            try
            {
                var format = NormalizeKeyFormat(args[0]);
                if (string.IsNullOrEmpty(format))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key format is invalid"));
                }

                if (!TryParseKeyUsages(args[4], out var usages))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: keyUsages must be an array-like of strings"));
                }

                if (!TryNormalizeAlgorithmName(args[2], out var algorithmName))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                var extractable = args[3].ToBoolean();

                switch (algorithmName)
                {
                    case "HMAC":
                        return ImportHmacKey(format, args[1], args[2], extractable, usages);
                    case "RSASSAPKCS1V15":
                        return ImportRsaSsaKey(format, args[1], args[2], extractable, usages);
                    case "RSAPSS":
                        return ImportRsaPssKey(format, args[1], args[2], extractable, usages);
                    case "RSAOAEP":
                        return ImportRsaOaepKey(format, args[1], args[2], extractable, usages);
                    case "ECDSA":
                        return ImportEcdsaKey(format, args[1], args[2], extractable, usages);
                    case "ECDH":
                        return ImportEcdhKey(format, args[1], args[2], extractable, usages);
                    case "AESGCM":
                        return ImportAesGcmKey(format, args[1], args[2], extractable, usages);
                    case "AESCBC":
                        return ImportAesCbcKey(format, args[1], args[2], extractable, usages);
                    case "AESCTR":
                        return ImportAesCtrKey(format, args[1], args[2], extractable, usages);
                    case "PBKDF2":
                        return ImportPbkdf2Key(format, args[1], extractable, usages);
                    case "HKDF":
                        return ImportHkdfKey(format, args[1], extractable, usages);
                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue ExportKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: exportKey requires format and key"));
            }

            try
            {
                var format = NormalizeKeyFormat(args[0]);
                if (string.IsNullOrEmpty(format))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key format is invalid"));
                }

                if (!TryGetCryptoKeyState(args[1], out var keyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Provided key is not a CryptoKey"));
                }

                if (!keyState.Extractable)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key is not extractable"));
                }

                switch (keyState.AlgorithmName)
                {
                    case "HMAC":
                    {
                        if (!string.Equals(format, "raw", StringComparison.Ordinal))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HMAC export only supports raw format"));
                        }

                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(keyState.SecretKey))));
                    }

                    case "AESGCM":
                    {
                        if (!string.Equals(format, "raw", StringComparison.Ordinal))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-GCM export only supports raw format"));
                        }

                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(keyState.SecretKey))));
                    }

                    case "AESCBC":
                    {
                        if (!string.Equals(format, "raw", StringComparison.Ordinal))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-CBC export only supports raw format"));
                        }

                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(keyState.SecretKey))));
                    }

                    case "AESCTR":
                    {
                        if (!string.Equals(format, "raw", StringComparison.Ordinal))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-CTR export only supports raw format"));
                        }

                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(keyState.SecretKey))));
                    }

                    case "RSASSAPKCS1V15":
                    case "RSAPSS":
                    case "RSAOAEP":
                    {
                        if (string.Equals(format, "pkcs8", StringComparison.Ordinal))
                        {
                            if (!keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: pkcs8 export requires a private key"));
                            }

                            var pkcs8 = keyState.RsaKey.ExportPkcs8PrivateKey();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(pkcs8))));
                        }

                        if (string.Equals(format, "spki", StringComparison.Ordinal))
                        {
                            if (keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: spki export requires a public key"));
                            }

                            var spki = keyState.RsaKey.ExportSubjectPublicKeyInfo();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(spki))));
                        }

                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA export supports pkcs8 or spki only"));
                    }

                    case "ECDSA":
                    {
                        if (string.Equals(format, "pkcs8", StringComparison.Ordinal))
                        {
                            if (!keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: pkcs8 export requires a private key"));
                            }

                            var pkcs8 = keyState.EcdsaKey.ExportPkcs8PrivateKey();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(pkcs8))));
                        }

                        if (string.Equals(format, "spki", StringComparison.Ordinal))
                        {
                            if (keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: spki export requires a public key"));
                            }

                            var spki = keyState.EcdsaKey.ExportSubjectPublicKeyInfo();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(spki))));
                        }

                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDSA export supports pkcs8 or spki only"));
                    }

                    case "ECDH":
                    {
                        if (string.Equals(format, "pkcs8", StringComparison.Ordinal))
                        {
                            if (!keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: pkcs8 export requires a private key"));
                            }

                            var pkcs8 = keyState.EcdhKey.ExportPkcs8PrivateKey();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(pkcs8))));
                        }

                        if (string.Equals(format, "spki", StringComparison.Ordinal))
                        {
                            if (keyState.IsPrivateKey)
                            {
                                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: spki export requires a public key"));
                            }

                            var spki = keyState.EcdhKey.ExportSubjectPublicKeyInfo();
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(spki))));
                        }

                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDH export supports pkcs8 or spki only"));
                    }

                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue Encrypt(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 3)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: encrypt requires algorithm, key, and data"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var keyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Provided key is not a CryptoKey"));
                }

                if (!keyState.Usages.Contains("encrypt"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow encryption"));
                }

                if (!TryNormalizeAlgorithmName(args[0], out var operationAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!string.Equals(operationAlgorithm, keyState.AlgorithmName, StringComparison.Ordinal))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Operation algorithm does not match key algorithm"));
                }

                if (!TryExtractDigestBytes(args[2], out var plaintext))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: encrypt data is invalid"));
                }

                if (string.Equals(operationAlgorithm, "AESGCM", StringComparison.Ordinal))
                {
                    if (!TryResolveAesGcmParams(args[0], out var iv, out var additionalData, out var tagLengthBits))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM parameters are invalid"));
                    }

                    var tagLengthBytes = tagLengthBits / 8;
                    var ciphertext = new byte[plaintext.Length];
                    var tag = new byte[tagLengthBytes];

                    using (var aes = new System.Security.Cryptography.AesGcm(keyState.SecretKey))
                    {
                        aes.Encrypt(iv, plaintext, ciphertext, tag, additionalData.Length == 0 ? null : additionalData);
                    }

                    var payload = new byte[ciphertext.Length + tag.Length];
                    Buffer.BlockCopy(ciphertext, 0, payload, 0, ciphertext.Length);
                    Buffer.BlockCopy(tag, 0, payload, ciphertext.Length, tag.Length);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(payload))));
                }

                if (string.Equals(operationAlgorithm, "AESCBC", StringComparison.Ordinal))
                {
                    if (!TryResolveAesCbcParams(args[0], out var iv))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC parameters are invalid"));
                    }

                    using var aes = System.Security.Cryptography.Aes.Create();
                    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                    aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                    aes.Key = keyState.SecretKey;
                    aes.IV = iv;

                    using var encryptor = aes.CreateEncryptor();
                    var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(ciphertext))));
                }

                if (string.Equals(operationAlgorithm, "AESCTR", StringComparison.Ordinal))
                {
                    if (!TryResolveAesCtrParams(args[0], out var counter, out var counterLengthBits, out var ctrParamError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(ctrParamError));
                    }

                    if (!TryValidateAesCtrCounterCapacity(counter, counterLengthBits, plaintext.Length, out var ctrCapacityError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(ctrCapacityError));
                    }

                    var ciphertext = TransformAesCtr(keyState.SecretKey, counter, counterLengthBits, plaintext);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(ciphertext))));
                }

                if (string.Equals(operationAlgorithm, "RSAOAEP", StringComparison.Ordinal))
                {
                    if (keyState.IsPrivateKey)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: RSA-OAEP encrypt requires a public key"));
                    }

                    if (!TryEnsureRsaOperationHashMatchesKey(args[0], keyState.HashName, out var hashMismatchError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(hashMismatchError));
                    }

                    if (!TryResolveRsaOaepPadding(args[0], keyState.HashName, out var padding))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP hash algorithm is invalid"));
                    }

                    if (!TryResolveRsaOaepLabel(args[0], out var label))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA-OAEP label is invalid"));
                    }

                    if (label.Length > 0)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP non-empty label is not supported"));
                    }

                    var ciphertext = keyState.RsaKey.Encrypt(plaintext, padding);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(ciphertext))));
                }

                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue Decrypt(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 3)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: decrypt requires algorithm, key, and data"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var keyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Provided key is not a CryptoKey"));
                }

                if (!keyState.Usages.Contains("decrypt"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow decryption"));
                }

                if (!TryNormalizeAlgorithmName(args[0], out var operationAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!string.Equals(operationAlgorithm, keyState.AlgorithmName, StringComparison.Ordinal))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Operation algorithm does not match key algorithm"));
                }

                if (!TryExtractDigestBytes(args[2], out var encryptedPayload))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: decrypt data is invalid"));
                }

                if (string.Equals(operationAlgorithm, "AESGCM", StringComparison.Ordinal))
                {
                    if (!TryResolveAesGcmParams(args[0], out var iv, out var additionalData, out var tagLengthBits))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM parameters are invalid"));
                    }

                    var tagLengthBytes = tagLengthBits / 8;
                    if (encryptedPayload.Length < tagLengthBytes)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("OperationError: AES-GCM ciphertext is shorter than authentication tag"));
                    }

                    var ciphertextLength = encryptedPayload.Length - tagLengthBytes;
                    var ciphertext = new byte[ciphertextLength];
                    var tag = new byte[tagLengthBytes];
                    Buffer.BlockCopy(encryptedPayload, 0, ciphertext, 0, ciphertextLength);
                    Buffer.BlockCopy(encryptedPayload, ciphertextLength, tag, 0, tagLengthBytes);

                    var plaintext = new byte[ciphertextLength];
                    using (var aes = new System.Security.Cryptography.AesGcm(keyState.SecretKey))
                    {
                        aes.Decrypt(iv, ciphertext, tag, plaintext, additionalData.Length == 0 ? null : additionalData);
                    }

                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(plaintext))));
                }

                if (string.Equals(operationAlgorithm, "AESCBC", StringComparison.Ordinal))
                {
                    if (!TryResolveAesCbcParams(args[0], out var iv))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC parameters are invalid"));
                    }

                    using var aes = System.Security.Cryptography.Aes.Create();
                    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                    aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                    aes.Key = keyState.SecretKey;
                    aes.IV = iv;

                    using var decryptor = aes.CreateDecryptor();
                    var plaintext = decryptor.TransformFinalBlock(encryptedPayload, 0, encryptedPayload.Length);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(plaintext))));
                }

                if (string.Equals(operationAlgorithm, "AESCTR", StringComparison.Ordinal))
                {
                    if (!TryResolveAesCtrParams(args[0], out var counter, out var counterLengthBits, out var ctrParamError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(ctrParamError));
                    }

                    if (!TryValidateAesCtrCounterCapacity(counter, counterLengthBits, encryptedPayload.Length, out var ctrCapacityError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(ctrCapacityError));
                    }

                    var plaintext = TransformAesCtr(keyState.SecretKey, counter, counterLengthBits, encryptedPayload);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(plaintext))));
                }

                if (string.Equals(operationAlgorithm, "RSAOAEP", StringComparison.Ordinal))
                {
                    if (!keyState.IsPrivateKey)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: RSA-OAEP decrypt requires a private key"));
                    }

                    if (!TryEnsureRsaOperationHashMatchesKey(args[0], keyState.HashName, out var hashMismatchError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(hashMismatchError));
                    }

                    if (!TryResolveRsaOaepPadding(args[0], keyState.HashName, out var padding))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP hash algorithm is invalid"));
                    }

                    if (!TryResolveRsaOaepLabel(args[0], out var label))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA-OAEP label is invalid"));
                    }

                    if (label.Length > 0)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP non-empty label is not supported"));
                    }

                    var decryptedPayload = keyState.RsaKey.Decrypt(encryptedPayload, padding);
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(decryptedPayload))));
                }

                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue Sign(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 3)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: sign requires algorithm, key, and data"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var keyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Provided key is not a CryptoKey"));
                }

                if (!keyState.Usages.Contains("sign"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow signing"));
                }

                if (!TryExtractDigestBytes(args[2], out var data))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: sign data is invalid"));
                }

                if (!TryNormalizeAlgorithmName(args[0], out var operationAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!string.Equals(operationAlgorithm, keyState.AlgorithmName, StringComparison.Ordinal))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Operation algorithm does not match key algorithm"));
                }

                if ((string.Equals(keyState.AlgorithmName, "RSASSAPKCS1V15", StringComparison.Ordinal)
                    || string.Equals(keyState.AlgorithmName, "RSAPSS", StringComparison.Ordinal))
                    && !TryEnsureRsaOperationHashMatchesKey(args[0], keyState.HashName, out var rsaHashMismatchError))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected(rsaHashMismatchError));
                }

                if (!TryResolveHashNameForOperation(args[0], keyState.HashName, out var hashName))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                }

                byte[] signature;
                if (string.Equals(keyState.AlgorithmName, "HMAC", StringComparison.Ordinal))
                {
                    using var hmac = CreateHmac(hashName, keyState.SecretKey);
                    if (hmac == null)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    signature = hmac.ComputeHash(data);
                }
                else if (string.Equals(keyState.AlgorithmName, "RSASSAPKCS1V15", StringComparison.Ordinal))
                {
                    if (!keyState.IsPrivateKey)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Signing requires a private key"));
                    }

                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    signature = keyState.RsaKey.SignData(data, hashAlgorithm, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                }
                else if (string.Equals(keyState.AlgorithmName, "RSAPSS", StringComparison.Ordinal))
                {
                    if (!keyState.IsPrivateKey)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Signing requires a private key"));
                    }

                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    if (!TryResolveRsaPssSaltLength(args[0], hashName, out _, out var saltLengthError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(saltLengthError));
                    }

                    signature = keyState.RsaKey.SignData(data, hashAlgorithm, System.Security.Cryptography.RSASignaturePadding.Pss);
                }
                else if (string.Equals(keyState.AlgorithmName, "ECDSA", StringComparison.Ordinal))
                {
                    if (!keyState.IsPrivateKey)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Signing requires a private key"));
                    }

                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    signature = keyState.EcdsaKey.SignData(data, hashAlgorithm);
                }
                else
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }

                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(signature))));
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue Verify(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 4)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: verify requires algorithm, key, signature, and data"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var keyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Provided key is not a CryptoKey"));
                }

                if (!keyState.Usages.Contains("verify"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow verification"));
                }

                if (!TryExtractDigestBytes(args[2], out var signature))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: verify signature is invalid"));
                }

                if (!TryExtractDigestBytes(args[3], out var data))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: verify data is invalid"));
                }

                if (!TryNormalizeAlgorithmName(args[0], out var operationAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!string.Equals(operationAlgorithm, keyState.AlgorithmName, StringComparison.Ordinal))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Operation algorithm does not match key algorithm"));
                }

                if ((string.Equals(keyState.AlgorithmName, "RSASSAPKCS1V15", StringComparison.Ordinal)
                    || string.Equals(keyState.AlgorithmName, "RSAPSS", StringComparison.Ordinal))
                    && !TryEnsureRsaOperationHashMatchesKey(args[0], keyState.HashName, out var rsaHashMismatchError))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected(rsaHashMismatchError));
                }

                if (!TryResolveHashNameForOperation(args[0], keyState.HashName, out var hashName))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                }

                bool valid;
                if (string.Equals(keyState.AlgorithmName, "HMAC", StringComparison.Ordinal))
                {
                    using var hmac = CreateHmac(hashName, keyState.SecretKey);
                    if (hmac == null)
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    var expected = hmac.ComputeHash(data);
                    valid = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, signature);
                }
                else if (string.Equals(keyState.AlgorithmName, "RSASSAPKCS1V15", StringComparison.Ordinal))
                {
                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    valid = keyState.RsaKey.VerifyData(data, signature, hashAlgorithm, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                }
                else if (string.Equals(keyState.AlgorithmName, "RSAPSS", StringComparison.Ordinal))
                {
                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    if (!TryResolveRsaPssSaltLength(args[0], hashName, out _, out var saltLengthError))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected(saltLengthError));
                    }

                    valid = keyState.RsaKey.VerifyData(data, signature, hashAlgorithm, System.Security.Cryptography.RSASignaturePadding.Pss);
                }
                else if (string.Equals(keyState.AlgorithmName, "ECDSA", StringComparison.Ordinal))
                {
                    if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                    {
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                    }

                    valid = keyState.EcdsaKey.VerifyData(data, signature, hashAlgorithm);
                }
                else
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }

                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromBoolean(valid)));
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue WrapKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 4)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: wrapKey requires format, key, wrappingKey, and wrapAlgorithm"));
            }

            try
            {
                var format = NormalizeKeyFormat(args[0]);
                if (string.IsNullOrEmpty(format))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key format is invalid"));
                }

                if (!TryGetCryptoKeyState(args[2], out var wrappingKeyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: wrappingKey is not a CryptoKey"));
                }

                if (!wrappingKeyState.Usages.Contains("wrapkey"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow key wrapping"));
                }

                var exportResult = ExportKey(new[] { args[0], args[1] }, thisVal);
                if (!TryGetFulfilledThenableResult(exportResult, out var exportedKeyData, out _))
                {
                    return exportResult;
                }

                var addedEncryptUsage = wrappingKeyState.Usages.Add("encrypt");
                try
                {
                    return Encrypt(new[] { args[3], args[2], exportedKeyData }, thisVal);
                }
                finally
                {
                    if (addedEncryptUsage)
                    {
                        wrappingKeyState.Usages.Remove("encrypt");
                    }
                }
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue UnwrapKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 7)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: unwrapKey requires format, wrappedKey, unwrappingKey, unwrapAlgorithm, unwrappedKeyAlgorithm, extractable, and keyUsages"));
            }

            try
            {
                var format = NormalizeKeyFormat(args[0]);
                if (string.IsNullOrEmpty(format))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key format is invalid"));
                }

                if (!TryGetCryptoKeyState(args[2], out var unwrappingKeyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: unwrappingKey is not a CryptoKey"));
                }

                if (!unwrappingKeyState.Usages.Contains("unwrapkey"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow key unwrapping"));
                }

                var addedDecryptUsage = unwrappingKeyState.Usages.Add("decrypt");
                FenValue decryptResult;
                try
                {
                    decryptResult = Decrypt(new[] { args[3], args[2], args[1] }, thisVal);
                }
                finally
                {
                    if (addedDecryptUsage)
                    {
                        unwrappingKeyState.Usages.Remove("decrypt");
                    }
                }

                if (!TryGetFulfilledThenableResult(decryptResult, out var rawKeyData, out _))
                {
                    return decryptResult;
                }

                return ImportKey(new[] { args[0], rawKeyData, args[4], args[5], args[6] }, thisVal);
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue DeriveBits(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 3)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: deriveBits requires algorithm, baseKey, and length"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var baseKeyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: baseKey is not a CryptoKey"));
                }

                if (!baseKeyState.Usages.Contains("derivebits"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow bit derivation"));
                }

                if (!TryNormalizeAlgorithmName(args[0], out var operationAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Algorithm is invalid"));
                }

                if (!string.Equals(operationAlgorithm, baseKeyState.AlgorithmName, StringComparison.Ordinal))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Operation algorithm does not match key algorithm"));
                }

                if (!args[2].IsNumber)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: length must be a number"));
                }

                var requestedBitsNumber = args[2].ToNumber();
                if (double.IsNaN(requestedBitsNumber) || double.IsInfinity(requestedBitsNumber))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: length must be a positive multiple of 8"));
                }

                if (Math.Floor(requestedBitsNumber) != requestedBitsNumber)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: length must be a positive multiple of 8"));
                }

                if (requestedBitsNumber > int.MaxValue)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: length must be a positive multiple of 8"));
                }

                var requestedBits = (int)Math.Floor(requestedBitsNumber);
                if (requestedBits <= 0 || requestedBits % 8 != 0)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: length must be a positive multiple of 8"));
                }

                switch (operationAlgorithm)
                {
                    case "PBKDF2":
                    {
                        if (!TryResolvePbkdf2Params(args[0], out var salt, out var iterations, out var hashName))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: PBKDF2 parameters are invalid"));
                        }

                        if (!TryResolveHashAlgorithm(hashName, out var hashAlgorithm))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                        }

                        using var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(baseKeyState.SecretKey, salt, iterations, hashAlgorithm);
                        var derived = kdf.GetBytes(requestedBits / 8);
                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(derived))));
                    }
                    case "HKDF":
                    {
                        if (!TryResolveHkdfParams(args[0], out var salt, out var info, out var hashName))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HKDF parameters are invalid"));
                        }

                        if (!TryGetHashOutputLengthBytes(hashName, out var hashOutputLengthBytes))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Hash algorithm not supported"));
                        }

                        var requestedBytes = requestedBits / 8;
                        var maxOutputBytes = 255 * hashOutputLengthBytes;
                        if (requestedBytes > maxOutputBytes)
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("OperationError: HKDF output length exceeds RFC 5869 limit"));
                        }

                        if (!TryComputeHkdf(baseKeyState.SecretKey, salt, info, hashName, requestedBytes, out var derived))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("OperationError: Failed to derive HKDF output"));
                        }

                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(derived))));
                    }
                    case "ECDH":
                    {
                        if (!baseKeyState.IsPrivateKey)
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: ECDH deriveBits requires a private key"));
                        }

                        if (!TryResolveEcdhPublicKey(args[0], out var publicKeyState, out var resolveError))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected(resolveError));
                        }

                        if (!string.Equals(baseKeyState.NamedCurve, publicKeyState.NamedCurve, StringComparison.Ordinal))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: ECDH keys must use the same namedCurve"));
                        }

                        var sharedSecret = baseKeyState.EcdhKey.DeriveKeyMaterial(publicKeyState.EcdhKey.PublicKey);
                        var requestedBytes = requestedBits / 8;
                        if (requestedBytes > sharedSecret.Length)
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("OperationError: Requested ECDH output length exceeds available shared secret length"));
                        }

                        var output = new byte[requestedBytes];
                        Buffer.BlockCopy(sharedSecret, 0, output, 0, requestedBytes);
                        return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateArrayBuffer(output))));
                    }

                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue DeriveKey(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 5)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: deriveKey requires algorithm, baseKey, derivedKeyAlgorithm, extractable, and keyUsages"));
            }

            try
            {
                if (!TryGetCryptoKeyState(args[1], out var baseKeyState))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: baseKey is not a CryptoKey"));
                }

                if (!baseKeyState.Usages.Contains("derivekey"))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: Key does not allow key derivation"));
                }

                if (!TryNormalizeAlgorithmName(args[2], out var derivedAlgorithm))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Derived key algorithm is invalid"));
                }

                int requiredBits;
                switch (derivedAlgorithm)
                {
                    case "AESGCM":
                        if (!TryResolveAesKeyLengthBitsForGenerate(args[2], out requiredBits))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM key length must be 128, 192, or 256"));
                        }

                        break;
                    case "HMAC":
                    {
                        if (!TryResolveHashNameForOperation(args[2], null, out var hashName))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HMAC hash algorithm is invalid"));
                        }

                        if (!TryResolveHmacLengthBits(args[2], hashName, out requiredBits))
                        {
                            return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HMAC length must be a positive multiple of 8"));
                        }

                        break;
                    }
                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Derived key algorithm not supported"));
                }

                var addedDeriveBitsUsage = baseKeyState.Usages.Add("derivebits");
                FenValue deriveBitsResult;
                try
                {
                    deriveBitsResult = DeriveBits(new[] { args[0], args[1], FenValue.FromNumber(requiredBits) }, thisVal);
                }
                finally
                {
                    if (addedDeriveBitsUsage)
                    {
                        baseKeyState.Usages.Remove("derivebits");
                    }
                }

                if (!TryGetFulfilledThenableResult(deriveBitsResult, out var rawKeyData, out _))
                {
                    return deriveBitsResult;
                }

                return ImportKey(new[] { FenValue.FromString("raw"), rawKeyData, args[2], args[3], args[4] }, thisVal);
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue Digest(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: digest requires algorithm and data"));
            }

            try
            {
                if (!TryNormalizeAlgorithmName(args[0], out var algoName))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: digest algorithm is invalid"));
                }

                if (!TryExtractDigestBytes(args[1], out var inputData))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: digest data is invalid"));
                }

                byte[] hash;
                switch (algoName)
                {
                    case "SHA1":
                        using (var sha1 = System.Security.Cryptography.SHA1.Create())
                            hash = sha1.ComputeHash(inputData);
                        break;
                    case "SHA256":
                        using (var sha256 = System.Security.Cryptography.SHA256.Create())
                            hash = sha256.ComputeHash(inputData);
                        break;
                    case "SHA384":
                        using (var sha384 = System.Security.Cryptography.SHA384.Create())
                            hash = sha384.ComputeHash(inputData);
                        break;
                    case "SHA512":
                        using (var sha512 = System.Security.Cryptography.SHA512.Create())
                            hash = sha512.ComputeHash(inputData);
                        break;
                    default:
                        return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Algorithm not supported"));
                }

                var resultBuffer = new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(hash.Length);
                Array.Copy(hash, resultBuffer.Data, hash.Length);
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(resultBuffer)));
            }
            catch (Exception ex)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"OperationError: {ex.Message}"));
            }
        }

        private static FenValue ImportHmacKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HMAC import supports raw format only"));
            }

            if (!ValidateKeyUsages(usages, HmacAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for HMAC"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, null, out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HMAC hash algorithm is invalid"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "HMAC",
                HashName = hashName,
                KeyType = "secret",
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue GenerateHmacKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HMAC generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, HmacAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for HMAC"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, null, out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HMAC hash algorithm is invalid"));
            }

            if (!TryResolveHmacLengthBits(algorithmArg, hashName, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HMAC length must be a positive multiple of 8"));
            }

            var keyBytes = new byte[keyLengthBits / 8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "HMAC",
                HashName = hashName,
                KeyType = "secret",
                Extractable = extractable,
                SecretKey = keyBytes,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue GenerateAesGcmKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-GCM"));
            }

            if (!TryResolveAesKeyLengthBitsForGenerate(algorithmArg, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM key length must be 128, 192, or 256"));
            }

            var keyBytes = new byte[keyLengthBits / 8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESGCM",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = keyBytes,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue GenerateAesCbcKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-CBC"));
            }

            if (!TryResolveAesKeyLengthBitsForGenerate(algorithmArg, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC key length must be 128, 192, or 256"));
            }

            var keyBytes = new byte[keyLengthBits / 8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESCBC",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = keyBytes,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue GenerateAesCtrKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CTR generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-CTR"));
            }

            if (!TryResolveAesKeyLengthBitsForGenerate(algorithmArg, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CTR key length must be 128, 192, or 256"));
            }

            var keyBytes = new byte[keyLengthBits / 8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESCTR",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = keyBytes,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportRsaSsaKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            var isPrivate = string.Equals(format, "pkcs8", StringComparison.Ordinal);
            var isPublic = string.Equals(format, "spki", StringComparison.Ordinal);

            if (!isPrivate && !isPublic)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSASSA import supports pkcs8 and spki formats"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            var allowedUsages = isPrivate ? RsaPrivateAllowedUsages : RsaPublicAllowedUsages;
            if (!ValidateKeyUsages(usages, allowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSASSA-PKCS1-v1_5"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA hash algorithm is invalid"));
            }

            var rsa = System.Security.Cryptography.RSA.Create();
            if (isPrivate)
            {
                rsa.ImportPkcs8PrivateKey(keyBytes, out _);
            }
            else
            {
                rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSASSAPKCS1V15",
                HashName = hashName,
                KeyType = isPrivate ? "private" : "public",
                Extractable = extractable,
                RsaKey = rsa,
                IsPrivateKey = isPrivate,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportRsaOaepKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            var isPrivate = string.Equals(format, "pkcs8", StringComparison.Ordinal);
            var isPublic = string.Equals(format, "spki", StringComparison.Ordinal);

            if (!isPrivate && !isPublic)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP import supports pkcs8 and spki formats"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            var allowedUsages = isPrivate ? RsaOaepPrivateAllowedUsages : RsaOaepPublicAllowedUsages;
            if (!ValidateKeyUsages(usages, allowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSA-OAEP"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP hash algorithm is invalid"));
            }

            var rsa = System.Security.Cryptography.RSA.Create();
            if (isPrivate)
            {
                rsa.ImportPkcs8PrivateKey(keyBytes, out _);
            }
            else
            {
                rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAOAEP",
                HashName = hashName,
                KeyType = isPrivate ? "private" : "public",
                Extractable = extractable,
                RsaKey = rsa,
                IsPrivateKey = isPrivate,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportRsaPssKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            var isPrivate = string.Equals(format, "pkcs8", StringComparison.Ordinal);
            var isPublic = string.Equals(format, "spki", StringComparison.Ordinal);

            if (!isPrivate && !isPublic)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-PSS import supports pkcs8 and spki formats"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            var allowedUsages = isPrivate ? RsaPrivateAllowedUsages : RsaPublicAllowedUsages;
            if (!ValidateKeyUsages(usages, allowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSA-PSS"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-PSS hash algorithm is invalid"));
            }

            var rsa = System.Security.Cryptography.RSA.Create();
            if (isPrivate)
            {
                rsa.ImportPkcs8PrivateKey(keyBytes, out _);
            }
            else
            {
                rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAPSS",
                HashName = hashName,
                KeyType = isPrivate ? "private" : "public",
                Extractable = extractable,
                RsaKey = rsa,
                IsPrivateKey = isPrivate,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportEcdsaKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            var isPrivate = string.Equals(format, "pkcs8", StringComparison.Ordinal);
            var isPublic = string.Equals(format, "spki", StringComparison.Ordinal);

            if (!isPrivate && !isPublic)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDSA import supports pkcs8 and spki formats"));
            }

            if (!TryResolveNamedCurve(algorithmArg, out var namedCurve, out _))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDSA namedCurve is invalid"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            var allowedUsages = isPrivate ? RsaPrivateAllowedUsages : RsaPublicAllowedUsages;
            if (!ValidateKeyUsages(usages, allowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for ECDSA"));
            }

            var ecdsa = System.Security.Cryptography.ECDsa.Create();
            if (isPrivate)
            {
                ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
            }
            else
            {
                ecdsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            if (!TryGetNamedCurveFromCurve(ecdsa.ExportParameters(false).Curve, out var actualNamedCurve))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Imported ECDSA key curve is unsupported"));
            }

            if (!string.Equals(namedCurve, actualNamedCurve, StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("DataError: Imported ECDSA key curve does not match requested namedCurve"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDSA",
                NamedCurve = actualNamedCurve,
                KeyType = isPrivate ? "private" : "public",
                Extractable = extractable,
                EcdsaKey = ecdsa,
                IsPrivateKey = isPrivate,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportEcdhKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            var isPrivate = string.Equals(format, "pkcs8", StringComparison.Ordinal);
            var isPublic = string.Equals(format, "spki", StringComparison.Ordinal);

            if (!isPrivate && !isPublic)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDH import supports pkcs8 and spki formats"));
            }

            if (!TryResolveNamedCurve(algorithmArg, out var namedCurve, out _))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDH namedCurve is invalid"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (isPrivate)
            {
                if (usages.Count == 0)
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: ECDH private key import requires at least one key usage"));
                }

                if (!ValidateKeyUsages(usages, EcdhPrivateAllowedUsages, out var unsupportedUsage))
                {
                    return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for ECDH"));
                }
            }
            else if (usages.Count != 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidAccessError: ECDH public key import does not support key usages"));
            }

            var ecdh = System.Security.Cryptography.ECDiffieHellman.Create();
            if (isPrivate)
            {
                ecdh.ImportPkcs8PrivateKey(keyBytes, out _);
            }
            else
            {
                ecdh.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }

            if (!TryGetNamedCurveFromCurve(ecdh.ExportParameters(false).Curve, out var actualNamedCurve))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: Imported ECDH key curve is unsupported"));
            }

            if (!string.Equals(namedCurve, actualNamedCurve, StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("DataError: Imported ECDH key curve does not match requested namedCurve"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDH",
                NamedCurve = actualNamedCurve,
                KeyType = isPrivate ? "private" : "public",
                Extractable = extractable,
                EcdhKey = ecdh,
                IsPrivateKey = isPrivate,
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportAesGcmKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-GCM import supports raw format only"));
            }

            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM import requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-GCM"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (!TryResolveAesKeyLengthBitsForImport(algorithmArg, keyBytes.Length, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-GCM key length must be 128, 192, or 256 and match raw key data"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESGCM",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportAesCbcKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-CBC import supports raw format only"));
            }

            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC import requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-CBC"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (!TryResolveAesKeyLengthBitsForImport(algorithmArg, keyBytes.Length, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CBC key length must be 128, 192, or 256 and match raw key data"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESCBC",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportAesCtrKey(string format, FenValue keyData, FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: AES-CTR import supports raw format only"));
            }

            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CTR import requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, AesAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for AES-CTR"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (!TryResolveAesKeyLengthBitsForImport(algorithmArg, keyBytes.Length, out var keyLengthBits))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: AES-CTR key length must be 128, 192, or 256 and match raw key data"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "AESCTR",
                KeyType = "secret",
                KeyLengthBits = keyLengthBits,
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportPbkdf2Key(string format, FenValue keyData, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: PBKDF2 import supports raw format only"));
            }

            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: PBKDF2 import requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, Pbkdf2AllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for PBKDF2"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (keyBytes.Length == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: PBKDF2 key material must not be empty"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "PBKDF2",
                KeyType = "secret",
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue ImportHkdfKey(string format, FenValue keyData, bool extractable, HashSet<string> usages)
        {
            if (!string.Equals(format, "raw", StringComparison.Ordinal))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: HKDF import supports raw format only"));
            }

            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HKDF import requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, HkdfAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for HKDF"));
            }

            if (!TryExtractDigestBytes(keyData, out var keyBytes))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: Key data is invalid"));
            }

            if (keyBytes.Length == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: HKDF key material must not be empty"));
            }

            var state = new LegacyCryptoKeyState
            {
                AlgorithmName = "HKDF",
                KeyType = "secret",
                Extractable = extractable,
                SecretKey = (byte[])keyBytes.Clone(),
                Usages = usages
            };

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateCryptoKeyObject(state))));
        }

        private static FenValue GenerateRsaSsaKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSASSA-PKCS1-v1_5 generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, new HashSet<string>(new[] { "sign", "verify" }, StringComparer.Ordinal), out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSASSA-PKCS1-v1_5"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA hash algorithm is invalid"));
            }

            if (!TryResolveRsaModulusLength(algorithmArg, out var modulusLength))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA modulusLength must be a positive integer"));
            }

            if (!TryResolveRsaPublicExponent(algorithmArg, out var publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA publicExponent is invalid"));
            }

            if (!IsSupportedRsaPublicExponent(publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA publicExponent currently supports 65537 only"));
            }

            using var generatedRsa = System.Security.Cryptography.RSA.Create(modulusLength);
            var privatePkcs8 = generatedRsa.ExportPkcs8PrivateKey();
            var publicSpki = generatedRsa.ExportSubjectPublicKeyInfo();

            var privateRsa = System.Security.Cryptography.RSA.Create();
            privateRsa.ImportPkcs8PrivateKey(privatePkcs8, out _);

            var publicRsa = System.Security.Cryptography.RSA.Create();
            publicRsa.ImportSubjectPublicKeyInfo(publicSpki, out _);

            var privateUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("sign"))
            {
                privateUsages.Add("sign");
            }

            var publicUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("verify"))
            {
                publicUsages.Add("verify");
            }

            var privateState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSASSAPKCS1V15",
                HashName = hashName,
                KeyType = "private",
                Extractable = extractable,
                RsaKey = privateRsa,
                IsPrivateKey = true,
                Usages = privateUsages
            };

            var publicState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSASSAPKCS1V15",
                HashName = hashName,
                KeyType = "public",
                Extractable = true,
                RsaKey = publicRsa,
                IsPrivateKey = false,
                Usages = publicUsages
            };

            var keyPair = new FenObject();
            keyPair.Set("privateKey", FenValue.FromObject(CreateCryptoKeyObject(privateState)));
            keyPair.Set("publicKey", FenValue.FromObject(CreateCryptoKeyObject(publicState)));
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(keyPair)));
        }

        private static FenValue GenerateRsaOaepKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA-OAEP generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, RsaOaepGenerateAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSA-OAEP"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-OAEP hash algorithm is invalid"));
            }

            if (!TryResolveRsaModulusLength(algorithmArg, out var modulusLength))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA modulusLength must be a positive integer"));
            }

            if (!TryResolveRsaPublicExponent(algorithmArg, out var publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA publicExponent is invalid"));
            }

            if (!IsSupportedRsaPublicExponent(publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA publicExponent currently supports 65537 only"));
            }

            using var generatedRsa = System.Security.Cryptography.RSA.Create(modulusLength);
            var privatePkcs8 = generatedRsa.ExportPkcs8PrivateKey();
            var publicSpki = generatedRsa.ExportSubjectPublicKeyInfo();

            var privateRsa = System.Security.Cryptography.RSA.Create();
            privateRsa.ImportPkcs8PrivateKey(privatePkcs8, out _);

            var publicRsa = System.Security.Cryptography.RSA.Create();
            publicRsa.ImportSubjectPublicKeyInfo(publicSpki, out _);

            var privateUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("decrypt"))
            {
                privateUsages.Add("decrypt");
            }

            if (usages.Contains("unwrapkey"))
            {
                privateUsages.Add("unwrapkey");
            }

            var publicUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("encrypt"))
            {
                publicUsages.Add("encrypt");
            }

            if (usages.Contains("wrapkey"))
            {
                publicUsages.Add("wrapkey");
            }

            var privateState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAOAEP",
                HashName = hashName,
                KeyType = "private",
                Extractable = extractable,
                RsaKey = privateRsa,
                IsPrivateKey = true,
                Usages = privateUsages
            };

            var publicState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAOAEP",
                HashName = hashName,
                KeyType = "public",
                Extractable = true,
                RsaKey = publicRsa,
                IsPrivateKey = false,
                Usages = publicUsages
            };

            var keyPair = new FenObject();
            keyPair.Set("privateKey", FenValue.FromObject(CreateCryptoKeyObject(privateState)));
            keyPair.Set("publicKey", FenValue.FromObject(CreateCryptoKeyObject(publicState)));
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(keyPair)));
        }

        private static FenValue GenerateRsaPssKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA-PSS generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, new HashSet<string>(new[] { "sign", "verify" }, StringComparer.Ordinal), out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for RSA-PSS"));
            }

            if (!TryResolveHashNameForOperation(algorithmArg, "SHA256", out var hashName))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA-PSS hash algorithm is invalid"));
            }

            if (!TryResolveRsaModulusLength(algorithmArg, out var modulusLength))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA modulusLength must be a positive integer"));
            }

            if (!TryResolveRsaPublicExponent(algorithmArg, out var publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: RSA publicExponent is invalid"));
            }

            if (!IsSupportedRsaPublicExponent(publicExponent))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: RSA publicExponent currently supports 65537 only"));
            }

            using var generatedRsa = System.Security.Cryptography.RSA.Create(modulusLength);
            var privatePkcs8 = generatedRsa.ExportPkcs8PrivateKey();
            var publicSpki = generatedRsa.ExportSubjectPublicKeyInfo();

            var privateRsa = System.Security.Cryptography.RSA.Create();
            privateRsa.ImportPkcs8PrivateKey(privatePkcs8, out _);

            var publicRsa = System.Security.Cryptography.RSA.Create();
            publicRsa.ImportSubjectPublicKeyInfo(publicSpki, out _);

            var privateUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("sign"))
            {
                privateUsages.Add("sign");
            }

            var publicUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("verify"))
            {
                publicUsages.Add("verify");
            }

            var privateState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAPSS",
                HashName = hashName,
                KeyType = "private",
                Extractable = extractable,
                RsaKey = privateRsa,
                IsPrivateKey = true,
                Usages = privateUsages
            };

            var publicState = new LegacyCryptoKeyState
            {
                AlgorithmName = "RSAPSS",
                HashName = hashName,
                KeyType = "public",
                Extractable = true,
                RsaKey = publicRsa,
                IsPrivateKey = false,
                Usages = publicUsages
            };

            var keyPair = new FenObject();
            keyPair.Set("privateKey", FenValue.FromObject(CreateCryptoKeyObject(privateState)));
            keyPair.Set("publicKey", FenValue.FromObject(CreateCryptoKeyObject(publicState)));
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(keyPair)));
        }

        private static FenValue GenerateEcdsaKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: ECDSA generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, new HashSet<string>(new[] { "sign", "verify" }, StringComparer.Ordinal), out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for ECDSA"));
            }

            if (!TryResolveNamedCurve(algorithmArg, out var namedCurve, out var curve))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDSA namedCurve is invalid"));
            }

            using var generatedEcdsa = System.Security.Cryptography.ECDsa.Create(curve);
            var privatePkcs8 = generatedEcdsa.ExportPkcs8PrivateKey();
            var publicSpki = generatedEcdsa.ExportSubjectPublicKeyInfo();

            var privateEcdsa = System.Security.Cryptography.ECDsa.Create();
            privateEcdsa.ImportPkcs8PrivateKey(privatePkcs8, out _);

            var publicEcdsa = System.Security.Cryptography.ECDsa.Create();
            publicEcdsa.ImportSubjectPublicKeyInfo(publicSpki, out _);

            var privateUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("sign"))
            {
                privateUsages.Add("sign");
            }

            var publicUsages = new HashSet<string>(StringComparer.Ordinal);
            if (usages.Contains("verify"))
            {
                publicUsages.Add("verify");
            }

            var privateState = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDSA",
                NamedCurve = namedCurve,
                KeyType = "private",
                Extractable = extractable,
                EcdsaKey = privateEcdsa,
                IsPrivateKey = true,
                Usages = privateUsages
            };

            var publicState = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDSA",
                NamedCurve = namedCurve,
                KeyType = "public",
                Extractable = true,
                EcdsaKey = publicEcdsa,
                IsPrivateKey = false,
                Usages = publicUsages
            };

            var keyPair = new FenObject();
            keyPair.Set("privateKey", FenValue.FromObject(CreateCryptoKeyObject(privateState)));
            keyPair.Set("publicKey", FenValue.FromObject(CreateCryptoKeyObject(publicState)));
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(keyPair)));
        }

        private static FenValue GenerateEcdhKey(FenValue algorithmArg, bool extractable, HashSet<string> usages)
        {
            if (usages.Count == 0)
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("TypeError: ECDH generateKey requires at least one key usage"));
            }

            if (!ValidateKeyUsages(usages, EcdhPrivateAllowedUsages, out var unsupportedUsage))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected($"InvalidAccessError: Unsupported key usage '{unsupportedUsage}' for ECDH"));
            }

            if (!TryResolveNamedCurve(algorithmArg, out var namedCurve, out var curve))
            {
                return FenValue.FromObject(ResolvedThenable.Rejected("NotSupportedError: ECDH namedCurve is invalid"));
            }

            using var generatedEcdh = System.Security.Cryptography.ECDiffieHellman.Create(curve);
            var privatePkcs8 = generatedEcdh.ExportPkcs8PrivateKey();
            var publicSpki = generatedEcdh.ExportSubjectPublicKeyInfo();

            var privateEcdh = System.Security.Cryptography.ECDiffieHellman.Create();
            privateEcdh.ImportPkcs8PrivateKey(privatePkcs8, out _);

            var publicEcdh = System.Security.Cryptography.ECDiffieHellman.Create();
            publicEcdh.ImportSubjectPublicKeyInfo(publicSpki, out _);

            var privateState = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDH",
                NamedCurve = namedCurve,
                KeyType = "private",
                Extractable = extractable,
                EcdhKey = privateEcdh,
                IsPrivateKey = true,
                Usages = usages
            };

            var publicState = new LegacyCryptoKeyState
            {
                AlgorithmName = "ECDH",
                NamedCurve = namedCurve,
                KeyType = "public",
                Extractable = true,
                EcdhKey = publicEcdh,
                IsPrivateKey = false,
                Usages = new HashSet<string>(StringComparer.Ordinal)
            };

            var keyPair = new FenObject();
            keyPair.Set("privateKey", FenValue.FromObject(CreateCryptoKeyObject(privateState)));
            keyPair.Set("publicKey", FenValue.FromObject(CreateCryptoKeyObject(publicState)));
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(keyPair)));
        }

        private static bool TryGetCryptoKeyState(FenValue keyArg, out LegacyCryptoKeyState state)
        {
            state = null;
            if (!keyArg.IsObject)
            {
                return false;
            }

            var keyObject = keyArg.AsObject() as FenObject;
            if (keyObject == null)
            {
                return false;
            }

            state = keyObject.NativeObject as LegacyCryptoKeyState;
            return state != null;
        }

        private static FenObject CreateCryptoKeyObject(LegacyCryptoKeyState state)
        {
            var keyObject = new FenObject
            {
                InternalClass = "CryptoKey",
                NativeObject = state
            };

            keyObject.Set("type", FenValue.FromString(state.KeyType));
            keyObject.Set("extractable", FenValue.FromBoolean(state.Extractable));
            keyObject.Set("algorithm", FenValue.FromObject(CreateAlgorithmDescriptor(state)));
            keyObject.Set("usages", FenValue.FromObject(CreateStringArray(state.Usages)));
            return keyObject;
        }

        private static FenObject CreateAlgorithmDescriptor(LegacyCryptoKeyState state)
        {
            var descriptor = new FenObject();
            descriptor.Set("name", FenValue.FromString(GetDisplayAlgorithmName(state.AlgorithmName)));

            if (!string.IsNullOrWhiteSpace(state.HashName))
            {
                var hash = new FenObject();
                hash.Set("name", FenValue.FromString(GetDisplayHashName(state.HashName)));
                descriptor.Set("hash", FenValue.FromObject(hash));
            }

            if (string.Equals(state.AlgorithmName, "HMAC", StringComparison.Ordinal))
            {
                descriptor.Set("length", FenValue.FromNumber(state.SecretKey.Length * 8));
            }
            else if (string.Equals(state.AlgorithmName, "AESGCM", StringComparison.Ordinal))
            {
                var length = state.KeyLengthBits > 0 ? state.KeyLengthBits : state.SecretKey.Length * 8;
                descriptor.Set("length", FenValue.FromNumber(length));
            }
            else if (string.Equals(state.AlgorithmName, "AESCBC", StringComparison.Ordinal))
            {
                var length = state.KeyLengthBits > 0 ? state.KeyLengthBits : state.SecretKey.Length * 8;
                descriptor.Set("length", FenValue.FromNumber(length));
            }
            else if (string.Equals(state.AlgorithmName, "AESCTR", StringComparison.Ordinal))
            {
                var length = state.KeyLengthBits > 0 ? state.KeyLengthBits : state.SecretKey.Length * 8;
                descriptor.Set("length", FenValue.FromNumber(length));
            }
            else if ((string.Equals(state.AlgorithmName, "ECDSA", StringComparison.Ordinal) || string.Equals(state.AlgorithmName, "ECDH", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(state.NamedCurve))
            {
                descriptor.Set("namedCurve", FenValue.FromString(state.NamedCurve));
            }

            return descriptor;
        }

        private static FenObject CreateStringArray(IEnumerable<string> values)
        {
            var arrayObject = FenObject.CreateArray();
            var index = 0;
            foreach (var value in values)
            {
                arrayObject.Set(index.ToString(), FenValue.FromString(value));
                index++;
            }

            arrayObject.Set("length", FenValue.FromNumber(index));
            return arrayObject;
        }

        private static bool TryParseKeyUsages(FenValue usagesArg, out HashSet<string> usages)
        {
            usages = new HashSet<string>(StringComparer.Ordinal);

            if (!usagesArg.IsObject)
            {
                return false;
            }

            var usagesObject = usagesArg.AsObject();
            if (usagesObject == null)
            {
                return false;
            }

            var lengthValue = usagesObject.Get("length");
            if (lengthValue.IsNull || lengthValue.IsUndefined || !lengthValue.IsNumber)
            {
                return false;
            }

            var lengthNumber = lengthValue.ToNumber();
            if (double.IsNaN(lengthNumber) || double.IsInfinity(lengthNumber))
            {
                return false;
            }

            if (Math.Floor(lengthNumber) != lengthNumber)
            {
                return false;
            }

            var length = (int)lengthNumber;
            if (length < 0)
            {
                return false;
            }

            if (length > 1024)
            {
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                var usageValue = usagesObject.Get(i.ToString());
                if (!usageValue.IsString)
                {
                    return false;
                }

                var usage = usageValue.ToString().Trim();
                if (string.IsNullOrWhiteSpace(usage))
                {
                    return false;
                }

                usages.Add(usage.ToLowerInvariant());
            }

            return true;
        }

        private static bool ValidateKeyUsages(HashSet<string> usages, HashSet<string> allowedUsages, out string unsupportedUsage)
        {
            unsupportedUsage = string.Empty;

            foreach (var usage in usages)
            {
                if (!allowedUsages.Contains(usage))
                {
                    unsupportedUsage = usage;
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeKeyFormat(FenValue formatArg)
        {
            if (!formatArg.IsString)
            {
                return string.Empty;
            }

            return formatArg.ToString().Trim().ToLowerInvariant();
        }

        private static bool TryResolveHashNameForOperation(FenValue algorithmArg, string fallbackHash, out string hashName)
        {
            hashName = fallbackHash;

            if (algorithmArg.IsObject)
            {
                var algorithmObject = algorithmArg.AsObject();
                var hashValue = algorithmObject?.Get("hash") ?? FenValue.Undefined;
                if (!hashValue.IsUndefined && !hashValue.IsNull)
                {
                    if (!TryNormalizeHashName(hashValue, out hashName))
                    {
                        return false;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(hashName))
            {
                return false;
            }

            return TryResolveHashAlgorithm(hashName, out _);
        }

        private static bool TryNormalizeHashName(FenValue hashArg, out string normalized)
        {
            normalized = string.Empty;
            string raw;

            if (hashArg.IsString)
            {
                raw = hashArg.ToString();
            }
            else if (hashArg.IsObject)
            {
                var hashObject = hashArg.AsObject();
                var nameValue = hashObject?.Get("name") ?? FenValue.Undefined;
                if (nameValue.IsUndefined || nameValue.IsNull)
                {
                    return false;
                }

                raw = nameValue.ToString();
            }
            else
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            normalized = raw
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private static bool TryResolveHashAlgorithm(string normalizedHashName, out System.Security.Cryptography.HashAlgorithmName hashAlgorithm)
        {
            hashAlgorithm = default;

            switch (normalizedHashName)
            {
                case "SHA1":
                    hashAlgorithm = System.Security.Cryptography.HashAlgorithmName.SHA1;
                    return true;
                case "SHA256":
                    hashAlgorithm = System.Security.Cryptography.HashAlgorithmName.SHA256;
                    return true;
                case "SHA384":
                    hashAlgorithm = System.Security.Cryptography.HashAlgorithmName.SHA384;
                    return true;
                case "SHA512":
                    hashAlgorithm = System.Security.Cryptography.HashAlgorithmName.SHA512;
                    return true;
                default:
                    return false;
            }
        }

        private static System.Security.Cryptography.HMAC CreateHmac(string normalizedHashName, byte[] secretKey)
        {
            switch (normalizedHashName)
            {
                case "SHA1":
                    return new System.Security.Cryptography.HMACSHA1(secretKey);
                case "SHA256":
                    return new System.Security.Cryptography.HMACSHA256(secretKey);
                case "SHA384":
                    return new System.Security.Cryptography.HMACSHA384(secretKey);
                case "SHA512":
                    return new System.Security.Cryptography.HMACSHA512(secretKey);
                default:
                    return null;
            }
        }

        private static bool TryGetFulfilledThenableResult(FenValue thenableValue, out FenValue result, out FenValue reason)
        {
            result = FenValue.Undefined;
            reason = FenValue.Undefined;

            if (!thenableValue.IsObject)
            {
                reason = FenValue.FromString("OperationError: Promise result is not thenable");
                return false;
            }

            var thenable = thenableValue.AsObject();
            if (thenable == null)
            {
                reason = FenValue.FromString("OperationError: Promise object is null");
                return false;
            }

            var state = thenable.Get("__state").AsString();
            if (string.Equals(state, "fulfilled", StringComparison.Ordinal))
            {
                result = thenable.Get("__result");
                return true;
            }

            reason = thenable.Get("__reason");
            return false;
        }

        private static FenBrowser.FenEngine.Core.Types.JsArrayBuffer CreateArrayBuffer(byte[] bytes)
        {
            var buffer = new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(bytes.Length);
            Array.Copy(bytes, buffer.Data, bytes.Length);
            return buffer;
        }

        private static string GetDisplayAlgorithmName(string normalizedAlgorithmName)
        {
            return normalizedAlgorithmName switch
            {
                "HMAC" => "HMAC",
                "AESGCM" => "AES-GCM",
                "AESCBC" => "AES-CBC",
                "AESCTR" => "AES-CTR",
                "RSASSAPKCS1V15" => "RSASSA-PKCS1-v1_5",
                "RSAPSS" => "RSA-PSS",
                "RSAOAEP" => "RSA-OAEP",
                "ECDSA" => "ECDSA",
                "ECDH" => "ECDH",
                _ => normalizedAlgorithmName
            };
        }

        private static string GetDisplayHashName(string normalizedHashName)
        {
            return normalizedHashName switch
            {
                "SHA1" => "SHA-1",
                "SHA256" => "SHA-256",
                "SHA384" => "SHA-384",
                "SHA512" => "SHA-512",
                _ => normalizedHashName
            };
        }

        private static bool TryNormalizeAlgorithmName(FenValue algorithmArg, out string normalized)
        {
            normalized = string.Empty;
            var raw = string.Empty;

            if (algorithmArg.IsString)
            {
                raw = algorithmArg.ToString();
            }
            else if (algorithmArg.IsObject)
            {
                var algoObject = algorithmArg.AsObject();
                if (algoObject != null)
                {
                    var nameValue = algoObject.Get("name");
                    if (!nameValue.IsNull && !nameValue.IsUndefined)
                    {
                        raw = nameValue.ToString();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            normalized = raw
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private static bool TryResolveHmacLengthBits(FenValue algorithmArg, string hashName, out int keyLengthBits)
        {
            keyLengthBits = hashName switch
            {
                "SHA1" => 160,
                "SHA256" => 256,
                "SHA384" => 384,
                "SHA512" => 512,
                _ => 0
            };

            if (!algorithmArg.IsObject)
            {
                return keyLengthBits > 0;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return keyLengthBits > 0;
            }

            var lengthValue = algorithmObject.Get("length");
            if (lengthValue.IsUndefined || lengthValue.IsNull)
            {
                return keyLengthBits > 0;
            }

            if (!lengthValue.IsNumber)
            {
                return false;
            }

            var bitsNumber = lengthValue.ToNumber();
            if (double.IsNaN(bitsNumber) || double.IsInfinity(bitsNumber))
            {
                return false;
            }

            if (Math.Floor(bitsNumber) != bitsNumber)
            {
                return false;
            }

            var bits = (int)Math.Floor(bitsNumber);
            if (bits <= 0 || bits % 8 != 0)
            {
                return false;
            }

            keyLengthBits = bits;
            return true;
        }

        private static bool TryResolveAesKeyLengthBitsForGenerate(FenValue algorithmArg, out int keyLengthBits)
        {
            keyLengthBits = 0;
            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var lengthValue = algorithmObject.Get("length");
            if (!lengthValue.IsNumber)
            {
                return false;
            }

            var lengthNumber = lengthValue.ToNumber();
            if (double.IsNaN(lengthNumber) || double.IsInfinity(lengthNumber))
            {
                return false;
            }

            if (Math.Floor(lengthNumber) != lengthNumber)
            {
                return false;
            }

            keyLengthBits = (int)Math.Floor(lengthNumber);
            return keyLengthBits == 128 || keyLengthBits == 192 || keyLengthBits == 256;
        }

        private static bool TryResolveAesKeyLengthBitsForImport(FenValue algorithmArg, int keyByteLength, out int keyLengthBits)
        {
            keyLengthBits = keyByteLength * 8;
            if (keyLengthBits != 128 && keyLengthBits != 192 && keyLengthBits != 256)
            {
                return false;
            }

            if (!algorithmArg.IsObject)
            {
                return true;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return true;
            }

            var lengthValue = algorithmObject.Get("length");
            if (lengthValue.IsUndefined || lengthValue.IsNull)
            {
                return true;
            }

            if (!lengthValue.IsNumber)
            {
                return false;
            }

            var requestedBitsNumber = lengthValue.ToNumber();
            if (double.IsNaN(requestedBitsNumber) || double.IsInfinity(requestedBitsNumber))
            {
                return false;
            }

            if (Math.Floor(requestedBitsNumber) != requestedBitsNumber)
            {
                return false;
            }

            var requestedBits = (int)Math.Floor(requestedBitsNumber);
            if (requestedBits != 128 && requestedBits != 192 && requestedBits != 256)
            {
                return false;
            }

            if (requestedBits != keyLengthBits)
            {
                return false;
            }

            keyLengthBits = requestedBits;
            return true;
        }

        private static bool TryResolvePbkdf2Params(FenValue algorithmArg, out byte[] salt, out int iterations, out string hashName)
        {
            salt = Array.Empty<byte>();
            iterations = 0;
            hashName = string.Empty;

            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var saltValue = algorithmObject.Get("salt");
            if (saltValue.IsUndefined || saltValue.IsNull || !TryExtractDigestBytes(saltValue, out salt) || salt.Length == 0)
            {
                return false;
            }

            var iterationsValue = algorithmObject.Get("iterations");
            if (!iterationsValue.IsNumber)
            {
                return false;
            }

            var iterationsNumber = iterationsValue.ToNumber();
            if (double.IsNaN(iterationsNumber) || double.IsInfinity(iterationsNumber))
            {
                return false;
            }

            if (Math.Floor(iterationsNumber) != iterationsNumber)
            {
                return false;
            }

            if (iterationsNumber > int.MaxValue)
            {
                return false;
            }

            iterations = (int)Math.Floor(iterationsNumber);
            if (iterations <= 0)
            {
                return false;
            }

            if (!TryResolveHashNameForOperation(algorithmArg, null, out hashName))
            {
                return false;
            }

            return true;
        }

        private static bool TryResolveHkdfParams(FenValue algorithmArg, out byte[] salt, out byte[] info, out string hashName)
        {
            salt = Array.Empty<byte>();
            info = Array.Empty<byte>();
            hashName = string.Empty;

            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var saltValue = algorithmObject.Get("salt");
            if (saltValue.IsUndefined || saltValue.IsNull || !TryExtractDigestBytes(saltValue, out salt))
            {
                return false;
            }

            var infoValue = algorithmObject.Get("info");
            if (infoValue.IsUndefined || infoValue.IsNull || !TryExtractDigestBytes(infoValue, out info))
            {
                return false;
            }

            if (!TryResolveHashNameForOperation(algorithmArg, null, out hashName))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetHashOutputLengthBytes(string normalizedHashName, out int hashOutputLengthBytes)
        {
            hashOutputLengthBytes = normalizedHashName switch
            {
                "SHA1" => 20,
                "SHA256" => 32,
                "SHA384" => 48,
                "SHA512" => 64,
                _ => 0
            };

            return hashOutputLengthBytes > 0;
        }

        private static bool TryComputeHkdf(byte[] ikm, byte[] salt, byte[] info, string normalizedHashName, int outputLengthBytes, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (ikm == null || ikm.Length == 0 || outputLengthBytes <= 0)
            {
                return false;
            }

            if (!TryGetHashOutputLengthBytes(normalizedHashName, out var hashOutputLengthBytes))
            {
                return false;
            }

            if (outputLengthBytes > 255 * hashOutputLengthBytes)
            {
                return false;
            }

            var effectiveSalt = salt.Length == 0 ? new byte[hashOutputLengthBytes] : salt;
            byte[] prk;
            using (var extractHmac = CreateHmac(normalizedHashName, effectiveSalt))
            {
                if (extractHmac == null)
                {
                    return false;
                }

                prk = extractHmac.ComputeHash(ikm);
            }

            var derived = new byte[outputLengthBytes];
            var bytesWritten = 0;
            var previousBlock = Array.Empty<byte>();
            byte counter = 1;

            while (bytesWritten < outputLengthBytes)
            {
                if (counter == 0)
                {
                    return false;
                }

                var blockInput = new byte[previousBlock.Length + info.Length + 1];
                if (previousBlock.Length > 0)
                {
                    Buffer.BlockCopy(previousBlock, 0, blockInput, 0, previousBlock.Length);
                }

                if (info.Length > 0)
                {
                    Buffer.BlockCopy(info, 0, blockInput, previousBlock.Length, info.Length);
                }

                blockInput[blockInput.Length - 1] = counter;

                using (var expandHmac = CreateHmac(normalizedHashName, prk))
                {
                    if (expandHmac == null)
                    {
                        return false;
                    }

                    previousBlock = expandHmac.ComputeHash(blockInput);
                }

                var copyLength = Math.Min(previousBlock.Length, outputLengthBytes - bytesWritten);
                Buffer.BlockCopy(previousBlock, 0, derived, bytesWritten, copyLength);
                bytesWritten += copyLength;
                counter++;
            }

            output = derived;
            return true;
        }

        private static bool TryResolveRsaOaepPadding(FenValue algorithmArg, string fallbackHash, out System.Security.Cryptography.RSAEncryptionPadding padding)
        {
            padding = null;
            if (!TryResolveHashNameForOperation(algorithmArg, fallbackHash, out var hashName))
            {
                return false;
            }

            padding = hashName switch
            {
                "SHA1" => System.Security.Cryptography.RSAEncryptionPadding.OaepSHA1,
                "SHA256" => System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256,
                "SHA384" => System.Security.Cryptography.RSAEncryptionPadding.OaepSHA384,
                "SHA512" => System.Security.Cryptography.RSAEncryptionPadding.OaepSHA512,
                _ => null
            };

            return padding != null;
        }

        private static bool TryEnsureRsaOperationHashMatchesKey(FenValue algorithmArg, string keyHashName, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(keyHashName) || !algorithmArg.IsObject)
            {
                return true;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return true;
            }

            var hashValue = algorithmObject.Get("hash");
            if (hashValue.IsUndefined || hashValue.IsNull)
            {
                return true;
            }

            if (!TryNormalizeHashName(hashValue, out var requestedHash))
            {
                error = "TypeError: Hash algorithm is invalid";
                return false;
            }

            if (!string.Equals(requestedHash, keyHashName, StringComparison.Ordinal))
            {
                error = "InvalidAccessError: Operation hash does not match key hash";
                return false;
            }

            return true;
        }

        private static bool TryResolveRsaOaepLabel(FenValue algorithmArg, out byte[] label)
        {
            label = Array.Empty<byte>();
            if (!algorithmArg.IsObject)
            {
                return true;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return true;
            }

            var labelValue = algorithmObject.Get("label");
            if (labelValue.IsUndefined || labelValue.IsNull)
            {
                return true;
            }

            return TryExtractDigestBytes(labelValue, out label);
        }

        private static bool TryResolveRsaPssSaltLength(FenValue algorithmArg, string normalizedHashName, out int saltLengthBytes, out string error)
        {
            saltLengthBytes = 0;
            error = string.Empty;

            if (!TryGetHashOutputLengthBytes(normalizedHashName, out var hashLengthBytes))
            {
                error = "NotSupportedError: Hash algorithm not supported";
                return false;
            }

            saltLengthBytes = hashLengthBytes;
            if (!algorithmArg.IsObject)
            {
                return true;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return true;
            }

            var saltLengthValue = algorithmObject.Get("saltLength");
            if (saltLengthValue.IsUndefined || saltLengthValue.IsNull)
            {
                return true;
            }

            if (!saltLengthValue.IsNumber)
            {
                error = "TypeError: RSA-PSS saltLength must be a non-negative integer";
                return false;
            }

            var requestedSaltLengthNumber = saltLengthValue.ToNumber();
            if (double.IsNaN(requestedSaltLengthNumber) || double.IsInfinity(requestedSaltLengthNumber))
            {
                error = "TypeError: RSA-PSS saltLength must be a non-negative integer";
                return false;
            }

            var requestedSaltLength = (int)Math.Floor(requestedSaltLengthNumber);
            if (requestedSaltLength < 0 || requestedSaltLength != saltLengthValue.ToNumber())
            {
                error = "TypeError: RSA-PSS saltLength must be a non-negative integer";
                return false;
            }

            if (requestedSaltLength != hashLengthBytes)
            {
                error = "NotSupportedError: RSA-PSS currently supports saltLength equal to hash length only";
                return false;
            }

            saltLengthBytes = requestedSaltLength;
            return true;
        }

        private static bool TryResolveEcdhPublicKey(FenValue algorithmArg, out LegacyCryptoKeyState publicKeyState, out string error)
        {
            publicKeyState = null;
            error = string.Empty;

            if (!algorithmArg.IsObject)
            {
                error = "TypeError: ECDH parameters are invalid";
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                error = "TypeError: ECDH parameters are invalid";
                return false;
            }

            var publicKeyValue = algorithmObject.Get("public");
            if (!TryGetCryptoKeyState(publicKeyValue, out publicKeyState))
            {
                error = "TypeError: ECDH public key is invalid";
                return false;
            }

            if (!string.Equals(publicKeyState.AlgorithmName, "ECDH", StringComparison.Ordinal))
            {
                error = "InvalidAccessError: ECDH public key algorithm mismatch";
                return false;
            }

            if (publicKeyState.IsPrivateKey)
            {
                error = "InvalidAccessError: ECDH public parameter must be a public key";
                return false;
            }

            return true;
        }

        private static bool TryResolveAesGcmParams(FenValue algorithmArg, out byte[] iv, out byte[] additionalData, out int tagLengthBits)
        {
            iv = Array.Empty<byte>();
            additionalData = Array.Empty<byte>();
            tagLengthBits = 128;

            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var ivValue = algorithmObject.Get("iv");
            if (ivValue.IsUndefined || ivValue.IsNull || !TryExtractDigestBytes(ivValue, out iv) || iv.Length == 0)
            {
                return false;
            }

            var additionalDataValue = algorithmObject.Get("additionalData");
            if (!additionalDataValue.IsUndefined && !additionalDataValue.IsNull)
            {
                if (!TryExtractDigestBytes(additionalDataValue, out additionalData))
                {
                    return false;
                }
            }

            var tagLengthValue = algorithmObject.Get("tagLength");
            if (!tagLengthValue.IsUndefined && !tagLengthValue.IsNull)
            {
                if (!tagLengthValue.IsNumber)
                {
                    return false;
                }

                var tagLengthNumber = tagLengthValue.ToNumber();
                if (double.IsNaN(tagLengthNumber) || double.IsInfinity(tagLengthNumber))
                {
                    return false;
                }

                if (Math.Floor(tagLengthNumber) != tagLengthNumber)
                {
                    return false;
                }

                var parsedTagLength = (int)Math.Floor(tagLengthNumber);
                if (parsedTagLength != 32
                    && parsedTagLength != 64
                    && parsedTagLength != 96
                    && parsedTagLength != 104
                    && parsedTagLength != 112
                    && parsedTagLength != 120
                    && parsedTagLength != 128)
                {
                    return false;
                }

                tagLengthBits = parsedTagLength;
            }

            return true;
        }

        private static bool TryResolveAesCbcParams(FenValue algorithmArg, out byte[] iv)
        {
            iv = Array.Empty<byte>();
            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var ivValue = algorithmObject.Get("iv");
            if (ivValue.IsUndefined || ivValue.IsNull || !TryExtractDigestBytes(ivValue, out iv))
            {
                return false;
            }

            return iv.Length == 16;
        }

        private static bool TryResolveAesCtrParams(FenValue algorithmArg, out byte[] counter, out int counterLengthBits, out string error)
        {
            counter = Array.Empty<byte>();
            counterLengthBits = 0;
            error = string.Empty;

            if (!algorithmArg.IsObject)
            {
                error = "TypeError: AES-CTR parameters are invalid";
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                error = "TypeError: AES-CTR parameters are invalid";
                return false;
            }

            var counterValue = algorithmObject.Get("counter");
            if (counterValue.IsUndefined || counterValue.IsNull || !TryExtractDigestBytes(counterValue, out counter) || counter.Length != 16)
            {
                error = "TypeError: AES-CTR counter must be 16 bytes";
                return false;
            }

            var lengthValue = algorithmObject.Get("length");
            if (!lengthValue.IsNumber)
            {
                error = "TypeError: AES-CTR length must be a number";
                return false;
            }

            var lengthNumber = lengthValue.ToNumber();
            if (double.IsNaN(lengthNumber) || double.IsInfinity(lengthNumber))
            {
                error = "TypeError: AES-CTR length must be an integer between 1 and 128";
                return false;
            }

            if (lengthNumber > int.MaxValue)
            {
                error = "TypeError: AES-CTR length must be an integer between 1 and 128";
                return false;
            }

            var parsedLength = (int)Math.Floor(lengthNumber);
            if (parsedLength < 1 || parsedLength > 128 || parsedLength != lengthNumber)
            {
                error = "TypeError: AES-CTR length must be an integer between 1 and 128";
                return false;
            }

            counterLengthBits = parsedLength;
            return true;
        }

        private static byte[] TransformAesCtr(byte[] key, byte[] counter, int counterLengthBits, byte[] input)
        {
            var output = new byte[input.Length];
            var counterBlock = (byte[])counter.Clone();
            var keystreamBlock = new byte[16];

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Mode = System.Security.Cryptography.CipherMode.ECB;
            aes.Padding = System.Security.Cryptography.PaddingMode.None;
            aes.Key = key;
            aes.IV = new byte[16];

            using var encryptor = aes.CreateEncryptor();
            for (var offset = 0; offset < input.Length; offset += 16)
            {
                encryptor.TransformBlock(counterBlock, 0, 16, keystreamBlock, 0);
                var blockSize = Math.Min(16, input.Length - offset);
                for (var i = 0; i < blockSize; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ keystreamBlock[i]);
                }

                IncrementCounterBlock(counterBlock, counterLengthBits);
            }

            return output;
        }

        private static bool TryValidateAesCtrCounterCapacity(byte[] counter, int counterLengthBits, int payloadLengthBytes, out string error)
        {
            error = string.Empty;
            if (payloadLengthBytes <= 0)
            {
                return true;
            }

            var blocksRequired = (payloadLengthBytes + 15L) / 16L;
            var initialCounterValue = ExtractAesCtrCounterValue(counter, counterLengthBits);
            var maxCounterValues = System.Numerics.BigInteger.One << counterLengthBits;
            var terminalCounter = initialCounterValue + blocksRequired - 1;
            if (terminalCounter >= maxCounterValues)
            {
                error = "OperationError: AES-CTR counter would overflow configured counter length";
                return false;
            }

            return true;
        }

        private static System.Numerics.BigInteger ExtractAesCtrCounterValue(byte[] counter, int counterLengthBits)
        {
            var bitsRemaining = counterLengthBits;
            var bitShift = 0;
            var value = System.Numerics.BigInteger.Zero;
            for (var i = counter.Length - 1; i >= 0 && bitsRemaining > 0; i--)
            {
                var bitsInByte = bitsRemaining >= 8 ? 8 : bitsRemaining;
                var lowBitsMask = (1 << bitsInByte) - 1;
                var part = counter[i] & lowBitsMask;
                value |= (System.Numerics.BigInteger)part << bitShift;
                bitShift += bitsInByte;
                bitsRemaining -= bitsInByte;
            }

            return value;
        }

        private static void IncrementCounterBlock(byte[] counterBlock, int counterLengthBits)
        {
            var bitsRemaining = counterLengthBits;
            var carry = true;
            for (var i = counterBlock.Length - 1; i >= 0 && bitsRemaining > 0; i--)
            {
                var bitsInByte = bitsRemaining >= 8 ? 8 : bitsRemaining;
                var lowBitsMask = (1 << bitsInByte) - 1;
                var original = counterBlock[i];
                var counterPart = original & lowBitsMask;
                if (carry)
                {
                    counterPart = (counterPart + 1) & lowBitsMask;
                    carry = counterPart == 0;
                }

                counterBlock[i] = (byte)((original & ~lowBitsMask) | counterPart);
                bitsRemaining -= bitsInByte;
            }
        }

        private static bool TryResolveRsaModulusLength(FenValue algorithmArg, out int modulusLength)
        {
            modulusLength = 0;
            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var modulusLengthValue = algorithmObject.Get("modulusLength");
            if (!modulusLengthValue.IsNumber)
            {
                return false;
            }

            var modulusLengthNumber = modulusLengthValue.ToNumber();
            if (double.IsNaN(modulusLengthNumber) || double.IsInfinity(modulusLengthNumber))
            {
                return false;
            }

            if (Math.Floor(modulusLengthNumber) != modulusLengthNumber)
            {
                return false;
            }

            if (modulusLengthNumber > int.MaxValue)
            {
                return false;
            }

            modulusLength = (int)Math.Floor(modulusLengthNumber);
            return modulusLength > 0;
        }

        private static bool TryResolveRsaPublicExponent(FenValue algorithmArg, out byte[] publicExponent)
        {
            publicExponent = Array.Empty<byte>();

            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var exponentValue = algorithmObject.Get("publicExponent");
            if (exponentValue.IsUndefined || exponentValue.IsNull)
            {
                return false;
            }

            if (!TryExtractDigestBytes(exponentValue, out publicExponent))
            {
                return false;
            }

            return publicExponent.Length > 0;
        }

        private static bool TryResolveNamedCurve(FenValue algorithmArg, out string namedCurve, out System.Security.Cryptography.ECCurve curve)
        {
            namedCurve = string.Empty;
            curve = default;

            if (!algorithmArg.IsObject)
            {
                return false;
            }

            var algorithmObject = algorithmArg.AsObject();
            if (algorithmObject == null)
            {
                return false;
            }

            var namedCurveValue = algorithmObject.Get("namedCurve");
            if (!namedCurveValue.IsString)
            {
                return false;
            }

            var normalized = namedCurveValue.ToString()
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();

            switch (normalized)
            {
                case "P256":
                case "SECP256R1":
                    namedCurve = "P-256";
                    curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256;
                    return true;
                case "P384":
                case "SECP384R1":
                    namedCurve = "P-384";
                    curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP384;
                    return true;
                case "P521":
                case "SECP521R1":
                    namedCurve = "P-521";
                    curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP521;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetNamedCurveFromCurve(System.Security.Cryptography.ECCurve curve, out string namedCurve)
        {
            namedCurve = string.Empty;
            var friendlyName = curve.Oid.FriendlyName?.Trim();
            var oidValue = curve.Oid.Value?.Trim();

            if (string.Equals(oidValue, "1.2.840.10045.3.1.7", StringComparison.Ordinal)
                || string.Equals(friendlyName, "nistP256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDH_P256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "secp256r1", StringComparison.OrdinalIgnoreCase))
            {
                namedCurve = "P-256";
                return true;
            }

            if (string.Equals(oidValue, "1.3.132.0.34", StringComparison.Ordinal)
                || string.Equals(friendlyName, "nistP384", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDSA_P384", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDH_P384", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "secp384r1", StringComparison.OrdinalIgnoreCase))
            {
                namedCurve = "P-384";
                return true;
            }

            if (string.Equals(oidValue, "1.3.132.0.35", StringComparison.Ordinal)
                || string.Equals(friendlyName, "nistP521", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDSA_P521", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "ECDH_P521", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "secp521r1", StringComparison.OrdinalIgnoreCase))
            {
                namedCurve = "P-521";
                return true;
            }

            return false;
        }

        private static bool IsSupportedRsaPublicExponent(byte[] exponent)
        {
            if (exponent == null || exponent.Length == 0)
            {
                return false;
            }

            var start = 0;
            while (start < exponent.Length - 1 && exponent[start] == 0)
            {
                start++;
            }

            return exponent.Length - start == 3
                && exponent[start] == 0x01
                && exponent[start + 1] == 0x00
                && exponent[start + 2] == 0x01;
        }

        private static bool TryExtractDigestBytes(FenValue dataArg, out byte[] data)
        {
            data = Array.Empty<byte>();

            if (dataArg.IsString)
            {
                data = System.Text.Encoding.UTF8.GetBytes(dataArg.ToString());
                return true;
            }

            if (!dataArg.IsObject)
            {
                return false;
            }

            var obj = dataArg.AsObject();
            if (obj is FenBrowser.FenEngine.Core.Types.JsTypedArrayView typedView)
            {
                data = new byte[typedView.ByteLength];
                Array.Copy(typedView.Buffer.Data, typedView.ByteOffset, data, 0, typedView.ByteLength);
                return true;
            }

            if (obj is FenBrowser.FenEngine.Core.Types.JsArrayBuffer buffer)
            {
                data = (byte[])buffer.Data.Clone();
                return true;
            }

            if (obj == null)
            {
                return false;
            }

            var lengthValue = obj.Get("length");
            if (lengthValue.IsNull || lengthValue.IsUndefined || !lengthValue.IsNumber)
            {
                return false;
            }

            var lengthNumber = lengthValue.ToNumber();
            if (double.IsNaN(lengthNumber) || double.IsInfinity(lengthNumber))
            {
                return false;
            }

            if (lengthNumber < 0 || lengthNumber > int.MaxValue || Math.Floor(lengthNumber) != lengthNumber)
            {
                return false;
            }

            var len = (int)lengthNumber;

            data = new byte[len];
            for (var i = 0; i < len; i++)
            {
                var item = obj.Get(i.ToString());
                if (!item.IsNumber)
                {
                    return false;
                }

                var itemNumber = item.ToNumber();
                if (double.IsNaN(itemNumber) || double.IsInfinity(itemNumber))
                {
                    return false;
                }

                if (Math.Floor(itemNumber) != itemNumber)
                {
                    return false;
                }

                if (itemNumber < 0 || itemNumber > byte.MaxValue)
                {
                    return false;
                }

                data[i] = (byte)itemNumber;
            }

            return true;
        }
    }

    public class JsPerformance : FenBrowser.FenEngine.Core.FenObject
    {
        private static readonly DateTime _start = DateTime.UtcNow;
        
        public JsPerformance()
        {
            Set("now", FenValue.FromFunction(new FenFunction("now", (args, _) => 
                FenValue.FromNumber((DateTime.UtcNow - _start).TotalMilliseconds))));
                
            Set("mark", FenValue.FromFunction(new FenFunction("mark", (args, _) => FenValue.Undefined)));
            Set("measure", FenValue.FromFunction(new FenFunction("measure", (args, _) => FenValue.Undefined)));
            Set("clearMarks", FenValue.FromFunction(new FenFunction("clearMarks", (args, _) => FenValue.Undefined)));
            Set("clearMeasures", FenValue.FromFunction(new FenFunction("clearMeasures", (args, _) => FenValue.Undefined)));
        }
    }

}















































