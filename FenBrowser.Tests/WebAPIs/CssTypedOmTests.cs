using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;
using JsExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class CssTypedOmTests
    {
        [Fact]
        public void StylePropertyMap_SetGetDeleteClear_WorksWithTypedValues()
        {
            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var ctor = Assert.IsType<FenFunction>(CssTypedOM.CreateStylePropertyMapConstructor(
                context,
                prop => styles.TryGetValue(prop, out var value) ? value : null,
                (prop, value) =>
                {
                    if (string.IsNullOrEmpty(value))
                        styles.Remove(prop);
                    else
                        styles[prop] = value;
                }));

            var map = Assert.IsAssignableFrom<FenObject>(ctor.Invoke(Array.Empty<FenValue>(), context).AsObject());

            map.Get("set").AsFunction().Invoke(
                new[] { FenValue.FromString("width"), FenValue.FromString("10px") },
                context,
                FenValue.FromObject(map));

            Assert.True(styles.ContainsKey("width"));
            Assert.Equal("10px", styles["width"]);

            var widthValue = map.Get("get").AsFunction().Invoke(
                new[] { FenValue.FromString("width") },
                context,
                FenValue.FromObject(map));
            Assert.True(widthValue.IsObject);
            var widthObject = Assert.IsType<FenObject>(widthValue.AsObject());
            Assert.Equal("CSSUnitValue", widthObject.InternalClass);
            Assert.Equal(10d, widthObject.Get("value").ToNumber());
            Assert.Equal("px", widthObject.Get("unit").AsString());

            var hasWidth = map.Get("has").AsFunction().Invoke(
                new[] { FenValue.FromString("width") },
                context,
                FenValue.FromObject(map));
            Assert.True(hasWidth.ToBoolean());

            map.Get("delete").AsFunction().Invoke(
                new[] { FenValue.FromString("width") },
                context,
                FenValue.FromObject(map));
            Assert.False(styles.ContainsKey("width"));

            map.Get("set").AsFunction().Invoke(
                new[] { FenValue.FromString("height"), FenValue.FromString("20px") },
                context,
                FenValue.FromObject(map));
            map.Get("set").AsFunction().Invoke(
                new[] { FenValue.FromString("margin-left"), FenValue.FromString("1em") },
                context,
                FenValue.FromObject(map));
            map.Get("clear").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(map));

            Assert.Empty(styles);
        }

        [Fact]
        public void StylePropertyMap_ReadOnly_RejectsMutations()
        {
            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var ctor = Assert.IsType<FenFunction>(CssTypedOM.CreateStylePropertyMapConstructor(
                context,
                _ => "blue",
                null,
                isReadOnly: true));

            var map = Assert.IsAssignableFrom<FenObject>(ctor.Invoke(Array.Empty<FenValue>(), context).AsObject());

            Assert.Throws<InvalidOperationException>(() =>
            {
                map.Get("set").AsFunction().Invoke(
                    new[] { FenValue.FromString("color"), FenValue.FromString("red") },
                    context,
                    FenValue.FromObject(map));
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                map.Get("delete").AsFunction().Invoke(
                    new[] { FenValue.FromString("color") },
                    context,
                    FenValue.FromObject(map));
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                map.Get("clear").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(map));
            });
        }

        [Fact]
        public void InstallOnElementPrototype_ExposesAttributeAndComputedStyleMaps()
        {
            var inlineStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var computedStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["color"] = "green"
            };

            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var elementPrototype = new FenObject();

            CssTypedOM.InstallOnElementPrototype(
                elementPrototype,
                context,
                getComputedStyle: (_, prop) => computedStyles.TryGetValue(prop, out var value) ? value : null,
                setInlineStyle: (_, prop, value) =>
                {
                    if (string.IsNullOrEmpty(value))
                        inlineStyles.Remove(prop);
                    else
                        inlineStyles[prop] = value;
                    return true;
                },
                getInlineStyle: (_, prop) => inlineStyles.TryGetValue(prop, out var value) ? value : null);

            var element = new FenObject();
            element.SetPrototype(elementPrototype);

            var attributeStyleMap = Assert.IsAssignableFrom<FenObject>(element.Get("attributeStyleMap").AsObject());
            attributeStyleMap.Get("set").AsFunction().Invoke(
                new[] { FenValue.FromString("margin-top"), FenValue.FromString("5px") },
                context,
                FenValue.FromObject(attributeStyleMap));

            Assert.Equal("5px", inlineStyles["margin-top"]);

            var computedStyleMapValue = element.Get("computedStyleMap");
            Assert.True(computedStyleMapValue.IsFunction);
            var computedStyleMap = Assert.IsAssignableFrom<FenObject>(computedStyleMapValue.AsFunction()
                .Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(element)).AsObject());

            var color = computedStyleMap.Get("get").AsFunction().Invoke(
                new[] { FenValue.FromString("color") },
                context,
                FenValue.FromObject(computedStyleMap));
            if (color.IsObject)
            {
                var colorObject = Assert.IsType<FenObject>(color.AsObject());
                Assert.Equal("CSSKeywordValue", colorObject.InternalClass);
                Assert.Equal("green", colorObject.Get("value").AsString());
            }
            else
            {
                Assert.True(color.IsUndefined);
            }

            var setThrew = false;
            try
            {
                computedStyleMap.Get("set").AsFunction().Invoke(
                    new[] { FenValue.FromString("color"), FenValue.FromString("red") },
                    context,
                    FenValue.FromObject(computedStyleMap));
            }
            catch (InvalidOperationException)
            {
                setThrew = true;
            }

            Assert.Equal("green", computedStyles["color"]);
            if (!setThrew)
            {
                Assert.False(inlineStyles.ContainsKey("color"));
            }
        }

        [Fact]
        public void CssMathConstructorGroup_ComputesValuesAndClamp()
        {
            var group = CssTypedOM.CreateCssMathConstructorGroup();
            var sumCtor = group.Get("CSSMathSum").AsFunction();
            var clampCtor = group.Get("CSSMathClamp").AsFunction();

            var sum = Assert.IsAssignableFrom<FenObject>(sumCtor.Invoke(
                new[] { FenValue.FromNumber(2), FenValue.FromNumber(3), FenValue.FromNumber(4) },
                null).AsObject());
            Assert.Equal(9d, sum.Get("computedValue").ToNumber());

            var clamped = Assert.IsAssignableFrom<FenObject>(clampCtor.Invoke(
                new[] { FenValue.FromNumber(10), FenValue.FromNumber(120), FenValue.FromNumber(80) },
                null).AsObject());
            Assert.Equal(80d, clamped.Get("computedValue").ToNumber());
        }
    }
}
