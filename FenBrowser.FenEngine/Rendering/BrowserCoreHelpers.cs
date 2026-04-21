using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering
{
    internal static class BrowserCoreHelpers
    {
        internal static bool? GetJsQueryOverride(Uri uri)
        {
            try
            {
                var query = uri == null ? null : uri.Query;
                if (string.IsNullOrWhiteSpace(query)) return null;

                var trimmed = query.TrimStart('?');
                var parts = trimmed.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var part in parts)
                {
                    var eqIndex = part.IndexOf('=');
                    if (eqIndex < 0) continue;

                    var key = Uri.UnescapeDataString(part.Substring(0, eqIndex)).Trim();
                    if (!string.Equals(key, "js", StringComparison.OrdinalIgnoreCase)) continue;

                    var valStr = Uri.UnescapeDataString(part.Substring(eqIndex + 1)).Trim();

                    if (string.Equals(valStr, "1") || string.Equals(valStr, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(valStr, "on", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(valStr, "0") || string.Equals(valStr, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(valStr, "off", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[BrowserCoreHelpers] Failed parsing js query override: {ex.Message}", LogCategory.Navigation);
            }
            return null;
        }
    }
}


