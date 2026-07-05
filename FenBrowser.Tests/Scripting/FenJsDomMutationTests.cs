using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsDomMutationTests
    {
        [Fact]
        public async Task ReplaceChild_ReplacesElementAndDocumentFragmentChildren()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body><div id='host'><span id='old'>old</span></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.getElementById('host');
                    var old = document.getElementById('old');
                    var replacement = document.createElement('strong');
                    replacement.id = 'new';
                    replacement.textContent = 'new';
                    var returned = host.replaceChild(replacement, old);

                    var fragment = document.createDocumentFragment();
                    var first = document.createElement('i');
                    first.textContent = 'first';
                    var second = document.createElement('b');
                    second.textContent = 'second';
                    fragment.appendChild(first);
                    var fragmentReturned = fragment.replaceChild(second, first);

                    return [
                        typeof Element.prototype.replaceChild,
                        String(returned === old),
                        String(old.parentNode === null),
                        host.firstElementChild.localName,
                        host.textContent,
                        String(fragmentReturned === first),
                        String(first.parentNode === null),
                        fragment.firstElementChild.localName,
                        fragment.firstElementChild.textContent
                    ].join('|');
                })();
                """);

            Assert.Equal("function|true|true|strong|new|true|true|b|second", result?.ToString());
        }

        [Fact]
        public async Task NodePrototypeInsertBefore_CanBeCapturedAndWrappedForDocumentFragments()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body><div id='host'><span id='old'>old</span></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var nativeInsertBefore = Node.prototype.insertBefore;
                    var callType = typeof nativeInsertBefore.call;
                    var wrappedCalls = 0;
                    Node.prototype.insertBefore = function (child, reference) {
                        if (child instanceof DocumentFragment) {
                            Array.from(child.children);
                        }
                        wrappedCalls++;
                        return nativeInsertBefore.call(this, child, reference);
                    };

                    var host = document.getElementById('host');
                    var old = document.getElementById('old');
                    var fragment = document.createDocumentFragment();
                    var first = document.createElement('i');
                    first.id = 'first';
                    first.textContent = 'first';
                    var second = document.createElement('b');
                    second.id = 'second';
                    second.textContent = 'second';
                    fragment.appendChild(first);
                    fragment.appendChild(second);

                    host.insertBefore(fragment, old);

                    return [
                        typeof nativeInsertBefore,
                        callType,
                        String(Object.prototype.hasOwnProperty.call(Node.prototype, 'insertBefore')),
                        String(Object.prototype.hasOwnProperty.call(Element.prototype, 'insertBefore')),
                        String(wrappedCalls),
                        host.firstElementChild.id,
                        String(host.children.length),
                        host.textContent
                    ].join('|');
                })();
                """);

            Assert.Equal("function|function|true|false|1|first|3|firstsecondold", result?.ToString());
        }

        [Fact]
        public async Task NodePrototypeMethods_CapturedFromInstancesHonorCallReceiver()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body><div id='first'></div><div id='second'></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var first = document.getElementById('first');
                    var second = document.getElementById('second');
                    var child = document.createElement('span');
                    child.id = 'moved';
                    var appendChild = first.appendChild;
                    var contains = first.contains;

                    appendChild.call(second, child);

                    return [
                        typeof appendChild,
                        typeof contains,
                        String(child.parentNode === second),
                        String(first.contains(child)),
                        String(second.contains(child)),
                        String(contains.call(child, second)),
                        String(contains.call(second, child))
                    ].join('|');
                })();
                """);

            Assert.Equal("function|function|true|false|true|false|true", result?.ToString());
        }

        [Fact]
        public async Task DocumentFragment_ExposesNodeSurfaceForPolyfillMutationGuards()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body><div id='host'></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var host = document.getElementById('host');
                    var child = document.createElement('span');
                    child.textContent = 'x';
                    var fragment = document.createDocumentFragment();
                    fragment.appendChild(child);
                    var empty = document.createDocumentFragment();
                    var emptyReturned = host.appendChild(empty);
                    var containedChildBefore = fragment.contains(child);
                    var containedHostBefore = fragment.contains(host);
                    var childNodesBefore = fragment.childNodes.length;
                    var hasChildNodesBefore = fragment.hasChildNodes();

                    host.appendChild(fragment);

                    return [
                        typeof fragment.contains,
                        typeof fragment.hasChildNodes,
                        String(fragment.isConnected),
                        String(containedChildBefore),
                        String(containedHostBefore),
                        String(childNodesBefore),
                        String(hasChildNodesBefore),
                        String(emptyReturned === empty),
                        String(host.firstChild === child),
                        String(fragment.childNodes.length),
                        String(host.contains(child))
                    ].join('|');
                })();
                """);

            Assert.Equal("function|function|false|true|false|1|true|true|true|0|true", result?.ToString());
        }

        [Fact]
        public async Task TreeWalkerCurrentNode_CanLeaveRootButTraversalStaysBounded()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body><div id='root'><span id='child'></span></div><p id='outsider'></p></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var root = document.getElementById('root');
                    var child = document.getElementById('child');
                    var outsider = document.getElementById('outsider');
                    var walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);

                    walker.currentNode = child;
                    var childAssigned = walker.currentNode === child;

                    var status = 'ok';
                    try {
                        walker.currentNode = outsider;
                    } catch (e) {
                        status = String(e && e.message ? e.message : e);
                    }

                    return [
                        status,
                        String(childAssigned),
                        String(walker.currentNode === outsider),
                        String(walker.parentNode() === null),
                        String(walker.firstChild() === null),
                        String(walker.lastChild() === null),
                        String(walker.previousSibling() === null),
                        String(walker.nextSibling() === null),
                        String(walker.previousNode() === null),
                        String(walker.nextNode() === null)
                    ].join('|');
                })();
                """);

            Assert.Equal("ok|true|true|true|true|true|true|true|true|true", result?.ToString());
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
