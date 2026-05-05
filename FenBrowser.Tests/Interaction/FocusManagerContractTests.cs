using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Interaction;
using Xunit;

namespace FenBrowser.Tests.Interaction
{
    public class FocusManagerContractTests
    {
        [Fact]
        public void SetFocus_FocusableElement_BecomesFocused()
        {
            var manager = new FocusManager();
            var input = new Element("input");

            manager.SetFocus(input);

            Assert.Same(input, manager.FocusedElement);
        }

        [Fact]
        public void SetFocus_NonFocusableElement_ClearsFocus()
        {
            var manager = new FocusManager();
            var input = new Element("input");
            var div = new Element("div");

            manager.SetFocus(input);
            manager.SetFocus(div);

            Assert.Null(manager.FocusedElement);
        }

        [Fact]
        public void FindNextFocusable_RespectsTabIndexPriorityThenTreeOrder()
        {
            var manager = new FocusManager();
            var root = new Element("div");

            var link = new Element("a");
            link.SetAttribute("href", "https://example.test");

            var tabFive = new Element("button");
            tabFive.SetAttribute("tabindex", "5");

            var tabTwo = new Element("input");
            tabTwo.SetAttribute("tabindex", "2");

            root.AppendChild(link);    // implicit tabindex (0)
            root.AppendChild(tabFive); // explicit tabindex 5
            root.AppendChild(tabTwo);  // explicit tabindex 2

            var first = manager.FindNextFocusable(root);
            Assert.Same(tabTwo, first);

            manager.SetFocus(first);
            var second = manager.FindNextFocusable(root);
            Assert.Same(tabFive, second);

            manager.SetFocus(second);
            var third = manager.FindNextFocusable(root);
            Assert.Same(link, third);
        }
    }
}
