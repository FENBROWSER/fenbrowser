using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Interface for HTML parsing strategies.
    /// </summary>
    public interface IHtmlParser
    {
        Document Parse(string html);
    }
}
