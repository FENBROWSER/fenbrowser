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
["navigator.connection"] = new HostApiSurfaceDescriptor(
"navigator.connection",
HostApiImplementationClass.CompatibilityShim,
"WebAPIs/NetworkInformationAPI.cs",
"Network Information API returns deterministic low-entropy connection metadata and per-runtime event state; live host network telemetry wiring remains future work."),
["navigator.serial"] = new HostApiSurfaceDescriptor(
"navigator.serial",
HostApiImplementationClass.CompatibilityShim,
"WebAPIs/SerialAPI.cs",
"Web Serial API enforces secure-context gating for getPorts(), returns deterministic empty port lists without synthetic devices, and fail-closes permission-gated requestPort() until host picker/device backends are wired."),
["navigator.mediaDevices"] = new HostApiSurfaceDescriptor(
"navigator.mediaDevices",
HostApiImplementationClass.CompatibilityShim,
"WebAPIs/MediaDevicesAPI.cs",
"MediaDevices API enforces secure-context and camera permission gates, returns deterministic empty device lists without synthetic labels, and fail-closes capture calls until host camera/screen backends are wired."),
["crypto.subtle"] = new HostApiSurfaceDescriptor(
"crypto.subtle",
HostApiImplementationClass.CompatibilityShim,
"Scripting/JavaScriptEngine.cs",
                "Legacy JavaScriptEngine crypto bridge now provides digest(), generateKey(), importKey(), exportKey(), deriveBits(), deriveKey(), wrapKey(), unwrapKey(), encrypt(), decrypt(), sign(), and verify() for HMAC, AES-GCM, RSA-OAEP, PBKDF2, HKDF, and RSASSA-PKCS1-v1_5 (65537 exponent), with fail-closed usage/extractability/parameter checks and deterministic thenable settlement; non-HKDF/PBKDF2 derive families remain pending."),
["window.open"] = new HostApiSurfaceDescriptor(
"window.open",
HostApiImplementationClass.CompatibilityShim,
"Core/FenRuntime.cs",
"window.open now enforces popup policy, blocks unsafe javascript:/data: URLs, and honors noopener/noreferrer null-return semantics, but popup/tab browsing-context creation still falls back to same-window navigation."),
["window.matchMedia"] = new HostApiSurfaceDescriptor(
"window.matchMedia",
HostApiImplementationClass.ProductionImplementation,
"Core/FenRuntime.cs",
"FenRuntime evaluates media queries against the active browser surface and keeps MediaQueryList objects synchronized as viewport and theme data change."),
["window.createImageBitmap"] = new HostApiSurfaceDescriptor(
"window.createImageBitmap",
HostApiImplementationClass.ProductionImplementation,
"WebAPIs/ImageBitmapAPI.cs",
"ImageBitmap API creates ImageBitmap from various image sources. Supports HTMLImageElement, Canvas, ImageData; Blob is future work."),
["window.navigation"] = new HostApiSurfaceDescriptor(
"window.navigation",
HostApiImplementationClass.CompatibilityShim,
"WebAPIs/NavigationAPI.cs",
"Navigation API shim now uses runtime-local history state with deterministic entries/navigate/reload/back/forward/traverseTo behavior; traverseByType and full transition/event semantics remain future work."),
                ["window.requestIdleCallback"] = new HostApiSurfaceDescriptor(
                    "window.requestIdleCallback",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "requestIdleCallback/cancelIdleCallback now enforce callable callback validation and timeout parsing with synthetic idle deadlines; scheduler budget remains heuristic and host-idle integration is future work."),
                ["window.MessageChannel"] = new HostApiSurfaceDescriptor(
                    "window.MessageChannel",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "MessageChannel/MessagePort now use structured-clone payload delivery (with transfer-list support) and message/messageerror listener wiring, while full port lifecycle/start entanglement parity is still pending."),
                ["window.BroadcastChannel"] = new HostApiSurfaceDescriptor(
                    "window.BroadcastChannel",
                    HostApiImplementationClass.CompatibilityShim,
                    "Core/FenRuntime.cs",
                    "BroadcastChannel now enforces closed-channel InvalidStateError and structured-clone payload delivery across same-name channels in-process; cross-process persistence and full origin-agent-cluster semantics remain future work."),
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

            EngineLogCompat.Warn(
                $"[HostApiSurface] {descriptor.Id} is classified as {descriptor.ImplementationClass}: {descriptor.Summary}",
                LogCategory.FeatureGaps);
        }
    }
}
