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
            // Fix: Only treat as file if it looks like a Windows path (drive letter or UNC)
            if (System.IO.Path.IsPathRooted(url) && !url.StartsWith("http") && !url.StartsWith("file://"))
            {
                // Check if it has a colon (e.g. C:\) or starts with \\ (UNC)
                if (url.IndexOf(':') >= 0 || url.StartsWith("\\\\"))
                {
                    url = "file:///" + url.Replace("\\", "/");
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[NavigationManager] Converted to file URI: {url}\r\n"); } catch {}
                }
            }

            // Default to HTTPS if no scheme
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("fen://") && !url.StartsWith("about:") && !url.StartsWith("data:"))
            {
                // Log what we are doing
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[NavigationManager] Defaulting '{url}' to HTTPS\r\n"); } catch {}
                url = "https://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                 return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid URL format" };
            }

            // Handle images
            var path = uri.AbsolutePath.ToLowerInvariant();
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[NavigationManager] Checking image: path='{path}' endsWithJpg={path.EndsWith(".jpg")}\r\n"); } catch {}
            if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || 
                path.EndsWith(".gif") || path.EndsWith(".bmp") || path.EndsWith(".webp") || path.EndsWith(".svg"))
            {
                var syntheticHtml = $"<!DOCTYPE html><html style=\"width: 100%; height: 100%; background-color: rgb(14, 14, 14);\"><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>{System.IO.Path.GetFileName(uri.LocalPath)}</title></head><body style=\"margin: 0; padding: 0; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background-color: rgb(14, 14, 14); overflow: hidden;\"><img style=\"display: block; position: absolute; top: 0; bottom: 0; left: 0; right: 0; margin: auto; max-width: 100%; max-height: 100%; object-fit: contain; -webkit-user-select: none;\" src=\"{uri.AbsoluteUri}\" alt=\"{System.IO.Path.GetFileName(uri.LocalPath)}\"></body></html>";
                return new FetchResult { Status = FetchStatus.Success, Content = syntheticHtml, FinalUri = uri, ContentType = "text/html" };
            }

            // 2. Fetch content
            return await _resourceManager.FetchTextDetailedAsync(uri);
        }
    }
}


