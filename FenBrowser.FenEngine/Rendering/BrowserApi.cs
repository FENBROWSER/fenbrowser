using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// High-level facade wrapping existing engine pieces.
    /// C# 5.0 Compatible for Windows Phone 8.1 (No Newtonsoft dependency)
    /// </summary>
    public interface IBrowser
    {
        Uri CurrentUri { get; }
        Task<bool> NavigateAsync(string url);
        Task<bool> GoBackAsync();
        Task<bool> GoForwardAsync();
        bool CanGoBack { get; }
        bool CanGoForward { get; }
        event EventHandler<Uri> Navigated;
        event EventHandler<string> NavigationFailed;
        event EventHandler<bool> LoadingChanged;
        event EventHandler<string> TitleChanged;
        event EventHandler<object> RepaintReady; // UI element delivered as object to avoid framework coupling
        void SetCookie(string name, string value);
        void DeleteCookie(string name);
        IList<string> GetAllLinks();
        string GetTextContent();
        Task<string> GetTitleAsync();
        Task<object> ExecuteScriptAsync(string script);
        Task<string> FindElementAsync(string strategy, string value);
        Task ClickElementAsync(string elementId);
        Task CaptureScreenshotAsync();
    }

    public sealed class BrowserHost : IBrowser, IDisposable
    {
        private readonly CustomHtmlEngine _engine = new CustomHtmlEngine();
        private readonly ResourceManager _resources = new ResourceManager(new HttpClient());
        private Uri _current;
        private bool _disposed;
        
        private readonly List<Uri> _history = new List<Uri>();
        private int _historyIndex = -1;
        private bool _isNavigatingHistory;
        
        // Map WebDriver Element IDs to LiteElements
        private readonly Dictionary<string, LiteElement> _elementMap = new Dictionary<string, LiteElement>();

        public event EventHandler<Uri> Navigated;
        public event EventHandler<string> NavigationFailed;
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event EventHandler<object> RepaintReady;

        public Uri CurrentUri => _current;
        public bool CanGoBack => _historyIndex > 0;
        public bool CanGoForward => _historyIndex < _history.Count - 1;

        public BrowserHost()
        {
            _engine.RepaintReady += (elem) =>
            {
                try { RepaintReady?.Invoke(this, elem); }
                catch { }
            };

            _engine.LoadingChanged += (s, loading) =>
            {
                try { LoadingChanged?.Invoke(this, loading); }
                catch { }
            };

            _engine.TitleChanged += (s, title) =>
            {
                try { TitleChanged?.Invoke(this, title); }
                catch { }
            };

            ResourceManager.LogSink = (msg) =>
            {
                Console.WriteLine(msg);
            };
            Console.WriteLine($"[BrowserHost] CWD: {Environment.CurrentDirectory}");
        }

        public async Task<bool> GoBackAsync()
        {
            if (!CanGoBack) return false;
            _historyIndex--;
            _isNavigatingHistory = true;
            try { return await NavigateAsync(_history[_historyIndex].AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public async Task<bool> GoForwardAsync()
        {
            if (!CanGoForward) return false;
            _historyIndex++;
            _isNavigatingHistory = true;
            try { return await NavigateAsync(_history[_historyIndex].AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public async Task<bool> NavigateAsync(string url)
        {
            if (_disposed) return false;
            if (string.IsNullOrWhiteSpace(url)) return false;

            if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("about:"))
            {
                url = "https://" + url;
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;

            try
            {
                Console.WriteLine($"[NavigateAsync] Start: {uri}");

                var path = uri.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg") || 
                    path.EndsWith(".gif") || path.EndsWith(".bmp") || path.EndsWith(".webp") || path.EndsWith(".svg"))
                {
                    var syntheticHtml = $"<html><body style='margin:0; background-color: #222; display: flex; justify-content: center; align-items: center; height: 100vh;'><img src='{uri.AbsoluteUri}' style='max-width: 100%; max-height: 100%; box-shadow: 0 0 20px rgba(0,0,0,0.5);' /></body></html>";
                    Console.WriteLine("[NavigateAsync] Synthetic Image Page");
                    
                    var element = await _engine.RenderAsync(syntheticHtml, uri, u => _resources.FetchTextAsync(u), u => _resources.FetchImageAsync(u), u => { _ = NavigateAsync(u.AbsoluteUri); });
                    try { RepaintReady?.Invoke(this, element); } catch { }
                    _current = uri;
                    if (!_isNavigatingHistory) { if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1)); _history.Add(uri); _historyIndex = _history.Count - 1; }
                    try { Navigated?.Invoke(this, uri); } catch { }
                    return true;
                }

                var html = await _resources.FetchTextAsync(uri);
                Console.WriteLine($"[NavigateAsync] Fetched {html?.Length ?? 0} chars");
                if (string.IsNullOrWhiteSpace(html))
                {
                    RaiseNavigationFailed("Empty response");
                    return false;
                }

                Console.WriteLine("[NavigateAsync] Calling RenderAsync");
                var elem = await _engine.RenderAsync(html, uri, u => _resources.FetchTextAsync(u), u => _resources.FetchImageAsync(u), u => { _ = NavigateAsync(u.AbsoluteUri); });
                
                try { RepaintReady?.Invoke(this, elem); } catch { }

                _current = uri;

                if (!_isNavigatingHistory)
                {
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                    }
                    _history.Add(uri);
                    _historyIndex = _history.Count - 1;
                }

                try { Navigated?.Invoke(this, uri); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { System.Diagnostics.Debug.WriteLine("[NavigateAsync] Exception: " + ex.ToString()); } catch { }
                var details = ex.ToString();
                if (details != null && details.Length > 2000) details = details.Substring(0, 2000) + "...";
                RaiseNavigationFailed(details);
                return false;
            }
        }

        public void SetCookie(string name, string value)
        {
            try
            {
                var u = _current ?? new Uri("about:blank");
                _engine.SetCookie(u, name, value);
            }
            catch { }
        }

        public void DeleteCookie(string name)
        {
            try
            {
                var u = _current ?? new Uri("about:blank");
                _engine.DeleteCookie(u, name);
            }
            catch { }
        }

        public IList<string> GetAllLinks()
        {
            var list = new List<string>();
            try
            {
                var root = _engine.GetActiveDom();
                if (root != null)
                {
                    foreach (var n in root.SelfAndDescendants())
                    {
                        if (n.Tag == "a" && n.Attr != null)
                        {
                            string href;
                            if (n.Attr.TryGetValue("href", out href) && !string.IsNullOrWhiteSpace(href))
                                list.Add(href);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public string GetTextContent()
        {
            try
            {
                var root = _engine.GetActiveDom();
                if (root == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var n in root.SelfAndDescendants())
                {
                    if (n.IsText && !string.IsNullOrWhiteSpace(n.Text))
                        sb.AppendLine(n.Text.Trim());
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        public async Task<string> GetTitleAsync()
        {
            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                var titleNode = dom.Descendants().FirstOrDefault(n => n.Tag == "title");
                if (titleNode != null) return titleNode.Text ?? "";
            }
            return "";
        }

        public async Task<object> ExecuteScriptAsync(string script)
        {
            return _engine.Evaluate(script);
        }

        public async Task<string> FindElementAsync(string strategy, string value)
        {
            var dom = _engine.GetActiveDom();
            if (dom == null) throw new Exception("No active DOM");

            LiteElement found = null;
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    found = dom.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    found = dom.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    found = dom.Descendants().FirstOrDefault(n => n.Tag == value);
                }
            }
            else if (strategy == "xpath")
            {
                if (value.StartsWith("//"))
                {
                    var tag = value.Substring(2);
                    found = dom.Descendants().FirstOrDefault(n => n.Tag == tag);
                }
            }

            if (found != null)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = found;
                return id;
            }
            throw new Exception("Element not found");
        }

        public async Task ClickElementAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var element))
            {
                if (element.Tag == "a" && element.Attr != null && element.Attr.TryGetValue("href", out var href))
                {
                     await NavigateAsync(href);
                }
            }
        }

        public async Task CaptureScreenshotAsync()
        {
            // If your engine supports screenshots, implement here.
            // For now, just return a completed task (no-op).
            await Task.CompletedTask;
        }

        private void RaiseNavigationFailed(string msg) => NavigationFailed?.Invoke(this, msg);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Dispose(); } catch { }
        }
    }
}