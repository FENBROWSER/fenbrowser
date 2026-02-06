// Foreign Content Parsing per WHATWG HTML5 Spec Section 13.2.6.5
// Handles SVG and MathML elements within HTML documents
using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2; // Updated to V2

namespace FenBrowser.FenEngine.HTML
{
    /// <summary>
    /// Foreign content namespace handling per WHATWG 13.2.6.5.
    /// Provides SVG and MathML element/attribute adjustments.
    /// </summary>
    public static class ForeignContent
    {
        // Namespace URIs
        public const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
        public const string SvgNamespace = "http://www.w3.org/2000/svg";
        public const string MathMLNamespace = "http://www.w3.org/1998/Math/MathML";
        public const string XLinkNamespace = "http://www.w3.org/1999/xlink";
        public const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
        public const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

        /// <summary>
        /// SVG element name adjustments per WHATWG spec.
        /// The parser receives lowercase names but SVG is case-sensitive.
        /// </summary>
        private static readonly Dictionary<string, string> SvgElementAdjustments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "altglyph", "altGlyph" },
            { "altglyphdef", "altGlyphDef" },
            { "altglyphitem", "altGlyphItem" },
            { "animatecolor", "animateColor" },
            { "animatemotion", "animateMotion" },
            { "animatetransform", "animateTransform" },
            { "clippath", "clipPath" },
            { "feblend", "feBlend" },
            { "fecolormatrix", "feColorMatrix" },
            { "fecomponenttransfer", "feComponentTransfer" },
            { "fecomposite", "feComposite" },
            { "feconvolvematrix", "feConvolveMatrix" },
            { "fediffuselighting", "feDiffuseLighting" },
            { "fedisplacementmap", "feDisplacementMap" },
            { "fedistantlight", "feDistantLight" },
            { "fedropshadow", "feDropShadow" },
            { "feflood", "feFlood" },
            { "fefunca", "feFuncA" },
            { "fefuncb", "feFuncB" },
            { "fefuncg", "feFuncG" },
            { "fefuncr", "feFuncR" },
            { "fegaussianblur", "feGaussianBlur" },
            { "feimage", "feImage" },
            { "femerge", "feMerge" },
            { "femergenode", "feMergeNode" },
            { "femorphology", "feMorphology" },
            { "feoffset", "feOffset" },
            { "fepointlight", "fePointLight" },
            { "fespecularlighting", "feSpecularLighting" },
            { "fespotlight", "feSpotLight" },
            { "fetile", "feTile" },
            { "feturbulence", "feTurbulence" },
            { "foreignobject", "foreignObject" },
            { "glyphref", "glyphRef" },
            { "lineargradient", "linearGradient" },
            { "radialgradient", "radialGradient" },
            { "textpath", "textPath" }
        };

        /// <summary>
        /// SVG attribute name adjustments per WHATWG spec.
        /// </summary>
        private static readonly Dictionary<string, string> SvgAttributeAdjustments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "attributename", "attributeName" },
            { "attributetype", "attributeType" },
            { "basefrequency", "baseFrequency" },
            { "baseprofile", "baseProfile" },
            { "calcmode", "calcMode" },
            { "clippathunits", "clipPathUnits" },
            { "diffuseconstant", "diffuseConstant" },
            { "edgemode", "edgeMode" },
            { "filterunits", "filterUnits" },
            { "glyphref", "glyphRef" },
            { "gradienttransform", "gradientTransform" },
            { "gradientunits", "gradientUnits" },
            { "kernelmatrix", "kernelMatrix" },
            { "kernelunitlength", "kernelUnitLength" },
            { "keypoints", "keyPoints" },
            { "keysplines", "keySplines" },
            { "keytimes", "keyTimes" },
            { "lengthadjust", "lengthAdjust" },
            { "limitingconeangle", "limitingConeAngle" },
            { "markerheight", "markerHeight" },
            { "markerunits", "markerUnits" },
            { "markerwidth", "markerWidth" },
            { "maskcontentunits", "maskContentUnits" },
            { "maskunits", "maskUnits" },
            { "numoctaves", "numOctaves" },
            { "pathlength", "pathLength" },
            { "patterncontentunits", "patternContentUnits" },
            { "patterntransform", "patternTransform" },
            { "patternunits", "patternUnits" },
            { "pointsatx", "pointsAtX" },
            { "pointsaty", "pointsAtY" },
            { "pointsatz", "pointsAtZ" },
            { "preservealpha", "preserveAlpha" },
            { "preserveaspectratio", "preserveAspectRatio" },
            { "primitiveunits", "primitiveUnits" },
            { "refx", "refX" },
            { "refy", "refY" },
            { "repeatcount", "repeatCount" },
            { "repeatdur", "repeatDur" },
            { "requiredextensions", "requiredExtensions" },
            { "requiredfeatures", "requiredFeatures" },
            { "specularconstant", "specularConstant" },
            { "specularexponent", "specularExponent" },
            { "spreadmethod", "spreadMethod" },
            { "startoffset", "startOffset" },
            { "stddeviation", "stdDeviation" },
            { "stitchtiles", "stitchTiles" },
            { "surfacescale", "surfaceScale" },
            { "systemlanguage", "systemLanguage" },
            { "tablevalues", "tableValues" },
            { "targetx", "targetX" },
            { "targety", "targetY" },
            { "textlength", "textLength" },
            { "viewbox", "viewBox" },
            { "viewtarget", "viewTarget" },
            { "xchannelselector", "xChannelSelector" },
            { "ychannelselector", "yChannelSelector" },
            { "zoomandpan", "zoomAndPan" }
        };

        /// <summary>
        /// MathML attribute name adjustments per WHATWG spec.
        /// </summary>
        private static readonly Dictionary<string, string> MathMLAttributeAdjustments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "definitionurl", "definitionURL" }
        };

        /// <summary>
        /// Foreign attributes that need namespace prefix handling.
        /// Format: lowercase name -> (prefix, localName, namespace)
        /// </summary>
        private static readonly Dictionary<string, (string Prefix, string LocalName, string Namespace)> ForeignAttributes = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            { "xlink:actuate", ("xlink", "actuate", XLinkNamespace) },
            { "xlink:arcrole", ("xlink", "arcrole", XLinkNamespace) },
            { "xlink:href", ("xlink", "href", XLinkNamespace) },
            { "xlink:role", ("xlink", "role", XLinkNamespace) },
            { "xlink:show", ("xlink", "show", XLinkNamespace) },
            { "xlink:title", ("xlink", "title", XLinkNamespace) },
            { "xlink:type", ("xlink", "type", XLinkNamespace) },
            { "xml:lang", ("xml", "lang", XmlNamespace) },
            { "xml:space", ("xml", "space", XmlNamespace) },
            { "xmlns", (null, "xmlns", XmlnsNamespace) },
            { "xmlns:xlink", ("xmlns", "xlink", XmlnsNamespace) }
        };

        /// <summary>
        /// HTML integration points - where we switch back to HTML parsing.
        /// </summary>
        private static readonly HashSet<string> MathMLIntegrationPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mi", "mo", "mn", "ms", "mtext"
        };

        private static readonly HashSet<string> SvgIntegrationPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "foreignObject", "desc", "title"
        };

        /// <summary>
        /// Elements that break out of foreign content.
        /// </summary>
        private static readonly HashSet<string> BreakoutElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt",
            "em", "embed", "h1", "h2", "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li",
            "listing", "menu", "meta", "nobr", "ol", "p", "pre", "ruby", "s", "small", "span",
            "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var"
        };

        /// <summary>
        /// Adjust SVG element name to proper casing.
        /// </summary>
        public static string AdjustSvgElementName(string tagName)
        {
            return SvgElementAdjustments.TryGetValue(tagName, out var adjusted) ? adjusted : tagName;
        }

        /// <summary>
        /// Adjust SVG attribute name to proper casing.
        /// </summary>
        public static string AdjustSvgAttributeName(string attrName)
        {
            return SvgAttributeAdjustments.TryGetValue(attrName, out var adjusted) ? adjusted : attrName;
        }

        /// <summary>
        /// Adjust MathML attribute name to proper casing.
        /// </summary>
        public static string AdjustMathMLAttributeName(string attrName)
        {
            return MathMLAttributeAdjustments.TryGetValue(attrName, out var adjusted) ? adjusted : attrName;
        }

        /// <summary>
        /// Check if element is an HTML integration point.
        /// </summary>
        public static bool IsHtmlIntegrationPoint(Element element)
        {
            if (element == null) return false;
            var ns = element.NamespaceUri ?? HtmlNamespace;
            var tag = element.TagName;

            // MathML text integration points
            if (ns == MathMLNamespace && MathMLIntegrationPoints.Contains(tag))
                return true;

            // SVG integration points
            if (ns == SvgNamespace && SvgIntegrationPoints.Contains(tag))
                return true;

            // MathML annotation-xml with encoding
            if (ns == MathMLNamespace && tag.Equals("annotation-xml", StringComparison.OrdinalIgnoreCase))
            {
                var encoding = element.GetAttribute("encoding");
                if (!string.IsNullOrEmpty(encoding))
                {
                    return encoding.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                           encoding.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        /// <summary>
        /// Check if element is a MathML text integration point.
        /// </summary>
        public static bool IsMathMLTextIntegrationPoint(Element element)
        {
            if (element == null) return false;
            var ns = element.NamespaceUri ?? HtmlNamespace;
            return ns == MathMLNamespace && MathMLIntegrationPoints.Contains(element.TagName);
        }

        /// <summary>
        /// Check if a tag should break out of foreign content.
        /// </summary>
        public static bool IsBreakoutElement(string tagName)
        {
            return BreakoutElements.Contains(tagName);
        }

        /// <summary>
        /// Check if we're in foreign content based on the current node.
        /// </summary>
        public static bool IsInForeignContent(Element currentNode)
        {
            if (currentNode == null) return false;
            var ns = currentNode.NamespaceUri ?? HtmlNamespace;
            return ns == SvgNamespace || ns == MathMLNamespace;
        }

        /// <summary>
        /// Create an element in the appropriate namespace.
        /// </summary>
        public static Element CreateForeignElement(string tagName, string namespaceUri, Dictionary<string, string> attributes)
        {
            string adjustedName = tagName;
            
            if (namespaceUri == SvgNamespace)
            {
                adjustedName = AdjustSvgElementName(tagName);
            }

            var element = new Element(adjustedName, null, namespaceUri);

            if (attributes != null)
            {
                foreach (var kv in attributes)
                {
                    string attrName = kv.Key;
                    
                    // Adjust attribute names
                    if (namespaceUri == SvgNamespace)
                        attrName = AdjustSvgAttributeName(attrName);
                    else if (namespaceUri == MathMLNamespace)
                        attrName = AdjustMathMLAttributeName(attrName);

                    // Handle foreign attributes with namespace prefixes
                    if (ForeignAttributes.TryGetValue(kv.Key, out var foreignAttr))
                    {
                        // Set with namespace info
                        element.SetAttributeNS(foreignAttr.Namespace, foreignAttr.LocalName, kv.Value);
                    }
                    else
                    {
                        element.SetAttribute(attrName, kv.Value);
                    }
                }
            }

            return element;
        }

        /// <summary>
        /// Get the namespace for a start tag based on current context.
        /// </summary>
        public static string GetNamespaceForTag(string tagName, Element currentNode)
        {
            if (currentNode == null) return HtmlNamespace;

            var currentNs = currentNode.NamespaceUri ?? HtmlNamespace;

            // SVG element
            if (tagName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                return SvgNamespace;

            // Math element
            if (tagName.Equals("math", StringComparison.OrdinalIgnoreCase))
                return MathMLNamespace;

            // Inside foreign content, inherit namespace unless at integration point
            if (currentNs != HtmlNamespace && !IsHtmlIntegrationPoint(currentNode))
                return currentNs;

            return HtmlNamespace;
        }
    }
}
