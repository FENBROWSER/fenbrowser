using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Legacy utility shim retained for compatibility with older code paths that still call LiteDomUtil.IsVoid.
    /// Delegates to HtmlLiteParser.IsVoid.
    /// </summary>
    public static class LiteDomUtil
    {
        public static bool IsVoid(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return HtmlLiteParser.IsVoid(tag.ToLowerInvariant());
        }
    }
}