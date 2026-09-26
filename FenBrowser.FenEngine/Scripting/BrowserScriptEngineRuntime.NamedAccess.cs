using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §7.2.2.3 named access on the Window object: <c>window.foo</c>, and a bare
/// <c>foo</c> in script, reach the element with id "foo", the form or image named "foo",
/// or the child browsing context named "foo". The names live on WindowProperties, the
/// WebIDL named properties object between <c>Window.prototype</c> and
/// <c>EventTarget.prototype</c>, so anything the window or its interface defines wins,
/// and a name stops resolving as soon as nothing in the document carries it.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// HTML §7.2.2.3 steps 1-4 of the named property getter: the WindowProxy of a child
    /// navigable with that target name, else the one named object, else an HTMLCollection
    /// of all of them. Null when the name is not supported.
    /// </summary>
    private JsValue? ResolveWindowNamedProperty(string name)
    {
        if (string.IsNullOrEmpty(name) || _currentDomRoot == null)
        {
            return null;
        }

        var document = _currentDomRoot as Document ?? _currentDomRoot.OwnerDocument;
        if (document == null)
        {
            return null;
        }

        var frame = document.GetNamedFrameContainer(name);
        if (frame != null)
        {
            var window = GetIFrameContentWindowForCurrentContext(frame);
            if (window.Tag is not (JsValueTag.Undefined or JsValueTag.Null))
            {
                return window;
            }
        }

        var objects = document.GetWindowNamedObjects(name);
        if (objects.Count == 0)
        {
            return null;
        }

        if (objects.Count == 1)
        {
            return ToHostNodeOrNull(objects[0]);
        }

        // A live collection of every named object with that name, rooted at the document.
        var collection = new FilteredHTMLCollection(document, element => IsWindowNamedObject(element, name));
        return ToHostOrNull(new FenJsHtmlCollectionHost(collection), HostObjectKind.Other);
    }

    private static bool IsWindowNamedObject(Element element, string name)
    {
        if (string.Equals(element.Id, name, StringComparison.Ordinal))
        {
            return true;
        }

        return element.NamespaceUri == Namespaces.Html &&
               element.LocalName is "embed" or "form" or "img" or "object" &&
               string.Equals(element.GetAttributeNS(null, "name"), name, StringComparison.Ordinal);
    }
}
