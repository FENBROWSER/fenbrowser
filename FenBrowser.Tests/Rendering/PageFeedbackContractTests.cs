using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
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

    }
}
