using System;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JavaScriptEngineTextareaValueTests
    {
        [Fact]
        public void JsDomElement_TextareaValueSetter_SynchronizesAttributeAndTextContent()
        {
            var engine = new JavaScriptEngine(CreateHost());
            var textarea = new Element("textarea");
            textarea.TextContent = "seed";

            var wrapperType = typeof(JavaScriptEngine).GetNestedType("JsDomElement", BindingFlags.NonPublic);
            Assert.NotNull(wrapperType);

            var wrapper = Activator.CreateInstance(
                wrapperType!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { engine, textarea },
                culture: null);

            Assert.NotNull(wrapper);

            var valueProperty = wrapperType!.GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(valueProperty);

            valueProperty!.SetValue(wrapper, "fenbrowser");

            Assert.Equal("fenbrowser", textarea.GetAttribute("value"));
            Assert.Equal("fenbrowser", textarea.TextContent);
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
