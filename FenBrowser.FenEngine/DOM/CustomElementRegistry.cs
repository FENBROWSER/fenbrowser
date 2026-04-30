using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Custom Elements Registry - implements Window.customElements API
    /// Allows defining custom HTML elements with JavaScript behaviors
    /// </summary>
    public class CustomElementRegistry
    {
        private readonly Dictionary<string, CustomElementDefinition> _definitions
            = new Dictionary<string, CustomElementDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TaskCompletionSource<bool>> _whenDefinedPromises
            = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();
        private readonly IExecutionContext _context;

        public CustomElementRegistry(IExecutionContext context = null)
        {
            _context = context;
        }

        /// <summary>
        /// Define a custom element with the given name and constructor
        /// </summary>
        /// <param name="name">Element name (must contain a hyphen)</param>
        /// <param name="constructor">Constructor function</param>
        /// <param name="options">Optional: extends option for customized built-in elements</param>
        public void Define(string name, IValue constructor, CustomElementOptions options = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Custom element name is required");

            // Validate name - must contain hyphen and start with letter
            if (!name.Contains("-"))
                throw new ArgumentException($"Custom element name '{name}' must contain a hyphen");

            if (!char.IsLetter(name[0]))
                throw new ArgumentException($"Custom element name '{name}' must start with a letter");

            // Check for reserved names
            var reserved = new[] { "annotation-xml", "color-profile", "font-face", "font-face-src", 
                "font-face-uri", "font-face-format", "font-face-name", "missing-glyph" };
            foreach (var r in reserved)
            {
                if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Cannot use reserved name '{name}'");
            }

            lock (_lock)
            {
                if (_definitions.ContainsKey(name))
                    throw new ArgumentException($"Custom element '{name}' is already defined");

                var definition = new CustomElementDefinition
                {
                    Name = name,
                    Constructor = constructor,
                    Extends = options?.Extends
                };

                // WHATWG HTML Â§4.13.4: Extract lifecycle callbacks from constructor.prototype
                if (constructor is FenValue ctorFenVal && (ctorFenVal.IsObject || ctorFenVal.IsFunction))
                {
                    var ctorObj = ctorFenVal.AsObject();
                    if (ctorObj != null)
                    {
                        // Extract observedAttributes from static getter
                        var observedAttrs = ctorObj.Get("observedAttributes");
                        if (observedAttrs.IsObject)
                        {
                            var attrsObj = observedAttrs.AsObject();
                            var lenVal = attrsObj?.Get("length");
                            if (lenVal.HasValue && lenVal.Value.IsNumber)
                            {
                                int len = (int)lenVal.Value.ToNumber();
                                for (int i = 0; i < len; i++)
                                {
                                    var attr = attrsObj.Get(i.ToString());
                                    if (!attr.IsUndefined)
                                        definition.ObservedAttributes.Add(attr.ToString());
                                }
                            }
                        }

                        // Extract lifecycle callbacks from prototype
                        var proto = ctorObj.Get("prototype");
                        if (proto.IsObject)
                        {
                            var protoObj = proto.AsObject();
                            if (protoObj != null)
                            {
                                var cb = protoObj.Get("connectedCallback");
                                if (cb.IsFunction) definition.ConnectedCallback = cb.AsFunction() as FenFunction;

                                cb = protoObj.Get("disconnectedCallback");
                                if (cb.IsFunction) definition.DisconnectedCallback = cb.AsFunction() as FenFunction;

                                cb = protoObj.Get("adoptedCallback");
                                if (cb.IsFunction) definition.AdoptedCallback = cb.AsFunction() as FenFunction;

                                cb = protoObj.Get("attributeChangedCallback");
                                if (cb.IsFunction) definition.AttributeChangedCallback = cb.AsFunction() as FenFunction;
                            }
                        }
                    }
                }

                _definitions[name] = definition;

                EngineLogCompat.Debug($"[CustomElements] Defined custom element: {name}", LogCategory.JavaScript);

                // Resolve whenDefined promise if any
                if (_whenDefinedPromises.TryGetValue(name, out var tcs))
                {
                    tcs.TrySetResult(true);
                    _whenDefinedPromises.Remove(name);
                }
            }

            // Upgrade already-connected matching elements when a definition is registered.
            UpgradeExistingDocumentElements();
        }

        /// <summary>
        /// Get the constructor for a defined custom element
        /// </summary>
        public FenValue Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return FenValue.Null;

            lock (_lock)
            {
                return _definitions.TryGetValue(name, out var def) ? (FenValue)def.Constructor : FenValue.Null;
            }
        }

        /// <summary>
        /// Get the name of a custom element given its constructor
        /// </summary>
        public string GetName(IValue constructor)
        {
            if (constructor  == null) return null;

            lock (_lock)
            {
                foreach (var kvp in _definitions)
                {
                    if (ReferenceEquals(kvp.Value.Constructor, constructor))
                        return kvp.Key;
                }
                return null;
            }
        }

        /// <summary>
        /// Check if a custom element is defined
        /// </summary>
        public bool IsDefined(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_lock)
            {
                return _definitions.ContainsKey(name);
            }
        }

        /// <summary>
        /// Return a promise that resolves when the named element is defined
        /// </summary>
        public Task WhenDefined(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Task.FromException(new ArgumentException("Name is required"));

            lock (_lock)
            {
                // Already defined
                if (_definitions.ContainsKey(name))
                    return Task.CompletedTask;

                // Create or return existing promise
                if (!_whenDefinedPromises.TryGetValue(name, out var tcs))
                {
                    tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _whenDefinedPromises[name] = tcs;
                }
                return tcs.Task;
            }
        }

        /// <summary>
        /// Upgrade an element to its custom element definition
        /// </summary>
        public void Upgrade(Element element)
        {
            if (element  == null) return;

            var tag = element.TagName?.ToLowerInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            CustomElementDefinition definition = null;
            lock (_lock)
            {
                // Check for autonomous custom element (tag name is the custom element name)
                if (!_definitions.TryGetValue(tag, out definition))
                {
                    // Check for customized built-in element (via is="" attribute)
                    var isAttr = element.GetAttribute("is");
                    if (!string.IsNullOrEmpty(isAttr))
                        _definitions.TryGetValue(isAttr, out definition);
                }

                if (definition == null) return;

                // Already upgraded â€” skip
                if (element.GetAttribute("data-ce-upgraded") == "true") return;

                // WHATWG HTML Â§4.13.6: Custom element upgrade algorithm
                EngineLogCompat.Debug($"[CustomElements] Upgrading element: {tag}", LogCategory.JavaScript);

                // Step 1: Invoke the constructor
                var constructorSucceeded = true;
                if (_context != null && definition.Constructor is FenValue ctorVal && ctorVal.IsFunction)
                {
                    try
                    {
                        var ctor = ctorVal.AsFunction();
                        var thisArg = DomWrapperFactory.Wrap(element, _context);
                        ctor?.Invoke(System.Array.Empty<FenValue>(), _context, thisArg);
                    }
                    catch (Exception ex)
                    {
                        constructorSucceeded = false;
                        EngineLogCompat.Error($"[CustomElements] Constructor error on <{tag}>: {ex.Message}", LogCategory.JavaScript);
                    }
                }


                if (!constructorSucceeded)
                {
                    try
                    {
                        element.RemoveAttribute("data-ce-upgraded");
                    }
                    catch
                    {
                    }
                    return;
                }

                element.SetAttribute("data-ce-upgraded", "true");

                // Step 2: Fire attributeChangedCallback for each observed attribute already present
                if (definition.AttributeChangedCallback != null && _context != null)
                {
                    foreach (var attrName in definition.ObservedAttributes)
                    {
                        var value = element.GetAttribute(attrName);
                        if (value != null)
                        {
                            try
                            {
                                definition.AttributeChangedCallback.Invoke(
                                    new[]
                                    {
                                        FenValue.FromString(attrName),
                                        FenValue.Null, // oldValue (none â€” first time)
                                        FenValue.FromString(value),
                                        FenValue.Null  // namespace
                                    }, _context);
                            }
                            catch (Exception ex)
                            {
                                EngineLogCompat.Error($"[CustomElements] attributeChangedCallback error on <{tag}> attr={attrName}: {ex.Message}", LogCategory.JavaScript);
                            }
                        }
                    }
                }

                // Step 3: Fire connectedCallback if the element is already connected
                if (element.IsConnected && definition.ConnectedCallback != null && _context != null)
                {
                    try
                    {
                        definition.ConnectedCallback.Invoke(
                            System.Array.Empty<FenValue>(),
                            _context,
                            DomWrapperFactory.Wrap(element, _context));
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Error($"[CustomElements] connectedCallback error on <{tag}>: {ex.Message}", LogCategory.JavaScript);
                    }
                }
            }
        }

        /// <summary>
        /// WHATWG HTML Â§4.13.5: Invoke disconnectedCallback when element is removed from the document.
        /// Called by DOM mutation logic when a custom element is disconnected.
        /// </summary>
        public void NotifyDisconnected(Element element)
        {
            if (element == null) return;
            if (element.GetAttribute("data-ce-upgraded") != "true") return;

            var tag = element.TagName?.ToLowerInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            CustomElementDefinition definition = null;
            lock (_lock)
            {
                if (!_definitions.TryGetValue(tag, out definition))
                {
                    var isAttr = element.GetAttribute("is");
                    if (!string.IsNullOrEmpty(isAttr))
                        _definitions.TryGetValue(isAttr, out definition);
                }
            }

            if (definition?.DisconnectedCallback != null && _context != null)
            {
                try
                {
                    definition.DisconnectedCallback.Invoke(
                        System.Array.Empty<FenValue>(),
                        _context,
                        DomWrapperFactory.Wrap(element, _context));
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Error($"[CustomElements] disconnectedCallback error on <{tag}>: {ex.Message}", LogCategory.JavaScript);
                }
            }
        }

        /// <summary>
        /// WHATWG HTML Â§4.13.5: Invoke attributeChangedCallback when an observed attribute changes.
        /// Called by DOM mutation logic when an attribute is set/removed on a custom element.
        /// </summary>
        public void NotifyAttributeChanged(Element element, string attrName, string oldValue, string newValue)
        {
            if (element == null || string.IsNullOrEmpty(attrName)) return;
            if (element.GetAttribute("data-ce-upgraded") != "true") return;

            var tag = element.TagName?.ToLowerInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            CustomElementDefinition definition = null;
            lock (_lock)
            {
                if (!_definitions.TryGetValue(tag, out definition))
                {
                    var isAttr = element.GetAttribute("is");
                    if (!string.IsNullOrEmpty(isAttr))
                        _definitions.TryGetValue(isAttr, out definition);
                }
            }

            if (definition?.AttributeChangedCallback == null || _context == null) return;
            if (!definition.ObservedAttributes.Contains(attrName)) return;

            try
            {
                definition.AttributeChangedCallback.Invoke(
                    new[]
                    {
                        FenValue.FromString(attrName),
                        oldValue != null ? FenValue.FromString(oldValue) : FenValue.Null,
                        newValue != null ? FenValue.FromString(newValue) : FenValue.Null,
                        FenValue.Null // namespace
                    },
                    _context,
                    DomWrapperFactory.Wrap(element, _context));
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[CustomElements] attributeChangedCallback error on <{tag}> attr={attrName}: {ex.Message}", LogCategory.JavaScript);
            }
        }

        /// <summary>
        /// WHATWG HTML Â§4.13.5: Invoke adoptedCallback when element is adopted into a new document.
        /// </summary>
        public void NotifyAdopted(Element element, Document oldDocument, Document newDocument)
        {
            if (element == null) return;
            if (element.GetAttribute("data-ce-upgraded") != "true") return;

            var tag = element.TagName?.ToLowerInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            CustomElementDefinition definition = null;
            lock (_lock)
            {
                if (!_definitions.TryGetValue(tag, out definition))
                {
                    var isAttr = element.GetAttribute("is");
                    if (!string.IsNullOrEmpty(isAttr))
                        _definitions.TryGetValue(isAttr, out definition);
                }
            }

            if (definition?.AdoptedCallback != null && _context != null)
            {
                try
                {
                    definition.AdoptedCallback.Invoke(
                        System.Array.Empty<FenValue>(),
                        _context,
                        DomWrapperFactory.Wrap(element, _context));
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Error($"[CustomElements] adoptedCallback error on <{tag}>: {ex.Message}", LogCategory.JavaScript);
                }
            }
        }

        /// <summary>
        /// Upgrade all elements in a subtree
        /// </summary>
        public void UpgradeSubtree(Element root)
        {
            if (root  == null) return;

            Upgrade(root);
            foreach (var child in root.Descendants())
            {
                if (child is Element el)
                    Upgrade(el);
            }
        }

        /// <summary>
        /// Convert to FenObject for JavaScript access
        /// </summary>
        public FenObject ToFenObject()
        {
            var obj = new FenObject();

            obj.Set("define", FenValue.FromFunction(new FenFunction("define", (args, thisVal) =>
            {
                if (args.Length < 2)
                    return FenValue.FromString("Error: customElements.define requires name and constructor");

                var name = args[0].ToString();
                var constructor = args[1];

                CustomElementOptions options = null;
                if (args.Length >= 3 && args[2].IsObject)
                {
                    var optObj = args[2].AsObject() as FenObject;
                    if (optObj != null)
                    {
                        options = new CustomElementOptions
                        {
                            Extends = optObj.Get("extends").ToString()
                        };
                    }
                }

                try
                {
                    Define(name, constructor, options);
                    return FenValue.Undefined;
                }
                catch (Exception ex)
                {
                    return FenValue.FromString("Error: " + ex.Message);
                }
            })));

            obj.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Undefined;
                var constructor = Get(args[0].ToString());
                return !constructor.IsUndefined ? constructor : FenValue.Undefined;
            })));

            obj.Set("getName", FenValue.FromFunction(new FenFunction("getName", (args, thisVal) =>
            {
                if (args.Length == 0) return FenValue.Null;
                var name = GetName(args[0]);
                return name != null ? FenValue.FromString(name) : FenValue.Null;
            })));

            obj.Set("whenDefined", FenValue.FromFunction(new FenFunction("whenDefined", (args, thisVal) =>
            {
                if (args.Length == 0)
                    return CreateRejectedPromise("Name is required");

                var name = args[0].ToString();
                
                if (IsDefined(name))
                    return CreateResolvedPromise(FenValue.Undefined);

                return CreatePendingWhenDefinedPromise(name);
            })));

            obj.Set("upgrade", FenValue.FromFunction(new FenFunction("upgrade", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0].AsObject() is ElementWrapper ew)
                {
                    Upgrade(ew.Element);
                }
                return FenValue.Undefined;
            })));

            return obj;
        }

        private void UpgradeExistingDocumentElements()
        {
            if (_context?.Environment == null)
            {
                return;
            }

            try
            {
                var documentValue = _context.Environment.Get("document");
                if (!documentValue.IsObject)
                {
                    return;
                }

                var documentNode = ResolveDocumentNode(documentValue.AsObject());
                var root = documentNode?.DocumentElement;
                if (root == null)
                {
                    return;
                }

                UpgradeSubtree(root);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[CustomElements] Failed to auto-upgrade existing elements after define(): {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static Document ResolveDocumentNode(IObject documentObject)
        {
            if (documentObject == null)
            {
                return null;
            }

            if (documentObject is DocumentWrapper wrapper)
            {
                return wrapper.Node as Document ?? wrapper.Node?.OwnerDocument;
            }

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var type = documentObject.GetType();
            foreach (var memberName in new[] { "Node", "_node", "Document", "_document" })
            {
                try
                {
                    var property = type.GetProperty(memberName, flags);
                    var propertyValue = property?.GetValue(documentObject);
                    if (propertyValue is Document propertyDocument)
                    {
                        return propertyDocument;
                    }

                    if (propertyValue is Node propertyNode)
                    {
                        return propertyNode as Document ?? propertyNode.OwnerDocument;
                    }
                }
                catch
                {
                }

                try
                {
                    var field = type.GetField(memberName, flags);
                    var fieldValue = field?.GetValue(documentObject);
                    if (fieldValue is Document fieldDocument)
                    {
                        return fieldDocument;
                    }

                    if (fieldValue is Node fieldNode)
                    {
                        return fieldNode as Document ?? fieldNode.OwnerDocument;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private FenValue CreatePendingWhenDefinedPromise(string name)
        {
            // Use real JsPromise when execution context is available per WHATWG HTML Â§4.13.5
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new Core.Types.JsPromise(FenValue.FromFunction(executor), _context);
                SetPromiseTrackingState(jsPromise, "pending", FenValue.Undefined, string.Empty);
                _ = ObserveWhenDefinedPromiseAsync(name,
                    value =>
                    {
                        SetPromiseTrackingState(jsPromise, "fulfilled", value, string.Empty);
                        if (capturedResolve.IsFunction)
                        {
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                        }
                    },
                    reason =>
                    {
                        SetPromiseTrackingState(jsPromise, "rejected", FenValue.Undefined, reason);
                        if (capturedReject.IsFunction)
                        {
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(reason) }, _context);
                        }
                    });
                return FenValue.FromObject(jsPromise);
            }

            return CreatePromise(
                "pending",
                FenValue.Undefined,
                string.Empty,
                (resolve, reject) => _ = ObserveWhenDefinedPromiseAsync(name, resolve, reject));
        }

        private FenValue CreateResolvedPromise(FenValue value)
        {
            if (_context != null)
            {
                var promise = Core.Types.JsPromise.Resolve(value, _context);
                SetPromiseTrackingState(promise, "fulfilled", value, string.Empty);
                return FenValue.FromObject(promise);
            }
            return CreatePromise("fulfilled", value, string.Empty);
        }

        private FenValue CreateRejectedPromise(string reason)
        {
            if (_context != null)
            {
                var promise = Core.Types.JsPromise.Reject(FenValue.FromString(reason ?? string.Empty), _context);
                SetPromiseTrackingState(promise, "rejected", FenValue.Undefined, reason ?? string.Empty);
                return FenValue.FromObject(promise);
            }
            return CreatePromise("rejected", FenValue.Undefined, reason ?? string.Empty);
        }

        private static void SetPromiseTrackingState(Core.Types.JsPromise promise, string state, FenValue value, string reason)
        {
            if (promise == null)
            {
                return;
            }

            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString(state ?? string.Empty));

            if (string.Equals(state, "fulfilled", StringComparison.Ordinal))
            {
                promise.Set("__value__", value);
                promise.Delete("__reason__");
            }
            else if (string.Equals(state, "rejected", StringComparison.Ordinal))
            {
                promise.Set("__reason__", FenValue.FromString(reason ?? string.Empty));
                promise.Delete("__value__");
            }
            else
            {
                promise.Delete("__value__");
                promise.Delete("__reason__");
            }
        }

        private FenValue CreatePromise(
            string initialState,
            FenValue initialValue,
            string initialReason,
            Action<Action<FenValue>, Action<string>> subscribe = null)
        {
            var promise = new FenObject();
            var fulfillmentCallbacks = new List<FenFunction>();
            var rejectionCallbacks = new List<FenFunction>();
            var gate = new object();
            var state = initialState;
            var resolvedValue = initialValue;
            var rejectionReason = initialReason ?? string.Empty;

            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString(initialState));
            if (string.Equals(initialState, "fulfilled", StringComparison.Ordinal))
            {
                promise.Set("__value__", initialValue);
            }
            else if (string.Equals(initialState, "rejected", StringComparison.Ordinal))
            {
                promise.Set("__reason__", FenValue.FromString(rejectionReason));
            }

            void Resolve(FenValue value)
            {
                FenFunction[] callbacks;
                lock (gate)
                {
                    if (!string.Equals(state, "pending", StringComparison.Ordinal))
                    {
                        return;
                    }

                    state = "fulfilled";
                    resolvedValue = value;
                    promise.Set("__state__", FenValue.FromString(state));
                    promise.Set("__value__", value);
                    callbacks = fulfillmentCallbacks.ToArray();
                    fulfillmentCallbacks.Clear();
                    rejectionCallbacks.Clear();
                }

                foreach (var callback in callbacks)
                {
                    SchedulePromiseCallback(() => callback.Invoke(new[] { value }, _context));
                }
            }

            void Reject(string reason)
            {
                FenFunction[] callbacks;
                lock (gate)
                {
                    if (!string.Equals(state, "pending", StringComparison.Ordinal))
                    {
                        return;
                    }

                    state = "rejected";
                    rejectionReason = reason ?? string.Empty;
                    promise.Set("__state__", FenValue.FromString(state));
                    promise.Set("__reason__", FenValue.FromString(rejectionReason));
                    callbacks = rejectionCallbacks.ToArray();
                    fulfillmentCallbacks.Clear();
                    rejectionCallbacks.Clear();
                }

                foreach (var callback in callbacks)
                {
                    SchedulePromiseCallback(() => callback.Invoke(new[] { FenValue.FromString(rejectionReason) }, _context));
                }
            }

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0].AsFunction();
                    if (callback != null)
                    {
                        FenValue callbackValue = FenValue.Undefined;
                        bool runCallback = false;

                        lock (gate)
                        {
                            if (string.Equals(state, "fulfilled", StringComparison.Ordinal))
                            {
                                callbackValue = resolvedValue;
                                runCallback = true;
                            }
                            else if (string.Equals(state, "pending", StringComparison.Ordinal))
                            {
                                fulfillmentCallbacks.Add(callback);
                            }
                        }

                        if (runCallback)
                        {
                            SchedulePromiseCallback(() => callback.Invoke(new[] { callbackValue }, _context));
                        }
                    }
                }

                if (args.Length > 1 && args[1].IsFunction)
                {
                    var rejectCallback = args[1].AsFunction();
                    if (rejectCallback != null)
                    {
                        string callbackReason = string.Empty;
                        bool runCallback = false;

                        lock (gate)
                        {
                            if (string.Equals(state, "rejected", StringComparison.Ordinal))
                            {
                                callbackReason = rejectionReason;
                                runCallback = true;
                            }
                            else if (string.Equals(state, "pending", StringComparison.Ordinal))
                            {
                                rejectionCallbacks.Add(rejectCallback);
                            }
                        }

                        if (runCallback)
                        {
                            SchedulePromiseCallback(() => rejectCallback.Invoke(new[] { FenValue.FromString(callbackReason) }, _context));
                        }
                    }
                }

                return FenValue.FromObject(promise);
            })));

            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0].AsFunction();
                    if (callback != null)
                    {
                        string callbackReason = string.Empty;
                        bool runCallback = false;

                        lock (gate)
                        {
                            if (string.Equals(state, "rejected", StringComparison.Ordinal))
                            {
                                callbackReason = rejectionReason;
                                runCallback = true;
                            }
                            else if (string.Equals(state, "pending", StringComparison.Ordinal))
                            {
                                rejectionCallbacks.Add(callback);
                            }
                        }

                        if (runCallback)
                        {
                            SchedulePromiseCallback(() => callback.Invoke(new[] { FenValue.FromString(callbackReason) }, _context));
                        }
                    }
                }

                return FenValue.FromObject(promise);
            })));

            if (string.Equals(initialState, "pending", StringComparison.Ordinal) && subscribe != null)
            {
                try
                {
                    subscribe(Resolve, Reject);
                }
                catch (Exception ex)
                {
                    SchedulePromiseCallback(() => Reject(ex.Message));
                }
            }

            return FenValue.FromObject(promise);
        }

        private async Task ObserveWhenDefinedPromiseAsync(string name, Action<FenValue> resolve, Action<string> reject)
        {
            try
            {
                await WhenDefined(name).ConfigureAwait(false);
                SchedulePromiseCallback(() => resolve(FenValue.Undefined));
            }
            catch (Exception ex)
            {
                SchedulePromiseCallback(() => reject(ex.Message));
            }
        }

        private void SchedulePromiseCallback(Action callback)
        {
            if (callback == null)
            {
                return;
            }

            if (_context?.ScheduleMicrotask != null)
            {
                _context.ScheduleMicrotask(callback);
                return;
            }

            EventLoopCoordinator.Instance.ScheduleMicrotask(callback);
        }
    }

    /// <summary>
    /// Options for customElements.define()
    /// </summary>
    public class CustomElementOptions
    {
        /// <summary>
        /// For customized built-in elements, the tag name to extend
        /// </summary>
        public string Extends { get; set; }
    }

    /// <summary>
    /// Stores a custom element definition with lifecycle callback references.
    /// WHATWG HTML Â§4.13.4: Custom element definition
    /// </summary>
    public class CustomElementDefinition
    {
        public string Name { get; set; }
        public IValue Constructor { get; set; }
        public string Extends { get; set; }

        // Lifecycle callbacks resolved from constructor.prototype
        public FenFunction ConnectedCallback { get; set; }
        public FenFunction DisconnectedCallback { get; set; }
        public FenFunction AdoptedCallback { get; set; }
        public FenFunction AttributeChangedCallback { get; set; }

        // observedAttributes from static getter on constructor
        public HashSet<string> ObservedAttributes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}


