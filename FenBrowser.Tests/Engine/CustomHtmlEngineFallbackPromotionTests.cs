using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineFallbackPromotionTests
    {
        [Theory]
        [InlineData("PromoteHiddenFallbackContent")]
        [InlineData("ForceGoogleChallengeBannerVisible")]
        [InlineData("RemoveGoogleAccessTroubleBanners")]
        [InlineData("NormalizeNoJsFallbackClasses")]
        [InlineData("RewriteWebPToJpg")]
        public void GenericRenderer_DoesNotContainSiteSpecificDomOrUrlInterventions(string methodName)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.Null(method);
        }

        [Fact]
        public async Task IncrementalRecascade_UpdatesDescendantsAfterAttributeSelectorMutation()
        {
            var baseUri = new Uri("https://attribute-selector.test/");
            var doc = new HtmlParser(
                "<html><head><style>" +
                ".spinner,.checkmark{display:none}" +
                ".checkbox[data-state='loading'] .spinner{display:block}" +
                ".checkbox[data-state='checked'] .checkmark{display:block}" +
                "</style></head><body>" +
                "<button id='checkbox' class='checkbox' data-state='loading'>" +
                "<span id='spinner' class='spinner'></span>" +
                "<span id='checkmark' class='checkmark'></span>" +
                "</button></body></html>",
                baseUri).Parse();
            var root = doc.DocumentElement;
            var checkbox = doc.GetElementById("checkbox");
            var spinner = doc.GetElementById("spinner");
            var checkmark = doc.GetElementById("checkmark");

            using var engine = new CustomHtmlEngine();
            SetPrivateField(engine, "_activeDom", root);
            SetPrivateField(engine, "_activeBaseUri", baseUri);
            SetPrivateField(
                engine,
                "_activeFetchCss",
                new Func<Uri, Task<string>>(_ => Task.FromResult(string.Empty)));

            await engine.RecascadeAsync();
            Assert.Equal("block", engine.LastComputedStyles[spinner].Display);
            Assert.Equal("none", engine.LastComputedStyles[checkmark].Display);

            checkbox.SetAttribute("data-state", "checked");

            var method = typeof(CustomHtmlEngine).GetMethod(
                "IncrementalRecascadeAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await Assert.IsAssignableFrom<Task>(method.Invoke(engine, new object[] { true }));

            Assert.Equal("none", engine.LastComputedStyles[spinner].Display);
            Assert.Equal("block", engine.LastComputedStyles[checkmark].Display);
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }
    }
}
