using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    [Collection("Engine Tests")]
    public class FormControlActivationTests
    {
        [Fact]
        public async Task HandleElementClick_Checkbox_TogglesChecked()
        {
            var host = new BrowserHost();
            var checkbox = new Element("input");
            checkbox.SetAttribute("type", "checkbox");

            await host.HandleElementClick(checkbox);
            Assert.True(ElementStateManager.Instance.IsChecked(checkbox));
            Assert.False(checkbox.HasAttribute("checked"));

            await host.HandleElementClick(checkbox);
            Assert.False(ElementStateManager.Instance.IsChecked(checkbox));
            Assert.False(checkbox.HasAttribute("checked"));
        }

        [Fact]
        public async Task HandleElementClick_Radio_SelectsOnlyOneNamedFormControl()
        {
            var host = new BrowserHost();
            var form = new Element("form");
            var first = new Element("input");
            var second = new Element("input");

            first.SetAttribute("type", "radio");
            first.SetAttribute("name", "choice");
            first.SetAttribute("checked", string.Empty);
            second.SetAttribute("type", "radio");
            second.SetAttribute("name", "choice");
            form.AppendChild(first);
            form.AppendChild(second);

            await host.HandleElementClick(second);

            Assert.False(ElementStateManager.Instance.IsChecked(first));
            Assert.True(ElementStateManager.Instance.IsChecked(second));
            Assert.True(first.HasAttribute("checked"));
            Assert.False(second.HasAttribute("checked"));

            await host.HandleElementClick(second);
            Assert.True(ElementStateManager.Instance.IsChecked(second));
            Assert.False(second.HasAttribute("checked"));
        }

        [Fact]
        public async Task HandleElementClick_LabelWithNestedCheckbox_ActivatesControl()
        {
            var host = new BrowserHost();
            var label = new Element("label");
            var checkbox = new Element("input");
            checkbox.SetAttribute("type", "checkbox");
            label.AppendChild(checkbox);
            label.AppendChild(new Text("Checkbox input"));

            await host.HandleElementClick(label);

            Assert.True(ElementStateManager.Instance.IsChecked(checkbox));
            Assert.False(checkbox.HasAttribute("checked"));
        }
    }
}
