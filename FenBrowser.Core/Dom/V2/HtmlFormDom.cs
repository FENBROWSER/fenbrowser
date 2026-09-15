// HTML Living Standard §4.10 Forms: the form-associated element model used by
// form.elements, form.length and the select/option accessors.
using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    public static class HtmlFormDom
    {
        private static bool Is(Element el, string localName) =>
            el != null &&
            string.Equals(el.NamespaceUri, Namespaces.Html, StringComparison.Ordinal) &&
            string.Equals(el.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        public static bool IsForm(Element el) => Is(el, "form");
        public static bool IsSelect(Element el) => Is(el, "select");
        public static bool IsOption(Element el) => Is(el, "option");

        /// <summary>
        /// HTML §4.10.2 "listed elements" that can appear in form.elements: button,
        /// fieldset, input (other than type=image), object, output, select,
        /// textarea.
        /// </summary>
        public static bool IsListedElement(Element el)
        {
            if (el == null || !string.Equals(el.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
                return false;

            switch (el.LocalName?.ToLowerInvariant())
            {
                case "button":
                case "fieldset":
                case "object":
                case "output":
                case "select":
                case "textarea":
                    return true;
                case "input":
                    return !string.Equals(el.GetAttribute("type"), "image", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        /// <summary>
        /// HTML §4.10.3 dom-form-elements: the form's listed elements in tree
        /// order - descendants whose form owner is this form, plus elements
        /// elsewhere in the tree whose form attribute names it (§4.10.17.3).
        /// </summary>
        public static List<Element> ListedElements(Element form)
        {
            var result = new List<Element>();
            var root = form.GetRootNode();
            var formId = form.GetAttribute("id");
            foreach (var node in root.Descendants())
            {
                if (node is not Element el || !IsListedElement(el))
                    continue;

                if (ReferenceEquals(FormOwner(el, formId), form))
                    result.Add(el);
            }

            return result;
        }

        /// <summary>
        /// HTML §4.10.17.3 form owner: the element named by a form attribute when
        /// one is present and resolvable, else the nearest ancestor form.
        /// </summary>
        public static Element FormOwner(Element control, string candidateFormId = null)
        {
            var formAttribute = control.GetAttribute("form");
            if (formAttribute != null)
            {
                if (formAttribute.Length == 0)
                    return null;

                var root = control.GetRootNode();
                Element byId = root switch
                {
                    Document document => document.GetElementById(formAttribute),
                    DocumentFragment fragment => fragment.GetElementById(formAttribute),
                    _ => null
                };
                return byId != null && IsForm(byId) ? byId : null;
            }

            for (var ancestor = control.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (IsForm(ancestor))
                    return ancestor;
            }

            return null;
        }

        /// <summary>dom-select-options: option descendants in tree order (through optgroups).</summary>
        public static List<Element> Options(Element select)
        {
            var options = new List<Element>();
            for (var child = select.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is not Element el)
                    continue;
                if (IsOption(el))
                {
                    options.Add(el);
                }
                else if (Is(el, "optgroup"))
                {
                    for (var inner = el.FirstChild; inner != null; inner = inner.NextSibling)
                    {
                        if (inner is Element innerEl && IsOption(innerEl))
                            options.Add(innerEl);
                    }
                }
            }

            return options;
        }

        /// <summary>
        /// dom-select-add: insert an option or optgroup before the option at
        /// <paramref name="beforeIndex"/> (or before <paramref name="beforeElement"/>),
        /// appending when neither is given. A before element that is not one of
        /// the select's options is a NotFoundError; an element that is an
        /// ancestor of the select is a HierarchyRequestError.
        /// </summary>
        public static void Add(Element select, Element element, Element beforeElement, int? beforeIndex)
        {
            if (element == null || !(IsOption(element) || Is(element, "optgroup")))
                throw new DomException("TypeError", "The element to add must be an option or optgroup");

            if (element.Contains(select))
                throw new DomException("HierarchyRequestError", "The new option is an ancestor of the select");

            Node reference = null;
            if (beforeElement != null)
            {
                if (!select.Contains(beforeElement))
                    throw new DomException("NotFoundError", "The reference element is not in the select");
                reference = beforeElement;
            }
            else if (beforeIndex.HasValue)
            {
                var options = Options(select);
                if (beforeIndex.Value >= 0 && beforeIndex.Value < options.Count)
                    reference = options[beforeIndex.Value];
            }

            if (reference == null)
            {
                select.AppendChild(element);
            }
            else
            {
                ((ContainerNode)reference.ParentNode).InsertBefore(element, reference);
            }
        }

        /// <summary>dom-select-remove: remove the option at the index; no-op when out of range.</summary>
        public static void Remove(Element select, int index)
        {
            var options = Options(select);
            if (index < 0 || index >= options.Count)
                return;

            var option = options[index];
            ((ContainerNode)option.ParentNode).RemoveChild(option);
        }

        /// <summary>dom-option-index: the option's index in its select's options, or 0.</summary>
        public static int OptionIndex(Element option)
        {
            for (var ancestor = option.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (IsSelect(ancestor))
                {
                    var index = Options(ancestor).IndexOf(option);
                    return index < 0 ? 0 : index;
                }
            }

            return 0;
        }
    }
}
