using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// SMIL timing from script (SVG 1.1 §19.2.2 and SVG Animations §6.4):
/// SVGSVGElement pauseAnimations, unpauseAnimations, animationsPaused,
/// getCurrentTime and setCurrentTime act on the outermost svg element's timeline,
/// and SVGAnimationElement beginElement(At), endElement(At), getCurrentTime and
/// targetElement. State lives in <see cref="SvgAnimationTimeline"/>; painting samples
/// it, so every change here only invalidates paint.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private bool TryGetSvgAnimationProperty(Element element, string property, out JsValue value)
    {
        value = JsValue.Undefined;
        if (element.LocalName == "svg")
        {
            return TryGetSvgTimeContainerProperty(element, property, out value);
        }
        if (SvgAnimationTimeline.IsAnimationElement(element))
        {
            return TryGetSvgAnimationElementProperty(element, property, out value);
        }
        return false;
    }

    private bool TryGetSvgTimeContainerProperty(Element svg, string property, out JsValue value)
    {
        Element container = SvgAnimationTimeline.TimeContainerOf(svg);
        switch (property)
        {
            case "pauseAnimations":
                value = GetOrCreateHostCallable(svg, property, (_, _) =>
                {
                    SvgAnimationTimeline.Pause(container);
                    InvalidateSvgTimeline(container);
                    return JsValue.Undefined;
                });
                return true;
            case "unpauseAnimations":
                value = GetOrCreateHostCallable(svg, property, (_, _) =>
                {
                    SvgAnimationTimeline.Unpause(container);
                    InvalidateSvgTimeline(container);
                    return JsValue.Undefined;
                });
                return true;
            case "animationsPaused":
                value = GetOrCreateHostCallable(svg, property,
                    (_, _) => JsValue.FromBoolean(SvgAnimationTimeline.IsPaused(container)));
                return true;
            case "getCurrentTime":
                value = GetOrCreateHostCallable(svg, property,
                    (_, _) => JsValue.FromNumber((float)SvgAnimationTimeline.CurrentTime(container)));
                return true;
            case "setCurrentTime":
                value = GetOrCreateHostCallable(svg, property, (_, args) =>
                {
                    SvgAnimationTimeline.Seek(container, FloatArgument(args, 0));
                    InvalidateSvgTimeline(container);
                    return JsValue.Undefined;
                }, length: 1);
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    private bool TryGetSvgAnimationElementProperty(Element animation, string property, out JsValue value)
    {
        Element container = SvgAnimationTimeline.TimeContainerOf(animation);
        switch (property)
        {
            case "beginElement":
                value = GetOrCreateHostCallable(animation, property,
                    (_, _) => AddAnimationInstanceTime(animation, container, 0f, isEnd: false));
                return true;
            case "beginElementAt":
                value = GetOrCreateHostCallable(animation, property,
                    (_, args) => AddAnimationInstanceTime(animation, container, FloatArgument(args, 0), isEnd: false),
                    length: 1);
                return true;
            case "endElement":
                value = GetOrCreateHostCallable(animation, property,
                    (_, _) => AddAnimationInstanceTime(animation, container, 0f, isEnd: true));
                return true;
            case "endElementAt":
                value = GetOrCreateHostCallable(animation, property,
                    (_, args) => AddAnimationInstanceTime(animation, container, FloatArgument(args, 0), isEnd: true),
                    length: 1);
                return true;
            case "getCurrentTime":
                value = GetOrCreateHostCallable(animation, property,
                    (_, _) => JsValue.FromNumber((float)SvgAnimationTimeline.CurrentTime(container)));
                return true;
            case "targetElement":
                value = ToHostOrNull(ResolveAnimationTarget(animation), HostObjectKind.DomElement);
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    /// <summary>
    /// Adds an instance time at the current document time plus an offset. Outside
    /// an svg element there is no timeline and nothing happens.
    /// </summary>
    private JsValue AddAnimationInstanceTime(Element animation, Element container, float offset, bool isEnd)
    {
        if (container != null)
        {
            SvgAnimationTimeline.AddInstanceTime(
                animation, SvgAnimationTimeline.CurrentTime(container) + offset, isEnd);
            InvalidateSvgTimeline(container);
        }
        return JsValue.Undefined;
    }

    private void InvalidateSvgTimeline(Element container)
    {
        if (container == null)
        {
            return;
        }
        container.MarkDirty(InvalidationKind.Paint);
        RequestRender?.Invoke();
    }

    /// <summary>The element an animation targets: its href reference, else its parent.</summary>
    private static Element ResolveAnimationTarget(Element animation)
    {
        string href = animation.GetAttribute("href") ?? animation.GetAttribute("xlink:href");
        if (!string.IsNullOrWhiteSpace(href))
        {
            string trimmed = href.Trim();
            return trimmed.StartsWith('#') && trimmed.Length > 1
                ? animation.OwnerDocument?.GetElementById(trimmed.Substring(1))
                : null;
        }
        return animation.ParentElement;
    }
}
