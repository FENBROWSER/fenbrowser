using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// ECMA-262 §10.4.6 Module Namespace Exotic Objects.
    /// Wraps a module's exported bindings as live accessor properties that
    /// read from the originating module environment on every access.
    /// </summary>
    public sealed class ModuleNamespaceObject : FenObject
    {
        private readonly FenEnvironment _moduleEnv;
        private readonly Dictionary<string, string> _exportBindings;

        /// <summary>
        /// Creates a module namespace exotic object.
        /// </summary>
        /// <param name="moduleEnv">The module's top-level environment.</param>
        /// <param name="exportBindings">
        /// Map from exported name → environment variable name (typically "__fen_export_" + exportedName).
        /// </param>
        public ModuleNamespaceObject(FenEnvironment moduleEnv, Dictionary<string, string> exportBindings)
            : base()
        {
            _moduleEnv = moduleEnv ?? throw new ArgumentNullException(nameof(moduleEnv));
            _exportBindings = exportBindings ?? throw new ArgumentNullException(nameof(exportBindings));

            InternalClass = "Module";

            // ECMA-262 §10.4.6: [[Prototype]] is null
            TrySetPrototype(null);

            // @@toStringTag = "Module"
            DefineOwnProperty("Symbol(Symbol.toStringTag)", new PropertyDescriptor
            {
                Value = FenValue.FromString("Module"),
                Writable = false,
                Enumerable = false,
                Configurable = false
            });

            // Install live accessor bindings for each export (sorted per spec)
            foreach (var exportName in exportBindings.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var envVarName = exportBindings[exportName];
                InstallLiveBinding(exportName, envVarName);
            }

            // Note: PreventExtensions is called by Seal() after star bindings are installed.
            // The caller (ModuleLoader) must call Seal() after ApplyExportStarAggregationsLive.
        }

        private void InstallLiveBinding(string exportName, string envVarName)
        {
            // Capture for closure
            var env = _moduleEnv;
            var varName = envVarName;

            DefineOwnProperty(exportName, new PropertyDescriptor
            {
                Getter = new FenFunction($"get {exportName}", (args, thisVal) =>
                {
                    return env.Get(varName);
                }),
                // No setter — module namespace properties are read-only
                Enumerable = true,
                Configurable = false
            });
        }

        /// <summary>
        /// Installs additional star-reexported bindings after initial construction.
        /// Called during export * aggregation for bindings from re-exported modules.
        /// </summary>
        internal void InstallStarBinding(string exportName, IObject sourceNamespace, string sourceExportName)
        {
            if (string.IsNullOrEmpty(exportName) || sourceNamespace == null)
                return;

            var src = sourceNamespace;
            var srcName = sourceExportName ?? exportName;

            // Star bindings are initially configurable so they can be removed if ambiguous.
            // SealNamespace() will make them non-configurable later.
            DefineOwnProperty(exportName, new PropertyDescriptor
            {
                Getter = new FenFunction($"get {exportName}", (args, thisVal) =>
                {
                    return src.Get(srcName);
                }),
                Enumerable = true,
                Configurable = true
            });
        }

        /// <summary>
        /// Remove a star-exported binding that turned out to be ambiguous.
        /// </summary>
        internal void RemoveStarBinding(string exportName)
        {
            // During construction, the object is still extensible, so we can remove properties.
            base.Delete(exportName);
        }

        /// <summary>
        /// Finalizes the namespace object: makes it non-extensible with non-configurable properties.
        /// Must be called after all star bindings are installed.
        /// </summary>
        internal void SealNamespace()
        {
            // Make all remaining configurable star-bindings non-configurable, then prevent extensions
            Seal();
        }

        // ECMA-262 §10.4.6.8: [[Set]] always returns false for module namespaces
        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            throw new FenTypeError($"TypeError: Cannot assign to read only property '{key}' of object '[object Module]'");
        }

        public override void Set(FenValue key, FenValue value, IExecutionContext context = null)
        {
            throw new FenTypeError($"TypeError: Cannot assign to read only property '{key}' of object '[object Module]'");
        }

        // ECMA-262 §10.4.6.9: [[Delete]] returns true only for non-existent properties
        public override bool Delete(string key, IExecutionContext context = null)
        {
            var desc = GetOwnPropertyDescriptor(key);
            if (desc.HasValue)
            {
                return base.Delete(key, context);
            }
            return true; // Non-existent key: succeeds vacuously
        }
    }
}
