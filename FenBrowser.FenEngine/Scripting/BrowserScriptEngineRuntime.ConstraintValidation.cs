using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §4.10.20 constraint validation: <c>validity</c> (a live
/// <c>ValidityState</c>), <c>willValidate</c>, <c>validationMessage</c>,
/// <c>checkValidity()</c>, <c>reportValidity()</c> and <c>setCustomValidity()</c>
/// on the listed form controls, and <c>checkValidity()</c>/<c>reportValidity()</c>
/// on forms. The names were published on the prototypes with nothing behind
/// them, so <c>input.validity.valid</c> threw - x.com's login form reads it
/// when Continue is pressed.
/// </summary>
/// <remarks>
/// Everything is computed from the element's own DOM state (value, type,
/// attributes, checkedness), so it is written in script, where a pattern
/// attribute is compiled by the engine's own RegExp as the specification asks.
/// A value counts as user-edited for tooLong/tooShort when it differs from the
/// default value, which is how the dirty value flag shows from outside.
/// reportValidity() reports through the invalid event only; there is no
/// validation bubble UI.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string ConstraintValidationPrelude = """
        (function () {
            var g = globalThis;
            if (typeof g.HTMLInputElement !== 'function') return;

            var customMessages = new WeakMap();
            var states = new WeakMap();
            var stateOwners = new WeakMap();

            var FLAGS = ['valueMissing', 'typeMismatch', 'patternMismatch', 'tooLong', 'tooShort',
                'rangeUnderflow', 'rangeOverflow', 'stepMismatch', 'badInput', 'customError'];
            var MESSAGES = {
                valueMissing: 'Please fill out this field.',
                typeMismatch: 'Please enter a valid value.',
                patternMismatch: 'Please match the requested format.',
                tooLong: 'Please shorten this text.',
                tooShort: 'Please lengthen this text.',
                rangeUnderflow: 'Value must be greater than or equal to the minimum.',
                rangeOverflow: 'Value must be less than or equal to the maximum.',
                stepMismatch: 'Please enter a valid value.',
                badInput: 'Please enter a valid value.'
            };
            var TEXT_TYPES = { text: 1, search: 1, url: 1, tel: 1, email: 1, password: 1 };
            var REQUIRED_TYPES = { text: 1, search: 1, url: 1, tel: 1, email: 1, password: 1, date: 1,
                month: 1, week: 1, time: 1, 'datetime-local': 1, number: 1, checkbox: 1, radio: 1, file: 1 };
            var READONLY_TYPES = { text: 1, search: 1, url: 1, tel: 1, email: 1, password: 1, date: 1,
                month: 1, week: 1, time: 1, 'datetime-local': 1, number: 1 };
            // HTML §4.10.5.1.5: a valid e-mail address.
            var EMAIL = /^[a-zA-Z0-9.!#$%&'*+\/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/;

            function tagOf(el) { return String(el.localName || el.tagName || '').toLowerCase(); }
            function typeOf(el) { return tagOf(el) === 'input' ? String(el.type || 'text').toLowerCase() : ''; }
            function valueOf(el) { var v = el.value; return v == null ? '' : String(v); }

            // HTML §4.10.18.5: disabled by its own attribute or by a disabled
            // fieldset ancestor, unless inside that fieldset's first legend.
            function isDisabled(el) {
                if (el.hasAttribute('disabled')) return true;
                var child = el;
                for (var p = el.parentElement; p; child = p, p = p.parentElement) {
                    if (tagOf(p) === 'fieldset' && p.hasAttribute('disabled')) {
                        var legend = null;
                        for (var c = p.firstElementChild; c; c = c.nextElementSibling) {
                            if (tagOf(c) === 'legend') { legend = c; break; }
                        }
                        if (legend !== child) return true;
                    }
                }
                return false;
            }

            // HTML §4.10.20.1: a submittable element is a candidate for constraint
            // validation unless something bars it.
            function willValidate(el) {
                var tag = tagOf(el);
                if (tag === 'input') {
                    var type = typeOf(el);
                    if (type === 'hidden' || type === 'reset' || type === 'button') return false;
                    if (READONLY_TYPES[type] && el.hasAttribute('readonly')) return false;
                } else if (tag === 'textarea') {
                    if (el.hasAttribute('readonly')) return false;
                } else if (tag === 'button') {
                    var buttonType = String(el.getAttribute('type') || 'submit').toLowerCase();
                    if (buttonType === 'reset' || buttonType === 'button') return false;
                } else if (tag !== 'select') {
                    return false;
                }
                if (isDisabled(el)) return false;
                for (var p = el.parentElement; p; p = p.parentElement) {
                    if (tagOf(p) === 'datalist') return false;
                }
                return true;
            }

            function radioGroup(el) {
                var name = el.getAttribute('name');
                if (!name) return [el];
                var scope = el.form || el.getRootNode();
                var candidates = scope && scope.querySelectorAll ? scope.querySelectorAll('input') : [el];
                var group = [];
                for (var i = 0; i < candidates.length; i++) {
                    var other = candidates[i];
                    if (typeOf(other) === 'radio' && other.getAttribute('name') === name && (other.form || null) === (el.form || null)) {
                        group.push(other);
                    }
                }
                return group.length ? group : [el];
            }

            function valueMissing(el) {
                var tag = tagOf(el);
                if (tag === 'textarea') return el.hasAttribute('required') && valueOf(el) === '';
                if (tag === 'select') {
                    if (!el.hasAttribute('required')) return false;
                    var index = el.selectedIndex;
                    if (index < 0) return true;
                    // The placeholder label option: the first option, empty value,
                    // of a single-selection drop-down.
                    var options = el.options;
                    return !el.multiple && (el.size || 1) === 1 && index === 0 && options && options.length &&
                        String(options[0].value) === '' && tagOf(options[0].parentElement) === 'select';
                }
                if (tag !== 'input') return false;
                var type = typeOf(el);
                if (type === 'radio') {
                    var group = radioGroup(el), required = false, checked = false;
                    for (var i = 0; i < group.length; i++) {
                        if (group[i].hasAttribute('required')) required = true;
                        if (group[i].checked) checked = true;
                    }
                    return required && !checked;
                }
                if (!el.hasAttribute('required') || !REQUIRED_TYPES[type]) return false;
                if (type === 'checkbox') return !el.checked;
                if (type === 'file') return !el.files || el.files.length === 0;
                return valueOf(el) === '';
            }

            function valuesOf(el) {
                var value = valueOf(el);
                if (typeOf(el) === 'email' && el.hasAttribute('multiple')) {
                    return value.split(',').map(function (part) { return part.trim(); });
                }
                return [value];
            }

            function typeMismatch(el) {
                var type = typeOf(el), value = valueOf(el);
                if (value === '') return false;
                if (type === 'email') {
                    return valuesOf(el).some(function (part) { return !EMAIL.test(part); });
                }
                if (type === 'url') {
                    if (typeof g.URL !== 'function') return false;
                    try { new g.URL(value); return false; } catch (_urlError) { return true; }
                }
                return false;
            }

            // HTML §4.10.5.3.6: the pattern is compiled with the v flag and must
            // match the whole value; a pattern that does not compile is ignored.
            function patternMismatch(el) {
                if (!TEXT_TYPES[typeOf(el)] || !el.hasAttribute('pattern')) return false;
                if (valueOf(el) === '') return false;
                var regexp;
                var source = '^(?:' + el.getAttribute('pattern') + ')$';
                try { regexp = new RegExp(source, 'v'); }
                catch (_vError) {
                    try { regexp = new RegExp(source, 'u'); } catch (_uError) { return false; }
                }
                return valuesOf(el).some(function (part) { return !regexp.test(part); });
            }

            function lengthLimited(el) {
                var tag = tagOf(el);
                return tag === 'textarea' || (tag === 'input' && TEXT_TYPES[typeOf(el)]);
            }

            function userEdited(el) {
                return valueOf(el) !== String(el.defaultValue == null ? '' : el.defaultValue);
            }

            function limit(el, name) {
                if (!el.hasAttribute(name)) return -1;
                var n = parseInt(el.getAttribute(name), 10);
                return isNaN(n) || n < 0 ? -1 : n;
            }

            function tooLong(el) {
                var max = limit(el, 'maxlength');
                return lengthLimited(el) && max >= 0 && userEdited(el) && valueOf(el).length > max;
            }

            function tooShort(el) {
                var min = limit(el, 'minlength');
                var length = valueOf(el).length;
                return lengthLimited(el) && min >= 0 && userEdited(el) && length > 0 && length < min;
            }

            function numeric(el) {
                var type = typeOf(el);
                if (type !== 'number' && type !== 'range') return null;
                var value = valueOf(el);
                if (value === '') return null;
                var n = Number(value);
                return isFinite(n) ? n : null;
            }

            function numberAttribute(el, name) {
                if (!el.hasAttribute(name)) return null;
                var n = Number(el.getAttribute(name));
                return el.getAttribute(name) !== '' && isFinite(n) ? n : null;
            }

            function rangeUnderflow(el) {
                var n = numeric(el), min = numberAttribute(el, 'min');
                return n !== null && min !== null && n < min;
            }

            function rangeOverflow(el) {
                var n = numeric(el), max = numberAttribute(el, 'max');
                return n !== null && max !== null && n > max;
            }

            // HTML §4.10.5.1.12/13: step defaults to 1 and is based on min, else
            // on the value attribute, else 0.
            function stepMismatch(el) {
                var n = numeric(el);
                if (n === null) return false;
                var stepText = String(el.getAttribute('step') || '').toLowerCase();
                if (stepText === 'any') return false;
                var step = Number(stepText);
                if (stepText === '' || !isFinite(step) || step <= 0) step = 1;
                var base = numberAttribute(el, 'min');
                if (base === null) base = numberAttribute(el, 'value');
                if (base === null) base = 0;
                var steps = (n - base) / step;
                return Math.abs(steps - Math.round(steps)) > 1e-9;
            }

            function customError(el) {
                return (customMessages.get(el) || '') !== '';
            }

            var CHECKS = {
                valueMissing: valueMissing, typeMismatch: typeMismatch, patternMismatch: patternMismatch,
                tooLong: tooLong, tooShort: tooShort, rangeUnderflow: rangeUnderflow,
                rangeOverflow: rangeOverflow, stepMismatch: stepMismatch,
                badInput: function () { return false; }, customError: customError
            };

            function flag(el, name) {
                return willValidate(el) && CHECKS[name](el);
            }

            function isValid(el) {
                for (var i = 0; i < FLAGS.length; i++) {
                    if (flag(el, FLAGS[i])) return false;
                }
                return true;
            }

            function ValidityState() { throw new TypeError('Illegal constructor'); }
            FLAGS.concat(['valid']).forEach(function (name) {
                Object.defineProperty(ValidityState.prototype, name, {
                    get: function () {
                        var owner = stateOwners.get(this);
                        if (!owner) throw new TypeError('Illegal invocation');
                        return name === 'valid' ? isValid(owner) : flag(owner, name);
                    },
                    enumerable: true, configurable: true
                });
            });
            Object.defineProperty(ValidityState.prototype, Symbol.toStringTag, { value: 'ValidityState', configurable: true });
            Object.defineProperty(g, 'ValidityState', { value: ValidityState, writable: true, configurable: true, enumerable: false });

            function validityOf(el) {
                var state = states.get(el);
                if (!state) {
                    state = Object.create(ValidityState.prototype);
                    states.set(el, state);
                    stateOwners.set(state, el);
                }
                return state;
            }

            function validationMessage(el) {
                if (!willValidate(el)) return '';
                var custom = customMessages.get(el) || '';
                if (custom !== '') return custom;
                for (var i = 0; i < FLAGS.length; i++) {
                    if (FLAGS[i] !== 'customError' && flag(el, FLAGS[i])) return MESSAGES[FLAGS[i]];
                }
                return '';
            }

            // HTML §4.10.20.2 checkValidity: a candidate that does not satisfy its
            // constraints fires a cancelable invalid event and reports false.
            function checkValidity(el) {
                if (!willValidate(el) || isValid(el)) return true;
                el.dispatchEvent(new Event('invalid', { cancelable: true }));
                return false;
            }

            function define(proto, name, descriptor) {
                if (!proto) return;
                descriptor.configurable = true;
                descriptor.enumerable = true;
                Object.defineProperty(proto, name, descriptor);
            }

            function method(fn, length) {
                Object.defineProperty(fn, 'length', { value: length, configurable: true });
                return fn;
            }

            ['HTMLInputElement', 'HTMLTextAreaElement', 'HTMLSelectElement', 'HTMLButtonElement',
             'HTMLFieldSetElement', 'HTMLOutputElement', 'HTMLObjectElement'].forEach(function (name) {
                var ctor = g[name];
                var proto = typeof ctor === 'function' ? ctor.prototype : null;
                define(proto, 'validity', { get: function validity() { return validityOf(this); } });
                define(proto, 'willValidate', { get: function willValidate_() { return willValidate(this); } });
                define(proto, 'validationMessage', { get: function validationMessage_() { return validationMessage(this); } });
                define(proto, 'checkValidity', { value: method(function checkValidity_() { return checkValidity(this); }, 0), writable: true });
                define(proto, 'reportValidity', { value: method(function reportValidity() { return checkValidity(this); }, 0), writable: true });
                define(proto, 'setCustomValidity', {
                    value: method(function setCustomValidity(error) {
                        if (arguments.length < 1) throw new TypeError("Failed to execute 'setCustomValidity': 1 argument required, but only 0 present.");
                        customMessages.set(this, String(error));
                    }, 1),
                    writable: true
                });
            });

            // HTML §4.10.21.2 statically validate the constraints of a form: every
            // invalid submittable element gets its invalid event.
            function checkForm(form) {
                var valid = true;
                var elements = form.elements || [];
                for (var i = 0; i < elements.length; i++) {
                    var el = elements[i];
                    if (willValidate(el) && !isValid(el)) {
                        el.dispatchEvent(new Event('invalid', { cancelable: true }));
                        valid = false;
                    }
                }
                return valid;
            }

            var formProto = typeof g.HTMLFormElement === 'function' ? g.HTMLFormElement.prototype : null;
            define(formProto, 'checkValidity', { value: method(function checkValidity_() { return checkForm(this); }, 0), writable: true });
            define(formProto, 'reportValidity', { value: method(function reportValidity() { return checkForm(this); }, 0), writable: true });
        })();
        """;

    private void InstallFenJsConstraintValidation()
    {
        try
        {
            EvaluateWithFenJsRaw(ConstraintValidationPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] constraint validation prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }
}
