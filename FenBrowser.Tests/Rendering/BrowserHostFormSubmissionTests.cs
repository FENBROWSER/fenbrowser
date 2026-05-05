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

        [Fact]
        public void CollectFormSubmissionEntries_IncludesOnlyActivatedSubmitter()
        {
            var form = new Element("form");

            var query = new Element("input");
            query.SetAttribute("name", "q");
            query.SetAttribute("value", "fen");

            var activatedSubmitter = new Element("button");
            activatedSubmitter.SetAttribute("type", "submit");
            activatedSubmitter.SetAttribute("name", "go");
            activatedSubmitter.SetAttribute("value", "Search");

            var otherSubmitter = new Element("input");
            otherSubmitter.SetAttribute("type", "submit");
            otherSubmitter.SetAttribute("name", "alt");
            otherSubmitter.SetAttribute("value", "Alt");

            form.AppendChild(query);
            form.AppendChild(activatedSubmitter);
            form.AppendChild(otherSubmitter);

            var collectMethod = typeof(BrowserHost).GetMethod(
                "CollectFormSubmissionEntries",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(collectMethod);

            var result = collectMethod!.Invoke(null, new object[] { form, activatedSubmitter }) as List<KeyValuePair<string, string>>;
            Assert.NotNull(result);

            Assert.Contains(result!, pair => pair.Key == "q" && pair.Value == "fen");
            Assert.Contains(result!, pair => pair.Key == "go" && pair.Value == "Search");
            Assert.DoesNotContain(result!, pair => pair.Key == "alt");
        }

        [Fact]
        public void CollectFormSubmissionEntries_UsesSuccessfulCheckboxAndRadioRules()
        {
            var form = new Element("form");

            var checkedCheckbox = new Element("input");
            checkedCheckbox.SetAttribute("type", "checkbox");
            checkedCheckbox.SetAttribute("name", "remember");
            checkedCheckbox.SetAttribute("checked", string.Empty);

            var uncheckedCheckbox = new Element("input");
            uncheckedCheckbox.SetAttribute("type", "checkbox");
            uncheckedCheckbox.SetAttribute("name", "tracking");

            var checkedRadio = new Element("input");
            checkedRadio.SetAttribute("type", "radio");
            checkedRadio.SetAttribute("name", "size");
            checkedRadio.SetAttribute("value", "large");
            checkedRadio.SetAttribute("checked", string.Empty);

            var uncheckedRadio = new Element("input");
            uncheckedRadio.SetAttribute("type", "radio");
            uncheckedRadio.SetAttribute("name", "size");
            uncheckedRadio.SetAttribute("value", "small");

            form.AppendChild(checkedCheckbox);
            form.AppendChild(uncheckedCheckbox);
            form.AppendChild(checkedRadio);
            form.AppendChild(uncheckedRadio);

            var collectMethod = typeof(BrowserHost).GetMethod(
                "CollectFormSubmissionEntries",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(collectMethod);

            var result = collectMethod!.Invoke(null, new object[] { form, null }) as List<KeyValuePair<string, string>>;
            Assert.NotNull(result);

            Assert.Contains(result!, pair => pair.Key == "remember" && pair.Value == "on");
            Assert.Contains(result!, pair => pair.Key == "size" && pair.Value == "large");
            Assert.DoesNotContain(result!, pair => pair.Key == "tracking");
            Assert.DoesNotContain(result!, pair => pair.Key == "size" && pair.Value == "small");
        }
    }
}
