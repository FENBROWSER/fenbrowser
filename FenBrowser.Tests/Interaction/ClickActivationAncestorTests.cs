using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Interaction
{
    /// <summary>
    /// Coverage for the activation-ancestor walk added to
    /// <see cref="BrowserHost.HandleElementClick"/>.
    ///
    /// The bug it fixes: hit-testing returns the deepest hit descendant of the
    /// pointer (per WHATWG HTML), so clicking a non-activating child of a
    /// <c>&lt;button&gt;</c>, <c>&lt;a&gt;</c>, or labelled control would land
    /// on that child instead of the activation-capable ancestor and silently do
    /// nothing. The Google Search button is the field repro: its hit target is
    /// the inner content node, not the <c>&lt;button type=submit&gt;</c>.
    ///
    /// Two layers of coverage:
    ///   1. The activation-ancestor helper itself (private static, reflected).
    ///   2. An end-to-end click on a descendant of an in-document anchor that
    ///      navigates by fragment (no network), proving the wiring inside
    ///      <c>HandleElementClick</c> actually consumes the ancestor result.
    /// </summary>
    [Collection("Engine Tests")]
    public class ClickActivationAncestorTests
    {
        [Fact]
        public void FindActivationAncestor_FromSpanInsideButton_ReturnsButton()
        {
            var form = new Element("form");
            var button = new Element("button");
            button.SetAttribute("type", "submit");
            var span = new Element("span");
            form.AppendChild(button);
            button.AppendChild(span);

            var result = InvokeFindActivationAncestor(span);

            Assert.NotNull(result);
            Assert.Equal("BUTTON", result!.TagName);
        }

        [Fact]
        public void FindActivationAncestor_FromDeeplyNestedDescendant_ReturnsButton()
        {
            // <button> <span><svg><g><rect/></g></svg></span> </button>
            var button = new Element("button");
            var span = new Element("span");
            var svg = new Element("svg");
            var g = new Element("g");
            var rect = new Element("rect");

            button.AppendChild(span);
            span.AppendChild(svg);
            svg.AppendChild(g);
            g.AppendChild(rect);

            var result = InvokeFindActivationAncestor(rect);

            Assert.NotNull(result);
            Assert.Equal("BUTTON", result!.TagName);
        }

        [Fact]
        public void FindActivationAncestor_FromAnchorDescendant_ReturnsAnchor()
        {
            var anchor = new Element("a");
            anchor.SetAttribute("href", "/target");
            var inner = new Element("span");
            anchor.AppendChild(inner);

            var result = InvokeFindActivationAncestor(inner);

            Assert.NotNull(result);
            Assert.Equal("A", result!.TagName);
        }

        [Fact]
        public void FindActivationAncestor_AnchorWithoutHref_IsNotActivationTarget()
        {
            // <a> (no href) <span/> </a> — anchors without href have no
            // activation behavior, so a click on the span has no ancestor to
            // promote to; result should be null and the caller falls back to
            // the descendant-walk legacy path.
            var anchor = new Element("a");
            var inner = new Element("span");
            anchor.AppendChild(inner);

            var result = InvokeFindActivationAncestor(inner);

            Assert.Null(result);
        }

        [Fact]
        public void FindActivationAncestor_FromInputType_Text_IsNotActivationTarget()
        {
            // <input type=text> — clicks focus, they do not activate. The
            // ancestor walk must NOT return text inputs as activation targets,
            // otherwise the legacy focus path would be skipped.
            var input = new Element("input");
            input.SetAttribute("type", "text");

            var result = InvokeFindActivationAncestor(input);

            Assert.Null(result);
        }

        [Fact]
        public void FindActivationAncestor_FromDisabledButton_IsNotActivationTarget()
        {
            // Disabled controls do not run activation behavior, even when the
            // hit target is their descendant.
            var button = new Element("button");
            button.SetAttribute("disabled", "");
            var span = new Element("span");
            button.AppendChild(span);

            var result = InvokeFindActivationAncestor(span);

            Assert.Null(result);
        }

        [Fact]
        public async Task HandleElementClick_OnSpanInsideAnchor_NavigatesToHref()
        {
            // End-to-end: hit-test of a span child of <a href="#fragment"> must
            // resolve to the anchor and execute its activation behavior
            // (fragment navigation, which is in-process so no network needed).
            var host = new BrowserHost();
            SetCurrentUri(host, new Uri("about:blank"));

            var anchor = new Element("a");
            anchor.SetAttribute("href", "#section-2");
            var span = new Element("span");
            anchor.AppendChild(span);

            await host.HandleElementClick(span);

            Assert.NotNull(host.CurrentUri);
            Assert.Equal("about:blank#section-2", host.CurrentUri!.AbsoluteUri);
        }

        private static Element? InvokeFindActivationAncestor(Element start)
        {
            var method = typeof(BrowserHost).GetMethod(
                "FindActivationAncestor",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method!.Invoke(null, new object[] { start }) as Element;
        }

        private static void SetCurrentUri(BrowserHost host, Uri uri)
        {
            var currentField = typeof(BrowserHost).GetField(
                "_current",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(currentField);
            currentField!.SetValue(host, uri);
        }
    }
}
