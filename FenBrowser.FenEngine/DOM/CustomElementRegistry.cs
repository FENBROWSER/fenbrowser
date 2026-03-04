using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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

                _definitions[name] = definition;

                FenLogger.Debug($"[CustomElements] Defined custom element: {name}", LogCategory.JavaScript);

                // Resolve whenDefined promise if any
                if (_whenDefinedPromises.TryGetValue(name, out var tcs))
                {
                    tcs.TrySetResult(true);
                    _whenDefinedPromises.Remove(name);
                }
            }
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
                    tcs = new TaskCompletionSource<bool>();
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
                if (_definitions.TryGetValue(tag, out definition))
                {
                    // Mark element as upgraded
                    element.SetAttribute("data-ce-upgraded", "true");
                    FenLogger.Debug($"[CustomElements] Upgraded element: {tag}", LogCategory.JavaScript);

                    // Fire connectedCallback if the element is already connected to a document
                    if (element.IsConnected && _context != null && definition.Constructor is FenValue ctorVal)
                    {
                        try
                        {
                            var protoObj = ctorVal.AsObject();
                            if (protoObj != null)
                            {
                                var proto = protoObj.Get("prototype");
                                if (proto.IsObject)
                                {
                                    var connectedCb = proto.AsObject()?.Get("connectedCallback");
                                    if (connectedCb.HasValue && connectedCb.Value.IsFunction)
                                        connectedCb.Value.AsFunction()?.Invoke(System.Array.Empty<FenValue>(), _context);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Error($"[CustomElements] connectedCallback error on <{tag}>: {ex.Message}", LogCategory.JavaScript);
                        }
                    }
                }
                else
                {
                    // Check for customized built-in element (via is="" attribute)
                    var isAttr = element.GetAttribute("is");
                    if (!string.IsNullOrEmpty(isAttr) && _definitions.TryGetValue(isAttr, out definition))
                    {
                        element.SetAttribute("data-ce-upgraded", "true");
                        FenLogger.Debug($"[CustomElements] Upgraded built-in element: {tag} is={isAttr}", LogCategory.JavaScript);
                    }
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

                // Return a promise-like object
                var promise = new FenObject();
                promise.Set("__isPromise__", FenValue.FromBoolean(true));
                promise.Set("__state__", FenValue.FromString("pending"));

                // Store for later resolution
                WhenDefined(name).ContinueWith(t =>
                {
                    promise.Set("__state__", FenValue.FromString("fulfilled"));
                    promise.Set("__value__", FenValue.Undefined);
                });

                promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                    FenValue.FromObject(promise))));

                return FenValue.FromObject(promise);
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

        private FenValue CreateResolvedPromise(FenValue value)
        {
            var promise = new FenObject();
            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString("fulfilled"));
            promise.Set("__value__", (FenValue)value);
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var fn = args[0].AsFunction() as FenFunction;
                    return fn?.Invoke(new FenValue[] { value }, null) ?? FenValue.Undefined;
                }
                return (FenValue)value;
            })));
            return FenValue.FromObject(promise);
        }

        private FenValue CreateRejectedPromise(string reason)
        {
            var promise = new FenObject();
            promise.Set("__isPromise__", FenValue.FromBoolean(true));
            promise.Set("__state__", FenValue.FromString("rejected"));
            promise.Set("__reason__", FenValue.FromString(reason));
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var fn = args[0].AsFunction() as FenFunction;
                    return fn?.Invoke(new FenValue[] { FenValue.FromString(reason) }, null) ?? FenValue.Undefined;
                }
                return FenValue.Undefined;
            })));
            return FenValue.FromObject(promise);
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
    /// Stores a custom element definition
    /// </summary>
    public class CustomElementDefinition
    {
        public string Name { get; set; }
        public IValue Constructor { get; set; }
        public string Extends { get; set; }
    }
}


