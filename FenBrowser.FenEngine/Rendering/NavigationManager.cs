using System;
using System.Net;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Security;
using FenBrowser.FenEngine.Rendering.Performance;

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
            return NavigateAsync(url, NavigationRequestKind.UserInput, referer: null);
        }

        public Task<FetchResult> NavigateAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.Programmatic, referer: null);
        }

        public async Task<FetchResult> NavigateAsync(
            string url,
            NavigationRequestKind requestKind,
            Uri referer = null)
        {
            // 1. Normalize URL
            if (string.IsNullOrWhiteSpace(url)) 
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Empty URL" };

            url = NormalizeInternalFenUrl(url.Trim());

            // Handle internal schemes
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = "", FinalUri = new Uri("about:blank"), ContentType = "text/html" };
            }
            
            if (url.Equals("fen://newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = NewTabRenderer.Render(), FinalUri = new Uri("fen://newtab"), ContentType = "text/html" };
            }

            if (url.StartsWith("fen://performance", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(url, UriKind.Absolute, out var performanceUri) &&
                string.Equals(performanceUri.Host, "performance", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult
                {
                    Status = FetchStatus.Success,
                    Content = PerformancePageRenderer.Render(performanceUri),
                    FinalUri = performanceUri,
                    ContentType = "text/html"
                };
            }

            // Handle local file paths (only for trusted user input)
            if (requestKind == NavigationRequestKind.UserInput &&
                System.IO.Path.IsPathRooted(url) &&
                !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it has a colon (e.g. C:\) or starts with \\ (UNC)
                if (url.IndexOf(':') >= 0 || url.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    url = "file:///" + url.Replace("\\", "/");
                    try
                    {
                        // Do not emit local filesystem paths into normal navigation logs.
                        FenBrowser.Core.EngineLogCompat.Debug(
                            "[NavigationManager] Converted rooted user path to file URI.",
                            FenBrowser.Core.Logging.LogCategory.Navigation);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.GetType().Name}");
                    }
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

            // Default to HTTPS if no supported scheme prefix is present. Scheme
            // matching is ASCII case-insensitive; treating HTTP:// as schemeless
            // produced malformed URLs such as https://HTTP://example.test.
            if (!HasKnownNavigationSchemePrefix(url))
            {
                try
                {
                    FenBrowser.Core.EngineLogCompat.Debug(
                        "[NavigationManager] Defaulting schemeless navigation to HTTPS.",
                        FenBrowser.Core.Logging.LogCategory.Navigation);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.GetType().Name}");
                }
                url = "https://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                 return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid URL format" };
            }

            var fileNavigationAllowed = IsFileNavigationAllowed(requestKind);
            var automationContext = IsAutomationContext();
            var navigationDecision = BrowserSecurityPolicy.EvaluateTopLevelNavigation(
                uri,
                requestKind == NavigationRequestKind.UserInput,
                automationContext,
                BrowserSettings.Instance.AllowFileSchemeNavigation,
                fileNavigationAllowed);

            if (!navigationDecision.IsAllowed)
            {
                navigationDecision.Log(LogCategory.Security);
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = navigationDecision.Message
                };
            }

            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                navigationDecision.Log(LogCategory.Security, LogLevel.Info);
            }

            // Handle images
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".png", StringComparison.Ordinal) ||
                path.EndsWith(".jpg", StringComparison.Ordinal) ||
                path.EndsWith(".jpeg", StringComparison.Ordinal) || 
                path.EndsWith(".gif", StringComparison.Ordinal) ||
                path.EndsWith(".bmp", StringComparison.Ordinal) ||
                path.EndsWith(".webp", StringComparison.Ordinal) ||
                path.EndsWith(".svg", StringComparison.Ordinal))
            {
                // This HTML becomes a privileged synthetic document. Encode every
                // URI-derived value before placing it into markup rather than relying
                // on System.Uri escaping to also satisfy HTML attribute/text syntax.
                var fileName = System.IO.Path.GetFileName(uri.LocalPath) ?? string.Empty;
                var encodedTitle = WebUtility.HtmlEncode(fileName);
                var encodedAlt = WebUtility.HtmlEncode(fileName);
                var encodedSrc = WebUtility.HtmlEncode(uri.AbsoluteUri);
                var syntheticHtml = $"<!DOCTYPE html><html style=\"width: 100%; height: 100%; background-color: rgb(14, 14, 14);\"><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>{encodedTitle}</title></head><body style=\"margin: 0; padding: 0; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background-color: rgb(14, 14, 14); overflow: hidden;\"><img style=\"display: block; position: absolute; top: 0; bottom: 0; left: 0; right: 0; margin: auto; max-width: 100%; max-height: 100%; object-fit: contain; -webkit-user-select: none;\" src=\"{encodedSrc}\" alt=\"{encodedAlt}\"></body></html>";
                return new FetchResult { Status = FetchStatus.Success, Content = syntheticHtml, FinalUri = uri, ContentType = "text/html" };
            }

            // 2. Fetch content as a top-level document navigation so servers
            // see navigation semantics instead of subresource-style headers.
            var initiator = requestKind == NavigationRequestKind.UserInput ? null : referer;
            return await _resourceManager.FetchTextDetailedAsync(
                new FetchContext
                {
                    RequestUri = uri,
                    InitiatorUri = initiator,
                    FrameDocumentUri = initiator,
                    TopLevelDocumentUri = referer,
                    Destination = "document",
                    Mode = "navigate",
                    CredentialsMode = "include",
                    IsTopLevelNavigation = true,
                    IsUserInitiated = requestKind == NavigationRequestKind.UserInput,
                    Method = "GET"
                }).ConfigureAwait(false);
        }

        private static bool HasKnownNavigationSchemePrefix(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("fen://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
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

        private static bool IsAutomationContext()
        {
            var webdriverEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER"),
                "1",
                StringComparison.Ordinal);
            var automationMode = string.Equals(
                Environment.GetEnvironmentVariable("FEN_AUTOMATION_MODE"),
                "1",
                StringComparison.Ordinal);
            return webdriverEnabled || automationMode;
        }
    }
}
