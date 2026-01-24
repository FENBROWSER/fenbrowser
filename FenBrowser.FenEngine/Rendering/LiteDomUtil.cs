using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Legacy utility shim retained for compatibility with older code paths that still call LiteDomUtil.IsVoid.
    /// Legacy utility shim retained for compatibility.
    /// </summary>
    public static class LiteDomUtil
    {
        public static bool IsVoid(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            switch (tag.ToLowerInvariant())
            {
                case "area": case "base": case "br": case "col": case "embed": case "hr": case "img": case "input":
                case "link": case "meta": case "param": case "source": case "track": case "wbr":
                    return true;
                default: return false;
            }
        }
    }
}