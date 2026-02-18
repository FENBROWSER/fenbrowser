using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BrowserHostFormSubmissionTests
    {
        [Fact]
        public void CollectFormSubmissionEntries_UsesTextareaQueryAndSkipsFileInputs()
        {
            var form = new Element("form");
            form.SetAttribute("method", "GET");
            form.SetAttribute("action", "/search");

            var fileInput = new Element("input");
            fileInput.SetAttribute("type", "file");
            fileInput.SetAttribute("name", "upload");
            fileInput.SetAttribute("value", "ignored.bin");

            var query = new Element("textarea");
            query.SetAttribute("name", "q");
            query.SetAttribute("value", "fenbrowser");

            var source = new Element("input");
            source.SetAttribute("type", "hidden");
            source.SetAttribute("name", "source");
            source.SetAttribute("value", "hp");

            var submit = new Element("input");
            submit.SetAttribute("type", "submit");
            submit.SetAttribute("name", "btnK");
            submit.SetAttribute("value", "Google Search");

            form.AppendChild(fileInput);
            form.AppendChild(query);
            form.AppendChild(source);
            form.AppendChild(submit);

            var collectMethod = typeof(BrowserHost).GetMethod(
                "CollectFormSubmissionEntries",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(collectMethod);

            var result = collectMethod!.Invoke(null, new object[] { form, submit }) as List<KeyValuePair<string, string>>;
            Assert.NotNull(result);

            Assert.Contains(result!, pair => pair.Key == "q" && pair.Value == "fenbrowser");
            Assert.Contains(result!, pair => pair.Key == "source" && pair.Value == "hp");
            Assert.Contains(result!, pair => pair.Key == "btnK" && pair.Value == "Google Search");
            Assert.DoesNotContain(result!, pair => pair.Key == "upload");
        }
    }
}
