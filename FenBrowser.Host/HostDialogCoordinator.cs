using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Widgets;

namespace FenBrowser.Host;

/// <summary>
/// Host-side implementation of the JS dialog bridge.
/// Posts dialog creation to the UI thread via WindowManager.RunOnMainThread,
/// then blocks the calling thread until the user dismisses the dialog.
/// </summary>
public static class HostDialogCoordinator
{
    private static readonly object _gate = new();

    /// <summary>
    /// Install the dialog bridge into FenEngine so that alert/confirm/prompt
    /// native functions call back into the Host. Must be called once during
    /// Host startup, before any page script runs.
    /// </summary>
    public static void Install()
    {
        JsDialogBridge.ShowDialog = ShowDialog;
        JsDialogBridge.AbortPending = AbortPending;
        JsDialogBridge.OpenWindow = OpenPopupWindow;
        JsDialogBridge.FinalizePopupDocument = FinalizePopupDocument;
        JsDialogBridge.ClosePopupWindow = ClosePopupWindow;
        JsDialogBridge.IsPopupWindowClosed = IsPopupWindowClosed;
    }

    private static object ShowDialog(string type, string message, string defaultValue)
    {
        var dialogType = type switch
        {
            "confirm" => JsDialogType.Confirm,
            "prompt" => JsDialogType.Prompt,
            _ => JsDialogType.Alert
        };

        var resultBox = new ResultBox();
        using (var signal = new ManualResetEventSlim(false))
        {
            // Post dialog creation to the main thread via WindowManager's queue.
            // If that throws (headless, startup race), create the dialog directly.
            try
            {
                WindowManager.Instance.RunOnMainThread(() =>
                {
                    try
                    {
                        ChromeManager.Instance.ShowJavascriptDialog(
                            dialogType, message, defaultValue,
                            r => { resultBox.Value = r; signal.Set(); });
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error(
                            $"[HostDialogCoordinator] ShowDialog failed: {ex.Message}",
                            LogCategory.JavaScript);
                        resultBox.Value = GetDefaultResult(type);
                        signal.Set();
                    }
                });
            }
            catch
            {
                // WindowManager unavailable — fall back immediately
                return GetDefaultResult(type);
            }

            // Poll with Thread.Sleep instead of a blocking Wait().
            // A blocking WaitOne (or Wait) can expose thread-pool starvation
            // if the main-thread queue never gets drained (e.g. during
            // engine-thread stalls).  Polling at ~60 Hz keeps the thread
            // alive and lets the CLR pump finalizers / GC without risk of
            // a permanent hang within a single WaitHandle.
            const int pollIntervalMs = 16;  // ~60 fps
            const int timeoutMs = 30_000;
            int elapsed = 0;
            while (!signal.IsSet && elapsed < timeoutMs)
            {
                Thread.Sleep(pollIntervalMs);
                elapsed += pollIntervalMs;
            }

            if (!signal.IsSet)
            {
                FenLogger.Warn(
                    "[HostDialogCoordinator] Dialog wait timed out after 30s.",
                    LogCategory.JavaScript);
                TryRemoveOverlay();
                return GetDefaultResult(type);
            }
        }

        return resultBox.Value;
    }

    private sealed class ResultBox
    {
        public object Value;
    }

    private static void TryRemoveOverlay()
    {
        try
        {
            WindowManager.Instance.RunOnMainThread(() =>
            {
                ChromeManager.Instance.DismissCurrentDialog();
            });
        }
        catch { /* best effort */ }
    }

    private static object GetDefaultResult(string type)
    {
        return type switch
        {
            "confirm" => true,
            "prompt" => null,
            _ => null // alert
        };
    }

    private static void AbortPending()
    {
        // No static signal to abort — each call has its own local signal.
        // The JsDialogBridge.AbortPending delegate is kept for API compatibility
        // and called during navigation teardown. Since ShowDialog uses a
        // per-call ManualResetEventSlim with a 30s timeout, stale dialogs
        // self-recover.
    }

    // ── window.open() support ──

    private static readonly Dictionary<object, PopupState> _popups = new();
    private static readonly object _popupsLock = new();

    private sealed class PopupState
    {
        public PopupWindow Window;
        public BrowserTab Tab;
        public string Name;
        public string AccumulatedHtml = string.Empty;
    }

