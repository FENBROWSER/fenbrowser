using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Compatibility
{
    public enum HostApiImplementationClass
    {
        ProductionImplementation,
        CompatibilityShim,
        Simulation
    }

    public sealed class HostApiSurfaceDescriptor
    {
        public HostApiSurfaceDescriptor(
            string id,
            HostApiImplementationClass implementationClass,
            string owner,
            string summary)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            ImplementationClass = implementationClass;
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        }

        public string Id { get; }

        public HostApiImplementationClass ImplementationClass { get; }

        public string Owner { get; }

        public string Summary { get; }

        public override string ToString()
        {
            return $"{Id} [{ImplementationClass}] ({Owner}) - {Summary}";
        }
    }

    public static class HostApiSurfaceCatalog
    {
        private static readonly Dictionary<string, HostApiSurfaceDescriptor> Entries =
            new(StringComparer.Ordinal)
            {
                ["navigator.userAgentData"] = new HostApiSurfaceDescriptor(
                    "navigator.userAgentData",
                    HostApiImplementationClass.ProductionImplementation,
                    "Core/FenRuntime.cs",
                    "FenRuntime exposes coherent low-entropy and high-entropy UA-CH data, including brands, platform, and getHighEntropyValues snapshots for the active browser surface."),
                ["crypto.subtle"] = new HostApiSurfaceDescriptor(
                    "crypto.subtle",
                    HostApiImplementationClass.CompatibilityShim,
                    "Scripting/JavaScriptEngine.cs",
                    "Legacy JavaScriptEngine crypto bridge still exposes subtle as a minimal object placeholder."),
                ["window.open"] = new HostApiSurfaceDescriptor(
                    "window.open",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "Popup orchestration still falls back to same-window navigation until real browsing-context creation exists."),
                ["window.matchMedia"] = new HostApiSurfaceDescriptor(
                    "window.matchMedia",
                    HostApiImplementationClass.ProductionImplementation,
                    "Core/FenRuntime.cs",
                    "FenRuntime evaluates media queries against the active browser surface and keeps MediaQueryList objects synchronized as viewport and theme data change."),
                ["window.requestIdleCallback"] = new HostApiSurfaceDescriptor(
                    "window.requestIdleCallback",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "Idle deadlines are synthetic and currently expose a fixed timeRemaining budget."),
                ["window.MessageChannel"] = new HostApiSurfaceDescriptor(
                    "window.MessageChannel",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "MessageChannel/MessagePort are currently implemented as a lightweight event-loop shim without structured-clone transfer lists or full port lifecycle semantics."),
                ["window.BroadcastChannel"] = new HostApiSurfaceDescriptor(
                    "window.BroadcastChannel",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "BroadcastChannel is implemented as an in-process event-loop shim that delivers same-name messages across live FenRuntime channel instances without cross-process persistence."),
                ["Intl"] = new HostApiSurfaceDescriptor(
                    "Intl",
                    HostApiImplementationClass.ProductionImplementation,
                    "Core/Types/JsIntl.cs",
                    "FenEngine ships a real baseline Intl implementation, but it does not claim full ICU or browser locale-data parity.")
            };

        private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
        private static readonly object WarnLock = new();

        public static IReadOnlyList<HostApiSurfaceDescriptor> GetEntries()
        {
            return Entries.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        }

        public static HostApiSurfaceDescriptor GetRequired(string id)
        {
            if (!Entries.TryGetValue(id, out var descriptor))
                throw new KeyNotFoundException($"Host API surface '{id}' is not classified.");

            return descriptor;
        }

        public static void TraceUsage(string id)
        {
            var descriptor = GetRequired(id);
            if (descriptor.ImplementationClass == HostApiImplementationClass.ProductionImplementation)
                return;

            lock (WarnLock)
            {
                if (!Warned.Add(id))
                    return;
            }

            FenLogger.Warn(
                $"[HostApiSurface] {descriptor.Id} is classified as {descriptor.ImplementationClass}: {descriptor.Summary}",
                LogCategory.FeatureGaps);
        }
    }
}
