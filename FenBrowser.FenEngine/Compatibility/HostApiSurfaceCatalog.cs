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
                    HostApiImplementationClass.CompatibilityShim,
                    "Scripting/JavaScriptEngine.cs",
                    "Client hints surface is a simplified object, not a full browser client-hints implementation."),
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
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "Media query evaluation is currently hard-coded to a fixed environment instead of a live media feature pipeline."),
                ["window.requestIdleCallback"] = new HostApiSurfaceDescriptor(
                    "window.requestIdleCallback",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "Idle deadlines are synthetic and currently expose a fixed timeRemaining budget."),
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
