using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §8.1.4.6 "report an exception": an exception nothing caught - thrown by a
/// classic script, a timer, an event listener, a microtask or a media task - becomes
/// an <c>ErrorEvent</c> fired at the window, so <c>onerror</c> and every
/// <c>error</c> listener see it, and reaches the console only when no handler
/// cancelled it. Before this the engine logged such exceptions and nothing on the
/// page heard about them.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string ErrorReportingPrelude = """
        (function () {
            var g = globalThis;
            var reporting = false;

            function describe(error) {
                try {
                    if (error !== null && typeof error === 'object' && 'message' in error) {
                        var name = error.name !== undefined ? String(error.name) : 'Error';
                        return name + ': ' + String(error.message);
                    }
                    return String(error);
                } catch (_describeFailure) {
                    return 'exception';
                }
            }

            // filename/lineno/colno are known for an error the host found itself
            // (a script that did not parse); otherwise they are left at their defaults.
            function report(error, filename, lineno, colno) {
                var message = 'Uncaught ' + describe(error);
                var handled = false;
                // An error handler that throws is not reported again: the second
                // exception goes to the console instead of looping.
                if (!reporting && typeof g.ErrorEvent === 'function' && typeof g.dispatchEvent === 'function') {
                    reporting = true;
                    try {
                        var event = new g.ErrorEvent('error', {
                            message: message,
                            error: error,
                            filename: filename !== undefined ? filename : '',
                            lineno: lineno || 0,
                            colno: colno || 0,
                            cancelable: true
                        });
                        handled = g.dispatchEvent(event) === false;
                    } catch (_dispatchFailure) {
                    } finally {
                        reporting = false;
                    }
                }
                if (!handled && g.console && typeof g.console.error === 'function') {
                    try { g.console.error(message); } catch (_consoleFailure) {}
                }
            }

            // HTML §8.1.8.1 "event handler processing algorithm", special error event
            // handling: the window's onerror is called with (message, source, lineno,
            // colno, error) for an ErrorEvent, and returning true cancels the event.
            function invokeWindowOnError(event) {
                var handler = g.onerror;
                if (typeof handler !== 'function') return;
                if (typeof g.ErrorEvent === 'function' && event instanceof g.ErrorEvent) {
                    var result = handler.call(g, event.message, event.filename, event.lineno, event.colno, event.error);
                    if (result === true && typeof event.preventDefault === 'function') event.preventDefault();
                    return;
                }
                handler.call(g, event);
            }

            Object.defineProperty(g, '__fenReportException', { value: report, writable: true, configurable: true, enumerable: false });
            Object.defineProperty(g, '__fenWindowOnError', { value: invokeWindowOnError, writable: true, configurable: true, enumerable: false });

            // HTML §8.1.4.7 reportError(e): the same algorithm, asked for by script.
            g.reportError = function reportError(e) {
                if (arguments.length < 1) throw new TypeError("Failed to execute 'reportError' on 'Window': 1 argument required, but only 0 present.");
                report(e);
            };
        })();
        """;

    private bool _reportingFenJsException;

    private void InstallFenJsErrorReporting()
    {
        try
        {
            EvaluateWithFenJsRaw(ErrorReportingPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] error reporting prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    /// <summary>
    /// Reports an exception that escaped script to the window. An abort the script is
    /// not allowed to observe (an exhausted budget, an interrupt) is not a page error
    /// and is left to the caller's logging.
    /// </summary>
    private void ReportFenJsException(Exception exception)
    {
        // HTML 8.1.4.2 "create a classic script": a script that does not parse gets
        // a SyntaxError, created in its realm, as its error to rethrow, and running
        // it reports that error like any other uncaught exception.
        if (exception is JsParserException parseError)
        {
            // The position is the script's own; an inline script starts on its
            // element's line in the document, and its URL is the document's.
            var record = GetCurrentScriptRecord();
            var external = !string.IsNullOrEmpty(record?.ResolvedUrl) || !string.IsNullOrEmpty(record?.Src);
            var line = parseError.Line;
            if (!external && line > 0 && record?.SourceLine > 0)
            {
                line += record.SourceLine - 1;
            }

            var filename = external
                ? (string.IsNullOrEmpty(record.ResolvedUrl) ? record.Src : record.ResolvedUrl)
                : _currentBaseUri?.AbsoluteUri;
            ReportFenJsValue(
                () => _interpreter.InvokeFunction(
                    ReadGlobalValueOrUndefined("SyntaxError"),
                    new[] { JsValue.FromString(parseError.Message) },
                    JsValue.Undefined),
                filename == null ? JsValue.Undefined : JsValue.FromString(filename),
                line,
                parseError.Column);
            return;
        }

        if (exception is not JsThrownException thrown || IsFenJsInputEventTimeout(thrown))
        {
            return;
        }

        ReportFenJsValue(() => thrown.Value, JsValue.Undefined, 0, 0);
    }

    private void ReportFenJsValue(Func<JsValue> errorValue, JsValue filename, int line, int column)
    {

        if (_reportingFenJsException || _interpreter == null || _realmAbandoned)
        {
            return;
        }

        _reportingFenJsException = true;
        try
        {
            RunFenJsWithLargeStack<object>(() =>
            {
                using (ScriptEngineLockProbe.Hold(_fenJsLock))
                {
                    var report = ReadGlobalValueOrUndefined("__fenReportException");
                    if (_interpreter.CanCallValue(report))
                    {
                        using var reportPin = PinFenJsValues(report);
                        var error = errorValue();
                        using var errorPin = PinFenJsValues(error);
                        _ = _interpreter.InvokeFunction(
                            report,
                            new[] { error, filename, JsValue.FromNumber(line), JsValue.FromNumber(column) },
                            JsValue.Undefined);
                    }
                }

                return null;
            });
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] reporting an exception failed: {ex.Message}", LogCategory.JavaScript);
        }
        finally
        {
            _reportingFenJsException = false;
        }
    }

    /// <summary>
    /// The window's <c>on*</c> handler for one event. <c>onerror</c> goes through the
    /// special error event handling; every other handler is called with the event.
    /// </summary>
    private void InvokeWindowEventHandler(JsValue handler, JsValue windowTarget, JsValue eventValue, string type)
    {
        if (string.Equals(type, "error", StringComparison.Ordinal))
        {
            var special = ReadGlobalValueOrUndefined("__fenWindowOnError");
            if (_interpreter.CanCallValue(special))
            {
                TryInvokeFenJsEventCallback(special, windowTarget, eventValue, type);
                return;
            }
        }

        TryInvokeFenJsEventCallback(handler, windowTarget, eventValue, type);
    }
}
