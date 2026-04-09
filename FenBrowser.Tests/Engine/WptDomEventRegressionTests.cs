using System;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class WptDomEventRegressionTests
{
    [Fact]
    public async Task CancelBubble_And_ReturnValue_Follow_Legacy_Event_Semantics()
    {
        var engine = await CreateEngineAsync("<html><body><div id='outer'><div id='middle'><div id='inner'></div></div></div></body></html>");

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var ev = new Event('foo', { cancelable: true });
                ev.returnValue = false;
                return ev.defaultPrevented && ev.returnValue === false;
            })();
        "));

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var outer = document.getElementById('outer');
                var middle = document.getElementById('middle');
                var inner = document.getElementById('inner');
                var outerHit = false;

                outer.addEventListener('barbaz', function () { outerHit = true; }, false);
                middle.addEventListener('barbaz', function (e) {
                    e.cancelBubble = true;
                    e.cancelBubble = false;
                }, false);

                inner.dispatchEvent(new Event('barbaz', { bubbles: true }));
                return outerHit === false;
            })();
        "));
    }

    [Fact]
    public async Task Document_Capture_Object_Option_Remains_Capturing_After_Bubble_Registration()
    {
        var engine = await CreateEngineAsync("<html><body><div id='host'></div></body></html>");

        Assert.Equal("3,1", engine.Evaluate(@"
            (function () {
                function runCaptureValue(captureValue) {
                    var handlerPhase = 'unset';
                    var handler = function (e) { handlerPhase = String(e.eventPhase); };
                    document.addEventListener('test', handler, captureValue);
                    document.body.dispatchEvent(new Event('test', { bubbles: true }));
                    document.removeEventListener('test', handler, captureValue);
                    document.body.dispatchEvent(new Event('test', { bubbles: true }));
                    return handlerPhase;
                }

                return runCaptureValue({}) + ',' + runCaptureValue({ capture: true });
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task InitEvent_And_DispatchEvent_Follow_Wpt_Semantics()
    {
        var engine = await CreateEngineAsync("<html><body><div id='host'></div></body></html>");

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var ev = document.createEvent('Event');
                ev.initEvent('foo', true, true);
                ev.preventDefault();
                ev.initEvent('foo', true, true);
                return ev.defaultPrevented === false;
            })();
        "));

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                try {
                    document.dispatchEvent(null);
                    return false;
                } catch (e) {
                    return e instanceof TypeError;
                }
            })();
        "));

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var e = document.createEvent('BeforeUnloadEvent');
                try {
                    document.dispatchEvent(e);
                    return false;
                } catch (err) {
                    return String(err).indexOf('InvalidStateError') >= 0;
                }
            })();
        "));

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var cases = [
                    {
                        name: 'KeyboardEvent',
                        event: new KeyboardEvent('type', { key: 'A' }),
                        init: function (e) { e.initKeyboardEvent('type2', true, true, null, 'b', 1, 'Control', true, 'en'); },
                        check: function (e) { return e.type === 'type' && e.key === 'A' && e.repeat === false; }
                    },
                    {
                        name: 'MouseEvent',
                        event: new MouseEvent('type'),
                        init: function (e) { e.initMouseEvent('type2', true, true, null, 0, 1, 1, 1, 1, true, true, true, true, 1, null); },
                        check: function (e) { return e.type === 'type' && e.screenX === 0 && e.button === 0; }
                    },
                    {
                        name: 'CustomEvent',
                        event: new CustomEvent('type', { detail: null }),
                        init: function (e) { e.initCustomEvent('type2', true, true, 1); },
                        check: function (e) { return e.type === 'type' && e.detail === null; }
                    },
                    {
                        name: 'UIEvent',
                        event: new UIEvent('type'),
                        init: function (e) { e.initUIEvent('type2', true, true, window, 1); },
                        check: function (e) { return e.type === 'type' && e.view === null && e.detail === 0; }
                    },
                    {
                        name: 'Event',
                        event: new Event('type'),
                        init: function (e) { e.initEvent('type2', true, true); },
                        check: function (e) { return e.type === 'type' && e.bubbles === false && e.cancelable === false; }
                    }
                ];

                for (var i = 0; i < cases.length; i++) {
                    var entry = cases[i];
                    if (typeof entry.init !== 'function') {
                        return entry.name + ':missing-init';
                    }

                    var target = document.createElement('div');
                    var ok = false;
                    target.addEventListener('type', (function (current) {
                        return function () {
                            current.init(current.event);
                            ok = current.check(current.event);

                            var o = current.event;
                            while ((o = Object.getPrototypeOf(o))) {
                                if (!o.constructor || !o.constructor.name) {
                                    ok = false;
                                    break;
                                }

                                if (!(o.constructor.name in {
                                    KeyboardEvent: true,
                                    MouseEvent: true,
                                    CustomEvent: true,
                                    UIEvent: true,
                                    Event: true
                                })) {
                                    break;
                                }
                            }
                        };
                    })(entry), false);
                    target.dispatchEvent(entry.event);
                    if (!ok) {
                        return entry.name + ':check-failed';
                    }
                }

                return true;
            })();
        "));

        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var events = {
                  'KeyboardEvent': {
                    'constructor': function() { return new KeyboardEvent('type', {key: 'A'}); },
                    'init': function(ev) { ev.initKeyboardEvent('type2', true, true, null, 'a', 1, '', true, ''); },
                    'check': function(ev) { return ev.key === 'A' && ev.repeat === false && ev.location === 0; }
                  },
                  'MouseEvent': {
                    'constructor': function() { return new MouseEvent('type'); },
                    'init': function(ev) { ev.initMouseEvent('type2', true, true, null, 0, 1, 1, 1, 1, true, true, true, true, 1, null); },
                    'check': function(ev) { return ev.screenX === 0 && ev.screenY === 0 && ev.button === 0; }
                  },
                  'CustomEvent': {
                    'constructor': function() { return new CustomEvent('type'); },
                    'init': function(ev) { ev.initCustomEvent('type2', true, true, 1); },
                    'check': function(ev) { return ev.detail === null; }
                  },
                  'UIEvent': {
                    'constructor': function() { return new UIEvent('type'); },
                    'init': function(ev) { ev.initUIEvent('type2', true, true, window, 1); },
                    'check': function(ev) { return ev.view === null && ev.detail === 0; }
                  },
                  'Event': {
                    'constructor': function() { return new Event('type'); },
                    'init': function(ev) { ev.initEvent('type2', true, true); },
                    'check': function(ev) { return ev.bubbles === false && ev.cancelable === false && ev.type === 'type'; }
                  }
                };

                var names = Object.keys(events);
                for (var i = 0; i < names.length; i++) {
                  var name = names[i];
                  var e = events[name].constructor();
                  if (!e) return name + ':ctor';

                  var target = document.createElement('div');
                  var outcome = null;
                  target.addEventListener('type', (function(currentName, currentEvent) {
                    return function() {
                      events[currentName].init(currentEvent);
                      var o = currentEvent;
                      while ((o = Object.getPrototypeOf(o))) {
                        if (!(o.constructor.name in events)) {
                          break;
                        }
                        if (!events[o.constructor.name].check(currentEvent)) {
                          outcome = currentName + ':check:' + o.constructor.name;
                          return;
                        }
                      }
                      outcome = 'ok';
                    };
                  })(name, e), false);

                  if (target.dispatchEvent(e) !== true) return name + ':dispatch';
                  if (outcome !== 'ok') return outcome || (name + ':listener');
                }

                return true;
            })();
        "));
    }

    [Fact]
    public async Task Document_Chain_Apis_Are_Available_On_Cloned_And_Constructed_Documents()
    {
        var engine = await CreateEngineAsync("<html><body><table id='table'><tbody id='table-body'><tr id='parent'><td id='target'>x</td></tr></tbody></table></body></html>");

        Assert.Equal("ok", engine.Evaluate(@"
            (function () {
                function inspectDocument(doc, label) {
                    if (!doc) return label + ':doc';
                    if (typeof doc.cloneNode !== 'function') return label + ':cloneNode';
                    if (typeof doc.appendChild !== 'function') return label + ':appendChild';
                    if (typeof doc.getElementsByTagName !== 'function') return label + ':getElementsByTagName';
                    if (!doc.documentElement) return label + ':documentElement';

                    var bodies = doc.getElementsByTagName('body');
                    if (!bodies) return label + ':body-collection';
                    if (!bodies[0]) return label + ':body';
                    return 'ok';
                }

                var clone;
                try {
                    clone = document.cloneNode(true);
                } catch (err) {
                    return 'clone-throw:' + err;
                }
                var cloneStatus = inspectDocument(clone, 'clone');
                if (cloneStatus !== 'ok') return cloneStatus;

                var created;
                try {
                    created = new Document();
                } catch (err) {
                    return 'new-throw:' + err;
                }
                var createdStatus = inspectDocument(created, 'new');
                if (createdStatus !== 'ok' && createdStatus !== 'new:documentElement' && createdStatus !== 'new:body') return createdStatus;
                try {
                    created.appendChild(document.documentElement.cloneNode(true));
                } catch (err) {
                    return 'append-throw:' + err;
                }
                createdStatus = inspectDocument(created, 'new-after-append');
                if (createdStatus !== 'ok') return createdStatus;

                var htmlDocument;
                try {
                    htmlDocument = document.implementation.createHTMLDocument();
                } catch (err) {
                    return 'html-throw:' + err;
                }
                var htmlStatus = inspectDocument(htmlDocument, 'html');
                if (htmlStatus !== 'ok') return htmlStatus;

                return 'ok';
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task Document_Event_Path_Uses_Wpt_Document_Chain_Order()
    {
        var engine = await CreateEngineAsync("<html><body><table id='table'><tbody id='table-body'><tr id='parent'><td id='target'>x</td></tr></tbody></table></body></html>");

        Assert.Equal("ok", engine.Evaluate(@"
            (function () {
                function sameValueArray(actual, expected) {
                    if (!actual || !expected || actual.length !== expected.length) {
                        return false;
                    }

                    for (var i = 0; i < actual.length; i++) {
                        if (actual[i] !== expected[i]) {
                            return false;
                        }
                    }

                    return true;
                }

                function targetsForDocumentChain(doc) {
                    return [
                        doc,
                        doc.documentElement,
                        doc.getElementsByTagName('body')[0],
                        doc.getElementById('table'),
                        doc.getElementById('table-body'),
                        doc.getElementById('parent')
                    ];
                }

                function concatReverse(items) {
                    return items.concat(items.slice().reverse());
                }

                function runCase(doc, eventType, bubbles, includeWindow) {
                    var target = doc.getElementById('target');
                    var parentChain = targetsForDocumentChain(doc);
                    if (includeWindow) {
                        parentChain = [window].concat(parentChain);
                    }

                    var targets = parentChain.concat(target);
                    var expectedTargets = bubbles ? concatReverse(targets) : targets.concat(target);
                    var actualTargets = [];
                    var listener = function (evt) { actualTargets.push(evt.currentTarget); };

                    for (var i = 0; i < targets.length; i++) {
                        targets[i].addEventListener(eventType, listener, true);
                        targets[i].addEventListener(eventType, listener, false);
                    }

                    var evt = doc.createEvent('Event');
                    evt.initEvent(eventType, bubbles, true);
                    target.dispatchEvent(evt);

                    for (var j = 0; j < targets.length; j++) {
                        targets[j].removeEventListener(eventType, listener, true);
                        targets[j].removeEventListener(eventType, listener, false);
                    }

                    return sameValueArray(actualTargets, expectedTargets)
                        ? 'ok'
                        : 'mismatch:' + eventType + ':' + actualTargets.length + ':' + expectedTargets.length;
                }

                var loadStatus = runCase(document, 'load', false, false);
                if (loadStatus !== 'ok') return loadStatus;

                var bubbleStatus = runCase(document, 'click', true, true);
                if (bubbleStatus !== 'ok') return bubbleStatus;

                var htmlDocument = document.implementation.createHTMLDocument();
                htmlDocument.body.appendChild(document.getElementById('table').cloneNode(true));
                var htmlStatus = runCase(htmlDocument, 'click', true, false);
                if (htmlStatus !== 'ok') return htmlStatus;

                return 'ok';
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task WindowOnError_Fires_For_ShadowTree_ErrorEvent_On_Wpt_Path()
    {
        var engine = await CreateEngineAsync("<html><body><div id='host'></div></body></html>");

        Assert.Equal("ok", engine.Evaluate(@"
            (function () {
                var host = document.getElementById('host');
                var root = host.attachShadow({ mode: 'open' });
                var span = document.createElement('span');
                root.appendChild(span);

                var windowOnErrorCalled = false;
                var windowEventType = 'missing';

                window.onerror = function () {
                    windowOnErrorCalled = true;
                    windowEventType = typeof window.event === 'object' && window.event ? window.event.type : typeof window.event;
                };

                span.dispatchEvent(new ErrorEvent('error', { composed: true, bubbles: true }));

                return windowOnErrorCalled && windowEventType === 'error' ? 'ok' : ('fail:' + windowOnErrorCalled + ':' + windowEventType);
            })();
        ")?.ToString());
    }

    private static async Task<JavaScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser(html, baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }
}
