using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Legacy utility shim retained for compatibility with older code paths that still call LiteDomUtil.IsVoid.
    /// </summary>
    public static class LiteDomUtil
    {
        public static bool IsVoid(string tag) => HtmlElementSemantics.IsVoid(tag);
    }
}
