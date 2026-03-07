using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class HtmlCollectionTests
    {
        [Fact]
        public void CallbackScopedStringIndexing_Works()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                'use strict';
                function test(fn) { fn(); }
                var __stringIndexErr = '';
                var __stringIndexValue = '';
                try {
                    test(() => {
                        const ids = '123';
                        let idx = 0;
                        __stringIndexValue = ids[idx++];
                    });
                } catch (e) {
                    __stringIndexErr = String(e);
                }
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__stringIndexErr").ToString());
            Assert.Equal("1", runtime.GetGlobal("__stringIndexValue").ToString());
        }

        [Fact]
        public void HtmlCollection_IsLiveAndNamedItemReflectsAppendedElements()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            var context = runtime.Context;
            var documentObject = runtime.GetGlobal("document").AsObject();
            var getElementsByTagName = documentObject.Get("getElementsByTagName", context).AsFunction();
            var createElement = documentObject.Get("createElement", context).AsFunction();
            var body = documentObject.Get("body", context);

            var collection = getElementsByTagName.Invoke(new[] { FenValue.FromString("span") }, context);
            Assert.Equal(0, collection.AsObject().Get("length", context).ToNumber());

            var spanElement = document.CreateElement("span");
            spanElement.SetAttribute("id", "new-id");
            document.Body!.AppendChild(spanElement);
            var span = DomWrapperFactory.Wrap(spanElement, context);

            Assert.Equal(1, collection.AsObject().Get("length", context).ToNumber());

            var namedItem = collection.AsObject().Get("namedItem", context).AsFunction();
            var resolved = namedItem.Invoke(new[] { FenValue.FromString("new-id") }, context, collection);

            Assert.True(resolved.StrictEquals(span));
        }

        [Fact]
        public void HtmlCollection_IsIterableViaForOf()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);
            var context = runtime.Context;

            var body = document.Body!;
            for (var i = 1; i <= 3; i++)
            {
                var paragraph = document.CreateElement("p");
                paragraph.SetAttribute("id", i.ToString());
                body.AppendChild(paragraph);
            }

            var documentObject = runtime.GetGlobal("document").AsObject();
            var getElementsByTagName = documentObject.Get("getElementsByTagName", context).AsFunction();
            var paragraphsCollection = getElementsByTagName.Invoke(new[] { FenValue.FromString("p") }, context);
            var item = paragraphsCollection.AsObject().Get("item", context).AsFunction()
                .Invoke(new[] { FenValue.FromNumber(0) }, context, paragraphsCollection);

            Assert.IsType<ElementWrapper>(item.AsObject());
            Assert.True(item.AsObject().Get("getAttribute", context).IsFunction);

            runtime.ExecuteSimple(@"
                var paragraphs = document.getElementsByTagName('p');
                var __bodyType = typeof document.body;
                var __bodyTagName = document.body ? document.body.tagName : 'missing';
                var __directItemType = typeof document.getElementsByTagName('p').item(0);
                var __directItemTagName = document.getElementsByTagName('p').item(0)
                    ? document.getElementsByTagName('p').item(0).tagName
                    : 'missing';
                var firstItem = paragraphs.item(0);
                var iterator = paragraphs[Symbol.iterator]();
                var firstStep = iterator.next();
                var __itemType = typeof firstItem;
                var __itemHasGetAttribute = firstItem ? typeof firstItem.getAttribute : 'missing';
                var __itemHasItem = firstItem ? typeof firstItem.item : 'missing';
                var __itemTagName = firstItem && firstItem.tagName
                    ? firstItem.tagName
                    : 'missing';
                var __itemId = firstItem && firstItem.getAttribute
                    ? firstItem.getAttribute('id')
                    : 'missing';
                var __manualDone = firstStep.done;
                var __manualValueType = typeof firstStep.value;
                var __manualHasGetAttribute = firstStep.value ? typeof firstStep.value.getAttribute : 'missing';
                var __manualHasItem = firstStep.value ? typeof firstStep.value.item : 'missing';
                var __manualTagName = firstStep.value && firstStep.value.tagName
                    ? firstStep.value.tagName
                    : 'missing';
                var __manualId = firstStep.value && firstStep.value.getAttribute
                    ? firstStep.value.getAttribute('id')
                    : 'missing';
            ");

            Assert.True(
                runtime.GetGlobal("__itemHasGetAttribute").ToString() == "function",
                $"body.type={runtime.GetGlobal("__bodyType")}; body.tagName={runtime.GetGlobal("__bodyTagName")}; direct.type={runtime.GetGlobal("__directItemType")}; direct.tagName={runtime.GetGlobal("__directItemTagName")}; item.type={runtime.GetGlobal("__itemType")}; item.getAttribute={runtime.GetGlobal("__itemHasGetAttribute")}; item.item={runtime.GetGlobal("__itemHasItem")}; item.tagName={runtime.GetGlobal("__itemTagName")}; manual.type={runtime.GetGlobal("__manualValueType")}; manual.getAttribute={runtime.GetGlobal("__manualHasGetAttribute")}; manual.item={runtime.GetGlobal("__manualHasItem")}; manual.tagName={runtime.GetGlobal("__manualTagName")}");
            Assert.Equal("undefined", runtime.GetGlobal("__itemHasItem").ToString());
            Assert.Equal("P", runtime.GetGlobal("__itemTagName").ToString());
            Assert.Equal("1", runtime.GetGlobal("__itemId").ToString());
            Assert.False(runtime.GetGlobal("__manualDone").ToBoolean());
            Assert.Equal("function", runtime.GetGlobal("__manualHasGetAttribute").ToString());
            Assert.Equal("undefined", runtime.GetGlobal("__manualHasItem").ToString());
            Assert.Equal("P", runtime.GetGlobal("__manualTagName").ToString());
            Assert.Equal("1", runtime.GetGlobal("__manualId").ToString());

            runtime.ExecuteSimple(@"
                var paragraphs = document.getElementsByTagName('p');
                var __ids = '';
                var __iterErr = '';
                try {
                    for (const element of paragraphs) {
                        __ids += element.getAttribute('id');
                    }
                } catch (e) {
                    __iterErr = String(e);
                }
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__iterErr").ToString());
            Assert.Equal("123", runtime.GetGlobal("__ids").ToString());

            runtime.ExecuteSimple(@"
                'use strict';
                var paragraphs = document.getElementsByTagName('p');
                var __nestedIds = '';
                var __nestedErr = '';
                (function () {
                    try {
                        for (const element of paragraphs) {
                            __nestedIds += element.getAttribute('id');
                        }
                    } catch (e) {
                        __nestedErr = String(e);
                    }
                })();
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__nestedErr").ToString());
            Assert.Equal("123", runtime.GetGlobal("__nestedIds").ToString());

            runtime.ExecuteSimple(@"
                'use strict';
                const paragraphs = document.getElementsByTagName('p');
                var __arrowIds = '';
                var __arrowErr = '';
                const run = () => {
                    try {
                        for (const element of paragraphs) {
                            __arrowIds += element.getAttribute('id');
                        }
                    } catch (e) {
                        __arrowErr = String(e);
                    }
                };
                run();
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__arrowErr").ToString());
            Assert.Equal("123", runtime.GetGlobal("__arrowIds").ToString());

            runtime.ExecuteSimple(@"
                'use strict';
                function test(fn) { fn(); }
                function assert_equals(actual, expected, message) {
                    if (actual !== expected) {
                        throw new Error(message || ('assert_equals failed: ' + actual + ' !== ' + expected));
                    }
                }
                const paragraphs = document.getElementsByTagName('p');
                var __harnessIds = '';
                var __harnessErr = '';
                try {
                    test(() => {
                        const ids = '123';
                        let idx = 0;
                        for (const element of paragraphs) {
                            __harnessIds += element.getAttribute('id');
                            assert_equals(element.getAttribute('id'), ids[idx++]);
                        }
                    });
                } catch (e) {
                    __harnessErr = String(e);
                }
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__harnessErr").ToString());
            Assert.Equal("123", runtime.GetGlobal("__harnessIds").ToString());

            runtime.ExecuteSimple(@"
                'use strict';
                function test(fn) { fn(); }
                var __stringIndexErr = '';
                var __stringIndexValue = '';
                try {
                    test(() => {
                        const ids = '123';
                        let idx = 0;
                        __stringIndexValue = ids[idx++];
                    });
                } catch (e) {
                    __stringIndexErr = String(e);
                }
            ");

            Assert.Equal(string.Empty, runtime.GetGlobal("__stringIndexErr").ToString());
            Assert.Equal("1", runtime.GetGlobal("__stringIndexValue").ToString());
        }
    }
}
