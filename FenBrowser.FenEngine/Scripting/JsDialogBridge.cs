using System;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Callback bridge for JavaScript modal dialogs (alert, confirm, prompt).
///
/// FenEngine cannot reference the Host assembly (circular dependency), so the Host
/// registers an implementation of this delegate during startup. BrowserScriptEngineRuntime
/// calls the delegate from native alert/confirm/prompt function bindings.
///
/// Thread model: the delegate is invoked from the FenJS Large-Stack Worker thread.
/// The Host implementation must post work to the UI thread, block the calling thread
/// until the user dismisses the dialog, and return the result.
/// </summary>
public static class JsDialogBridge
{
    /// <summary>
    /// Show a JS modal dialog and return the result synchronously.
    /// Parameters: (type, message, defaultValue) → result
    ///   - type: "alert", "confirm", or "prompt"
    ///   - message: the message text
    ///   - defaultValue: the default value for prompt (empty string for others)
    /// Returns: null for alert/cancel, true/false for confirm, string or null for prompt.
    /// </summary>
    public static Func<string, string, string, object> ShowDialog { get; set; }

    /// <summary>
    /// Abort any pending dialog (e.g. during navigation teardown).
    /// Safe to call from any thread.
    /// </summary>
    public static Action AbortPending { get; set; }

    /// <summary>
    /// Open a new popup window/tab. Called from window.open().
    /// Parameters: (url, name, features) → popup window host object identifier.
    /// The Host creates a new tab and returns an opaque handle that the native
    /// function wraps in a JS host object.
    /// </summary>
    public static Func<string, string, string, object> OpenWindow { get; set; }

    /// <summary>
    /// Load accumulated HTML into a popup window created by OpenWindow.
    /// Called when popup.document.close() is invoked.
    /// Parameters: (popupHandle, html) → void.
    /// </summary>
    public static Action<object, string> FinalizePopupDocument { get; set; }

    /// <summary>
    /// Close a popup window created by OpenWindow.
    /// Parameters: (popupHandle) → void.
    /// </summary>
    public static Action<object> ClosePopupWindow { get; set; }

    /// <summary>
    /// Check whether a popup window is still open.
    /// Parameters: (popupHandle) → bool.
    /// </summary>
    public static Func<object, bool> IsPopupWindowClosed { get; set; }
}
