using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Accessibility
{
    // ── Accessibility Platform Bridges ────────────────────────────────────────
    // Maps FenBrowser's internal AX tree to OS accessibility APIs:
    //   - Windows:  UI Automation (UIA) via COM IAccessible2 / IRawElementProviderSimple
    //   - Linux:    AT-SPI2 (DBus-based, IAccessible compatible)
    //   - macOS:    NSAccessibility protocol (via P/Invoke to AppKit Objective-C)
    //
    // Design: the bridge is a thin translation layer — it does not own state.
    // It reads from AccessibilityTree (already maintained with invalidation)
    // and translates AccessibilityNode to platform objects on demand.
    //
    // Invalidation: DOM/ARIA mutations trigger AccessibilityTree.Invalidate()
    //               → platform bridge receives change notification and fires
    //               the appropriate platform event (UIA PropertyChanged, AT-SPI
    //               state-changed, NSAccessibility notification).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Platform accessibility API target.</summary>
    public enum A11yPlatform { None, WindowsUia, LinuxAtSpi, MacOsNsAccessibility }

    /// <summary>Accessibility event types (platform-agnostic).</summary>
    public enum A11yEvent
    {
        FocusChanged,
        StructureChanged,        // node added/removed
        PropertyChanged,         // name/role/state changed
        StateChanged,
        TextChanged,
        SelectionChanged,
        ValueChanged,
        MenuOpened,
        MenuClosed,
        LiveRegionChanged,
        AlertFired,
    }

    /// <summary>
    /// Abstraction over platform-specific AX APIs.
    /// Implemented per-platform; the host chooses the right implementation.
    /// </summary>
    public interface IPlatformA11yBridge : IDisposable
    {
        A11yPlatform Platform { get; }
        bool IsAvailable { get; }

        /// <summary>Called when the AX tree is first built or fully invalidated.</summary>
        void Initialize(AccessibilityTree tree, IntPtr nativeWindowHandle);

        /// <summary>Fire an AX event for the given node.</summary>
        void FireEvent(AccessibilityNode node, A11yEvent eventType);

        /// <summary>Notify of a property change for the given node.</summary>
        void FirePropertyChanged(AccessibilityNode node, string propertyName, object oldValue, object newValue);

        /// <summary>Update the root window handle (e.g. after window resize/recreate).</summary>
        void UpdateWindowHandle(IntPtr hwnd);
    }

    /// <summary>
    /// Factory: returns the best available platform bridge.
    /// </summary>
    public static class PlatformA11yBridgeFactory
    {
        public static IPlatformA11yBridge Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new WindowsUiaBridge();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return new LinuxAtSpiBridge();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new MacOsNsAccessibilityBridge();
            return new NullA11yBridge();
        }
    }

    // ── Windows UIA bridge ────────────────────────────────────────────────────

    /// <summary>
    /// Windows UI Automation bridge.
    /// Exposes the AX tree via IRawElementProviderSimple (UIA provider pattern).
    /// The renderer implements IRawElementProviderFragment; the broker serves
    /// UIA client requests from assistive technologies (screen readers, etc.).
    ///
    /// Architecture:
    ///   - WindowsUiaBridge registers the root provider with UiaReturnRawElementProvider.
    ///   - For each AccessibilityNode, a UiaNodeProvider is created on demand.
    ///   - Events are fired via UiaRaiseAutomationEvent / UiaRaisePropertyChangedEvent.
    ///
    /// P/Invoke pattern: we use UIAutomationCore.dll via P/Invoke rather than
    /// the managed UIAutomationClient assembly to avoid WPF dependency.
    /// </summary>
    public sealed class WindowsUiaBridge : IPlatformA11yBridge
    {
        private IntPtr _hwnd;
        private AccessibilityTree _tree;
        private readonly Dictionary<int, UiaNodeProvider> _nodeProviders = new();
        private bool _initialized;
        private bool _disposed;

        public A11yPlatform Platform => A11yPlatform.WindowsUia;

        public bool IsAvailable
        {
            get
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
                try { return NativeWindowsUia.UiaClientsAreListening(); }
                catch { return false; }
            }
        }

        public void Initialize(AccessibilityTree tree, IntPtr nativeWindowHandle)
        {
            if (_tree != null)
                _tree.TreeInvalidated -= OnTreeInvalidated;

            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
            _hwnd = nativeWindowHandle;
            _initialized = true;

            // Subscribe to invalidation notifications
            if (tree != null)
                tree.TreeInvalidated += OnTreeInvalidated;

            // Register the root provider with UIA
            if (_hwnd != IntPtr.Zero && IsAvailable)
            {
                try
                {
                    var rootNode = tree.Root;
                    if (rootNode != null)
                    {
                        var rootProvider = GetOrCreateProvider(rootNode);
                        NativeWindowsUia.UiaReturnRawElementProvider(_hwnd, IntPtr.Zero, IntPtr.Zero, rootProvider.NativeProvider);
                    }
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Warn($"[UIA] Failed to register root provider: {ex.Message}", LogCategory.General);
                }
            }
        }

        public void FireEvent(AccessibilityNode node, A11yEvent eventType)
        {
            if (!_initialized || !IsAvailable || node == null) return;

            var uiaEventId = MapEventId(eventType);
            if (uiaEventId == 0) return;

            try
            {
                var provider = GetOrCreateProvider(node);
                NativeWindowsUia.UiaRaiseAutomationEvent(provider.NativeProvider, uiaEventId);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[UIA] FireEvent failed: {ex.Message}", LogCategory.General);
            }
        }

        public void FirePropertyChanged(AccessibilityNode node, string propertyName, object oldValue, object newValue)
        {
            if (!_initialized || !IsAvailable || node == null) return;

            var propId = MapPropertyId(propertyName);
            if (propId == 0) return;

            try
            {
                var provider = GetOrCreateProvider(node);
                NativeWindowsUia.UiaRaisePropertyChangedEvent(provider.NativeProvider, propId,
                    oldValue ?? NativeWindowsUia.EmptyVariant,
                    newValue ?? NativeWindowsUia.EmptyVariant);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[UIA] PropertyChanged failed: {ex.Message}", LogCategory.General);
            }
        }

        public void UpdateWindowHandle(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (_tree?.Root != null && IsAvailable)
            {
                try
                {
                    var rootProvider = GetOrCreateProvider(_tree.Root);
                    NativeWindowsUia.UiaReturnRawElementProvider(hwnd, IntPtr.Zero, IntPtr.Zero, rootProvider.NativeProvider);
                }
                catch { }
            }
        }

        private void OnTreeInvalidated()
        {
            // Rebuild provider map after tree invalidation
            lock (_nodeProviders) { _nodeProviders.Clear(); }

            // Fire StructureChanged on root
            if (_tree?.Root != null)
                FireEvent(_tree.Root, A11yEvent.StructureChanged);
        }

        private UiaNodeProvider GetOrCreateProvider(AccessibilityNode node)
        {
            var id = node?.SourceElement?.GetHashCode() ?? 0;
            lock (_nodeProviders)
            {
                if (!_nodeProviders.TryGetValue(id, out var p))
                {
                    p = new UiaNodeProvider(node);
                    _nodeProviders[id] = p;
                }
                return p;
            }
        }

        private static int MapEventId(A11yEvent e) => e switch
        {
            A11yEvent.FocusChanged      => NativeWindowsUia.UIA_AutomationFocusChangedEventId,
            A11yEvent.StructureChanged  => NativeWindowsUia.UIA_StructureChangedEventId,
            A11yEvent.LiveRegionChanged => NativeWindowsUia.UIA_LiveRegionChangedEventId,
            A11yEvent.AlertFired        => NativeWindowsUia.UIA_SystemAlertEventId,
            A11yEvent.MenuOpened        => NativeWindowsUia.UIA_MenuOpenedEventId,
            A11yEvent.MenuClosed        => NativeWindowsUia.UIA_MenuClosedEventId,
            _ => 0
        };

        private static int MapPropertyId(string propName) => propName switch
        {
            "name"        => NativeWindowsUia.UIA_NamePropertyId,
            "role"        => NativeWindowsUia.UIA_ControlTypePropertyId,
            "value"       => NativeWindowsUia.UIA_ValueValuePropertyId,
            "description" => NativeWindowsUia.UIA_FullDescriptionPropertyId,
            "enabled"     => NativeWindowsUia.UIA_IsEnabledPropertyId,
            "expanded"    => NativeWindowsUia.UIA_ExpandCollapseExpandCollapseStatePropertyId,
            "checked"     => NativeWindowsUia.UIA_ToggleToggleStatePropertyId,
            _ => 0
        };

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_tree != null) _tree.TreeInvalidated -= OnTreeInvalidated;
                lock (_nodeProviders) { _nodeProviders.Clear(); }
            }
        }
    }

    /// <summary>Wraps an AccessibilityNode and exposes a UIA IRawElementProviderSimple.</summary>
    internal sealed class UiaNodeProvider
    {
        public AccessibilityNode Node { get; }
        public IntPtr NativeProvider { get; }  // COM pointer (IntPtr.Zero in non-Windows or unavailable)

        public UiaNodeProvider(AccessibilityNode node)
        {
            Node = node;
            // In a full implementation, NativeProvider would be a COM pointer obtained by
            // registering a managed IRawElementProviderSimple implementation via COM interop.
            // For now, we use a placeholder; the P/Invoke stubs protect against null.
            NativeProvider = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Minimal P/Invoke declarations for UIAutomationCore.dll.
    /// Only the entry points needed for the bridge are declared.
    /// </summary>
    internal static class NativeWindowsUia
    {
        // UIA event IDs (from uiautomationclient.h)
        public const int UIA_AutomationFocusChangedEventId  = 20005;
        public const int UIA_StructureChangedEventId         = 20002;
        public const int UIA_LiveRegionChangedEventId        = 20024;
        public const int UIA_SystemAlertEventId              = 20011;
        public const int UIA_MenuOpenedEventId               = 20003;
        public const int UIA_MenuClosedEventId               = 20004;

        // UIA property IDs (from uiautomationclient.h)
        public const int UIA_NamePropertyId              = 30005;
        public const int UIA_ControlTypePropertyId       = 30003;
        public const int UIA_ValueValuePropertyId        = 30045;
        public const int UIA_FullDescriptionPropertyId   = 30159;
        public const int UIA_IsEnabledPropertyId         = 30010;
        public const int UIA_ExpandCollapseExpandCollapseStatePropertyId = 30070;
        public const int UIA_ToggleToggleStatePropertyId = 30086;

        public static readonly object EmptyVariant = DBNull.Value;

        [DllImport("UIAutomationCore.dll", SetLastError = false, CharSet = CharSet.Auto)]
        public static extern bool UiaClientsAreListening();

        [DllImport("UIAutomationCore.dll", SetLastError = false)]
        public static extern int UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wParam, IntPtr lParam, IntPtr provider);

        [DllImport("UIAutomationCore.dll", SetLastError = false)]
        public static extern int UiaRaiseAutomationEvent(IntPtr provider, int id);

        [DllImport("UIAutomationCore.dll", SetLastError = false)]
        public static extern int UiaRaisePropertyChangedEvent(IntPtr provider, int propertyId, object oldValue, object newValue);
    }

    // ── Linux AT-SPI2 bridge ──────────────────────────────────────────────────

    /// <summary>
    /// Linux AT-SPI2 accessibility bridge.
    /// AT-SPI2 uses DBus for IPC between the accessible application and AT clients.
    /// The bridge registers an AT-SPI2 accessible root and fires state/property
    /// change events via the at-spi2-core DBus interface.
    /// </summary>
    public sealed class LinuxAtSpiBridge : IPlatformA11yBridge
    {
        private AccessibilityTree _tree;
        private LinuxAtSpiEventBroker _eventBroker;
        private bool _available;
        private bool _disposed;

        public A11yPlatform Platform => A11yPlatform.LinuxAtSpi;

        public bool IsAvailable
        {
            get
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
                try
                {
                    // AT_SPI_BUS_ADDRESS is set by at-spi2-registryd and is the definitive
                    // indicator that an AT-SPI2 bus is running for this session.
                    // DBUS_SESSION_BUS_ADDRESS alone does not imply AT-SPI2 is active.
                    return !string.IsNullOrEmpty(
                        Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS"));
                }
                catch { return false; }
            }
        }

        public void Initialize(AccessibilityTree tree, IntPtr nativeWindowHandle)
        {
            if (_tree != null)
                _tree.TreeInvalidated -= OnTreeInvalidated;

            _tree = tree;
            _available = IsAvailable;

            if (!_available)
            {
                EngineLogCompat.Info("[AT-SPI] AT-SPI2 bus not found (AT_SPI_BUS_ADDRESS unset). " +
                    "Accessibility events will not reach assistive technologies.", LogCategory.Accessibility);
                return;
            }

            _eventBroker?.Dispose();
            if (!LinuxAtSpiEventBroker.TryCreate(out _eventBroker, out var failureReason))
            {
                _available = false;
                EngineLogCompat.Warn(
                    $"[AT-SPI] Failed to initialize event broker: {failureReason}",
                    LogCategory.Accessibility);
                return;
            }

            if (tree != null)
                tree.TreeInvalidated += OnTreeInvalidated;

            EngineLogCompat.Info(
                "[AT-SPI] Accessibility bridge initialized with bounded event queue and DBus signal emission.",
                LogCategory.Accessibility);
        }

        public void FireEvent(AccessibilityNode node, A11yEvent eventType)
        {
            if (!_available || node == null) return;

            if (!TryCreateSignal(node, eventType, propertyName: null, oldValue: null, newValue: null, out var signal))
            {
                return;
            }

            if (!_eventBroker.TryPost(signal, out var failureReason))
            {
                EngineLogCompat.Warn(
                    $"[AT-SPI] Dropped event '{signal.Member}' for node '{node.Name}': {failureReason}",
                    LogCategory.Accessibility);
            }
        }

        public void FirePropertyChanged(AccessibilityNode node, string propertyName, object oldValue, object newValue)
        {
            if (!_available || node == null) return;

            if (!TryCreateSignal(node, A11yEvent.PropertyChanged, propertyName, oldValue, newValue, out var signal))
            {
                return;
            }

            if (!_eventBroker.TryPost(signal, out var failureReason))
            {
                EngineLogCompat.Warn(
                    $"[AT-SPI] Dropped property change '{propertyName}' for node '{node.Name}': {failureReason}",
                    LogCategory.Accessibility);
            }
        }

        public void UpdateWindowHandle(IntPtr hwnd) { /* AT-SPI2 is window-handle-independent */ }

        private void OnTreeInvalidated()
        {
            if (!_available) return;
            // Signal AT-SPI2 clients that the tree structure changed
            FireEvent(_tree?.Root, A11yEvent.StructureChanged);
        }

        private static bool TryCreateSignal(
            AccessibilityNode node,
            A11yEvent eventType,
            string propertyName,
            object oldValue,
            object newValue,
            out AtSpiSignal signal)
        {
            signal = null;
            if (node == null)
            {
                return false;
            }

            var payload = newValue?.ToString()
                ?? oldValue?.ToString()
                ?? node.Name
                ?? string.Empty;

            switch (eventType)
            {
                case A11yEvent.FocusChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "StateChanged",
                        Detail1 = "focused",
                        Detail2 = 1,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.PropertyChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "PropertyChange",
                        Detail1 = propertyName ?? "property-change",
                        Detail2 = 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.StructureChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "ChildrenChanged",
                        Detail1 = "structure",
                        Detail2 = node.Children?.Count ?? 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.TextChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "TextChanged",
                        Detail1 = "text",
                        Detail2 = 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.SelectionChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "SelectionChanged",
                        Detail1 = "selection",
                        Detail2 = 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.ValueChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "PropertyChange",
                        Detail1 = "accessible-value",
                        Detail2 = 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.LiveRegionChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "PropertyChange",
                        Detail1 = "accessible-name",
                        Detail2 = 0,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.AlertFired:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "StateChanged",
                        Detail1 = "showing",
                        Detail2 = 1,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                case A11yEvent.StateChanged:
                    signal = new AtSpiSignal
                    {
                        InterfaceName = "org.a11y.atspi.Event.Object",
                        Member = "StateChanged",
                        Detail1 = propertyName ?? "state",
                        Detail2 = 1,
                        Detail3 = 0,
                        Payload = payload
                    };
                    return true;
                default:
                    return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _eventBroker?.Dispose();
                if (_tree != null) _tree.TreeInvalidated -= OnTreeInvalidated;
            }
        }
    }

    // ── macOS NSAccessibility bridge ──────────────────────────────────────────

    /// <summary>
    /// macOS NSAccessibility protocol bridge.
    /// Exposes the AX tree via the macOS Accessibility API (AppKit NSAccessibility).
    /// Fires NSAccessibility notifications via the AppKit runtime.
    /// </summary>
    public sealed class MacOsNsAccessibilityBridge : IPlatformA11yBridge
    {
        private AccessibilityTree _tree;
        private bool _available;
        private bool _disposed;

        public A11yPlatform Platform => A11yPlatform.MacOsNsAccessibility;

        public bool IsAvailable
        {
            get
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
                try { return NativeMacA11y.AXAPIEnabled(); }
                catch { return false; }
            }
        }

        public void Initialize(AccessibilityTree tree, IntPtr nativeWindowHandle)
        {
            if (_tree != null)
                _tree.TreeInvalidated -= OnTreeInvalidated;

            _tree = tree;
            _available = IsAvailable;

            if (!_available)
            {
                EngineLogCompat.Info("[NSAccessibility] Not available on this system.", LogCategory.General);
                return;
            }

            if (tree != null)
                tree.TreeInvalidated += OnTreeInvalidated;

            EngineLogCompat.Info("[NSAccessibility] Accessibility bridge initialized.", LogCategory.General);
        }

        public void FireEvent(AccessibilityNode node, A11yEvent eventType)
        {
            if (!_available || node == null) return;

            var nsNotification = MapNsNotification(eventType);
            if (nsNotification == null) return;

            try
            {
                // In production: call NSAccessibilityPostNotification(element, notification)
                // via P/Invoke to the AppKit Objective-C runtime.
                NativeMacA11y.PostNotification(IntPtr.Zero, nsNotification);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[NSAccessibility] PostNotification failed: {ex.Message}", LogCategory.General);
            }
        }

        public void FirePropertyChanged(AccessibilityNode node, string propertyName, object oldValue, object newValue)
        {
            if (!_available || node == null) return;
            var notif = propertyName switch
            {
                "name"  => "AXTitleChanged",
                "value" => "AXValueChanged",
                _       => "AXUIElementDestroyed"
            };
            try { NativeMacA11y.PostNotification(IntPtr.Zero, notif); }
            catch { }
        }

        public void UpdateWindowHandle(IntPtr hwnd) { /* macOS uses NSWindow, not HWND */ }

        private void OnTreeInvalidated()
        {
            if (!_available) return;
            FireEvent(_tree?.Root, A11yEvent.StructureChanged);
        }

        private static string MapNsNotification(A11yEvent e) => e switch
        {
            A11yEvent.FocusChanged      => "AXFocusedUIElementChanged",
            A11yEvent.StructureChanged  => "AXUIElementDestroyed",
            A11yEvent.ValueChanged      => "AXValueChanged",
            A11yEvent.StateChanged      => "AXUIElementDestroyed",
            A11yEvent.SelectionChanged  => "AXSelectedTextChanged",
            A11yEvent.LiveRegionChanged => "AXAnnouncementRequested",
            A11yEvent.AlertFired        => "AXAnnouncementRequested",
            A11yEvent.MenuOpened        => "AXMenuOpened",
            A11yEvent.MenuClosed        => "AXMenuClosed",
            _ => null
        };

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_tree != null) _tree.TreeInvalidated -= OnTreeInvalidated;
            }
        }
    }

    /// <summary>
    /// AppKit / ApplicationServices P/Invoke for NSAccessibility.
    /// Notification posting uses the Objective-C runtime so that we avoid a hard
    /// link against AppKit.framework (which is loaded lazily on demand).
    /// </summary>
    internal static class NativeMacA11y
    {
        // ── Objective-C runtime ────────────────────────────────────────────────
        [DllImport("libobjc.A.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr ObjcGetClass(string name);

        [DllImport("libobjc.A.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr SelRegisterName(string name);

        // objc_msgSend for id return type
        [DllImport("libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr ObjcMsgSend(IntPtr receiver, IntPtr selector);

        // objc_msgSend for id return + const char* argument
        [DllImport("libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr ObjcMsgSendStr(IntPtr receiver, IntPtr selector, string arg);

        // NSAccessibilityPostNotification(element, notification)
        [DllImport("AppKit.framework/AppKit", EntryPoint = "NSAccessibilityPostNotification")]
        private static extern void NsAccessibilityPostNotification(IntPtr element, IntPtr notificationName);

        // ── ApplicationServices ────────────────────────────────────────────────
        // AXIsProcessTrusted() — the correct way to test whether the process has
        // been granted accessibility access by the user (System Preferences → Privacy).
        [DllImport("ApplicationServices.framework/ApplicationServices",
            EntryPoint = "AXIsProcessTrusted")]
        private static extern bool AXIsProcessTrusted();

        // ── Cached selectors ───────────────────────────────────────────────────
        private static readonly Lazy<IntPtr> _selStringWithUtf8 = new Lazy<IntPtr>(
            () => SelRegisterName("stringWithUTF8String:"));
        private static readonly Lazy<IntPtr> _nsStringClass = new Lazy<IntPtr>(
            () => ObjcGetClass("NSString"));

        /// <summary>
        /// Returns true only when the process has been granted Accessibility access.
        /// Replaces the previous (always-true) NSApplication existence check.
        /// </summary>
        public static bool AXAPIEnabled()
        {
            try { return AXIsProcessTrusted(); }
            catch { return false; }
        }

        /// <summary>
        /// Posts an NSAccessibility notification for the given element.
        /// Creates an NSString from <paramref name="notification"/> via the ObjC runtime
        /// then calls NSAccessibilityPostNotification.
        /// </summary>
        public static void PostNotification(IntPtr element, string notification)
        {
            if (string.IsNullOrEmpty(notification)) return;
            try
            {
                // Build NSString* for the notification name.
                var nsStr = ObjcMsgSendStr(_nsStringClass.Value, _selStringWithUtf8.Value, notification);
                if (nsStr == IntPtr.Zero) return;
                NsAccessibilityPostNotification(element, nsStr);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[NSAccessibility] PostNotification('{notification}') failed: {ex.Message}", LogCategory.General);
            }
        }
    }

    // ── Null bridge (no-op) ───────────────────────────────────────────────────

    public sealed class NullA11yBridge : IPlatformA11yBridge
    {
        public A11yPlatform Platform => A11yPlatform.None;
        public bool IsAvailable => false;
        public void Initialize(AccessibilityTree tree, IntPtr hwnd) { }
        public void FireEvent(AccessibilityNode node, A11yEvent eventType) { }
        public void FirePropertyChanged(AccessibilityNode node, string prop, object old, object @new) { }
        public void UpdateWindowHandle(IntPtr hwnd) { }
        public void Dispose() { }
    }

    // ── AccessibilityManager — coordinates tree + bridge ─────────────────────

    /// <summary>
    /// Top-level coordinator: owns the <see cref="AccessibilityTree"/>,
    /// the platform bridge, and drives invalidation → event fire pipeline.
    /// </summary>
    public sealed class AccessibilityManager : IDisposable
    {
        private readonly IPlatformA11yBridge _bridge;
        private AccessibilityTree _tree;
        private Action _treeInvalidationHandler;
        private bool _disposed;

        public IPlatformA11yBridge Bridge => _bridge;
        public AccessibilityTree Tree => _tree;

        public AccessibilityManager(IPlatformA11yBridge bridge = null)
        {
            _bridge = bridge ?? PlatformA11yBridgeFactory.Create();
        }

        /// <summary>Attach to a document's accessibility tree and init the platform bridge.</summary>
        public void Attach(Dom.V2.Document document, IntPtr nativeWindowHandle)
        {
            if (_tree != null && _treeInvalidationHandler != null)
                _tree.TreeInvalidated -= _treeInvalidationHandler;

            _tree = AccessibilityTree.For(document);
            _bridge.Initialize(_tree, nativeWindowHandle);

            _treeInvalidationHandler = () => _bridge.FireEvent(_tree.Root, A11yEvent.StructureChanged);
            _tree.TreeInvalidated += _treeInvalidationHandler;
        }

        /// <summary>Fire a focus-changed event for the given node.</summary>
        public void FocusChanged(AccessibilityNode node)
        {
            _bridge.FireEvent(node, A11yEvent.FocusChanged);
        }

        /// <summary>Fire a value-changed event (e.g. input field updated).</summary>
        public void ValueChanged(AccessibilityNode node, string oldVal, string newVal)
        {
            _bridge.FirePropertyChanged(node, "value", oldVal, newVal);
        }

        /// <summary>Alert: fire an alert event (live region with role=alert).</summary>
        public void Alert(AccessibilityNode node)
        {
            _bridge.FireEvent(node, A11yEvent.AlertFired);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_tree != null && _treeInvalidationHandler != null)
                    _tree.TreeInvalidated -= _treeInvalidationHandler;
                _bridge?.Dispose();
            }
        }
    }
}
