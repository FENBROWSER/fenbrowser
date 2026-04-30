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
                const paragraphsForArrow = document.getElementsByTagName('p');
                var __arrowIds = '';
                var __arrowErr = '';
                const run = () => {
                    try {
                        for (const element of paragraphsForArrow) {
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
                const paragraphsForHarness = document.getElementsByTagName('p');
                var __harnessIds = '';
                var __harnessErr = '';
                try {
                    test(() => {
                        const ids = '123';
                        let idx = 0;
                        for (const element of paragraphsForHarness) {
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

        [Fact]
        public void FormAndSelectCollections_ExposeHtmlSpecificInterfaces_And_RadioNodeListValue()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var form = document.createElement('form');
                var first = document.createElement('input');
                first.type = 'radio';
                first.name = 'group';
                first.value = 'one';

                var second = document.createElement('input');
                second.type = 'radio';
                second.name = 'group';
                second.value = 'two';

                form.appendChild(first);
                form.appendChild(second);
                document.body.appendChild(form);

                var select = document.createElement('select');
                var optA = new Option('A', 'a');
                var optB = new Option('B', 'b');
                select.appendChild(optA);
                select.appendChild(optB);
                document.body.appendChild(select);

                var controls = form.elements;
                var named = controls.namedItem('group');
                second.checked = true;

                globalThis.__hasHtmlFormControlsCollectionCtor = typeof HTMLFormControlsCollection;
                globalThis.__hasHtmlOptionsCollectionCtor = typeof HTMLOptionsCollection;
                globalThis.__hasRadioNodeListCtor = typeof RadioNodeList;

                globalThis.__controlsInstanceof = String(controls instanceof HTMLFormControlsCollection);
                globalThis.__namedInstanceofRadioNodeList = String(named instanceof RadioNodeList);
                globalThis.__indexedRadioCount = String(named.length);
                globalThis.__radioValue = named.value;
                globalThis.__firstChecked = String(first.checked);
                globalThis.__secondChecked = String(second.checked);

                globalThis.__optionsInstanceof = String(select.options instanceof HTMLOptionsCollection);
                globalThis.__optionsLength = String(select.options.length);
            ");

            Assert.Equal("function", runtime.GetGlobal("__hasHtmlFormControlsCollectionCtor").ToString());
            Assert.Equal("function", runtime.GetGlobal("__hasHtmlOptionsCollectionCtor").ToString());
            Assert.Equal("function", runtime.GetGlobal("__hasRadioNodeListCtor").ToString());
            Assert.Equal("true", runtime.GetGlobal("__controlsInstanceof").ToString());
            Assert.Equal("true", runtime.GetGlobal("__namedInstanceofRadioNodeList").ToString());
            Assert.Equal("2", runtime.GetGlobal("__indexedRadioCount").ToString());
            Assert.Equal("two", runtime.GetGlobal("__radioValue").ToString());
            Assert.Equal("false", runtime.GetGlobal("__firstChecked").ToString());
            Assert.Equal("true", runtime.GetGlobal("__secondChecked").ToString());
            Assert.Equal("true", runtime.GetGlobal("__optionsInstanceof").ToString());
            Assert.Equal("2", runtime.GetGlobal("__optionsLength").ToString());
        }

        [Fact]
        public void DocumentCollections_And_GetElementsByName_ExposeExpectedHtmlApis()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var img = document.createElement('img');
                img.id = 'hero';
                document.body.appendChild(img);

                var form = document.createElement('form');
                form.id = 'checkout-form';
                form.name = 'checkout';
                var namedA = document.createElement('input');
                namedA.name = 'group';
                var namedB = document.createElement('input');
                namedB.name = 'group';
                form.appendChild(namedA);
                form.appendChild(namedB);
                document.body.appendChild(form);

                var script = document.createElement('script');
                script.src = '/app.js';
                document.body.appendChild(script);

                var embed = document.createElement('embed');
                embed.id = 'plugin';
                document.body.appendChild(embed);

                var applet = document.createElement('applet');
                applet.name = 'legacy';
                document.body.appendChild(applet);

                var namedAnchor = document.createElement('a');
                namedAnchor.name = 'toc';
                namedAnchor.href = '#toc';
                document.body.appendChild(namedAnchor);

                var regularAnchor = document.createElement('a');
                regularAnchor.href = '#regular';
                document.body.appendChild(regularAnchor);

                var byName = document.getElementsByName('group');

                globalThis.__imagesLength = String(document.images.length);
                globalThis.__formsLength = String(document.forms.length);
                globalThis.__scriptsLength = String(document.scripts.length);
                globalThis.__embedsLength = String(document.embeds.length);
                globalThis.__pluginsLength = String(document.plugins.length);
                globalThis.__appletsLength = String(document.applets.length);
                globalThis.__anchorsLength = String(document.anchors.length);
                globalThis.__allLength = String(document.all.length);
                globalThis.__imagesNamed = String(document.images.namedItem('hero') === img);
                globalThis.__formsNamed = String(document.forms.namedItem('checkout-form') === form);
                globalThis.__getByNameLength = String(byName.length);
                globalThis.__getByNameFirst = byName.item(0) ? byName.item(0).name : '';
            ");

            Assert.Equal("1", runtime.GetGlobal("__imagesLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__formsLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__scriptsLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__embedsLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__pluginsLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__appletsLength").ToString());
            Assert.Equal("1", runtime.GetGlobal("__anchorsLength").ToString());
            Assert.True(runtime.GetGlobal("__allLength").ToNumber() >= 9);
            Assert.Equal("true", runtime.GetGlobal("__imagesNamed").ToString());
            Assert.Equal("true", runtime.GetGlobal("__formsNamed").ToString());
            Assert.Equal("2", runtime.GetGlobal("__getByNameLength").ToString());
            Assert.Equal("group", runtime.GetGlobal("__getByNameFirst").ToString());
        }

        [Fact]
        public void DocumentStyleSheets_ExposesInlineAndLinkedStylesheetEntries()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                style.textContent = 'body { color: red; }';
                document.head.appendChild(style);

                var link = document.createElement('link');
                link.setAttribute('rel', 'stylesheet preload');
                link.setAttribute('href', '/assets/site.css');
                document.head.appendChild(link);

                var sheets = document.styleSheets;
                var inlineSheet = sheets.item(0);
                var linkedSheet = sheets.item(1);

                globalThis.__styleSheetsLength = String(sheets.length);
                globalThis.__inlineOwnerTag = inlineSheet && inlineSheet.ownerNode ? inlineSheet.ownerNode.tagName : '';
                globalThis.__inlineRulesLength = inlineSheet && inlineSheet.cssRules ? String(inlineSheet.cssRules.length) : '';
                globalThis.__linkedOwnerTag = linkedSheet && linkedSheet.ownerNode ? linkedSheet.ownerNode.tagName : '';
                globalThis.__linkedHref = linkedSheet && linkedSheet.href ? String(linkedSheet.href) : '';
                globalThis.__linkedRulesLength = linkedSheet && linkedSheet.cssRules ? String(linkedSheet.cssRules.length) : '';
                globalThis.__missingSheetIsNull = String(sheets.item(10) === null);
            ");

            Assert.Equal("2", runtime.GetGlobal("__styleSheetsLength").ToString());
            Assert.Equal("STYLE", runtime.GetGlobal("__inlineOwnerTag").ToString());
            Assert.Equal("1", runtime.GetGlobal("__inlineRulesLength").ToString());
            Assert.Equal("LINK", runtime.GetGlobal("__linkedOwnerTag").ToString());
            Assert.Contains("/assets/site.css", runtime.GetGlobal("__linkedHref").ToString());
            Assert.Equal("0", runtime.GetGlobal("__linkedRulesLength").ToString());
            Assert.Equal("true", runtime.GetGlobal("__missingSheetIsNull").ToString());
        }
    }
}
