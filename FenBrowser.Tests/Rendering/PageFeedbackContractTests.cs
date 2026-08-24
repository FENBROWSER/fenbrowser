using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class PageFeedbackContractTests
    {
        [Fact]
        public void UpdateTextCaret_PublishesElementAndOffset()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var input = new Element("input");

            manager.UpdateTextCaret(input, 3);
            Assert.Same(input, manager.CaretElement);
            Assert.Equal(3, manager.CaretOffset);

            manager.UpdateTextCaret(input, 5);
            Assert.Equal(5, manager.CaretOffset);
        }

        [Fact]
        public void UpdateTextCaret_NullElement_ClearsCaret()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var input = new Element("textarea");

            manager.UpdateTextCaret(input, 2);
            manager.UpdateTextCaret(null, 0);

            Assert.Null(manager.CaretElement);
            Assert.Equal(0, manager.CaretOffset);
        }

        [Fact]
        public void UpdateTextCaret_NegativeOffset_ClampsToZero()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var input = new Element("input");

            manager.UpdateTextCaret(input, -4);

            Assert.Equal(0, manager.CaretOffset);
        }

        [Fact]
        public void UpdateTextCaret_ChangeRefreshesBlinkPhaseAnchor()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var input = new Element("input");

            manager.UpdateTextCaret(input, 0);
            var first = manager.CaretLastChangeUtc;

            manager.UpdateTextCaret(input, 1);

            Assert.True(manager.CaretLastChangeUtc >= first);
        }

        [Fact]
        public void HoverDescendantWithNotClass_MatchesHoveredAncestorChain()
        {
            ElementStateManager.Reset();
            var document = new HtmlParser(@"
<!doctype html>
<html><body>
    <button id='ai' class='plR5qb'><span id='pill' class='bvUkz'></span></button>
    <button id='ai2' class='plR5qb PHjFye'><span id='pill2' class='bvUkz'></span></button>
</body></html>").Parse();
            var elements = document.Descendants().OfType<Element>().ToArray();
            var hovered = elements.Single(element => element.Id == "ai");
            var matchingPill = elements.Single(element => element.Id == "pill");
            var excludedPill = elements.Single(element => element.Id == "pill2");

            ElementStateManager.Instance.SetHoveredElement(hovered);

            Assert.True(SelectorMatcher.Matches(matchingPill, ".plR5qb:not(.PHjFye):hover .bvUkz"));
            Assert.False(SelectorMatcher.Matches(excludedPill, ".plR5qb:not(.PHjFye):hover .bvUkz"));

            ElementStateManager.Instance.SetHoveredElement(null);
            Assert.False(SelectorMatcher.Matches(matchingPill, ".plR5qb:not(.PHjFye):hover .bvUkz"));
        }

    }
}
