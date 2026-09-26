using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Resolved values of <c>width</c> and <c>height</c> as <c>getComputedStyle</c>
/// reports them.
/// </summary>
/// <remarks>
/// CSSOM §6.7.2 "resolved value": when the property applies to the element and its
/// display is not <c>none</c> or <c>contents</c>, the resolved value is the used
/// value, so a percentage, <c>calc()</c> or <c>auto</c> size reads back in pixels.
/// Width and height do not apply to non-replaced inline boxes, which keep the
/// computed value. The used value follows <c>box-sizing</c>: a border-box element
/// reports its border-box size. w3schools' tryit page prints "Result Size: NaN x NaN"
/// from `Number(getComputedStyle(iframe).width.replace("px", ""))` when this reads
/// back as "100%".
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private void ResolveComputedSizes(Element element, Dictionary<string, JsValue> props)
    {
        var display = ReadStringProp(props, "display")?.ToLowerInvariant();
        if (display is null or "none" or "contents" ||
            (display == "inline" && !ReplacedElementSizing.ShouldTreatAsAtomicReplacedElement(element)) ||
            LayoutBoxResolver?.Invoke(element) is not BoxModel box)
        {
            return;
        }

        bool borderBox = string.Equals(ReadStringProp(props, "box-sizing"), "border-box", StringComparison.OrdinalIgnoreCase);
        var size = borderBox ? box.BorderBox : box.ContentBox;
        props["width"] = JsValue.FromString(FormatCssPx(size.Width));
        props["height"] = JsValue.FromString(FormatCssPx(size.Height));
    }
}
