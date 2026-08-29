using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Widgets;

namespace FenBrowser.Host;

/// <summary>
/// Host-side implementation of the JS dialog bridge.
/// Posts dialog creation to the UI thread via WindowManager.RunOnMainThread,
/// then blocks the calling FenJS worker until the user dismisses the dialog.
/// </summary>
public static class HostDialogCoordinator
{
    private static readonly object _gate = new();
    private static readonly HashSet<PendingDialog> _pendingDialogs = new();

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

        using var pending = new PendingDialog(type);
        lock (_gate)
        {
            pending.IsRegistered = true;
            _pendingDialogs.Add(pending);
        }

        try
        {
            try
            {
                WindowManager.Instance.RunOnMainThread(() =>
                {
                    try
                    {
                        ChromeManager.Instance.ShowJavascriptDialog(
                            dialogType,
                            message,
                            defaultValue,
                            result => CompletePending(pending, result));
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error(
                            $"[HostDialogCoordinator] ShowDialog failed: {ex.Message}",
                            LogCategory.JavaScript);
                        CompletePending(pending, GetDefaultResult(type));
                    }
                });
            }
            catch
            {
                return GetDefaultResult(type);
            }

            if (!pending.Signal.Wait(TimeSpan.FromSeconds(30)))
            {
                FenLogger.Warn(
                    "[HostDialogCoordinator] Dialog wait timed out after 30s.",
                    LogCategory.JavaScript);
                TryRemoveOverlay();
                return GetDefaultResult(type);
            }

            return pending.Value;
        }
        finally
        {
            lock (_gate)
            {
                pending.IsRegistered = false;
                _pendingDialogs.Remove(pending);
            }
        }
    }

    private static void CompletePending(PendingDialog pending, object value)
    {
        lock (_gate)
        {
            if (!pending.IsRegistered || pending.IsCompleted)
            {
                return;
            }

            pending.Value = value;
            pending.IsCompleted = true;
            pending.Signal.Set();
        }
    }

    private sealed class PendingDialog : IDisposable
    {
        public PendingDialog(string type)
        {
            Type = type;
            Value = GetDefaultResult(type);
            Signal = new ManualResetEventSlim(false);
        }

        public string Type { get; }
        public ManualResetEventSlim Signal { get; }
        public object Value { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsRegistered { get; set; }

        public void Dispose() => Signal.Dispose();
    }

    private static void TryRemoveOverlay()
    {
        try
        {
            WindowManager.Instance.RunOnMainThread(() => ChromeManager.Instance.DismissCurrentDialog());
        }
        catch
        {
        }
    }

    private static object GetDefaultResult(string type)
    {
        return type switch
        {
            "confirm" => false,
            "prompt" => null,
            _ => null
        };
    }

    private static void AbortPending()
    {
        PendingDialog[] pending;
        lock (_gate)
        {
            pending = _pendingDialogs.ToArray();
            foreach (var dialog in pending)
            {
                if (!dialog.IsRegistered || dialog.IsCompleted)
                {
                    continue;
                }

                dialog.Value = GetDefaultResult(dialog.Type);
                dialog.IsCompleted = true;
                dialog.Signal.Set();
            }
        }

        if (pending.Length > 0)
        {
            TryRemoveOverlay();
        }
    }

    // ── window.open() support ──

    private static readonly Dictionary<object, PopupState> _popups = new();
    private static readonly object _popupsLock = new();

    private sealed class PopupState
    {
        public readonly object Sync = new();
        public PopupWindow Window;
        public string Name;
        public string NavigationUrl;
        public string AccumulatedHtml = string.Empty;
        public bool HasFinalizedDocument;
        public bool Closed;
    }

    private static object OpenPopupWindow(string url, string name, string features)
    {
        var width = ParseFeaturePx(features, "width", 460);
        var height = ParseFeaturePx(features, "height", 360);
        var state = new PopupState
        {
            Name = name,
            NavigationUrl = NormalizePopupNavigationUrl(url)
        };

        lock (_popupsLock)
        {
            _popups[state] = state;
        }

        try
        {
            var createTask = WindowManager.Instance.RunOnMainThread(() =>
                CreatePopupOnMainThread(state, width, height));
            ObservePopupUiTask(createTask, state, "create popup", closeOnFailure: true);
            return state;
        }
        catch (Exception ex)
        {
            MarkPopupClosed(state);
            FenLogger.Error(
                $"[HostDialogCoordinator] Failed to queue popup creation: {ex.Message}",
                LogCategory.JavaScript);
            return null;
        }
    }

    private static void CreatePopupOnMainThread(PopupState state, int width, int height)
    {
        lock (state.Sync)
        {
            if (state.Closed)
            {
                return;
            }

            // The popup window is the only surface for window.open(). The previous
            // flow also created a main-window tab per popup, duplicating the load
            // and stealing active-tab focus; document.write popups surfaced only as
            // such a tab and never got a window at all.
            var popup = PopupWindowManager.Create(state.Name, width, height);
            state.Window = popup;

            if (state.HasFinalizedDocument)
            {
                popup.SetContent(state.AccumulatedHtml);
            }
            else if (!string.Equals(state.NavigationUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                popup.SetContent(
                    $"<html><body style='font-family:sans-serif;padding:20px;'>Loading {state.NavigationUrl}...</body></html>");

                // The engine-side popup context only finalizes through
                // document.write(); a plain URL popup would otherwise sit on the
                // placeholder forever, success or failure. Load the URL into the
                // popup window here so it shows the page or the standard error
                // page (e.g. unresolvable hosts).
                _ = LoadPopupUrlContentAsync(state);
            }
        }
    }

    private static async Task LoadPopupUrlContentAsync(PopupState state)
    {
        try
        {
            // Popups no longer own a tab; share the opener session's resources so
            // cookies and caches match the browsing context that opened them.
            var resources = TabManager.Instance.ActiveTab?.Browser?.Host?.ResourceManager;
            if (resources == null)
            {
                return;
            }

            var navigation = new FenBrowser.FenEngine.Rendering.NavigationManager(resources);
            var result = await navigation.NavigateAsync(state.NavigationUrl, FenBrowser.FenEngine.Rendering.NavigationRequestKind.Programmatic)
                .ConfigureAwait(false);

            var uiTask = WindowManager.Instance.RunOnMainThread(() =>
            {
                PopupWindow window;
                lock (state.Sync)
                {
                    if (state.Closed || state.HasFinalizedDocument)
                    {
                        return;
                    }
                    window = state.Window;
                }

                if (window == null || window.IsClosed)
                {
                    return;
                }

                if (result.Status == FetchStatus.Success)
                {
                    window.SetContent(
                        result.Content ?? string.Empty,
                        cssUri => resources.FetchCssAsync(cssUri),
                        imageUri => resources.FetchImageAsync(imageUri),
                        result.FinalUri);
                }
                else
                {
                    window.SetContent(RenderPopupErrorHtml(state.NavigationUrl, result));
                }
            });
            ObservePopupUiTask(uiTask, state, "load popup url", closeOnFailure: false);
        }
        catch (Exception ex)
        {
            FenLogger.Error(
                $"[HostDialogCoordinator] Popup URL load failed for '{state.NavigationUrl}': {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    internal static string RenderPopupErrorHtml(string url, FetchResult result)
    {
        // Mirror the tab navigation error pages; a 4xx response that carried an
        // HTML body (challenge pages, access denied) renders that body instead.
        if (result.FailureReason == FetchFailureReasonCode.HttpError &&
            result.StatusCode >= 400 && result.StatusCode < 500 &&
            !string.IsNullOrWhiteSpace(result.Content) &&
            (result.ContentType ?? string.Empty).StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return result.Content;
        }

        return result.Status switch
        {
            FetchStatus.ConnectionFailed => FenBrowser.FenEngine.Rendering.ErrorPageRenderer.RenderConnectionFailed(url, result.ErrorDetail),
            FetchStatus.SslError => FenBrowser.FenEngine.Rendering.ErrorPageRenderer.RenderSslError(url, result.ErrorDetail, result.Certificate),
            FetchStatus.Timeout => FenBrowser.FenEngine.Rendering.ErrorPageRenderer.RenderGenericError(url, "Connection Timed Out", "The server took too long to respond.", result.ErrorDetail),
            FetchStatus.NotFound => FenBrowser.FenEngine.Rendering.ErrorPageRenderer.RenderGenericError(url, "404 Not Found", "The page you requested could not be found.", result.ErrorDetail),
            _ => FenBrowser.FenEngine.Rendering.ErrorPageRenderer.RenderGenericError(url, "Error", "Something went wrong.", result.ErrorDetail)
        };
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
        if (string.IsNullOrEmpty(features))
        {
            return defaultValue;
        }

        var parts = features.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            var value = trimmed.Substring(eq + 1).Trim();
            if (int.TryParse(value, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static void FinalizePopupDocument(object handle, string html)
    {
        if (handle is not PopupState state)
        {
            return;
        }

        lock (state.Sync)
        {
            if (state.Closed)
            {
                return;
            }
            state.AccumulatedHtml = html ?? string.Empty;
            state.HasFinalizedDocument = true;
        }

        try
        {
            var task = WindowManager.Instance.RunOnMainThread(() =>
            {
                PopupWindow window;
                string content;
                lock (state.Sync)
                {
                    if (state.Closed)
                    {
                        return;
                    }

                    window = state.Window;
                    content = state.AccumulatedHtml;
                }

                // Rendering and navigation can be expensive/re-entrant. Do not hold
                // PopupState.Sync across either operation or JS close()/native close
                // can block behind unrelated rendering work.
                window?.SetContent(content);
            });
            ObservePopupUiTask(task, state, "finalize popup document", closeOnFailure: false);
        }
        catch (Exception ex)
        {
            FenLogger.Error(
                $"[HostDialogCoordinator] Failed to queue popup document update: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    private static void ClosePopupWindow(object handle)
    {
        if (handle is not PopupState state)
        {
            return;
        }

        lock (state.Sync)
        {
            if (state.Closed)
            {
                return;
            }
            state.Closed = true;
        }

        RemovePopupState(state);

        try
        {
            var task = WindowManager.Instance.RunOnMainThread(() =>
            {
                try
                {
                    state.Window?.Close();
                }
                catch (Exception ex)
                {
                    FenLogger.Warn(
                        $"[HostDialogCoordinator] Native popup close failed: {ex.Message}",
                        LogCategory.JavaScript);
                }
            });
            ObservePopupUiTask(task, state, "close popup", closeOnFailure: false);
        }
        catch (Exception ex)
        {
            FenLogger.Warn(
                $"[HostDialogCoordinator] Failed to queue popup close: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    private static bool IsPopupWindowClosed(object handle)
    {
        if (handle is not PopupState state)
        {
            return true;
        }

        PopupWindow window;
        lock (state.Sync)
        {
            if (state.Closed)
            {
                return true;
            }

            window = state.Window;
        }

        // Native user-close must immediately propagate to WindowProxy.closed and
        // release the coordinator's strong PopupState reference. Previously only
        // JavaScript close() changed state.Closed, so manually closed windows could
        // remain logically open and retained indefinitely.
        if (window?.IsClosed == true)
        {
            MarkPopupClosed(state);
            return true;
        }

        // UI creation is queued but not yet completed; the native close check
        // above covers every later lifetime transition.
        return false;
    }

    private static void ObservePopupUiTask(
        Task task,
        PopupState state,
        string operation,
        bool closeOnFailure)
    {
        if (task == null)
        {
            return;
        }

        _ = task.ContinueWith(
            completed =>
            {
                var ex = completed.Exception?.GetBaseException();
                FenLogger.Error(
                    $"[HostDialogCoordinator] Failed to {operation}: {ex?.Message ?? "unknown error"}",
                    LogCategory.JavaScript);
                if (closeOnFailure)
                {
                    MarkPopupClosed(state);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void MarkPopupClosed(PopupState state)
    {
        lock (state.Sync)
        {
            state.Closed = true;
        }

        RemovePopupState(state);
    }

    private static void RemovePopupState(PopupState state)
    {
        lock (_popupsLock)
        {
            _popups.Remove(state);
        }
    }
}
