using System;
using System.Xml;
using System.Xml.Linq;

namespace FenBrowser.Core.Dom.V2;

/// <summary>
/// Thrown when <see cref="XmlDomParser.Parse"/> encounters input that is not
/// well-formed XML. The DOM <c>DOMParser</c> surface converts this into the
/// script-visible parse failure for the selected MIME type.
/// </summary>
public sealed class XmlDomParseException : Exception
{
    public XmlDomParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parses well-formed XML into a V2 <see cref="Document"/> with content type
/// <c>application/xml</c>. Supports elements (with namespaces preserved as
/// qualified names), attributes, text, comments, CDATA, and processing
/// instructions. This is the engine-side implementation behind the
/// <c>DOMParser</c> XML MIME types.
/// </summary>
public static class XmlDomParser
{
    public const string XmlContentType = "application/xml";

    /// <summary>
    /// Returns true when the MIME type is one of the XML types DOMParser must
    /// parse as XML (includes any "+xml" suffix per the DOM Parsing spec).
    /// </summary>
    public static bool IsXmlMimeType(string mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            return false;
        }

        return string.Equals(mimeType, "text/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || (mimeType.Length > 4 && mimeType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses the given well-formed XML into a new document. Throws
    /// <see cref="XmlDomParseException"/> when the input is not well-formed.
    /// </summary>
    public static Document Parse(string xml, string contentType = XmlContentType)
    {        if (xml == null)
        {
            throw new XmlDomParseException("XML input must not be null.");
        }

        XObject parsed;
        try
        {
            using var reader = XmlReader.Create(
                new System.IO.StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    ConformanceLevel = ConformanceLevel.Document,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                    IgnoreWhitespace = false
                });
            parsed = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new XmlDomParseException(ex.Message);
        }

        var document = (XDocument)parsed;
        var doc = new Document(contentType: contentType ?? XmlContentType);

        foreach (var node in document.Nodes())
        {
            switch (node)
            {
                case XDocumentType doctype:
                    doc.AppendChild(doc.CreateDocumentType(doctype.Name));
                    break;
                case XElement element:
                    doc.AppendChild(CreateElement(doc, element));
                    break;
                case XText:
                    // Whitespace around the document element - the newline after
                    // </html> that most files end with - is not part of the DOM: a
                    // Document cannot hold Text. Anything else there is not
                    // well-formed and the reader has already rejected it.
                    break;
                case XComment comment:
                    doc.AppendChild(doc.CreateComment(comment.Value));
                    break;
                case XProcessingInstruction pi:
                    doc.AppendChild(doc.CreateProcessingInstruction(pi.Target, pi.Data));
                    break;
            }
        }

        // Created documents are not loaded from the network; their readiness is
        // complete as soon as parsing finishes (HTML: resource-metadata
        // management).
        doc.ReadyState = DocumentReadyState.Complete;
        return doc;
    }

    /// <summary>
    /// Parses the given XML, returning a script-visible error document with a
    /// <c>&lt;parsererror&gt;</c> document element when the input is not
    /// well-formed. This mirrors the browser-observable DOMParser behavior of
    /// returning an error document instead of throwing.
    /// </summary>
    public static Document ParseWithErrorDocument(string xml)
    {
        try
        {
            return Parse(xml);
        }
        catch (XmlDomParseException ex)
        {
            return CreateParserErrorDocument(ex.Message);
        }
    }

    private static Document CreateParserErrorDocument(string message)
    {
        const string parserErrorNamespace = "http://www.mozilla.org/newlayout/xml/parsererror.xml";

        var doc = new Document(contentType: XmlContentType);
        var root = doc.CreateElementNS(parserErrorNamespace, "parsererror");
        root.TextContent = message;
        doc.AppendChild(root);
        doc.ReadyState = DocumentReadyState.Complete;
        return doc;
    }

    private static Element CreateElement(Document doc, XElement element)
    {
        var elementName = element.Name;
        Element created;

        if (!string.IsNullOrEmpty(elementName.NamespaceName))
        {
            // The DOM element carries the source qualified name (prefix:local);
            // XName.ToString() is the expanded {namespace}local form and is not
            // a qualified name at all.
            var prefix = element.GetPrefixOfNamespace(elementName.Namespace);
            created = doc.CreateElementNS(
                elementName.NamespaceName,
                string.IsNullOrEmpty(prefix) ? elementName.LocalName : prefix + ":" + elementName.LocalName);
        }
        else
        {
            created = doc.CreateElement(elementName.LocalName);
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                created.SetAttributeNS(
                    "http://www.w3.org/2000/xmlns/",
                    attribute.Name.LocalName == "xmlns" ? "xmlns" : "xmlns:" + attribute.Name.LocalName,
                    attribute.Value);
            }
            else if (!string.IsNullOrEmpty(attribute.Name.NamespaceName))
            {
                var attributePrefix = element.GetPrefixOfNamespace(attribute.Name.Namespace);
                created.SetAttributeNS(
                    attribute.Name.NamespaceName,
                    string.IsNullOrEmpty(attributePrefix) ? attribute.Name.LocalName : attributePrefix + ":" + attribute.Name.LocalName,
                    attribute.Value);
            }
            else
            {
                created.SetAttribute(attribute.Name.LocalName, attribute.Value);
            }
        }

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    created.AppendChild(CreateElement(doc, child));
                    break;
                case XText text:
                    AppendText(doc, created, text.Value);
                    break;
                case XComment comment:
                    created.AppendChild(doc.CreateComment(comment.Value));
                    break;
                case XProcessingInstruction pi:
                    created.AppendChild(doc.CreateProcessingInstruction(pi.Target, pi.Data));
                    break;
            }
        }

        return created;
    }

    private static void AppendText(Document doc, ContainerNode parent, string data)
    {
        // Adjacent character data is merged into one text node per DOM rules.
        if (parent.LastChild is Text existing)
        {
            existing.TextContent += data;
            return;
        }

        parent.AppendChild(doc.CreateTextNode(data));
    }
}