    private static object OpenPopupWindow(string url, string name, string features)
    {
        // Parse width/height from features string like "width=460,height=360"
        int width = ParseFeaturePx(features, "width", 460);
        int height = ParseFeaturePx(features, "height", 360);

        PopupState state = null;
        try
        {
            using var signal = new ManualResetEventSlim(false);
            Exception createError = null;
            WindowManager.Instance.RunOnMainThread(() =>
            {
                try
                {
                    if (state != null)
                    {
                        return;
                    }

                    var navigationUrl = NormalizePopupNavigationUrl(url);
                    var tab = TabManager.Instance.CreateTab(navigationUrl);
                    state = new PopupState { Tab = tab, Name = name };
                    lock (_popupsLock) { _popups[state] = state; }
                }
                catch (Exception ex)
                {
                    createError = ex;
                }
                finally
                {
                    signal.Set();
                }
            });

            if (!signal.Wait(TimeSpan.FromMilliseconds(50)) || createError != null)
            {
                // WebDriver element click can invoke window.open() from the JS
                // event path while the main thread is still inside the command.
                // In that case the queued UI action cannot drain before WPT's
                // new-window poll times out. Fall back to direct tab creation so
                // the popup is still represented as a top-level browsing context.
                var navigationUrl = NormalizePopupNavigationUrl(url);
                var tab = TabManager.Instance.CreateTab(navigationUrl);
                state = new PopupState { Tab = tab, Name = name };
                lock (_popupsLock) { _popups[state] = state; }
            }

            if (!string.IsNullOrEmpty(url) &&
                !string.Equals(url, "undefined", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(url, "null", StringComparison.OrdinalIgnoreCase))
            {
                // Keep a host popup fallback for document.write()/close() callers
                // that expect the lightweight PopupWindow object surface.
                var popup = PopupWindowManager.Create(name, width, height);
                state.Window = popup;
                state.AccumulatedHtml = $"<html><body style='font-family:sans-serif;padding:20px;'>Loading {url}...</body></html>";
                popup.SetContent(state.AccumulatedHtml);
            }
        }
        catch (Exception ex)
        {
            FenLogger.Error(
                $"[HostDialogCoordinator] Failed to create popup window: {ex.Message}",
                LogCategory.JavaScript);
            return null;
        }

        return state;
    }

    private static string NormalizePopupNavigationUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            string.Equals(url, "undefined", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(url, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "about:blank";
        }

        return url;
    }

    private static int ParseFeaturePx(string features, string key, int defaultValue)
    {
        if (string.IsNullOrEmpty(features)) return defaultValue;
        var parts = features.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase))
            {
                var eq = trimmed.IndexOf('=');
                var val = trimmed.Substring(eq + 1).Trim();
                if (int.TryParse(val, out var parsed) && parsed > 0)
                    return parsed;
            }
        }
        return defaultValue;
    }

    private static void FinalizePopupDocument(object handle, string html)
    {
        if (handle is not PopupState state) return;
        state.AccumulatedHtml = html;
        if (state.Window != null && !state.Window.IsClosed)
        {
            state.Window.SetContent(html);
        }
        if (state.Tab != null)
        {
            _ = WindowManager.Instance.RunOnMainThread(() =>
            {
                _ = state.Tab.NavigateProgrammaticAsync("data:text/html;charset=utf-8," + Uri.EscapeDataString(html ?? string.Empty));
            });
        }
    }

    private static void ClosePopupWindow(object handle)
    {
        if (handle is not PopupState state) return;
        try { state.Window?.Close(); } catch { }
        try
        {
            WindowManager.Instance.RunOnMainThread(() =>
            {
                var tabs = TabManager.Instance;
                for (var i = 0; i < tabs.Tabs.Count; i++)
                {
                    if (ReferenceEquals(tabs.Tabs[i], state.Tab))
                    {
                        tabs.CloseTab(i);
                        break;
                    }
                }
            });
        }
        catch { }
        lock (_popupsLock) { _popups.Remove(state); }
    }

    private static bool IsPopupWindowClosed(object handle)
    {
        if (handle is not PopupState state)
        {
            return true;
        }

        if (state.Tab != null)
        {
            var tabs = TabManager.Instance.Tabs;
            return !tabs.Any(tab => ReferenceEquals(tab, state.Tab));
        }

        return state.Window == null || state.Window.IsClosed;
    }
}
