using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class BrowserApiHoverNormalizationTests
    {
        private static readonly MethodInfo NormalizeHoverTargetMethod =
            typeof(BrowserHost).GetMethod("NormalizeHoverTarget", BindingFlags.NonPublic | BindingFlags.Static);

        [Fact]
        public void NormalizeHoverTarget_StripsPassiveContainerHover()
        {
            var container = new Element("div");

            var normalized = InvokeNormalizeHoverTarget(container);

            Assert.Null(normalized);
        }

        [Fact]
        public void NormalizeHoverTarget_KeepsInteractiveAnchorHover()
        {
            var link = new Element("a");
            link.SetAttribute("href", "https://iana.org/domains/example");

            var normalized = InvokeNormalizeHoverTarget(link);

            Assert.Same(link, normalized);
        }

        private static Element InvokeNormalizeHoverTarget(Element element)
        {
            Assert.NotNull(NormalizeHoverTargetMethod);
            return (Element)NormalizeHoverTargetMethod.Invoke(null, new object[] { element });
        }
    }
}
