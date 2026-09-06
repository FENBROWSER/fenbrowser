using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    /// <summary>
    /// A form with method=POST is only submitted when its entry list reaches the
    /// server. Navigating to the action URL alone is an unrelated empty GET, which
    /// the page cannot distinguish from the server rejecting the submission.
    /// </summary>
    [Collection("Engine Tests")]
    public class FormPostSubmissionTests
    {
        private static Element BuildForm(string enctype = null)
        {
            var form = new Element("form");
            form.SetAttribute("method", "POST");
            form.SetAttribute("action", "index");
            if (enctype != null)
            {
                form.SetAttribute("enctype", enctype);
            }

            return form;
        }

        private static Element AddControl(Element form, string tag, string name, string value)
        {
            var control = new Element(tag);
            control.SetAttribute("name", name);
            control.SetAttribute("value", value);
            form.AppendChild(control);
            return control;
        }

        [Fact]
        public void EncodeFormSubmissionBody_DefaultsToUrlEncoded()
        {
            var form = BuildForm();
            AddControl(form, "input", "q", "hello world");
            AddControl(form, "input", "lang", "en");

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, contentType) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.Equal(
                "application/x-www-form-urlencoded;charset=UTF-8",
                contentType);
            Assert.Equal("q=hello+world&lang=en", Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void EncodeFormSubmissionBody_CarriesScriptAssignedTextareaValue()
        {
            // reCAPTCHA writes its token into a hidden textarea and then submits the
            // form; a body that omits it is rejected as an unsolved challenge.
            var form = BuildForm();
            var token = "03AFcWeA7-token_value";
            var response = new Element("textarea");
            response.SetAttribute("name", "g-recaptcha-response");
            response.SetAttribute("value", token);
            form.AppendChild(response);

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, _) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.Contains(
                "g-recaptcha-response=" + Uri.EscapeDataString(token),
                Encoding.UTF8.GetString(body),
                StringComparison.Ordinal);
        }

        [Fact]
        public void EncodeFormSubmissionBody_EncodesReservedCharacters()
        {
            var form = BuildForm();
            AddControl(form, "input", "a&b", "c=d&e");

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, _) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.Equal("a%26b=c%3Dd%26e", Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void EncodeFormSubmissionBody_HonoursTextPlainEnctype()
        {
            var form = BuildForm("text/plain");
            AddControl(form, "input", "q", "hello world");

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, contentType) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.Equal("text/plain;charset=UTF-8", contentType);
            Assert.Equal("q=hello world\r\n", Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void EncodeFormSubmissionBody_HonoursMultipartEnctype()
        {
            var form = BuildForm("multipart/form-data");
            AddControl(form, "input", "q", "fen");

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, contentType) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.StartsWith("multipart/form-data; boundary=", contentType, StringComparison.Ordinal);

            var boundary = contentType.Substring(contentType.IndexOf('=') + 1);
            var text = Encoding.UTF8.GetString(body);

            Assert.Contains("--" + boundary + "\r\n", text, StringComparison.Ordinal);
            Assert.Contains(
                "Content-Disposition: form-data; name=\"q\"\r\n\r\nfen\r\n",
                text,
                StringComparison.Ordinal);
            Assert.EndsWith("--" + boundary + "--\r\n", text, StringComparison.Ordinal);
        }

        [Fact]
        public void EncodeFormSubmissionBody_OmitsUnsuccessfulControls()
        {
            var form = BuildForm();
            AddControl(form, "input", "kept", "1");

            var disabled = AddControl(form, "input", "dropped", "1");
            disabled.SetAttribute("disabled", string.Empty);

            var unchecked_ = new Element("input");
            unchecked_.SetAttribute("type", "checkbox");
            unchecked_.SetAttribute("name", "box");
            form.AppendChild(unchecked_);

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, _) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.Equal("kept=1", Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void EncodeFormSubmissionBody_ProducesEmptyBodyForEmptyForm()
        {
            var form = BuildForm();

            var entries = BrowserHost.CollectFormSubmissionEntries(form, submitter: null);
            var (body, contentType) = BrowserHost.EncodeFormSubmissionBody(form, entries);

            Assert.NotNull(body);
            Assert.Empty(body);
            Assert.Equal("application/x-www-form-urlencoded;charset=UTF-8", contentType);
        }
    }
}
