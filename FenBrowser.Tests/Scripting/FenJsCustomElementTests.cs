using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsCustomElementTests
    {
        [Fact]
        public async Task DefinedAutonomousCustomElement_CanBeConstructedAsHostBackedElement()
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
                    class DirectProbe extends HTMLElement {
                        constructor() {
                            super();
                            this.constructedTag = this.tagName;
                            this.readyBeforeSet = this.hasAttribute('data-ready');
                        }

                        answer() {
                            return [
                                this.constructedTag,
                                String(this.readyBeforeSet),
                                this.tagName,
                                String(this instanceof HTMLElement),
                                String(Object.getPrototypeOf(this) === DirectProbe.prototype)
                            ].join('|');
                        }
                    }

                    customElements.define('x-direct-probe', DirectProbe);
                    var constructed = new DirectProbe();
                    constructed.setAttribute('data-ready', 'yes');

                    return [
                        constructed.answer(),
                        String(constructed.hasAttribute('data-ready'))
                    ].join('|');
                })();
                """);

            Assert.Equal("X-DIRECT-PROBE|false|X-DIRECT-PROBE|true|true|true", result?.ToString());
        }

        [Fact]
        public async Task Es5CustomElementUpgrade_UsesConstructionElementWhenCallPathHitsNativeHTMLElement()
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
                    function WrappedProbe() {
                        var self = HTMLElement.call(this) || this;
                        self.constructed = self.id || 'pending';
                        self.hasReady = self.hasAttribute('data-ready');
                        return self;
                    }
                    WrappedProbe.prototype = Object.create(HTMLElement.prototype);
                    WrappedProbe.prototype.constructor = WrappedProbe;
                    WrappedProbe.prototype.connectedCallback = function () {
                        this.textContent = this.constructed + ':' + String(this.hasReady);
                    };

                    var element = document.createElement('x-es5-call-probe');
                    element.id = 'leaf';
                    element.setAttribute('data-ready', 'yes');
                    document.body.appendChild(element);

                    customElements.define('x-es5-call-probe', WrappedProbe);

                    return [
                        element.constructed,
                        String(element.hasReady),
                        element.textContent,
                        String(element instanceof HTMLElement),
                        String(typeof element.connectedCallback)
                    ].join('|');
                })();
                """);

            Assert.Equal("leaf|true|leaf:true|true|function", result?.ToString());
        }

        [Fact]
        public async Task Es5CustomElementUpgrade_ResolvesInheritedLifecycleHelpers()
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
                    var element = document.createElement('x-inherited-lifecycle-probe');
                    document.body.appendChild(element);

                    function FoundationProbe() {}
                    FoundationProbe.prototype._enableProperties = function () {
                        this.enabledFromFoundation = true;
                    };

                    function BaseProbe() {}
                    BaseProbe.prototype = Object.create(FoundationProbe.prototype);
                    BaseProbe.prototype.constructor = BaseProbe;
                    BaseProbe.prototype.connectedCallback = function () {
                        this.baseConnected = true;
                    };

                    function AppProbe() {
                        return HTMLElement.call(this) || this;
                    }
                    AppProbe.prototype = Object.create(BaseProbe.prototype);
                    AppProbe.prototype.constructor = AppProbe;
                    AppProbe.prototype.connectedCallback = function () {
                        if (BaseProbe.prototype.connectedCallback) {
                            BaseProbe.prototype.connectedCallback.call(this);
                        }
                        this._enableProperties();
                    };

                    customElements.define('x-inherited-lifecycle-probe', AppProbe);

                    return [
                        String(element.baseConnected),
                        String(element.enabledFromFoundation),
                        typeof element._enableProperties,
                        String(Object.getPrototypeOf(element) === AppProbe.prototype),
                        element.__fenCustomElementName
                    ].join('|');
                })();
                """);

            Assert.Equal("true|true|function|true|x-inherited-lifecycle-probe", result?.ToString());
        }

        [Fact]
        public async Task Es5CustomElementUpgrade_WorksAfterHTMLElementReflectConstructShim()
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
                    var nativeHTMLElement = HTMLElement;
                    function ShimmedHTMLElement() {
                        return Reflect.construct(nativeHTMLElement, [], this.constructor);
                    }
                    ShimmedHTMLElement.prototype = nativeHTMLElement.prototype;
                    ShimmedHTMLElement.prototype.constructor = ShimmedHTMLElement;
                    ShimmedHTMLElement.es5Shimmed = true;
                    Object.setPrototypeOf(ShimmedHTMLElement, nativeHTMLElement);
                    HTMLElement = ShimmedHTMLElement;

                    var element = document.createElement('x-reflect-shim-probe');
                    document.body.appendChild(element);

                    function inherit(child, parent) {
                        child.prototype = Object.create(parent.prototype);
                        child.prototype.constructor = child;
                        Object.setPrototypeOf(child, parent);
                    }

                    function DataElement() {
                        return HTMLElement.call(this) || this;
                    }
                    inherit(DataElement, HTMLElement);
                    DataElement.prototype._enableProperties = function () {
                        this.enabledFromDataElement = true;
                    };

                    function AppProbe() {
                        return DataElement.call(this) || this;
                    }
                    inherit(AppProbe, DataElement);
                    AppProbe.prototype.connectedCallback = function () {
                        DataElement.prototype.connectedCallback && DataElement.prototype.connectedCallback.call(this);
                        this._enableProperties();
                    };

                    customElements.define('x-reflect-shim-probe', AppProbe);

                    return [
                        String(element.enabledFromDataElement),
                        typeof element._enableProperties,
                        String(Object.getPrototypeOf(element) === AppProbe.prototype),
                        String(element instanceof HTMLElement)
                    ].join('|');
                })();
                """);

            Assert.Equal("true|function|true|true", result?.ToString());
        }

        [Fact]
        public async Task Es5CustomElementUpgrade_PreservesWrapperConstructedPrototype()
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
                    var NativeHTMLElement = HTMLElement;
                    var element = document.createElement('x-wrapper-prototype-probe');
                    document.body.appendChild(element);

                    function inherit(child, parent) {
                        child.prototype = Object.create(parent.prototype);
                        child.prototype.constructor = child;
                        Object.setPrototypeOf(child, parent);
                    }

                    function DataElement() {
                        return HTMLElement.call(this) || this;
                    }
                    inherit(DataElement, HTMLElement);
                    DataElement.prototype._enableProperties = function () {
                        this.enabledFromDataElement = true;
                    };

                    function AppProbe() {
                        return DataElement.call(this) || this;
                    }
                    inherit(AppProbe, DataElement);
                    AppProbe.prototype.connectedCallback = function () {
                        DataElement.prototype.connectedCallback && DataElement.prototype.connectedCallback.call(this);
                        this._enableProperties();
                    };

                    var originalPrototype = AppProbe.prototype;
                    var WrappedProbe = class extends NativeHTMLElement {
                        constructor() {
                            super();
                            Object.setPrototypeOf(this, originalPrototype);
                            AppProbe.call(this);
                        }
                    };
                    WrappedProbe.prototype.connectedCallback = originalPrototype.connectedCallback;

                    customElements.define('x-wrapper-prototype-probe', WrappedProbe);

                    return [
                        String(element.enabledFromDataElement),
                        typeof element._enableProperties,
                        String(Object.getPrototypeOf(element) === originalPrototype),
                        String(element instanceof HTMLElement)
                    ].join('|');
                })();
                """);

            Assert.Equal("true|function|true|true", result?.ToString());
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
