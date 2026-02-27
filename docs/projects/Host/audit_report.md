# FenBrowser.Host Project - Technical Audit Report

**Date:** 2026-02-27
**Target:** `FenBrowser.Host` Project (UI, Core Flow, Input, Process Isolation)

## 1. Executive Summary

The `FenBrowser.Host` project successfully establishes a modular, decoupled architecture where the UI retained-mode widget tree, process isolation for renderers, and windowing abstractions are clearly separated. The codebase adheres well to the project's zero-dependency philosophy (outside of primitive drawing via SkiaSharp and windowing via Silk.NET), avoiding large monolithic UI frameworks.

Overall code quality is high, with excellent adherence to native resource ownership (e.g., proactive `using` blocks for Skia objects). However, several areas present opportunities for performance optimization, UI state synchronization, and feature completion.

---

## 2. Core Architecture & Windowing

### Findings

- **Window Manager & Compositor:** The `WindowManager` correctly abstracts Silk.NET, while `Compositor` manages the double-buffered SKSurface pipeline.
- **Dirty Rect Invalidation:** The widget system uses bounding-box invalidation (`Invalidate(SKRect bounds)`). The compositor propagates these damage rects to optimize rendering.

### Areas for Improvement

- **Compositor VSync / Frame Pacing:** Ensure the compositor loop is tightly bound to monitor refresh rates. Currently, it relies on Silk.NET's Render event, but explicit frame dropping or pacing might be needed under heavy load.
- **Widget Hit-Testing:** `Widget.HitTestDeep` iterates through children recursively. Currently, it operates in reverse order (topmost first) which is correct. For very complex UIs, a spatial partitioning approach isn't needed yet, but caching absolute bounds rather than computing them repeatedly during mouse movement could reduce CPU load.

---

## 3. UI Components (Widgets)

### Findings

- The custom retained-mode UI system (`Widget.cs` base class) is clean and flexible.
- `AddressBarWidget` has comprehensive keyboard and text interaction logic, including text selection, clipboard support, and a blinking caret.
- `TabBarWidget` supports overflow scrolling and rudimentary drag-and-drop state tracking.

### Actionable Improvements

1.  **AddressBarWidget Performance Issues:**
    - **Text Layout Thrashing:** In `AddressBarWidget.cs`, `_isTextLayoutDirty = true` and `EnsureTextBlock()` are called synchronously on almost every keypress and deletion. For long URLs, recreating the `TextBlock` object frequently is computationally expensive.
    - **Fix:** Decouple the logical caret movement from full text layout invalidation. Only invalidate the layout when the actual text string changes, not when the selection or caret moves.
2.  **Navigation Security UI:**
    - The shield icon in `AddressBarWidget` uses a naive check (`if (_text.StartsWith("https://"))`).
    - **Fix:** Bind the `CurrentSecurity` enum state directly to the SSL/TLS verification results from the `BrowserIntegration` network stack, rather than relying on the URL string.
3.  **TabBarWidget Event Wiring:**
    - The `TabBarWidget` implements `BeginTabDrag`, `UpdateTabDrag`, and `EndTabDrag`, but delegates mouse event handling to its base class (`OnMouseDown` is practically empty). If child tabs capture mouse events, there needs to be a mechanism to bubble drag events up to the `TabBarWidget` to properly handle visual reordering.

---

## 4. Process Isolation & Tab Management

### Findings

- `BrokeredProcessIsolationCoordinator` correctly manages the lifecycle of out-of-process (OOP) renderers using `System.Diagnostics.Process` and Named Pipes (`RendererChildSession`).
- Crash handling is gracefully managed. When a child crashes, `TabManager` is notified, and `BrowserTab` renders a "Sad Face" crash screen with the error reason.

### Actionable Improvements

1.  **Zombie Process Cleanup:** While `StopSession` calls `process.Kill(entireProcessTree: true)`, there should be a guaranteed cleanup hook (e.g., using Windows Job Objects via P/Invoke) to ensure child processes are terminated if the Host process crashes ungracefully.
2.  **IPC Serialization Overhead:** `RendererIpc` uses `System.Text.Json` to serialize/deserialize envelopes. In high-frequency paths (like `FrameRequest` and mouse `InputEvent`), JSON serialization causes string allocation overhead.
    - **Fix:** Migrate high-frequency IPC messages to a binary format (e.g., protobuf or MemoryPack) or use custom zero-allocation byte block writers over the pipe.

---

## 5. Supporting Integrations

### Theme System

- **Finding:** `ThemeManager` allows toggling Dark/Light modes cleanly.
- **Gap:** Toggling the theme using `ThemeManager.ToggleTheme()` changes the static properties but does _not_ broadcast an event. Resultingly, active widgets do not automatically repaint to reflect the new theme.
- **Fix:** Add a `ThemeChanged` event to `ThemeManager`. Have `RootWidget` subscribe to this event and call `InvalidateLayout()` and `Invalidate()` globally when it fires.

### WebDriver Integration

- **Finding:** `HostBrowserDriver` efficiently maps W3C commands to the UI thread using `WindowManager.Instance.RunOnMainThread`.
- **Gap:** The `SwitchToWindowAsync(windowHandle)` method is a no-op (comment: "Session-level handle switching is managed by WebDriver session state"). While protocol state manages the focus, physical UI tab switching might be expected by visually inspecting tests.
- **Fix:** Correlate WebDriver window handles to `BrowserTab.Id` and wire `SwitchToWindowAsync` to `TabManager.Instance.SwitchToTab()`.

---

## 6. Recommendations & Next Steps

We recommend addressing the findings in the following priority order to harden the browser for production use:

1.  **High Priority:** Optimize `AddressBarWidget` text layout caching to prevent typing latency.
2.  **High Priority:** Implement Windows Job Objects for infallible `BrokeredProcessIsolation` renderer cleanup.
3.  **Medium Priority:** Add `ThemeChanged` event to `ThemeManager` for real-time UI updates.
4.  **Medium Priority:** Refactor high-intensity IPC channels (Input/Frames) away from JSON to reduce GC pressure.
5.  **Low Priority:** Implement `SecurityState` binding to actual network layer certificate validation.
