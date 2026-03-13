using System;
using System.Threading.Tasks;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    public enum NavigationRequestKind
    {
        UserInput,
        Programmatic
    }

    public class NavigationManager
    {
        private readonly ResourceManager _resourceManager;

        public NavigationManager(ResourceManager resourceManager)
        {
            _resourceManager = resourceManager;
        }

        public Task<FetchResult> NavigateUserInputAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.UserInput);
        }

        public Task<FetchResult> NavigateAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.Programmatic);
        }

        public async Task<FetchResult> NavigateAsync(string url, NavigationRequestKind requestKind)
        {
            // 1. Normalize URL
            if (string.IsNullOrWhiteSpace(url)) 
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Empty URL" };

            url = NormalizeInternalFenUrl(url);

            // Handle internal schemes
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = "", FinalUri = new Uri("about:blank"), ContentType = "text/html" };
            }
            
            if (url.Equals("fen://newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = NewTabRenderer.Render(), FinalUri = new Uri("fen://newtab"), ContentType = "text/html" };
            }

            // Handle local file paths (only for trusted user input)
            if (requestKind == NavigationRequestKind.UserInput &&
                System.IO.Path.IsPathRooted(url) &&
                !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it has a colon (e.g. C:\) or starts with \\ (UNC)
                if (url.IndexOf(':') >= 0 || url.StartsWith("\\\\"))
                {
                    url = "file:///" + url.Replace("\\", "/");
                    try { FenBrowser.Core.FenLogger.Debug($"[NavigationManager] Converted to file URI: {url}", FenBrowser.Core.Logging.LogCategory.Navigation); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.Message}"); }
                }
            }
            else if (requestKind == NavigationRequestKind.Programmatic &&
                     System.IO.Path.IsPathRooted(url) &&
                     !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = "Programmatic rooted-path navigation is blocked by policy"
                };
            }

            // Default to HTTPS if no scheme
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("fen://") && !url.StartsWith("about:") && !url.StartsWith("data:"))
            {
                // Log what we are doing
                try { FenBrowser.Core.FenLogger.Debug($"[NavigationManager] Defaulting '{url}' to HTTPS", FenBrowser.Core.Logging.LogCategory.Navigation); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.Message}"); }
                url = "https://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                 return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid URL format" };
            }

            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase) &&
                !IsFileNavigationAllowed(requestKind))
            {
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = "file:// navigation blocked by policy"
                };
            }

            // Handle images
            var path = uri.AbsolutePath.ToLowerInvariant();
            try { FenBrowser.Core.FenLogger.Debug($"[NavigationManager] Checking image: path='{path}' endsWithJpg={path.EndsWith(".jpg")}", FenBrowser.Core.Logging.LogCategory.Navigation); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.Message}"); }
            if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || 
                path.EndsWith(".gif") || path.EndsWith(".bmp") || path.EndsWith(".webp") || path.EndsWith(".svg"))
            {
                var syntheticHtml = $"<!DOCTYPE html><html style=\"width: 100%; height: 100%; background-color: rgb(14, 14, 14);\"><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>{System.IO.Path.GetFileName(uri.LocalPath)}</title></head><body style=\"margin: 0; padding: 0; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background-color: rgb(14, 14, 14); overflow: hidden;\"><img style=\"display: block; position: absolute; top: 0; bottom: 0; left: 0; right: 0; margin: auto; max-width: 100%; max-height: 100%; object-fit: contain; -webkit-user-select: none;\" src=\"{uri.AbsoluteUri}\" alt=\"{System.IO.Path.GetFileName(uri.LocalPath)}\"></body></html>";
                return new FetchResult { Status = FetchStatus.Success, Content = syntheticHtml, FinalUri = uri, ContentType = "text/html" };
            }

            // 2. Fetch content as a top-level document navigation so servers
            // see navigation semantics instead of subresource-style headers.
            return await _resourceManager.FetchTextDetailedAsync(
                uri,
                referer: null,
                accept: null,
                secFetchDest: "document");
        }

        private static string NormalizeInternalFenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (url.Equals("fen://newtab/", StringComparison.OrdinalIgnoreCase))
            {
                return "fen://newtab";
            }

            if (url.Equals("fen://settings/", StringComparison.OrdinalIgnoreCase))
            {
                return "fen://settings";
            }

            return url;
        }

        private static bool IsFileNavigationAllowed(NavigationRequestKind requestKind)
        {
            var settings = BrowserSettings.Instance;
            if (!settings.AllowFileSchemeNavigation)
            {
                return false;
            }

            if (requestKind != NavigationRequestKind.Programmatic)
            {
                return true;
            }

            var webdriverEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER"),
                "1",
                StringComparison.Ordinal);
            var automationMode = string.Equals(
                Environment.GetEnvironmentVariable("FEN_AUTOMATION_MODE"),
                "1",
                StringComparison.Ordinal);

            if (!webdriverEnabled && !automationMode)
            {
                return true;
            }

            if (settings.AllowAutomationFileNavigation)
            {
                return true;
            }

            return string.Equals(
                Environment.GetEnvironmentVariable("FEN_ALLOW_AUTOMATION_FILE_NAVIGATION"),
                "1",
                StringComparison.Ordinal);
        }
    }
}



