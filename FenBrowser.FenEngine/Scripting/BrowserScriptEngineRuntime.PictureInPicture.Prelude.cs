namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The Picture-in-Picture interfaces as script: <c>PictureInPictureWindow</c>,
/// <c>PictureInPictureEvent</c>, the document and video element members, and the promises.
/// The state machine behind them is a <c>__fenPip*</c> native.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string PictureInPicturePrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EventTarget !== 'function' || typeof g.PictureInPictureWindow === 'function') return;
            if (typeof g.__fenPipCheck !== 'function') return;
            var doc = g.document;
            if (!doc) return;

            function defineInterface(name, ctor, base) {
                ctor.prototype = Object.create((base || g.EventTarget).prototype);
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
                return ctor;
            }
            function accessor(target, name, get, set) {
                Object.defineProperty(target, name, { get: get, set: set, enumerable: true, configurable: true });
            }
            function method(target, name, fn, length) {
                if (length !== undefined) Object.defineProperty(fn, 'length', { value: length, configurable: true });
                Object.defineProperty(target, name, { value: fn, writable: true, enumerable: true, configurable: true });
            }
            function handlerAttribute(proto, name) {
                var key = '_fenHandler_' + name;
                var type = name.slice(2);
                accessor(proto, name,
                    function () { return this[key] || null; },
                    function (v) {
                        this[key] = (typeof v === 'function' || (v !== null && typeof v === 'object')) ? v : null;
                        if (!this[key + '_installed'] && typeof this.addEventListener === 'function') {
                            this[key + '_installed'] = true;
                            var target = this;
                            this.addEventListener(type, function (ev) {
                                var h = target[key];
                                if (typeof h === 'function') h.call(target, ev);
                                else if (h && typeof h.handleEvent === 'function') h.handleEvent(ev);
                            });
                        }
                    });
            }
            // The media element task source, so a Picture-in-Picture event lands in the
            // same queue as the playback events around it.
            function queueTask(fn) {
                if (typeof g.__fenQueueMediaTask === 'function') g.__fenQueueMediaTask(null, fn);
                else g.setTimeout(fn, 0);
            }

            // ---- PictureInPictureWindow ----
            // A page keeps the window it was given, so there is one per element and it is
            // handed back for every request. It reads 0 by 0 once its element is no longer
            // the one being shown.

            function PictureInPictureWindow() {
                g.EventTarget.call(this);
                this._width = 0;
                this._height = 0;
            }
            defineInterface('PictureInPictureWindow', PictureInPictureWindow);
            accessor(PictureInPictureWindow.prototype, 'width', function () { return this._width; });
            accessor(PictureInPictureWindow.prototype, 'height', function () { return this._height; });
            handlerAttribute(PictureInPictureWindow.prototype, 'onresize');

            // ---- PictureInPictureEvent ----

            function PictureInPictureEvent(type, init) {
                if (arguments.length < 1)
                    throw new TypeError("Failed to construct 'PictureInPictureEvent': 1 argument required.");
                init = init || {};
                if (!init.pictureInPictureWindow)
                    throw new TypeError("Failed to construct 'PictureInPictureEvent': pictureInPictureWindow is required.");
                var ev = new g.Event(String(type), init);
                Object.setPrototypeOf(ev, PictureInPictureEvent.prototype);
                ev._pictureInPictureWindow = init.pictureInPictureWindow;
                return ev;
            }
            defineInterface('PictureInPictureEvent', PictureInPictureEvent, g.Event);
            accessor(PictureInPictureEvent.prototype, 'pictureInPictureWindow', function () {
                return this._pictureInPictureWindow;
            });

            var windows = new WeakMap();
            function windowFor(element) {
                var w = windows.get(element);
                if (!w) { w = new PictureInPictureWindow(); windows.set(element, w); }
                return w;
            }

            function fire(element, type, win) {
                // §4.1/§4.2: the events bubble, are not cancelable and are not composed.
                var ev = new PictureInPictureEvent(type, {
                    bubbles: true, cancelable: false, composed: false, pictureInPictureWindow: win
                });
                try { element.dispatchEvent(ev); } catch (e) {}
            }

            function refuse(reject, name) {
                reject(new g.DOMException(messageFor(name), name));
            }

            // The exit half, shared by exitPictureInPicture() and by a request that
            // displaces another element: the element stops being shown and its window reads
            // 0 by 0 before its handler runs, so a page reading
            // document.pictureInPictureElement there sees nothing being shown.
            function applyExit(result) {
                var left = result.left;
                if (!left) return null;
                var leftWindow = windowFor(left);
                leftWindow._width = 0;
                leftWindow._height = 0;
                return { element: left, win: leftWindow };
            }

            function exitSequence(result, resolve, reject) {
                if (result.error) { refuse(reject, result.error); return; }
                var left = applyExit(result);
                queueTask(function () {
                    if (left) fire(left.element, 'leavepictureinpicture', left.win);
                    if (resolve) resolve(undefined);
                });
            }

            function messageFor(name) {
                if (name === 'NotAllowedError')
                    return 'Must be handling a user gesture to request Picture-in-Picture.';
                if (name === 'NotSupportedError')
                    return 'Picture-in-Picture is not available.';
                return 'The element is not eligible for Picture-in-Picture, or nothing is in Picture-in-Picture.';
            }

            // ---- Document members ----

            var documentTarget = (typeof g.Document === 'function' && g.Document.prototype) ? g.Document.prototype : doc;
            accessor(documentTarget, 'pictureInPictureEnabled', function () { return !!g.__fenPipEnabled(); });
            // The Picture-in-Picture element is retargeted against the node whose property
            // is read, so a video inside a shadow tree is reported to the document as the
            // shadow host rather than as itself.
            function retarget(node, against) {
                while (node) {
                    var root = typeof node.getRootNode === 'function' ? node.getRootNode() : null;
                    if (!root || root === against || root === node) return node;
                    if (typeof g.ShadowRoot !== 'function' || !(root instanceof g.ShadowRoot)) return node;
                    node = root.host;
                }
                return null;
            }
            accessor(documentTarget, 'pictureInPictureElement', function () {
                return retarget(g.__fenPipElement(), this);
            });
            method(documentTarget, 'exitPictureInPicture', function () {
                return new Promise(function (resolve, reject) {
                    exitSequence(g.__fenPipExit(), resolve, reject);
                });
            }, 0);

            // A ShadowRoot is a DocumentOrShadowRoot too, and reports the element only when
            // it is inside that tree.
            if (typeof g.ShadowRoot === 'function' && g.ShadowRoot.prototype) {
                accessor(g.ShadowRoot.prototype, 'pictureInPictureElement', function () {
                    // Step 1 of the getter: a shadow root whose host is not connected
                    // exposes nothing, even though the document still reports that host.
                    if (!this.host || !this.host.isConnected) return null;
                    var element = g.__fenPipElement();
                    if (!element) return null;
                    // Only a node of this tree, retargeted the way the document does it.
                    var here = retarget(element, this);
                    if (!here) return null;
                    var root = typeof here.getRootNode === 'function' ? here.getRootNode() : null;
                    return root === this ? here : null;
                });
            }

            // ---- HTMLVideoElement members ----

            if (typeof g.HTMLVideoElement === 'function' && g.HTMLVideoElement.prototype) {
                var videoProto = g.HTMLVideoElement.prototype;
                method(videoProto, 'requestPictureInPicture', function () {
                    var element = this;
                    // The checks run and the activation is spent now; the swap itself is a
                    // task, so a second request made inside the same gesture is refused
                    // before the first one has taken effect.
                    var check = g.__fenPipCheck(element);
                    return new Promise(function (resolve, reject) {
                        if (check.error) { refuse(reject, check.error); return; }
                        queueTask(function () {
                            var displaced = null;
                            if (g.__fenPipElement() !== element) {
                                displaced = applyExit(g.__fenPipExit());
                                if (displaced) fire(displaced.element, 'leavepictureinpicture', displaced.win);
                            }

                            var result = g.__fenPipEnter(element);
                            if (result.error) { refuse(reject, result.error); return; }
                            var win = windowFor(element);
                            win._width = result.width;
                            win._height = result.height;
                            if (result.entered) fire(element, 'enterpictureinpicture', win);
                            resolve(win);
                        });
                    });
                }, 0);

                handlerAttribute(videoProto, 'onenterpictureinpicture');
                handlerAttribute(videoProto, 'onleavepictureinpicture');
            }

            // The engine takes an element out of Picture-in-Picture: its document went away,
            // or the window was closed from outside the page.
            g.__fenPipExitForced = function (element) {
                exitSequence(g.__fenPipExit(), null, function () {});
            };

            g.__fenPipFireResize = function (width, height) {
                var element = g.__fenPipElement();
                if (!element) return;
                var win = windowFor(element);
                win._width = width;
                win._height = height;
                queueTask(function () {
                    try { win.dispatchEvent(new g.Event('resize')); } catch (e) {}
                });
            };
        })();
        """;
}
