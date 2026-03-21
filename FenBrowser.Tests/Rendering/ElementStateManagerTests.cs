using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class ElementStateManagerTests
    {
        [Fact]
        public void HoverStateChange_DoesNotQueueFullRepaint()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var element = new Element("div");

            manager.SetHoveredElement(element);

            Assert.False(manager.ConsumeFullRepaintRequest());
        }

        [Fact]
        public void ActiveStateChange_RequestsOneFullRepaint()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var element = new Element("div");

            manager.SetActiveElement(element);

            Assert.True(manager.ConsumeFullRepaintRequest());
            Assert.False(manager.ConsumeFullRepaintRequest());
        }

        [Fact]
        public void RepeatingSameActiveTarget_DoesNotQueueAnotherFullRepaint()
        {
            ElementStateManager.Reset();
            var manager = ElementStateManager.Instance;
            var element = new Element("div");

            manager.SetActiveElement(element);
            Assert.True(manager.ConsumeFullRepaintRequest());

            manager.SetActiveElement(element);

            Assert.False(manager.ConsumeFullRepaintRequest());
        }
    }
}
