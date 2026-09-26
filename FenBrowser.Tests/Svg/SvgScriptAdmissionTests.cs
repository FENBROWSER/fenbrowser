using System;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgScriptAdmissionTests
    {
        [Fact]
        public void WptSyncSvgAttributes_FailsClosedInsteadOfPaintingThePreScriptTextPosition()
        {
            // WPT svg/struct/reftests/sync-svg-attributes.svg: a script moves the
            // text of a <use> target from y=100 to y=20. The static tree alone
            // paints it at y=100, which is not the document the script produces, so
            // the renderer must refuse rather than report a successful pre-script
            // frame. The same document is what the WPT reference comparison catches.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='500' height='200' " +
                "class='reftest-wait'><g id='g'><text y='100' font-size='20'>overlap</text></g>" +
                "<use href='#g' fill='blue'/>" +
                "<script>document.querySelector('#g > text').setAttribute('y', '20');" +
                "document.documentElement.classList.remove('reftest-wait');</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void WptScriptedStyleAndReferenceMutation_FailsClosed()
        {
            // WPT svg/geometry/reftests/circle-005.svg sets circle.style.r from
            // script, and svg/struct/reftests/use-image-href-mutating.svg sets an
            // image href. Neither mutation is reachable from the static tree, so a
            // style-driven geometry channel and an href channel are both refused.
            foreach (string document in new[]
            {
                "<svg width='340' height='140' xmlns='http://www.w3.org/2000/svg'>" +
                "<style>circle { cx: 204px; cy: 56px; r: 5px; fill: blue; }</style>" +
                "<circle/><script><![CDATA[" +
                "let circle = document.querySelector('circle');" +
                "circle.parentNode.style.display = 'none';" +
                "circle.getTotalLength();" +
                "circle.parentNode.style.display = '';" +
                "circle.style.r = '65px';" +
                "]]></script></svg>",
                "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='200'>" +
                "<defs><image id='target' width='100' height='50'/></defs>" +
                "<use x='25' y='25' href='#target'/>" +
                "<script>document.getElementById('target')" +
                ".setAttribute('href', '/images/green-100x50.png');</script></svg>"
            })
            {
                using var result = new FenSvgRenderer().Render(document);

                AssertFailsClosed(result);
                Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void WptScriptedPaintServerAndStyleInvalidation_FailClosed()
        {
            // WPT svg/pservers/scripted/*-transform-clear.svg removes a transform
            // attribute from a paint server, svg/styling/invalidation/nth-child-of-
            // class.svg mutates a class, and svg/text/reftests/lang-attribute-dynamic
            // .svg sets lang. All three rewrite a paint input the static tree still
            // holds, so none of them may be reported as a supported render.
            foreach (string document in new[]
            {
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<defs><linearGradient id='g' gradientUnits='userSpaceOnUse' " +
                "gradientTransform='rotate(45)'><stop offset='0' stop-color='red'/>" +
                "<stop offset='1' stop-color='lime'/></linearGradient></defs>" +
                "<rect width='100' height='100' fill='url(#g)'/>" +
                "<script>document.querySelector('linearGradient')" +
                ".removeAttribute('gradientTransform');</script></svg>",
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<style>rect { fill: red } rect.special { fill: lime }</style>" +
                "<rect width='100' height='100'/>" +
                "<script>document.querySelector('rect').classList.add('special');</script></svg>",
                "<svg width='200' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<text x='10' y='50' font-size='20'>hi</text>" +
                "<script>document.querySelector('text').setAttribute('lang', 'ja');</script></svg>"
            })
            {
                using var result = new FenSvgRenderer().Render(document);

                AssertFailsClosed(result);
                Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void WptImageHrefMutationWithLoadHandler_FailsClosed()
        {
            // WPT svg/embedded/image-modify-href-1.svg and image-remove-href-1.svg
            // pair an onload handler with a script that rewrites the image href. The
            // script element is the signal, and the pair needs only one report.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 4 4' " +
                "width='200' height='200' class='reftest-wait'>" +
                "<image width='2' height='2' onload='test()' " +
                "href='data:image/png;base64," +
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK6QAAAAASUVORK5CYII='/>" +
                "<script href='/common/reftest-wait.js'></script>" +
                "<script>async function test() {" +
                "document.querySelector('image').removeAttribute('href');}</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }


        [Fact]
        public void ScriptElementInsideReferencedOnlySubtrees_StillFailsClosed()
        {
            // A script element is refused wherever the parser interprets markup: the
            // draw walk never visits <defs> or <symbol> children, so a script parked
            // there would otherwise be the one place a single-pass render could
            // reach a static frame the script had rewritten.
            foreach (string document in new[]
            {
                "<svg width='20' height='20'><defs><script>document.body.innerHTML = '';" +
                "</script></defs><rect width='20' height='20' fill='green'/></svg>",
                "<svg width='20' height='20'><symbol id='s'><script>run();</script>" +
                "<rect width='20' height='20' fill='green'/></symbol>" +
                "<use href='#s'/></svg>",
                "<svg width='20' height='20'><g><title>t</title><script>run();</script></g>" +
                "<rect width='20' height='20' fill='green'/></svg>"
            })
            {
                using var result = new FenSvgRenderer().Render(document);

                AssertFailsClosed(result);
                Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void ScriptInsideARawSkippedSubtree_StaysInertAndDoesNotCondemnTheDocument()
        {
            // The parser consumes these subtrees raw, so markup inside them is never
            // interpreted, never a child element, and never contributes paint. A
            // script there is therefore not script the renderer declined to run: it
            // is content the renderer never read, and the document's paint really is
            // the static tree.
            foreach (string container in new[] { "metadata", "desc", "title" })
            {
                string document =
                    "<svg width='20' height='20'>" +
                    $"<{container}><script>run();</script></{container}>" +
                    "<rect width='20' height='20' fill='green'/></svg>";

                using var result = new FenSvgRenderer().Render(document);

                Assert.True(result.Success, document + ": " + result.ErrorMessage);
                Assert.False(
                    result.RequiresFallback,
                    document + ": " + string.Join("; ", result.Warnings));
                Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
            }
        }

        [Fact]
        public void NamespacedAndUppercaseScriptElements_AreAdmittedAsScriptSignals()
        {
            // The parser resolves a namespaced script to its local name and leaves a
            // non-namespaced one with its authored case, so admission matches the
            // local name instead of the spelled one.
            foreach (string name in new[]
            {
                "script", "SCRIPT", "ScRiPt",
                "svg:script", "h:script", "html:script", "foo:script"
            })
            {
                string svg =
                    "<svg xmlns='http://www.w3.org/2000/svg' " +
                    "xmlns:svg='http://www.w3.org/2000/svg' " +
                    "xmlns:h='http://www.w3.org/1999/xhtml' " +
                    "xmlns:html='http://www.w3.org/1999/xhtml' " +
                    "xmlns:foo='http://www.w3.org/2000/svg' width='20' height='20'>" +
                    $"<{name}>run();</{name}>" +
                    "<rect width='20' height='20' fill='green'/></svg>";

                using var result = new FenSvgRenderer().Render(svg);

                AssertFailsClosed(result);
                Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void HandlerAttributesAlone_AreNotScriptSignals()
        {
            // An on* content attribute is code, but code is compiled by a script
            // engine and this renderer has none. With exactly one frame there is no
            // pre-script frame to withhold, so the static tree really is the whole
            // document. WPT svg/struct/reftests/currentScale.svg is the case that
            // matters: the handler body is supplied by a sibling script element, and
            // that script element is what the admission rule already refuses.
            foreach (string document in new[]
            {
                "<svg width='40' height='40' onload='scaleDown()'>" +
                "<rect width='40' height='40' fill='green'/></svg>",
                "<svg width='40' height='40' ONLOAD='scaleDown()'>" +
                "<rect width='40' height='40' fill='green'/></svg>",
                "<svg width='40' height='40'><circle cx='20' cy='20' r='10' " +
                "onclick='redraw()'/></svg>",
                "<svg width='40' height='40'><g onmouseover='redraw()'>" +
                "<rect width='40' height='40' fill='green'/></g></svg>"
            })
            {
                using var result = new FenSvgRenderer().Render(document);

                Assert.True(result.Success, document + ": " + result.ErrorMessage);
                Assert.False(
                    result.RequiresFallback,
                    document + ": " + string.Join("; ", result.Warnings));
                Assert.DoesNotContain("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void HandlerAttributesAlone_DoNotMaskAScriptElement()
        {
            // Dropping the on* clause must not weaken the script clause: the same
            // document with a script element is still refused, and the handler
            // attribute on the root changes nothing about that verdict.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40' onload='scaleDown()'>" +
                "<rect width='40' height='40' fill='green'/>" +
                "<script>function scaleDown(){}</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            Assert.Contains(SvgFeatureSupport.ScriptElementReason, result.Warnings);
        }

        [Fact]
        public void EmptyHandlerAttributes_AreNotScriptSignals()
        {
            // An empty handler attribute carries no code, so the document's static
            // tree really is the whole document and nothing is being concealed.
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' onload='' onclick='  '>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void TimingElementHandlerAttributes_StillFailClosed()
        {
            // The timing branch is a separate rule with its own justification: an
            // animation element carrying an event handler is a scripted document
            // whatever the surrounding markup says, and it reports the specific
            // animate-handler reason rather than the generic script one.
            foreach (string elementName in new[]
            {
                "animate", "animateColor", "animateMotion", "animateTransform", "set"
            })
            {
                using var result = new FenSvgRenderer().Render(
                    "<svg width='40' height='40'><rect width='40' height='40' fill='green'>" +
                    $"<{elementName} attributeName='x' from='0' to='9' dur='1s' " +
                    "onbegin='f()'/></rect></svg>");

                AssertFailsClosed(result);
                Assert.Contains("smil-animation", result.FallbackReasonCodes);
                Assert.Contains(result.Warnings, warning =>
                    warning.Contains(
                        $"SVG animate event handler attribute 'onbegin'", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void OnlyTimingElementHandlerNames_AreAdmittedAsTimingHandlers()
        {
            // The timing branch is a fixed name list, not an "on" prefix rule: a
            // shape that merely spells an attribute onward stays an ordinary
            // attribute, and an unlisted handler name on a timing element does not
            // earn the animate-handler reason on top of the SMIL one.
            using var handlerShaped = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><path id='one' d='M0 0 H20' " +
                "onward='paint' on='x'/><rect width='20' height='20' fill='green'/></svg>");

            Assert.True(handlerShaped.Success, handlerShaped.ErrorMessage);
            Assert.False(handlerShaped.RequiresFallback, string.Join("; ", handlerShaped.Warnings));

            using var unlistedTimingHandler = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'>" +
                "<animate attributeName='x' to='9' dur='1s' onwhatever='f()'/></rect></svg>");

            AssertFailsClosed(unlistedTimingHandler);
            Assert.Equal(
                new[] { "smil-animation" }, unlistedTimingHandler.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("application/noSuchLanguage")]
        [InlineData("application/noSuchLanguage; charset=utf-8")]
        [InlineData("text/plain")]
        [InlineData("text/html")]
        [InlineData("importmap")]
        [InlineData("")]
        [InlineData("text/javascriptish")]
        [InlineData("javascript")]
        public void ScriptElementWithANonJavaScriptType_IsADataBlockAndRenders(string type)
        {
            // WPT svg/import/script-specify-02-f-manual.svg spells exactly this:
            // <script type="application/noSuchLanguage"> is a data block the HTML
            // scripting model never compiles, so no engine ever runs it and the
            // document has no post-script frame to withhold.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                $"<script type='{type}'>run();</script>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("dynamic-content", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Theory]
        [InlineData("text/javascript")]
        [InlineData("TEXT/JAVASCRIPT")]
        [InlineData("  text/javascript  ")]
        [InlineData("text/javascript; charset=utf-8")]
        [InlineData("application/ecmascript")]
        [InlineData("application/javascript")]
        [InlineData("application/x-ecmascript")]
        [InlineData("application/x-javascript")]
        [InlineData("text/ecmascript")]
        [InlineData("text/javascript1.5")]
        [InlineData("text/jscript")]
        [InlineData("text/livescript")]
        [InlineData("text/x-ecmascript")]
        [InlineData("text/x-javascript")]
        [InlineData("module")]
        [InlineData("MODULE")]
        [InlineData("module; charset=utf-8")]
        [InlineData("TYPE='text/javascript'")]
        [InlineData("type='text/javascript'")]
        public void ScriptElementNamingAJavaScriptType_StillFailsClosed(string type)
        {
            // The gate only opens for a type that provably never compiles. Every
            // spelling an engine does compile - including the case-insensitive
            // attribute name, the case-insensitive value, and the parameter-carrying
            // form - must keep the refusal.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                $"<script {type}>run();</script>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void ScriptElementWithoutATypeAttribute_StillFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><script>run();</script>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void OperatorScriptElement_IsNotAScriptElement()
        {
            // <d:operatorScript> is SVG 1.1 test-suite boilerplate, not script, and
            // it occurs in hundreds of corpus documents. The name is 13 characters
            // after the prefix, so a length-bounded local-name match cannot confuse
            // the two, and an authored element name is never read as a policy
            // signal: the document is still refused, as an unknown element.
            foreach (string name in new[] { "operatorScript", "d:operatorScript" })
            {
                string document =
                    "<svg xmlns='http://www.w3.org/2000/svg' " +
                    "xmlns:d='http://www.w3.org/2000/02/svg/testsuite/description/' " +
                    "width='20' height='20'>" +
                    $"<{name}>x</{name}>" +
                    "<rect width='20' height='20' fill='green'/></svg>";

                using var result = new FenSvgRenderer().Render(document);

                AssertFailsClosed(result);
                Assert.Contains("unsupported-element", result.FallbackReasonCodes);
                Assert.DoesNotContain("dynamic-content", result.FallbackReasonCodes);
            }
        }

        [Fact]
        public void ScriptSubtreeContent_StaysInertAndIsNeverExposed()
        {
            // Refusing the document must not change how the body is handled: the
            // script subtree is still consumed raw, so its text is never character
            // data, never a child of the script element, and never re-emitted.
            const string source =
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                "<script>visible = 'text'; if (a &lt; b) c();</script>" +
                "<rect width='20' height='20' fill='green'/></svg>";

            bool parsed = SvgMarkupParser.TryParse(
                source, SvgRenderLimits.Default, out var document, out string fatal);

            Assert.True(parsed, fatal);
            var script = document.Root.Children[0];
            Assert.Equal("script", script.Name);
            Assert.Null(script.TextContent);
            Assert.Empty(script.Children);
            Assert.Empty(script.Content);
            Assert.Equal("rect", document.Root.Children[1].Name);
        }

        [Fact]
        public void DoctypeEntityCannotMaterializeAScriptElement()
        {
            // WPT svg/struct/reftests/* inject markup through a declared entity. The
            // injected script is consumed inside the raw <desc> subtree, so it never
            // becomes an element the admission stage has to judge - and the document
            // still renders, which is what "inert" has to mean for it.
            const string source =
                "<!DOCTYPE svg [<!ENTITY e \"</desc><script>alert(1)</script>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                "<desc>&e;</desc><rect width='20' height='20' fill='green'/></svg>";

            bool parsed = SvgMarkupParser.TryParse(
                source, SvgRenderLimits.Default, out var document, out string fatal);

            Assert.True(parsed, fatal);
            Assert.DoesNotContain(document.Root.Children, child => child.Name == "script");
            Assert.Equal("desc", document.Root.Children[0].Name);
            Assert.Equal("rect", document.Root.Children[1].Name);

            using var result = new FenSvgRenderer().Render(source);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void ScriptAndSecurityRejections_AreReportedTogether()
        {
            // Refusing a scripted document must not suppress the sandbox verdicts
            // the same document earns, and a security rejection must not suppress
            // the script refusal: both are routing signals a caller needs.
            using var scriptedExternal = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><style>rect { fill: url(https://x.test/a) }" +
                "</style><script>run();</script><rect width='20' height='20'/></svg>");

            AssertFailsClosed(scriptedExternal);
            Assert.Contains("dynamic-content", scriptedExternal.FallbackReasonCodes);
            Assert.Contains("external-resource", scriptedExternal.ResourceRejectionReasonCodes);

            using var entityInjectedScript = new FenSvgRenderer().Render(
                "<!DOCTYPE svg [<!ENTITY q '<script>injected</script>'>]>" +
                "<svg width='20' height='20'><desc>&q;</desc>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(entityInjectedScript.Success, entityInjectedScript.ErrorMessage);
            Assert.False(entityInjectedScript.RequiresFallback);
        }

        [Fact]
        public void ScriptedDocumentWithoutAnyRenderableShape_FailsClosed()
        {
            // The refusal is a document property, not a paint decision: a document
            // whose only element would have painted nothing is refused on exactly
            // the same ground as one that would have painted a full frame.
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><script>document.title = 'x';</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void AdmissionDiagnostic_ReplacesThePaintStageWording()
        {
            // The document is refused with the admission diagnostic, and the reason
            // the paint stage used to invent is gone: there is no second, later
            // verdict competing with the earliest-stage one.
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='40' onload='scaleDown()'>" +
                "<rect width='40' height='40' fill='green'/>" +
                "<script>function scaleDown(){}</script></svg>");

            AssertFailsClosed(result);
            Assert.Contains(SvgFeatureSupport.ScriptElementReason, result.Warnings);
            Assert.All(result.Warnings, warning =>
                Assert.DoesNotContain(
                    "combines a script element with an event-handler attribute",
                    warning,
                    StringComparison.Ordinal));
            Assert.All(result.Warnings, warning =>
                Assert.DoesNotContain(
                    "is authored for a scripted document",
                    warning,
                    StringComparison.Ordinal));
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
