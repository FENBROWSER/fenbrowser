using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §4.10.5.2.10 selection APIs of text controls: selectionStart,
/// selectionEnd, selectionDirection, setSelectionRange(), select() and
/// setRangeText() on input (text, search, url, tel, password) and textarea.
/// The selection itself lives in <see cref="FormControlSelection"/>, where the
/// keyboard editor publishes its caret and picks up what script sets.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string TextSelectionPrelude = """
        (function () {
            var g = globalThis;
            var getSelection = g.__fenGetTextSelection;
            var setSelection = g.__fenSetTextSelection;
            if (typeof getSelection !== 'function' || typeof setSelection !== 'function') return;

            function selectionOf(el) {
                var s = getSelection(el);
                return s ? { start: s[0], end: s[1], direction: s[2] } : null;
            }

            function requireSelection(el) {
                var s = selectionOf(el);
                if (!s) throw new DOMException("The input element's type does not support selection.", 'InvalidStateError');
                return s;
            }

            // WebIDL unsigned long.
            function offset(v) { var n = Number(v); return isFinite(n) ? (Math.trunc(n) >>> 0) : 0; }

            function define(proto, name, descriptor) {
                descriptor.configurable = true;
                descriptor.enumerable = true;
                Object.defineProperty(proto, name, descriptor);
            }

            function method(fn, length) {
                Object.defineProperty(fn, 'length', { value: length, configurable: true });
                return fn;
            }

            ['HTMLInputElement', 'HTMLTextAreaElement'].forEach(function (name) {
                var ctor = g[name];
                if (typeof ctor !== 'function' || !ctor.prototype) return;
                var proto = ctor.prototype;

                define(proto, 'selectionStart', {
                    get: function selectionStart() { var s = selectionOf(this); return s ? s.start : null; },
                    set: function selectionStart(v) {
                        var s = requireSelection(this), start = offset(v);
                        setSelection(this, start, Math.max(s.end, start), s.direction);
                    }
                });
                define(proto, 'selectionEnd', {
                    get: function selectionEnd() { var s = selectionOf(this); return s ? s.end : null; },
                    set: function selectionEnd(v) {
                        var s = requireSelection(this);
                        setSelection(this, s.start, offset(v), s.direction);
                    }
                });
                define(proto, 'selectionDirection', {
                    get: function selectionDirection() { var s = selectionOf(this); return s ? s.direction : null; },
                    set: function selectionDirection(v) {
                        var s = requireSelection(this);
                        setSelection(this, s.start, s.end, String(v));
                    }
                });
                define(proto, 'setSelectionRange', {
                    writable: true,
                    value: method(function setSelectionRange(start, end, direction) {
                        requireSelection(this);
                        setSelection(this, offset(start), offset(end), direction === undefined ? 'none' : String(direction));
                    }, 2)
                });
                define(proto, 'select', {
                    writable: true,
                    value: method(function select() {
                        if (!selectionOf(this)) return;
                        setSelection(this, 0, String(this.value).length, 'none');
                    }, 0)
                });
                // HTML "setRangeText": replace a range of the value and place the
                // selection by selectMode (select, start, end or preserve).
                define(proto, 'setRangeText', {
                    writable: true,
                    value: method(function setRangeText(replacement, start, end, selectMode) {
                        if (arguments.length < 1) throw new TypeError("Failed to execute 'setRangeText': 1 argument required, but only 0 present.");
                        var s = requireSelection(this);
                        replacement = String(replacement);
                        if (arguments.length < 3) { start = s.start; end = s.end; }
                        else { start = offset(start); end = offset(end); }
                        if (start > end) throw new DOMException('The provided start value is larger than the end value.', 'IndexSizeError');
                        var value = String(this.value), length = value.length;
                        if (start > length) start = length;
                        if (end > length) end = length;
                        var selStart = s.start, selEnd = s.end;
                        this.value = value.slice(0, start) + replacement + value.slice(end);
                        var newEnd = start + replacement.length;
                        var mode = selectMode === undefined ? 'preserve' : String(selectMode);
                        if (mode === 'select') { selStart = start; selEnd = newEnd; }
                        else if (mode === 'start') { selStart = selEnd = start; }
                        else if (mode === 'end') { selStart = selEnd = newEnd; }
                        else {
                            var delta = replacement.length - (end - start);
                            if (selStart > end) selStart += delta; else if (selStart > start) selStart = start;
                            if (selEnd > end) selEnd += delta; else if (selEnd > start) selEnd = newEnd;
                        }
                        setSelection(this, selStart, selEnd, 'none');
                    }, 1)
                });
            });
        })();
        """;

    private void InstallFenJsTextSelection()
    {
        try
        {
            _interpreter.RegisterGlobalValue(
                "__fenGetTextSelection",
                _interpreter.AllocateNativeFunction(
                    "__fenGetTextSelection",
                    (_, args) =>
                    {
                        if (args.Count == 0 || ResolveHostObjectOrNull(args[0]) is not Element element ||
                            !FormControlSelection.Applies(element))
                        {
                            return JsValue.Null;
                        }

                        var (start, end, direction) = FormControlSelection.Get(element);
                        return _interpreter.AllocateArray(new[]
                        {
                            JsValue.FromInt32(start), JsValue.FromInt32(end), JsValue.FromString(direction)
                        });
                    },
                    length: 1));
            _interpreter.RegisterGlobalValue(
                "__fenSetTextSelection",
                _interpreter.AllocateNativeFunction(
                    "__fenSetTextSelection",
                    (_, args) =>
                    {
                        if (args.Count < 3 || ResolveHostObjectOrNull(args[0]) is not Element element ||
                            !FormControlSelection.Applies(element))
                        {
                            return JsValue.Undefined;
                        }

                        FormControlSelection.Set(
                            element,
                            (int)Math.Min(int.MaxValue, CoerceToFiniteNumber(args[1], 0)),
                            (int)Math.Min(int.MaxValue, CoerceToFiniteNumber(args[2], 0)),
                            args.Count > 3 ? CoerceToHostString(args[3]) : "none",
                            fromScript: true);
                        return JsValue.Undefined;
                    },
                    length: 4));
            EvaluateWithFenJsRaw(TextSelectionPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] text selection prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }
}
