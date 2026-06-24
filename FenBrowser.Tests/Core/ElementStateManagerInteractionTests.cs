using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Core
{
    public class ElementStateManagerInteractionTests
    {
        [Fact]
        public void HoverStateChange_RequestsOneFullRepaint()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var element = new Element("a");

            manager.SetHoveredElement(element);

            Assert.True(manager.ConsumeFullRepaintRequest());
            Assert.False(manager.ConsumeFullRepaintRequest());
        }

        [Fact]
        public void RepeatingSameHoverTarget_DoesNotQueueAnotherFullRepaint()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var element = new Element("a");

            manager.SetHoveredElement(element);
            Assert.True(manager.ConsumeFullRepaintRequest());

            manager.SetHoveredElement(element);

            Assert.False(manager.ConsumeFullRepaintRequest());
        }
    }
}
