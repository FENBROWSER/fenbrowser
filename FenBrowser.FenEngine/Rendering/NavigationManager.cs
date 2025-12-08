using System;
using System.Threading.Tasks;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    public class NavigationManager
    {
        private readonly ResourceManager _resourceManager;

        public NavigationManager(ResourceManager resourceManager)
        {
            _resourceManager = resourceManager;
        }

        public async Task<FetchResult> NavigateAsync(string url)
        {
            // 1. Normalize URL
            if (string.IsNullOrWhiteSpace(url)) 
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Empty URL" };

            // Handle internal schemes
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = "", FinalUri = new Uri("about:blank"), ContentType = "text/html" };
            }
            
            if (url.Equals("fen://newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
            {
                 return new FetchResult { Status = FetchStatus.Success, Content = NewTabRenderer.Render(), FinalUri = new Uri("fen://newtab"), ContentType = "text/html" };
            }

            // Handle local file paths
            if (System.IO.Path.IsPathRooted(url) && !url.StartsWith("http") && !url.StartsWith("file://"))
            {
                url = "file:///" + url.Replace("\\", "/");
            }

            // Default to HTTPS if no scheme
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("fen://") && !url.StartsWith("about:") && !url.StartsWith("data:"))
            {
                url = "https://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                 return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid URL format" };
            }

            // Handle images
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || 
                path.EndsWith(".gif") || path.EndsWith(".bmp") || path.EndsWith(".webp") || path.EndsWith(".svg"))
            {
                var syntheticHtml = $"<html style='height: 100%;'><head><meta name='viewport' content='width=device-width, minimum-scale=0.1'><title>{System.IO.Path.GetFileName(uri.LocalPath)}</title></head><body style='margin: 0px; height: 100vh; background-color: rgb(14, 14, 14); display: flex; justify-content: center; align-items: center;'><img class='fen-image-view' style='display: block; margin: auto; background-color: hsl(0, 0%, 90%); transition: background-color 300ms; max-width: 100%; max-height: 100%;' src='{uri.AbsoluteUri}'></body></html>";
                return new FetchResult { Status = FetchStatus.Success, Content = syntheticHtml, FinalUri = uri, ContentType = "text/html" };
            }

            // 2. Fetch content
            return await _resourceManager.FetchTextDetailedAsync(uri);
        }
    }
}


