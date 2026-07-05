using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsShadowDomHostObjectTests
    {
        [Fact]
        public async Task HostObjectProtoAssignment_ReachesAssignedPrototypeMethods()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var fragment = document.createDocumentFragment();
                    var proto = {
                        za: function (host, source) {
                            this.templateInfo = source.nodeName;
                            this.hostSeen = host.localName;
                            return this;
                        }
                    };

                    fragment.__proto__ = proto;
                    fragment.za(document.createElement('div'), document);

                    return [
                        fragment.templateInfo,
                        fragment.hostSeen,
                        String(fragment.__proto__ === proto),
                        String(Object.getPrototypeOf(fragment) === proto)
                    ].join('|');
                })();
                """);

            Assert.Equal("#document|div|true|true", result?.ToString());
        }

        [Fact]
        public async Task ElementAttachShadow_ReturnsOpenShadowRootHostObject()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.createElement('div');
                    var shadow = host.attachShadow({
                        mode: 'open',
                        delegatesFocus: true,
                        slotAssignment: 'manual'
                    });
                    shadow.appendChild(document.createElement('span'));

                    return [
                        typeof Element.prototype.attachShadow,
                        String(host.shadowRoot === shadow),
                        String(shadow instanceof ShadowRoot),
                        String(shadow.host === host),
                        shadow.mode,
                        String(shadow.delegatesFocus),
                        shadow.slotAssignment,
                        shadow.firstElementChild.localName
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|true|true|open|true|manual|span", result?.ToString());
        }

        [Fact]
        public async Task HTMLElementPrototypeAttachShadow_CallReachesHostElement()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.createElement('div');
                    var attachShadow = HTMLElement.prototype.attachShadow;
                    var shadow = attachShadow.call(host, { mode: 'open' });

                    return [
                        typeof attachShadow,
                        String(shadow.host === host),
                        shadow.mode,
                        String(host.shadowRoot === shadow)
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|open|true", result?.ToString());
        }

        [Fact]
        public async Task ElementPrototypeAttachShadow_CanBeWrappedWithCapturedCall()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var original = Element.prototype.attachShadow;
                    var wrapperSeen = false;
                    Element.prototype.attachShadow = function (init) {
                        var shadow = original.call(this, init);
                        wrapperSeen = true;
                        return shadow;
                    };

                    var host = document.createElement('div');
                    var shadow = host.attachShadow({ mode: 'open' });

                    return [
                        typeof original,
                        String(shadow.host === host),
                        shadow.mode,
                        String(wrapperSeen),
                        String(host.shadowRoot === shadow)
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|open|true|true", result?.ToString());
        }

        [Fact]
        public async Task HostElementHasOwnProperty_IsCallableThroughObjectPrototypeMethod()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.createElement('div');
                    host.expando = 42;

                    return [
                        typeof host.hasOwnProperty,
                        typeof host.hasOwnProperty.call,
                        String(host.hasOwnProperty('expando')),
                        String(host.hasOwnProperty.call(host, 'expando')),
                        String(host.hasOwnProperty('missing')),
                        String(host.hasOwnProperty('hasOwnProperty'))
                    ].join('|');
                })();
                """);

            Assert.Equal("function|function|true|true|false|false", result?.ToString());
        }

        [Fact]
        public async Task ElementAttachShadow_CopiesShadyUpgradeFragmentExpandosToShadowRoot()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.createElement('div');
                    var fragment = document.createDocumentFragment();
                    fragment.nodeList = [document.createElement('span')];
                    fragment.$ = { root: true };
                    fragment.templateInfo = { ready: true };
                    fragment.za = function () {
                        return this.templateInfo.ready && this.$.root && this.nodeList.length === 1;
                    };

                    var shadow = host.attachShadow({
                        mode: 'open',
                        shadyUpgradeFragment: fragment
                    });

                    return [
                        String(shadow.nodeList === fragment.nodeList),
                        String(shadow.$ === fragment.$),
                        String(shadow.templateInfo === fragment.templateInfo),
                        String(shadow.za()),
                        String(shadow.host === host),
                        shadow.mode
                    ].join('|');
                })();
                """);

            Assert.Equal("true|true|true|true|true|open", result?.ToString());
        }

        [Fact]
        public async Task ElementAttachShadow_CopiesShadyUpgradeFragmentPrototypeMethodsToShadowRoot()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.createElement('div');
                    var fragment = document.createDocumentFragment();
                    fragment.__proto__ = {
                        za: function (hostNode, source) {
                            this.hostSeen = hostNode.localName;
                            this.templateInfo = source.nodeName;
                            return this;
                        }
                    };

                    var shadow = host.attachShadow({
                        mode: 'open',
                        shadyUpgradeFragment: fragment
                    });
                    var returned = shadow.za.call(shadow, host, document);

                    return [
                        typeof shadow.za,
                        String(returned === shadow),
                        shadow.hostSeen,
                        shadow.templateInfo,
                        String(shadow.host === host),
                        shadow.mode
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|div|#document|true|open", result?.ToString());
        }

        [Fact]
        public async Task ObjectPrototypeAnnexBMethods_HandleDomHostObjectReceivers()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var fragment = document.createDocumentFragment();
                    fragment.expando = 7;

                    Object.prototype.__defineGetter__.call(fragment, 'answer', function () {
                        return this.expando + 35;
                    });
                    Object.prototype.__defineSetter__.call(fragment, 'answer', function (value) {
                        this.expando = value - 35;
                    });

                    var getter = Object.prototype.__lookupGetter__.call(fragment, 'answer');
                    var setter = Object.prototype.__lookupSetter__.call(fragment, 'answer');
                    var before = fragment.answer;
                    fragment.answer = 50;

                    var proto = { marker: function () { return 'ok'; } };
                    var protoAccessor = Object.getOwnPropertyDescriptor(Object.prototype, '__proto__');
                    protoAccessor.set.call(fragment, proto);

                    return [
                        String(Object.prototype.hasOwnProperty.call(fragment, 'expando')),
                        String(Object.prototype.propertyIsEnumerable.call(fragment, 'expando')),
                        String(getter === Object.getOwnPropertyDescriptor(fragment, 'answer').get),
                        String(typeof setter),
                        String(before),
                        String(fragment.expando),
                        String(protoAccessor.get.call(fragment) === proto),
                        fragment.marker(),
                        String(Object.prototype.valueOf.call(fragment) === fragment)
                    ].join('|');
                })();
                """);

            Assert.Equal("true|true|true|function|42|15|true|ok|true", result?.ToString());
        }

        [Fact]
        public async Task ObjectStaticEnumerationMethods_HandleDomHostObjectExpandos()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var fragment = document.createDocumentFragment();
                    fragment.first = 'a';
                    fragment.second = 'b';

                    var entries = Object.entries(fragment);
                    var copied = Object.assign({}, fragment);
                    var target = document.createDocumentFragment();
                    Object.assign(target, { third: 'c' });

                    return [
                        Object.keys(fragment).join(','),
                        Object.values(fragment).join(','),
                        entries[0][0] + ':' + entries[0][1],
                        entries[1][0] + ':' + entries[1][1],
                        copied.first + copied.second,
                        target.third
                    ].join('|');
                })();
                """);

            Assert.Equal("first,second|a,b|first:a|second:b|ab|c", result?.ToString());
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
}
